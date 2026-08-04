namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Splits a stream of Annex-B H.264 bytes into <em>access units</em> — the
/// group of NAL units that compose one coded picture. <see cref="IVideoSocket.Send"/>
/// expects one buffer per picture, so we have to find the boundaries before
/// we can call it.
///
/// Strategy: rely on the AUD (Access Unit Delimiter) NAL — type 9. We tell
/// libx264 to emit AUDs with <c>x264-params "aud=1"</c>, then every time we
/// see a new AUD we emit everything we've accumulated so far as one unit
/// (excluding the just-found AUD; that becomes the start of the next unit).
///
/// Annex-B start codes: 3-byte <c>00 00 01</c> or 4-byte <c>00 00 00 01</c>.
/// NAL unit type lives in the low 5 bits of the byte immediately after the
/// start code (<c>nal_unit_header &amp; 0x1F</c>).
///
/// Not thread-safe — one parser per stream / per call.
/// </summary>
public sealed class H264AccessUnitParser
{
    private const byte NalTypeAud = 9;

    private readonly List<byte> _accumulated = new(64 * 1024);

    /// <summary>
    /// Feed bytes from ffmpeg stdout. Returns zero or more complete access
    /// units that can be passed straight to <see cref="IVideoSocket.Send"/>.
    /// The parser keeps any leftover bytes (current in-progress AU) for the
    /// next call.
    /// </summary>
    public IEnumerable<byte[]> Feed(ReadOnlySpan<byte> bytes)
    {
        var results = new List<byte[]>();
        var startOfAccumulated = _accumulated.Count;

        // Append everything first. We'll then re-scan from a few bytes before
        // the new region in case a start code straddles the previous chunk's
        // tail (a 4-byte start code can have its last 1–3 bytes in the prior
        // chunk).
        var rescanFrom = Math.Max(0, startOfAccumulated - 3);
        foreach (var b in bytes) _accumulated.Add(b);

        // Walk the buffer looking for AUD NAL units. Anything before an AUD
        // (other than the very first AUD we ever see) is one complete AU.
        var i = rescanFrom;
        while (i < _accumulated.Count - 4)
        {
            if (!IsStartCode(i, out var headerOffset)) { i++; continue; }

            var nalType = (byte)(_accumulated[headerOffset] & 0x1F);
            if (nalType == NalTypeAud && i > 0)
            {
                // Emit [0..i) as a complete access unit, drop it from the
                // accumulator, restart scan from the AUD that's now at offset 0.
                var unit = new byte[i];
                _accumulated.CopyTo(0, unit, 0, i);
                results.Add(unit);
                _accumulated.RemoveRange(0, i);
                i = 0;
                continue;
            }

            // Skip past this start code — start codes don't overlap.
            i = headerOffset + 1;
        }

        return results;
    }

    private bool IsStartCode(int i, out int headerOffset)
    {
        // 4-byte: 00 00 00 01
        if (i + 4 <= _accumulated.Count
            && _accumulated[i]     == 0
            && _accumulated[i + 1] == 0
            && _accumulated[i + 2] == 0
            && _accumulated[i + 3] == 1)
        {
            headerOffset = i + 4;
            return true;
        }
        // 3-byte: 00 00 01
        if (i + 3 <= _accumulated.Count
            && _accumulated[i]     == 0
            && _accumulated[i + 1] == 0
            && _accumulated[i + 2] == 1)
        {
            headerOffset = i + 3;
            return true;
        }
        headerOffset = -1;
        return false;
    }
}
