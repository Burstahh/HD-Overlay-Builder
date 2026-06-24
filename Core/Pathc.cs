using System.Buffers.Binary;
using System.Text;

namespace CDTextureOverlayBuilder.Core;

public sealed class PathcHeader
{
    public uint Unknown0, Unknown1, DdsRecordSize, DdsRecordCount, HashCount, CollisionPathCount, CollisionBlobSize;
}
public sealed class PathcMapEntry
{
    public uint Selector, M1, M2, M3, M4;
}
public sealed class PathcCollisionEntry
{
    public uint PathOffset, DdsIndex, M1, M2, M3, M4;
    public string Path = "";
}
public sealed class PathcFile
{
    public PathcHeader Header = new();
    public List<byte[]> DdsRecords = new();
    public List<uint> KeyHashes = new();
    public List<PathcMapEntry> MapEntries = new();
    public List<PathcCollisionEntry> CollisionEntries = new();

    public static PathcFile Read(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < 0x1C) throw new InvalidDataException($"{path} is too small to be PATHC");
        var p = new PathcFile();
        p.Header = new PathcHeader
        {
            Unknown0 = BinaryUtil.U32(raw, 0), Unknown1 = BinaryUtil.U32(raw, 4), DdsRecordSize = BinaryUtil.U32(raw, 8),
            DdsRecordCount = BinaryUtil.U32(raw, 12), HashCount = BinaryUtil.U32(raw, 16), CollisionPathCount = BinaryUtil.U32(raw, 20), CollisionBlobSize = BinaryUtil.U32(raw, 24)
        };
        int ddsOff = 0x1C;
        int hashOff = ddsOff + checked((int)(p.Header.DdsRecordSize * p.Header.DdsRecordCount));
        int mapOff = hashOff + checked((int)(p.Header.HashCount * 4));
        int collOff = mapOff + checked((int)(p.Header.HashCount * 20));
        int blobOff = collOff + checked((int)(p.Header.CollisionPathCount * 24));
        for (int i = 0; i < p.Header.DdsRecordCount; i++)
        {
            int off = ddsOff + checked((int)(i * p.Header.DdsRecordSize));
            p.DdsRecords.Add(raw.AsSpan(off, checked((int)p.Header.DdsRecordSize)).ToArray());
        }
        for (int i = 0; i < p.Header.HashCount; i++) p.KeyHashes.Add(BinaryUtil.U32(raw, hashOff + i * 4));
        for (int i = 0; i < p.Header.HashCount; i++)
        {
            int off = mapOff + i * 20;
            p.MapEntries.Add(new PathcMapEntry { Selector = BinaryUtil.U32(raw, off), M1 = BinaryUtil.U32(raw, off + 4), M2 = BinaryUtil.U32(raw, off + 8), M3 = BinaryUtil.U32(raw, off + 12), M4 = BinaryUtil.U32(raw, off + 16) });
        }
        byte[] blob = raw.AsSpan(blobOff, Math.Min((int)p.Header.CollisionBlobSize, raw.Length - blobOff)).ToArray();
        for (int i = 0; i < p.Header.CollisionPathCount; i++)
        {
            int off = collOff + i * 24;
            var e = new PathcCollisionEntry { PathOffset = BinaryUtil.U32(raw, off), DdsIndex = BinaryUtil.U32(raw, off + 4), M1 = BinaryUtil.U32(raw, off + 8), M2 = BinaryUtil.U32(raw, off + 12), M3 = BinaryUtil.U32(raw, off + 16), M4 = BinaryUtil.U32(raw, off + 20) };
            int po = (int)e.PathOffset;
            if (po >= 0 && po < blob.Length)
            {
                int end = Array.IndexOf(blob, (byte)0, po);
                if (end < 0) end = blob.Length;
                e.Path = Encoding.UTF8.GetString(blob, po, end - po);
            }
            p.CollisionEntries.Add(e);
        }
        return p;
    }

    public byte[] Serialize()
    {
        using var collisionBlob = new MemoryStream();
        var collisionRows = new List<byte[]>();
        foreach (var e in CollisionEntries)
        {
            uint poff = (uint)collisionBlob.Position;
            var pb = Encoding.UTF8.GetBytes(e.Path);
            collisionBlob.Write(pb); collisionBlob.WriteByte(0);
            using var row = new MemoryStream();
            BinaryUtil.WriteU32(row, poff); BinaryUtil.WriteU32(row, e.DdsIndex); BinaryUtil.WriteU32(row, e.M1); BinaryUtil.WriteU32(row, e.M2); BinaryUtil.WriteU32(row, e.M3); BinaryUtil.WriteU32(row, e.M4);
            collisionRows.Add(row.ToArray());
        }
        Header.DdsRecordCount = (uint)DdsRecords.Count;
        Header.HashCount = (uint)KeyHashes.Count;
        Header.CollisionPathCount = (uint)CollisionEntries.Count;
        Header.CollisionBlobSize = (uint)collisionBlob.Length;
        using var outb = new MemoryStream();
        foreach (var v in new[] { Header.Unknown0, Header.Unknown1, Header.DdsRecordSize, Header.DdsRecordCount, Header.HashCount, Header.CollisionPathCount, Header.CollisionBlobSize }) BinaryUtil.WriteU32(outb, v);
        foreach (var r in DdsRecords) outb.Write(r);
        foreach (var h in KeyHashes) BinaryUtil.WriteU32(outb, h);
        foreach (var e in MapEntries) { BinaryUtil.WriteU32(outb, e.Selector); BinaryUtil.WriteU32(outb, e.M1); BinaryUtil.WriteU32(outb, e.M2); BinaryUtil.WriteU32(outb, e.M3); BinaryUtil.WriteU32(outb, e.M4); }
        foreach (var row in collisionRows) outb.Write(row);
        outb.Write(collisionBlob.ToArray());
        return outb.ToArray();
    }

    public static string NormalizePath(string path) => "/" + path.Replace('\\','/').Trim().Trim('/');
    public static uint PathHash(string path) => HashLittle.Compute(Encoding.UTF8.GetBytes(NormalizePath(path).ToLowerInvariant()), 0x000C5EDE);

    public void UpdateEntry(string virtualPath, int ddsIndex, uint[] m)
    {
        uint h = PathHash(virtualPath);
        int idx = KeyHashes.BinarySearch(h);
        uint selector = 0xFFFF0000u | ((uint)ddsIndex & 0xFFFFu);
        if (idx >= 0)
        {
            var me = MapEntries[idx];
            me.Selector = selector; me.M1 = m[0]; me.M2 = m[1]; me.M3 = m[2]; me.M4 = m[3];
        }
        else
        {
            idx = ~idx;
            KeyHashes.Insert(idx, h);
            MapEntries.Insert(idx, new PathcMapEntry { Selector = selector, M1 = m[0], M2 = m[1], M3 = m[2], M4 = m[3] });
        }
    }

    public bool RemoveHash(string virtualPath)
    {
        uint h = PathHash(virtualPath);
        int idx = KeyHashes.BinarySearch(h);
        if (idx < 0) return false;
        KeyHashes.RemoveAt(idx); MapEntries.RemoveAt(idx); return true;
    }
}
