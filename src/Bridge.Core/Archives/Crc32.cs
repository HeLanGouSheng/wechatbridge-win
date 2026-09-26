namespace Bridge.Core.Archives;

/// <summary>Plain CRC-32 (IEEE 802.3, the one ZIP uses). Hand-rolled so Core stays dependency-free:
/// System.IO.Compression does not verify an entry's CRC when reading, and a truncated or bit-flipped
/// transcript must be refused rather than half-parsed.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public const uint Initial = 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));
}
