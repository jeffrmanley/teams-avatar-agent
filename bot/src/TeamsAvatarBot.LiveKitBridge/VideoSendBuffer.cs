using System.Runtime.InteropServices;
using Microsoft.Skype.Bots.Media;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Concrete <see cref="VideoMediaBuffer"/> backed by unmanaged memory we
/// allocate ourselves. Used to ferry an Annex-B H.264 access unit (one
/// picture's worth of NAL units) from <see cref="FfmpegH264Encoder"/> to
/// <see cref="IVideoSocket.Send"/>.
///
/// Lifecycle matches <see cref="AudioSendBuffer"/>: SDK takes ownership on
/// <c>Send</c>, calls <see cref="Dispose"/> when done, and that's when we
/// release the unmanaged block.
/// </summary>
internal sealed class VideoSendBuffer : VideoMediaBuffer
{
    private bool _disposed;

    public VideoSendBuffer(byte[] nal, int length, VideoFormat format, long timestamp)
    {
        var buffer = Marshal.AllocHGlobal(length);
        Marshal.Copy(nal, 0, buffer, length);

        Data        = buffer;
        Length      = length;
        VideoFormat = format;
        Timestamp   = timestamp;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data);
    }
}
