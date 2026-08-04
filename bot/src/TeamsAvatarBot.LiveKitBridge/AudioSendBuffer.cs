using System.Runtime.InteropServices;
using Microsoft.Skype.Bots.Media;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Concrete <see cref="AudioMediaBuffer"/> backed by unmanaged memory we
/// allocate ourselves. Used to ferry decoded PCM frames from LiveKit into
/// <see cref="IAudioSocket.Send"/>.
///
/// Lifecycle:
///   1. We allocate via <see cref="Marshal.AllocHGlobal(int)"/> and copy the
///      caller's PCM samples in.
///   2. <see cref="IAudioSocket.Send"/> takes ownership of the buffer reference
///      and may keep it past the call return. The SDK calls <see cref="Dispose"/>
///      when it's done — that's when we free the unmanaged block.
/// </summary>
internal sealed class AudioSendBuffer : AudioMediaBuffer
{
    private bool _disposed;

    public AudioSendBuffer(short[] samples, int sampleCount, AudioFormat format, long timestamp)
    {
        var byteLength = sampleCount * sizeof(short);
        var buffer = Marshal.AllocHGlobal(byteLength);
        Marshal.Copy(samples, 0, buffer, sampleCount);

        Data        = buffer;
        Length      = byteLength;
        AudioFormat = format;
        Timestamp   = timestamp;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data);
    }
}
