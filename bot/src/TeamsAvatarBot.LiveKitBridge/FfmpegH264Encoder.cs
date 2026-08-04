using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// One ffmpeg subprocess per call. Eats raw I420 frames at <see cref="Width"/>×
/// <see cref="Height"/>×<see cref="FrameRate"/>, emits Annex-B H.264 access
/// units suitable for <see cref="IVideoSocket.Send"/>.
///
/// Per ADR-009 (option 1): we picked subprocess over in-process
/// FFmpeg.AutoGen because libx264 is GPL and in-process would force the bot
/// into GPL too. Subprocess keeps libx264 in its own address space and at
/// arm's length.
///
/// Pipeline:
///   <c>I420 frame → stdin → libx264 → stdout (Annex-B H.264) → access-unit
///   parser → channel of complete AUs</c>
/// stderr is drained line-by-line into the logger so encoder warnings
/// surface without blocking the pipe.
///
/// Tuning:
///   - <c>-preset ultrafast -tune zerolatency</c>: minimum encoder latency.
///   - <c>-profile baseline -level 3.1</c>: maximum Teams interop.
///   - <c>-bf 0</c>: no B-frames (no reorder delay).
///   - <c>-g 30 -keyint_min 30</c>: IDR every 30 frames = 1 s. Teams asks for
///     key frames via <see cref="IVideoSocket.RequestKeyFrame"/>; we don't
///     respond on demand (yet) — relying on the 1-second cadence is good
///     enough for v1.
///   - <c>-x264-params aud=1</c>: emit Access Unit Delimiters so the parser
///     can find picture boundaries.
/// </summary>
internal sealed class FfmpegH264Encoder : IAsyncDisposable
{
    public int InputWidth   { get; }
    public int InputHeight  { get; }
    public int OutputWidth  { get; }
    public int OutputHeight { get; }
    public int FrameRate    { get; }

    private readonly ILogger<FfmpegH264Encoder>      _log;
    private readonly Process                         _proc;
    private readonly H264AccessUnitParser            _parser   = new();
    private readonly Channel<byte[]>                 _outChan;
    private readonly CancellationTokenSource         _cts      = new();
    private readonly Task                            _stdoutPump;
    private readonly Task                            _stderrPump;
    private bool                                     _disposed;

    public FfmpegH264Encoder(
        int inWidth, int inHeight,
        int outWidth, int outHeight,
        int frameRate, int bitrateKbps,
        ILogger<FfmpegH264Encoder> log)
    {
        InputWidth   = inWidth;
        InputHeight  = inHeight;
        OutputWidth  = outWidth;
        OutputHeight = outHeight;
        FrameRate    = frameRate;
        _log         = log;
        _outChan     = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        var needsScale = inWidth != outWidth || inHeight != outHeight;
        var argList = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-f", "rawvideo",
            "-pix_fmt", "yuv420p",
            "-s", $"{inWidth}x{inHeight}",
            "-r", frameRate.ToString(),
            "-i", "pipe:0",
        };
        if (needsScale)
        {
            argList.Add("-vf");
            argList.Add($"scale={outWidth}:{outHeight}:flags=fast_bilinear,setsar=1");
        }
        argList.AddRange(new[]
        {
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-profile:v", "baseline",
            "-level:v", "3.1",
            "-pix_fmt", "yuv420p",
            "-bf", "0",
            "-g", frameRate.ToString(),
            "-keyint_min", frameRate.ToString(),
            "-b:v", $"{bitrateKbps}k",
            "-maxrate", $"{bitrateKbps}k",
            "-bufsize", $"{bitrateKbps}k",
            "-x264-params", "aud=1:nal-hrd=cbr:force-cfr=1",
            "-f", "h264",
            "pipe:1",
        });
        var args = string.Join(' ', argList);

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                Arguments              = args,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            },
            EnableRaisingEvents = true,
        };
        _proc.Exited += (_, _) =>
            _log.LogWarning("ffmpeg exited code={Code} (encoder is now dead — call should drop)", _proc.ExitCode);

        _proc.Start();
        _log.LogInformation(
            "ffmpeg started pid={Pid} in={InW}x{InH} out={OutW}x{OutH}@{Fps} bitrate={Br}kbps",
            _proc.Id, inWidth, inHeight, outWidth, outHeight, frameRate, bitrateKbps);

        _stdoutPump = Task.Run(StdoutLoopAsync);
        _stderrPump = Task.Run(StderrLoopAsync);
    }

    /// <summary>Channel of complete Annex-B H.264 access units.</summary>
    public ChannelReader<byte[]> EncodedReader => _outChan.Reader;

    /// <summary>
    /// Push one I420 frame. Bytes must be exactly <c>Width*Height*3/2</c>
    /// long (Y plane + Cb/Cr planes at half resolution each). Blocks if
    /// ffmpeg stdin is backpressured — that's correct propagation back to
    /// the LiveKit subscribe loop.
    /// </summary>
    public async Task WriteFrameAsync(ReadOnlyMemory<byte> i420, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FfmpegH264Encoder));
        var expected = InputWidth * InputHeight * 3 / 2;
        if (i420.Length != expected)
            throw new ArgumentException(
                $"I420 frame length {i420.Length} != expected {expected} for {InputWidth}x{InputHeight}",
                nameof(i420));

        try
        {
            await _proc.StandardInput.BaseStream.WriteAsync(i420, ct);
        }
        catch (IOException ex) when (_proc.HasExited)
        {
            _log.LogWarning(ex, "stdin write failed because ffmpeg exited code={Code}", _proc.ExitCode);
            throw;
        }
    }

    private async Task StdoutLoopAsync()
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await _proc.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(), _cts.Token);
                if (n == 0) break; // EOF — ffmpeg closed stdout
                foreach (var au in _parser.Feed(buf.AsSpan(0, n)))
                    await _outChan.Writer.WriteAsync(au, _cts.Token);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "stdout pump terminated");
        }
        finally
        {
            _outChan.Writer.TryComplete();
        }
    }

    private async Task StderrLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _proc.StandardError.ReadLineAsync(_cts.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _log.LogDebug("ffmpeg: {Line}", line);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "stderr pump terminated");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts.Cancel();
            try { _proc.StandardInput.Close(); } catch { /* already closed */ }
            if (!_proc.HasExited)
            {
                try
                {
                    if (!_proc.WaitForExit(2_000)) _proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { _log.LogDebug(ex, "ffmpeg kill failed"); }
            }
            await Task.WhenAny(Task.WhenAll(_stdoutPump, _stderrPump), Task.Delay(2_000));
        }
        finally
        {
            _proc.Dispose();
            _cts.Dispose();
        }
    }
}
