using System.Buffers.Binary;
using System.Text;

namespace CDTextureOverlayBuilder.Core;

public sealed class PazEntry
{
    public string Path { get; init; } = "";
    public string PazFile { get; init; } = "";
    public uint Offset { get; init; }
    public uint CompSize { get; init; }
    public uint OrigSize { get; init; }
    public uint Flags { get; init; }
    public uint PazIndex { get; init; }
    public bool Compressed => CompSize != OrigSize;
    public int CompressionType => (int)((Flags >> 16) & 0x0F);
    public bool Encrypted => Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
}

public static class PazParser
{
    private const uint MaxSanePazCount = 4096;

    public static List<PazEntry> ParsePamt(string pamtPath, string? pazDir = null)
    {
        byte[] data = File.ReadAllBytes(pamtPath);
        if (data.Length < 32) throw new InvalidDataException($"Corrupt PAMT {Path.GetFileName(pamtPath)}: truncated");
        pazDir ??= Path.GetDirectoryName(pamtPath) ?? ".";
        string pamtStem = Path.GetFileNameWithoutExtension(pamtPath);
        int off = 0;
        off += 4;
        uint pazCount = BinaryUtil.U32(data, off); off += 4;
        if (pazCount > MaxSanePazCount) throw new InvalidDataException($"Corrupt PAMT {Path.GetFileName(pamtPath)}: paz_count {pazCount} is implausibly large");
        off += 8;
        for (int i = 0; i < pazCount; i++)
        {
            off += 4; off += 4;
            if (i < pazCount - 1) off += 4;
        }
        uint folderSize = BinaryUtil.U32(data, off); off += 4;
        int folderEnd = checked(off + (int)folderSize);
        if (folderEnd > data.Length) throw new InvalidDataException("PAMT folder section extends past end");
        string folderPrefix = "";
        while (off < folderEnd)
        {
            uint parent = BinaryUtil.U32(data, off);
            int slen = data[off + 4];
            string name = Encoding.UTF8.GetString(data, off + 5, slen);
            if (parent == 0xFFFFFFFF) folderPrefix = name;
            off += 5 + slen;
        }
        uint nodeSize = BinaryUtil.U32(data, off); off += 4;
        int nodeStart = off;
        int nodeEnd = checked(nodeStart + (int)nodeSize);
        if (nodeEnd > data.Length) throw new InvalidDataException("PAMT node section extends past end");
        var nodes = new Dictionary<uint, (uint parent, string name)>();
        while (off < nodeEnd)
        {
            uint rel = (uint)(off - nodeStart);
            uint parent = BinaryUtil.U32(data, off);
            int slen = data[off + 4];
            string name = Encoding.UTF8.GetString(data, off + 5, slen);
            nodes[rel] = (parent, name);
            off += 5 + slen;
        }
        string BuildPath(uint nodeRef)
        {
            var parts = new List<string>();
            uint cur = nodeRef;
            int guard = 0;
            while (cur != 0xFFFFFFFF && guard++ < 64)
            {
                if (!nodes.TryGetValue(cur, out var n)) break;
                parts.Add(n.name);
                cur = n.parent;
            }
            parts.Reverse();
            return string.Concat(parts);
        }
        uint folderCount = BinaryUtil.U32(data, off); off += 4;
        var folderRecords = new List<(uint pathHash, uint folderRef, uint fileIndex, uint fileCount)>();
        for (int i = 0; i < folderCount; i++)
        {
            folderRecords.Add((BinaryUtil.U32(data, off), BinaryUtil.U32(data, off + 4), BinaryUtil.U32(data, off + 8), BinaryUtil.U32(data, off + 12)));
            off += 16;
        }
        uint fileCount = BinaryUtil.U32(data, off); off += 4;
        var fileToFolder = new Dictionary<uint, int>();
        for (int fi = 0; fi < folderRecords.Count; fi++)
        {
            var fr = folderRecords[fi];
            for (uint k = fr.fileIndex; k < fr.fileIndex + fr.fileCount; k++) fileToFolder[k] = fi;
        }
        var entries = new List<PazEntry>((int)Math.Min(fileCount, 1_000_000));
        for (uint i = 0; i < fileCount; i++)
        {
            if (off + 20 > data.Length) throw new InvalidDataException("PAMT file records truncated");
            uint nodeRef = BinaryUtil.U32(data, off);
            uint pazOffset = BinaryUtil.U32(data, off + 4);
            uint compSize = BinaryUtil.U32(data, off + 8);
            uint origSize = BinaryUtil.U32(data, off + 12);
            uint flags = BinaryUtil.U32(data, off + 16);
            off += 20;
            string filename = BuildPath(nodeRef);
            string entryPath = string.IsNullOrEmpty(folderPrefix) ? filename : $"{folderPrefix}/{filename}";
            uint pazIndex = flags & 0xFF;
            int stemNum = int.TryParse(pamtStem, out var sn) ? sn : 0;
            string pazFile = Path.Combine(pazDir, $"{stemNum + pazIndex}.paz");
            entries.Add(new PazEntry { Path = entryPath.Replace('\\','/'), PazFile = pazFile, Offset = pazOffset, CompSize = compSize, OrigSize = origSize, Flags = flags, PazIndex = pazIndex });
        }
        return entries;
    }
}
