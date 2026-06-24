using System.Buffers.Binary;
using System.Text;

namespace CDTextureOverlayBuilder.Core;

public sealed class PapgtManager
{
    private const int EntrySize = 12;
    private const uint DefaultLangType = 0x3FFF;
    private readonly string _gameDir;
    private readonly string _papgtPath;

    public PapgtManager(string gameDir)
    {
        _gameDir = gameDir;
        _papgtPath = Path.Combine(gameDir, "meta", "0.papgt");
    }

    public static uint EncodeFlags(int isOptional = 0, uint langType = DefaultLangType, int zero = 0) => (uint)((isOptional & 0xFF) | (((int)langType & 0xFFFF) << 8) | ((zero & 0xFF) << 24));

    public byte[] Rebuild(Dictionary<string, byte[]>? modifiedPamts = null, byte[]? modPapgt = null)
    {
        byte[] papgt = modPapgt is { Length: >= 12 } ? modPapgt : File.Exists(_papgtPath) ? File.ReadAllBytes(_papgtPath) : throw new FileNotFoundException("PAPGT not found", _papgtPath);
        if (papgt.Length < 12) throw new InvalidDataException("PAPGT file too small");
        byte[] meta0 = papgt.AsSpan(0, 4).ToArray();
        byte[] meta8 = papgt.AsSpan(8, 4).ToArray();
        int entryStart = 12;
        int count = FindEntryCount(papgt, entryStart);
        int stringStart = entryStart + count * EntrySize + 4;
        var parsed = new List<(string dir, uint flags, uint hash)>();
        for (int i = 0; i < count; i++)
        {
            int pos = entryStart + i * EntrySize;
            uint flags = BinaryUtil.U32(papgt, pos);
            uint nameOff = BinaryUtil.U32(papgt, pos + 4);
            uint hash = BinaryUtil.U32(papgt, pos + 8);
            string? name = ReadString(papgt, stringStart, (int)nameOff);
            if (!string.IsNullOrEmpty(name)) parsed.Add((name!, flags, hash));
        }
        modifiedPamts ??= new Dictionary<string, byte[]>();
        var live = new List<(string dir, uint flags, uint hash)>();
        foreach (var e in parsed)
        {
            bool onDisk = File.Exists(Path.Combine(_gameDir, e.dir, "0.pamt"));
            bool inMod = modifiedPamts.ContainsKey(e.dir);
            bool vanilla = true;
            if (int.TryParse(e.dir, out int n)) vanilla = n < 36;
            if (onDisk || inMod || vanilla) live.Add(e);
        }
        var existing = new HashSet<string>(live.Select(x => x.dir));
        var newDirs = new List<string>();
        foreach (var d in modifiedPamts.Keys.OrderBy(x => x)) if (existing.Add(d)) newDirs.Add(d);
        foreach (var d in Directory.EnumerateDirectories(_gameDir).Select(Path.GetFileName).Where(x => x != null).Cast<string>().OrderBy(x => x))
        {
            if (!int.TryParse(d, out int n) || d.Length != 4 || n < 36) continue;
            if (existing.Contains(d)) continue;
            if (File.Exists(Path.Combine(_gameDir, d, "0.pamt"))) { existing.Add(d); newDirs.Add(d); }
        }
        var all = new List<(string dir, uint flags)>();
        foreach (var d in newDirs) all.Add((d, EncodeFlags()));
        foreach (var e in live) all.Add((e.dir, e.flags));
        using var strTab = new MemoryStream();
        var offsets = new Dictionary<string, uint>();
        foreach (var (dir, _) in all)
        {
            if (offsets.ContainsKey(dir)) continue;
            offsets[dir] = (uint)strTab.Position;
            var b = Encoding.ASCII.GetBytes(dir); strTab.Write(b); strTab.WriteByte(0);
        }
        using var result = new MemoryStream();
        result.Write(meta0); BinaryUtil.WriteU32(result, 0); result.Write(meta8);
        var existingHashes = parsed.ToDictionary(x => x.dir, x => x.hash);
        foreach (var (dir, flags) in all)
        {
            uint pamtHash = 0;
            if (modifiedPamts.TryGetValue(dir, out var pamtData)) pamtHash = HashLittle.ComputePamtHash(pamtData);
            else
            {
                string p = Path.Combine(_gameDir, dir, "0.pamt");
                if (File.Exists(p)) pamtHash = HashLittle.ComputePamtHash(File.ReadAllBytes(p));
                else if (existingHashes.TryGetValue(dir, out var old)) pamtHash = old;
            }
            BinaryUtil.WriteU32(result, flags); BinaryUtil.WriteU32(result, offsets[dir]); BinaryUtil.WriteU32(result, pamtHash);
        }
        BinaryUtil.WriteU32(result, (uint)strTab.Length); result.Write(strTab.ToArray());
        byte[] outb = result.ToArray();
        outb[8] = (byte)(all.Count & 0xFF);
        BinaryUtil.W32(outb, 4, HashLittle.ComputePapgtHash(outb));
        return outb;
    }

    private static int FindEntryCount(byte[] papgt, int entryStart)
    {
        for (int n = 1; n < 10000; n++)
        {
            int pos = entryStart + n * EntrySize;
            if (pos + 4 > papgt.Length) break;
            uint size = BinaryUtil.U32(papgt, pos);
            if (pos + 4 + size == papgt.Length) return n;
        }
        return Math.Max(0, (papgt.Length - entryStart - 4) / EntrySize);
    }

    private static string? ReadString(byte[] data, int stringStart, int offset)
    {
        int abs = stringStart + offset;
        if (abs < 0 || abs >= data.Length) return null;
        int end = Array.IndexOf(data, (byte)0, abs);
        if (end < 0) end = data.Length;
        return Encoding.ASCII.GetString(data, abs, end - abs);
    }
}
