using TeamsAvatarBot.LiveKitBridge;

namespace TeamsAvatarBot.Tests;

/// <summary>
/// Pin the NAL Annex-B → access-unit parser. Each test feeds a hand-built
/// byte stream and asserts which complete access units come out and what's
/// kept in the parser's internal buffer for the next chunk.
///
/// AUD NAL header byte: nal_unit_type=9, nal_ref_idc=0 → 0x09. Followed by
/// the Annex-B start code (3- or 4-byte).
/// </summary>
public sealed class H264AccessUnitParserTests
{
    // 4-byte start code + AUD NAL header (type 9, primary_pic_type 0xF0) +
    // typically a slice NAL after that. We don't care about the slice
    // payload bytes — the parser only looks at NAL types.
    private static byte[] StartCode4 => [0x00, 0x00, 0x00, 0x01];
    private static byte[] StartCode3 => [0x00, 0x00, 0x01];
    private static byte AudHeader     => 0x09;
    private static byte SliceHeader   => 0x65; // IDR slice, ref_idc=3
    private static byte SpsHeader     => 0x67; // SPS, ref_idc=3

    [Fact]
    public void Single_au_with_no_trailing_aud_is_kept_in_buffer()
    {
        var parser = new H264AccessUnitParser();
        var bytes  = Concat(StartCode4, [AudHeader], StartCode4, [SliceHeader, 0xAA, 0xBB]);
        var units  = parser.Feed(bytes).ToArray();

        // First AU is incomplete until we see the NEXT AUD — nothing emitted yet.
        Assert.Empty(units);
    }

    [Fact]
    public void Two_consecutive_aus_emit_the_first_complete_one()
    {
        var parser = new H264AccessUnitParser();
        var au1    = Concat(StartCode4, [AudHeader], StartCode4, [SliceHeader, 0x11, 0x22]);
        var au2    = Concat(StartCode4, [AudHeader], StartCode4, [SliceHeader, 0x33, 0x44]);

        var units = parser.Feed(Concat(au1, au2)).ToArray();
        Assert.Single(units);
        Assert.Equal(au1, units[0]);
    }

    [Fact]
    public void Three_byte_start_code_is_recognized()
    {
        var parser = new H264AccessUnitParser();
        var au1    = Concat(StartCode3, [AudHeader], StartCode3, [SliceHeader, 0x01]);
        var au2    = Concat(StartCode3, [AudHeader], StartCode3, [SliceHeader, 0x02]);

        var units = parser.Feed(Concat(au1, au2)).ToArray();
        Assert.Single(units);
        Assert.Equal(au1, units[0]);
    }

    [Fact]
    public void Sps_pps_idr_sequence_emits_as_one_access_unit()
    {
        // Realistic key-frame access unit: AUD + SPS + PPS + IDR slice.
        // PPS NAL type = 8, header byte 0x68 (ref_idc=3).
        const byte ppsHeader = 0x68;
        var parser = new H264AccessUnitParser();
        var au1 = Concat(
            StartCode4, [AudHeader],
            StartCode4, [SpsHeader, 0x42, 0x80, 0x1E],
            StartCode4, [ppsHeader, 0xCE, 0x06, 0xE2],
            StartCode4, [SliceHeader, 0xDE, 0xAD]);
        var au2 = Concat(StartCode4, [AudHeader], StartCode4, [SliceHeader, 0xBE, 0xEF]);

        var units = parser.Feed(Concat(au1, au2)).ToArray();
        Assert.Single(units);
        Assert.Equal(au1, units[0]);
    }

    [Fact]
    public void Start_code_split_across_chunks_is_not_lost()
    {
        var parser = new H264AccessUnitParser();
        var au1Pre = Concat(StartCode4, [AudHeader], StartCode4, [SliceHeader, 0xAA]);
        // Split the second AU's start code 00 00 | 00 01 across two feeds.
        var split1 = Concat(au1Pre, [0x00, 0x00]);
        var split2 = Concat([0x00, 0x01], [AudHeader], StartCode4, [SliceHeader, 0xBB]);

        var first  = parser.Feed(split1).ToArray();
        Assert.Empty(first);   // no complete AU yet — second AUD still hidden in next chunk

        var second = parser.Feed(split2).ToArray();
        Assert.Single(second); // now the boundary is visible — AU1 emits
        Assert.Equal(au1Pre, second[0]);
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        var off = 0;
        foreach (var a in arrays) { Buffer.BlockCopy(a, 0, result, off, a.Length); off += a.Length; }
        return result;
    }
}
