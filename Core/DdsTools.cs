using System.Buffers.Binary;

namespace CDTextureOverlayBuilder.Core;

public static class DdsTools
{
    private static readonly Dictionary<string, int> BlockByFourCc = new(StringComparer.Ordinal)
    {
        ["DXT1"] = 8, ["ATI1"] = 8, ["BC4U"] = 8, ["BC4S"] = 8,
        ["DXT3"] = 16, ["DXT5"] = 16, ["ATI2"] = 16, ["BC5U"] = 16, ["BC5S"] = 16,
    };
    private static readonly Dictionary<uint, int> BlockByDxgi = new()
    {
        [70]=8,[71]=8,[72]=8,[73]=16,[74]=16,[75]=16,[76]=16,[77]=16,[78]=16,
        [79]=8,[80]=8,[81]=8,[82]=16,[83]=16,[84]=16,[94]=16,[95]=16,[96]=16,[97]=16,[98]=16,[99]=16,
    };
    private static readonly Dictionary<string, uint> Last4ByFourCc = new(StringComparer.Ordinal)
    {
        ["DXT1"] = 12, ["DXT2"] = 15, ["DXT3"] = 15, ["DXT4"] = 15, ["DXT5"] = 15,
        ["ATI1"] = 4, ["BC4U"] = 4, ["BC4S"] = 4, ["ATI2"] = 4, ["BC5U"] = 4, ["BC5S"] = 4,
    };
    private static readonly Dictionary<uint, uint> Last4ByDxgi = new()
    {
        [70]=12,[71]=12,[72]=12,[73]=15,[74]=15,[75]=15,[76]=15,[77]=15,[78]=15,
        [79]=4,[80]=4,[81]=4,[82]=4,[83]=4,[84]=4,[94]=4,[95]=4,[96]=4,[97]=15,[98]=15,[99]=15,
    };

    public static (byte[] payload, uint[] m) BuildPartialPayload(byte[] dds)
    {
        if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ') return (dds, new uint[] {0,0,0,0});
        uint height = BinaryUtil.U32(dds, 12);
        uint width = BinaryUtil.U32(dds, 16);
        uint depth = BinaryUtil.U32(dds, 24);
        uint mipCount = BinaryUtil.U32(dds, 28); if (mipCount == 0) mipCount = 1;
        string fourcc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        uint field112 = BinaryUtil.U32(dds, 112);
        bool isDx10 = fourcc == "DX10" && dds.Length >= 148;
        int headerSize = isDx10 ? 148 : 128;
        uint dxgi = isDx10 ? BinaryUtil.U32(dds, 128) : 0;
        uint arraySize = isDx10 ? BinaryUtil.U32(dds, 140) : 1;
        int blockBytes = BlockByFourCc.TryGetValue(fourcc, out var bb) ? bb : (BlockByDxgi.TryGetValue(dxgi, out bb) ? bb : 0);
        if (blockBytes == 0) return (dds, new uint[] {0,0,0,0});

        int slots = (int)Math.Max(4, mipCount);
        var mipSizes = new uint[slots];
        uint w = Math.Max(1, width), h = Math.Max(1, height);
        for (int i = 0; i < Math.Min(slots, (int)mipCount); i++)
        {
            mipSizes[i] = (uint)(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (uint)blockBytes);
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        bool notDx10OrArraySmall = !isDx10 || arraySize < 2;
        bool multiChunkRawable = mipCount > 5 && field112 == 0 && depth < 2;
        bool flag3 = (!notDx10OrArraySmall) || (!multiChunkRawable);
        byte[] header = new byte[headerSize];
        Buffer.BlockCopy(dds, 0, header, 0, headerSize);
        if (depth == 0) BinaryUtil.W32(header, 24, 1);
        using var ms = new MemoryStream();
        ms.Write(header);
        uint[] m = {0,0,0,0};
        if (flag3)
        {
            int firstSize = checked((int)mipSizes[0]);
            int firstEnd = Math.Min(dds.Length, headerSize + firstSize);
            var first = dds.AsSpan(headerSize, Math.Max(0, firstEnd - headerSize)).ToArray();
            var comp = Lz4Block.Compress(first);
            var chosen = comp.Length < first.Length ? comp : first;
            m[0] = (uint)chosen.Length;
            m[1] = mipSizes[0];
            if (mipCount > 1) m[2] = mipSizes[1];
            if (mipCount > 2) m[3] = mipSizes[2];
            ms.Write(chosen);
            if (firstEnd < dds.Length) ms.Write(dds, firstEnd, dds.Length - firstEnd);
        }
        else
        {
            int cursor = headerSize;
            for (int j = 0; j < Math.Min(4, (int)mipCount); j++)
            {
                int size = checked((int)mipSizes[j]);
                m[j] = (uint)size;
                int end = Math.Min(dds.Length, cursor + size);
                if (end > cursor) ms.Write(dds, cursor, end - cursor);
                cursor = end;
            }
            if (cursor < dds.Length) ms.Write(dds, cursor, dds.Length - cursor);
        }
        byte[] outb = ms.ToArray();
        if (outb.Length >= 76)
        {
            for (int i = 0; i < 4; i++) BinaryUtil.W32(outb, 32 + i * 4, m[i]);
            for (int i = 0; i < 7; i++) BinaryUtil.W32(outb, 48 + i * 4, 0);
        }
        return (outb, m);
    }

    public static uint GetFormatLast4(byte[] dds)
    {
        if (dds.Length < 92) return 0;
        string fourcc = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        if (fourcc == "DX10" && dds.Length >= 132)
        {
            uint dxgi = BinaryUtil.U32(dds, 128);
            return Last4ByDxgi.TryGetValue(dxgi, out var v) ? v : 0;
        }
        return Last4ByFourCc.TryGetValue(fourcc, out var f) ? f : 0;
    }
}
