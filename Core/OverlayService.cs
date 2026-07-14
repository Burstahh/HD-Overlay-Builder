using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CDTextureOverlayBuilder.Core;

public sealed class PamtIndex
{
    public Dictionary<string, List<IndexCandidate>> ByFull { get; } = new();
    public Dictionary<string, List<IndexCandidate>> ByFlat { get; } = new();
    public Dictionary<string, List<IndexCandidate>> ByName { get; } = new();
    public Dictionary<string, List<IndexCandidate>> BySuffix { get; } = new();
    public Dictionary<string, List<string>> ExistingModTargets { get; } = new();
    public Dictionary<string, List<string>> ExistingModNames { get; } = new();
    public List<IndexCandidate> Candidates { get; } = new();

    public void Add(IndexCandidate c, bool isModDir = false)
    {
        Candidates.Add(c);
        string flat = OverlayBuilder.Norm(c.EntryPath);
        string full = OverlayBuilder.Norm(c.FullPath);
        string name = c.Filename.ToLowerInvariant();
        AddTo(ByFlat, flat, c); AddTo(ByFull, full, c); AddTo(ByName, name, c);
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++) AddTo(BySuffix, string.Join('/', parts.Skip(i)), c);
        if (isModDir)
        {
            AddStr(ExistingModTargets, flat, c.PamtDir);
            AddStr(ExistingModNames, name, c.PamtDir);
        }
    }
    private static void AddTo(Dictionary<string, List<IndexCandidate>> d, string k, IndexCandidate c) { if (!d.TryGetValue(k, out var l)) d[k] = l = new(); l.Add(c); }
    private static void AddStr(Dictionary<string, List<string>> d, string k, string v) { if (!d.TryGetValue(k, out var l)) d[k] = l = new(); l.Add(v); }
}

public sealed record FilterPresetDef(string Id, string Es, string En, string Pamt, string Prefix)
{
    public string Label(string lang) => CDTextureOverlayBuilder.L.PresetLabel(Id, Es, En, lang);
}

public sealed class OverlayService
{
    public sealed record SourceRootContainerGuardResult(bool Warn, string SelectedRoot, List<string> DdsBearingChildFolders, bool ArchiveRootException)
    {
        public string ChildFolderSummary => string.Join(", ", DdsBearingChildFolders.Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private sealed record ExistingOverlayTarget(string FullPath, string EntryPath, string OverlayDir, string ManifestPath, uint PazIndex, uint PazOffset, uint CompSize);

    private sealed class LooseMatchDiagnostic
    {
        public string SourceRel { get; set; } = "";
        public string SourceBasename { get; set; } = "";
        public string MatchMethod { get; set; } = "";
        public string SelectedPrimaryTarget { get; set; } = "";
        public string FinalDecision { get; set; } = "";
        public string SourceDdsInfo { get; set; } = "";
        public List<LooseMatchCandidateDiagnostic> Candidates { get; set; } = new();
    }

    private sealed class LooseMatchCandidateDiagnostic
    {
        public string PamtDir { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string EntryPath { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool ExactBasename { get; set; }
        public bool ExactSuffixType { get; set; }
        public string SourceSuffixType { get; set; } = "";
        public string CandidateSuffixType { get; set; } = "";
        public string DimensionFormatCompatibility { get; set; } = "";
        public bool Hotfix2LegacyWouldApply { get; set; }
        public bool Hotfix3StrictWouldApply { get; set; }
        public string FinalDecision { get; set; } = "";
    }

    private sealed record DdsHeaderDiagnostic(string Text);

    // Source packs may be organized as 0000\..., 0001\..., etc.
    // Earlier builds only recognized the friendly texture preset archive
    // folders here.  Major game updates can add, remove, or renumber
    // archive folders, so exact archive-path matching should accept any
    // current stock numeric archive prefix instead of a small hard-coded
    // preset list.  The actual scan still uses folders that exist on disk.

    public const string AppName = "HD Overlay Builder";
    public const string AppVersion = "1.4.2";
    private const string ManagedOverlayPrefix = "HD";
    private const string ManagedOverlayMarkerFile = "HD_UPSCALE_OVERLAY_BUILDER_MANAGED.txt";
    private const string LegacyManagedOverlayMarkerFile = "HD_OVERLAY_BUILDER_MANAGED.txt";
    private const string IncompleteOverlayMarkerFile = "HD_UPSCALE_OVERLAY_BUILDER_INCOMPLETE.txt";
    public const string DefaultModName = "HDUpscaleOverlay";
    private const string LegacyDefaultModName = "KhainOneHDTexture";
    private const string ToolDataFolderName = "HDOverlayBuilder";
    private const string PreviousToolDataFolderName = "HDUpscaleOverlayBuilder";
    private const string LegacyToolDataFolderName = "CDTextureOverlayBuilder";
    private const string RegistryFileName = "hd_upscale_overlay_registry.json";
    private const string ActiveTargetManifestFileName = "active_target_manifest_v1.json";
    private const string LegacyRegistryFileName = "texture_overlay_registry.json";
    private const string HoldRootFolderName = "HDOverlayBuilder_HOLD";
    private const string PreviousHoldRootFolderName = "HDUpscaleOverlayBuilder_HOLD";
    private const string EasyApplyRollbackFolderName = "HDOverlayBuilder_EasyApplyRollback";
    private const string PreviousEasyApplyRollbackFolderName = "HDUpscaleOverlayBuilder_EasyApplyRollback";
    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase) { ".dds" };

    public static readonly List<FilterPresetDef> Presets = new()
    {
        // Path filtered texture families. These are the friendly/default options
        // users should normally choose when their DDS folder is organized by
        // archive family or when they are applying one stock archive family at
        // a time from a loose DDS folder.
        new("all", "Todo / sin filtro", "All / no filter", "", ""),
        new("objects", "0000 - Objetos / Subcapas", "0000 - Objects / Sublayers", "0000", "object/texture"),
        new("object_sublayer", "0000 - Solo Subcapas", "0000 - Sublayers Only", "0000", "object/texture/sublayer"),
        new("trees", "0001 - Vegetación", "0001 - Vegetation", "0001", "tree"),
        new("shared", "0002 - Compartidas", "0002 - Shared", "0002", "texture"),
        new("effects", "0007 - Efectos", "0007 - Effects", "0007", "effect"),
        new("characters", "0009 - Personajes", "0009 - Characters", "0009", "character/texture"),
        new("ui", "0012 - Interfaz", "0012 - UI", "0012", "ui"),
        new("leveldata", "0015 - Datos de Nivel", "0015 - Level Data", "0015", "leveldata"),

        // Raw archive filters. These use only the PAMT number and do not restrict
        // by path. They are useful as advanced/catch all options for a specific
        // archive.
        new("pamt_0000", "Solo PAMT 0000", "PAMT 0000 only", "0000", ""),
        new("pamt_0001", "Solo PAMT 0001", "PAMT 0001 only", "0001", ""),
        new("pamt_0002", "Solo PAMT 0002", "PAMT 0002 only", "0002", ""),
        new("pamt_0007", "Solo PAMT 0007", "PAMT 0007 only", "0007", ""),
        new("pamt_0009", "Solo PAMT 0009", "PAMT 0009 only", "0009", ""),
        new("pamt_0012", "Solo PAMT 0012", "PAMT 0012 only", "0012", ""),
        new("pamt_0015", "Solo PAMT 0015", "PAMT 0015 only", "0015", ""),
    };

    public static FilterPresetDef GetPreset(string? idOrLabel)
    {
        string v = (idOrLabel ?? "").Trim();
        return Presets.FirstOrDefault(p =>
            string.Equals(p.Id, v, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Es, v, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.En, v, StringComparison.OrdinalIgnoreCase)) ?? Presets.First(p => p.Id == "objects");
    }

    public static bool IsGameDir(string path)
        => File.Exists(Path.Combine(path, "meta", "0.papgt")) && Directory.EnumerateDirectories(path, "????").Any(d => File.Exists(Path.Combine(d, "0.pamt")));

    private static bool IsStockPamtFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length != 4) return false;
        // v1.11.00 stock is not a perfectly contiguous archive set
        // (0018/0033/0034 are absent in the tested install).  Do not
        // require a fixed expected count; the scanner uses actual folders
        // on disk.  n < 36 keeps the current stock range and legacy numeric
        // overlay behavior stable while HD## remains the managed output path.
        return int.TryParse(name, out int n) && n < 36;
    }

    private static readonly string[] SupportedSourceArchiveIds = new[]
    {
        "0000", "0001", "0002", "0007", "0009", "0012", "0015"
    };

    private static bool TryDetectSupportedArchiveIdInFolderName(string? name, out string archiveId)
    {
        archiveId = string.Empty;
        string s = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(s)) return false;

        var matches = new List<string>();
        foreach (string id in SupportedSourceArchiveIds)
        {
            int searchFrom = 0;
            while (searchFrom < s.Length)
            {
                int idx = s.IndexOf(id, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;

                int after = idx + id.Length;
                bool leftBoundaryOk = idx == 0 || !char.IsDigit(s[idx - 1]);
                bool rightBoundaryOk = after >= s.Length || !char.IsDigit(s[after]);
                if (leftBoundaryOk && rightBoundaryOk) matches.Add(id);

                searchFrom = idx + 1;
            }
        }

        if (matches.Count == 1)
        {
            archiveId = matches[0];
            return true;
        }

        // Multiple archive-looking tokens in one folder name are ambiguous, so do
        // not force an archive scope from that folder. This avoids broad/random
        // four-digit matching while still allowing names like UHD0000,
        // CDHDTR0001, and SomePack0015.
        return false;
    }

    private static bool IsArchiveFolderPrefix(string? name)
        => TryDetectSupportedArchiveIdInFolderName(name, out _);

    private static string DetectSourceRootArchive(string? textureDir)
    {
        try
        {
            string? name = Path.GetFileName(Path.GetFullPath(textureDir ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return TryDetectSupportedArchiveIdInFolderName(name, out string archiveId) ? archiveId : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static bool IsLegacyNumericOverlayFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length != 4) return false;
        return int.TryParse(name, out int n) && n >= 36;
    }

    private static bool IsGeneratedOverlayFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length != 4) return false;
        if (!name.StartsWith(ManagedOverlayPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(name[ManagedOverlayPrefix.Length..], out int n) && n >= 1 && n <= 99;
    }

    private static bool IsNonStockOverlayFolderName(string? name)
        => !string.IsNullOrWhiteSpace(name) && !IsStockPamtFolderName(name);



    private static string DisplayPerformanceMemoryMode(string? memoryMode)
    {
        string v = (memoryMode ?? "Auto").Trim();
        if (v.Equals("Full", StringComparison.OrdinalIgnoreCase) || v.Contains("Max", StringComparison.OrdinalIgnoreCase)) return "Max Performance";
        if (v.Equals("Medium", StringComparison.OrdinalIgnoreCase) || v.Contains("Medium", StringComparison.OrdinalIgnoreCase) || v.Contains("Balanced", StringComparison.OrdinalIgnoreCase)) return "Balanced";
        if (v.Equals("Safe", StringComparison.OrdinalIgnoreCase) || v.Contains("Safe", StringComparison.OrdinalIgnoreCase) || v.Contains("External", StringComparison.OrdinalIgnoreCase) || v.Contains("Slow", StringComparison.OrdinalIgnoreCase)) return "Slow / External Drive Safe Mode";
        if (v.Equals("Low", StringComparison.OrdinalIgnoreCase) || v.Contains("Low", StringComparison.OrdinalIgnoreCase)) return "Low";
        return "Auto / Recommended";
    }

    private sealed record StorageProbeResult(string Purpose, string Path, string Root, string DriveType, long ProbeBytes, double WriteMbPerSec, double ReadMbPerSec, double MaxWriteChunkMs, string Tier, string Detail);
    private sealed record StoragePerformancePolicy(string EffectiveMode, int EffectiveCustomWorkers, string Tier, bool WarnUser, string RuntimeSummary);

    private static readonly ConcurrentDictionary<string, StorageProbeResult> StorageProbeCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsAutoPerformanceMode(string? memoryMode)
    {
        string v = (memoryMode ?? "Auto").Trim().ToLowerInvariant();
        if (v.Contains("full") || v.Contains("max")) return false;
        if (v.Contains("medium") || v.Contains("balanced")) return false;
        if (v.Contains("low") || v.Contains("safe") || v.Contains("external") || v.Contains("slow")) return false;
        if (v.Contains("custom")) return false;
        return true;
    }

    public static bool ShouldUseExternalDriveSafeModeForSelectedFolders(string gameDir, string textureDir, out string summary)
    {
        summary = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(textureDir)) return false;
            if (!Directory.Exists(gameDir) || !Directory.Exists(textureDir)) return false;

            // UI/preflight detection intentionally mirrors the build-time Auto policy,
            // but it runs before applying so users on USB/external/slow storage see
            // the dropdown switch to the safer mode before the build starts.
            string outputProbeRoot = RegistryRoot(gameDir);
            var outputProbe = ProbeStoragePath("preflight-game/output", outputProbeRoot, allowWriteProbe: true, _ => { });
            var sourceProbe = ProbeStoragePath("preflight-dds-source", textureDir, allowWriteProbe: false, _ => { });
            bool sameRoot = SameStorageRoot(gameDir, textureDir);
            string worstTier = WorstStorageTier(outputProbe.Tier, sourceProbe.Tier, sameRoot);
            bool externalLike = IsExternalLikeStorage(outputProbe) || IsExternalLikeStorage(sourceProbe);

            // Be conservative for normal fixed/internal drives. A fixed NVMe can have
            // a transient slow probe right after a huge build while Windows, AV, or
            // the filesystem is still flushing. Auto should only switch to Safe Mode
            // on high-confidence signals: removable/network storage, clearly slow
            // output writes, or a very-slow source read.
            bool outputClearlySlow = outputProbe.Tier.Equals("slow", StringComparison.OrdinalIgnoreCase)
                || outputProbe.Tier.Equals("very-slow", StringComparison.OrdinalIgnoreCase);
            bool sourceClearlySlow = sourceProbe.Tier.Equals("very-slow", StringComparison.OrdinalIgnoreCase);

            bool safeRecommended = externalLike || outputClearlySlow || sourceClearlySlow;

            if (!safeRecommended)
            {
                summary = $"Storage preflight: output {DescribeStorageProbe(outputProbe)}; source {DescribeStorageProbe(sourceProbe)}; same root: {sameRoot}; selected tier: {worstTier}; Auto can stay on normal policy.";
                return false;
            }

            summary = $"Storage preflight: output {DescribeStorageProbe(outputProbe)}; source {DescribeStorageProbe(sourceProbe)}; same root: {sameRoot}; selected tier: {worstTier}; Slow / External Drive Safe Mode recommended.";
            return true;
        }
        catch (Exception ex)
        {
            summary = "Storage preflight failed: " + ex.Message;
            return false;
        }
    }

    private static StoragePerformancePolicy ResolveStoragePerformancePolicy(BuildOptions options, Action<string> log)
    {
        if (!IsAutoPerformanceMode(options.PerformanceMemoryMode))
            return new StoragePerformancePolicy(options.PerformanceMemoryMode, options.CustomPrepareWorkers, "manual-override", false, "manual performance mode selected; storage probe did not override worker policy");

        var outputProbeRoot = options.ApplyToGame ? RegistryRoot(options.GameDir) : options.OutputDir;
        var outputProbe = ProbeStoragePath("game/output", outputProbeRoot, allowWriteProbe: true, log);
        var sourceProbe = ProbeStoragePath("dds-source", options.TextureDir, allowWriteProbe: false, log);
        bool sameRoot = SameStorageRoot(options.GameDir, options.TextureDir);
        string worstTier = WorstStorageTier(outputProbe.Tier, sourceProbe.Tier, sameRoot);

        int workers = 0;
        bool warn = false;
        string effectiveMode = options.PerformanceMemoryMode;
        bool externalLike = IsExternalLikeStorage(outputProbe) || IsExternalLikeStorage(sourceProbe);

        bool outputVerySlow = outputProbe.Tier.Equals("very-slow", StringComparison.OrdinalIgnoreCase);
        bool outputSlow = outputProbe.Tier.Equals("slow", StringComparison.OrdinalIgnoreCase);
        bool sourceVerySlow = sourceProbe.Tier.Equals("very-slow", StringComparison.OrdinalIgnoreCase);

        if (externalLike)
        {
            effectiveMode = "Safe";
            workers = worstTier == "very-slow" ? 1 : (worstTier == "slow" || sameRoot ? 2 : 3);
            warn = true;
        }
        else if (outputVerySlow || sourceVerySlow)
        {
            effectiveMode = "Safe";
            workers = 1;
            warn = true;
        }
        else if (outputSlow)
        {
            effectiveMode = "Safe";
            workers = sameRoot ? 1 : 2;
            warn = true;
        }

        string runtime = $"Storage probe: output {DescribeStorageProbe(outputProbe)}; source {DescribeStorageProbe(sourceProbe)}; same root: {sameRoot}; selected tier: {worstTier}; Auto policy: "
            + (workers > 0 ? $"External Drive Safe Mode ({workers} worker(s), single-buffer I/O)" : "normal Auto / Recommended CPU/RAM policy");
        log("[runtime] " + runtime);
        if (workers > 0)
        {
            log("Slow or external storage detected. This build may be much slower because HD Overlay Builder performs heavy PAZ read/write and CRC work. External Drive Safe Mode will reduce simultaneous disk pressure.");
            log($"External Drive Safe Mode active: PAZ pipeline will use single-buffer I/O and cap payload prep to {workers} worker(s). Select Max Performance or Balanced manually only if you want to override Auto.");
        }

        return new StoragePerformancePolicy(effectiveMode, workers > 0 ? workers : options.CustomPrepareWorkers, worstTier, warn, runtime);
    }

    private static StorageProbeResult ProbeStoragePath(string purpose, string path, bool allowWriteProbe, Action<string> log)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { full = path ?? string.Empty; }
        string root = "";
        try { root = Path.GetPathRoot(full) ?? ""; } catch { }
        string cacheKey = purpose + "|" + full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        if (StorageProbeCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = allowWriteProbe
            ? RunWriteReadProbe(purpose, full, root)
            : RunReadSampleProbe(purpose, full, root);
        StorageProbeCache[cacheKey] = result;
        log("[runtime] " + DescribeStorageProbe(result));
        return result;
    }

    private static string GetDriveTypeLabel(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root)) return "unknown";
            return new DriveInfo(root).DriveType.ToString();
        }
        catch { return "unknown"; }
    }

    private static bool IsExternalLikeStorage(StorageProbeResult p)
        => p.DriveType.Equals("Removable", StringComparison.OrdinalIgnoreCase)
           || p.DriveType.Equals("Network", StringComparison.OrdinalIgnoreCase);

    private static string ApplyDriveTypeToTier(string tier, string driveType)
    {
        if (driveType.Equals("Removable", StringComparison.OrdinalIgnoreCase) || driveType.Equals("Network", StringComparison.OrdinalIgnoreCase))
        {
            if (tier == "unknown" || tier == "fast") return "slow";
        }
        return tier;
    }

    private static StorageProbeResult RunWriteReadProbe(string purpose, string path, string root)
    {
        // The old preflight used a tiny 8 MiB probe, which was too easy for
        // Windows/device cache to hide on USB 3.x external HDDs.  Use a larger
        // flushed sequential probe so Auto can catch the drives that look like a
        // normal fixed disk but collapse during large PAZ writes.
        const long preferredProbeBytes = 512L * 1024L * 1024L;
        const long minimumProbeBytes = 128L * 1024L * 1024L;
        const int chunkBytes = 4 * 1024 * 1024;
        string probeDir = path;
        try { Directory.CreateDirectory(probeDir); }
        catch { probeDir = AppContext.BaseDirectory; }

        long probeBytes = preferredProbeBytes;
        try
        {
            string probeRoot = Path.GetPathRoot(Path.GetFullPath(probeDir)) ?? root;
            if (!string.IsNullOrWhiteSpace(probeRoot))
            {
                var di = new DriveInfo(probeRoot);
                long free = di.AvailableFreeSpace;
                if (free < preferredProbeBytes + (256L * 1024L * 1024L))
                    probeBytes = Math.Max(minimumProbeBytes, Math.Min(preferredProbeBytes, Math.Max(32L * 1024L * 1024L, free / 4)));
            }
        }
        catch { }

        string probePath = Path.Combine(probeDir, ".hdupscale_storage_probe.tmp");
        byte[] buffer = new byte[chunkBytes];
        RandomNumberGenerator.Fill(buffer);
        double write = 0;
        double read = 0;
        double maxWriteChunkMs = 0;
        string detail = $"flushed sequential write/read probe ({FormatBytesShort(probeBytes)})";
        try
        {
            long written = 0;
            var swTotal = Stopwatch.StartNew();
            using (var fs = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None, chunkBytes, FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                while (written < probeBytes)
                {
                    int want = (int)Math.Min(buffer.Length, probeBytes - written);
                    var swChunk = Stopwatch.StartNew();
                    fs.Write(buffer, 0, want);
                    swChunk.Stop();
                    if (swChunk.Elapsed.TotalMilliseconds > maxWriteChunkMs) maxWriteChunkMs = swChunk.Elapsed.TotalMilliseconds;
                    written += want;
                }
                fs.Flush(true);
            }
            swTotal.Stop();
            write = SecondsToMbPerSec(written, swTotal.Elapsed.TotalSeconds);

            Array.Clear(buffer);
            long total = 0;
            swTotal.Restart();
            using (var fs = new FileStream(probePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkBytes, FileOptions.SequentialScan))
            {
                while (total < probeBytes)
                {
                    int want = (int)Math.Min(buffer.Length, probeBytes - total);
                    int n = fs.Read(buffer, 0, want);
                    if (n <= 0) break;
                    total += n;
                }
            }
            swTotal.Stop();
            read = total > 0 ? SecondsToMbPerSec(total, swTotal.Elapsed.TotalSeconds) : 0;
        }
        catch (Exception ex)
        {
            detail = "probe failed: " + ex.Message;
        }
        finally
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); } catch { }
        }

        string driveType = GetDriveTypeLabel(root);
        string tier = ApplyDriveTypeToTier(ClassifyStorageTier(write, read, maxWriteChunkMs, probeBytes), driveType);
        return new StorageProbeResult(purpose, path, root, driveType, probeBytes, write, read, maxWriteChunkMs, tier, detail);
    }

    private static StorageProbeResult RunReadSampleProbe(string purpose, string path, string root)
    {
        const long maxBytes = 256L * 1024L * 1024L;
        long totalBytes = 0;
        double read = 0;
        string detail = $"source read sample ({FormatBytesShort(maxBytes)} max)";
        try
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(p => SupportedExts.Contains(Path.GetExtension(p)))
                .OrderByDescending(p => { try { return new FileInfo(p).Length; } catch { return 0L; } })
                .Take(8)
                .ToList();
            byte[] buffer = new byte[1024 * 1024];
            var sw = Stopwatch.StartNew();
            foreach (var file in files)
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
                long remaining = Math.Min(maxBytes - totalBytes, fs.Length);
                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int n = fs.Read(buffer, 0, want);
                    if (n <= 0) break;
                    totalBytes += n;
                    remaining -= n;
                    if (totalBytes >= maxBytes) break;
                }
                if (totalBytes >= maxBytes) break;
            }
            sw.Stop();
            read = totalBytes > 0 ? SecondsToMbPerSec(totalBytes, sw.Elapsed.TotalSeconds) : 0;
            if (totalBytes == 0) detail = "no DDS sample available";
        }
        catch (Exception ex)
        {
            detail = "read sample failed: " + ex.Message;
        }
        string driveType = GetDriveTypeLabel(root);
        string tier = ApplyDriveTypeToTier(ClassifyStorageTier(0, read, 0, totalBytes), driveType);
        return new StorageProbeResult(purpose, path, root, driveType, totalBytes, 0, read, 0, tier, detail);
    }

    private static double SecondsToMbPerSec(long bytes, double seconds)
        => seconds <= 0 ? 0 : (bytes / 1024.0 / 1024.0) / seconds;

    private static string FormatBytesShort(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " GiB";
        if (bytes >= 1024L * 1024L) return (bytes / 1024.0 / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " MiB";
        if (bytes >= 1024L) return (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KiB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }

    private static string ClassifyStorageTier(double writeMb, double readMb, double maxWriteChunkMs = 0, long probeBytes = 0)
    {
        // Use sustained write as the strongest signal because the slow user case
        // was PAZ output taking nearly the entire build.  USB/external HDDs can
        // report as fixed disks and can look fine for tiny cached writes, so the
        // larger flushed probe and max-chunk stall check intentionally err on the
        // safe side.
        if (writeMb > 0)
        {
            if (writeMb < 50 || maxWriteChunkMs > 1500) return "very-slow";
            if (writeMb < 120 || maxWriteChunkMs > 750) return "slow";
            if (writeMb < 350 || maxWriteChunkMs > 300) return "moderate";
            return "fast";
        }

        if (readMb > 0)
        {
            if (readMb < 40) return "very-slow";
            if (readMb < 100) return "slow";
            if (readMb < 300) return "moderate";
            return "fast";
        }

        return "unknown";
    }

    private static string WorstStorageTier(string outputTier, string sourceTier, bool sameRoot)
    {
        int rank(string t) => t switch { "very-slow" => 4, "slow" => 3, "moderate" => 2, "fast" => 1, _ => 0 };
        string worst = rank(outputTier) >= rank(sourceTier) ? outputTier : sourceTier;
        return worst == "unknown" ? outputTier : worst;
    }

    private static bool SameStorageRoot(string a, string b)
    {
        try
        {
            string ra = Path.GetPathRoot(Path.GetFullPath(a)) ?? "";
            string rb = Path.GetPathRoot(Path.GetFullPath(b)) ?? "";
            return !string.IsNullOrWhiteSpace(ra) && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string DescribeStorageProbe(StorageProbeResult p)
        => $"{p.Purpose} storage: tier {p.Tier}, drive {p.DriveType}, probe {FormatBytesShort(p.ProbeBytes)}, write {(p.WriteMbPerSec > 0 ? p.WriteMbPerSec.ToString("F1") : "n/a")} MB/s, read {(p.ReadMbPerSec > 0 ? p.ReadMbPerSec.ToString("F1") : "n/a")} MB/s, max write chunk {(p.MaxWriteChunkMs > 0 ? p.MaxWriteChunkMs.ToString("F0") : "n/a")} ms, root '{p.Root}', path '{p.Path}' ({p.Detail})";

    private sealed class BuildTimingBreakdown
    {
        public Stopwatch Total { get; } = Stopwatch.StartNew();
        public double ScanMatchSeconds { get; set; }
        public double PayloadPrepSeconds { get; set; }
        public double PazWriteSeconds { get; set; }
        public double PazCreatePreallocateSeconds { get; set; }
        public double PazPayloadWriteSeconds { get; set; }
        public double PazCrcHashSeconds { get; set; }
        public double PazFinalizeSeconds { get; set; }
        public double PamtBuildSeconds { get; set; }
        public double PathcUpdateSeconds { get; set; }
        public double PapgtRebuildSeconds { get; set; }
        public double ManifestSaveSeconds { get; set; }

        public void CopyOverlayTimings(OverlayBuilder.OverlayBuildTimings overlayTimings)
        {
            PayloadPrepSeconds = overlayTimings.PayloadPrepSeconds;
            PazWriteSeconds = overlayTimings.PazWriteSeconds;
            PazCreatePreallocateSeconds = overlayTimings.PazCreatePreallocateSeconds;
            PazPayloadWriteSeconds = overlayTimings.PazPayloadWriteSeconds;
            PazCrcHashSeconds = overlayTimings.PazCrcHashSeconds;
            PazFinalizeSeconds = overlayTimings.PazFinalizeSeconds;
            PamtBuildSeconds = overlayTimings.PamtBuildSeconds;
        }
    }

    private static void LogTimingBreakdown(Action<string> log, BuildTimingBreakdown timing)
    {
        log($"Timing: scan/match: {timing.ScanMatchSeconds:F1}s");
        log($"Timing: payload prep: {timing.PayloadPrepSeconds:F1}s");
        log($"Timing: PAZ write: {timing.PazWriteSeconds:F1}s");
        log($"Timing: PAZ create/preallocate: {timing.PazCreatePreallocateSeconds:F1}s");
        log($"Timing: PAZ payload write: {timing.PazPayloadWriteSeconds:F1}s");
        log($"Timing: PAZ CRC/hash: {timing.PazCrcHashSeconds:F1}s");
        log($"Timing: PAZ table/finalize: {timing.PazFinalizeSeconds:F1}s");
        log($"Timing: PAMT build: {timing.PamtBuildSeconds:F1}s");
        log($"Timing: PATHC update: {timing.PathcUpdateSeconds:F1}s");
        log($"Timing: PAPGT rebuild: {timing.PapgtRebuildSeconds:F1}s");
        log($"Timing: manifest save: {timing.ManifestSaveSeconds:F1}s");
        log($"Timing: total: {timing.Total.Elapsed.TotalSeconds:F1}s");
    }

    public BuildResult BuildOrApply(BuildOptions options, Action<string> log, Action<int, string>? progress = null, CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;
        var timing = new BuildTimingBreakdown();
        var scanMatchWatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(0, CDTextureOverlayBuilder.L.IsSpanish(options.Language) ? "INICIANDO" : "STARTING");
        if (!IsGameDir(options.GameDir)) throw new FileNotFoundException("The game folder does not look valid. It must contain meta/0.papgt and 0000/0.pamt folders.");
        if (!Directory.Exists(options.TextureDir)) throw new DirectoryNotFoundException("The texture folder does not exist.");
        if (string.IsNullOrWhiteSpace(options.ModName)) throw new InvalidOperationException("Enter a mod name.");
        log($"Overlay folder split target: {options.SplitGb:F2} GiB.");
        if (options.SplitGb > 3.90) log("Stock-style layout: overlay folders may contain multiple PAZ parts; normal PAZ parts are capped around 900 MiB to stay close to stock archive behavior; oversized single payloads under 4 GiB are isolated into dedicated PAZ parts.");
        log($"Performance Mode: {DisplayPerformanceMemoryMode(options.PerformanceMemoryMode)}" + (string.Equals(options.PerformanceMemoryMode, "Custom", StringComparison.OrdinalIgnoreCase) ? $" ({options.CustomPrepareWorkers} requested worker(s))" : "") + ".");
        var storagePolicy = ResolveStoragePerformancePolicy(options, log);
        string effectivePerformanceMemoryMode = storagePolicy.EffectiveMode;
        int effectiveCustomPrepareWorkers = storagePolicy.EffectiveCustomWorkers;
        log($"Memory before build: system {OverlayBuilder.CurrentMemorySnapshotText()}.");
        log($"Process memory before build: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
        log("== Scan / matching ==");
        var (matches, skipped, ambiguous, stats, looseDiagnostics) = MatchTextures(options, log, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var duplicateTargetNotPacked = ResolveDuplicateTargetMatches(ref matches, options.Language);
        if (duplicateTargetNotPacked.Count > 0)
        {
            skipped.AddRange(duplicateTargetNotPacked);
            stats["duplicate_target_prefer_exact_path"] = duplicateTargetNotPacked.Count;
            log($"NOTICE: {duplicateTargetNotPacked.Count} duplicate source file(s) pointed at an already selected internal target and were not packed twice. Exact full/archive path matches were preferred.");
        }
        if (matches.Count == 0) throw new InvalidOperationException("No textures could be matched. Use full internal paths or enable unique filename matching.");

        if (options.ScanExistingModDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogExternalOverlayConflicts(options.GameDir, matches, log);
        }

        progress?.Invoke(15, CDTextureOverlayBuilder.L.IsSpanish(options.Language) ? "COINCIDENCIAS" : "MATCHED");
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string safeName = SafeName(options.ModName);
        string buildRoot = Path.Combine(options.OutputDir, $"{safeName}_{stamp}");
        string reportPath = Path.Combine(buildRoot, "overlay_report.txt");
        string manifestPath = Path.Combine(buildRoot, "manifest.json");

        var allResolvedMatchesForSourceManifest = matches.ToList();
        var sourceManifestSkippedUnchanged = new List<MatchedFile>();
        SourceManifestState? loadedSourceManifest = null;
        SourceManifestState? activeTargetManifest = null;
        ActiveBuildSnapshot? activeBuildSnapshot = null;
        var matchesForNewOverlays = matches;
        var matchesForExistingOverlayUpdates = new List<MatchedFile>();
        var existingUpdateTargets = new Dictionary<string, ExistingOverlayTarget>(StringComparer.OrdinalIgnoreCase);
        if (options.ApplyToGame && options.UpdateExistingOverlays && !options.DryRun)
        {
            existingUpdateTargets = BuildManagedOverlayTargetIndex(options.GameDir, log);
            activeBuildSnapshot = TryGetActiveBuildSnapshot(options.GameDir, log);
            activeTargetManifest = TryLoadActiveTargetManifestForUpdate(options.GameDir, activeBuildSnapshot, existingUpdateTargets, options, log);
            loadedSourceManifest = TryLoadSourceManifestForUpdate(options.GameDir, options.TextureDir, activeBuildSnapshot, options, log);
            if (loadedSourceManifest != null)
            {
                WarnRemovedSourceFilesFromManifest(options.TextureDir, loadedSourceManifest, log, cancellationToken);
                var filtered = FilterUnchangedSourceManifestMatches(matches, loadedSourceManifest, existingUpdateTargets, activeBuildSnapshot!, log);
                matches = filtered.ChangedMatches;
                sourceManifestSkippedUnchanged = filtered.UnchangedMatches;
                if (sourceManifestSkippedUnchanged.Count > 0) stats["source_manifest_unchanged_skipped"] = sourceManifestSkippedUnchanged.Count;
            }
            else
            {
                if (activeTargetManifest != null && existingUpdateTargets.Count > 0 && activeBuildSnapshot != null)
                {
                    var filtered = FilterUnchangedActiveTargetManifestMatches(matches, activeTargetManifest, existingUpdateTargets, activeBuildSnapshot, options.TextureDir, log);
                    matches = filtered.ChangedMatches;
                    sourceManifestSkippedUnchanged = filtered.UnchangedMatches;
                    if (sourceManifestSkippedUnchanged.Count > 0) stats["active_target_manifest_unchanged_skipped"] = sourceManifestSkippedUnchanged.Count;
                    if (sourceManifestSkippedUnchanged.Count > 0)
                        log("Update Existing Build: selected source folder was reconciled against the installed target manifest; only changed selected targets will be hotfixed.");
                }
                else
                {
                    log("Update Existing Build: no trusted source-root manifest is available; selected DDS files will be matched to installed targets and processed conservatively. Missing files outside the selected folder are ignored for partial-folder hotfixes.");
                    int selectedInstalledTargetMatches = matches.Select(m => OverlayBuilder.Norm(m.FullPath)).Where(existingUpdateTargets.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    if (selectedInstalledTargetMatches >= 1000 && existingUpdateTargets.Count >= 1000)
                        log($"WARN: Update Existing Build matched {selectedInstalledTargetMatches:n0} installed target(s) without a trusted unchanged-skip manifest. This is safe but may rebuild more PAZ/PAMT content than necessary.");
                }
            }

            if (existingUpdateTargets.Count > 0)
            {
                var newOnly = new List<MatchedFile>();
                foreach (var m in matches)
                {
                    if (existingUpdateTargets.ContainsKey(OverlayBuilder.Norm(m.FullPath))) matchesForExistingOverlayUpdates.Add(m);
                    else newOnly.Add(m);
                }
                matchesForNewOverlays = newOnly;
                if (matchesForExistingOverlayUpdates.Count > 0)
                    log($"Update Existing Build: {matchesForExistingOverlayUpdates.Count} changed texture(s) will rebuild affected managed overlay target(s).");
            }
            else if (loadedSourceManifest != null)
            {
                log("Update source manifest loaded but active overlay target index was empty; unchanged skip was disabled for safety.");
                matches = allResolvedMatchesForSourceManifest.ToList();
                sourceManifestSkippedUnchanged.Clear();
                matchesForNewOverlays = matches;
            }
        }

        var preBuildSkippedBreakdown = ClassifySkipped(skipped);
        var chunks = SplitMatches(matchesForNewOverlays, options.SplitGb);
        log($"Matched textures: {allResolvedMatchesForSourceManifest.Count}. Changed/new to process: {matches.Count}. Unchanged skipped: {sourceManifestSkippedUnchanged.Count}. Chunks/overlays: {chunks.Count}. Not found: {preBuildSkippedBreakdown.NotFound}. Already covered duplicates: {preBuildSkippedBreakdown.DuplicateSourceIgnored}. Failed/skipped: {preBuildSkippedBreakdown.FailedSkipped}. Ambiguous: {ambiguous.Count}.");
        if (matchesForExistingOverlayUpdates.Count > 0 || sourceManifestSkippedUnchanged.Count > 0) log($"Update Existing Build: changed existing targets: {matchesForExistingOverlayUpdates.Count}; new overlay textures: {matchesForNewOverlays.Count}; unchanged skipped: {sourceManifestSkippedUnchanged.Count}.");
        if (ambiguous.Count > 0) log($"NOTICE: {ambiguous.Count} textures were not applied because their loose filenames matched multiple vanilla PAMT targets. See the AMBIGUOUS section in the report.");
        scanMatchWatch.Stop();
        timing.ScanMatchSeconds = scanMatchWatch.Elapsed.TotalSeconds;

        var overlayDirs = AllocateOverlayDirs(options.GameDir, chunks.Count, options.ApplyToGame ? null : buildRoot);
        var newOverlayOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int oi = 0; oi < chunks.Count && oi < overlayDirs.Count; oi++)
        {
            foreach (var m in chunks[oi]) newOverlayOwners[OverlayBuilder.Norm(m.FullPath)] = overlayDirs[oi];
        }
        log(options.ApplyToGame ? (overlayDirs.Count > 0 ? $"New overlays planned: {string.Join(", ", overlayDirs)}." : sourceManifestSkippedUnchanged.Count > 0 && matches.Count == 0 ? "No new or changed DDS targets detected; active managed overlays will be left untouched." : "No new overlay folders are needed; existing managed overlays will be updated in place.") : $"Build only: overlays planned in output folder: {string.Join(", ", overlayDirs)}.");
        if (options.ApplyToGame) MigrateLegacyBuildOutput(options.GameDir, log);
        Directory.CreateDirectory(buildRoot);
        try { File.WriteAllText(Path.Combine(buildRoot, ".incomplete_build"), DateTime.Now.ToString("s"), Encoding.UTF8); } catch { }
        if (options.ApplyToGame && options.UpdateExistingOverlays && !options.DryRun && matches.Count == 0 && sourceManifestSkippedUnchanged.Count > 0)
        {
            log("Update Existing Build: all matched DDS targets are unchanged. No PAZ/PAMT/PATHC/PAPGT rebuild was needed.");
            var manifestSaveWatch = Stopwatch.StartNew();
            // No-change updates must not replace the global active target manifest
            // with only the selected source-root subset. Refresh only when we can
            // merge against a trusted existing active target manifest, or when the
            // selected source clearly covers the full installed target index.
            int selectedNoChangeTargetCount = allResolvedMatchesForSourceManifest
                .Select(m => OverlayBuilder.Norm(m.FullPath))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            bool canRefreshNoChangeActiveTargets = activeTargetManifest != null
                || (existingUpdateTargets.Count > 0 && selectedNoChangeTargetCount >= existingUpdateTargets.Count);
            if (canRefreshNoChangeActiveTargets)
            {
                TrySaveActiveTargetManifest(options.GameDir, options.TextureDir, allResolvedMatchesForSourceManifest, newOverlayOwners, existingUpdateTargets, activeTargetManifest, options, log, activeTargetManifest != null);
            }
            else
            {
                log($"Active target manifest not refreshed on no-change update: selected source has {selectedNoChangeTargetCount:n0} target record(s), but the installed managed build has {existingUpdateTargets.Count:n0}. This prevents partial-source manifest overwrite.");
            }
            WriteReport(reportPath, options, new List<MatchedFile>(), skipped, ambiguous, new List<string>(), new List<string>(), stats, new List<PathcUpdateResult>(), looseDiagnostics);
            var noChangeSkippedBreakdown = ClassifySkipped(skipped);
            WriteJson(manifestPath, new
            {
                app = AppName,
                version = AppVersion,
                update_no_changes = true,
                mod_name = options.ModName,
                matched_count = allResolvedMatchesForSourceManifest.Count,
                changed_count = 0,
                unchanged_source_manifest_skipped = sourceManifestSkippedUnchanged.Count,
                active_overlay_dirs = activeBuildSnapshot?.OverlayDirs ?? new List<string>(),
                skipped_count = skipped.Count,
                not_found_count = noChangeSkippedBreakdown.NotFound,
                duplicate_source_ignored_count = noChangeSkippedBreakdown.DuplicateSourceIgnored,
                failed_skipped_count = noChangeSkippedBreakdown.FailedSkipped,
                ambiguous_count = ambiguous.Count,
                report = reportPath
            });
            manifestSaveWatch.Stop();
            timing.ManifestSaveSeconds = manifestSaveWatch.Elapsed.TotalSeconds;
            LogTimingBreakdown(log, timing);
            try { string incomplete = Path.Combine(buildRoot, ".incomplete_build"); if (File.Exists(incomplete)) File.Delete(incomplete); } catch { }
            progress?.Invoke(100, CDTextureOverlayBuilder.L.IsSpanish(options.Language) ? "TERMINADO" : "FINISHED");
            return new BuildResult(allResolvedMatchesForSourceManifest.Count, skipped.Count, ambiguous.Count, activeBuildSnapshot?.OverlayDirs ?? new List<string>(), buildRoot, manifestPath, reportPath, true, (DateTime.UtcNow - start).TotalSeconds, noChangeSkippedBreakdown.NotFound, noChangeSkippedBreakdown.DuplicateSourceIgnored, noChangeSkippedBreakdown.FailedSkipped);
        }
        if (options.DryRun)
        {
            var manifestSaveWatch = Stopwatch.StartNew();
            WriteReport(reportPath, options, matches, skipped, ambiguous, overlayDirs, new List<string>(), stats, new List<PathcUpdateResult>(), looseDiagnostics);
            var drySkippedBreakdown = ClassifySkipped(skipped);
            WriteJson(manifestPath, new { app = AppName, version = AppVersion, dry_run = true, mod_name = options.ModName, overlay_dirs_planned = overlayDirs, matched_count = matches.Count, skipped_count = skipped.Count, not_found_count = drySkippedBreakdown.NotFound, duplicate_source_ignored_count = drySkippedBreakdown.DuplicateSourceIgnored, failed_skipped_count = drySkippedBreakdown.FailedSkipped, ambiguous_count = ambiguous.Count, report = reportPath });
            manifestSaveWatch.Stop();
            timing.ManifestSaveSeconds = manifestSaveWatch.Elapsed.TotalSeconds;
            log("Dry run finished. No PAZ/PAMT or game meta files were written.");
            LogTimingBreakdown(log, timing);
            try { string incomplete = Path.Combine(buildRoot, ".incomplete_build"); if (File.Exists(incomplete)) File.Delete(incomplete); } catch { }
            progress?.Invoke(100, CDTextureOverlayBuilder.L.IsSpanish(options.Language) ? "TERMINADO" : "FINISHED");
            return new BuildResult(matches.Count, skipped.Count, ambiguous.Count, overlayDirs, buildRoot, manifestPath, reportPath, false, (DateTime.UtcNow - start).TotalSeconds, drySkippedBreakdown.NotFound, drySkippedBreakdown.DuplicateSourceIgnored, drySkippedBreakdown.FailedSkipped);
        }

        string? backupDir = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (options.ApplyToGame && options.BackupMeta)
        {
            backupDir = CopyCurrentMetaBackup(options.GameDir, log);
            CopyRegistryStateBackup(options.GameDir, backupDir, log);
            DebugFailureInjector.Check(DebugFailureInjector.AfterMetaBackup, log);
        }
        var allOverlayEntriesForPathc = new List<OverlayEntry>();
        var matchesForPathc = new List<MatchedFile>();
        var chunkInfos = new List<object>();
        string vanillaPathc = Path.Combine(options.GameDir, "meta", "0.pathc");
        var modifiedPamts = new Dictionary<string, byte[]>();
        var updatedOverlayDirs = new List<string>();
        var pathcSummaries = new List<PathcUpdateResult>();
        var overlayTimings = new OverlayBuilder.OverlayBuildTimings();
        var createdNewOverlayDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hotfixOverlayBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string pathcReplaySnapshot = string.Empty;

        try
        {

        if (matchesForExistingOverlayUpdates.Count > 0)
        {
            log("== Updating existing managed overlays ==");
            var hotfix = RebuildExistingManagedOverlays(options.GameDir, matchesForExistingOverlayUpdates, existingUpdateTargets, modifiedPamts, hotfixOverlayBackups, log, progress, File.Exists(vanillaPathc) ? vanillaPathc : null, effectivePerformanceMemoryMode, effectiveCustomPrepareWorkers, overlayTimings, cancellationToken);
            foreach (var entry in hotfix.entries)
            {
                if (string.IsNullOrWhiteSpace(entry.OverlayDir) && !string.IsNullOrWhiteSpace(entry.EntryPath))
                {
                    var owner = existingUpdateTargets.TryGetValue(OverlayBuilder.Norm(string.IsNullOrEmpty(entry.DirPath) ? entry.EntryPath : $"{entry.DirPath.Trim('/')}/{entry.Filename}"), out var target) ? target.OverlayDir : "";
                    if (!string.IsNullOrWhiteSpace(owner)) entry.OverlayDir = owner;
                }
            }
            allOverlayEntriesForPathc.AddRange(hotfix.entries);
            matchesForPathc.AddRange(matchesForExistingOverlayUpdates);
            foreach (var ci in hotfix.chunkInfos) chunkInfos.Add(ci);
            updatedOverlayDirs = hotfix.overlayDirs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            log($"Existing overlays rebuilt: {string.Join(", ", updatedOverlayDirs)}.");
        }

        if (chunks.Count > 0)
        {
            log("== Build overlay PAZ/PAMT ==");
            for (int ci = 0; ci < chunks.Count; ci++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string od = overlayDirs[ci];
                var chunk = chunks[ci];
                log($"Building overlay {ci + 1}/{chunks.Count} -> {od} ({chunk.Count} textures)...");
                progress?.Invoke(15 + (int)Math.Round((ci / Math.Max(1.0, chunks.Count)) * 70.0), $"{ci + 1}/{chunks.Count}");
                string targetBase = options.ApplyToGame ? options.GameDir : buildRoot;
                string outDir = Path.Combine(targetBase, od);
                Directory.CreateDirectory(outDir);
                WriteManagedOverlayMarker(outDir, od, options);
                if (options.ApplyToGame)
                {
                    createdNewOverlayDirs.Add(od);
                    WriteIncompleteOverlayMarker(outDir, od);
                    DebugFailureInjector.Check(DebugFailureInjector.FreshAfterIncompleteOverlayCreation, log);
                }
                var builderInputs = chunk.Select(m => (m.SourcePath, m.Metadata())).ToList();
                var (pamtBytes, entries, pazSize) = OverlayBuilder.BuildOverlayToFile(builderInputs, Path.Combine(outDir, "0.paz"), options.GameDir, (i, total, name) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int basePct = 15 + (int)Math.Round((ci / Math.Max(1.0, chunks.Count)) * 70.0);
                    int nextPct = 15 + (int)Math.Round(((ci + 1) / Math.Max(1.0, chunks.Count)) * 70.0);
                    int pct = total > 0 ? basePct + (int)Math.Round((nextPct - basePct) * (i / Math.Max(1.0, total))) : basePct;
                    string clean = CleanProgressName(name);
                    bool isStage = name.StartsWith("[stage]", StringComparison.OrdinalIgnoreCase);
                    bool isHash = name.StartsWith("[hash]", StringComparison.OrdinalIgnoreCase);
                    bool isPrepare = name.StartsWith("[prepare]", StringComparison.OrdinalIgnoreCase);
                    bool isWrite = name.StartsWith("[write]", StringComparison.OrdinalIgnoreCase);
                    bool isOversizedGuard = clean.StartsWith("Oversized texture packed into dedicated PAZ part", StringComparison.OrdinalIgnoreCase);
                    string phase = isPrepare ? "PREP" : isWrite ? "WRITE" : "";
                    string progressText = isStage
                        ? clean
                        : isHash
                            ? $"{od} HASH"
                            : !string.IsNullOrEmpty(phase)
                                ? $"{od} {phase} {Math.Min(i, Math.Max(1, total))}/{Math.Max(1, total)}"
                                : $"{od} {Math.Min(i, Math.Max(1, total))}/{Math.Max(1, total)}";
                    progress?.Invoke(Math.Min(85, pct), progressText);
                    if (isStage) log($"  overlay {ci + 1}/{chunks.Count}: {clean}");
                    else if (isHash)
                    {
                        if (i == total || i % 512 == 0) log($"  overlay {ci + 1}/{chunks.Count}: {clean}");
                    }
                    else if (isOversizedGuard)
                    {
                        log($"  overlay {ci + 1}/{chunks.Count}: {clean}");
                    }
                    else if (i >= total || i % 50 == 0)
                    {
                        string logPhase = isPrepare ? "prep" : isWrite ? "write" : "step";
                        log($"  overlay {ci + 1}/{chunks.Count}: {logPhase} {Math.Min(i, total)}/{total} {clean}");
                    }
                }, File.Exists(vanillaPathc) ? vanillaPathc : null, cancellationToken, effectivePerformanceMemoryMode, effectiveCustomPrepareWorkers, overlayTimings);
                SetOverlayDir(entries, od);
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllBytes(Path.Combine(outDir, "0.pamt"), pamtBytes);
                modifiedPamts[od] = pamtBytes;
                allOverlayEntriesForPathc.AddRange(entries);
                matchesForPathc.AddRange(chunk);
                chunkInfos.Add(new { overlay_dir = od, texture_count = chunk.Count, paz_size = pazSize, pamt_size = pamtBytes.Length, entries = entries });

                // The PAZ writer already clears per-part payload buffers. After each
                // overlay, drop temporary build-input references and request a one-time
                // LOH cleanup so repeated manual applies do not keep stale prepared
                // payload memory around until the app is relaunched.
                builderInputs.Clear();
                OverlayBuilder.ReleaseCompletedBuildMemory();
                log($"  overlay {ci + 1}/{chunks.Count}: post-overlay cleanup process memory: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
            }
        }

        if (options.ApplyToGame)
        {
            if (matchesForExistingOverlayUpdates.Count > 0) DebugFailureInjector.Check(DebugFailureInjector.HotfixAfterPamtBeforePathc, log);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(88, "PATHC");
            log("== Updating meta/0.pathc ==");
            var pathcWatch = Stopwatch.StartNew();
            var pathcUpdate = UpdatePathcForMatches(options.GameDir, matchesForPathc, allOverlayEntriesForPathc, log, progress, 88, 95, "PATHC");
            cancellationToken.ThrowIfCancellationRequested();
            if (pathcUpdate.bytes != null)
            {
                SafeWrite(Path.Combine(options.GameDir, "meta", "0.pathc"), pathcUpdate.bytes);
                pathcReplaySnapshot = CopyPathcReplaySnapshot(options.GameDir, "apply", log);
            }
            DebugFailureInjector.Check(DebugFailureInjector.AfterPathcBeforePapgt, log);
            pathcWatch.Stop();
            timing.PathcUpdateSeconds += pathcWatch.Elapsed.TotalSeconds;
            pathcSummaries.Add(pathcUpdate.summary);
            progress?.Invoke(96, "PAPGT");
            log("== Rebuilding meta/0.papgt ==");
            cancellationToken.ThrowIfCancellationRequested();
            var papgtWatch = Stopwatch.StartNew();
            byte[] papgt = new PapgtManager(options.GameDir).Rebuild(modifiedPamts);
            SafeWrite(Path.Combine(options.GameDir, "meta", "0.papgt"), papgt);
            papgtWatch.Stop();
            timing.PapgtRebuildSeconds += papgtWatch.Elapsed.TotalSeconds;
        }

        timing.CopyOverlayTimings(overlayTimings);
        var skippedBreakdown = ClassifySkipped(skipped);
        var manifest = new Dictionary<string, object?>
        {
            ["app"] = AppName, ["version"] = AppVersion, ["created_at"] = DateTime.Now.ToString("s"),
            ["game_dir"] = options.GameDir, ["texture_dir"] = options.TextureDir, ["output_dir"] = buildRoot,
            ["mod_name"] = options.ModName, ["applied_to_game"] = options.ApplyToGame,
            ["target_pamt_dir"] = options.TargetPamtDir, ["target_full_prefix"] = options.TargetFullPrefix,
            ["overlay_dirs"] = overlayDirs, ["updated_overlay_dirs"] = updatedOverlayDirs,
            ["backup_dir"] = backupDir, ["matched_count"] = allResolvedMatchesForSourceManifest.Count,
            ["changed_or_new_count"] = matches.Count,
            ["unchanged_source_manifest_skipped"] = sourceManifestSkippedUnchanged.Count,
            ["skipped_count"] = skipped.Count,
            ["not_found_count"] = skippedBreakdown.NotFound,
            ["duplicate_source_ignored_count"] = skippedBreakdown.DuplicateSourceIgnored,
            ["failed_skipped_count"] = skippedBreakdown.FailedSkipped,
            ["ambiguous_count"] = ambiguous.Count,
            ["updated_existing_count"] = matchesForExistingOverlayUpdates.Count,
            ["new_overlay_texture_count"] = matchesForNewOverlays.Count,
            ["report"] = reportPath, ["chunks"] = chunkInfos,
            ["pathc_replay_snapshot"] = pathcReplaySnapshot,
            ["matches"] = matches, ["overlay_entries"] = allOverlayEntriesForPathc,
            ["matching_policy"] = BuildMatchingPolicy(options), ["performance_memory_mode"] = options.PerformanceMemoryMode, ["effective_performance_memory_mode"] = effectivePerformanceMemoryMode, ["effective_custom_prepare_workers"] = effectiveCustomPrepareWorkers, ["storage_performance_tier"] = storagePolicy.Tier
        };
        var finalManifestSaveWatch = Stopwatch.StartNew();
        WriteReport(reportPath, options, matches, skipped, ambiguous, overlayDirs, updatedOverlayDirs, stats, pathcSummaries, looseDiagnostics);
        WriteJson(manifestPath, manifest);
        if (options.ApplyToGame && overlayDirs.Count > 0)
        {
            DebugFailureInjector.Check(DebugFailureInjector.BeforeActiveRegistryWrite, log);
            RegisterAppliedManifest(options.GameDir, manifestPath, manifest, log, initializeManagedBase: !options.UpdateExistingOverlays);
        }
        else if (options.ApplyToGame && updatedOverlayDirs.Count > 0)
        {
            DebugFailureInjector.Check(DebugFailureInjector.BeforeActiveRegistryWrite, log);
            IncrementActiveBuildRevision(options.GameDir, "Update Existing Build modified installed managed PAZ/PAMT content", log);
        }
        if (options.ApplyToGame && !options.DryRun)
        {
            DebugFailureInjector.Check(DebugFailureInjector.BeforeSourceManifestSave, log);
            bool mergeActiveTargetManifest = options.UpdateExistingOverlays && (matchesForExistingOverlayUpdates.Count > 0 || sourceManifestSkippedUnchanged.Count > 0);
            TrySaveActiveTargetManifest(options.GameDir, options.TextureDir, allResolvedMatchesForSourceManifest, newOverlayOwners, existingUpdateTargets, activeTargetManifest, options, log, mergeActiveTargetManifest);
            TrySaveSourceManifest(options.GameDir, options.TextureDir, allResolvedMatchesForSourceManifest, newOverlayOwners, existingUpdateTargets, loadedSourceManifest, options, log);
        }
        finalManifestSaveWatch.Stop();
        timing.ManifestSaveSeconds += finalManifestSaveWatch.Elapsed.TotalSeconds;
        try { string incomplete = Path.Combine(buildRoot, ".incomplete_build"); if (File.Exists(incomplete)) File.Delete(incomplete); } catch { }
        foreach (var od in createdNewOverlayDirs) ClearIncompleteOverlayMarker(Path.Combine(options.GameDir, od));
        log(options.ApplyToGame ? "Applied to game. Overlay dirs + PATHC + PAPGT updated." : "Build only finished.");
        log($"Report: {reportPath}");
        log($"Memory before cleanup: system {OverlayBuilder.CurrentMemorySnapshotText()}.");
        log($"Process memory before cleanup: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
        OverlayBuilder.ReleaseCompletedBuildMemory(trimWorkingSet: true);
        log($"Memory after cleanup: system {OverlayBuilder.CurrentMemorySnapshotText()}.");
        log($"Process memory after cleanup: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
        LogTimingBreakdown(log, timing);
        log($"Total time: {(DateTime.UtcNow - start).TotalSeconds:F1}s");
        progress?.Invoke(100, CDTextureOverlayBuilder.L.IsSpanish(options.Language) ? "TERMINADO" : "FINISHED");
        DeleteHotfixCancelBackups(hotfixOverlayBackups, log);
        var resultDirs = updatedOverlayDirs.Concat(overlayDirs).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        return new BuildResult(allResolvedMatchesForSourceManifest.Count, skipped.Count, ambiguous.Count, resultDirs, buildRoot, manifestPath, reportPath, options.ApplyToGame, (DateTime.UtcNow - start).TotalSeconds, skippedBreakdown.NotFound, skippedBreakdown.DuplicateSourceIgnored, skippedBreakdown.FailedSkipped);
        }
        catch (OperationCanceledException)
        {
            log("Normal cleanup/rollback path entered.");
            CleanupCancelledBuild(options.GameDir, buildRoot, createdNewOverlayDirs, hotfixOverlayBackups, backupDir, options.ApplyToGame, log);
            log("Cleanup/rollback complete.");
            throw;
        }
        catch (Exception ex)
        {
            if (DebugFailureInjector.IsSimulated(ex)) log("Normal cleanup/rollback path entered.");
            CleanupCancelledBuild(options.GameDir, buildRoot, createdNewOverlayDirs, hotfixOverlayBackups, backupDir, options.ApplyToGame, log);
            if (DebugFailureInjector.IsSimulated(ex)) log("Cleanup/rollback complete.");
            throw;
        }
    }

    public static bool HasActiveManagedOverlays(string gameDir)
    {
        try
        {
            if (!IsGameDir(gameDir)) return false;
            return GetManagedOverlayDirs(gameDir).Count > 0;
        }
        catch { return false; }
    }

    public static bool HasActiveManagedBuildRegistry(string gameDir)
    {
        try
        {
            if (!IsGameDir(gameDir)) return false;
            var snapshot = TryGetActiveBuildSnapshot(gameDir, null);
            return snapshot != null && snapshot.IsValid;
        }
        catch { return false; }
    }

    public static bool HasAnyManagedBuildState(string gameDir)
    {
        try
        {
            if (!IsGameDir(gameDir)) return false;

            // Read-only workflow detection: do not auto-repair or migrate state
            // merely because the user clicked Easy Apply and may still cancel.
            foreach (var path in new[] { RegistryPath(gameDir), PreviousRegistryPath(gameDir), LegacyRegistryPath(gameDir), LegacyNestedRegistryPath(gameDir) })
            {
                var reg = ReadJsonDict(path);
                if (reg == null) continue;
                foreach (var mod in ObjMods(reg).Where(IsTextureMod))
                {
                    if (StringListObj(mod.GetValueOrDefault("overlay_dirs")).Count > 0) return true;
                    if (HeldOverlayList(mod).Count > 0) return true;
                }
            }

            if (ActiveTargetManifestPaths(gameDir).Any(File.Exists)) return true;

            foreach (var dir in Directory.EnumerateDirectories(gameDir, ManagedOverlayPrefix + "??"))
            {
                if (LooksLikeToolOwnedOverlayFolder(dir)) return true;
            }

            foreach (var holdRoot in RegistryHoldRoots(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(holdRoot)) continue;
                foreach (var dir in Directory.EnumerateDirectories(holdRoot, ManagedOverlayPrefix + "??", SearchOption.AllDirectories))
                {
                    if (LooksLikeToolOwnedOverlayFolder(dir)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static HashSet<string> GetManagedOverlayDirs(string gameDir, Action<string>? log = null)
    {
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reg = LoadRegistry(gameDir);
        foreach (var mod in ObjMods(reg).Where(m => IsTextureMod(m) && string.Equals(SObj(m, "status").DefaultIfEmpty("active"), "active", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var od in StringListObj(mod.GetValueOrDefault("overlay_dirs")))
            {
                if (!string.IsNullOrWhiteSpace(od)) set.Add(od.Trim());
            }
        }
        return set;
    }

    private static Dictionary<string, ExistingOverlayTarget> BuildManagedOverlayTargetIndex(string gameDir, Action<string>? log = null)
    {
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var reg = LoadRegistry(gameDir);
        var mods = ObjMods(reg).Where(m => IsTextureMod(m) && string.Equals(SObj(m, "status").DefaultIfEmpty("active"), "active", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new Dictionary<string, ExistingOverlayTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            string mp = SObj(mod, "manifest_copy");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) mp = SObj(mod, "original_manifest");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) continue;
            foreach (var (overlayDir, entries) in ReadManifestChunkEntries(mp))
            {
                foreach (var e in entries)
                {
                    string full = OverlayBuilder.Norm(string.IsNullOrEmpty(e.DirPath) ? e.EntryPath : $"{e.DirPath.Trim('/')}/{e.Filename}");
                    if (string.IsNullOrWhiteSpace(full)) continue;
                    result[full] = new ExistingOverlayTarget(full, e.EntryPath, overlayDir, mp, e.PazIndex, e.PazOffset, e.CompSize);
                }
            }
        }
        return result;
    }

    private static (List<OverlayEntry> entries, List<object> chunkInfos, List<string> overlayDirs) RebuildExistingManagedOverlays(
        string gameDir,
        List<MatchedFile> replacementMatches,
        Dictionary<string, ExistingOverlayTarget> targetIndex,
        Dictionary<string, byte[]> modifiedPamts,
        Dictionary<string, string> hotfixOverlayBackups,
        Action<string> log,
        Action<int, string>? progress,
        string? vanillaPathc,
        string performanceMemoryMode,
        int customPrepareWorkers,
        OverlayBuilder.OverlayBuildTimings overlayTimings,
        CancellationToken cancellationToken = default)
    {
        var entriesOut = new List<OverlayEntry>();
        var chunkInfos = new List<object>();
        var overlayDirs = new List<string>();
        var replacementsByFull = replacementMatches
            .GroupBy(m => OverlayBuilder.Norm(m.FullPath))
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var groups = replacementMatches
            .Select(m => (match: m, target: targetIndex[OverlayBuilder.Norm(m.FullPath)]))
            .GroupBy(x => (x.target.ManifestPath, x.target.OverlayDir))
            .OrderBy(g => g.Key.OverlayDir)
            .ToList();

        log($"Hotfix plan: changed existing targets: {replacementMatches.Count}; affected HD## folder(s): {groups.Count}.");

        int gi = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            gi++;
            string manifestPath = group.Key.ManifestPath;
            string od = group.Key.OverlayDir;
            overlayDirs.Add(od);
            var chunks = ReadManifestChunkEntries(manifestPath);
            if (!chunks.TryGetValue(od, out var oldEntries) || oldEntries.Count == 0)
                throw new InvalidOperationException($"Cannot hotfix overlay {od}; its manifest does not contain chunk entries.");

            LogHotfixOverlayPlan(od, oldEntries, group.Select(x => x.match).ToList(), log);

            if (TryRebuildExistingManagedPazParts(
                gameDir, od, manifestPath, oldEntries, group.Select(x => x.match).ToList(), replacementsByFull,
                modifiedPamts, hotfixOverlayBackups, entriesOut, chunkInfos, log, progress, vanillaPathc,
                performanceMemoryMode, customPrepareWorkers, overlayTimings, cancellationToken))
            {
                continue;
            }

            // Conservative fallback: if a changed texture no longer fits its original
            // PAZ part safely, rebuild the whole owning HD## overlay using the existing
            // validated path. This avoids risky cross-overlay reshuffling in the first
            // PAZ-part-level hotfix implementation.
            var deferred = new List<OverlayBuilder.DeferredOverlayInput>();
            foreach (var e in oldEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string full = FullPathFromOverlayEntry(e);
                if (replacementsByFull.TryGetValue(full, out var repl))
                {
                    deferred.Add(OverlayBuilder.DeferredOverlayInput.FromSource(repl.SourcePath, repl.Metadata(), gameDir));
                    continue;
                }

                var existingEntry = CloneOverlayEntry(e);
                deferred.Add(new OverlayBuilder.DeferredOverlayInput(existingEntry.EntryPath, existingEntry.CompSize, (_, _) =>
                {
                    byte[] oldPayload = ReadPackedOverlayPayload(gameDir, od, existingEntry);
                    var cloned = CloneOverlayEntry(existingEntry);
                    return new PreparedOverlayPayload(cloned, oldPayload);
                }));
            }

            string outDir = Path.Combine(gameDir, od);
            Directory.CreateDirectory(outDir);
            EnsureHotfixCancelBackup(gameDir, od, hotfixOverlayBackups, log);
            EnsureHotfixManifestCancelBackup(manifestPath, hotfixOverlayBackups[od], log);
            log($"Update Existing Build: PAZ-part hotfix fallback rebuilding full overlay {od} ({group.Count()} replacement(s), {deferred.Count} total textures). This happens when an affected PAZ part cannot be safely rebuilt in place.");
            var (pamtBytes, rebuiltEntries, pazSize) = OverlayBuilder.BuildOverlayFromDeferredInputsToFile(deferred, Path.Combine(outDir, "0.paz"), gameDir, (i, total, name) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int basePct = 15 + (int)Math.Round(((gi - 1) / Math.Max(1.0, groups.Count)) * 20.0);
                int nextPct = 15 + (int)Math.Round((gi / Math.Max(1.0, groups.Count)) * 20.0);
                int pct = total > 0 ? basePct + (int)Math.Round((nextPct - basePct) * (i / Math.Max(1.0, total))) : basePct;
                string clean = CleanProgressName(name);
                bool isStage = name.StartsWith("[stage]", StringComparison.OrdinalIgnoreCase);
                bool isHash = name.StartsWith("[hash]", StringComparison.OrdinalIgnoreCase);
                bool isPrepare = name.StartsWith("[prepare]", StringComparison.OrdinalIgnoreCase);
                bool isWrite = name.StartsWith("[write]", StringComparison.OrdinalIgnoreCase);
                bool isOversizedGuard = clean.StartsWith("Oversized texture packed into dedicated PAZ part", StringComparison.OrdinalIgnoreCase);
                string progressText = isStage
                    ? clean
                    : isHash
                        ? $"{od} HASH"
                        : isWrite
                            ? $"{od} WRITE {Math.Min(i, Math.Max(1, total))}/{Math.Max(1, total)}"
                            : isPrepare
                                ? $"{od} PREP {Math.Min(i, Math.Max(1, total))}/{Math.Max(1, total)}"
                                : $"{od} {Math.Min(i, Math.Max(1, total))}/{Math.Max(1, total)}";
                progress?.Invoke(Math.Min(45, pct), progressText);
                if (isStage) log($"  existing {od}: {clean}");
                else if (isHash)
                {
                    if (i == total || i % 512 == 0) log($"  existing {od}: {clean}");
                }
                else if (isOversizedGuard) log($"  existing {od}: {clean}");
                else if (i >= total || i % 50 == 0) log($"  existing {od}: {(isPrepare ? "prep" : isWrite ? "write" : "step")} {Math.Min(i, total)}/{total} {clean}");
            }, vanillaPathc, cancellationToken, performanceMemoryMode, customPrepareWorkers, overlayTimings);
            SetOverlayDir(rebuiltEntries, od);
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(Path.Combine(outDir, "0.pamt"), pamtBytes);
            DebugFailureInjector.Check(DebugFailureInjector.HotfixAfterPamtBeforePathc, log);
            modifiedPamts[od] = pamtBytes;
            entriesOut.AddRange(rebuiltEntries.Where(e => replacementsByFull.ContainsKey(FullPathFromOverlayEntry(e))));
            chunkInfos.Add(new { overlay_dir = od, hotfix_existing_overlay = true, hotfix_granularity = "whole-overlay-fallback", replacement_count = group.Count(), texture_count = deferred.Count, paz_size = pazSize, pamt_size = pamtBytes.Length, entries = rebuiltEntries });
            UpdateManifestAfterHotfix(manifestPath, od, group.Select(x => x.match).ToList(), rebuiltEntries, log);
        }
        return (entriesOut, chunkInfos, overlayDirs);
    }

    private static void LogHotfixOverlayPlan(string overlayDir, List<OverlayEntry> oldEntries, List<MatchedFile> replacements, Action<string> log)
    {
        try
        {
            var oldByFull = oldEntries
                .GroupBy(FullPathFromOverlayEntry, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            var affected = new SortedDictionary<uint, int>();
            int missing = 0;
            foreach (var repl in replacements)
            {
                string full = OverlayBuilder.Norm(repl.FullPath);
                if (!oldByFull.TryGetValue(full, out var existing))
                {
                    missing++;
                    continue;
                }
                affected.TryGetValue(existing.PazIndex, out int count);
                affected[existing.PazIndex] = count + 1;
            }

            int totalParts = oldEntries.Select(e => e.PazIndex).Distinct().Count();
            int preserved = Math.Max(0, totalParts - affected.Count);
            string parts = affected.Count == 0
                ? "none"
                : string.Join(", ", affected.Select(kv => kv.Value == 1 ? kv.Key.ToString() : $"{kv.Key}({kv.Value} changes)"));
            log($"Hotfix plan: {overlayDir} changed target(s): {replacements.Count}; affected PAZ part(s): {parts}; unaffected PAZ parts preserved if direct hotfix succeeds: {preserved}.");
            if (missing > 0) log($"Hotfix plan: {overlayDir} has {missing} changed target(s) not found in the installed manifest; direct PAZ-part hotfix will fall back safely.");
        }
        catch (Exception ex)
        {
            log($"WARN: could not summarize hotfix plan for {overlayDir}: {ex.Message}");
        }
    }

    private static bool TryRebuildExistingManagedPazParts(
        string gameDir,
        string overlayDir,
        string manifestPath,
        List<OverlayEntry> oldEntries,
        List<MatchedFile> replacements,
        Dictionary<string, MatchedFile> replacementsByFull,
        Dictionary<string, byte[]> modifiedPamts,
        Dictionary<string, string> hotfixOverlayBackups,
        List<OverlayEntry> entriesOut,
        List<object> chunkInfos,
        Action<string> log,
        Action<int, string>? progress,
        string? vanillaPathc,
        string performanceMemoryMode,
        int customPrepareWorkers,
        OverlayBuilder.OverlayBuildTimings overlayTimings,
        CancellationToken cancellationToken)
    {
        string outDir = Path.Combine(gameDir, overlayDir);
        string pamtPath = Path.Combine(outDir, "0.pamt");
        if (!File.Exists(pamtPath))
        {
            log($"Update Existing Build: PAZ-part hotfix unavailable for {overlayDir}; 0.pamt is missing. Falling back to whole-overlay rebuild.");
            return false;
        }

        var oldByFull = oldEntries
            .GroupBy(FullPathFromOverlayEntry, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var affectedParts = new SortedSet<uint>();
        foreach (var repl in replacements)
        {
            string full = OverlayBuilder.Norm(repl.FullPath);
            if (!oldByFull.TryGetValue(full, out var existing))
            {
                log($"Update Existing Build: PAZ-part hotfix unavailable for {overlayDir}; changed target was not found in the installed overlay manifest: {repl.FullPath}. Falling back to whole-overlay rebuild.");
                return false;
            }
            affectedParts.Add(existing.PazIndex);
        }

        if (affectedParts.Count == 0) return true;

        List<(uint crc, uint length)> pazHeaders;
        try { pazHeaders = ReadPamtPazHeaders(pamtPath); }
        catch (Exception ex)
        {
            log($"Update Existing Build: PAZ-part hotfix unavailable for {overlayDir}; could not read PAMT PAZ headers: {ex.Message}. Falling back to whole-overlay rebuild.");
            return false;
        }

        if (pazHeaders.Count == 0 || affectedParts.Any(p => p >= pazHeaders.Count))
        {
            log($"Update Existing Build: PAZ-part hotfix unavailable for {overlayDir}; affected PAZ index is outside the installed PAMT header range. Falling back to whole-overlay rebuild.");
            return false;
        }

        var partEntriesByIndex = new Dictionary<uint, List<OverlayEntry>>();
        foreach (uint partIndex in affectedParts)
        {
            string pazPath = Path.Combine(outDir, $"{partIndex}.paz");
            if (!File.Exists(pazPath))
            {
                log($"Update Existing Build: PAZ-part hotfix unavailable for {overlayDir}; {partIndex}.paz is missing. Falling back to whole-overlay rebuild.");
                return false;
            }

            var partEntries = oldEntries
                .Where(e => e.PazIndex == partIndex)
                .OrderBy(e => e.PazOffset)
                .ToList();
            if (partEntries.Count == 0)
            {
                log($"Update Existing Build: PAZ-part hotfix unavailable for {overlayDir}; manifest has no entries for PAZ part {partIndex}. Falling back to whole-overlay rebuild.");
                return false;
            }

            long estimatedPartBytes = 0;
            foreach (var e in partEntries)
            {
                string full = FullPathFromOverlayEntry(e);
                if (replacementsByFull.TryGetValue(full, out var repl)) estimatedPartBytes = checked(estimatedPartBytes + Align16(new FileInfo(repl.SourcePath).Length));
                else estimatedPartBytes = checked(estimatedPartBytes + Align16(e.CompSize));
            }
            if (partEntries.Count > 1 && estimatedPartBytes > 943718400L + (16L * 1024L * 1024L))
            {
                log($"Update Existing Build: PAZ-part hotfix fallback for {overlayDir} part {partIndex}; changed payload may no longer fit the original ~900 MiB PAZ part safely ({FormatBytesForLog(estimatedPartBytes)} estimated). Falling back to whole-overlay rebuild.");
                return false;
            }

            partEntriesByIndex[partIndex] = partEntries;
        }

        int totalPazParts = pazHeaders.Count;
        int preservedPazParts = Math.Max(0, totalPazParts - affectedParts.Count);
        log($"Update Existing Build: PAZ-part hotfix for {overlayDir}; {replacements.Count} changed target(s) across {affectedParts.Count} PAZ part(s): {string.Join(", ", affectedParts.Select(p => p.ToString()))}.");
        log($"Hotfix plan: {overlayDir} unaffected PAZ parts preserved: {preservedPazParts} of {totalPazParts}.");
        EnsureHotfixPartCancelBackup(gameDir, overlayDir, affectedParts, hotfixOverlayBackups, log);
        EnsureHotfixManifestCancelBackup(manifestPath, hotfixOverlayBackups[overlayDir], log);
        DebugFailureInjector.Check(DebugFailureInjector.HotfixAfterAffectedPazPartBackup, log);

        var updatedEntriesByFull = new Dictionary<string, OverlayEntry>(StringComparer.OrdinalIgnoreCase);
        var changedEntriesForPathc = new List<OverlayEntry>();
        var rebuiltPartDetails = new List<object>();
        bool firstAffectedPartReplacementCompleted = false;

        foreach (uint partIndex in affectedParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string pazPath = Path.Combine(outDir, $"{partIndex}.paz");
            var partEntries = partEntriesByIndex[partIndex];

            long estimatedPartBytes = 0;
            var deferred = new List<OverlayBuilder.DeferredOverlayInput>(partEntries.Count);
            foreach (var e in partEntries)
            {
                string full = FullPathFromOverlayEntry(e);
                if (replacementsByFull.TryGetValue(full, out var repl))
                {
                    estimatedPartBytes = checked(estimatedPartBytes + Align16(new FileInfo(repl.SourcePath).Length));
                    deferred.Add(OverlayBuilder.DeferredOverlayInput.FromSource(repl.SourcePath, repl.Metadata(), gameDir));
                    continue;
                }

                var existingEntry = CloneOverlayEntry(e);
                estimatedPartBytes = checked(estimatedPartBytes + Align16(existingEntry.CompSize));
                deferred.Add(new OverlayBuilder.DeferredOverlayInput(existingEntry.EntryPath, existingEntry.CompSize, (_, _) =>
                {
                    byte[] oldPayload = ReadPackedOverlayPayload(gameDir, overlayDir, existingEntry);
                    var cloned = CloneOverlayEntry(existingEntry);
                    return new PreparedOverlayPayload(cloned, oldPayload);
                }));
            }

            if (partEntries.Count > 1 && estimatedPartBytes > 943718400L + (16L * 1024L * 1024L))
                throw new InvalidOperationException($"PAZ-part hotfix preflight failed for {overlayDir} part {partIndex} after modification had started; use Remove Current Build / Easy Apply for this change.");

            log($"Update Existing Build: rebuilding {overlayDir}/{partIndex}.paz only ({partEntries.Count} texture(s), {partEntries.Count(e => replacementsByFull.ContainsKey(FullPathFromOverlayEntry(e)))} replacement(s)).");
            var rebuilt = OverlayBuilder.BuildSinglePazPartFromDeferredInputsToFile(deferred, pazPath, (int)partIndex, gameDir, (i, total, name) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string clean = CleanProgressName(name);
                bool isStage = name.StartsWith("[stage]", StringComparison.OrdinalIgnoreCase);
                bool isHash = name.StartsWith("[hash]", StringComparison.OrdinalIgnoreCase);
                bool isPrepare = name.StartsWith("[prepare]", StringComparison.OrdinalIgnoreCase);
                bool isWrite = name.StartsWith("[write]", StringComparison.OrdinalIgnoreCase);
                if (isStage) log($"  hotfix {overlayDir}/{partIndex}.paz: {clean}");
                else if (isHash)
                {
                    if (i == total || i % 512 == 0) log($"  hotfix {overlayDir}/{partIndex}.paz: {clean}");
                }
                else if (i >= total || i % 50 == 0)
                {
                    log($"  hotfix {overlayDir}/{partIndex}.paz: {(isPrepare ? "prep" : isWrite ? "write" : "step")} {Math.Min(i, total)}/{total} {clean}");
                }
                progress?.Invoke(25, $"UPDATE {overlayDir} PAZ {partIndex}");
            }, vanillaPathc, cancellationToken, performanceMemoryMode, customPrepareWorkers, overlayTimings);

            pazHeaders[(int)partIndex] = (rebuilt.crc, rebuilt.length);
            SetOverlayDir(rebuilt.entries, overlayDir);
            foreach (var entry in rebuilt.entries)
            {
                string full = FullPathFromOverlayEntry(entry);
                updatedEntriesByFull[full] = entry;
                if (replacementsByFull.ContainsKey(full)) changedEntriesForPathc.Add(entry);
            }
            rebuiltPartDetails.Add(new { paz_index = partIndex, texture_count = partEntries.Count, replacement_count = partEntries.Count(e => replacementsByFull.ContainsKey(FullPathFromOverlayEntry(e))), paz_size = rebuilt.length });
            if (!firstAffectedPartReplacementCompleted)
            {
                firstAffectedPartReplacementCompleted = true;
                DebugFailureInjector.Check(DebugFailureInjector.HotfixAfterFirstAffectedPazPartReplacement, log);
            }
        }

        var allEntries = oldEntries.Select(e => updatedEntriesByFull.TryGetValue(FullPathFromOverlayEntry(e), out var updated) ? CloneOverlayEntry(updated) : CloneOverlayEntry(e)).ToList();
        SetOverlayDir(allEntries, overlayDir);
        SetOverlayDir(changedEntriesForPathc, overlayDir);
        byte[] pamtBytes = OverlayBuilder.BuildPamtFromExistingEntries(allEntries, pazHeaders);
        File.WriteAllBytes(pamtPath, pamtBytes);
        DebugFailureInjector.Check(DebugFailureInjector.HotfixAfterPamtBeforePathc, log);
        modifiedPamts[overlayDir] = pamtBytes;
        entriesOut.AddRange(changedEntriesForPathc);
        chunkInfos.Add(new { overlay_dir = overlayDir, hotfix_existing_overlay = true, hotfix_granularity = "paz-part", replacement_count = replacements.Count, affected_paz_parts = affectedParts.ToList(), rebuilt_parts = rebuiltPartDetails, texture_count = allEntries.Count, pamt_size = pamtBytes.Length, entries = allEntries });
        UpdateManifestAfterHotfix(manifestPath, overlayDir, replacements, allEntries, log);
        log($"Update Existing Build hotfix complete for {overlayDir}: rebuilt {affectedParts.Count} PAZ part(s), preserved {preservedPazParts} PAZ part(s), updated {replacements.Count} target(s).");
        OverlayBuilder.ReleaseCompletedBuildMemory(trimWorkingSet: true);
        return true;
    }

    private static string FullPathFromOverlayEntry(OverlayEntry e)
        => OverlayBuilder.Norm(string.IsNullOrEmpty(e.DirPath) ? e.EntryPath : $"{e.DirPath.Trim('/')}/{e.Filename}");

    private static long Align16(long length)
    {
        long rem = length % 16L;
        return rem == 0 ? length : checked(length + 16L - rem);
    }

    private static string FormatBytesForLog(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:F2} {units[unit]}";
    }

    private static List<(uint crc, uint length)> ReadPamtPazHeaders(string pamtPath)
    {
        byte[] data = File.ReadAllBytes(pamtPath);
        if (data.Length < 16) throw new InvalidDataException("PAMT is too small to contain PAZ headers.");
        int off = 4;
        uint pazCount = BinaryUtil.U32(data, off); off += 4;
        if (pazCount > 4096) throw new InvalidDataException($"PAMT PAZ count is implausibly large: {pazCount}.");
        off += 8;
        var result = new List<(uint crc, uint length)>((int)pazCount);
        for (int i = 0; i < pazCount; i++)
        {
            if (i > 0) off += 4;
            if (off + 8 > data.Length) throw new InvalidDataException("PAMT PAZ header section is truncated.");
            uint crc = BinaryUtil.U32(data, off); off += 4;
            uint length = BinaryUtil.U32(data, off); off += 4;
            result.Add((crc, length));
        }
        return result;
    }

    private static OverlayEntry CloneOverlayEntry(OverlayEntry e) => new()
    {
        DirPath = e.DirPath,
        Filename = e.Filename,
        PazIndex = e.PazIndex,
        PazOffset = e.PazOffset,
        CompSize = e.CompSize,
        DecompSize = e.DecompSize,
        Flags = e.Flags,
        DdsMValues = e.DdsMValues == null ? null : e.DdsMValues.ToArray(),
        DdsLast4 = e.DdsLast4,
        DdsPathcHeader = e.DdsPathcHeader == null ? null : e.DdsPathcHeader.ToArray(),
        OverlayDir = e.OverlayDir,
        EntryPath = e.EntryPath
    };

    private static void SetOverlayDir(IEnumerable<OverlayEntry> entries, string overlayDir)
    {
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.OverlayDir)) e.OverlayDir = overlayDir;
        }
    }

    private static byte[] ReadPackedOverlayPayload(string gameDir, string overlayDir, OverlayEntry e)
    {
        string paz = Path.Combine(gameDir, overlayDir, $"{e.PazIndex}.paz");
        if (!File.Exists(paz)) throw new FileNotFoundException($"Cannot hotfix overlay {overlayDir}; existing {e.PazIndex}.paz is missing: {paz}");
        using var fs = File.OpenRead(paz);
        if ((long)e.PazOffset + e.CompSize > fs.Length) throw new InvalidDataException($"Cannot hotfix overlay {overlayDir}; payload range is outside the expected PAZ part for {e.EntryPath}.");
        fs.Position = e.PazOffset;
        byte[] payload = new byte[checked((int)e.CompSize)];
        int read = fs.Read(payload, 0, payload.Length);
        if (read != payload.Length) throw new EndOfStreamException($"Cannot hotfix overlay {overlayDir}; could not read existing payload for {e.EntryPath}.");
        return payload;
    }

    private static List<MatchedFile> ReadManifestMatches(string manifestPath)
    {
        var result = new List<MatchedFile>();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("matches", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var el in arr.EnumerateArray())
        {
            string source = JStr(el, "SourcePath", "source_path");
            string rel = JStr(el, "RelPath", "rel_path");
            string pamt = JStr(el, "PamtDir", "pamt_dir");
            string entry = JStr(el, "EntryPath", "entry_path");
            string full = JStr(el, "FullPath", "full_path");
            string fn = JStr(el, "Filename", "filename");
            long size = JLong(el, "Size", "size");
            int comp = (int)JLong(el, "CompressionType", "compression_type");
            bool enc = JBool(el, "Encrypted", "encrypted");
            string crypto = JStr(el, "CryptoFilename", "crypto_filename");
            string method = JStr(el, "MatchMethod", "match_method");
            if (string.IsNullOrWhiteSpace(fn) && !string.IsNullOrWhiteSpace(entry)) fn = entry.Contains('/') ? entry[(entry.LastIndexOf('/') + 1)..] : entry;
            result.Add(new MatchedFile { SourcePath = source, RelPath = rel, Size = size, PamtDir = pamt, EntryPath = entry, FullPath = full, Filename = fn, CompressionType = comp, Encrypted = enc, CryptoFilename = crypto, MatchMethod = method });
        }
        return result;
    }

    private static Dictionary<string, List<OverlayEntry>> ReadManifestChunkEntries(string manifestPath)
    {
        var result = new Dictionary<string, List<OverlayEntry>>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != JsonValueKind.Array) return result;
        foreach (var ch in chunks.EnumerateArray())
        {
            string od = JStr(ch, "overlay_dir", "OverlayDir");
            if (string.IsNullOrWhiteSpace(od)) continue;
            var list = new List<OverlayEntry>();
            if (ch.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in entries.EnumerateArray()) list.Add(ReadOverlayEntry(el, od));
            }
            SetOverlayDir(list, od);
            result[od] = list;
        }
        return result;
    }

    private static List<OverlayEntry> ReadManifestOverlayEntries(string manifestPath)
    {
        var result = new List<OverlayEntry>();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));

        // Prefer per-chunk entries when available because they preserve the
        // owning HD## folder. That lets Release Hold + Reapply and Relink
        // recover DDS PATHC header data directly from installed/held PAZ files
        // when old manifests do not yet contain cached source-independent data.
        if (doc.RootElement.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
        {
            foreach (var ch in chunks.EnumerateArray())
            {
                string od = JStr(ch, "overlay_dir", "OverlayDir");
                if (string.IsNullOrWhiteSpace(od)) continue;
                if (!ch.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in entries.EnumerateArray()) result.Add(ReadOverlayEntry(el, od));
            }
            if (result.Count > 0) return result;
        }

        if (!doc.RootElement.TryGetProperty("overlay_entries", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var el in arr.EnumerateArray()) result.Add(ReadOverlayEntry(el));
        return result;
    }

    private static OverlayEntry ReadOverlayEntry(JsonElement el, string overlayDir = "")
    {
        return new OverlayEntry
        {
            DirPath = JStr(el, "DirPath", "dir_path"),
            Filename = JStr(el, "Filename", "filename"),
            EntryPath = JStr(el, "EntryPath", "entry_path"),
            PazIndex = (uint)JLong(el, "PazIndex", "paz_index"),
            PazOffset = (uint)JLong(el, "PazOffset", "paz_offset"),
            CompSize = (uint)JLong(el, "CompSize", "comp_size"),
            DecompSize = (uint)JLong(el, "DecompSize", "decomp_size"),
            Flags = (ushort)JLong(el, "Flags", "flags"),
            DdsLast4 = (uint)JLong(el, "DdsLast4", "dds_last4"),
            DdsMValues = JUIntArray(el, "DdsMValues", "dds_m_values"),
            DdsPathcHeader = JByteArray(el, "DdsPathcHeader", "dds_pathc_header"),
            OverlayDir = JStr(el, "OverlayDir", "overlay_dir").DefaultIfEmpty(overlayDir)
        };
    }

    private static string RelinkMatchTargetKey(MatchedFile match)
    {
        string target = !string.IsNullOrWhiteSpace(match.FullPath) ? match.FullPath : match.EntryPath;
        if (string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(match.PamtDir) && !string.IsNullOrWhiteSpace(match.EntryPath))
            target = $"{match.PamtDir}/{match.EntryPath}";
        return OverlayBuilder.Norm(target);
    }

    private static string RelinkOverlayTargetKey(OverlayEntry entry)
    {
        string target = !string.IsNullOrWhiteSpace(entry.DirPath) && !string.IsNullOrWhiteSpace(entry.Filename)
            ? $"{entry.DirPath.Trim('/')}/{entry.Filename}"
            : entry.EntryPath;
        return OverlayBuilder.Norm(target);
    }

    private static string RelinkOverlayOwnerKey(OverlayEntry entry)
        => OverlayBuilder.Norm(entry.OverlayDir);

    private static void UpdateManifestAfterHotfix(string manifestPath, string overlayDir, List<MatchedFile> replacements, List<OverlayEntry> rebuiltEntries, Action<string>? log)
    {
        try
        {
            var manifest = ReadJsonDict(manifestPath);
            if (manifest == null) return;
            SetOverlayDir(rebuiltEntries, overlayDir);
            var repl = replacements.GroupBy(m => OverlayBuilder.Norm(m.FullPath)).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            if (manifest.TryGetValue("matches", out var rawMatches) && rawMatches is List<object?> list)
            {
                foreach (var item in list.OfType<Dictionary<string, object?>>())
                {
                    string full = SObj(item, "FullPath").DefaultIfEmpty(SObj(item, "full_path"));
                    if (!repl.TryGetValue(OverlayBuilder.Norm(full), out var m)) continue;
                    item["SourcePath"] = m.SourcePath;
                    item["RelPath"] = m.RelPath;
                    item["Size"] = m.Size;
                    item["CompressionType"] = m.CompressionType;
                    item["Encrypted"] = m.Encrypted;
                    item["CryptoFilename"] = m.CryptoFilename;
                    item["MatchMethod"] = m.MatchMethod;
                }
            }

            if (manifest.TryGetValue("chunks", out var rawChunks) && rawChunks is List<object?> chunks)
            {
                foreach (var item in chunks.OfType<Dictionary<string, object?>>())
                {
                    string od = SObj(item, "overlay_dir").DefaultIfEmpty(SObj(item, "OverlayDir"));
                    if (!string.Equals(od, overlayDir, StringComparison.OrdinalIgnoreCase)) continue;
                    item["entries"] = rebuiltEntries;
                    item["texture_count"] = rebuiltEntries.Count;
                    item["hotfix_existing_overlay"] = true;
                    item["last_hotfix_at"] = DateTime.Now.ToString("s");
                }
                // Prefer authoritative chunk entries from the JSON file when possible.
                var rebuiltChunks = new List<OverlayEntry>();
                foreach (var kv in ReadManifestChunkEntriesFromObject(chunks)) rebuiltChunks.AddRange(kv.Value);
                if (rebuiltChunks.Count > 0) manifest["overlay_entries"] = rebuiltChunks;
            }

            manifest["last_hotfix_at"] = DateTime.Now.ToString("s");
            manifest["last_hotfix_replaced_count"] = replacements.Count;
            WriteJson(manifestPath, manifest);
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARN: could not update hotfixed manifest source paths: {ex.Message}");
        }
    }

    private static Dictionary<string, List<OverlayEntry>> ReadManifestChunkEntriesFromObject(List<object?> chunks)
    {
        var result = new Dictionary<string, List<OverlayEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in chunks.OfType<Dictionary<string, object?>>())
        {
            string od = SObj(item, "overlay_dir").DefaultIfEmpty(SObj(item, "OverlayDir"));
            if (string.IsNullOrWhiteSpace(od)) continue;
            var list = new List<OverlayEntry>();
            if (item.TryGetValue("entries", out var raw))
            {
                if (raw is List<object?> entries)
                {
                    foreach (var e in entries)
                    {
                        if (e is OverlayEntry oe) list.Add(CloneOverlayEntry(oe));
                        else if (e is Dictionary<string, object?> d) list.Add(ReadOverlayEntry(d, od));
                    }
                }
                else if (raw is IEnumerable<OverlayEntry> overlayEntries)
                {
                    foreach (var e in overlayEntries) list.Add(CloneOverlayEntry(e));
                }
            }
            SetOverlayDir(list, od);
            result[od] = list;
        }
        return result;
    }

    private static OverlayEntry ReadOverlayEntry(Dictionary<string, object?> d, string overlayDir = "")
    {
        uint U(string key1, string key2 = "")
        {
            string v = SObj(d, key1).DefaultIfEmpty(string.IsNullOrEmpty(key2) ? "" : SObj(d, key2));
            return uint.TryParse(v, out var u) ? u : 0u;
        }
        ushort US(string key1, string key2 = "")
        {
            string v = SObj(d, key1).DefaultIfEmpty(string.IsNullOrEmpty(key2) ? "" : SObj(d, key2));
            return ushort.TryParse(v, out var u) ? u : (ushort)0;
        }
        uint[]? arr = null;
        object? raw = null;
        if (d.TryGetValue("DdsMValues", out raw) || d.TryGetValue("dds_m_values", out raw))
        {
            if (raw is List<object?> lo)
            {
                var vals = lo.Select(x => uint.TryParse(Convert.ToString(x), out var u) ? u : 0u).ToArray();
                if (vals.Length > 0) arr = vals;
            }
            else if (raw is uint[] ua) arr = ua.ToArray();
        }
        byte[]? hdr = null;
        if (d.TryGetValue("DdsPathcHeader", out raw) || d.TryGetValue("dds_pathc_header", out raw))
        {
            if (raw is string b64)
            {
                try { hdr = Convert.FromBase64String(b64); } catch { hdr = null; }
            }
            else if (raw is List<object?> lo)
            {
                try { hdr = lo.Select(x => (byte)(byte.TryParse(Convert.ToString(x), out var b) ? b : 0)).ToArray(); } catch { hdr = null; }
            }
            else if (raw is byte[] ba) hdr = ba.ToArray();
        }
        return new OverlayEntry
        {
            DirPath = SObj(d, "DirPath").DefaultIfEmpty(SObj(d, "dir_path")),
            Filename = SObj(d, "Filename").DefaultIfEmpty(SObj(d, "filename")),
            PazIndex = U("PazIndex", "paz_index"),
            PazOffset = U("PazOffset", "paz_offset"),
            CompSize = U("CompSize", "comp_size"),
            DecompSize = U("DecompSize", "decomp_size"),
            Flags = US("Flags", "flags"),
            DdsLast4 = U("DdsLast4", "dds_last4"),
            DdsMValues = arr,
            DdsPathcHeader = hdr,
            OverlayDir = SObj(d, "OverlayDir").DefaultIfEmpty(SObj(d, "overlay_dir")).DefaultIfEmpty(overlayDir),
            EntryPath = SObj(d, "EntryPath").DefaultIfEmpty(SObj(d, "entry_path"))
        };
    }

    private static string JStr(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (TryGetPropertyIgnoreCase(el, n, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        return "";
    }

    private static long JLong(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (TryGetPropertyIgnoreCase(el, n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
                if (long.TryParse(v.ToString(), out l)) return l;
            }
        return 0;
    }

    private static bool JBool(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (TryGetPropertyIgnoreCase(el, n, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
                if (bool.TryParse(v.ToString(), out var b)) return b;
            }
        return false;
    }

    private static uint[]? JUIntArray(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (TryGetPropertyIgnoreCase(el, n, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                var vals = v.EnumerateArray().Select(x => (uint)(x.TryGetUInt32(out var u) ? u : 0u)).ToArray();
                return vals.Length == 0 ? null : vals;
            }
        return null;
    }

    private static byte[]? JByteArray(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!TryGetPropertyIgnoreCase(el, n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                try { return Convert.FromBase64String(v.GetString() ?? ""); } catch { return null; }
            }
            if (v.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    var vals = v.EnumerateArray().Select(x => (byte)(x.TryGetUInt32(out var u) && u <= byte.MaxValue ? (byte)u : (byte)0)).ToArray();
                    return vals.Length == 0 ? null : vals;
                }
                catch { return null; }
            }
        }
        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(name, out value)) return true;
            foreach (var p in el.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        }
        value = default;
        return false;
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Length != y.Length) return false;
            return x.AsSpan().SequenceEqual(y);
        }
        public int GetHashCode(byte[] obj)
        {
            unchecked
            {
                int hash = 216613626;
                for (int i = 0; i < obj.Length; i++) hash = (hash ^ obj[i]) * 16777619;
                return hash;
            }
        }
    }

    private sealed class CachedDdsHeader
    {
        public long Length { get; init; }
        public long LastWriteUtcTicks { get; init; }
        public byte[] Header { get; init; } = Array.Empty<byte>();
    }

    private static readonly ConcurrentDictionary<string, CachedDdsHeader> s_ddsHeaderCache = new(StringComparer.OrdinalIgnoreCase);

    private static byte[] ReadDdsHeaderOnly(string sourcePath, out bool fromCache)
    {
        const int maxHeader = 148;
        fromCache = false;
        string fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (info.Exists && s_ddsHeaderCache.TryGetValue(fullPath, out var cached) &&
            cached.Length == info.Length && cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
        {
            fromCache = true;
            return cached.Header;
        }

        byte[] header = new byte[maxHeader];
        int read = 0;
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
        while (read < maxHeader)
        {
            int n = fs.Read(header, read, maxHeader - read);
            if (n <= 0) break;
            read += n;
        }
        if (read != maxHeader) Array.Resize(ref header, read);

        if (info.Exists)
        {
            // Header cache is intentionally tiny per texture (max 148 bytes).
            // Clear it if a very large session would otherwise grow without bound.
            if (s_ddsHeaderCache.Count > 100_000) s_ddsHeaderCache.Clear();
            s_ddsHeaderCache[fullPath] = new CachedDdsHeader
            {
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Header = header
            };
        }
        return header;
    }

    private static bool TryBuildDdsPathcRecordFromHeader(byte[]? header, int recordSize, out byte[] record)
    {
        record = Array.Empty<byte>();
        if (header == null || header.Length < 128 || recordSize <= 0) return false;
        if (header[0] != (byte)'D' || header[1] != (byte)'D' || header[2] != (byte)'S' || header[3] != (byte)' ') return false;
        string fourcc = header.Length >= 88 ? Encoding.ASCII.GetString(header, 84, 4) : "";
        int headSize = fourcc == "DX10" && header.Length >= 148 ? 148 : 128;
        record = new byte[recordSize];
        Buffer.BlockCopy(header, 0, record, 0, Math.Min(Math.Min(header.Length, headSize), recordSize));
        return true;
    }

    private static bool TryReadDdsPathcRecordFromOverlayPayload(string gameDir, OverlayEntry oe, int recordSize, out byte[] record, out string detail)
    {
        record = Array.Empty<byte>();
        detail = string.Empty;
        if (oe == null) { detail = "missing overlay entry"; return false; }
        string overlayDir = oe.OverlayDir;
        if (string.IsNullOrWhiteSpace(overlayDir)) { detail = "manifest entry has no overlay folder owner"; return false; }
        try
        {
            byte[] payload = ReadPackedOverlayPayload(gameDir, overlayDir, oe);
            if ((oe.Flags & 0xF0) == 0x30)
            {
                string keyName = string.IsNullOrWhiteSpace(oe.Filename) ? (oe.EntryPath.Contains('/') ? oe.EntryPath[(oe.EntryPath.LastIndexOf('/') + 1)..] : oe.EntryPath) : oe.Filename;
                payload = ArchiveCrypto.EncryptDecrypt(payload, keyName);
            }
            if (TryBuildDdsPathcRecordFromHeader(payload, recordSize, out record))
            {
                detail = $"overlay payload {overlayDir}/{oe.PazIndex}.paz";
                return true;
            }
            detail = $"overlay payload {overlayDir}/{oe.PazIndex}.paz did not contain a readable DDS header";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static Dictionary<uint, byte[]> BuildReplayDdsRecordLookup(IEnumerable<PathcFile>? pathcReplaySources, int recordSize)
    {
        var result = new Dictionary<uint, byte[]>();
        if (pathcReplaySources == null || recordSize <= 0) return result;
        foreach (var src in pathcReplaySources)
        {
            if (src == null || src.KeyHashes.Count != src.MapEntries.Count) continue;
            for (int i = 0; i < src.KeyHashes.Count; i++)
            {
                var me = src.MapEntries[i];
                if ((me.Selector & 0xFFFF0000u) != 0xFFFF0000u) continue;
                int ddsIndex = (int)(me.Selector & 0xFFFFu);
                if (ddsIndex < 0 || ddsIndex >= src.DdsRecords.Count) continue;
                byte[] rec = src.DdsRecords[ddsIndex];
                if (rec == null || rec.Length != recordSize) continue;
                if (!result.ContainsKey(src.KeyHashes[i])) result[src.KeyHashes[i]] = rec;
            }
        }
        return result;
    }

    private static (byte[]? bytes, PathcUpdateResult summary) UpdatePathcForMatches(
        string gameDir,
        List<MatchedFile> matches,
        List<OverlayEntry> overlayEntries,
        Action<string> log,
        Action<int, string>? progress = null,
        int progressStart = 0,
        int progressEnd = 100,
        string progressLabel = "PATHC",
        List<PathcFile>? replayPathcSources = null)
    {
        progressStart = Math.Max(0, Math.Min(100, progressStart));
        progressEnd = Math.Max(progressStart, Math.Min(100, progressEnd));
        string pathcPath = Path.Combine(gameDir, "meta", "0.pathc");
        var emptySummary = new PathcUpdateResult(0, 0, 0, 0, 0, 0, 0, 0, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        if (!File.Exists(pathcPath)) { log("WARN: meta/0.pathc does not exist. DDS PATHC registration skipped."); return (null, emptySummary); }
        var ddsMatches = matches.Where(m => m.EntryPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ddsMatches.Count == 0) return (null, emptySummary);
        progress?.Invoke(progressStart, $"{progressLabel}: START 0/{ddsMatches.Count}");
        var packedByFull = overlayEntries
            .Where(e => !string.IsNullOrEmpty(e.Filename))
            .GroupBy(e => OverlayBuilder.Norm(string.IsNullOrEmpty(e.DirPath) ? e.EntryPath : $"{e.DirPath.Trim('/')}/{e.Filename}"))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());
        var packedByEntry = overlayEntries
            .Where(e => !string.IsNullOrEmpty(e.EntryPath))
            .GroupBy(e => OverlayBuilder.Norm(e.EntryPath))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());
        var byName = overlayEntries.GroupBy(e => e.Filename.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList());
        progress?.Invoke(progressStart, $"{progressLabel}: READ PATHC");
        var pathcReadWatch = Stopwatch.StartNew();
        var pathc = PathcFile.Read(pathcPath);
        pathcReadWatch.Stop();

        var pathcIndexWatch = Stopwatch.StartNew();
        var mapByHash = new Dictionary<uint, PathcMapEntry>(pathc.KeyHashes.Count + ddsMatches.Count);
        for (int i = 0; i < pathc.KeyHashes.Count && i < pathc.MapEntries.Count; i++)
            mapByHash[pathc.KeyHashes[i]] = pathc.MapEntries[i];

        // Use structural byte-array keys instead of converting every PATHC DDS
        // record to hex text.  This keeps the output identical while avoiding a
        // large temporary string allocation pass on the 282k-row stock PATHC.
        var ddsRecordLookup = new Dictionary<byte[], int>(pathc.DdsRecords.Count + ddsMatches.Count, ByteArrayComparer.Instance);
        for (int i = 0; i < pathc.DdsRecords.Count; i++)
        {
            if (!ddsRecordLookup.ContainsKey(pathc.DdsRecords[i])) ddsRecordLookup[pathc.DdsRecords[i]] = i;
        }
        pathcIndexWatch.Stop();

        double sourceHeaderReadSeconds = 0;
        int sourceHeaderDiskReads = 0;
        int sourceHeaderCacheHits = 0;
        var pathcApplyWatch = Stopwatch.StartNew();

        int updated = 0, added = 0, preserved = 0, skipped = 0;
        var updatedPaths = new List<string>();
        var addedPaths = new List<string>();
        var unchangedPaths = new List<string>();
        var packedWithoutEditableMetadataPaths = new List<string>();
        var skippedPaths = new List<string>();
        var packedWithoutEditableMetadataSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recordSize = (int)pathc.Header.DdsRecordSize;
        var replayDdsRecordByHash = BuildReplayDdsRecordLookup(replayPathcSources, recordSize);
        int cachedHeaderRecordsUsed = 0;
        int replaySnapshotRecordsUsed = 0;
        int overlayPayloadHeaderRecordsUsed = 0;
        int sourceHeaderRequiredReads = 0;
        int totalProgressMatches = Math.Max(1, ddsMatches.Count);
        int processedProgressMatches = 0;
        foreach (var m in ddsMatches)
        {
            processedProgressMatches++;
            if (processedProgressMatches == 1 || processedProgressMatches % 250 == 0 || processedProgressMatches == ddsMatches.Count)
            {
                int pct = progressStart + (int)Math.Round((processedProgressMatches / (double)totalProgressMatches) * Math.Max(0, progressEnd - progressStart));
                progress?.Invoke(Math.Max(progressStart, Math.Min(progressEnd, pct)), $"{progressLabel}: {processedProgressMatches}/{ddsMatches.Count}");
            }
            if (!packedByFull.TryGetValue(OverlayBuilder.Norm(m.FullPath), out var oe) &&
                !packedByEntry.TryGetValue(OverlayBuilder.Norm(m.EntryPath), out oe))
            {
                var c = byName.TryGetValue(m.Filename.ToLowerInvariant(), out var l) ? l : new List<OverlayEntry>();
                if (c.Count == 1) oe = c[0];
            }
            string displayPath = string.IsNullOrWhiteSpace(m.FullPath) ? m.EntryPath : m.FullPath;
            if (oe == null || oe.DdsMValues == null)
            {
                string line = displayPath + " | packed; PATHC metadata not editable (missing m values)";
                if (packedWithoutEditableMetadataSet.Add(line))
                {
                    packedWithoutEditableMetadataPaths.Add(line);
                    string pathcInfoLine = $"INFO: packed {m.EntryPath}; PATHC metadata not editable for this stock row.";
                    // Public Activity Log stays user-level. Per-file PATHC metadata
                    // details are runtime/report only unless --debugmode is enabled.
                    if (DebugFailureInjector.Enabled) log(pathcInfoLine);
                    else log("[runtime] " + pathcInfoLine);
                }
                continue;
            }
            string vpath = !string.IsNullOrEmpty(oe.DirPath) ? "/" + oe.DirPath.Trim('/') + "/" + m.Filename : "/" + m.FullPath.Trim('/');
            uint h = PathcFile.PathHash(vpath);
            bool exists = mapByHash.TryGetValue(h, out var oldEntry);
            if (exists && oldEntry is not null && oldEntry.M1 == oe.DdsMValues[0] && oldEntry.M2 == oe.DdsMValues[1] && oldEntry.M3 == oe.DdsMValues[2] && oldEntry.M4 == oe.DdsMValues[3])
            {
                preserved++;
                unchangedPaths.Add(displayPath);
                continue;
            }

            byte[] rec;
            uint hForReplay = h;
            if (TryBuildDdsPathcRecordFromHeader(oe.DdsPathcHeader, recordSize, out rec))
            {
                cachedHeaderRecordsUsed++;
            }
            else if (replayDdsRecordByHash.TryGetValue(hForReplay, out var replayRecord))
            {
                rec = replayRecord.ToArray();
                replaySnapshotRecordsUsed++;
            }
            else if (TryReadDdsPathcRecordFromOverlayPayload(gameDir, oe, recordSize, out rec, out string overlayPayloadDetail))
            {
                overlayPayloadHeaderRecordsUsed++;
            }
            else
            {
                // Legacy fallback for old manifests that do not yet contain cached
                // PATHC header snapshots and cannot recover the header from the
                // installed/held overlay PAZ. New v1.4.2+ manifests should not need
                // this during Hold/Release or Relink.
                var headerWatch = Stopwatch.StartNew();
                byte[] header;
                try
                {
                    header = ReadDdsHeaderOnly(m.SourcePath, out bool headerFromCache);
                    headerWatch.Stop();
                    sourceHeaderReadSeconds += headerWatch.Elapsed.TotalSeconds;
                    if (headerFromCache) sourceHeaderCacheHits++;
                    else sourceHeaderDiskReads++;
                    sourceHeaderRequiredReads++;
                }
                catch (Exception ex)
                {
                    headerWatch.Stop();
                    string line = displayPath + " | could not acquire DDS PATHC header from cached metadata, PATHC snapshot, overlay payload, or source file: " + ex.Message;
                    if (skippedSet.Add(line)) skippedPaths.Add(line);
                    continue;
                }

                if (!TryBuildDdsPathcRecordFromHeader(header, recordSize, out rec))
                {
                    string line = displayPath + " | source is not a DDS header";
                    if (skippedSet.Add(line)) skippedPaths.Add(line);
                    continue;
                }
            }
            if (!ddsRecordLookup.TryGetValue(rec, out int ddsIdx))
            {
                pathc.DdsRecords.Add(rec);
                ddsIdx = pathc.DdsRecords.Count - 1;
                ddsRecordLookup[rec] = ddsIdx;
            }
            uint selector = 0xFFFF0000u | ((uint)ddsIdx & 0xFFFFu);
            mapByHash[h] = new PathcMapEntry { Selector = selector, M1 = oe.DdsMValues[0], M2 = oe.DdsMValues[1], M3 = oe.DdsMValues[2], M4 = oe.DdsMValues[3] };
            if (exists) { updated++; updatedPaths.Add(displayPath); }
            else { added++; addedPaths.Add(displayPath); }
        }

        pathcApplyWatch.Stop();
        int packedWithoutEditableMetadata = packedWithoutEditableMetadataPaths.Count;
        skipped = skippedPaths.Count;

        int startingHashCount = pathc.KeyHashes.Count;
        int currentApplyTouched = updated + added + preserved;
        int retainedExisting = Math.Max(0, mapByHash.Count - currentApplyTouched);

        var pathcOrderWatch = Stopwatch.StartNew();
        var ordered = mapByHash.OrderBy(kv => kv.Key).ToList();
        pathc.KeyHashes = ordered.Select(kv => kv.Key).ToList();
        pathc.MapEntries = ordered.Select(kv => kv.Value).ToList();
        pathcOrderWatch.Stop();

        var pathcSerializeWatch = Stopwatch.StartNew();
        byte[] serializedPathc = pathc.Serialize();
        pathcSerializeWatch.Stop();

        log($"PATHC current apply: {updated} updated, {added} added, {preserved} unchanged, {packedWithoutEditableMetadata} packed without editable metadata, {skipped} skipped.");
        log($"PATHC existing entries retained: {retainedExisting}. Total PATHC rows: {mapByHash.Count} (was {startingHashCount}).");
        log($"Timing: PATHC read: {pathcReadWatch.Elapsed.TotalSeconds:F1}s");
        log($"Timing: PATHC index build: {pathcIndexWatch.Elapsed.TotalSeconds:F1}s");
        log($"Timing: PATHC source DDS header read/acquire: {sourceHeaderReadSeconds:F1}s ({sourceHeaderDiskReads:n0} disk read(s), {sourceHeaderCacheHits:n0} cache hit(s)).");
        log($"PATHC header replay sources: manifest cache {cachedHeaderRecordsUsed:n0}, PATHC snapshot {replaySnapshotRecordsUsed:n0}, overlay PAZ payload {overlayPayloadHeaderRecordsUsed:n0}, source folder fallback {sourceHeaderRequiredReads:n0}.");
        log($"Timing: PATHC map update: {pathcApplyWatch.Elapsed.TotalSeconds:F1}s");
        log($"Timing: PATHC order rows: {pathcOrderWatch.Elapsed.TotalSeconds:F1}s");
        log($"Timing: PATHC serialize: {pathcSerializeWatch.Elapsed.TotalSeconds:F1}s");
        progress?.Invoke(progressEnd, $"{progressLabel}: DONE");
        var summary = new PathcUpdateResult(updated, added, preserved, packedWithoutEditableMetadata, skipped, retainedExisting, mapByHash.Count, startingHashCount, updatedPaths, addedPaths, unchangedPaths, packedWithoutEditableMetadataPaths, skippedPaths);
        return (serializedPathc, summary);
    }


    public static SourceRootContainerGuardResult AnalyzeTextureSourceRootSelection(string textureDir)
    {
        var empty = new SourceRootContainerGuardResult(false, textureDir, new List<string>(), false);
        if (string.IsNullOrWhiteSpace(textureDir) || !Directory.Exists(textureDir)) return empty;

        string fullRoot;
        try { fullRoot = Path.GetFullPath(textureDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return empty; }

        // Selecting an archive folder itself (0000, 0001, 0009, etc.) is intentional.
        string rootName = Path.GetFileName(fullRoot);
        if (IsArchiveFolderPrefix(rootName)) return new SourceRootContainerGuardResult(false, fullRoot, new List<string>(), true);

        List<string> childDirs;
        try { childDirs = Directory.EnumerateDirectories(fullRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return empty; }
        if (childDirs.Count < 2) return empty;

        var ddsBearingChildren = new List<string>();
        foreach (var child in childDirs)
        {
            try
            {
                if (Directory.EnumerateFiles(child, "*.dds", SearchOption.AllDirectories).Any())
                    ddsBearingChildren.Add(child);
            }
            catch
            {
                // If a child cannot be scanned, leave it out of the guard. The normal
                // build scan will report the real access error later if needed.
            }
        }

        if (ddsBearingChildren.Count < 2) return new SourceRootContainerGuardResult(false, fullRoot, ddsBearingChildren, false);

        // A root containing top-level archive folders like 0000/0001/0009/0012 is a
        // valid archive-scoped texture root, not a random parent/container folder.
        bool archiveRoot = ddsBearingChildren.All(d => IsArchiveFolderPrefix(Path.GetFileName(d)));
        if (archiveRoot) return new SourceRootContainerGuardResult(false, fullRoot, ddsBearingChildren, true);

        return new SourceRootContainerGuardResult(true, fullRoot, ddsBearingChildren, false);
    }


    private static (List<MatchedFile>, List<string>, List<string>, Dictionary<string,int>, List<LooseMatchDiagnostic>) MatchTextures(BuildOptions options, Action<string> log, CancellationToken cancellationToken = default)
    {
        var index = BuildPamtIndex(options.GameDir, options.ScanExistingModDirs, log);
        var sourceFiles = Directory.EnumerateFiles(options.TextureDir, "*", SearchOption.AllDirectories)
            .Where(p => SupportedExts.Contains(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceFiles.Count == 0) throw new FileNotFoundException("No .dds files were found in the texture/mod folder.");

        bool en = string.Equals(options.Language, "en", StringComparison.OrdinalIgnoreCase);
        var matched = new List<MatchedFile>();
        var skipped = new List<string>();
        var ambiguous = new List<string>();
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var looseDiagnostics = new List<LooseMatchDiagnostic>();
        string sourceRootArchiveScope = DetectSourceRootArchive(options.TextureDir);

        log($"Detected {sourceFiles.Count} .dds textures in the mod.");
        if (!string.IsNullOrEmpty(sourceRootArchiveScope))
        {
            stats[$"archive_scope_{sourceRootArchiveScope}"] = sourceFiles.Count;
            log($"Top-level archive folder scope detected: {sourceRootArchiveScope}. Source files will only match {sourceRootArchiveScope} stock targets unless their own path gives a more specific archive hint.");
        }
        else
        {
            stats["safe_primary_flat_or_auto_source"] = sourceFiles.Count;
        }
        string pamtFilter = NormalizePamt(options.TargetPamtDir);
        string prefixFilter = NormalizePrefix(options.TargetFullPrefix);
        if (!string.IsNullOrEmpty(pamtFilter) || !string.IsNullOrEmpty(prefixFilter))
            log($"Source filter active: PAMT={pamtFilter.DefaultIfEmpty("ALL")}, path={prefixFilter.DefaultIfEmpty("ALL")}." );

        foreach (var src in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(options.TextureDir, src).Replace('\\','/').Trim('/');
            string filename = Path.GetFileName(src);
            var (chosen, method, candidates) = ChooseCandidates(rel, filename, index, options.AllowUniqueFilename, options.ApplyDuplicateLooseMatches, pamtFilter, prefixFilter, sourceRootArchiveScope);
            stats[method] = stats.GetValueOrDefault(method) + 1;
            AddLooseMatchDiagnosticsIfNeeded(looseDiagnostics, stats, src, rel, filename, method, chosen, candidates);

            if (chosen.Count == 0)
            {
                if (method.StartsWith("ambiguous", StringComparison.OrdinalIgnoreCase))
                {
                    string candText = string.Join(", ", candidates.Take(20).Select(c => $"{c.PamtDir}:{c.FullPath}"));
                    ambiguous.Add(en
                        ? $"{rel} -> {method}: {candidates.Count} candidates: {candText}"
                        : $"{rel} -> {method}: {candidates.Count} candidatos: {candText}");
                }
                else
                {
                    skipped.Add(en ? $"{rel} -> not found in vanilla PAMT ({method})" : $"{rel} -> no encontrado en PAMT vanilla ({method})");
                }
                continue;
            }

            var fi = new FileInfo(src);
            foreach (var cand in chosen)
            {
                matched.Add(new MatchedFile
                {
                    SourcePath = src,
                    RelPath = rel,
                    Size = fi.Length,
                    PamtDir = cand.PamtDir,
                    EntryPath = cand.EntryPath,
                    FullPath = cand.FullPath,
                    Filename = cand.Filename,
                    CompressionType = cand.CompressionType,
                    Encrypted = cand.Encrypted,
                    CryptoFilename = cand.CryptoFilename,
                    MatchMethod = method
                });
            }
        }

        return (matched, skipped, ambiguous, stats, looseDiagnostics);
    }



    private static void AddLooseMatchDiagnosticsIfNeeded(List<LooseMatchDiagnostic> diagnostics, Dictionary<string,int> stats, string src, string rel, string filename, string method, List<IndexCandidate> chosen, List<IndexCandidate> candidates)
    {
        bool isLooseDiagnostic = method.Contains("loose", StringComparison.OrdinalIgnoreCase)
            || method.Contains("filename_fallback", StringComparison.OrdinalIgnoreCase)
            || candidates.Count > 1;
        if (!isLooseDiagnostic || candidates.Count <= 1) return;

        string sourceBase = Path.GetFileName(filename);
        string sourceSuffix = TextureTypeSuffix(sourceBase);
        string sourceInfo = TryReadDdsHeaderDiagnostic(src).Text;
        IndexCandidate? primary = null;
        if (method.Contains("+loose_duplicates", StringComparison.OrdinalIgnoreCase) && chosen.Count > 0)
            primary = chosen[0];
        else if (!method.Contains("multi_target_loose_filename", StringComparison.OrdinalIgnoreCase) && chosen.Count == 1)
            primary = chosen[0];

        var diag = new LooseMatchDiagnostic
        {
            SourceRel = rel,
            SourceBasename = sourceBase,
            MatchMethod = method,
            SelectedPrimaryTarget = primary == null ? "<none; legacy matching applies all selected candidates>" : $"{primary.PamtDir}:{primary.FullPath}",
            FinalDecision = chosen.Count > 0 ? $"Safe Primary / selected matching applied {chosen.Count} target(s)." : "not applied",
            SourceDdsInfo = sourceInfo
        };

        var chosenKeys = new HashSet<string>(chosen.Select(CandidateKey), StringComparer.OrdinalIgnoreCase);
        bool legacyFanoutWouldApplyAll = candidates.Count > 1 &&
            (method.Contains("safe_primary", StringComparison.OrdinalIgnoreCase)
             || method.Contains("multi_target_loose_filename", StringComparison.OrdinalIgnoreCase)
             || method.Contains("+loose_duplicates", StringComparison.OrdinalIgnoreCase));
        foreach (var cand in candidates.OrderBy(c => c.PamtDir).ThenBy(c => OverlayBuilder.Norm(c.FullPath), StringComparer.Ordinal))
        {
            bool currentPolicyWouldApply = chosenKeys.Contains(CandidateKey(cand));
            bool legacyFanoutWouldApply = legacyFanoutWouldApplyAll || currentPolicyWouldApply;
            bool hotfix3WouldApply = WouldStrictHotfix3Apply(primary, cand, method);
            string candSuffix = TextureTypeSuffix(cand.Filename);
            bool exactSuffix = string.Equals(sourceSuffix, candSuffix, StringComparison.OrdinalIgnoreCase);
            bool exactBase = string.Equals(sourceBase, cand.Filename, StringComparison.OrdinalIgnoreCase);
            string reason = currentPolicyWouldApply
                ? (primary == null ? "legacy fan-out duplicate filename candidate" : (CandidateKey(cand) == CandidateKey(primary) ? "primary exact/selected target" : "legacy fan-out expansion target"))
                : (legacyFanoutWouldApply ? "rejected secondary duplicate target; legacy fan-out would have applied" : "candidate only / not selected");
            string dimCompat = $"source={sourceInfo}; target_dims=unknown_from_PAMT; target_orig_size={cand.OrigSize}; target_comp_size={cand.CompSize}";
            string final = currentPolicyWouldApply ? "APPLIED_BY_CURRENT_POLICY" : "REJECTED_SECONDARY_DUPLICATE_TARGET";
            if (!currentPolicyWouldApply && legacyFanoutWouldApply) final += "; LEGACY_FANOUT_WOULD_HAVE_APPLIED";
            if (currentPolicyWouldApply && !hotfix3WouldApply) final += "; STRICT_HOTFIX3_WOULD_SKIP_OR_MARK_AMBIGUOUS";

            diag.Candidates.Add(new LooseMatchCandidateDiagnostic
            {
                PamtDir = cand.PamtDir,
                FullPath = cand.FullPath,
                EntryPath = cand.EntryPath,
                Reason = reason,
                ExactBasename = exactBase,
                ExactSuffixType = exactSuffix,
                SourceSuffixType = sourceSuffix.DefaultIfEmpty("<none>"),
                CandidateSuffixType = candSuffix.DefaultIfEmpty("<none>"),
                DimensionFormatCompatibility = dimCompat,
                Hotfix2LegacyWouldApply = legacyFanoutWouldApply,
                Hotfix3StrictWouldApply = hotfix3WouldApply,
                FinalDecision = final
            });

            if (currentPolicyWouldApply && (method.Contains("multi_target_loose_filename", StringComparison.OrdinalIgnoreCase) || method.Contains("+loose_duplicates", StringComparison.OrdinalIgnoreCase)))
                stats["loose_duplicate_matches_applied"] = stats.GetValueOrDefault("loose_duplicate_matches_applied") + 1;
            if (!currentPolicyWouldApply && legacyFanoutWouldApply)
                stats["safe_primary_rejected_secondary_duplicate_targets"] = stats.GetValueOrDefault("safe_primary_rejected_secondary_duplicate_targets") + 1;
            if (currentPolicyWouldApply && !hotfix3WouldApply)
                stats["loose_duplicate_candidates_considered_unsafe_by_strict_hotfix3"] = stats.GetValueOrDefault("loose_duplicate_candidates_considered_unsafe_by_strict_hotfix3") + 1;
            if (!exactSuffix)
                stats["diagnostic_suffix_type_mismatch_candidates"] = stats.GetValueOrDefault("diagnostic_suffix_type_mismatch_candidates") + 1;
            if (primary != null && !SamePamtAndParentFamily(primary, cand))
                stats["diagnostic_pamt_path_family_mismatch_candidates"] = stats.GetValueOrDefault("diagnostic_pamt_path_family_mismatch_candidates") + 1;
            stats["diagnostic_dimension_format_unknown_candidates"] = stats.GetValueOrDefault("diagnostic_dimension_format_unknown_candidates") + 1;
        }
        diagnostics.Add(diag);
    }

    private static string CandidateKey(IndexCandidate c) => $"{c.PamtDir}:{OverlayBuilder.Norm(c.FullPath)}:{c.Filename}";

    private static bool WouldStrictHotfix3Apply(IndexCandidate? primary, IndexCandidate cand, string method)
    {
        if (primary == null) return false;
        if (CandidateKey(primary).Equals(CandidateKey(cand), StringComparison.OrdinalIgnoreCase)) return true;
        return SamePamtAndParentFamily(primary, cand)
            && string.Equals(primary.Filename, cand.Filename, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TextureTypeSuffix(primary.Filename), TextureTypeSuffix(cand.Filename), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePamtAndParentFamily(IndexCandidate a, IndexCandidate b)
    {
        return string.Equals(a.PamtDir, b.PamtDir, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ParentFamily(a.FullPath), ParentFamily(b.FullPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string ParentFamily(string path)
    {
        path = OverlayBuilder.Norm(path);
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "" : path[..slash];
    }

    private static string TextureTypeSuffix(string filename)
    {
        string stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        string[] suffixes = { "_n", "_d", "_sp", "_tr", "_dec", "_emc", "_s", "_disp", "_dmap", "_m", "_r", "_ao", "_h", "_height", "_normal", "_color" };
        return suffixes.FirstOrDefault(s => stem.EndsWith(s, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static DdsHeaderDiagnostic TryReadDdsHeaderDiagnostic(string path)
    {
        try
        {
            byte[] h = new byte[148];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            int n = fs.Read(h, 0, h.Length);
            if (n < 128 || h[0] != 'D' || h[1] != 'D' || h[2] != 'S' || h[3] != ' ')
                return new DdsHeaderDiagnostic("not-dds-or-short-header");
            uint height = BinaryUtil.U32(h, 12);
            uint width = BinaryUtil.U32(h, 16);
            uint mipCount = BinaryUtil.U32(h, 28);
            if (mipCount == 0) mipCount = 1;
            string fourcc = Encoding.ASCII.GetString(h, 84, 4).TrimEnd('\0');
            string fmt = fourcc;
            if (fourcc == "DX10" && n >= 132)
            {
                uint dxgi = BinaryUtil.U32(h, 128);
                fmt = $"DX10/{dxgi}";
            }
            return new DdsHeaderDiagnostic($"{width}x{height}, mips={mipCount}, format={fmt}, size={new FileInfo(path).Length}");
        }
        catch (Exception ex)
        {
            return new DdsHeaderDiagnostic($"header-read-failed: {ex.Message}");
        }
    }

    private static List<string> ResolveDuplicateTargetMatches(ref List<MatchedFile> matches, string language)
    {
        bool en = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var skipped = new List<string>();
        if (matches.Count <= 1) return skipped;

        var resolved = new List<MatchedFile>(matches.Count);
        foreach (var group in matches.GroupBy(m => $"{m.PamtDir}:{OverlayBuilder.Norm(m.FullPath)}", StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            if (list.Count == 1)
            {
                resolved.Add(list[0]);
                continue;
            }

            var best = list
                .OrderByDescending(m => MatchMethodPriority(m.MatchMethod))
                .ThenByDescending(m => OverlayBuilder.Norm(m.RelPath).Count(ch => ch == '/'))
                .ThenBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase)
                .First();
            resolved.Add(best);

            foreach (var drop in list.Where(m => !ReferenceEquals(m, best)))
            {
                if (en)
                {
                    skipped.Add($"{drop.RelPath} -> duplicate internal target {drop.PamtDir}:{drop.FullPath}; already covered by {best.RelPath} ({best.MatchMethod}). Packed only once.");
                }
                else
                {
                    skipped.Add($"{drop.RelPath} -> destino interno duplicado {drop.PamtDir}:{drop.FullPath}; ya cubierto por {best.RelPath} ({best.MatchMethod}). Empaquetado una sola vez.");
                }
            }
        }

        matches = resolved
            .OrderBy(m => m.PamtDir, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => OverlayBuilder.Norm(m.FullPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return skipped;
    }


    private static string CleanProgressName(string? name)
    {
        string clean = name ?? "";
        if (clean.StartsWith("[stage]", StringComparison.OrdinalIgnoreCase)) clean = clean[7..].Trim();
        else if (clean.StartsWith("[hash]", StringComparison.OrdinalIgnoreCase)) clean = clean[6..].Trim();
        else if (clean.StartsWith("[prepare]", StringComparison.OrdinalIgnoreCase)) clean = StripProgressPhaseAndCounter(clean, 9);
        else if (clean.StartsWith("[write]", StringComparison.OrdinalIgnoreCase)) clean = StripProgressPhaseAndCounter(clean, 7);
        return clean;
    }


    private static string StripProgressPhaseAndCounter(string text, int prefixLength)
    {
        string clean = text[prefixLength..].Trim();
        int space = clean.IndexOf(' ');
        if (space > 0)
        {
            string first = clean[..space];
            int slash = first.IndexOf('/');
            if (slash > 0 && first[..slash].All(char.IsDigit) && first[(slash + 1)..].All(char.IsDigit))
                clean = clean[(space + 1)..].Trim();
        }
        return clean;
    }

    private static int MatchMethodPriority(string? method)
    {
        method = (method ?? "").ToLowerInvariant();
        if (method is "exact_archive_path" or "exact_relative_path") return 100;
        if (method == "exact_flat_path") return 80;
        if (method == "suffix_path") return 60;
        if (method is "filename_fallback" or "loose_filename") return 50;
        if (method == "multi_target_loose_filename") return 40;
        return 10;
    }

    private static void LogExternalOverlayConflicts(string gameDir, List<MatchedFile> matches, Action<string> log)
    {
        try
        {
            var managedDirs = GetManagedOverlayDirs(gameDir, log);
            var external = BuildExternalOverlayConflictIndex(gameDir, managedDirs, log);
            if (external.Count == 0) return;

            var conflicts = matches
                .Select(m => OverlayBuilder.Norm(m.FullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => external.ContainsKey(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (conflicts.Count == 0) return;

            var dirs = conflicts.SelectMany(p => external[p]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            log($"NOTICE: Conflict scan found {conflicts.Count} target texture(s) already present in overlay folder(s) not managed by this tool: {string.Join(", ", dirs)}.");
            foreach (var p in conflicts.Take(12))
                log($"  conflict: {p} -> {string.Join(", ", external[p].Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))}");
            if (conflicts.Count > 12) log($"  ... {conflicts.Count - 12} more conflict target(s) not shown.");
        }
        catch (Exception ex)
        {
            log($"WARN: conflict scan could not complete: {ex.Message}");
        }
    }

    private static Dictionary<string, List<string>> BuildExternalOverlayConflictIndex(string gameDir, HashSet<string> managedDirs, Action<string> log)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(gameDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string od = Path.GetFileName(dir);
            string pamtPath = Path.Combine(dir, "0.pamt");
            if (!File.Exists(pamtPath)) continue;
            if (!IsNonStockOverlayFolderName(od)) continue;
            if (managedDirs.Contains(od)) continue;

            List<PazEntry> entries;
            try { entries = PazParser.ParsePamt(pamtPath, dir); }
            catch (Exception ex) { log($"WARN: could not scan conflict overlay {od}: {ex.Message}"); continue; }

            var fullMap = PamtFullPathMap.Parse(pamtPath);
            foreach (var e in entries)
            {
                if (!SupportedExts.Contains(Path.GetExtension(e.Path))) continue;
                string full = fullMap.TryGetValue(OverlayBuilder.Norm(e.Path), out var f) ? f : e.Path;
                string key = OverlayBuilder.Norm(full);
                if (!result.TryGetValue(key, out var list)) result[key] = list = new();
                if (!list.Contains(od, StringComparer.OrdinalIgnoreCase)) list.Add(od);
            }
        }
        return result;
    }

    private static PamtIndex BuildPamtIndex(string gameDir, bool includeExistingModDirs, Action<string> log)
    {
        var allPamtPaths = Directory.EnumerateDirectories(gameDir)
            .Select(d => Path.Combine(d, "0.pamt"))
            .Where(File.Exists)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stockPamtPaths = allPamtPaths
            .Where(p => IsStockPamtFolderName(Path.GetFileName(Path.GetDirectoryName(p)!)))
            .ToList();

        var existingModPamtPaths = includeExistingModDirs
            ? allPamtPaths
                .Where(p => !IsStockPamtFolderName(Path.GetFileName(Path.GetDirectoryName(p)!)))
                .ToList()
            : new List<string>();

        if (stockPamtPaths.Count == 0) throw new FileNotFoundException("No 0000/0.pamt, 0001/0.pamt, etc. folders were found. Is the game path correct?");

        PamtIndex idx;
        if (TryLoadStockPamtIndexCache(gameDir, stockPamtPaths, log, out var cachedIndex))
        {
            idx = cachedIndex;
        }
        else
        {
            idx = new PamtIndex();
            log($"Scanning {stockPamtPaths.Count} stock PAMT files...");
            ParsePamtPathsIntoIndex(idx, stockPamtPaths, true, log, "stock PAMT");
            TrySaveStockPamtIndexCache(gameDir, stockPamtPaths, idx.Candidates, log);
        }

        if (existingModPamtPaths.Count > 0)
        {
            log($"Scanning {existingModPamtPaths.Count} existing/non-stock PAMT files for conflict hints...");
            ParsePamtPathsIntoIndex(idx, existingModPamtPaths, false, log, "overlay PAMT");
        }

        log($"Index ready: {idx.Candidates.Count} textures found.");
        return idx;
    }

    private static void ParsePamtPathsIntoIndex(PamtIndex idx, List<string> pamtPaths, bool targetIndex, Action<string> log, string progressLabel)
    {
        int n = 0;
        foreach (var pamtPath in pamtPaths)
        {
            n++;
            string pamtDir = Path.GetFileName(Path.GetDirectoryName(pamtPath)!)!;
            var fullMap = PamtFullPathMap.Parse(pamtPath);
            List<PazEntry> entries;
            try { entries = PazParser.ParsePamt(pamtPath, Path.GetDirectoryName(pamtPath)); }
            catch (Exception ex) { log($"WARN: could not read {pamtPath}: {ex.Message}"); continue; }
            foreach (var e in entries)
            {
                if (!SupportedExts.Contains(Path.GetExtension(e.Path))) continue;
                string filename = e.Path.Contains('/') ? e.Path[(e.Path.LastIndexOf('/') + 1)..] : e.Path;
                string full = fullMap.TryGetValue(OverlayBuilder.Norm(e.Path), out var f) ? f : e.Path;
                var cand = new IndexCandidate(pamtDir, e.Path.Replace('\\','/'), full.Replace('\\','/'), filename, e.CompressionType, e.Encrypted, filename, e.Flags, e.CompSize, e.OrigSize);
                if (targetIndex)
                {
                    idx.Add(cand);
                }
                else
                {
                    string flat = OverlayBuilder.Norm(cand.EntryPath);
                    string name = cand.Filename.ToLowerInvariant();
                    if (!idx.ExistingModTargets.TryGetValue(flat, out var lt)) idx.ExistingModTargets[flat] = lt = new();
                    lt.Add(cand.PamtDir);
                    if (!idx.ExistingModNames.TryGetValue(name, out var ln)) idx.ExistingModNames[name] = ln = new();
                    ln.Add(cand.PamtDir);
                }
            }
            if (n % 5 == 0) log($"  {progressLabel} {n}/{pamtPaths.Count}...");
        }
    }

    private const int StockPamtIndexCacheFormatVersion = 2;

    private sealed class StockPamtIndexCache
    {
        public int Version { get; set; }
        public string AppVersion { get; set; } = "";
        public string GameRootHash { get; set; } = "";
        public StockMetaCacheInfo Meta { get; set; } = new();
        public List<StockPamtCacheSource> Sources { get; set; } = new();
        public List<IndexCandidate> Candidates { get; set; } = new();
    }

    private sealed class StockMetaCacheInfo
    {
        public long PapgtLength { get; set; }
        public long PapgtLastWriteUtcTicks { get; set; }
        public long PathcLength { get; set; }
        public long PathcLastWriteUtcTicks { get; set; }
    }

    private sealed class StockPamtCacheSource
    {
        public string RelativePath { get; set; } = "";
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }

    private static string AppLocalCacheRoot()
        => Path.Combine(AppContext.BaseDirectory, "Cache");

    private static string StableSha256Text(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GameRootCacheHash(string gameDir)
    {
        string full = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return StableSha256Text(full)[..16];
    }

    private static string StockPamtIndexCachePath(string gameDir)
        => Path.Combine(AppLocalCacheRoot(), $"stock_pamt_index_{GameRootCacheHash(gameDir)}_v{StockPamtIndexCacheFormatVersion}.json");

    private static StockMetaCacheInfo BuildStockMetaCacheInfo(string gameDir)
    {
        static (long Length, long Ticks) Info(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : (0, 0);
        }

        var papgt = Info(Path.Combine(gameDir, "meta", "0.papgt"));
        var pathc = Info(Path.Combine(gameDir, "meta", "0.pathc"));
        return new StockMetaCacheInfo
        {
            PapgtLength = papgt.Length,
            PapgtLastWriteUtcTicks = papgt.Ticks,
            PathcLength = pathc.Length,
            PathcLastWriteUtcTicks = pathc.Ticks
        };
    }

    private static List<StockPamtCacheSource> BuildStockPamtCacheSources(string gameDir, List<string> pamtPaths)
    {
        return pamtPaths.Select(p =>
        {
            var fi = new FileInfo(p);
            return new StockPamtCacheSource
            {
                RelativePath = Path.GetRelativePath(gameDir, p).Replace('\\', '/'),
                Length = fi.Length,
                LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks
            };
        }).OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryLoadStockPamtIndexCache(string gameDir, List<string> stockPamtPaths, Action<string> log, out PamtIndex idx)
    {
        idx = new PamtIndex();
        string cachePath = StockPamtIndexCachePath(gameDir);
        if (!File.Exists(cachePath))
        {
            log("Stock PAMT index cache invalid/outdated; rebuilding...");
            return false;
        }
        try
        {
            var cache = JsonSerializer.Deserialize<StockPamtIndexCache>(File.ReadAllText(cachePath));
            var expectedSources = BuildStockPamtCacheSources(gameDir, stockPamtPaths);
            string expectedGameRootHash = GameRootCacheHash(gameDir);
            if (cache == null
                || cache.Version != StockPamtIndexCacheFormatVersion
                || !string.Equals(cache.GameRootHash, expectedGameRootHash, StringComparison.OrdinalIgnoreCase)
                || cache.Candidates.Count == 0
                || !StockPamtCacheSourcesMatch(expectedSources, cache.Sources))
            {
                log("Stock PAMT index cache invalid/outdated; rebuilding...");
                return false;
            }
            foreach (var c in cache.Candidates) idx.Add(c);
            log($"Stock PAMT index cache loaded: {idx.Candidates.Count} texture records.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Stock PAMT index cache invalid/outdated; rebuilding... ({ex.Message})");
            idx = new PamtIndex();
            return false;
        }
    }

    private static void TrySaveStockPamtIndexCache(string gameDir, List<string> stockPamtPaths, List<IndexCandidate> candidates, Action<string> log)
    {
        try
        {
            string cachePath = StockPamtIndexCachePath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var cache = new StockPamtIndexCache
            {
                Version = StockPamtIndexCacheFormatVersion,
                AppVersion = AppVersion,
                GameRootHash = GameRootCacheHash(gameDir),
                Meta = BuildStockMetaCacheInfo(gameDir),
                Sources = BuildStockPamtCacheSources(gameDir, stockPamtPaths),
                Candidates = candidates.ToList()
            };
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(cachePath, json);
            log($"Stock PAMT index cache saved: {candidates.Count} texture records. Location: {cachePath}");
        }
        catch (Exception ex)
        {
            log($"WARN: stock PAMT index cache could not be saved: {ex.Message}");
        }
    }

    private static bool StockPamtCacheSourcesMatch(List<StockPamtCacheSource> expected, List<StockPamtCacheSource> actual)
    {
        if (expected.Count != actual.Count) return false;
        actual = actual.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(expected[i].RelativePath, actual[i].RelativePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (expected[i].Length != actual[i].Length) return false;
            if (expected[i].LastWriteUtcTicks != actual[i].LastWriteUtcTicks) return false;
        }
        return true;
    }



    private const int SourceManifestFormatVersion = 1;

    private sealed class ActiveBuildSnapshot
    {
        public List<string> BuildIds { get; set; } = new();
        public List<string> OverlayDirs { get; set; } = new();
        public long ActiveBuildRevision { get; set; }
        public bool IsValid => BuildIds.Count > 0 && OverlayDirs.Count > 0;
    }

    private sealed class SourceManifestState
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("game_root_hash")] public string GameRootHash { get; set; } = "";
        [JsonPropertyName("source_root_hash")] public string SourceRootHash { get; set; } = "";
        [JsonPropertyName("created_at_utc")] public string CreatedAtUtc { get; set; } = "";
        [JsonPropertyName("updated_at_utc")] public string UpdatedAtUtc { get; set; } = "";
        [JsonPropertyName("active_build_ids")] public List<string> ActiveBuildIds { get; set; } = new();
        [JsonPropertyName("active_overlay_dirs")] public List<string> ActiveOverlayDirs { get; set; } = new();
        [JsonPropertyName("active_build_revision")] public long? ActiveBuildRevision { get; set; }
        [JsonPropertyName("matching_policy")] public SourceManifestMatchingPolicy MatchingPolicy { get; set; } = new();
        [JsonPropertyName("entries")] public List<SourceManifestEntry> Entries { get; set; } = new();
    }

    private sealed class SourceManifestMatchingPolicy
    {
        [JsonPropertyName("allow_unique_filename")] public bool AllowUniqueFilename { get; set; }
        [JsonPropertyName("loose_duplicates_to_all_targets")] public bool ApplyDuplicateLooseMatches { get; set; }
        [JsonPropertyName("conflict_scan_enabled")] public bool ScanExistingModDirs { get; set; }
        [JsonPropertyName("target_pamt_dir")] public string TargetPamtDir { get; set; } = "";
        [JsonPropertyName("target_full_prefix")] public string TargetFullPrefix { get; set; } = "";
    }

    private sealed class SourceManifestEntry
    {
        [JsonPropertyName("relative_path")] public string RelativePath { get; set; } = "";
        [JsonPropertyName("source_root_hash")] public string SourceRootHash { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("last_write_utc_ticks")] public long LastWriteUtcTicks { get; set; }
        [JsonPropertyName("quick_hash")] public string QuickHash { get; set; } = "";
        [JsonPropertyName("target_internal_path")] public string TargetInternalPath { get; set; } = "";
        [JsonPropertyName("target_entry_path")] public string TargetEntryPath { get; set; } = "";
        [JsonPropertyName("target_pamt_dir")] public string TargetPamtDir { get; set; } = "";
        [JsonPropertyName("owning_overlay")] public string OwningOverlay { get; set; } = "";
        [JsonPropertyName("owning_paz_index")] public uint OwningPazIndex { get; set; }
        [JsonPropertyName("owning_paz_offset")] public uint OwningPazOffset { get; set; }
        [JsonPropertyName("owning_paz_comp_size")] public uint OwningPazCompSize { get; set; }
        [JsonPropertyName("match_method")] public string MatchMethod { get; set; } = "";
    }

    private static string AppLocalStateRoot()
        => Path.Combine(AppContext.BaseDirectory, "State");

    private static string SourceRootCacheHash(string sourceDir)
    {
        string full = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return StableSha256Text(full)[..16];
    }

    private static string SourceManifestPath(string gameDir, string sourceDir)
        => Path.Combine(AppLocalStateRoot(), $"source_manifest_{GameRootCacheHash(gameDir)}_{SourceRootCacheHash(sourceDir)}_v{SourceManifestFormatVersion}.json");

    private static string ActiveTargetManifestPath(string gameDir)
        => Path.Combine(RegistryRoot(gameDir), ActiveTargetManifestFileName);

    private static IEnumerable<string> ActiveTargetManifestPaths(string gameDir)
    {
        yield return Path.Combine(RegistryRoot(gameDir), ActiveTargetManifestFileName);
        yield return Path.Combine(PreviousRegistryRoot(gameDir), ActiveTargetManifestFileName);
        yield return Path.Combine(LegacyRegistryRoot(gameDir), ActiveTargetManifestFileName);
    }

    private static Dictionary<string, SourceManifestEntry> SourceManifestTargetMap(SourceManifestState state)
    {
        var map = new Dictionary<string, SourceManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in state.Entries)
        {
            string key = OverlayBuilder.Norm(e.TargetInternalPath);
            if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key)) map[key] = e;
        }
        return map;
    }

    private static SourceManifestState? TryLoadActiveTargetManifestForUpdate(
        string gameDir,
        ActiveBuildSnapshot? activeSnapshot,
        Dictionary<string, ExistingOverlayTarget> activeTargetIndex,
        BuildOptions options,
        Action<string> log)
    {
        if (activeSnapshot == null || !activeSnapshot.IsValid)
        {
            log("Active target manifest ignored: no active managed build registry was found.");
            return null;
        }

        string? path = ActiveTargetManifestPaths(gameDir).FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            log("Active target manifest not found; selected source DDS targets will be treated as changed/new if they match installed overlays.");
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<SourceManifestState>(File.ReadAllText(path));
            if (state == null
                || state.Version != SourceManifestFormatVersion
                || !string.Equals(state.GameRootHash, GameRootCacheHash(gameDir), StringComparison.OrdinalIgnoreCase))
            {
                log("Active target manifest invalid/outdated; target-centric unchanged skip disabled for this run.");
                return null;
            }

            bool identityMatches = SameStringSet(state.ActiveBuildIds, activeSnapshot.BuildIds)
                && SameStringSet(state.ActiveOverlayDirs, activeSnapshot.OverlayDirs);
            bool structurallyTrusted = false;
            string structuralReason = "";

            if (!identityMatches)
            {
                structurallyTrusted = ActiveTargetManifestMatchesInstalledTargets(state, activeTargetIndex, activeSnapshot, out structuralReason);
                if (!structurallyTrusted)
                {
                    log("Active target manifest belongs to a different active managed build and could not be reconciled with the installed managed overlay index; target-centric unchanged skip disabled for this run." + (string.IsNullOrWhiteSpace(structuralReason) ? "" : " " + structuralReason));
                    return null;
                }
                log("Active target manifest identity differs from the current active build, but its target records match the installed managed overlay index; target-centric unchanged skip remains enabled and the manifest will be rebased when refreshed.");
            }

            if (!state.ActiveBuildRevision.HasValue || state.ActiveBuildRevision.Value != activeSnapshot.ActiveBuildRevision)
            {
                string oldRev = state.ActiveBuildRevision.HasValue ? state.ActiveBuildRevision.Value.ToString() : "missing";
                log($"Active target manifest revision differs from active build revision ({oldRev} -> {activeSnapshot.ActiveBuildRevision}); target-centric unchanged skip remains enabled because the installed target index still matches. The manifest will be rebased when refreshed.");
            }

            // The target-centric manifest is keyed by installed target path and verified
            // against the active managed overlay index before a DDS is skipped. A source-root
            // manifest still honors exact matching-policy gates, but the global active target
            // manifest should not lose unchanged-skip solely because the UI policy/revision
            // metadata drifted after remove/replay/relink style operations.
            if (!MatchingPolicyEquals(state.MatchingPolicy, BuildMatchingPolicy(options)))
                log("Active target manifest matching policy differs from the current UI options; target-centric unchanged skip remains enabled because each skip is verified against the installed target index and source file metadata.");

            state.ActiveBuildIds = activeSnapshot.BuildIds.ToList();
            state.ActiveOverlayDirs = activeSnapshot.OverlayDirs.ToList();
            state.ActiveBuildRevision = activeSnapshot.ActiveBuildRevision;
            log($"Active target manifest loaded: {state.Entries.Count:n0} installed target record(s), active build revision {state.ActiveBuildRevision.Value}.");
            return state;
        }
        catch (Exception ex)
        {
            log($"Active target manifest invalid/outdated; target-centric unchanged skip disabled for this run. ({ex.Message})");
            return null;
        }
    }

    private static bool ActiveTargetManifestMatchesInstalledTargets(
        SourceManifestState state,
        Dictionary<string, ExistingOverlayTarget> activeTargetIndex,
        ActiveBuildSnapshot activeSnapshot,
        out string reason)
    {
        reason = "";
        if (activeTargetIndex.Count == 0)
        {
            reason = "Installed managed target index is empty.";
            return false;
        }

        var map = SourceManifestTargetMap(state);
        if (map.Count < activeTargetIndex.Count)
        {
            reason = $"Manifest has {map.Count:n0} target record(s), but installed managed index has {activeTargetIndex.Count:n0}.";
            return false;
        }

        var activeDirs = activeSnapshot.OverlayDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int verified = 0;
        foreach (var kv in activeTargetIndex)
        {
            if (!map.TryGetValue(kv.Key, out var entry))
            {
                reason = $"Missing installed target record: {kv.Key}";
                return false;
            }
            if (string.IsNullOrWhiteSpace(entry.OwningOverlay)
                || !activeDirs.Contains(entry.OwningOverlay)
                || !string.Equals(entry.OwningOverlay, kv.Value.OverlayDir, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Owning overlay mismatch for installed target: {kv.Key}";
                return false;
            }
            verified++;
        }

        reason = $"Verified {verified:n0} installed target record(s).";
        return true;
    }

    private static ActiveBuildSnapshot? TryGetActiveBuildSnapshot(string gameDir, Action<string>? log = null)
    {
        try
        {
            AutoRepairRegistryFromLocalManifests(gameDir, log);
            var reg = LoadRegistry(gameDir);
            var active = ObjMods(reg)
                .Where(m => IsTextureMod(m) && string.Equals(SObj(m, "status").DefaultIfEmpty("active"), "active", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => SObj(m, "mod_id"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (active.Count == 0) return null;

            var ids = active.Select(m => SObj(m, "mod_id"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var dirs = active.SelectMany(m => StringListObj(m.GetValueOrDefault("overlay_dirs")))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0 || dirs.Count == 0) return null;
            long activeRevision = RegistryActiveBuildRevision(reg);
            foreach (var od in dirs)
            {
                string full = Path.Combine(gameDir, od);
                if (!Directory.Exists(full))
                {
                    log?.Invoke($"Update source manifest ignored: active registry references missing overlay folder {od}.");
                    return null;
                }
            }
            return new ActiveBuildSnapshot { BuildIds = ids, OverlayDirs = dirs, ActiveBuildRevision = activeRevision };
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARN: could not read active build snapshot for source manifest: {ex.Message}");
            return null;
        }
    }

    private static bool SameStringSet(List<string> a, List<string> b)
    {
        a = a.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        b = b.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return a.Count == b.Count && a.Zip(b).All(x => string.Equals(x.First, x.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static SourceManifestState? TryLoadSourceManifestForUpdate(string gameDir, string sourceDir, ActiveBuildSnapshot? activeSnapshot, BuildOptions options, Action<string> log)
    {
        if (activeSnapshot == null || !activeSnapshot.IsValid)
        {
            log("Update source manifest ignored: no active managed build registry was found. A full rebuild is required.");
            return null;
        }

        string path = SourceManifestPath(gameDir, sourceDir);
        if (!File.Exists(path))
        {
            log("Update source manifest not found; changed/new DDS detection will use the active build registry only.");
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<SourceManifestState>(File.ReadAllText(path));
            if (state == null
                || state.Version != SourceManifestFormatVersion
                || !string.Equals(state.GameRootHash, GameRootCacheHash(gameDir), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.SourceRootHash, SourceRootCacheHash(sourceDir), StringComparison.OrdinalIgnoreCase))
            {
                log("Update source manifest invalid/outdated; unchanged DDS skip disabled for this run.");
                return null;
            }

            if (!SameStringSet(state.ActiveBuildIds, activeSnapshot.BuildIds)
                || !SameStringSet(state.ActiveOverlayDirs, activeSnapshot.OverlayDirs))
            {
                log("Update source manifest belongs to a different active managed build; unchanged DDS skip disabled for this source root.");
                return null;
            }

            if (!state.ActiveBuildRevision.HasValue)
            {
                log("Update source manifest is missing active build revision data; unchanged DDS skip disabled for this source root.");
                return null;
            }

            if (state.ActiveBuildRevision.Value != activeSnapshot.ActiveBuildRevision)
            {
                log($"Update source manifest stale for this source root: active build revision changed from {state.ActiveBuildRevision.Value} to {activeSnapshot.ActiveBuildRevision}. Target-centric active manifest fallback will be used when available; stale source manifest skipping is disabled.");
                return null;
            }
            if (!MatchingPolicyEquals(state.MatchingPolicy, BuildMatchingPolicy(options)))
            {
                log("Update source manifest matching policy differs from the installed build; unchanged DDS skip disabled for this source root.");
                return null;
            }
            log($"Update source manifest loaded: {state.Entries.Count:n0} source target record(s), active build revision {state.ActiveBuildRevision.Value}.");
            return state;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Update source manifest invalid/outdated; unchanged DDS skip disabled for this run. ({ex.Message})");
            return null;
        }
    }

    private static SourceManifestMatchingPolicy BuildMatchingPolicy(BuildOptions options)
        => new()
        {
            AllowUniqueFilename = options.AllowUniqueFilename,
            ApplyDuplicateLooseMatches = options.ApplyDuplicateLooseMatches,
            ScanExistingModDirs = options.ScanExistingModDirs,
            TargetPamtDir = NormalizePamt(options.TargetPamtDir),
            TargetFullPrefix = NormalizePrefix(options.TargetFullPrefix)
        };

    private static bool MatchingPolicyEquals(SourceManifestMatchingPolicy? a, SourceManifestMatchingPolicy b)
    {
        if (a == null) return false;
        return a.AllowUniqueFilename == b.AllowUniqueFilename
            && a.ApplyDuplicateLooseMatches == b.ApplyDuplicateLooseMatches
            && a.ScanExistingModDirs == b.ScanExistingModDirs
            && string.Equals(NormalizePamt(a.TargetPamtDir), NormalizePamt(b.TargetPamtDir), StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizePrefix(a.TargetFullPrefix), NormalizePrefix(b.TargetFullPrefix), StringComparison.OrdinalIgnoreCase);
    }

    private static string SourceManifestEntryKey(string relPath, string targetInternalPath)
        => OverlayBuilder.Norm(relPath) + "|" + OverlayBuilder.Norm(targetInternalPath);

    private static Dictionary<string, SourceManifestEntry> SourceManifestEntryMap(SourceManifestState state)
    {
        var map = new Dictionary<string, SourceManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in state.Entries)
        {
            string key = SourceManifestEntryKey(e.RelativePath, e.TargetInternalPath);
            if (!map.ContainsKey(key)) map[key] = e;
        }
        return map;
    }

    private static bool SourceFileMatchesManifest(MatchedFile m, SourceManifestEntry entry)
    {
        var fi = new FileInfo(m.SourcePath);
        if (!fi.Exists) return false;
        if (fi.Length != entry.Size) return false;
        if (fi.LastWriteTimeUtc.Ticks != entry.LastWriteUtcTicks) return false;
        // quick_hash is intentionally optional for RC5 UPDATE TEST3 HOTFIX4 DIAGNOSTICS.  The manifest schema
        // carries it for future stronger validation without forcing extra source IO today.
        return true;
    }

    private static bool SourceFileMatchesActiveTargetRecord(MatchedFile m, SourceManifestEntry entry, string currentSourceRootHash)
    {
        var fi = new FileInfo(m.SourcePath);
        if (!fi.Exists) return false;
        if (fi.Length != entry.Size) return false;
        if (fi.LastWriteTimeUtc.Ticks != entry.LastWriteUtcTicks) return false;

        // Target-centric active state must also know which source root produced the
        // currently installed target. This prevents a different test folder with the
        // same relative filename from being incorrectly treated as unchanged.
        if (!string.IsNullOrWhiteSpace(entry.SourceRootHash)
            && !string.Equals(entry.SourceRootHash, currentSourceRootHash, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(OverlayBuilder.Norm(entry.RelativePath), OverlayBuilder.Norm(m.RelPath), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static (List<MatchedFile> ChangedMatches, List<MatchedFile> UnchangedMatches) FilterUnchangedActiveTargetManifestMatches(
        List<MatchedFile> matches,
        SourceManifestState state,
        Dictionary<string, ExistingOverlayTarget> activeTargetIndex,
        ActiveBuildSnapshot activeSnapshot,
        string sourceDir,
        Action<string> log)
    {
        var map = SourceManifestTargetMap(state);
        var changed = new List<MatchedFile>();
        var unchanged = new List<MatchedFile>();
        var activeDirs = activeSnapshot.OverlayDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string sourceRootHash = SourceRootCacheHash(sourceDir);

        foreach (var m in matches)
        {
            string fullKey = OverlayBuilder.Norm(m.FullPath);
            if (!activeTargetIndex.TryGetValue(fullKey, out var activeTarget)
                || !map.TryGetValue(fullKey, out var entry)
                || !activeDirs.Contains(entry.OwningOverlay)
                || !string.Equals(entry.OwningOverlay, activeTarget.OverlayDir, StringComparison.OrdinalIgnoreCase)
                || !SourceFileMatchesActiveTargetRecord(m, entry, sourceRootHash))
            {
                changed.Add(m);
                continue;
            }

            unchanged.Add(m);
        }

        if (unchanged.Count > 0)
        {
            log($"Update Existing Build: skipped {unchanged.Count:n0} unchanged DDS target(s) using target-centric active manifest.");
            foreach (var m in unchanged.Take(8)) log($"  unchanged active target: {m.RelPath} -> {m.FullPath}");
            if (unchanged.Count > 8) log($"  ... {unchanged.Count - 8:n0} more unchanged target(s) skipped.");
        }
        return (changed, unchanged);
    }

    private static (List<MatchedFile> ChangedMatches, List<MatchedFile> UnchangedMatches) FilterUnchangedSourceManifestMatches(
        List<MatchedFile> matches,
        SourceManifestState state,
        Dictionary<string, ExistingOverlayTarget> activeTargetIndex,
        ActiveBuildSnapshot activeSnapshot,
        Action<string> log)
    {
        var map = SourceManifestEntryMap(state);
        var changed = new List<MatchedFile>();
        var unchanged = new List<MatchedFile>();
        var activeDirs = activeSnapshot.OverlayDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var m in matches)
        {
            string fullKey = OverlayBuilder.Norm(m.FullPath);
            if (!activeTargetIndex.TryGetValue(fullKey, out var activeTarget))
            {
                changed.Add(m);
                continue;
            }

            if (!map.TryGetValue(SourceManifestEntryKey(m.RelPath, m.FullPath), out var entry)
                || !SourceFileMatchesManifest(m, entry)
                || !activeDirs.Contains(entry.OwningOverlay)
                || !string.Equals(entry.OwningOverlay, activeTarget.OverlayDir, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(m);
                continue;
            }

            unchanged.Add(m);
        }

        if (unchanged.Count > 0)
        {
            log($"Update Existing Build: skipped {unchanged.Count:n0} unchanged DDS target(s) using app-local source manifest.");
            foreach (var m in unchanged.Take(8)) log($"  unchanged: {m.RelPath} -> {m.FullPath}");
            if (unchanged.Count > 8) log($"  ... {unchanged.Count - 8:n0} more unchanged target(s) skipped.");
        }
        return (changed, unchanged);
    }

    private static void WarnRemovedSourceFilesFromManifest(string sourceDir, SourceManifestState state, Action<string> log, CancellationToken cancellationToken)
    {
        try
        {
            var current = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Where(p => SupportedExts.Contains(Path.GetExtension(p)))
                .Select(p => OverlayBuilder.Norm(Path.GetRelativePath(sourceDir, p)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = state.Entries
                .Select(e => OverlayBuilder.Norm(e.RelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(rel => !current.Contains(rel))
                .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            if (removed.Count == 0) return;
            log($"Update Existing Build: {removed.Count:n0} previously installed source DDS file(s) are no longer present. They were reported only; no overlay cleanup was performed.");
            foreach (var rel in removed.Take(12)) log($"  removed source still installed until cleanup is requested: {rel}");
            if (removed.Count > 12) log($"  ... {removed.Count - 12:n0} more removed source file(s) not shown.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log($"WARN: could not check removed source DDS files from source manifest: {ex.Message}");
        }
    }

    private static string? OverlayOwnerForMatch(
        MatchedFile m,
        Dictionary<string, string> newOverlayOwners,
        Dictionary<string, ExistingOverlayTarget> existingUpdateTargets,
        SourceManifestState? priorState)
    {
        string full = OverlayBuilder.Norm(m.FullPath);
        if (newOverlayOwners.TryGetValue(full, out var od)) return od;
        if (existingUpdateTargets.TryGetValue(full, out var target)) return target.OverlayDir;
        if (priorState != null)
        {
            var prior = SourceManifestEntryMap(priorState);
            if (prior.TryGetValue(SourceManifestEntryKey(m.RelPath, m.FullPath), out var e) && !string.IsNullOrWhiteSpace(e.OwningOverlay)) return e.OwningOverlay;
        }
        return null;
    }

    private static void TryClearActiveTargetManifest(string gameDir, Action<string> log, string reason)
    {
        try
        {
            int deleted = 0;
            foreach (var path in ActiveTargetManifestPaths(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch { }
            }
            if (deleted > 0) log($"Active target manifest cleared: no active managed texture builds remain after {reason}.");
        }
        catch (Exception ex)
        {
            log($"WARN: active target manifest cleanup after {reason} failed: {ex.Message}");
        }
    }

    private static void TryRebaseActiveTargetManifestToInstalledTargets(string gameDir, Action<string> log, string reason)
    {
        try
        {
            var active = TryGetActiveBuildSnapshot(gameDir, log);
            if (active == null || !active.IsValid)
            {
                TryClearActiveTargetManifest(gameDir, log, reason);
                return;
            }

            string? priorPath = ActiveTargetManifestPaths(gameDir).FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(priorPath)) return;

            var prior = JsonSerializer.Deserialize<SourceManifestState>(File.ReadAllText(priorPath));
            if (prior == null
                || prior.Version != SourceManifestFormatVersion
                || !string.Equals(prior.GameRootHash, GameRootCacheHash(gameDir), StringComparison.OrdinalIgnoreCase))
            {
                log($"Active target manifest not rebased after {reason}: existing manifest is invalid/outdated.");
                return;
            }

            var activeTargetDetails = BuildManagedOverlayTargetIndex(gameDir, null);
            if (activeTargetDetails.Count == 0)
            {
                TryClearActiveTargetManifest(gameDir, log, reason);
                return;
            }

            var priorMap = SourceManifestTargetMap(prior);
            var activeOverlayDirs = active.OverlayDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rebased = new List<SourceManifestEntry>();
            foreach (var kv in activeTargetDetails.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!priorMap.TryGetValue(kv.Key, out var entry)) continue;
                if (string.IsNullOrWhiteSpace(kv.Value.OverlayDir) || !activeOverlayDirs.Contains(kv.Value.OverlayDir)) continue;
                if (!string.IsNullOrWhiteSpace(entry.OwningOverlay)
                    && !string.Equals(entry.OwningOverlay, kv.Value.OverlayDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entry.OwningOverlay = kv.Value.OverlayDir;
                entry.TargetEntryPath = kv.Value.EntryPath;
                entry.OwningPazIndex = kv.Value.PazIndex;
                entry.OwningPazOffset = kv.Value.PazOffset;
                entry.OwningPazCompSize = kv.Value.CompSize;
                rebased.Add(entry);
            }

            if (rebased.Count < activeTargetDetails.Count)
            {
                log($"WARN: active target manifest not rebased after {reason} because only {rebased.Count:n0} of {activeTargetDetails.Count:n0} currently installed target record(s) could be matched. Existing manifest was preserved.");
                return;
            }

            string now = DateTime.UtcNow.ToString("o");
            var state = new SourceManifestState
            {
                Version = SourceManifestFormatVersion,
                AppVersion = AppVersion,
                GameRootHash = GameRootCacheHash(gameDir),
                SourceRootHash = "ACTIVE_TARGET_CONTENT",
                CreatedAtUtc = prior.CreatedAtUtc.DefaultIfEmpty(now),
                UpdatedAtUtc = now,
                ActiveBuildIds = active.BuildIds,
                ActiveOverlayDirs = active.OverlayDirs,
                ActiveBuildRevision = active.ActiveBuildRevision,
                MatchingPolicy = prior.MatchingPolicy,
                Entries = rebased.OrderBy(e => e.TargetInternalPath, StringComparer.OrdinalIgnoreCase).ToList()
            };

            string path = ActiveTargetManifestPath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteJson(path, state);
            log($"Active target manifest rebased after {reason}: {state.Entries.Count:n0} currently installed target record(s), active build revision {active.ActiveBuildRevision}.");
        }
        catch (Exception ex)
        {
            log($"WARN: active target manifest rebase after {reason} failed: {ex.Message}");
        }
    }

    private static void TrySaveActiveTargetManifest(
        string gameDir,
        string sourceDir,
        List<MatchedFile> sourceMatches,
        Dictionary<string, string> newOverlayOwners,
        Dictionary<string, ExistingOverlayTarget> existingUpdateTargets,
        SourceManifestState? priorActiveTargetState,
        BuildOptions options,
        Action<string> log,
        bool mergeExisting)
    {
        try
        {
            var active = TryGetActiveBuildSnapshot(gameDir, log);
            if (active == null || !active.IsValid)
            {
                log("WARN: active target manifest not saved because no active managed build registry was found after apply.");
                return;
            }

            var activeTargetDetails = BuildManagedOverlayTargetIndex(gameDir, null);
            var map = new Dictionary<string, SourceManifestEntry>(StringComparer.OrdinalIgnoreCase);
            if (mergeExisting && priorActiveTargetState != null)
            {
                foreach (var e in priorActiveTargetState.Entries)
                {
                    string key = OverlayBuilder.Norm(e.TargetInternalPath);
                    if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key)) map[key] = e;
                }
            }

            string sourceRootHash = SourceRootCacheHash(sourceDir);
            foreach (var m in sourceMatches.OrderBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase))
            {
                string? owner = OverlayOwnerForMatch(m, newOverlayOwners, existingUpdateTargets, null);
                if (string.IsNullOrWhiteSpace(owner)) continue;
                var fi = new FileInfo(m.SourcePath);
                if (!fi.Exists) continue;
                activeTargetDetails.TryGetValue(OverlayBuilder.Norm(m.FullPath), out var savedTarget);
                if (savedTarget == null) existingUpdateTargets.TryGetValue(OverlayBuilder.Norm(m.FullPath), out savedTarget);
                var entry = new SourceManifestEntry
                {
                    RelativePath = m.RelPath,
                    SourceRootHash = sourceRootHash,
                    Size = fi.Length,
                    LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    QuickHash = "",
                    TargetInternalPath = m.FullPath,
                    TargetEntryPath = m.EntryPath,
                    TargetPamtDir = m.PamtDir,
                    OwningOverlay = owner,
                    OwningPazIndex = savedTarget?.PazIndex ?? 0,
                    OwningPazOffset = savedTarget?.PazOffset ?? 0,
                    OwningPazCompSize = savedTarget?.CompSize ?? 0,
                    MatchMethod = m.MatchMethod
                };
                string key = OverlayBuilder.Norm(entry.TargetInternalPath);
                if (!string.IsNullOrWhiteSpace(key)) map[key] = entry;
            }

            if (activeTargetDetails.Count > 0)
            {
                var filtered = new Dictionary<string, SourceManifestEntry>(StringComparer.OrdinalIgnoreCase);
                var activeOverlayDirs = active.OverlayDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in activeTargetDetails.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!map.TryGetValue(kv.Key, out var entry)) continue;
                    if (string.IsNullOrWhiteSpace(kv.Value.OverlayDir) || !activeOverlayDirs.Contains(kv.Value.OverlayDir)) continue;
                    if (!string.IsNullOrWhiteSpace(entry.OwningOverlay)
                        && !string.Equals(entry.OwningOverlay, kv.Value.OverlayDir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    entry.OwningOverlay = kv.Value.OverlayDir;
                    entry.TargetEntryPath = kv.Value.EntryPath;
                    entry.OwningPazIndex = kv.Value.PazIndex;
                    entry.OwningPazOffset = kv.Value.PazOffset;
                    entry.OwningPazCompSize = kv.Value.CompSize;
                    filtered[kv.Key] = entry;
                }

                if (options.UpdateExistingOverlays && filtered.Count < activeTargetDetails.Count)
                {
                    log($"WARN: active target manifest not saved because the rebased record set ({filtered.Count:n0}) is smaller than the installed managed target index ({activeTargetDetails.Count:n0}). Preserving the existing active target manifest to avoid partial-source overwrite.");
                    return;
                }

                map = filtered;
            }
            else if (options.UpdateExistingOverlays && map.Count < activeTargetDetails.Count)
            {
                log($"WARN: active target manifest not saved because the merged record set ({map.Count:n0}) is smaller than the installed managed target index ({activeTargetDetails.Count:n0}). Preserving the existing active target manifest to avoid partial-source overwrite.");
                return;
            }

            string path = ActiveTargetManifestPath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var now = DateTime.UtcNow.ToString("o");
            var state = new SourceManifestState
            {
                Version = SourceManifestFormatVersion,
                AppVersion = AppVersion,
                GameRootHash = GameRootCacheHash(gameDir),
                SourceRootHash = "ACTIVE_TARGET_CONTENT",
                CreatedAtUtc = priorActiveTargetState?.CreatedAtUtc.DefaultIfEmpty(now) ?? now,
                UpdatedAtUtc = now,
                ActiveBuildIds = active.BuildIds,
                ActiveOverlayDirs = active.OverlayDirs,
                ActiveBuildRevision = active.ActiveBuildRevision,
                MatchingPolicy = BuildMatchingPolicy(options),
                Entries = map.Values.OrderBy(e => e.TargetInternalPath, StringComparer.OrdinalIgnoreCase).ToList()
            };
            WriteJson(path, state);
            log($"Active target manifest saved: {state.Entries.Count:n0} installed target record(s), active build revision {active.ActiveBuildRevision}. Location: {path}");
        }
        catch (Exception ex)
        {
            log($"WARN: active target manifest could not be saved: {ex.Message}");
        }
    }

    private static void TrySaveSourceManifest(
        string gameDir,
        string sourceDir,
        List<MatchedFile> sourceMatches,
        Dictionary<string, string> newOverlayOwners,
        Dictionary<string, ExistingOverlayTarget> existingUpdateTargets,
        SourceManifestState? priorState,
        BuildOptions options,
        Action<string> log)
    {
        try
        {
            var active = TryGetActiveBuildSnapshot(gameDir, log);
            if (active == null || !active.IsValid)
            {
                log("WARN: source manifest not saved because no active managed build registry was found after apply.");
                return;
            }

            var activeTargetDetails = BuildManagedOverlayTargetIndex(gameDir, null);
            var entries = new List<SourceManifestEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in sourceMatches.OrderBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                string? owner = OverlayOwnerForMatch(m, newOverlayOwners, existingUpdateTargets, priorState);
                if (string.IsNullOrWhiteSpace(owner)) continue;
                var fi = new FileInfo(m.SourcePath);
                if (!fi.Exists) continue;
                activeTargetDetails.TryGetValue(OverlayBuilder.Norm(m.FullPath), out var savedTarget);
                if (savedTarget == null) existingUpdateTargets.TryGetValue(OverlayBuilder.Norm(m.FullPath), out savedTarget);
                var entry = new SourceManifestEntry
                {
                    RelativePath = m.RelPath,
                    SourceRootHash = SourceRootCacheHash(sourceDir),
                    Size = fi.Length,
                    LastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks,
                    QuickHash = "",
                    TargetInternalPath = m.FullPath,
                    TargetEntryPath = m.EntryPath,
                    TargetPamtDir = m.PamtDir,
                    OwningOverlay = owner,
                    OwningPazIndex = savedTarget?.PazIndex ?? 0,
                    OwningPazOffset = savedTarget?.PazOffset ?? 0,
                    OwningPazCompSize = savedTarget?.CompSize ?? 0,
                    MatchMethod = m.MatchMethod
                };
                string key = SourceManifestEntryKey(entry.RelativePath, entry.TargetInternalPath);
                if (seen.Add(key)) entries.Add(entry);
            }

            string path = SourceManifestPath(gameDir, sourceDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var now = DateTime.UtcNow.ToString("o");
            var state = new SourceManifestState
            {
                Version = SourceManifestFormatVersion,
                AppVersion = AppVersion,
                GameRootHash = GameRootCacheHash(gameDir),
                SourceRootHash = SourceRootCacheHash(sourceDir),
                CreatedAtUtc = priorState?.CreatedAtUtc.DefaultIfEmpty(now) ?? now,
                UpdatedAtUtc = now,
                ActiveBuildIds = active.BuildIds,
                ActiveOverlayDirs = active.OverlayDirs,
                ActiveBuildRevision = active.ActiveBuildRevision,
                MatchingPolicy = BuildMatchingPolicy(options),
                Entries = entries
            };
            WriteJson(path, state);
            log($"Update source manifest saved: {entries.Count:n0} source target record(s), active build revision {active.ActiveBuildRevision}. Location: {path}");
        }
        catch (Exception ex)
        {
            log($"WARN: source manifest could not be saved: {ex.Message}");
        }
    }


    private static (List<IndexCandidate> chosen, string method, List<IndexCandidate> candidates) ChooseCandidates(string relVirtual, string sourceName, PamtIndex index, bool allowUniqueFilename, bool allowDuplicateLooseMatches, string pamt, string prefix, string sourceRootArchiveScope = "")
    {
        string relOriginal = OverlayBuilder.Norm(relVirtual);
        string rel = relOriginal;
        string name = sourceName.ToLowerInvariant();
        string sourceArchive = NormalizePamt(sourceRootArchiveScope);
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && TryDetectSupportedArchiveIdInFolderName(parts[0], out string pathArchiveScope))
        {
            sourceArchive = pathArchiveScope;
            rel = string.Join('/', parts.Skip(1));
        }

        // Exact full/archive paths must stay the primary match, but Loose Duplicates should still be able
        // to add same filename targets across the user selected scope.  Beta 8/9 intentionally protected
        // exact paths, but it also made archive structured packs behave as if loose duplicate expansion was
        // disabled because the source archive (ex: 0000) became a hard filter.  For duplicate expansion,
        // use the visible target filter instead: All/no filter means all archives; PAMT 0000 means only 0000.
        string effectivePamt = !string.IsNullOrEmpty(sourceArchive) ? sourceArchive : pamt;
        string looseExpansionPamt = pamt;
        bool loose = !rel.Contains('/');
        if (!loose)
        {
            string exactMethod = !string.IsNullOrEmpty(sourceArchive) ? "exact_archive_path" : "exact_relative_path";
            var exactFull = SingleOrMulti(index.ByFull.GetValueOrDefault(rel) ?? new(), exactMethod, effectivePamt, prefix, allowMulti: false);
            if (exactFull.chosen.Count > 0)
                return ExpandExactWithLooseDuplicates(exactFull.chosen, exactMethod, name, index, allowUniqueFilename, allowDuplicateLooseMatches, looseExpansionPamt, prefix);
            if (exactFull.candidates.Count > 1) return exactFull;

            var exactFlat = SingleOrMulti(index.ByFlat.GetValueOrDefault(rel) ?? new(), "exact_flat_path", effectivePamt, prefix, allowMulti: false);
            if (exactFlat.chosen.Count > 0)
                return ExpandExactWithLooseDuplicates(exactFlat.chosen, "exact_flat_path", name, index, allowUniqueFilename, allowDuplicateLooseMatches, looseExpansionPamt, prefix);
            if (exactFlat.candidates.Count > 1) return exactFlat;

            var suffix = SingleOrMulti(index.BySuffix.GetValueOrDefault(rel) ?? new(), "suffix_path", effectivePamt, prefix, allowMulti: false);
            if (suffix.chosen.Count > 0)
                return ExpandExactWithLooseDuplicates(suffix.chosen, "suffix_path", name, index, allowUniqueFilename, allowDuplicateLooseMatches, looseExpansionPamt, prefix);
            if (suffix.candidates.Count > 1) return suffix;
        }

        if (allowUniqueFilename)
        {
            var byName = DistinctCandidates(Filter(index.ByName.GetValueOrDefault(name) ?? new(), effectivePamt, prefix));
            if (byName.Count == 1) return (byName, loose ? "loose_filename" : "filename_fallback", byName);
            if (byName.Count > 1)
            {
                if (allowDuplicateLooseMatches) return (byName, "multi_target_loose_filename", byName);

                var safePrimary = ChooseSafePrimaryCandidate(relOriginal, rel, sourceArchive, byName, pamt, prefix);
                if (safePrimary.chosen != null)
                    return (new List<IndexCandidate> { safePrimary.chosen }, safePrimary.method, byName);

                return (new List<IndexCandidate>(), safePrimary.method.DefaultIfEmpty("ambiguous_safe_primary_filename"), byName);
            }
            return (new List<IndexCandidate>(), index.ByName.ContainsKey(name) ? "filtered_out_loose_filename" : "not_found", index.ByName.GetValueOrDefault(name) ?? new());
        }
        return (new List<IndexCandidate>(), "not_found", new List<IndexCandidate>());
    }


    private static (IndexCandidate? chosen, string method) ChooseSafePrimaryCandidate(string relOriginal, string relWithoutArchive, string sourceArchive, List<IndexCandidate> candidates, string pamtFilter, string prefixFilter)
    {
        if (candidates.Count == 0) return (null, "not_found");
        string sourcePath = OverlayBuilder.Norm(relOriginal);
        string sourcePathNoArchive = OverlayBuilder.Norm(relWithoutArchive);
        var sourceParts = sourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToList();
        var sourcePartsNoArchive = sourcePathNoArchive.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToList();
        bool hasSourcePathEvidence = sourcePartsNoArchive.Count > 1 || !string.IsNullOrEmpty(sourceArchive);
        bool userScoped = !string.IsNullOrEmpty(NormalizePamt(pamtFilter)) || !string.IsNullOrEmpty(NormalizePrefix(prefixFilter));

        var scored = candidates
            .Select(c => new { Candidate = c, Score = SafePrimaryScore(c, sourcePath, sourcePathNoArchive, sourceArchive, sourceParts, sourcePartsNoArchive, hasSourcePathEvidence, userScoped) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => CategoryRiskRank(x.Candidate))
            .ThenBy(x => x.Candidate.PamtDir, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => OverlayBuilder.Norm(x.Candidate.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var best = scored[0];
        int secondScore = scored.Count > 1 ? scored[1].Score : int.MinValue;
        bool clearByScore = best.Score - secondScore >= 25;
        bool clearByPath = hasSourcePathEvidence && best.Score > secondScore;
        bool clearByScope = userScoped && best.Score >= secondScore;
        bool clearByCanonical = !hasSourcePathEvidence && !IsHighRiskCandidate(best.Candidate);

        if (clearByScore || clearByPath || clearByScope || clearByCanonical)
        {
            string method = hasSourcePathEvidence
                ? "safe_primary_path_filename"
                : (userScoped ? "safe_primary_scoped_filename" : "safe_primary_canonical_filename");
            return (best.Candidate, method);
        }

        return (null, "ambiguous_safe_primary_filename");
    }

    private static int SafePrimaryScore(IndexCandidate c, string sourcePath, string sourcePathNoArchive, string sourceArchive, List<string> sourceParts, List<string> sourcePartsNoArchive, bool hasSourcePathEvidence, bool userScoped)
    {
        string full = OverlayBuilder.Norm(c.FullPath).ToLowerInvariant();
        string entry = OverlayBuilder.Norm(c.EntryPath).ToLowerInvariant();
        string parent = ParentFamily(full).ToLowerInvariant();
        int score = 0;

        if (!string.IsNullOrEmpty(sourceArchive) && string.Equals(c.PamtDir, sourceArchive, StringComparison.OrdinalIgnoreCase)) score += 500;
        if (!string.IsNullOrEmpty(sourcePathNoArchive))
        {
            if (string.Equals(full, sourcePathNoArchive, StringComparison.OrdinalIgnoreCase)) score += 500;
            if (string.Equals(entry, sourcePathNoArchive, StringComparison.OrdinalIgnoreCase)) score += 450;
            if (full.EndsWith("/" + sourcePathNoArchive, StringComparison.OrdinalIgnoreCase)) score += 250;
            if (entry.EndsWith("/" + sourcePathNoArchive, StringComparison.OrdinalIgnoreCase)) score += 220;
        }

        if (sourcePartsNoArchive.Count > 1)
        {
            string sourceParent = string.Join('/', sourcePartsNoArchive.Take(sourcePartsNoArchive.Count - 1));
            if (string.Equals(parent, sourceParent, StringComparison.OrdinalIgnoreCase)) score += 240;
            if (parent.EndsWith("/" + sourceParent, StringComparison.OrdinalIgnoreCase)) score += 160;

            foreach (var token in sourcePartsNoArchive.Take(sourcePartsNoArchive.Count - 1).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (token.Length < 3) continue;
                if (full.Contains("/" + token + "/", StringComparison.OrdinalIgnoreCase) || full.StartsWith(token + "/", StringComparison.OrdinalIgnoreCase)) score += 18;
            }
        }

        if (!hasSourcePathEvidence)
        {
            // Flat source folders have no archive/path proof. Pick a single stable canonical target instead of fanning out globally.
            // Bias toward generic/world/object archives and away from high-risk character/creature targets unless the source path proved them.
            score += CanonicalArchiveScore(c.PamtDir);
            if (full.Contains("object/texture/", StringComparison.OrdinalIgnoreCase)) score += 120;
            if (full.Contains("tree/", StringComparison.OrdinalIgnoreCase)) score += 95;
            if (full.Contains("texture/", StringComparison.OrdinalIgnoreCase)) score += 80;
            if (IsHighRiskCandidate(c)) score -= 180;
        }
        else if (IsHighRiskCandidate(c) && !SourceSuggestsHighRisk(sourceParts))
        {
            score -= 40;
        }

        if (userScoped) score += 20;
        return score;
    }

    private static int CanonicalArchiveScore(string pamtDir)
    {
        return NormalizePamt(pamtDir) switch
        {
            "0000" => 220,
            "0001" => 200,
            "0002" => 180,
            "0007" => 150,
            "0012" => 130,
            "0015" => 120,
            "0009" => 40,
            _ => 80
        };
    }

    private static int CategoryRiskRank(IndexCandidate c) => IsHighRiskCandidate(c) ? 1 : 0;

    private static bool IsHighRiskCandidate(IndexCandidate c)
    {
        string p = (c.PamtDir + "/" + OverlayBuilder.Norm(c.FullPath)).ToLowerInvariant();
        return p.Contains("0009/")
            || p.Contains("character/")
            || p.Contains("monster/")
            || p.Contains("creature/")
            || p.Contains("/face")
            || p.Contains("face/")
            || p.Contains("/body")
            || p.Contains("body/")
            || p.Contains("bear");
    }

    private static bool SourceSuggestsHighRisk(List<string> sourceParts)
    {
        string s = string.Join('/', sourceParts).ToLowerInvariant();
        return s.Contains("0009/")
            || s.Contains("character/")
            || s.Contains("monster/")
            || s.Contains("creature/")
            || s.Contains("face")
            || s.Contains("body")
            || s.Contains("bear");
    }

    private static (List<IndexCandidate> chosen, string method, List<IndexCandidate> candidates) ExpandExactWithLooseDuplicates(List<IndexCandidate> primary, string primaryMethod, string name, PamtIndex index, bool allowUniqueFilename, bool allowDuplicateLooseMatches, string pamt, string prefix)
    {
        if (!allowUniqueFilename || !allowDuplicateLooseMatches)
            return (primary, primaryMethod, primary);

        var byName = DistinctCandidates(Filter(index.ByName.GetValueOrDefault(name) ?? new(), pamt, prefix));
        if (byName.Count <= primary.Count)
            return (primary, primaryMethod, primary);

        var combined = primary
            .Concat(byName)
            .GroupBy(c => (c.PamtDir, OverlayBuilder.Norm(c.FullPath), c.Filename.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(c => primary.Any(p => p.PamtDir == c.PamtDir && OverlayBuilder.Norm(p.FullPath) == OverlayBuilder.Norm(c.FullPath)) ? 1 : 0).First())
            .OrderBy(c => c.PamtDir)
            .ThenBy(c => OverlayBuilder.Norm(c.FullPath), StringComparer.Ordinal)
            .ToList();

        if (combined.Count > primary.Count)
            return (combined, primaryMethod + "+loose_duplicates", combined);

        return (primary, primaryMethod, primary);
    }

    private static List<IndexCandidate> DistinctCandidates(List<IndexCandidate> raw)
    {
        return raw
            .GroupBy(c => (c.PamtDir, OverlayBuilder.Norm(c.FullPath), c.Filename.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderBy(c => c.PamtDir)
            .ThenBy(c => OverlayBuilder.Norm(c.FullPath), StringComparer.Ordinal)
            .ToList();
    }

    private static (List<IndexCandidate> chosen, string method, List<IndexCandidate> candidates) SingleOrMulti(List<IndexCandidate> raw, string method, string pamt, string prefix, bool allowMulti)
    {
        var list = Filter(raw, pamt, prefix)
            .GroupBy(c => (c.PamtDir, OverlayBuilder.Norm(c.FullPath), c.Filename.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderBy(c => c.PamtDir).ThenBy(c => OverlayBuilder.Norm(c.FullPath), StringComparer.Ordinal)
            .ToList();
        if (list.Count == 1) return (list, method, list);
        if (list.Count > 1)
        {
            if (allowMulti) return (list, method, list);
            return (new List<IndexCandidate>(), "ambiguous_" + method, list);
        }
        return (new List<IndexCandidate>(), raw.Count > 0 ? "filtered_out_" + method : "not_found", raw);
    }

    private static List<IndexCandidate> Filter(List<IndexCandidate> raw, string pamt, string prefix)
    {
        pamt = NormalizePamt(pamt);
        prefix = NormalizePrefix(prefix);
        if (string.IsNullOrEmpty(pamt) && string.IsNullOrEmpty(prefix)) return raw;
        string ps = string.IsNullOrEmpty(prefix) ? "" : prefix + "/";
        return raw.Where(c => (string.IsNullOrEmpty(pamt) || c.PamtDir == pamt) && (string.IsNullOrEmpty(prefix) || OverlayBuilder.Norm(c.FullPath) == prefix || OverlayBuilder.Norm(c.FullPath).StartsWith(ps) || OverlayBuilder.Norm(c.EntryPath) == prefix || OverlayBuilder.Norm(c.EntryPath).StartsWith(ps))).ToList();
    }

    private static List<List<MatchedFile>> SplitMatches(List<MatchedFile> matches, double splitGb)
    {
        double target = Math.Max(0.5, splitGb) * 1024 * 1024 * 1024;
        var chunks = new List<List<MatchedFile>>(); var cur = new List<MatchedFile>(); long curSize = 0;
        foreach (var m in matches)
        {
            long est = m.Size + 65536; long rem = est % 16; if (rem != 0) est += 16 - rem;
            if (cur.Count > 0 && curSize + est > target) { chunks.Add(cur); cur = new(); curSize = 0; }
            cur.Add(m); curSize += est;
        }
        if (cur.Count > 0) chunks.Add(cur);
        return chunks;
    }

    private static List<string> AllocateOverlayDirs(string gameDir, int count, string? outputDir)
    {
        // v1.3.1 RC6: generated Overlay Builder archives use clear HD## names
        // (HD01, HD02, ...) for both apply to game and build only output.
        // Build only output is isolated to the new build folder, so it starts at
        // HD01 there. Apply to game skips any existing HD## folder in the game dir.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseDirs = string.IsNullOrEmpty(outputDir)
            ? new[] { gameDir }
            : new[] { outputDir };

        foreach (var baseDir in baseDirs)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;
            foreach (var d in Directory.EnumerateDirectories(baseDir))
                used.Add(Path.GetFileName(d) ?? string.Empty);
        }

        var result = new List<string>();
        for (int i = 1; i <= 99 && result.Count < count; i++)
        {
            string name = $"{ManagedOverlayPrefix}{i:00}";
            if (used.Contains(name)) continue;
            used.Add(name);
            result.Add(name);
        }
        if (result.Count < count) throw new InvalidOperationException("Not enough free HD## overlay folder names are available. Remove the current HD Overlay Builder build or clear old HD## folders first.");
        return result;
    }

    public static string? DetectGameDir()
    {
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName))
        {
            candidates.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Crimson Desert"));
            candidates.Add(Path.Combine(drive, "Steam", "steamapps", "common", "Crimson Desert"));
            candidates.Add(Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common", "Crimson Desert"));
        }
        return candidates.FirstOrDefault(IsGameDir);
    }

    public static string? FindLatestMetaBackup(string gameDir)
    {
        foreach (var root in RegistryRoots(gameDir).Select(r => Path.Combine(r, "backups")).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            var hit = Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, "meta", "0.papgt"))).OrderBy(x => x).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(hit)) return hit;
        }
        return null;
    }

    private static readonly string[] RestorableMetaBackupFiles =
    {
        Path.Combine("meta", "0.papgt"),
        Path.Combine("meta", "0.pathc")
    };

    private static readonly string[] CapturedMetaBackupFiles =
    {
        Path.Combine("meta", "0.papgt"),
        Path.Combine("meta", "0.pathc"),
        Path.Combine("meta", "0.paver")
    };

    private const string MetaBackupInfoFileName = "HDOB_META_BACKUP_INFO.json";
    private const string ActiveBaseInfoFileName = "HDOB_ACTIVE_BASE_INFO.json";

    public static bool RestoreMetaFromBackup(string gameDir, string backupDir, Action<string> log, bool allowStaleBackup = false)
    {
        if (!allowStaleBackup && IsMetaBackupLikelyFromOlderGamePatch(gameDir, backupDir, log))
        {
            log($"WARN: skipped restoring stale meta backup from an older game patch: {backupDir}");
            log("WARN: current game meta was left in place so an old patch backup is not written over the current game installation.");
            return false;
        }

        int restored = 0;
        foreach (var rel in RestorableMetaBackupFiles)
        {
            string src = Path.Combine(backupDir, rel);
            string dst = Path.Combine(gameDir, rel);
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
                restored++;
                log($"Restored {rel} from {backupDir}");
            }
        }
        if (restored == 0) throw new FileNotFoundException("The backup does not contain meta/0.papgt or meta/0.pathc.");
        return true;
    }

    public static string CopyCurrentMetaBackup(string gameDir, Action<string> log, string purpose = "HD Overlay Builder meta backup compatibility guard")
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupDir = Path.Combine(RegistryRoot(gameDir), "backups", stamp);
        int suffix = 1;
        while (Directory.Exists(backupDir))
            backupDir = Path.Combine(RegistryRoot(gameDir), "backups", $"{stamp}_{suffix++:00}");
        Directory.CreateDirectory(Path.Combine(backupDir, "meta"));
        foreach (var rel in CapturedMetaBackupFiles)
        {
            string src = Path.Combine(gameDir, rel);
            if (File.Exists(src)) File.Copy(src, Path.Combine(backupDir, rel), true);
        }
        WriteMetaBackupInfo(gameDir, backupDir, purpose, log);
        log($"Meta backup created: {backupDir}");
        return backupDir;
    }

    private static string CopyPathcReplaySnapshot(string gameDir, string purpose, Action<string> log)
    {
        try
        {
            string src = Path.Combine(gameDir, "meta", "0.pathc");
            if (!File.Exists(src))
            {
                log("WARN: PATHC replay snapshot skipped because meta/0.pathc does not exist.");
                return string.Empty;
            }
            string safePurpose = SafeName(string.IsNullOrWhiteSpace(purpose) ? "pathc" : purpose);
            string root = Path.Combine(RegistryRoot(gameDir), "pathc_replay_snapshots", safePurpose + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(root);
            string dst = Path.Combine(root, "0.pathc");
            File.Copy(src, dst, true);
            log($"PATHC replay snapshot saved: {dst}");
            return dst;
        }
        catch (Exception ex)
        {
            log($"WARN: could not save PATHC replay snapshot: {ex.Message}");
            return string.Empty;
        }
    }

    private static List<PathcFile> LoadPathcReplaySources(IEnumerable<string> paths, Action<string> log, string label)
    {
        var result = new List<PathcFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            string path = raw;
            try { path = Path.GetFullPath(raw); } catch { }
            if (!seen.Add(path)) continue;
            if (!File.Exists(path))
            {
                log($"WARN: {label} PATHC replay snapshot not found: {path}");
                continue;
            }
            try
            {
                result.Add(PathcFile.Read(path));
            }
            catch (Exception ex)
            {
                log($"WARN: {label} PATHC replay snapshot could not be read: {path}: {ex.Message}");
            }
        }
        if (result.Count > 0) log($"{label}: loaded {result.Count} PATHC replay snapshot(s) for source-independent replay.");
        return result;
    }

    private static void WriteMetaBackupInfo(string gameDir, string backupDir, string purpose, Action<string> log)
    {
        try
        {
            var files = new List<Dictionary<string, object?>>();
            foreach (var rel in CapturedMetaBackupFiles)
            {
                string path = Path.Combine(gameDir, rel);
                if (!File.Exists(path)) continue;
                var fi = new FileInfo(path);
                files.Add(new Dictionary<string, object?>
                {
                    ["relative_file"] = rel.Replace('\\', '/'),
                    ["size_bytes"] = fi.Length,
                    ["modified_utc"] = fi.LastWriteTimeUtc.ToString("o"),
                    ["sha256"] = Sha256File(path)
                });
            }

            WriteJson(Path.Combine(backupDir, MetaBackupInfoFileName), new
            {
                app = AppName,
                version = AppVersion,
                created_at_utc = DateTime.UtcNow.ToString("o"),
                game_dir = gameDir,
                purpose = purpose,
                files = files
            });
        }
        catch (Exception ex)
        {
            log($"WARN: could not write meta backup info: {ex.Message}");
        }
    }

    private static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static bool IsMetaBackupLikelyFromOlderGamePatch(string gameDir, string backupDir, Action<string> log)
    {
        try
        {
            string currentPaver = Path.Combine(gameDir, "meta", "0.paver");
            string backupPaver = Path.Combine(backupDir, "meta", "0.paver");

            // Newer backups capture meta/0.paver as a direct game-patch marker.
            // If the backup's paver differs from the current game's paver, the backup
            // belongs to another game patch and must not be restored over the live install.
            if (File.Exists(currentPaver) && File.Exists(backupPaver))
            {
                var c = new FileInfo(currentPaver);
                var b = new FileInfo(backupPaver);
                if (c.Length != b.Length || !string.Equals(Sha256File(currentPaver), Sha256File(backupPaver), StringComparison.OrdinalIgnoreCase))
                {
                    log("WARN: meta backup guard detected a different meta/0.paver than the current game patch.");
                    return true;
                }
                return false;
            }

            // Older backups did not include 0.paver. For those, use the backup folder
            // timestamp against the latest stock meta/PAMT timestamp. This prevents an
            // old v1.12 backup from being restored after the game has moved to v1.13+.
            DateTime? backupUtc = TryGetBackupFolderTimestampUtc(backupDir);
            DateTime? currentBasisUtc = LatestCurrentGameBasisTimestampUtc(gameDir);
            if (backupUtc.HasValue && currentBasisUtc.HasValue && backupUtc.Value.AddMinutes(2) < currentBasisUtc.Value)
            {
                log($"WARN: meta backup guard detected backup timestamp {backupUtc.Value:o} older than current game basis {currentBasisUtc.Value:o}.");
                return true;
            }
        }
        catch (Exception ex)
        {
            log($"WARN: meta backup guard could not fully validate backup age ({ex.Message}); restore will continue.");
        }
        return false;
    }

    private static DateTime? TryGetBackupFolderTimestampUtc(string backupDir)
    {
        string name = Path.GetFileName(backupDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string timestampPart = name.Length >= 15 ? name[..15] : name;
        if (DateTime.TryParseExact(timestampPart, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return local.ToUniversalTime();
        }
        try
        {
            if (Directory.Exists(backupDir)) return Directory.GetCreationTimeUtc(backupDir);
        }
        catch { }
        return null;
    }

    private static DateTime? LatestCurrentGameBasisTimestampUtc(string gameDir)
    {
        DateTime latest = DateTime.MinValue;
        foreach (var path in EnumerateCurrentGameBasisFiles(gameDir))
        {
            try
            {
                var t = File.GetLastWriteTimeUtc(path);
                if (t > latest) latest = t;
            }
            catch { }
        }
        return latest == DateTime.MinValue ? null : latest;
    }

    private static IEnumerable<string> EnumerateCurrentGameBasisFiles(string gameDir)
    {
        foreach (var rel in CapturedMetaBackupFiles)
        {
            string p = Path.Combine(gameDir, rel);
            if (File.Exists(p)) yield return p;
        }

        foreach (var dir in Directory.EnumerateDirectories(gameDir).Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}$")))
        {
            string pamt = Path.Combine(dir, "0.pamt");
            if (File.Exists(pamt)) yield return pamt;
        }
    }
    private static void WriteManagedOverlayMarker(string overlayDirPath, string overlayDir, BuildOptions options)
    {
        try
        {
            string text = string.Join(Environment.NewLine, new[]
            {
                AppName,
                $"Version: {AppVersion}",
                $"Managed overlay: {overlayDir}",
                $"Mod name: {options.ModName}",
                $"Created: {DateTime.Now:s}",
                "This folder was generated by HD Overlay Builder."
            }) + Environment.NewLine;
            File.WriteAllText(Path.Combine(overlayDirPath, ManagedOverlayMarkerFile), text, Encoding.UTF8);
        }
        catch
        {
            // Marker files are helpful for humans but not required for the game to load.
        }
    }


    private static void EnsureHotfixManifestCancelBackup(string manifestPath, string backupDir, Action<string> log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return;
            string manifestBackupDir = Path.Combine(backupDir, "__manifest_backup");
            Directory.CreateDirectory(manifestBackupDir);
            string backupFile = Path.Combine(manifestBackupDir, "manifest.json");
            string pathFile = Path.Combine(manifestBackupDir, "manifest_path.txt");
            if (File.Exists(backupFile) && File.Exists(pathFile)) return;
            File.Copy(manifestPath, backupFile, true);
            File.WriteAllText(pathFile, manifestPath, Encoding.UTF8);
            log("Cancel safety: backed up active build manifest before hotfix edits.");
        }
        catch (Exception ex)
        {
            log($"WARN: could not back up active build manifest before hotfix: {ex.Message}");
        }
    }

    private static void RestoreHotfixManifestCancelBackup(string backupDir, Action<string> log)
    {
        try
        {
            string manifestBackupDir = Path.Combine(backupDir, "__manifest_backup");
            string backupFile = Path.Combine(manifestBackupDir, "manifest.json");
            string pathFile = Path.Combine(manifestBackupDir, "manifest_path.txt");
            if (!File.Exists(backupFile) || !File.Exists(pathFile)) return;
            string manifestPath = File.ReadAllText(pathFile, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(manifestPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.Copy(backupFile, manifestPath, true);
            log("Cancel cleanup: restored active build manifest from hotfix safety backup.");
        }
        catch (Exception ex)
        {
            log($"WARN: cancel cleanup could not restore active build manifest backup: {ex.Message}");
        }
    }

    private static void EnsureHotfixCancelBackup(string gameDir, string overlayDir, Dictionary<string, string> hotfixOverlayBackups, Action<string> log)
    {
        string backupDir = EnsureHotfixBackupRoot(gameDir, overlayDir, hotfixOverlayBackups);
        int copied = 0;
        string srcDir = Path.Combine(gameDir, overlayDir);
        string pamt = Path.Combine(srcDir, "0.pamt");
        if (File.Exists(pamt))
        {
            File.Copy(pamt, Path.Combine(backupDir, "0.pamt"), true);
            copied++;
        }
        foreach (var paz in Directory.Exists(srcDir) ? Directory.EnumerateFiles(srcDir, "*.paz") : Enumerable.Empty<string>())
        {
            File.Copy(paz, Path.Combine(backupDir, Path.GetFileName(paz)), true);
            copied++;
        }
        log(copied > 0
            ? $"Cancel safety: backed up existing overlay {overlayDir} ({copied} PAZ/PAMT file(s))."
            : $"Cancel safety: existing overlay {overlayDir} had no PAZ/PAMT files to back up.");
    }

    private static void EnsureHotfixPartCancelBackup(string gameDir, string overlayDir, IEnumerable<uint> pazIndices, Dictionary<string, string> hotfixOverlayBackups, Action<string> log)
    {
        string backupDir = EnsureHotfixBackupRoot(gameDir, overlayDir, hotfixOverlayBackups);
        string srcDir = Path.Combine(gameDir, overlayDir);
        int copied = 0;
        string pamt = Path.Combine(srcDir, "0.pamt");
        string pamtBackup = Path.Combine(backupDir, "0.pamt");
        if (File.Exists(pamt) && !File.Exists(pamtBackup))
        {
            File.Copy(pamt, pamtBackup, true);
            copied++;
        }
        foreach (uint idx in pazIndices.Distinct())
        {
            string name = $"{idx}.paz";
            string src = Path.Combine(srcDir, name);
            string dst = Path.Combine(backupDir, name);
            if (!File.Exists(src) || File.Exists(dst)) continue;
            File.Copy(src, dst, true);
            copied++;
        }
        log(copied > 0
            ? $"Cancel safety: backed up {overlayDir} PAZ part(s) {string.Join(", ", pazIndices.Distinct().OrderBy(x => x))} plus PAMT metadata."
            : $"Cancel safety: {overlayDir} affected PAZ part backup already existed.");
    }

    private static string EnsureHotfixBackupRoot(string gameDir, string overlayDir, Dictionary<string, string> hotfixOverlayBackups)
    {
        if (hotfixOverlayBackups.TryGetValue(overlayDir, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            Directory.CreateDirectory(existing);
            return existing;
        }
        string backupRoot = Path.Combine(RegistryRoot(gameDir), "cancel_hotfix_backups", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        string backupDir = Path.Combine(backupRoot, overlayDir);
        Directory.CreateDirectory(backupDir);
        hotfixOverlayBackups[overlayDir] = backupDir;
        return backupDir;
    }

    private static void RestoreHotfixCancelBackups(string gameDir, Dictionary<string, string> hotfixOverlayBackups, Action<string> log)
    {
        foreach (var kv in hotfixOverlayBackups.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string od = kv.Key;
            string backupDir = kv.Value;
            try
            {
                string targetDir = Path.Combine(gameDir, od);
                Directory.CreateDirectory(targetDir);
                RestoreHotfixManifestCancelBackup(backupDir, log);
                int restored = 0;
                foreach (var src in Directory.Exists(backupDir) ? Directory.EnumerateFiles(backupDir) : Enumerable.Empty<string>())
                {
                    string name = Path.GetFileName(src);
                    if (!string.Equals(name, "0.pamt", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".paz", StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(src, Path.Combine(targetDir, name), true);
                    restored++;
                }
                log(restored > 0
                    ? $"Cancel cleanup: restored existing overlay {od} from safety backup."
                    : $"Cancel cleanup: no files were present in the safety backup for existing overlay {od}.");
            }
            catch (Exception ex)
            {
                log($"WARN: cancel cleanup could not restore existing overlay {od}: {ex.Message}");
            }
        }
        DeleteHotfixCancelBackups(hotfixOverlayBackups, log);
    }

    private static void DeleteHotfixCancelBackups(Dictionary<string, string> hotfixOverlayBackups, Action<string> log)
    {
        var roots = hotfixOverlayBackups.Values
            .Select(v => Directory.GetParent(v)?.FullName)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var root in roots)
        {
            try
            {
                if (root != null && Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception ex)
            {
                log($"WARN: could not delete cancel safety backup folder: {ex.Message}");
            }
        }
        hotfixOverlayBackups.Clear();
    }

    private static void CleanupCancelledBuild(string gameDir, string buildRoot, IEnumerable<string> createdNewOverlayDirs, Dictionary<string, string> hotfixOverlayBackups, string? backupDir, bool applyToGame, Action<string> log)
    {
        log("Cancel cleanup: reverting partial build state...");

        if (applyToGame)
        {
            RestoreHotfixCancelBackups(gameDir, hotfixOverlayBackups, log);

            foreach (var od in createdNewOverlayDirs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string overlayDir = Path.Combine(gameDir, od);
                try
                {
                    if (Directory.Exists(overlayDir))
                    {
                        Directory.Delete(overlayDir, true);
                        log($"Cancel cleanup: deleted partial overlay folder {od}.");
                    }
                }
                catch (Exception ex)
                {
                    log($"WARN: cancel cleanup could not delete partial overlay folder {od}: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(backupDir) && Directory.Exists(backupDir))
            {
                try
                {
                    RestoreMetaFromBackup(gameDir, backupDir, log);
                    log("Cancel cleanup: restored meta/0.papgt and meta/0.pathc from the backup from before the build.");
                    RestoreRegistryStateBackup(gameDir, backupDir, log);
                }
                catch (Exception ex)
                {
                    log($"WARN: cancel cleanup could not restore meta backup: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    byte[] papgt = new PapgtManager(gameDir).Rebuild(new Dictionary<string, byte[]>());
                    SafeWrite(Path.Combine(gameDir, "meta", "0.papgt"), papgt);
                    log("Cancel cleanup: no backup was available, so meta/0.papgt was rebuilt after deleting partial overlays. meta/0.pathc was not changed.");
                }
                catch (Exception ex)
                {
                    log($"WARN: cancel cleanup could not rebuild PAPGT: {ex.Message}");
                }
            }
        }

        try
        {
            if (Directory.Exists(buildRoot))
            {
                Directory.Delete(buildRoot, true);
                log("Cancel cleanup: deleted partial build output folder.");
            }
        }
        catch (Exception ex)
        {
            log($"WARN: cancel cleanup could not delete partial build output folder: {ex.Message}");
        }
    }


    private static void WriteIncompleteOverlayMarker(string overlayDir, string overlayName)
    {
        try
        {
            File.WriteAllText(Path.Combine(overlayDir, IncompleteOverlayMarkerFile), $"Incomplete HD Overlay Builder output. Overlay={overlayName}. Created={DateTime.Now:s}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    private static void ClearIncompleteOverlayMarker(string overlayDir)
    {
        try
        {
            string marker = Path.Combine(overlayDir, IncompleteOverlayMarkerFile);
            if (File.Exists(marker)) File.Delete(marker);
        }
        catch { }
    }

    private static bool HasIncompleteOverlayMarker(string overlayDir) => File.Exists(Path.Combine(overlayDir, IncompleteOverlayMarkerFile));

    public static List<string> FindIncompleteBuildArtifacts(string gameDir)
    {
        var result = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return result;
            var activeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var snapshot = TryGetActiveBuildSnapshot(gameDir, null);
                if (snapshot != null && snapshot.IsValid)
                    foreach (var od in snapshot.OverlayDirs) activeDirs.Add(Path.GetFullPath(Path.Combine(gameDir, od)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch { }
            foreach (var dir in Directory.EnumerateDirectories(gameDir, "HD??", SearchOption.TopDirectoryOnly))
            {
                string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (activeDirs.Contains(full)) continue;
                if (HasIncompleteOverlayMarker(dir)) result.Add(dir);
            }
        }
        catch { }
        return result;
    }

    public static int CleanupIncompleteBuildArtifacts(string gameDir, Action<string> log)
    {
        int deleted = 0;
        foreach (var dir in FindIncompleteBuildArtifacts(gameDir))
        {
            try
            {
                if (LooksLikeToolOwnedOverlayFolder(dir) || HasIncompleteOverlayMarker(dir))
                {
                    Directory.Delete(dir, true);
                    deleted++;
                    log($"Startup cleanup: deleted incomplete overlay folder {dir}.");
                }
            }
            catch (Exception ex) { log($"WARN: startup cleanup could not delete incomplete overlay folder {dir}: {ex.Message}"); }
        }
        try
        {
            string builds = BuildOutputRoot(gameDir);
            if (Directory.Exists(builds))
            {
                foreach (var dir in Directory.EnumerateDirectories(builds).Where(d => File.Exists(Path.Combine(d, ".incomplete_build"))).ToList())
                {
                    Directory.Delete(dir, true);
                    deleted++;
                    log($"Startup cleanup: deleted incomplete build output {dir}.");
                }
            }
        }
        catch (Exception ex) { log($"WARN: startup cleanup could not scan incomplete build outputs: {ex.Message}"); }
        return deleted;
    }

    private static string CopyRegistryStateBackup(string gameDir, string backupDir, Action<string>? log)
    {
        string root = Path.Combine(backupDir, "registry_state");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var registryRoot in RegistryRoots(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(registryRoot)) continue;

                // Do not copy the destination backup tree back into itself.  The
                // current tool data root owns its own backups folder, and this
                // registry/state backup is also created under that same tree.
                // Copy only the live state content needed for cancel/restore
                // safety and skip nested backups/temp rollback state.
                CopyRegistryStateDirectory(registryRoot, Path.Combine(root, Path.GetFileName(registryRoot)));
            }
            log?.Invoke($"Registry/state backup created: {root}");
        }
        catch (Exception ex) { log?.Invoke($"WARN: could not backup registry/state: {ex.Message}"); }
        return root;
    }

    private static void RestoreRegistryStateBackup(string gameDir, string backupDir, Action<string>? log)
    {
        string stagingRoot = string.Empty;
        try
        {
            string root = Path.Combine(backupDir, "registry_state");
            if (!Directory.Exists(root)) return;

            // The backup itself lives under HDOverlayBuilder\backups. Stage it
            // outside the managed registry roots before deleting/replacing those
            // roots, otherwise restoring HDOverlayBuilder would delete the source
            // backup tree before it can be copied back.
            stagingRoot = Path.Combine(Path.GetTempPath(), "HDOB_registry_restore_" + Guid.NewGuid().ToString("N"));
            CopyDirectory(root, stagingRoot);

            foreach (var folderName in new[] { ToolDataFolderName, PreviousToolDataFolderName, LegacyToolDataFolderName }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string src = Path.Combine(stagingRoot, folderName);
                if (!Directory.Exists(src)) continue;
                string dst = Path.Combine(gameDir, folderName);
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                CopyDirectory(src, dst);
            }
            log?.Invoke("Registry/state restored from pre-operation backup.");
        }
        catch (Exception ex) { log?.Invoke($"WARN: could not restore registry/state backup: {ex.Message}"); }
        finally
        {
            try { if (!string.IsNullOrWhiteSpace(stagingRoot) && Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); } catch { }
        }
    }

    public static BuildResult RelinkOverlaysAfterGameUpdate(string gameDir, Action<string> log, Action<int, string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsGameDir(gameDir)) throw new InvalidOperationException("The selected game folder does not look valid. Relink requires updated game meta files and stock PAMT folders.");
        var start = DateTime.UtcNow;
        progress?.Invoke(2, "RELINK: LOAD REGISTRY");
        log("===== RELINK OVERLAYS AFTER GAME UPDATE =====");
        log("Relink uses the current updated game meta files as the new base. It does not rebuild or repack DDS/PAZ/PAMT archives.");
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var reg = LoadRegistry(gameDir);
        var mods = ObjMods(reg).Where(m => IsTextureMod(m) && string.Equals(SObj(m, "status").DefaultIfEmpty("active"), "active", StringComparison.OrdinalIgnoreCase)).OrderBy(m => SObj(m, "created_at")).ToList();
        if (mods.Count == 0) throw new InvalidOperationException("No active managed HD overlay build registry was found. Use Easy Apply for a full rebuild.");

        // Relink may see the same historical target in more than one active
        // manifest after Update Existing Build rebuilt an already-owned HD##
        // overlay. Canonicalize those records before PATHC replay so duplicate
        // history cannot make a valid overlay entry look ambiguous/non-editable.
        var canonicalMatchesByTarget = new Dictionary<string, (int ManifestOrder, long Sequence, MatchedFile Match)>(StringComparer.OrdinalIgnoreCase);
        var canonicalEntriesByOwnerTarget = new Dictionary<string, (int ManifestOrder, long Sequence, string TargetKey, OverlayEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        var modifiedPamts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var validatedOverlays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string relinkReportPath = Path.Combine(RegistryRoot(gameDir), "relink_reports", $"relink_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var pathcReplaySnapshotPaths = new List<string>();
        int rawMatchRecordCount = 0;
        int rawOverlayEntryRecordCount = 0;
        int duplicateMatchRecordsCollapsed = 0;
        int duplicateSameOwnerEntryRecordsCollapsed = 0;
        int manifestOrder = 0;
        long canonicalSequence = 0;

        progress?.Invoke(10, "RELINK: VALIDATE OVERLAYS");
        foreach (var mod in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifestOrder++;
            string mp = SObj(mod, "manifest_copy");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) mp = SObj(mod, "original_manifest");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) throw new InvalidOperationException($"Active build {SObj(mod, "mod_id")} has no readable manifest. Full Easy Apply is required.");
            var manifestInfo = ReadJsonDict(mp);
            if (manifestInfo != null)
            {
                string snap = SObj(manifestInfo, "pathc_replay_snapshot");
                if (!string.IsNullOrWhiteSpace(snap)) pathcReplaySnapshotPaths.Add(snap);
            }
            string heldSnap = SObj(mod, "pathc_hold_snapshot");
            if (!string.IsNullOrWhiteSpace(heldSnap)) pathcReplaySnapshotPaths.Add(heldSnap);
            var matches = ReadManifestMatches(mp);
            var entries = ReadManifestOverlayEntries(mp);
            if (matches.Count == 0 || entries.Count == 0) throw new InvalidOperationException($"Active build manifest is incomplete: {mp}. Full Easy Apply is required.");

            foreach (var match in matches)
            {
                rawMatchRecordCount++;
                string targetKey = RelinkMatchTargetKey(match);
                if (string.IsNullOrWhiteSpace(targetKey))
                    throw new InvalidOperationException($"Active build manifest contains a match with no usable target path: {mp}");
                canonicalSequence++;
                if (canonicalMatchesByTarget.ContainsKey(targetKey)) duplicateMatchRecordsCollapsed++;
                // Mods/manifests are processed oldest to newest. Assignment means
                // the newest active manifest wins for an updated target.
                canonicalMatchesByTarget[targetKey] = (manifestOrder, canonicalSequence, match);
            }

            foreach (var entry in entries)
            {
                rawOverlayEntryRecordCount++;
                string targetKey = RelinkOverlayTargetKey(entry);
                if (string.IsNullOrWhiteSpace(targetKey))
                    throw new InvalidOperationException($"Active build manifest contains an overlay entry with no usable target path: {mp}");
                string ownerKey = RelinkOverlayOwnerKey(entry);
                string ownerTargetKey = targetKey + "\u001f" + ownerKey;
                canonicalSequence++;
                if (canonicalEntriesByOwnerTarget.ContainsKey(ownerTargetKey)) duplicateSameOwnerEntryRecordsCollapsed++;
                // First collapse duplicate history for the same target + HD## owner.
                canonicalEntriesByOwnerTarget[ownerTargetKey] = (manifestOrder, canonicalSequence, targetKey, entry);
            }

            foreach (var od in StringListObj(mod.GetValueOrDefault("overlay_dirs")).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string odDir = Path.Combine(gameDir, od);
                if (!Directory.Exists(odDir)) throw new InvalidOperationException($"Relink failed validation: overlay folder is missing: {od}");
                if (!LooksLikeToolOwnedOverlayFolder(odDir)) throw new InvalidOperationException($"Relink failed validation: overlay folder is not marked as managed by this tool: {od}");
                string pamt = Path.Combine(odDir, "0.pamt");
                if (!File.Exists(pamt)) throw new InvalidOperationException($"Relink failed validation: {od}/0.pamt is missing.");
                byte[] pamtBytes = File.ReadAllBytes(pamt);
                var headers = ReadPamtPazHeaders(pamt);
                if (headers.Count == 0) throw new InvalidOperationException($"Relink failed validation: {od}/0.pamt does not list any PAZ parts.");
                for (int i = 0; i < headers.Count; i++)
                {
                    string paz = Path.Combine(odDir, $"{i}.paz");
                    if (!File.Exists(paz)) throw new InvalidOperationException($"Relink failed validation: {od}/{i}.paz is missing.");
                    long len = new FileInfo(paz).Length;
                    if (len < headers[i].length) throw new InvalidOperationException($"Relink failed validation: {od}/{i}.paz is shorter than 0.pamt expects ({len} < {headers[i].length}).");
                }
                modifiedPamts[od] = pamtBytes;
                validatedOverlays.Add(od);
                log($"Relink validation OK: {od} ({headers.Count} PAZ part(s)).");
            }
        }

        var allMatches = canonicalMatchesByTarget.Values
            .OrderBy(v => v.ManifestOrder)
            .ThenBy(v => v.Sequence)
            .Select(v => v.Match)
            .ToList();

        int duplicateCrossOwnerEntryRecordsCollapsed = 0;
        var allEntries = new List<OverlayEntry>();
        foreach (var group in canonicalEntriesByOwnerTarget.Values
                     .GroupBy(v => v.TargetKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var newest = group
                .OrderByDescending(v => v.ManifestOrder)
                .ThenByDescending(v => v.Sequence)
                .First();
            allEntries.Add(newest.Entry);
            duplicateCrossOwnerEntryRecordsCollapsed += Math.Max(0, group.Count() - 1);
        }

        int duplicateHistoricalRecordsCollapsed = duplicateMatchRecordsCollapsed
            + duplicateSameOwnerEntryRecordsCollapsed
            + duplicateCrossOwnerEntryRecordsCollapsed;
        int matchedCount = allMatches.Count;
        log($"Relink canonicalization: {rawMatchRecordCount:n0} historical match record(s) -> {matchedCount:n0} unique managed target(s).");
        log($"Relink canonicalization: {rawOverlayEntryRecordCount:n0} historical overlay entry record(s) -> {allEntries.Count:n0} canonical target entry/entries.");
        log($"Relink canonicalization collapsed {duplicateHistoricalRecordsCollapsed:n0} duplicate historical record(s): " +
            $"matches {duplicateMatchRecordsCollapsed:n0}, same target/owner entries {duplicateSameOwnerEntryRecordsCollapsed:n0}, overlapping owner candidates {duplicateCrossOwnerEntryRecordsCollapsed:n0}.");

        var relinkReplaySources = LoadPathcReplaySources(pathcReplaySnapshotPaths, log, "Relink");
        if (ShouldUseExternalDriveSafeModeForSelectedFolders(gameDir, gameDir, out string relinkStorageSummary))
            log("Relink storage notice: slow or external game storage detected. Relink is transactional, but external/slow storage can still make validation and meta writes slower. " + relinkStorageSummary);
        else if (!string.IsNullOrWhiteSpace(relinkStorageSummary))
            log("[runtime] Relink " + relinkStorageSummary);

        var previousBaseSelection = ResolveManagedBaseBackup(gameDir, reg, mods, "Relink", log, migrateLatestRelink: false);
        string previousActiveBaseBackup = previousBaseSelection.BackupDir;
        long previousActiveBaseRevision = Math.Max(RegistryActiveBaseRevision(reg), previousBaseSelection.Revision);
        log($"Relink managed base before rebase: revision {previousActiveBaseRevision}; backup: {previousActiveBaseBackup.DefaultIfEmpty("<none registered>")}; source: {previousBaseSelection.Source}.");

        string backupDir = string.Empty;
        long newActiveBaseRevision = previousActiveBaseRevision;
        bool relinkWritesStarted = false;
        List<string>? relinkFailureReportLines = null;
        try
        {
            backupDir = CopyCurrentMetaBackup(gameDir, log, "Candidate current non-HDOB underlay captured before Relink replay");
            log($"Relink candidate new managed base captured before overlay replay: {backupDir}");
            CopyRegistryStateBackup(gameDir, backupDir, log);
            DebugFailureInjector.Check(DebugFailureInjector.RelinkAfterMetaBackup, log);
            progress?.Invoke(35, "RELINK: PATHC");
            var pathc = UpdatePathcForMatches(gameDir, allMatches, allEntries, log, progress, 35, 82, "RELINK PATHC", relinkReplaySources);
            if (pathc.bytes == null) throw new InvalidOperationException("Relink could not update PATHC. Full Easy Apply may be required.");

            int expectedManagedDdsTargets = allMatches.Count;
            int resolvedManagedDdsTargets = pathc.summary.Updated + pathc.summary.Added + pathc.summary.Unchanged;
            bool unresolvedManagedTargets = pathc.summary.Skipped > 0
                || pathc.summary.PackedWithoutEditableMetadata > 0
                || resolvedManagedDdsTargets != expectedManagedDdsTargets;
            if (unresolvedManagedTargets)
            {
                relinkFailureReportLines = new List<string>
                {
                    $"{AppName} {AppVersion} - Relink Overlays After Game Update - ABORTED BEFORE COMMIT",
                    $"Created: {DateTime.Now:s}",
                    $"Game: {gameDir}",
                    $"Historical match records scanned: {rawMatchRecordCount:n0}",
                    $"Unique managed targets expected: {expectedManagedDdsTargets:n0}",
                    $"Unique managed targets resolved: {resolvedManagedDdsTargets:n0}",
                    $"Duplicate historical records collapsed: {duplicateHistoricalRecordsCollapsed:n0}",
                    $"  Match records: {duplicateMatchRecordsCollapsed:n0}",
                    $"  Same target/owner overlay entries: {duplicateSameOwnerEntryRecordsCollapsed:n0}",
                    $"  Overlapping owner candidates: {duplicateCrossOwnerEntryRecordsCollapsed:n0}",
                    $"PATHC: {pathc.summary.Updated} updated / {pathc.summary.Added} added / {pathc.summary.Unchanged} unchanged / {pathc.summary.PackedWithoutEditableMetadata} unresolved packed/non-editable / {pathc.summary.Skipped} skipped",
                    "",
                    "UNRESOLVED PACKED/NON-EDITABLE TARGETS:"
                };
                relinkFailureReportLines.AddRange(pathc.summary.PackedWithoutEditableMetadataPaths);
                relinkFailureReportLines.Add("");
                relinkFailureReportLines.Add("SKIPPED TARGETS:");
                relinkFailureReportLines.AddRange(pathc.summary.SkippedPaths);

                log($"ERROR: Relink resolved {resolvedManagedDdsTargets:n0} of {expectedManagedDdsTargets:n0} unique managed DDS target(s); " +
                    $"{pathc.summary.PackedWithoutEditableMetadata:n0} unresolved packed/non-editable and {pathc.summary.Skipped:n0} skipped. No relink meta changes were committed.");
                throw new InvalidOperationException(
                    $"Relink aborted before commit because {Math.Max(0, expectedManagedDdsTargets - resolvedManagedDdsTargets):n0} managed target(s) were not resolved safely. " +
                    "The pre-relink meta and managed registry/state will be restored.");
            }

            log($"Relink PATHC resolution complete: {resolvedManagedDdsTargets:n0}/{expectedManagedDdsTargets:n0} unique managed DDS target(s) resolved. Commit is now allowed.");
            relinkWritesStarted = true;
            SafeWrite(Path.Combine(gameDir, "meta", "0.pathc"), pathc.bytes);

            progress?.Invoke(86, "RELINK: PAPGT");
            byte[] papgt = new PapgtManager(gameDir).Rebuild(modifiedPamts);
            SafeWrite(Path.Combine(gameDir, "meta", "0.papgt"), papgt);

            progress?.Invoke(94, "RELINK: REGISTRY");
            DebugFailureInjector.Check(DebugFailureInjector.RelinkBeforeRegistryRefresh, log);
            newActiveBaseRevision = Math.Max(1L, previousActiveBaseRevision + 1L);
            reg["last_relink_after_game_update_at"] = DateTime.Now.ToString("s");
            reg["last_relink_game_root_hash"] = GameRootCacheHash(gameDir);
            reg["last_relink_overlay_dirs"] = validatedOverlays.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            reg["active_base_backup_dir"] = backupDir;
            reg["active_base_revision"] = newActiveBaseRevision;
            reg["active_base_updated_at"] = DateTime.Now.ToString("s");
            reg["active_base_update_reason"] = "Successful Relink rebased current non-HDOB underlay";
            reg["active_base_previous_backup_dir"] = previousActiveBaseBackup;
            SaveRegistry(gameDir, reg);
            WriteActiveBaseInfo(backupDir, newActiveBaseRevision, "Successful Relink rebased current non-HDOB underlay", previousActiveBaseBackup, log);
            log($"Relink managed base rebased successfully: revision {newActiveBaseRevision}; new base backup: {backupDir}; previous base backup: {previousActiveBaseBackup.DefaultIfEmpty("<none registered>")}.");
            var relinkDirs = validatedOverlays.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            log($"Relink complete. Existing overlays relinked against current game meta: {string.Join(", ", relinkDirs)}.");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(relinkReportPath)!);
                File.WriteAllLines(relinkReportPath, new[]
                {
                    $"{AppName} {AppVersion} - Relink Overlays After Game Update",
                    $"Created: {DateTime.Now:s}",
                    $"Game: {gameDir}",
                    $"Historical match records scanned: {rawMatchRecordCount:n0}",
                    $"Unique managed targets replayed: {matchedCount:n0}",
                    $"Historical overlay entry records scanned: {rawOverlayEntryRecordCount:n0}",
                    $"Canonical overlay target entries used: {allEntries.Count:n0}",
                    $"Duplicate historical records collapsed: {duplicateHistoricalRecordsCollapsed:n0}",
                    $"  Match records: {duplicateMatchRecordsCollapsed:n0}",
                    $"  Same target/owner overlay entries: {duplicateSameOwnerEntryRecordsCollapsed:n0}",
                    $"  Overlapping owner candidates: {duplicateCrossOwnerEntryRecordsCollapsed:n0}",
                    $"Overlay folders validated: {string.Join(", ", relinkDirs)}",
                    $"PATHC: {pathc.summary.Updated} updated / {pathc.summary.Added} added / {pathc.summary.Unchanged} unchanged / {pathc.summary.Skipped} skipped",
                    $"PATHC packed without editable metadata: {pathc.summary.PackedWithoutEditableMetadata}",
                    $"PATHC total rows: {pathc.summary.TotalRows} (was {pathc.summary.StartingRows})",
                    $"Previous active base revision: {previousActiveBaseRevision}",
                    $"Previous active base backup: {previousActiveBaseBackup.DefaultIfEmpty("<none registered>")}",
                    $"New active base revision: {newActiveBaseRevision}",
                    $"New active base backup: {backupDir}"
                }, Encoding.UTF8);
            }
            catch { }
            progress?.Invoke(100, "RELINK DONE");
            return new BuildResult(matchedCount, 0, 0, relinkDirs, RegistryRoot(gameDir), "", relinkReportPath, true, (DateTime.UtcNow - start).TotalSeconds, 0, 0, 0);
        }
        catch (Exception ex)
        {
            if (DebugFailureInjector.IsSimulated(ex)) log("Normal cleanup/rollback path entered.");
            if (string.IsNullOrWhiteSpace(backupDir))
            {
                log("Relink rollback: no backup existed yet; no meta or registry/state restore required.");
            }
            else if (!relinkWritesStarted && ex is SimulatedFailureException sim && string.Equals(sim.FailurePoint, DebugFailureInjector.RelinkAfterMetaBackup, StringComparison.OrdinalIgnoreCase))
            {
                log("Relink rollback: no relink writes had occurred yet; restoring pre-relink registry/state only.");
                RestoreRegistryStateBackup(gameDir, backupDir, log);
            }
            else
            {
                log("Relink rollback: restoring pre-relink meta and registry/state.");
                RestoreMetaFromBackup(gameDir, backupDir, log);
                RestoreRegistryStateBackup(gameDir, backupDir, log);
            }
            log("Relink rollback cleanup complete.");
            if (relinkFailureReportLines != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(relinkReportPath)!);
                    File.WriteAllLines(relinkReportPath, relinkFailureReportLines, Encoding.UTF8);
                    log($"Relink failure report: {relinkReportPath}");
                }
                catch (Exception reportEx)
                {
                    log($"WARN: could not preserve Relink failure report after rollback: {reportEx.Message}");
                }
            }
            throw;
        }
    }

    private static string RegistryRoot(string gameDir) => Path.Combine(gameDir, ToolDataFolderName);
    private static string PreviousRegistryRoot(string gameDir) => Path.Combine(gameDir, PreviousToolDataFolderName);
    private static string LegacyRegistryRoot(string gameDir) => Path.Combine(gameDir, LegacyToolDataFolderName);
    private static IEnumerable<string> RegistryRoots(string gameDir)
    {
        yield return RegistryRoot(gameDir);
        yield return PreviousRegistryRoot(gameDir);
        yield return LegacyRegistryRoot(gameDir);
    }
    private static string RegistryManifestDir(string gameDir) => Path.Combine(RegistryRoot(gameDir), "manifests");
    private static string PreviousRegistryManifestDir(string gameDir) => Path.Combine(PreviousRegistryRoot(gameDir), "manifests");
    private static string LegacyRegistryManifestDir(string gameDir) => Path.Combine(LegacyRegistryRoot(gameDir), "manifests");
    private static IEnumerable<string> RegistryManifestDirs(string gameDir)
    {
        yield return RegistryManifestDir(gameDir);
        yield return PreviousRegistryManifestDir(gameDir);
        yield return LegacyRegistryManifestDir(gameDir);
    }
    private static string RegistryPath(string gameDir) => Path.Combine(RegistryRoot(gameDir), RegistryFileName);
    private static string PreviousRegistryPath(string gameDir) => Path.Combine(PreviousRegistryRoot(gameDir), RegistryFileName);
    private static string LegacyRegistryPath(string gameDir) => Path.Combine(LegacyRegistryRoot(gameDir), LegacyRegistryFileName);
    private static string LegacyNestedRegistryPath(string gameDir) => Path.Combine(LegacyRegistryRoot(gameDir), "registry", LegacyRegistryFileName);
    public static string BuildOutputRoot(string gameDir) => Path.Combine(RegistryRoot(gameDir), "builds");

    public static void MigrateLegacyBuildOutput(string gameDir, Action<string>? log = null)
    {
        SeedCurrentToolDataFromPrevious(gameDir, log);
        // v0.4.14/v0.4.15 wrote reports to a separate game side
        // CDTextureOverlayBuilds folder. Keep only one game side tool folder
        // by folding that legacy output into HDOverlayBuilder\builds.
        try
        {
            string legacy = Path.Combine(gameDir, "CDTextureOverlayBuilds");
            if (!Directory.Exists(legacy)) return;
            string targetRoot = BuildOutputRoot(gameDir);
            Directory.CreateDirectory(targetRoot);

            foreach (var item in Directory.EnumerateFileSystemEntries(legacy))
            {
                string name = Path.GetFileName(item) ?? "legacy_build_output";
                string dst = Path.Combine(targetRoot, name);
                if (Directory.Exists(dst) || File.Exists(dst))
                    dst = Path.Combine(targetRoot, name + "_legacy_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

                if (Directory.Exists(item)) Directory.Move(item, dst);
                else File.Move(item, dst);
            }

            Directory.Delete(legacy, true);
            log?.Invoke($"Legacy build output moved into single tool folder: {targetRoot}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARN: could not migrate legacy CDTextureOverlayBuilds folder: {ex.Message}");
        }
    }

    private static string RegistryHoldRoot(string gameDir)
    {
        string gameName = SafeName(Path.GetFileName(gameDir)).DefaultIfEmpty("Crimson Desert");
        return Path.Combine(Directory.GetParent(gameDir)?.FullName ?? gameDir, HoldRootFolderName, gameName);
    }

    private static string PreviousRegistryHoldRoot(string gameDir)
    {
        string gameName = SafeName(Path.GetFileName(gameDir)).DefaultIfEmpty("Crimson Desert");
        return Path.Combine(Directory.GetParent(gameDir)?.FullName ?? gameDir, PreviousHoldRootFolderName, gameName);
    }

    private static IEnumerable<string> RegistryHoldRoots(string gameDir)
    {
        yield return RegistryHoldRoot(gameDir);
        yield return PreviousRegistryHoldRoot(gameDir);
    }

    private static string EasyApplyRollbackRoot(string gameDir)
        => Path.Combine(Directory.GetParent(gameDir)?.FullName ?? gameDir, EasyApplyRollbackFolderName);

    private static string PreviousEasyApplyRollbackRoot(string gameDir)
        => Path.Combine(Directory.GetParent(gameDir)?.FullName ?? gameDir, PreviousEasyApplyRollbackFolderName);

    private static IEnumerable<string> EasyApplyRollbackRoots(string gameDir)
    {
        yield return EasyApplyRollbackRoot(gameDir);
        yield return PreviousEasyApplyRollbackRoot(gameDir);
    }

    private static string EasyApplyRollbackGameRoot(string gameDir)
    {
        string gameName = SafeName(Path.GetFileName(gameDir)).DefaultIfEmpty("Crimson Desert");
        return Path.Combine(EasyApplyRollbackRoot(gameDir), gameName);
    }

    private static void CleanEmptyRollbackFolders(string gameDir, Action<string>? log = null)
    {
        foreach (string rollbackRoot in EasyApplyRollbackRoots(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(rollbackRoot)) continue;

            foreach (var dir in Directory.EnumerateDirectories(rollbackRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                try
                {
                    if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                    Directory.Delete(dir, false);
                    log?.Invoke($"Cleaned empty rollback folder: {dir}");
                }
                catch
                {
                    // Best-effort cleanup only. A non-empty or locked rollback folder
                    // should never block the actual build/remove operation.
                }
            }

            CleanEmptyDirectoryUpTo(rollbackRoot, rollbackRoot, log, "rollback");
        }
    }

    private static void CleanEmptyDirectoryUpTo(string? dir, string stopDir, Action<string>? log = null, string label = "hold")
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        string stopFull = Path.GetFullPath(stopDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? cur = Path.GetFullPath(dir);
        while (!string.IsNullOrWhiteSpace(cur))
        {
            string curFull = cur.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!curFull.StartsWith(stopFull, StringComparison.OrdinalIgnoreCase)) break;
            if (!Directory.Exists(curFull)) { cur = Directory.GetParent(curFull)?.FullName; continue; }
            try
            {
                if (Directory.EnumerateFileSystemEntries(curFull).Any()) break;
                Directory.Delete(curFull, false);
                log?.Invoke($"Cleaned empty {label} folder: {curFull}");
            }
            catch { break; }
            if (string.Equals(curFull, stopFull, StringComparison.OrdinalIgnoreCase)) break;
            cur = Directory.GetParent(curFull)?.FullName;
        }
    }

    private static bool LooksLikeToolOwnedOverlayFolder(string dir)
    {
        string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!IsGeneratedOverlayFolderName(name) && !IsLegacyNumericOverlayFolderName(name)) return false;
        if (File.Exists(Path.Combine(dir, ManagedOverlayMarkerFile)) || File.Exists(Path.Combine(dir, LegacyManagedOverlayMarkerFile))) return true;
        return File.Exists(Path.Combine(dir, "0.pamt")) && File.Exists(Path.Combine(dir, "0.paz"));
    }

    private static int CleanupHeldCopiesForMod(string gameDir, Dictionary<string, object?> mod, Action<string> log, string reason)
    {
        var holdRoots = RegistryHoldRoots(gameDir)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();
        var holdRootTops = holdRoots.ToDictionary(
            r => r,
            r => Directory.GetParent(r)?.FullName ?? r,
            StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        var parentFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool TryGetMatchingHoldRoot(string fullPath, List<string> roots, out string root)
        {
            string full = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string candidate in roots)
            {
                if (full.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(candidate + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    root = candidate;
                    return true;
                }
            }
            root = "";
            return false;
        }

        foreach (var item in HeldOverlayList(mod))
        {
            if (!item.TryGetValue("held_path", out var heldPath) || string.IsNullOrWhiteSpace(heldPath)) continue;
            try
            {
                string full = Path.GetFullPath(heldPath);
                if (!TryGetMatchingHoldRoot(full, holdRoots, out _)) continue;
                if (Directory.Exists(full) && LooksLikeToolOwnedOverlayFolder(full))
                {
                    string? parent = Directory.GetParent(full)?.FullName;
                    Directory.Delete(full, true);
                    deleted++;
                    log($"{reason}: deleted held overlay copy: {full}");
                    if (!string.IsNullOrWhiteSpace(parent)) parentFolders.Add(parent);
                }
            }
            catch (Exception ex) { log($"WARN: {reason}: could not delete held overlay copy: {ex.Message}"); }
        }

        string modId = SObj(mod, "mod_id");
        if (!string.IsNullOrWhiteSpace(modId))
        {
            foreach (string holdRoot in holdRoots)
            {
                string modHold = Path.Combine(holdRoot, modId);
                if (!Directory.Exists(modHold)) continue;
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(modHold).ToList())
                    {
                        if (LooksLikeToolOwnedOverlayFolder(child))
                        {
                            Directory.Delete(child, true);
                            deleted++;
                            log($"{reason}: deleted stale held overlay copy: {child}");
                        }
                    }
                    parentFolders.Add(modHold);
                }
                catch (Exception ex) { log($"WARN: {reason}: could not clean held build folder: {ex.Message}"); }
            }
        }

        foreach (var parent in parentFolders)
        {
            if (TryGetMatchingHoldRoot(parent, holdRoots, out string root) && holdRootTops.TryGetValue(root, out string? stop) && !string.IsNullOrWhiteSpace(stop))
                CleanEmptyDirectoryUpTo(parent, stop, log);
        }
        return deleted;
    }

    private static void SeedCurrentToolDataFromPrevious(string gameDir, Action<string>? log)
    {
        try
        {
            string current = RegistryRoot(gameDir);
            string previous = PreviousRegistryRoot(gameDir);
            if (Directory.Exists(current) || !Directory.Exists(previous)) return;
            CopyRegistryStateDirectory(previous, current);
            log?.Invoke($"Legacy builder data detected and copied for compatibility: {previous} -> {current}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"WARN: legacy builder data could not be copied into HDOverlayBuilder; legacy detection will remain active. {ex.Message}");
        }
    }

    private static Dictionary<string, object?> NewRegistry(string gameDir) => new()
    {
        ["schema"] = 1,
        ["app"] = AppName,
        ["created_at"] = DateTime.Now.ToString("s"),
        ["updated_at"] = DateTime.Now.ToString("s"),
        ["game_dir"] = gameDir,
        ["active_build_revision"] = 0L,
        ["mods"] = new List<Dictionary<string, object?>>()
    };

    private static Dictionary<string, object?> LoadRegistry(string gameDir)
    {
        SeedCurrentToolDataFromPrevious(gameDir, null);
        foreach (var path in new[] { RegistryPath(gameDir), PreviousRegistryPath(gameDir), LegacyRegistryPath(gameDir), LegacyNestedRegistryPath(gameDir) })
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var reg = JsonElementToObject(doc.RootElement) as Dictionary<string, object?> ?? NewRegistry(gameDir);
                if (!reg.ContainsKey("mods")) reg["mods"] = new List<Dictionary<string, object?>>();
                if (!reg.ContainsKey("active_build_revision")) reg["active_build_revision"] = 0L;
                return reg;
            }
            catch { }
        }
        return NewRegistry(gameDir);
    }

    private static void SaveRegistry(string gameDir, Dictionary<string, object?> reg)
    {
        reg["schema"] = 1;
        reg["app"] = AppName;
        reg["game_dir"] = gameDir;
        if (!reg.ContainsKey("active_build_revision")) reg["active_build_revision"] = 0L;
        reg["updated_at"] = DateTime.Now.ToString("s");
        WriteJson(RegistryPath(gameDir), reg);
    }

    private static object? JsonElementToObject(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.TryGetDouble(out var d) ? d : null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string SObj(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? Convert.ToString(v) ?? "" : "";
    private static List<Dictionary<string, object?>> ObjMods(Dictionary<string, object?> reg)
    {
        if (reg.TryGetValue("mods", out var mods) && mods is List<object?> lo)
            return lo.OfType<Dictionary<string, object?>>().ToList();
        if (reg.TryGetValue("mods", out mods) && mods is List<Dictionary<string, object?>> ld)
            return ld;
        return new List<Dictionary<string, object?>>();
    }
    private static List<string> StringListObj(object? value)
    {
        if (value is List<object?> lo) return lo.Select(x => Convert.ToString(x) ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (value is List<string> ls) return ls;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array) return je.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        return new List<string>();
    }
    private static bool IsTextureMod(Dictionary<string, object?> m)
    {
        string name = SObj(m, "mod_name");
        string id = SObj(m, "mod_id");
        return string.Equals(name, DefaultModName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, LegacyDefaultModName, StringComparison.OrdinalIgnoreCase)
            || id.Contains(DefaultModName, StringComparison.OrdinalIgnoreCase)
            || id.Contains(LegacyDefaultModName, StringComparison.OrdinalIgnoreCase);
    }

    private static long RegistryActiveBuildRevision(Dictionary<string, object?> reg)
    {
        try
        {
            if (reg.TryGetValue("active_build_revision", out var raw))
            {
                if (raw is long l) return l;
                if (raw is int i) return i;
                if (raw is double d) return (long)d;
                if (long.TryParse(Convert.ToString(raw), out var parsed)) return parsed;
            }
        }
        catch { }
        return 0L;
    }

    private sealed record ManagedBaseSelection(string BackupDir, long Revision, string Source, bool Migrated, bool RelinkMarkerPresent);

    private static long RegistryActiveBaseRevision(Dictionary<string, object?> reg)
    {
        try
        {
            if (reg.TryGetValue("active_base_revision", out var raw))
            {
                if (raw is long l) return l;
                if (raw is int i) return i;
                if (raw is double d) return (long)d;
                if (long.TryParse(Convert.ToString(raw), out var parsed)) return parsed;
            }
        }
        catch { }
        return 0L;
    }

    private static string RegistryActiveBaseBackupDir(Dictionary<string, object?> reg)
        => SObj(reg, "active_base_backup_dir");

    private static DateTime? ParseRegistryLocalTimestampUtc(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return parsed.ToUniversalTime();
        return null;
    }

    private static IEnumerable<string> EnumerateMetaBackupDirectories(string gameDir)
    {
        foreach (var root in RegistryRoots(gameDir).Select(r => Path.Combine(r, "backups")).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, "meta", "0.pathc")) || File.Exists(Path.Combine(dir, "meta", "0.papgt")))
                    yield return dir;
            }
        }
    }

    private static string LegacyManifestBaseBackup(IEnumerable<Dictionary<string, object?>> mods)
    {
        var earliestManifest = mods
            .Select(m => ReadJsonDict(SObj(m, "manifest_copy")) ?? ReadJsonDict(SObj(m, "original_manifest")))
            .Where(m => m != null)
            .OrderBy(m => SObj(m!, "created_at"))
            .FirstOrDefault();
        return earliestManifest != null ? SObj(earliestManifest, "backup_dir") : string.Empty;
    }

    private static string FindLatestSuccessfulRelinkBaseCandidate(string gameDir, Dictionary<string, object?> reg)
    {
        DateTime? relinkUtc = ParseRegistryLocalTimestampUtc(SObj(reg, "last_relink_after_game_update_at"));
        if (!relinkUtc.HasValue) return string.Empty;

        var candidates = EnumerateMetaBackupDirectories(gameDir)
            .Select(path => new { Path = path, TimestampUtc = TryGetBackupFolderTimestampUtc(path) })
            .Where(x => x.TimestampUtc.HasValue && x.TimestampUtc.Value <= relinkUtc.Value.AddMinutes(2))
            .Where(x => !IsMetaBackupLikelyFromOlderGamePatch(gameDir, x.Path, _ => { }))
            .OrderBy(x => x.TimestampUtc)
            .ToList();
        return candidates.LastOrDefault()?.Path ?? string.Empty;
    }

    private static void WriteActiveBaseInfo(string backupDir, long revision, string reason, string previousBackupDir, Action<string> log)
    {
        try
        {
            WriteJson(Path.Combine(backupDir, ActiveBaseInfoFileName), new
            {
                app = AppName,
                version = AppVersion,
                active_base_revision = revision,
                registered_at_utc = DateTime.UtcNow.ToString("o"),
                reason,
                previous_active_base_backup_dir = previousBackupDir,
                backup_dir = backupDir
            });
        }
        catch (Exception ex)
        {
            log($"WARN: could not write active managed base marker: {ex.Message}");
        }
    }

    private static ManagedBaseSelection ResolveManagedBaseBackup(
        string gameDir,
        Dictionary<string, object?> reg,
        IEnumerable<Dictionary<string, object?>> mods,
        string operation,
        Action<string> log,
        bool migrateLatestRelink)
    {
        string active = RegistryActiveBaseBackupDir(reg);
        long revision = RegistryActiveBaseRevision(reg);
        DateTime? latestRelinkUtc = ParseRegistryLocalTimestampUtc(SObj(reg, "last_relink_after_game_update_at"));
        DateTime? activeBaseUpdatedUtc = ParseRegistryLocalTimestampUtc(SObj(reg, "active_base_updated_at"));
        bool relinkMarkerPresent = latestRelinkUtc.HasValue;
        bool activePointerPredatesLatestRelink = relinkMarkerPresent
            && (!activeBaseUpdatedUtc.HasValue || activeBaseUpdatedUtc.Value.AddSeconds(1) < latestRelinkUtc!.Value);

        if (!string.IsNullOrWhiteSpace(active) && Directory.Exists(active) && !activePointerPredatesLatestRelink)
        {
            if (!IsMetaBackupLikelyFromOlderGamePatch(gameDir, active, _ => { }))
                return new ManagedBaseSelection(active, revision, "active managed base pointer", false, relinkMarkerPresent);
            log($"WARN: {operation}: active managed base points to a different game patch and will not be restored: {active}");
        }
        else if (!string.IsNullOrWhiteSpace(active) && activePointerPredatesLatestRelink)
        {
            log($"WARN: {operation}: active managed base revision {revision} predates the latest successful Relink and will be rebased from Relink history before restore: {active}");
        }

        if (relinkMarkerPresent)
        {
            string migrated = FindLatestSuccessfulRelinkBaseCandidate(gameDir, reg);
            if (!string.IsNullOrWhiteSpace(migrated))
            {
                long migratedRevision = Math.Max(1L, revision + 1L);
                if (migrateLatestRelink)
                {
                    string previous = active;
                    reg["active_base_backup_dir"] = migrated;
                    reg["active_base_revision"] = migratedRevision;
                    reg["active_base_updated_at"] = DateTime.Now.ToString("s");
                    reg["active_base_update_reason"] = "Recovered managed base from latest successful Relink";
                    reg["active_base_previous_backup_dir"] = previous;
                    SaveRegistry(gameDir, reg);
                    WriteActiveBaseInfo(migrated, migratedRevision, "Recovered managed base from latest successful Relink", previous, log);
                    log($"{operation}: migrated the latest successful Relink base into managed state. Active base revision {migratedRevision}: {migrated}");
                }
                return new ManagedBaseSelection(migrated, migratedRevision, "latest successful Relink recovery", migrateLatestRelink, true);
            }

            log($"ERROR: {operation}: a successful Relink is recorded, but its pre-overlay base backup could not be recovered. The older original build backup will not be restored silently.");
            return new ManagedBaseSelection(string.Empty, revision, "unresolved latest Relink base", false, true);
        }

        string legacy = LegacyManifestBaseBackup(mods);
        return new ManagedBaseSelection(legacy, revision, "legacy earliest build manifest", false, false);
    }

    private static long IncrementActiveBuildRevision(string gameDir, string reason, Action<string>? log)
    {
        var reg = LoadRegistry(gameDir);
        long next = RegistryActiveBuildRevision(reg) + 1L;
        reg["active_build_revision"] = next;
        reg["last_content_update_at"] = DateTime.Now.ToString("s");
        reg["last_content_update_reason"] = reason;
        SaveRegistry(gameDir, reg);
        log?.Invoke($"Active build revision updated to {next} ({reason}).");
        return next;
    }

    private static string ModIdFromManifest(Dictionary<string, object?> manifest)
    {
        string raw = string.Join("|", SObj(manifest, "mod_name"), SObj(manifest, "created_at"), SObj(manifest, "target_pamt_dir"), SObj(manifest, "target_full_prefix"), string.Join(",", StringListObj(manifest.GetValueOrDefault("overlay_dirs"))));
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        string digest = Convert.ToHexString(hash).ToLowerInvariant()[..10];
        return $"{SafeName(SObj(manifest, "mod_name").DefaultIfEmpty(DefaultModName)).Replace(' ', '_')}_{digest}";
    }

    private static Dictionary<string, object?> RegistryEntryFromManifest(string gameDir, string manifestPath, Dictionary<string, object?> manifest, string status = "active", List<Dictionary<string, string>>? held = null)
    {
        var overlayDirs = StringListObj(manifest.GetValueOrDefault("overlay_dirs"));
        return new Dictionary<string, object?>
        {
            ["mod_id"] = ModIdFromManifest(manifest),
            ["mod_name"] = SObj(manifest, "mod_name").DefaultIfEmpty(DefaultModName),
            ["status"] = status,
            ["created_at"] = SObj(manifest, "created_at").DefaultIfEmpty(DateTime.Now.ToString("s")),
            ["manifest_copy"] = manifestPath,
            ["original_manifest"] = SObj(manifest, "original_manifest"),
            ["overlay_dirs"] = overlayDirs,
            ["target_pamt_dir"] = SObj(manifest, "target_pamt_dir"),
            ["target_full_prefix"] = SObj(manifest, "target_full_prefix"),
            ["matched_count"] = manifest.TryGetValue("matched_count", out var mc) ? mc : 0,
            ["updated_overlay_dirs"] = StringListObj(manifest.GetValueOrDefault("updated_overlay_dirs")),
            ["new_overlay_texture_count"] = manifest.TryGetValue("new_overlay_texture_count", out var ntc) ? ntc : 0,
            ["updated_existing_count"] = manifest.TryGetValue("updated_existing_count", out var uec) ? uec : 0,
            ["held_overlays"] = held ?? new List<Dictionary<string, string>>()
        };
    }

    private static Dictionary<string, object?>? ReadJsonDict(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return JsonElementToObject(doc.RootElement) as Dictionary<string, object?>;
        }
        catch { return null; }
    }

    private static void AutoRepairRegistryFromLocalManifests(string gameDir, Action<string>? log)
    {
        var reg = LoadRegistry(gameDir);
        var mods = ObjMods(reg);
        var existingIds = new HashSet<string>(mods.Select(m => SObj(m, "mod_id")), StringComparer.OrdinalIgnoreCase);
        int imported = 0;
        var importedFrom = new List<string>();
        foreach (var manDir in RegistryManifestDirs(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(manDir)) continue;
            foreach (var mp in Directory.EnumerateFiles(manDir, "*.json"))
            {
                var manifest = ReadJsonDict(mp);
                if (manifest == null) continue;
                bool applied = manifest.TryGetValue("applied_to_game", out var ap) && Convert.ToString(ap)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                if (!applied && !(manifest.TryGetValue("applied_to_game", out ap) && ap is bool b && b)) continue;
                string mgame = SObj(manifest, "game_dir");
                if (!string.IsNullOrWhiteSpace(mgame) && !string.Equals(Path.GetFullPath(mgame).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                var ods = StringListObj(manifest.GetValueOrDefault("overlay_dirs"));
                if (ods.Count == 0) continue;
                var entry = RegistryEntryFromManifest(gameDir, mp, manifest, ods.Any(od => Directory.Exists(Path.Combine(gameDir, od))) ? "active" : "missing");
                string id = SObj(entry, "mod_id");
                if (existingIds.Contains(id)) continue;
                mods.Add(entry);
                existingIds.Add(id);
                imported++;
                if (!importedFrom.Contains(manDir, StringComparer.OrdinalIgnoreCase)) importedFrom.Add(manDir);
            }
        }
        if (imported > 0)
        {
            reg["mods"] = mods;
            SaveRegistry(gameDir, reg);
            log?.Invoke($"Registry auto repair: imported {imported} manifest(s) from {string.Join(", ", importedFrom)}");
        }
    }

    private static void RegisterAppliedManifest(string gameDir, string manifestPath, Dictionary<string, object?> manifest, Action<string> log, bool initializeManagedBase)
    {
        string manDir = RegistryManifestDir(gameDir);
        Directory.CreateDirectory(manDir);
        manifest = new Dictionary<string, object?>(manifest);
        manifest["original_manifest"] = manifestPath;
        string modId = ModIdFromManifest(manifest);
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        string copy = Path.Combine(manDir, $"{SafeName(DefaultModName)}_{stamp}_{modId.Split('_').Last()}.json");
        WriteJson(copy, manifest);

        var reg = LoadRegistry(gameDir);
        var mods = ObjMods(reg).Where(m => !string.Equals(SObj(m, "mod_id"), modId, StringComparison.OrdinalIgnoreCase)).ToList();
        mods.Add(RegistryEntryFromManifest(gameDir, copy, manifest, "active"));
        reg["mods"] = mods;
        long nextRevision = RegistryActiveBuildRevision(reg) + 1L;
        reg["active_build_revision"] = nextRevision;
        reg["last_content_update_at"] = DateTime.Now.ToString("s");
        reg["last_content_update_reason"] = "Easy Apply / full apply registered managed overlay build";
        string initialBaseBackup = SObj(manifest, "backup_dir");
        if (initializeManagedBase
            && string.IsNullOrWhiteSpace(RegistryActiveBaseBackupDir(reg))
            && !string.IsNullOrWhiteSpace(initialBaseBackup)
            && Directory.Exists(initialBaseBackup))
        {
            long initialBaseRevision = Math.Max(1L, RegistryActiveBaseRevision(reg));
            reg["active_base_backup_dir"] = initialBaseBackup;
            reg["active_base_revision"] = initialBaseRevision;
            reg["active_base_updated_at"] = DateTime.Now.ToString("s");
            reg["active_base_update_reason"] = "Initial managed base captured before Easy Apply / full apply";
            WriteActiveBaseInfo(initialBaseBackup, initialBaseRevision, "Initial managed base captured before Easy Apply / full apply", string.Empty, log);
            log($"Initial managed base registered. Revision {initialBaseRevision}: {initialBaseBackup}");
        }
        SaveRegistry(gameDir, reg);
        log($"Master registry updated: {RegistryPath(gameDir)}");
        log($"Active build revision updated to {nextRevision} (Easy Apply / full apply registered managed overlay build).");
    }

    public static bool RemoveCurrentTextureBuild(string gameDir, Action<string> log, bool deleteOverlays, Action<int, string>? progress = null, bool suppressMissingOverlayWarnings = false)
    {
        progress?.Invoke(2, "REMOVE: REPAIR REGISTRY");
        log("Remove current build: repairing registry and loading installed builds...");
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var reg = LoadRegistry(gameDir);
        var mods = ObjMods(reg);
        var targets = mods.Where(IsTextureMod).ToList();
        if (targets.Count == 0)
        {
            progress?.Invoke(100, "REMOVE: NOTHING FOUND");
            log("No registered HD Overlay Builder build was found. Nothing to remove.");
            return false;
        }

        int totalOverlays = targets
            .SelectMany(m => StringListObj(m.GetValueOrDefault("overlay_dirs")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        progress?.Invoke(10, "REMOVE: BACKUP META");
        log("Remove current build: backing up current meta before restore/delete...");
        CopyCurrentMetaBackup(gameDir, log);

        // Restore the currently registered managed underlay. After a successful
        // Relink this points to the pre-Relink stock/mod-manager meta, not the
        // original pre-update Easy Apply backup.
        progress?.Invoke(22, "REMOVE: RESTORE BASE META");
        log("Remove current build: resolving the current managed base meta/PATHC backup...");
        var removeBase = ResolveManagedBaseBackup(gameDir, reg, targets, "Remove Current Build", log, migrateLatestRelink: true);
        if (removeBase.RelinkMarkerPresent && string.IsNullOrWhiteSpace(removeBase.BackupDir))
            throw new InvalidOperationException("Remove Current Build stopped because the latest successful Relink base could not be recovered safely. Verify game files or run Relink again before removing the managed build.");
        log($"Remove Current Build will restore managed base revision {removeBase.Revision} from: {removeBase.BackupDir.DefaultIfEmpty("<no backup available>")} (source: {removeBase.Source}).");
        bool removeBaseMetaRestored = false;
        if (!string.IsNullOrWhiteSpace(removeBase.BackupDir) && Directory.Exists(removeBase.BackupDir))
        {
            try
            {
                removeBaseMetaRestored = RestoreMetaFromBackup(gameDir, removeBase.BackupDir, log);
                if (removeBaseMetaRestored)
                    log($"Remove Current Build restored exact managed base backup: {removeBase.BackupDir}");
            }
            catch (Exception ex) { log($"WARN: could not restore managed base backup: {ex.Message}"); }
        }
        if (!removeBaseMetaRestored)
        {
            log("WARN: Remove current build did not restore a base meta backup. This is expected when the registered build backup is from an older game patch.");
            log("WARN: overlays will still be deleted and PAPGT rebuilt against the current game files. If Steam still reports an installation problem, verify game files once.");
        }

        int changed = 0;
        progress?.Invoke(35, totalOverlays > 0 ? $"REMOVE: DELETE OVERLAYS 0/{totalOverlays}" : "REMOVE: DELETE OVERLAYS");
        foreach (var mod in targets)
        {
            string modId = SObj(mod, "mod_id").DefaultIfEmpty("texture_overlay");
            var overlays = StringListObj(mod.GetValueOrDefault("overlay_dirs"));
            foreach (var od in overlays.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int pct = totalOverlays > 0 ? 35 + (int)Math.Round(Math.Min(1.0, changed / (double)totalOverlays) * 35.0) : 55;
                progress?.Invoke(pct, totalOverlays > 0 ? $"REMOVE: DELETE {od} ({changed + 1}/{totalOverlays})" : $"REMOVE: DELETE {od}");
                string src = Path.Combine(gameDir, od);
                if (!Directory.Exists(src))
                {
                    foreach (var item in HeldOverlayList(mod))
                    {
                        if (item.TryGetValue("original_dir", out var orig) && string.Equals(orig, od, StringComparison.OrdinalIgnoreCase) && item.TryGetValue("held_path", out var hp) && Directory.Exists(hp)) src = hp;
                    }
                }
                if (!Directory.Exists(src))
                {
                    log(suppressMissingOverlayWarnings
                        ? $"Remove current build: registered overlay already moved out of the game folder, skipped delete: {od}"
                        : $"WARN: registered overlay missing, skipped: {od}");
                    continue;
                }
                Directory.Delete(src, true);
                log($"Deleted overlay: {src}");
                changed++;
            }
            int heldDeleted = CleanupHeldCopiesForMod(gameDir, mod, log, "Remove current build");
            if (heldDeleted > 0) log($"Remove current build: cleaned held overlay copies: {heldDeleted}.");
            foreach (var mp in new[] { SObj(mod, "manifest_copy") })
            {
                try { if (!string.IsNullOrWhiteSpace(mp) && File.Exists(mp) && Path.GetDirectoryName(mp)?.Equals(RegistryManifestDir(gameDir), StringComparison.OrdinalIgnoreCase) == true) File.Delete(mp); } catch { }
            }
        }

        progress?.Invoke(75, "REMOVE: REBUILD PAPGT");
        log("Remove current build: rebuilding PAPGT after overlay deletion...");
        byte[] papgt = new PapgtManager(gameDir).Rebuild(new Dictionary<string, byte[]>());
        SafeWrite(Path.Combine(gameDir, "meta", "0.papgt"), papgt);

        progress?.Invoke(84, "REMOVE: UPDATE REGISTRY");
        mods = mods.Where(m => !targets.Any(t => string.Equals(SObj(t, "mod_id"), SObj(m, "mod_id"), StringComparison.OrdinalIgnoreCase))).ToList();
        reg["mods"] = mods;
        SaveRegistry(gameDir, reg);

        // Full cleanup: once the registered texture build is removed, remove the
        // game side HDOverlayBuilder working folder plus legacy tool folders too.
        // This clears the registry/manifests/backups the tool created inside the game directory.
        progress?.Invoke(92, "REMOVE: CLEAN TOOL DATA");
        try
        {
            foreach (var registryRoot in RegistryRoots(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(registryRoot))
                {
                    Directory.Delete(registryRoot, true);
                    log($"Cleaned previous builder data folder: {registryRoot}");
                }
            }
            string legacyBuildRoot = Path.Combine(gameDir, "CDTextureOverlayBuilds");
            if (Directory.Exists(legacyBuildRoot))
            {
                Directory.Delete(legacyBuildRoot, true);
                log($"Removed legacy build output folder: {legacyBuildRoot}");
            }
            CleanEmptyRollbackFolders(gameDir, log);
        }
        catch (Exception ex)
        {
            log($"WARN: could not remove tool data folder: {ex.Message}");
        }

        progress?.Invoke(100, "REMOVE DONE");
        log($"Current build removed. Overlays deleted: {changed}. Ready to apply a new build.");
        return true;
    }

    public static List<InstalledBuildSummary> GetInstalledTextureBuilds(string gameDir)
    {
        AutoRepairRegistryFromLocalManifests(gameDir, null);
        var reg = LoadRegistry(gameDir);
        return ObjMods(reg)
            .Where(IsTextureMod)
            .OrderBy(m => SObj(m, "created_at"))
            .Select(m =>
            {
                var manifestInfo = ReadJsonDict(SObj(m, "manifest_copy")) ?? ReadJsonDict(SObj(m, "original_manifest"));
                var overlays = StringListObj(m.GetValueOrDefault("overlay_dirs"));
                string pamt = SObj(m, "target_pamt_dir").DefaultIfEmpty("ALL");
                string prefix = SObj(m, "target_full_prefix").DefaultIfEmpty("ALL");
                string label = $"{pamt} / {prefix} / {string.Join(",", overlays)}";
                int matched = 0;
                try { matched = Convert.ToInt32(m.GetValueOrDefault("matched_count") ?? manifestInfo?.GetValueOrDefault("matched_count") ?? 0); } catch { }
                var updatedOverlays = StringListObj(m.GetValueOrDefault("updated_overlay_dirs"));
                if (updatedOverlays.Count == 0 && manifestInfo != null) updatedOverlays = StringListObj(manifestInfo.GetValueOrDefault("updated_overlay_dirs"));
                int newOverlayTextures = 0;
                int updatedExisting = 0;
                try { newOverlayTextures = Convert.ToInt32(m.GetValueOrDefault("new_overlay_texture_count") ?? manifestInfo?.GetValueOrDefault("new_overlay_texture_count") ?? 0); } catch { }
                try { updatedExisting = Convert.ToInt32(m.GetValueOrDefault("updated_existing_count") ?? manifestInfo?.GetValueOrDefault("updated_existing_count") ?? 0); } catch { }
                if (newOverlayTextures <= 0 && overlays.Count > 0 && updatedExisting <= 0) newOverlayTextures = matched;
                if (updatedExisting <= 0 && matched > newOverlayTextures) updatedExisting = matched - newOverlayTextures;
                return new InstalledBuildSummary(SObj(m, "mod_id"), label, SObj(m, "status").DefaultIfEmpty("active"), overlays, matched, pamt, prefix, SObj(m, "created_at"), updatedOverlays, newOverlayTextures, updatedExisting);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
            .ToList();
    }

    public static bool RemoveSelectedTextureBuilds(string gameDir, IEnumerable<string> modIds, Action<string> log, Action<int, string>? progress = null)
    {
        var selected = new HashSet<string>(modIds.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return false;
        progress?.Invoke(2, "REMOVE SELECTED: REPAIR REGISTRY");
        log("Remove selected builds: repairing registry and loading installed builds...");
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var reg = LoadRegistry(gameDir);
        var allMods = ObjMods(reg);
        var textureMods = allMods.Where(IsTextureMod).OrderBy(m => SObj(m, "created_at")).ToList();
        var removeMods = textureMods.Where(m => selected.Contains(SObj(m, "mod_id"))).ToList();
        if (removeMods.Count == 0)
        {
            progress?.Invoke(100, "REMOVE SELECTED: NOTHING FOUND");
            return false;
        }
        var keepMods = textureMods.Where(m => !selected.Contains(SObj(m, "mod_id"))).ToList();

        int totalRemoveOverlays = removeMods
            .SelectMany(m => StringListObj(m.GetValueOrDefault("overlay_dirs")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        progress?.Invoke(10, "REMOVE SELECTED: BACKUP META");
        log("Remove selected builds: backing up current meta before changes...");
        CopyCurrentMetaBackup(gameDir, log);

        // Restore baseline meta from the first known texture overlay manifest, then replay kept builds.
        progress?.Invoke(22, "REMOVE SELECTED: RESTORE BASE META");
        log("Remove selected builds: restoring base meta/PATHC from earliest build backup...");
        var earliestManifest = textureMods
            .Select(m => ReadJsonDict(SObj(m, "manifest_copy")) ?? ReadJsonDict(SObj(m, "original_manifest")))
            .Where(m => m != null)
            .Cast<Dictionary<string, object?>>()
            .OrderBy(m => SObj(m, "created_at"))
            .FirstOrDefault();
        string baseBackup = earliestManifest != null ? SObj(earliestManifest, "backup_dir") : "";
        progress?.Invoke(65, "HOLD: RESTORE BASE META");
        if (!string.IsNullOrWhiteSpace(baseBackup) && Directory.Exists(baseBackup))
        {
            if (!RestoreMetaFromBackup(gameDir, baseBackup, log))
                log("WARN: Remove selected builds skipped the first build backup because it appears to be from an older game patch.");
        }
        else
        {
            log("WARN: could not find the first build backup. Selected overlays will be removed and PAPGT rebuilt, but PATHC may retain stale rows until a full rebuild/remove is done.");
        }

        int deleted = 0;
        progress?.Invoke(35, totalRemoveOverlays > 0 ? $"REMOVE SELECTED: DELETE OVERLAYS 0/{totalRemoveOverlays}" : "REMOVE SELECTED: DELETE OVERLAYS");
        foreach (var mod in removeMods)
        {
            foreach (var od in StringListObj(mod.GetValueOrDefault("overlay_dirs")).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int pct = totalRemoveOverlays > 0 ? 35 + (int)Math.Round(Math.Min(1.0, deleted / (double)totalRemoveOverlays) * 25.0) : 50;
                progress?.Invoke(pct, totalRemoveOverlays > 0 ? $"REMOVE SELECTED: DELETE {od} ({deleted + 1}/{totalRemoveOverlays})" : $"REMOVE SELECTED: DELETE {od}");
                string dir = Path.Combine(gameDir, od);
                if (!Directory.Exists(dir))
                {
                    foreach (var item in HeldOverlayList(mod))
                    {
                        if (item.TryGetValue("original_dir", out var orig) && string.Equals(orig, od, StringComparison.OrdinalIgnoreCase) && item.TryGetValue("held_path", out var hp) && Directory.Exists(hp)) dir = hp;
                    }
                }
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    deleted++;
                    log($"Deleted selected overlay: {dir}");
                }
                else
                {
                    log($"WARN: selected overlay missing, skipped: {od}");
                }
            }
            int heldDeleted = CleanupHeldCopiesForMod(gameDir, mod, log, "Remove selected builds");
            if (heldDeleted > 0) log($"Remove selected builds: cleaned held overlay copies: {heldDeleted}.");
            string mp = SObj(mod, "manifest_copy");
            try { if (!string.IsNullOrWhiteSpace(mp) && File.Exists(mp) && Path.GetDirectoryName(mp)?.Equals(RegistryManifestDir(gameDir), StringComparison.OrdinalIgnoreCase) == true) File.Delete(mp); } catch { }
        }

        // Replay PATHC rows for the builds that remain active, preserving apply order.
        int replayIndex = 0;
        foreach (var mod in keepMods)
        {
            replayIndex++;
            progress?.Invoke(62 + (keepMods.Count > 0 ? (int)Math.Round((replayIndex - 1) / (double)keepMods.Count * 18.0) : 0), $"REMOVE SELECTED: REPLAY KEPT {replayIndex}/{keepMods.Count}");
            string mp = SObj(mod, "manifest_copy");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) mp = SObj(mod, "original_manifest");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) { log($"WARN: kept build has no readable manifest: {SObj(mod, "mod_id")}"); continue; }
            var matches = ReadManifestMatches(mp);
            var entries = ReadManifestOverlayEntries(mp);
            if (matches.Count == 0 || entries.Count == 0) continue;
            int replayStart = 62 + (keepMods.Count > 0 ? (int)Math.Round((replayIndex - 1) / (double)keepMods.Count * 18.0) : 0);
            int replayEnd = 62 + (keepMods.Count > 0 ? (int)Math.Round(replayIndex / (double)keepMods.Count * 18.0) : 18);
            var pathcReplay = UpdatePathcForMatches(gameDir, matches, entries, log, progress, replayStart, Math.Max(replayStart, replayEnd), $"REMOVE SELECTED: PATHC {replayIndex}/{keepMods.Count}");
            if (pathcReplay.bytes != null) SafeWrite(Path.Combine(gameDir, "meta", "0.pathc"), pathcReplay.bytes);
        }

        progress?.Invoke(84, "REMOVE SELECTED: REBUILD PAPGT");
        byte[] papgt = new PapgtManager(gameDir).Rebuild(new Dictionary<string, byte[]>());
        SafeWrite(Path.Combine(gameDir, "meta", "0.papgt"), papgt);

        progress?.Invoke(92, "REMOVE SELECTED: UPDATE REGISTRY");
        var remainingMods = allMods.Where(m => !removeMods.Any(r => string.Equals(SObj(r, "mod_id"), SObj(m, "mod_id"), StringComparison.OrdinalIgnoreCase))).ToList();
        reg["mods"] = remainingMods;
        long nextRevision = RegistryActiveBuildRevision(reg) + 1L;
        reg["active_build_revision"] = nextRevision;
        reg["last_content_update_at"] = DateTime.Now.ToString("s");
        reg["last_content_update_reason"] = "Remove Selected Build removed managed overlay build(s)";
        SaveRegistry(gameDir, reg);
        log($"Active build revision updated to {nextRevision} (Remove Selected Build removed managed overlay build(s)).");
        TryRebaseActiveTargetManifestToInstalledTargets(gameDir, log, "Remove Selected Build");
        progress?.Invoke(100, "REMOVE SELECTED DONE");
        log($"Selected builds removed. Overlay folders deleted: {deleted}. Remaining builds replayed: {keepMods.Count}.");
        return true;
    }

    private static List<Dictionary<string, string>> HeldOverlayList(Dictionary<string, object?> mod)
    {
        var result = new List<Dictionary<string, string>>();
        if (!mod.TryGetValue("held_overlays", out var raw)) return result;
        if (raw is List<object?> lo)
        {
            foreach (var x in lo.OfType<Dictionary<string, object?>>())
                result.Add(x.ToDictionary(k => k.Key, k => Convert.ToString(k.Value) ?? ""));
        }
        if (raw is List<Dictionary<string, string>> ls) result.AddRange(ls);
        return result;
    }

    private static string NextFreeOverlayDir(string gameDir, HashSet<string>? reserved = null)
    {
        reserved ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= 99; i++)
        {
            string name = $"{ManagedOverlayPrefix}{i:00}";
            if (!Directory.Exists(Path.Combine(gameDir, name)) && !reserved.Contains(name)) return name;
        }
        throw new InvalidOperationException("Not enough free HD## overlay folder names are available.");
    }

    public static void HoldRegisteredOverlays(string gameDir, Action<string> log, Action<int, string>? progress = null)
    {
        progress?.Invoke(2, "HOLD: REPAIR REGISTRY");
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var reg = LoadRegistry(gameDir);
        var mods = ObjMods(reg);
        var active = mods.Where(m => IsTextureMod(m) && !string.Equals(SObj(m, "status"), "held", StringComparison.OrdinalIgnoreCase)).ToList();
        if (active.Count == 0) throw new InvalidOperationException("No active registered overlays were found for Hold.");
        var holdBase = ResolveManagedBaseBackup(gameDir, reg, active, "Smart Overlay Hold", log, migrateLatestRelink: true);
        if (holdBase.RelinkMarkerPresent && string.IsNullOrWhiteSpace(holdBase.BackupDir))
            throw new InvalidOperationException("Smart Overlay Hold stopped because the latest successful Relink base could not be recovered safely. Verify game files or run Relink again before using Hold.");
        if (!string.IsNullOrWhiteSpace(holdBase.BackupDir)
            && IsMetaBackupLikelyFromOlderGamePatch(gameDir, holdBase.BackupDir, _ => { }))
            throw new InvalidOperationException("Smart Overlay Hold stopped because the registered managed base belongs to an older game patch. Relink against the current game meta before using Hold.");
        log($"Smart Overlay Hold will restore managed base revision {holdBase.Revision} from: {holdBase.BackupDir.DefaultIfEmpty("<no backup available>")} (source: {holdBase.Source}).");

        progress?.Invoke(8, "HOLD: BACKUP META");
        CopyCurrentMetaBackup(gameDir, log, "Pre-Hold overlay-applied meta safety backup");
        string pathcHoldSnapshot = CopyPathcReplaySnapshot(gameDir, "hold", log);
        string holdRoot = RegistryHoldRoot(gameDir);
        int moved = 0;
        int totalToMove = active.Sum(m => StringListObj(m.GetValueOrDefault("overlay_dirs")).Count);
        progress?.Invoke(15, totalToMove > 0 ? $"HOLD: MOVE 0/{totalToMove}" : "HOLD: MOVE OVERLAYS");
        foreach (var mod in active)
        {
            string modId = SObj(mod, "mod_id").DefaultIfEmpty("texture_overlay");
            string modHold = Path.Combine(holdRoot, modId);
            Directory.CreateDirectory(modHold);
            var held = new List<Dictionary<string, string>>();
            foreach (var od in StringListObj(mod.GetValueOrDefault("overlay_dirs")))
            {
                string src = Path.Combine(gameDir, od);
                string dst = Path.Combine(modHold, od);
                if (Directory.Exists(src))
                {
                    if (Directory.Exists(dst)) dst += "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    Directory.Move(src, dst);
                    moved++;
                    int movePct = 15 + (int)Math.Round((moved / (double)Math.Max(1, totalToMove)) * 40.0);
                    progress?.Invoke(Math.Min(55, movePct), $"HOLD: MOVE {moved}/{Math.Max(1, totalToMove)}");
                    log($"Hold: {src} -> {dst}");
                    held.Add(new Dictionary<string, string> { ["original_dir"] = od, ["held_path"] = dst });
                }
                else
                {
                    log($"WARN: registered overlay does not exist in the game folder and could not be moved: {src}");
                    held.Add(new Dictionary<string, string> { ["original_dir"] = od, ["held_path"] = dst, ["missing_at_hold"] = "1" });
                }
            }
            mod["status"] = "held";
            mod["held_at"] = DateTime.Now.ToString("s");
            if (!string.IsNullOrWhiteSpace(pathcHoldSnapshot)) mod["pathc_hold_snapshot"] = pathcHoldSnapshot;
            mod["held_overlays"] = held;
        }
        string baseBackup = holdBase.BackupDir;
        progress?.Invoke(65, "HOLD: RESTORE BASE META");
        if (!string.IsNullOrWhiteSpace(baseBackup) && Directory.Exists(baseBackup))
        {
            if (RestoreMetaFromBackup(gameDir, baseBackup, log))
                log($"Smart Overlay Hold restored exact managed base revision {holdBase.Revision}: {baseBackup}");
            else
                throw new InvalidOperationException("Smart Overlay Hold could not safely restore the registered managed base meta backup.");
        }
        else
        {
            log("WARN: Smart Overlay Hold could not find a managed base meta backup. PAPGT will be rebuilt without held overlays, but PATHC may retain stale HD rows.");
        }

        progress?.Invoke(76, "HOLD: UPDATE REGISTRY");
        reg["mods"] = mods;
        SaveRegistry(gameDir, reg);
        progress?.Invoke(88, "HOLD: REBUILD PAPGT");
        byte[] papgt = new PapgtManager(gameDir).Rebuild(new Dictionary<string, byte[]>());
        SafeWrite(Path.Combine(gameDir, "meta", "0.papgt"), papgt);
        log("Smart Overlay Hold: rebuilt PAPGT without held overlay folders.");
        progress?.Invoke(100, "HOLD DONE");
        log($"Smart Overlay Hold finished. Folders moved out of the game folder: {moved}. Managed base revision restored: {holdBase.Revision}. Base backup: {baseBackup.DefaultIfEmpty("<none>")}. Registry: {RegistryPath(gameDir)}");
    }

    public static void ReleaseHoldAndReapply(string gameDir, Action<string> log, Action<int, string>? progress = null)
    {
        progress?.Invoke(2, "RELEASE: REPAIR REGISTRY");
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var reg = LoadRegistry(gameDir);
        var mods = ObjMods(reg);
        var heldMods = mods.Where(m => IsTextureMod(m) && string.Equals(SObj(m, "status"), "held", StringComparison.OrdinalIgnoreCase)).ToList();
        if (heldMods.Count == 0) throw new InvalidOperationException("No held mods were found to restore.");
        progress?.Invoke(8, "RELEASE: BACKUP META");
        CopyCurrentMetaBackup(gameDir, log);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modifiedPamts = new Dictionary<string, byte[]>();
        var releasedHoldParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var releaseDirMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        int totalToRelease = heldMods.Sum(m => HeldOverlayList(m).Count);
        int releasedCount = 0;
        progress?.Invoke(15, totalToRelease > 0 ? $"RELEASE: RESTORE 0/{totalToRelease}" : "RELEASE: RESTORE OVERLAYS");
        foreach (var mod in heldMods)
        {
            var newDirs = new List<string>();
            var releaseDirMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in HeldOverlayList(mod))
            {
                if (!item.TryGetValue("original_dir", out var old) || string.IsNullOrWhiteSpace(old)) continue;
                if (!item.TryGetValue("held_path", out var heldPath) || !Directory.Exists(heldPath)) { log($"WARN: held overlay does not exist and will be skipped: {heldPath}"); continue; }
                string targetName = old;
                string target = Path.Combine(gameDir, targetName);
                if (Directory.Exists(target) || reserved.Contains(targetName))
                {
                    targetName = NextFreeOverlayDir(gameDir, reserved);
                    target = Path.Combine(gameDir, targetName);
                    log($"Release: {old} is already occupied. It will be restored as {targetName}.");
                }
                reserved.Add(targetName);
                string? heldParent = Directory.GetParent(heldPath)?.FullName;
                Directory.Move(heldPath, target);
                releasedCount++;
                int releasePct = 15 + (int)Math.Round((releasedCount / (double)Math.Max(1, totalToRelease)) * 35.0);
                progress?.Invoke(Math.Min(50, releasePct), $"RELEASE: RESTORE {releasedCount}/{Math.Max(1, totalToRelease)}");
                if (!string.IsNullOrWhiteSpace(heldParent)) releasedHoldParents.Add(heldParent);
                newDirs.Add(targetName);
                releaseDirMap[old] = targetName;
                log($"Release: {heldPath} -> {target}");
                string pamt = Path.Combine(target, "0.pamt");
                if (File.Exists(pamt)) modifiedPamts[targetName] = File.ReadAllBytes(pamt);
            }
            mod["status"] = "active";
            mod["released_at"] = DateTime.Now.ToString("s");
            mod["overlay_dirs"] = newDirs;
            if (releaseDirMap.Count > 0) releaseDirMaps[SObj(mod, "mod_id")] = releaseDirMap;
            mod["release_dir_map"] = releaseDirMap;
            mod["held_overlays"] = new List<Dictionary<string, string>>();
        }
        progress?.Invoke(55, "RELEASE: UPDATE REGISTRY");
        reg["mods"] = mods;
        SaveRegistry(gameDir, reg);

        var releaseReplaySnapshotPaths = new List<string>();
        foreach (var mod in heldMods)
        {
            string snap = SObj(mod, "pathc_hold_snapshot");
            if (!string.IsNullOrWhiteSpace(snap)) releaseReplaySnapshotPaths.Add(snap);
            string mpForSnap = SObj(mod, "manifest_copy");
            if (string.IsNullOrWhiteSpace(mpForSnap) || !File.Exists(mpForSnap)) mpForSnap = SObj(mod, "original_manifest");
            var manifestInfo = !string.IsNullOrWhiteSpace(mpForSnap) && File.Exists(mpForSnap) ? ReadJsonDict(mpForSnap) : null;
            string applySnap = manifestInfo != null ? SObj(manifestInfo, "pathc_replay_snapshot") : "";
            if (!string.IsNullOrWhiteSpace(applySnap)) releaseReplaySnapshotPaths.Add(applySnap);
        }
        var releaseReplaySources = LoadPathcReplaySources(releaseReplaySnapshotPaths, log, "Release Hold + Reapply");

        // Reapply PATHC on top of the current meta files. This is the whole
        // point of Smart Hold: let DMM/JSON Mod Manager change meta while the
        // texture overlays are out of the way, then replay our texture rows
        // against that newly modded meta instead of restoring stale before hold meta.
        log("Validating texture metadata for PATHC replay. This may take a while on large builds or slower storage.");
        int replayIndex = 0;
        foreach (var mod in heldMods)
        {
            replayIndex++;
            progress?.Invoke(60 + (int)Math.Round(((replayIndex - 1) / (double)Math.Max(1, heldMods.Count)) * 25.0), $"RELEASE: REAPPLY PATHC {replayIndex}/{heldMods.Count}");
            string mp = SObj(mod, "manifest_copy");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) mp = SObj(mod, "original_manifest");
            if (string.IsNullOrWhiteSpace(mp) || !File.Exists(mp)) { log($"WARN: released build has no readable manifest for PATHC replay: {SObj(mod, "mod_id")}"); continue; }
            var matches = ReadManifestMatches(mp);
            var entries = ReadManifestOverlayEntries(mp);
            if (releaseDirMaps.TryGetValue(SObj(mod, "mod_id"), out var releaseDirMapForMod))
            {
                foreach (var e in entries)
                {
                    if (!string.IsNullOrWhiteSpace(e.OverlayDir) && releaseDirMapForMod.TryGetValue(e.OverlayDir, out var newOwner)) e.OverlayDir = newOwner;
                }
            }
            if (matches.Count == 0 || entries.Count == 0) continue;
            int replayStart = 60 + (int)Math.Round(((replayIndex - 1) / (double)Math.Max(1, heldMods.Count)) * 25.0);
            int replayEnd = 60 + (int)Math.Round((replayIndex / (double)Math.Max(1, heldMods.Count)) * 25.0);
            var replay = UpdatePathcForMatches(gameDir, matches, entries, log, progress, replayStart, Math.Max(replayStart, replayEnd), $"RELEASE: PATHC {replayIndex}/{heldMods.Count}", releaseReplaySources);
            if (replay.bytes != null) SafeWrite(Path.Combine(gameDir, "meta", "0.pathc"), replay.bytes);
        }

        progress?.Invoke(88, "RELEASE: REBUILD PAPGT");
        byte[] papgt = new PapgtManager(gameDir).Rebuild(modifiedPamts);
        SafeWrite(Path.Combine(gameDir, "meta", "0.papgt"), papgt);
        progress?.Invoke(95, "RELEASE: CLEAN HOLD");
        var holdRoots = RegistryHoldRoots(gameDir)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();
        foreach (var parent in releasedHoldParents)
        {
            string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string holdRoot in holdRoots)
            {
                if (fullParent.Equals(holdRoot, StringComparison.OrdinalIgnoreCase)
                    || fullParent.StartsWith(holdRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullParent.StartsWith(holdRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    string holdRootTop = Directory.GetParent(holdRoot)?.FullName ?? holdRoot;
                    CleanEmptyDirectoryUpTo(parent, holdRootTop, log);
                    break;
                }
            }
        }
        progress?.Invoke(100, "RELEASE DONE");
        log("Release Hold + Reapply: rebuilt PAPGT with released overlay folders.");
        log($"Release Hold + Reapply finished. Overlays present: {modifiedPamts.Count}.");
    }

    public static string CreateEasyApplyRollbackBackup(string gameDir, Action<string> log)
    {
        AutoRepairRegistryFromLocalManifests(gameDir, log);
        var reg = LoadRegistry(gameDir);
        var textureMods = ObjMods(reg).Where(IsTextureMod).ToList();
        if (textureMods.Count == 0) return string.Empty;

        string root = Path.Combine(EasyApplyRollbackGameRoot(gameDir), DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(root);

        string metaDst = Path.Combine(root, "meta");
        Directory.CreateDirectory(metaDst);
        foreach (var rel in new[] { Path.Combine("meta", "0.papgt"), Path.Combine("meta", "0.pathc") })
        {
            string src = Path.Combine(gameDir, rel);
            if (File.Exists(src)) File.Copy(src, Path.Combine(root, rel), true);
        }

        foreach (var registryRoot in RegistryRoots(gameDir).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(registryRoot)) CopyRegistryStateDirectory(registryRoot, Path.Combine(root, Path.GetFileName(registryRoot)));
        }

        string overlaysRoot = Path.Combine(root, "overlays");
        Directory.CreateDirectory(overlaysRoot);
        var overlayDirs = textureMods
            .SelectMany(m => StringListObj(m.GetValueOrDefault("overlay_dirs")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int moved = 0;
        int copied = 0;
        string rollbackManifestPath = Path.Combine(root, "rollback_manifest.json");
        WriteJson(rollbackManifestPath, new
        {
            created_at = DateTime.Now.ToString("s"),
            game_dir = gameDir,
            overlay_dirs = overlayDirs,
            moved_overlay_count = moved,
            copied_overlay_count = copied
        });

        try
        {
            foreach (var od in overlayDirs)
            {
                string src = Path.Combine(gameDir, od);
                if (!Directory.Exists(src)) continue;
                string dst = Path.Combine(overlaysRoot, od);

                // UI11: rollback protects Easy Apply-over-existing builds by moving
                // managed overlays out of the game folder when possible instead of
                // copying tens/hundreds of GiB and then deleting the originals.  Same
                // volume moves are metadata-only and avoid poisoning the fresh apply
                // with a huge pre-copy/delete I/O pass.  If a move is not possible,
                // fall back to the older copy behavior and let Remove Current Build
                // delete the original overlay afterward.
                bool fastMoved = MoveDirectoryFastOrCopyFallback(src, dst, log, "Easy Apply rollback");
                if (fastMoved) moved++; else copied++;
            }
        }
        catch
        {
            log("WARN: Easy Apply rollback creation failed; restoring any overlays already moved to rollback.");
            RestoreEasyApplyRollbackBackup(gameDir, root, log);
            throw;
        }

        WriteJson(rollbackManifestPath, new
        {
            created_at = DateTime.Now.ToString("s"),
            game_dir = gameDir,
            overlay_dirs = overlayDirs,
            moved_overlay_count = moved,
            copied_overlay_count = copied
        });
        log($"Easy Apply rollback backup created: {root} (overlays moved: {moved}, copied fallback: {copied}).");
        return root;
    }

    public static void RestoreEasyApplyRollbackBackup(string gameDir, string rollbackRoot, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(rollbackRoot) || !Directory.Exists(rollbackRoot)) return;
        log("Easy Apply rollback: restoring previous managed build...");

        var manifest = ReadJsonDict(Path.Combine(rollbackRoot, "rollback_manifest.json"));
        var overlayDirs = manifest != null ? StringListObj(manifest.GetValueOrDefault("overlay_dirs")) : new List<string>();

        foreach (var od in overlayDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string dst = Path.Combine(gameDir, od);
            string src = Path.Combine(rollbackRoot, "overlays", od);
            if (Directory.Exists(src))
            {
                try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch { }
                MoveDirectoryFastOrCopyFallback(src, dst, log, "Easy Apply rollback restore");
            }
        }

        foreach (var folderName in new[] { ToolDataFolderName, PreviousToolDataFolderName, LegacyToolDataFolderName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string src = Path.Combine(rollbackRoot, folderName);
            if (!Directory.Exists(src)) continue;
            string dst = Path.Combine(gameDir, folderName);
            try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch { }
            CopyDirectory(src, dst);
        }

        foreach (var rel in new[] { Path.Combine("meta", "0.papgt"), Path.Combine("meta", "0.pathc") })
        {
            string src = Path.Combine(rollbackRoot, rel);
            string dst = Path.Combine(gameDir, rel);
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
            }
        }
        log("Easy Apply rollback: previous managed build restored.");
        DeleteEasyApplyRollbackBackup(rollbackRoot, log, "Easy Apply rollback backup deleted after successful rollback restore.");
    }

    public static void DeleteEasyApplyRollbackBackup(string rollbackRoot, Action<string> log) => DeleteEasyApplyRollbackBackup(rollbackRoot, log, "Easy Apply rollback backup deleted after successful apply.");

    private static void DeleteEasyApplyRollbackBackup(string rollbackRoot, Action<string> log, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(rollbackRoot) || !Directory.Exists(rollbackRoot)) return;
        try
        {
            string? parent = Directory.GetParent(rollbackRoot)?.FullName;
            string? stop = parent != null ? Directory.GetParent(parent)?.FullName : null;
            Directory.Delete(rollbackRoot, true);
            log(successMessage);
            if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(stop)) CleanEmptyDirectoryUpTo(parent, stop, log, "rollback");
        }
        catch (Exception ex)
        {
            log($"WARN: could not delete Easy Apply rollback backup: {ex.Message}");
        }
    }

    private static readonly HashSet<string> RegistryStateBackupExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Prevent recursive backup nesting and avoid copying transient safety state.
        "backups",
        "registry_state",
        "cancel_hotfix_backups",
        "temp",
        "tmp",
        "hold",
        "rollback",
        "__manifest_backup"
    };

    private static void CopyRegistryStateDirectory(string sourceDir, string destDir)
        => CopyDirectory(sourceDir, destDir, RegistryStateBackupExcludedFolderNames);

    private static void CopyDirectory(string sourceDir, string destDir)
        => CopyDirectory(sourceDir, destDir, null);

    private static void CopyDirectory(string sourceDir, string destDir, ISet<string>? excludedFolderNames)
    {
        string sourceFull = NormalizeFullPathForChildCheck(sourceDir);
        string destFull = NormalizeFullPathForChildCheck(destDir);

        if (excludedFolderNames == null && IsSameOrChildPath(destFull, sourceFull))
            throw new InvalidOperationException($"Refusing to copy a folder into itself: {sourceDir} -> {destDir}");

        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            string dst = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, dst, true);
        }
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name) && excludedFolderNames?.Contains(name) == true) continue;

            string dirFull = NormalizeFullPathForChildCheck(dir);
            // If the destination is inside this source child, copying that child would
            // recurse into the backup being created.  Skip that child even if its
            // folder name was not covered by the exclusion list.
            if (IsSameOrChildPath(destFull, dirFull)) continue;

            CopyDirectory(dir, Path.Combine(destDir, name), excludedFolderNames);
        }
    }

    private static string NormalizeFullPathForChildCheck(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrChildPath(string candidatePath, string parentPath)
    {
        if (string.Equals(candidatePath, parentPath, StringComparison.OrdinalIgnoreCase)) return true;
        return candidatePath.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || candidatePath.StartsWith(parentPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MoveDirectoryFastOrCopyFallback(string sourceDir, string destDir, Action<string> log, string label)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destDir)!);
        try
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            Directory.Move(sourceDir, destDir);
            log($"{label}: moved folder: {sourceDir} -> {destDir}");
            return true;
        }
        catch (Exception moveEx)
        {
            log($"WARN: {label}: fast folder move failed ({moveEx.Message}); falling back to copy.");
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            CopyDirectory(sourceDir, destDir);
            log($"{label}: copied folder fallback: {sourceDir} -> {destDir}");
            return false;
        }
    }

    public static void SafeWrite(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }


    private sealed record SkippedBreakdown(int NotFound, int DuplicateSourceIgnored, int FailedSkipped);

    private static SkippedBreakdown ClassifySkipped(IEnumerable<string> skipped)
    {
        int notFound = 0;
        int duplicateSourceIgnored = 0;
        int failedSkipped = 0;
        foreach (var raw in skipped)
        {
            string line = raw ?? string.Empty;
            if (line.Contains("duplicate internal target", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("destino interno duplicado", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Packed only once", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Empaquetado una sola vez", StringComparison.OrdinalIgnoreCase))
            {
                duplicateSourceIgnored++;
            }
            else if (line.Contains("not found in vanilla PAMT", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("no encontrado en PAMT vanilla", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("(not_found)", StringComparison.OrdinalIgnoreCase))
            {
                notFound++;
            }
            else
            {
                failedSkipped++;
            }
        }
        return new SkippedBreakdown(notFound, duplicateSourceIgnored, failedSkipped);
    }

    private static void SplitSkipped(IEnumerable<string> skipped, out List<string> notFound, out List<string> duplicateSourceIgnored, out List<string> failedSkipped)
    {
        notFound = new List<string>();
        duplicateSourceIgnored = new List<string>();
        failedSkipped = new List<string>();
        foreach (var line in skipped)
        {
            var b = ClassifySkipped(new[] { line });
            if (b.DuplicateSourceIgnored > 0) duplicateSourceIgnored.Add(line);
            else if (b.NotFound > 0) notFound.Add(line);
            else failedSkipped.Add(line);
        }
    }

    private static void WriteReport(string reportPath, BuildOptions o, List<MatchedFile> matches, List<string> skipped, List<string> ambiguous, List<string> newOverlays, List<string> updatedOverlays, Dictionary<string,int> stats, List<PathcUpdateResult> pathcSummaries, List<LooseMatchDiagnostic> looseDiagnostics)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        bool en = !CDTextureOverlayBuilder.L.IsSpanish(o.Language);
        using var f = new StreamWriter(reportPath, false, Encoding.UTF8);
        string overlayLine = string.Join(", ", newOverlays);
        string updatedLine = string.Join(", ", updatedOverlays);
        var pathcUpdated = pathcSummaries.SelectMany(s => s.UpdatedPaths).ToList();
        var pathcAdded = pathcSummaries.SelectMany(s => s.AddedPaths).ToList();
        var pathcUnchanged = pathcSummaries.SelectMany(s => s.UnchangedPaths).ToList();
        var pathcPackedNoEditableMetadata = pathcSummaries.SelectMany(s => s.PackedWithoutEditableMetadataPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pathcSkipped = pathcSummaries.SelectMany(s => s.SkippedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int pUpdated = pathcSummaries.Sum(s => s.Updated);
        int pAdded = pathcSummaries.Sum(s => s.Added);
        int pUnchanged = pathcSummaries.Sum(s => s.Unchanged);
        int pPackedNoEditableMetadata = pathcPackedNoEditableMetadata.Count;
        int pSkipped = pathcSkipped.Count;
        SplitSkipped(skipped, out var notFoundSkipped, out var duplicateSourceIgnoredSkipped, out var failedSkipped);

        if (en)
        {
            f.WriteLine($"{AppName} v{AppVersion}");
            f.WriteLine($"Date: {DateTime.Now:s}");
            f.WriteLine($"Game: {o.GameDir}");
            f.WriteLine($"Textures: {o.TextureDir}");
            string sourceScope = DetectSourceRootArchive(o.TextureDir);
            f.WriteLine($"Matching policy: {(o.ApplyDuplicateLooseMatches ? "Place duplicates everywhere found (advanced/risky)" : "Safe Primary default")}");
            f.WriteLine(string.IsNullOrEmpty(sourceScope) ? "Source layout: flat/auto Safe Primary" : $"Source layout: archive-scoped Safe Primary ({sourceScope})");
            f.WriteLine($"Source PAMT filter: {NormalizePamt(o.TargetPamtDir).DefaultIfEmpty("ALL")}");
            f.WriteLine($"Internal path filter: {NormalizePrefix(o.TargetFullPrefix).DefaultIfEmpty("ALL")}");
            f.WriteLine($"Mode: {(o.ApplyToGame ? "APPLIED TO GAME" : "BUILD ONLY")}");
            f.WriteLine($"New overlay dirs: {overlayLine.DefaultIfEmpty("none")}");
            f.WriteLine($"Updated existing overlays: {updatedLine.DefaultIfEmpty("none")}");
            f.WriteLine($"Matched textures: {matches.Count} ({matches.Sum(m => m.Size) / 1024.0 / 1024.0:F1} MB source)");
            f.WriteLine($"Not found: {notFoundSkipped.Count}");
            f.WriteLine($"Already covered duplicate sources: {duplicateSourceIgnoredSkipped.Count}");
            f.WriteLine($"Failed/skipped: {failedSkipped.Count}");
            f.WriteLine($"Ambiguous: {ambiguous.Count}");
            f.WriteLine($"Stats: {JsonSerializer.Serialize(stats)}");
            f.WriteLine("Archive distribution: " + string.Join(", ", matches.GroupBy(m => m.PamtDir).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count():n0}")));
            if (stats.TryGetValue("safe_primary_rejected_secondary_duplicate_targets", out int avoidedFanout) && avoidedFanout > 0) f.WriteLine($"Legacy fan-out avoided: {avoidedFanout:n0} secondary target(s) rejected by Safe Primary.");
            if (pathcSummaries.Count > 0) f.WriteLine($"PATHC: {pUpdated} updated, {pAdded} added, {pUnchanged} unchanged, {pPackedNoEditableMetadata} packed without editable metadata, {pSkipped} skipped");
            f.WriteLine();
            f.WriteLine("=== MATCHED ===");
            foreach (var m in matches) f.WriteLine($"{m.RelPath} -> {m.PamtDir}:{m.FullPath} | entry={m.EntryPath} | method={m.MatchMethod.DefaultIfEmpty("unknown")} | {m.Size} bytes");
            WritePathcReportSections(f, true, pathcUpdated, pathcAdded, pathcUnchanged, pathcPackedNoEditableMetadata, pathcSkipped);
            f.WriteLine("\n=== AMBIGUOUS ==="); foreach (var line in ambiguous) f.WriteLine(line);
            WriteLooseMatchDiagnosticsReport(f, looseDiagnostics, english: true);
            f.WriteLine("\n=== NOT FOUND ==="); foreach (var line in notFoundSkipped) f.WriteLine(line);
            f.WriteLine("\n=== ALREADY COVERED DUPLICATE SOURCES ==="); foreach (var line in duplicateSourceIgnoredSkipped) f.WriteLine(line);
            f.WriteLine("\n=== FAILED / SKIPPED ==="); foreach (var line in failedSkipped) f.WriteLine(line);
        }
        else
        {
            f.WriteLine($"{AppName} v{AppVersion}");
            f.WriteLine($"Fecha: {DateTime.Now:s}");
            f.WriteLine($"Juego: {o.GameDir}");
            f.WriteLine($"Texturas: {o.TextureDir}");
            string sourceScope = DetectSourceRootArchive(o.TextureDir);
            f.WriteLine($"Politica de coincidencia: {(o.ApplyDuplicateLooseMatches ? "Colocar duplicados en todos los lugares encontrados (avanzado/riesgoso)" : "Primario Seguro predeterminado")}");
            f.WriteLine(string.IsNullOrEmpty(sourceScope) ? "Diseno de fuente: carpeta plana / Primario Seguro automatico" : $"Diseno de fuente: Primario Seguro con scope de archivo ({sourceScope})");
            f.WriteLine($"Filtro PAMT origen: {NormalizePamt(o.TargetPamtDir).DefaultIfEmpty("TODOS")}");
            f.WriteLine($"Filtro ruta interna: {NormalizePrefix(o.TargetFullPrefix).DefaultIfEmpty("TODAS")}");
            f.WriteLine($"Modo: {(o.ApplyToGame ? "APLICADO AL JUEGO" : "SOLO CONSTRUIR")}");
            f.WriteLine($"Carpetas overlay nuevas: {overlayLine.DefaultIfEmpty("ninguna")}");
            f.WriteLine($"Overlays existentes actualizados: {updatedLine.DefaultIfEmpty("ninguno")}");
            f.WriteLine($"Texturas coincidentes: {matches.Count} ({matches.Sum(m => m.Size) / 1024.0 / 1024.0:F1} MB fuente)");
            f.WriteLine($"No encontradas: {notFoundSkipped.Count}");
            f.WriteLine($"Fuentes duplicadas ya cubiertas: {duplicateSourceIgnoredSkipped.Count}");
            f.WriteLine($"Fallidas/omitidas: {failedSkipped.Count}");
            f.WriteLine($"Ambiguas: {ambiguous.Count}");
            f.WriteLine($"Estadísticas: {JsonSerializer.Serialize(stats)}");
            f.WriteLine("Distribucion por archivo: " + string.Join(", ", matches.GroupBy(m => m.PamtDir).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count():n0}")));
            if (stats.TryGetValue("safe_primary_rejected_secondary_duplicate_targets", out int avoidedFanout) && avoidedFanout > 0) f.WriteLine($"Fan-out legacy evitado: {avoidedFanout:n0} objetivo(s) secundarios rechazados por Primario Seguro.");
            if (pathcSummaries.Count > 0) f.WriteLine($"PATHC: {pUpdated} actualizadas, {pAdded} agregadas, {pUnchanged} sin cambios, {pPackedNoEditableMetadata} empaquetadas sin metadatos editables, {pSkipped} omitidas");
            f.WriteLine();
            f.WriteLine("=== COINCIDENTES ===");
            foreach (var m in matches) f.WriteLine($"{m.RelPath} -> {m.PamtDir}:{m.FullPath} | entrada={m.EntryPath} | método={m.MatchMethod.DefaultIfEmpty("desconocido")} | {m.Size} bytes");
            WritePathcReportSections(f, false, pathcUpdated, pathcAdded, pathcUnchanged, pathcPackedNoEditableMetadata, pathcSkipped);
            f.WriteLine("\n=== AMBIGUAS ==="); foreach (var line in ambiguous) f.WriteLine(line);
            WriteLooseMatchDiagnosticsReport(f, looseDiagnostics, english: false);
            f.WriteLine("\n=== NO ENCONTRADAS ==="); foreach (var line in notFoundSkipped) f.WriteLine(line);
            f.WriteLine("\n=== FUENTES DUPLICADAS YA CUBIERTAS ==="); foreach (var line in duplicateSourceIgnoredSkipped) f.WriteLine(line);
            f.WriteLine("\n=== FALLIDAS / OMITIDAS ==="); foreach (var line in failedSkipped) f.WriteLine(line);
        }
    }


    private static void WriteLooseMatchDiagnosticsReport(StreamWriter f, List<LooseMatchDiagnostic> diagnostics, bool english)
    {
        f.WriteLine(english ? "\n=== LOOSE / DUPLICATE MATCH DIAGNOSTICS (SAFE PRIMARY DEFAULT) ===" : "\n=== DIAGNOSTICO DE COINCIDENCIAS SUELTAS / DUPLICADAS (PRIMARIO SEGURO) ===");
        f.WriteLine(english
            ? "Safe Primary applies one confident target per source by default. Legacy fan-out decisions are shown as diagnostics when available and only apply when the advanced legacy option is enabled."
            : "Primario Seguro aplica un objetivo confiable por fuente de forma predeterminada. Las decisiones legacy de expansion se muestran como diagnostico cuando corresponde y solo se aplican si la opcion avanzada legacy esta activada.");
        if (diagnostics.Count == 0)
        {
            f.WriteLine(english ? "none" : "ninguna");
            return;
        }
        foreach (var d in diagnostics)
        {
            f.WriteLine($"\nSOURCE: {d.SourceRel}");
            f.WriteLine($"  source_basename: {d.SourceBasename}");
            f.WriteLine($"  source_dds: {d.SourceDdsInfo}");
            f.WriteLine($"  match_method: {d.MatchMethod}");
            f.WriteLine($"  selected_primary_target: {d.SelectedPrimaryTarget}");
            f.WriteLine($"  final_decision: {d.FinalDecision}");
            f.WriteLine($"  candidates: {d.Candidates.Count}");
            foreach (var c in d.Candidates)
            {
                f.WriteLine($"    - {c.PamtDir}:{c.FullPath}");
                f.WriteLine($"      entry_path: {c.EntryPath}");
                f.WriteLine($"      reason: {c.Reason}");
                f.WriteLine($"      exact_basename: {c.ExactBasename}");
                f.WriteLine($"      suffix_type: source={c.SourceSuffixType}, candidate={c.CandidateSuffixType}, exact={c.ExactSuffixType}");
                f.WriteLine($"      dimensions_format: {c.DimensionFormatCompatibility}");
                f.WriteLine($"      legacy_fanout_would_apply: {c.Hotfix2LegacyWouldApply}");
                f.WriteLine($"      strict_hotfix3_would_apply: {c.Hotfix3StrictWouldApply}");
                f.WriteLine($"      final_decision: {c.FinalDecision}");
            }
        }
    }

    private static void WritePathcReportSections(StreamWriter f, bool en, List<string> updated, List<string> added, List<string> unchanged, List<string> packedNoEditableMetadata, List<string> skipped)
    {
        if (updated.Count + added.Count + unchanged.Count + packedNoEditableMetadata.Count + skipped.Count == 0) return;
        if (en)
        {
            f.WriteLine("\n=== PATHC UPDATED ==="); foreach (var line in updated) f.WriteLine(line);
            f.WriteLine("\n=== PATHC ADDED ==="); foreach (var line in added) f.WriteLine(line);
            f.WriteLine("\n=== PATHC UNCHANGED ==="); foreach (var line in unchanged) f.WriteLine(line);
            f.WriteLine("\n=== PATHC PACKED - METADATA NOT EDITABLE ===");
            if (packedNoEditableMetadata.Count > 0) f.WriteLine("These files were packed into the overlay, but the stock PATHC row did not expose editable metadata values.");
            foreach (var line in packedNoEditableMetadata) f.WriteLine(line);
            f.WriteLine("\n=== PATHC SKIPPED ==="); foreach (var line in skipped) f.WriteLine(line);
        }
        else
        {
            f.WriteLine("\n=== PATHC ACTUALIZADAS ==="); foreach (var line in updated) f.WriteLine(line);
            f.WriteLine("\n=== PATHC AGREGADAS ==="); foreach (var line in added) f.WriteLine(line);
            f.WriteLine("\n=== PATHC SIN CAMBIOS ==="); foreach (var line in unchanged) f.WriteLine(line);
            f.WriteLine("\n=== PATHC EMPAQUETADAS - METADATOS NO EDITABLES ===");
            if (packedNoEditableMetadata.Count > 0) f.WriteLine("Estos archivos se empaquetaron en el overlay, pero la fila stock de PATHC no tenía valores de metadatos editables.");
            foreach (var line in packedNoEditableMetadata) f.WriteLine(line);
            f.WriteLine("\n=== PATHC OMITIDAS ==="); foreach (var line in skipped) f.WriteLine(line);
        }
    }

    private static void WriteJson(string path, object data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string NormalizePamt(string s) { s = (s ?? "").Trim(); if (string.IsNullOrEmpty(s)) return ""; if (int.TryParse(s, out int n)) return n.ToString("D4"); return s; }
    private static string NormalizePrefix(string s) => (s ?? "").Replace('\\','/').Trim().Trim('/').ToLowerInvariant();
    private static string SafeName(string s) => string.Concat((s ?? "TextureOverlay").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' ? c : '_')).Trim().DefaultIfEmpty("TextureOverlay");
}

internal static class StringExtensions
{
    public static string DefaultIfEmpty(this string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;
}
