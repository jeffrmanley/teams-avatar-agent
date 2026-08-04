using Microsoft.Extensions.Logging.Abstractions;
using TeamsAvatarBot.LiveKitBridge;

namespace TeamsAvatarBot.Tests;

/// <summary>
/// End-to-end smoke check that the encoder pipeline (ffmpeg subprocess +
/// args + NAL parser + channel) actually produces H.264 access units when
/// you feed it I420 frames. Catches the silent-break failure mode where
/// libx264 isn't compiled in, an arg name shifts between ffmpeg versions,
/// or the AUD x264-param doesn't make it into the stream.
///
/// Spawns a real ffmpeg subprocess; requires <c>ffmpeg</c> on PATH (the
/// preflight checks for it). Uses a tiny 320×180@10fps stream to keep the
/// test under a couple seconds.
/// </summary>
public sealed class FfmpegH264EncoderSmokeTests
{
    [Fact]
    public async Task Encoder_emits_at_least_one_access_unit()
    {
        const int w = 320, h = 180, fps = 10, frames = 20;

        await using var encoder = new FfmpegH264Encoder(
            inWidth: w, inHeight: h,
            outWidth: w, outHeight: h,
            frameRate: fps, bitrateKbps: 200,
            log: NullLogger<FfmpegH264Encoder>.Instance);

        // Solid mid-grey I420 frame (Y=128, U=128, V=128).
        var frame = new byte[w * h * 3 / 2];
        Array.Fill(frame, (byte)128);

        var feed = Task.Run(async () =>
        {
            for (var i = 0; i < frames; i++)
                await encoder.WriteFrameAsync(frame, CancellationToken.None);
        });

        var collected = new List<byte[]>();
        var collect   = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await foreach (var au in encoder.EncodedReader.ReadAllAsync(cts.Token))
            {
                collected.Add(au);
                if (collected.Count >= 1) break; // one is enough to prove the pipeline
            }
        });

        await Task.WhenAll(feed, collect);

        Assert.NotEmpty(collected);
        // First AU should start with an Annex-B start code.
        var first = collected[0];
        Assert.True(first.Length >= 4);
        Assert.True(
            (first[0] == 0 && first[1] == 0 && first[2] == 0 && first[3] == 1)
            || (first[0] == 0 && first[1] == 0 && first[2] == 1),
            $"first AU does not start with a start code: {BitConverter.ToString(first, 0, Math.Min(8, first.Length))}");
    }
}
