namespace CDTextureOverlayBuilder.Core;

public sealed record IndexCandidate(
    string PamtDir,
    string EntryPath,
    string FullPath,
    string Filename,
    int CompressionType,
    bool Encrypted,
    string CryptoFilename,
    uint Flags,
    uint CompSize,
    uint OrigSize);

public sealed class MatchedFile
{
    public string SourcePath { get; init; } = "";
    public string RelPath { get; init; } = "";
    public long Size { get; init; }
    public string PamtDir { get; init; } = "";
    public string EntryPath { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Filename { get; init; } = "";
    public int CompressionType { get; init; }
    public bool Encrypted { get; init; }
    public string CryptoFilename { get; init; } = "";
    public string MatchMethod { get; init; } = "";

    public Dictionary<string, object?> Metadata()
    {
        var st = new FileInfo(SourcePath);
        return new Dictionary<string, object?>
        {
            ["entry_path"] = EntryPath,
            ["pamt_dir"] = PamtDir,
            ["compression_type"] = CompressionType,
            ["encrypted"] = Encrypted,
            ["crypto_filename"] = CryptoFilename,
            ["source_path"] = SourcePath,
            ["full_path"] = FullPath,
            ["delta_hash"] = st.Exists ? $"{st.Length}:{new DateTimeOffset(st.LastWriteTimeUtc).ToUnixTimeSeconds()}" : "",
            ["match_method"] = MatchMethod
        };
    }
}

public sealed record BuildOptions(
    string GameDir,
    string TextureDir,
    string OutputDir,
    string ModName,
    bool ApplyToGame,
    bool AllowUniqueFilename,
    bool DryRun,
    double SplitGb,
    bool BackupMeta,
    bool ScanExistingModDirs,
    bool ApplyDuplicateLooseMatches,
    bool UpdateExistingOverlays,
    string TargetPamtDir,
    string TargetFullPrefix,
    string Language,
    string PerformanceMemoryMode,
    int CustomPrepareWorkers);

public sealed record InstalledBuildSummary(
    string ModId,
    string Label,
    string Status,
    List<string> OverlayDirs,
    int MatchedCount,
    string TargetPamtDir,
    string TargetFullPrefix,
    string CreatedAt,
    List<string> UpdatedOverlayDirs,
    int NewOverlayTextureCount,
    int UpdatedExistingCount);

public sealed record BuildResult(
    int MatchedCount,
    int SkippedCount,
    int AmbiguousCount,
    List<string> OverlayDirs,
    string OutputDir,
    string ManifestPath,
    string ReportPath,
    bool Applied,
    double ElapsedSeconds,
    int NotFoundCount,
    int DuplicateSourceIgnoredCount,
    int FailedSkippedCount);


public sealed class PreparedOverlayPayload
{
    public OverlayEntry Entry { get; }
    public byte[] Payload { get; set; }

    public PreparedOverlayPayload(OverlayEntry entry, byte[] payload)
    {
        Entry = entry;
        Payload = payload;
    }
}

public sealed record PathcUpdateResult(
    int Updated,
    int Added,
    int Unchanged,
    int PackedWithoutEditableMetadata,
    int Skipped,
    int RetainedExisting,
    int TotalRows,
    int StartingRows,
    List<string> UpdatedPaths,
    List<string> AddedPaths,
    List<string> UnchangedPaths,
    List<string> PackedWithoutEditableMetadataPaths,
    List<string> SkippedPaths);

public sealed class OverlayEntry
{
    public string DirPath { get; set; } = "";
    public string Filename { get; set; } = "";
    public uint PazIndex { get; set; }
    public uint PazOffset { get; set; }
    public uint CompSize { get; set; }
    public uint DecompSize { get; set; }
    public ushort Flags { get; set; }
    public uint[]? DdsMValues { get; set; }
    public uint DdsLast4 { get; set; }
    public string EntryPath { get; set; } = "";

    // Source-independent PATHC replay support.  Release Hold + Reapply and
    // Relink should not require the original extracted DDS source folder after
    // the managed HD## overlays have already been built.
    public string OverlayDir { get; set; } = "";
    public byte[]? DdsPathcHeader { get; set; }
}
