using System.Text;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace CDTextureOverlayBuilder.Core;

public static class OverlayBuilder
{
    private const uint HashSeed = 0xC5EDE;
    private const int PazAlignment = 16;
    private const uint PamtConstant = 0x610E0232;
    private const long StockStylePazPartTargetBytes = 943718400L; // 900 MiB normal PAZ part cap
    private const long SafeUInt32PazFieldLimitBytes = 0xFFFFFFFFL; // PAMT PAZ offsets/sizes are UInt32
    private const long OneGiB = 1024L * 1024L * 1024L;
    private const long OneMiB = 1024L * 1024L;

    public sealed class OverlayBuildTimings
    {
        private double _payloadPrepSeconds;
        private double _pazWriteSeconds;
        private double _pazCreatePreallocateSeconds;
        private double _pazPayloadWriteSeconds;
        private double _pazCrcHashSeconds;
        private double _pazFinalizeSeconds;
        private double _pamtBuildSeconds;

        public double PayloadPrepSeconds => _payloadPrepSeconds;
        public double PazWriteSeconds => _pazWriteSeconds;
        public double PazCreatePreallocateSeconds => _pazCreatePreallocateSeconds;
        public double PazPayloadWriteSeconds => _pazPayloadWriteSeconds;
        public double PazCrcHashSeconds => _pazCrcHashSeconds;
        public double PazFinalizeSeconds => _pazFinalizeSeconds;
        public double PamtBuildSeconds => _pamtBuildSeconds;

        public void AddPayloadPrep(double seconds) => _payloadPrepSeconds += Math.Max(0, seconds);
        public void AddPazWrite(double seconds) => _pazWriteSeconds += Math.Max(0, seconds);
        public void AddPazCreatePreallocate(double seconds) => _pazCreatePreallocateSeconds += Math.Max(0, seconds);
        public void AddPazPayloadWrite(double seconds) => _pazPayloadWriteSeconds += Math.Max(0, seconds);
        public void AddPazCrcHash(double seconds) => _pazCrcHashSeconds += Math.Max(0, seconds);
        public void AddPazFinalize(double seconds) => _pazFinalizeSeconds += Math.Max(0, seconds);
        public void AddPamtBuild(double seconds) => _pamtBuildSeconds += Math.Max(0, seconds);
    }

    public sealed class DeferredOverlayInput
    {
        private readonly Func<ConcurrentDictionary<string, Dictionary<string, string>>, Dictionary<uint, uint>, PreparedOverlayPayload> _prepare;

        public string DisplayPath { get; }
        public long EstimatedPayloadLength { get; }

        public DeferredOverlayInput(
            string displayPath,
            long estimatedPayloadLength,
            Func<ConcurrentDictionary<string, Dictionary<string, string>>, Dictionary<uint, uint>, PreparedOverlayPayload> prepare)
        {
            DisplayPath = displayPath;
            EstimatedPayloadLength = Math.Max(0, estimatedPayloadLength);
            _prepare = prepare;
        }

        public PreparedOverlayPayload Prepare(
            ConcurrentDictionary<string, Dictionary<string, string>> fullPathMapCache,
            Dictionary<uint, uint> pathcLast4Map)
            => _prepare(fullPathMapCache, pathcLast4Map);

        public static DeferredOverlayInput FromSource(string sourcePath, Dictionary<string, object?> metadata, string gameDir)
        {
            string entryPath = S(metadata, "entry_path").Replace('\\','/');
            long len = new FileInfo(sourcePath).Length;
            if (len > SafeUInt32PazFieldLimitBytes)
                throw new InvalidDataException($"Single source payload exceeds the 4 GiB PAZ/PAMT UInt32 limit and was not packed: {entryPath} ({FormatBytes(len)})");

            return new DeferredOverlayInput(entryPath, len, (fullPathMapCache, pathcLast4Map) =>
                PreparePayloadFromSource(sourcePath, metadata, gameDir, fullPathMapCache, pathcLast4Map));
        }

        public static DeferredOverlayInput FromPrepared(PreparedOverlayPayload payload)
            => new(payload.Entry.EntryPath, payload.Payload.LongLength, (_, _) => payload);
    }

    public static (byte[] pamtBytes, List<OverlayEntry> entries, long pazSize) BuildOverlayToFile(
        List<(string sourcePath, Dictionary<string, object?> metadata)> inputs,
        string pazPath,
        string gameDir,
        Action<int, int, string>? progress,
        string? vanillaPathcPath,
        CancellationToken cancellationToken = default,
        string memoryMode = "Auto",
        int customPrepareWorkers = 0,
        OverlayBuildTimings? timings = null)
    {
        var deferred = inputs.Select(i => DeferredOverlayInput.FromSource(i.sourcePath, i.metadata, gameDir)).ToList();
        return BuildOverlayFromDeferredInputsToFile(deferred, pazPath, gameDir, progress, vanillaPathcPath, cancellationToken, memoryMode, customPrepareWorkers, timings);
    }

    public static PreparedOverlayPayload PreparePayloadFromSource(
        string sourcePath,
        Dictionary<string, object?> meta,
        string gameDir,
        string? vanillaPathcPath)
    {
        var fullPathMapCache = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var pathcLast4Map = BuildPathcLast4Map(vanillaPathcPath);
        return PreparePayloadFromSource(sourcePath, meta, gameDir, fullPathMapCache, pathcLast4Map);
    }

    private static PreparedOverlayPayload PreparePayloadFromSource(
        string sourcePath,
        Dictionary<string, object?> meta,
        string gameDir,
        ConcurrentDictionary<string, Dictionary<string, string>> fullPathMapCache,
        Dictionary<uint, uint> pathcLast4Map)
    {
        string entryPath = S(meta, "entry_path").Replace('\\','/');
        string filename = entryPath.Contains('/') ? entryPath[(entryPath.LastIndexOf('/') + 1)..] : entryPath;
        long sourceLength = new FileInfo(sourcePath).Length;
        if (sourceLength > SafeUInt32PazFieldLimitBytes)
        {
            throw new InvalidDataException($"Single source payload exceeds the 4 GiB PAZ/PAMT UInt32 limit and was not packed: {entryPath} ({FormatBytes(sourceLength)})");
        }
        byte[] content = File.ReadAllBytes(sourcePath);
        int compType = I(meta, "compression_type", InferCompType(filename));
        if (entryPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) compType = 1;
        string pamtDir = S(meta, "pamt_dir");
        var fullPathMap = fullPathMapCache.GetOrAdd(pamtDir, key => BuildFullPathMap(key, gameDir));
        string dirPath = fullPathMap.TryGetValue(Norm(entryPath), out var fullFolder) ? fullFolder : "";
        if (string.IsNullOrEmpty(dirPath) && entryPath.Contains('/')) dirPath = entryPath[..entryPath.LastIndexOf('/')];
        if (!string.IsNullOrEmpty(dirPath) && !string.IsNullOrEmpty(filename) &&
            Norm(dirPath).EndsWith("/" + filename.ToLowerInvariant(), StringComparison.Ordinal))
        {
            dirPath = dirPath[..Math.Max(0, dirPath.LastIndexOf('/'))];
        }

        byte[] payload;
        uint compSize, decompSize;
        ushort flags;
        uint[]? mValues = null;
        uint last4 = 0;
        if (compType == 1)
        {
            var (partial, m) = DdsTools.BuildPartialPayload(content);
            byte[] buf;
            if (partial.Length == content.Length)
            {
                // Reuse the already-sized DDS/partial buffer when the prepared
                // output is the same length. This avoids one extra full-size LOH
                // allocation per texture without changing the packed bytes.
                buf = partial;
            }
            else
            {
                buf = new byte[content.Length];
                Buffer.BlockCopy(partial, 0, buf, 0, Math.Min(partial.Length, buf.Length));
            }
            last4 = GetPathcLast4(pathcLast4Map, entryPath);
            if (last4 == 0) last4 = DdsTools.GetFormatLast4(content);
            if (last4 != 0 && buf.Length >= 128) BinaryUtil.W32(buf, 124, last4);
            payload = buf; compSize = (uint)payload.Length; decompSize = (uint)payload.Length; flags = 1; mValues = m;
            if (!ReferenceEquals(content, payload)) content = Array.Empty<byte>();
            if (!ReferenceEquals(partial, payload)) partial = Array.Empty<byte>();
        }
        else if (compType == 2)
        {
            payload = Lz4Block.Compress(content); compSize = (uint)payload.Length; decompSize = (uint)content.Length; flags = 2;
        }
        else
        {
            payload = content; compSize = (uint)payload.Length; decompSize = (uint)payload.Length; flags = 0;
        }
        if (B(meta, "encrypted"))
        {
            string keyName = S(meta, "crypto_filename"); if (string.IsNullOrWhiteSpace(keyName)) keyName = filename;
            payload = ArchiveCrypto.EncryptDecrypt(payload, keyName);
            compSize = (uint)payload.Length;
            flags = (ushort)((flags & 0x0F) | 0x30);
        }
        var entry = new OverlayEntry
        {
            DirPath = dirPath,
            Filename = filename,
            PazOffset = 0,
            CompSize = compSize,
            DecompSize = decompSize,
            Flags = flags,
            DdsMValues = mValues,
            DdsLast4 = last4,
            EntryPath = entryPath
        };
        return new PreparedOverlayPayload(entry, payload);
    }

    public static (byte[] pamtBytes, List<OverlayEntry> entries, long pazSize) BuildOverlayFromPreparedPayloadsToFile(
        List<PreparedOverlayPayload> inputs,
        string pazPath,
        Action<int, int, string>? progress,
        CancellationToken cancellationToken = default,
        OverlayBuildTimings? timings = null)
    {
        var deferred = inputs.Select(DeferredOverlayInput.FromPrepared).ToList();
        return BuildOverlayFromDeferredInputsToFile(deferred, pazPath, "", progress, null, cancellationToken, "Full", 0, timings);
    }

    public static (List<OverlayEntry> entries, uint crc, uint length) BuildSinglePazPartFromDeferredInputsToFile(
        List<DeferredOverlayInput> inputs,
        string pazPath,
        int pazIndex,
        string gameDir,
        Action<int, int, string>? progress,
        string? vanillaPathcPath,
        CancellationToken cancellationToken = default,
        string memoryMode = "Auto",
        int customPrepareWorkers = 0,
        OverlayBuildTimings? timings = null)
    {
        if (inputs.Count == 0) throw new InvalidDataException("No inputs were provided for the PAZ part rebuild.");
        if (pazIndex < 0 || pazIndex > byte.MaxValue) throw new InvalidDataException($"PAZ part index {pazIndex} is outside the PAMT UInt8 part index range.");

        Directory.CreateDirectory(Path.GetDirectoryName(pazPath)!);
        string tmp = pazPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        long estimatedLength = 0;
        foreach (var input in inputs)
        {
            estimatedLength = checked(estimatedLength + AlignedLength(input.EstimatedPayloadLength));
        }
        if (estimatedLength > SafeUInt32PazFieldLimitBytes)
            throw new InvalidDataException($"PAZ part {pazIndex} would exceed the 4 GiB PAZ/PAMT UInt32 limit ({FormatBytes(estimatedLength)}).");

        var part = new PazPartPlan { Index = pazIndex, StartInputIndex = 0, Count = inputs.Count, Length = estimatedLength };
        var fullPathMapCache = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var pathcLast4Map = BuildPathcLast4Map(vanillaPathcPath);
        var zeroPad = new byte[PazAlignment];
        var entries = new List<OverlayEntry>(inputs.Count);
        int modeBannerShown = 0;

        void ShowModeBannerOnce(WorkerPlan workerPlan, PazPartPlan currentPart)
        {
            if (Interlocked.Exchange(ref modeBannerShown, 1) != 0) return;
            progress?.Invoke(0, Math.Max(1, inputs.Count),
                $"[stage] Performance Mode: {workerPlan.ModeLabel}; CPU threads: {Environment.ProcessorCount}; detected RAM: {FormatMemorySnapshot(workerPlan.Snapshot)}.");
        }

        PreparedPazPartResult? preparedResult = null;
        try
        {
            preparedResult = PreparePlannedPart(inputs, part, fullPathMapCache, pathcLast4Map, memoryMode, customPrepareWorkers, progress, cancellationToken, timings, ShowModeBannerOnce);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(0, Math.Max(1, inputs.Count),
                $"[stage] Writing PAZ part {pazIndex} ({FormatBytes(preparedResult.ActualLength)}, CRC during write)...");
            uint crc = WritePreparedPart(tmp, pazIndex, preparedResult.Prepared, preparedResult.ActualLength, entries, zeroPad, progress, cancellationToken, timings);
            ReleasePreparedPayloadBuffers(preparedResult.Prepared);
            preparedResult = null;

            if (File.Exists(pazPath)) File.Delete(pazPath);
            File.Move(tmp, pazPath);
            return (entries, crc, (uint)new FileInfo(pazPath).Length);
        }
        catch
        {
            try { if (preparedResult != null) ReleasePreparedPayloadBuffers(preparedResult.Prepared); } catch { }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            ReleaseCompletedBuildMemory(trimWorkingSet: true);
            throw;
        }
    }

    public static byte[] BuildPamtFromExistingEntries(List<OverlayEntry> entries, List<(uint crc, uint length)> pazHeaders)
        => BuildMultiPamt(entries, pazHeaders);

    public static (byte[] pamtBytes, List<OverlayEntry> entries, long pazSize) BuildOverlayFromDeferredInputsToFile(
        List<DeferredOverlayInput> inputs,
        string pazPath,
        string gameDir,
        Action<int, int, string>? progress,
        string? vanillaPathcPath,
        CancellationToken cancellationToken = default,
        string memoryMode = "Auto",
        int customPrepareWorkers = 0,
        OverlayBuildTimings? timings = null)
    {
        if (inputs.Count == 0) throw new InvalidDataException("No inputs were provided for the overlay PAZ/PAMT build.");

        Directory.CreateDirectory(Path.GetDirectoryName(pazPath)!);
        string pazDir = Path.GetDirectoryName(pazPath)!;
        string pazStemText = Path.GetFileNameWithoutExtension(pazPath);
        int basePazStem = int.TryParse(pazStemText, out var stem) ? stem : 0;

        // RC5 memory safety stays locked: never prepare an entire 40 GiB HD##
        // overlay in RAM.  UI10 may overlap additional next-part preparation with
        // current PAZ writing on high-RAM systems, but it still only buffers
        // bounded PAZ-sized parts, never a whole overlay folder.

        string PazFinalPath(int idx) => Path.Combine(pazDir, $"{basePazStem + idx}.paz");
        string PazTempPath(int idx) => PazFinalPath(idx) + ".tmp";

        var partPlans = PlanPazParts(inputs);
        var overlayEntries = new List<OverlayEntry>(inputs.Count);
        var tempFiles = new List<(int index, string tmp, string final)>();
        var pazHeaders = new List<(uint crc, uint length)>(partPlans.Count);
        var fullPathMapCache = new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var pathcLast4Map = BuildPathcLast4Map(vanillaPathcPath);
        byte[] zeroPad = new byte[PazAlignment];
        int modeBannerShown = 0;
        bool pipelineFallbackLogged = false;

        PipelinePlan pipelinePlan = SelectPazPipelineMode(memoryMode, customPrepareWorkers, partPlans);
        progress?.Invoke(0, Math.Max(1, inputs.Count), $"[stage] PAZ pipeline: {pipelinePlan.Label}{pipelinePlan.Reason}");

        var inFlightPrep = new Dictionary<int, Task<PreparedPazPartResult>>();
        int nextPartToStart = 0;

        try
        {
            void StartPart(int index)
            {
                inFlightPrep[index] = StartPreparePartTask(partPlans[index]);
                nextPartToStart = Math.Max(nextPartToStart, index + 1);
            }

            StartPart(0);

            for (int partCursor = 0; partCursor < partPlans.Count; partCursor++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentPrepTask = inFlightPrep[partCursor];
                inFlightPrep.Remove(partCursor);
                var current = currentPrepTask.GetAwaiter().GetResult();

                // Keep at most the selected number of PAZ-sized parts buffered/in-flight.
                // This allows double/triple/quad buffering on high-RAM systems without
                // ever returning to the old whole-HD## preload behavior.
                while (pipelinePlan.Depth > 1 &&
                       nextPartToStart < partPlans.Count &&
                       inFlightPrep.Count < pipelinePlan.Depth - 1)
                {
                    var nextPlan = partPlans[nextPartToStart];
                    string fallbackReason = "available RAM below threshold";
                    // Account for the current part being written plus any already
                    // queued future parts. This keeps the runtime fallback test in
                    // sync with the selected pipeline depth and prevents undercounting
                    // memory pressure while current.Prepared is still alive.
                    long queuedBytes = Math.Max(0, current.ActualLength);
                    foreach (var queued in inFlightPrep.Keys) queuedBytes += Math.Max(0, partPlans[queued].Length);
                    if (CanStartPipelinedPrepare(nextPlan, queuedBytes, current.WorkerPlan, out fallbackReason))
                    {
                        StartPart(nextPartToStart);
                    }
                    else
                    {
                        if (!pipelineFallbackLogged)
                        {
                            pipelineFallbackLogged = true;
                            progress?.Invoke(Math.Max(0, overlayEntries.Count), Math.Max(1, inputs.Count),
                                $"[stage] PAZ pipeline fallback: {fallbackReason}; continuing with a lower effective pipeline depth for this overlay.");
                        }
                        break;
                    }
                }

                string tmp = PazTempPath(current.Plan.Index);
                string final = PazFinalPath(current.Plan.Index);
                if (File.Exists(tmp)) File.Delete(tmp);
                tempFiles.Add((current.Plan.Index, tmp, final));

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(Math.Max(0, overlayEntries.Count), Math.Max(1, inputs.Count),
                    $"[stage] Writing PAZ part {current.Plan.Index} ({FormatBytes(current.ActualLength)}, CRC during write)...");

                var writeWatch = Stopwatch.StartNew();
                uint pazCrc = 0;
                try
                {
                    pazCrc = WritePreparedPart(tmp, current.Plan.Index, current.Prepared, current.ActualLength, overlayEntries, zeroPad, progress, cancellationToken, timings);
                }
                finally
                {
                    ReleasePreparedPayloadBuffers(current.Prepared);
                }
                writeWatch.Stop();
                timings?.AddPazWrite(writeWatch.Elapsed.TotalSeconds);
                pazHeaders.Add((pazCrc, (uint)current.ActualLength));
                if (current.Plan.Index == 0) DebugFailureInjector.Check(DebugFailureInjector.FreshAfterFirstPazPartWrite);

                progress?.Invoke(Math.Max(0, overlayEntries.Count), Math.Max(1, inputs.Count),
                    $"[stage] Released PAZ part {current.Plan.Index} payload buffers.");
                if (current.WorkerPlan.UseLowRamCleanup || current.ActualLength >= StockStylePazPartTargetBytes / 2)
                {
                    ForceReleaseLargeObjectHeap();
                }

                if (nextPartToStart < partPlans.Count && inFlightPrep.Count == 0)
                {
                    StartPart(nextPartToStart);
                }
            }

            long totalPazSize = pazHeaders.Sum(p => (long)p.length);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(inputs.Count, inputs.Count, $"[stage] Building PAMT ({overlayEntries.Count} entries, {partPlans.Count} PAZ part(s))...");
            var pamtWatch = Stopwatch.StartNew();
            byte[] pamt = BuildMultiPamt(overlayEntries, pazHeaders);
            pamtWatch.Stop();
            timings?.AddPamtBuild(pamtWatch.Elapsed.TotalSeconds);

            // Replace only after every temp PAZ and the PAMT have been built successfully.
            foreach (string existing in Directory.EnumerateFiles(pazDir, "*.paz"))
            {
                string stemName = Path.GetFileNameWithoutExtension(existing);
                if (int.TryParse(stemName, out var n) && n >= basePazStem) File.Delete(existing);
            }
            foreach (var tf in tempFiles)
            {
                if (File.Exists(tf.final)) File.Delete(tf.final);
                File.Move(tf.tmp, tf.final);
            }
            return (pamt, overlayEntries, totalPazSize);
        }
        catch
        {
            foreach (var task in inFlightPrep.Values.ToList())
            {
                try
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        ReleasePreparedPayloadBuffers(task.Result.Prepared);
                    }
                }
                catch { }
            }
            inFlightPrep.Clear();
            foreach (var tf in tempFiles)
            {
                try { if (File.Exists(tf.tmp)) File.Delete(tf.tmp); } catch { }
            }
            ReleaseCompletedBuildMemory(trimWorkingSet: true);
            throw;
        }

        void ShowModeBannerOnce(WorkerPlan workerPlan, PazPartPlan part)
        {
            if (Interlocked.Exchange(ref modeBannerShown, 1) != 0) return;
            progress?.Invoke(Math.Max(0, part.StartInputIndex), Math.Max(1, inputs.Count),
                $"[stage] Performance Mode: {workerPlan.ModeLabel}; CPU threads: {Environment.ProcessorCount}; detected RAM: {FormatMemorySnapshot(workerPlan.Snapshot)}.");
        }

        Task<PreparedPazPartResult> StartPreparePartTask(PazPartPlan part)
            => Task.Run(() => PreparePlannedPart(inputs, part, fullPathMapCache, pathcLast4Map, memoryMode, customPrepareWorkers, progress, cancellationToken, timings, ShowModeBannerOnce), cancellationToken);
    }

    private sealed class PreparedPazPartResult
    {
        public required PazPartPlan Plan { get; init; }
        public required WorkerPlan WorkerPlan { get; init; }
        public required List<PreparedOverlayPayload> Prepared { get; init; }
        public required long ActualLength { get; init; }
    }

    private static PreparedPazPartResult PreparePlannedPart(
        List<DeferredOverlayInput> inputs,
        PazPartPlan part,
        ConcurrentDictionary<string, Dictionary<string, string>> fullPathMapCache,
        Dictionary<uint, uint> pathcLast4Map,
        string memoryMode,
        int customPrepareWorkers,
        Action<int, int, string>? progress,
        CancellationToken cancellationToken,
        OverlayBuildTimings? timings,
        Action<WorkerPlan, PazPartPlan> showModeBannerOnce)
    {
        var workerPlan = SelectPrepareWorkerCount(part.Count, part.Length, memoryMode, customPrepareWorkers);
        showModeBannerOnce(workerPlan, part);

        progress?.Invoke(Math.Max(0, part.StartInputIndex), Math.Max(1, inputs.Count),
            $"[stage] Preparing PAZ part {part.Index} payloads ({part.Count} texture(s), target {FormatBytes(part.Length)}, {workerPlan.Workers} worker(s))...");

        var prepWatch = Stopwatch.StartNew();
        var prepared = PreparePartPayloads(inputs, part, fullPathMapCache, pathcLast4Map, workerPlan.Workers, progress, cancellationToken);
        prepWatch.Stop();
        timings?.AddPayloadPrep(prepWatch.Elapsed.TotalSeconds);

        long actualLength = 0;
        foreach (var p in prepared)
        {
            actualLength = checked(actualLength + AlignedLength(p.Payload.LongLength));
        }
        if (actualLength > SafeUInt32PazFieldLimitBytes)
            throw new InvalidDataException($"PAZ part {part.Index} exceeded the 4 GiB PAZ/PAMT UInt32 limit ({FormatBytes(actualLength)}).");
        if (prepared.Count > 1 && actualLength > StockStylePazPartTargetBytes + (16 * 1024 * 1024L))
            throw new InvalidDataException($"PAZ part {part.Index} grew past the normal ~900 MiB stock-style cap after preparation ({FormatBytes(actualLength)}). Re-run with a smaller texture set or report this source pack layout.");

        return new PreparedPazPartResult { Plan = part, WorkerPlan = workerPlan, Prepared = prepared, ActualLength = actualLength };
    }

    private static List<PreparedOverlayPayload> PreparePartPayloads(
        List<DeferredOverlayInput> inputs,
        PazPartPlan part,
        ConcurrentDictionary<string, Dictionary<string, string>> fullPathMapCache,
        Dictionary<uint, uint> pathcLast4Map,
        int workers,
        Action<int, int, string>? progress,
        CancellationToken cancellationToken)
    {
        var prepared = new PreparedOverlayPayload[part.Count];
        int completed = 0;
        int step = Math.Max(1, part.Count / 40);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = cancellationToken };
        Parallel.For(0, part.Count, parallelOptions, localIndex =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceIndex = part.StartInputIndex + localIndex;
            prepared[localIndex] = inputs[sourceIndex].Prepare(fullPathMapCache, pathcLast4Map);
            int done = Interlocked.Increment(ref completed);
            if (done == part.Count || done % step == 0)
            {
                string filename = inputs[sourceIndex].DisplayPath.Contains('/') ? inputs[sourceIndex].DisplayPath[(inputs[sourceIndex].DisplayPath.LastIndexOf('/') + 1)..] : inputs[sourceIndex].DisplayPath;
                progress?.Invoke(part.StartInputIndex + done, inputs.Count, $"[prepare] PAZ part {part.Index} {done}/{part.Count} {filename}");
            }
        });
        var result = prepared.ToList();
        Array.Clear(prepared, 0, prepared.Length);
        return result;
    }

    private static void ReleasePreparedPayloadBuffers(List<PreparedOverlayPayload>? prepared)
    {
        if (prepared == null) return;
        try
        {
            for (int i = 0; i < prepared.Count; i++)
            {
                if (prepared[i] != null)
                {
                    prepared[i].Payload = Array.Empty<byte>();
                }
            }
            prepared.Clear();
            prepared.TrimExcess();
        }
        catch
        {
            try { prepared.Clear(); } catch { }
        }
    }

    private static uint WritePreparedPart(
        string tmp,
        int partIndex,
        List<PreparedOverlayPayload> prepared,
        long actualLength,
        List<OverlayEntry> overlayEntries,
        byte[] zeroPad,
        Action<int, int, string>? progress,
        CancellationToken cancellationToken,
        OverlayBuildTimings? timings)
    {
        var hash = new HashLittle.StreamingComputer(actualLength, HashSeed);
        var createWatch = Stopwatch.StartNew();
        using var output = CreatePazWriteStream(tmp, actualLength);
        createWatch.Stop();
        timings?.AddPazCreatePreallocate(createWatch.Elapsed.TotalSeconds);

        for (int localIndex = 0; localIndex < prepared.Count; localIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = prepared[localIndex].Entry;
            string filename = string.IsNullOrWhiteSpace(src.Filename)
                ? (src.EntryPath.Contains('/') ? src.EntryPath[(src.EntryPath.LastIndexOf('/') + 1)..] : src.EntryPath)
                : src.Filename;

            byte[] payload = prepared[localIndex].Payload;
            long payloadWithPad = AlignedLength(payload.LongLength);
            if (payloadWithPad > SafeUInt32PazFieldLimitBytes)
            {
                throw new InvalidDataException($"Single prepared payload exceeds the 4 GiB PAZ/PAMT UInt32 limit and was not packed: {src.EntryPath} ({FormatBytes(payloadWithPad)})");
            }
            bool oversizedDedicatedPart = payloadWithPad > StockStylePazPartTargetBytes;
            if (oversizedDedicatedPart)
            {
                progress?.Invoke(overlayEntries.Count + 1, Math.Max(1, overlayEntries.Count + prepared.Count), $"[write] Oversized texture packed into dedicated PAZ part: {src.EntryPath} ({FormatBytes(payloadWithPad)})");
            }

            progress?.Invoke(overlayEntries.Count + 1, Math.Max(1, overlayEntries.Count + prepared.Count), $"[write] PAZ {partIndex} {localIndex + 1}/{prepared.Count} {filename}");
            long offset = output.Position;
            if (offset > uint.MaxValue) throw new InvalidDataException($"Overlay PAZ exceeded 4 GiB at entry {src.EntryPath}");

            var payloadWriteWatch = Stopwatch.StartNew();
            output.Write(payload, 0, payload.Length);
            payloadWriteWatch.Stop();
            timings?.AddPazPayloadWrite(payloadWriteWatch.Elapsed.TotalSeconds);

            var crcWatch = Stopwatch.StartNew();
            hash.Update(payload);
            crcWatch.Stop();
            timings?.AddPazCrcHash(crcWatch.Elapsed.TotalSeconds);

            int pad = PazAlignment - (int)(output.Position % PazAlignment);
            if (pad < PazAlignment)
            {
                var padWriteWatch = Stopwatch.StartNew();
                output.Write(zeroPad, 0, pad);
                padWriteWatch.Stop();
                timings?.AddPazPayloadWrite(padWriteWatch.Elapsed.TotalSeconds);

                var padCrcWatch = Stopwatch.StartNew();
                hash.Update(zeroPad.AsSpan(0, pad));
                padCrcWatch.Stop();
                timings?.AddPazCrcHash(padCrcWatch.Elapsed.TotalSeconds);
            }

            overlayEntries.Add(new OverlayEntry
            {
                DirPath = src.DirPath ?? "",
                Filename = filename,
                PazIndex = (uint)partIndex,
                PazOffset = (uint)offset,
                CompSize = (uint)payload.Length,
                DecompSize = src.DecompSize,
                Flags = src.Flags,
                DdsMValues = src.DdsMValues,
                DdsLast4 = src.DdsLast4,
                EntryPath = src.EntryPath
            });

            // Break large-buffer references immediately after the payload is safely
            // written and represented by the lightweight OverlayEntry.  This keeps
            // completed Task results, lists, or exception paths from retaining PAZ
            // payload bytes for the rest of the build/session.
            prepared[localIndex].Payload = Array.Empty<byte>();
        }

        if (output.Length != actualLength)
            throw new InvalidDataException($"PAZ part {partIndex} wrote {FormatBytes(output.Length)} but planned {FormatBytes(actualLength)}.");

        var finalizeWatch = Stopwatch.StartNew();
        output.Flush(flushToDisk: false);
        finalizeWatch.Stop();
        timings?.AddPazFinalize(finalizeWatch.Elapsed.TotalSeconds);

        var finishCrcWatch = Stopwatch.StartNew();
        uint crc = hash.Finish();
        finishCrcWatch.Stop();
        timings?.AddPazCrcHash(finishCrcWatch.Elapsed.TotalSeconds);
        return crc;
    }

    private static FileStream CreatePazWriteStream(string path, long preallocationSize)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4 * 1024 * 1024,
                Options = FileOptions.SequentialScan,
                PreallocationSize = Math.Max(0, preallocationSize)
            });
        }
        catch
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.SequentialScan);
        }
    }

    private sealed class PazPartPlan
    {
        public int Index { get; init; }
        public int StartInputIndex { get; init; }
        public int Count { get; init; }
        public long Length { get; init; }
    }

    private static List<PazPartPlan> PlanPazParts(List<DeferredOverlayInput> inputs)
    {
        if (inputs.Count == 0) throw new InvalidDataException("No inputs were provided for the overlay PAZ/PAMT build.");

        var parts = new List<PazPartPlan>();
        int nextIndex = 0;
        int currentStart = 0;
        int currentCount = 0;
        long currentLength = 0;

        void AddPart(int start, int count, long length)
        {
            if (count <= 0) return;
            if (nextIndex > byte.MaxValue)
                throw new InvalidDataException("Overlay requires more than 256 PAZ parts; lower the split size.");
            if (length > SafeUInt32PazFieldLimitBytes)
                throw new InvalidDataException($"PAZ part {nextIndex} exceeded the 4 GiB PAZ/PAMT UInt32 limit ({FormatBytes(length)}).");
            parts.Add(new PazPartPlan { Index = nextIndex, StartInputIndex = start, Count = count, Length = length });
            nextIndex++;
        }

        void FlushCurrent()
        {
            AddPart(currentStart, currentCount, currentLength);
            currentStart = 0;
            currentCount = 0;
            currentLength = 0;
        }

        for (int i = 0; i < inputs.Count; i++)
        {
            var src = inputs[i];
            long payloadWithPad = AlignedLength(src.EstimatedPayloadLength);
            if (payloadWithPad > SafeUInt32PazFieldLimitBytes)
            {
                throw new InvalidDataException($"Single prepared payload exceeds the 4 GiB PAZ/PAMT UInt32 limit and was not packed: {src.DisplayPath} ({FormatBytes(payloadWithPad)})");
            }

            bool oversizedDedicatedPart = payloadWithPad > StockStylePazPartTargetBytes;
            if (oversizedDedicatedPart)
            {
                FlushCurrent();
                AddPart(i, 1, payloadWithPad);
                continue;
            }

            if (currentCount == 0)
            {
                currentStart = i;
            }
            else if (currentLength + payloadWithPad > StockStylePazPartTargetBytes)
            {
                FlushCurrent();
                currentStart = i;
            }

            currentLength += payloadWithPad;
            currentCount++;
        }

        FlushCurrent();
        return parts;
    }

    private sealed record MemorySnapshot(long TotalBytes, long AvailableBytes);
    private sealed record WorkerPlan(int Workers, string ModeLabel, bool UseLowRamCleanup, MemorySnapshot Snapshot);
    private sealed record PipelinePlan(int Depth, string Label, string Reason);

    private static PipelinePlan SelectPazPipelineMode(string memoryMode, int customPrepareWorkers, List<PazPartPlan> parts)
    {
        string mode = NormalizeMemoryMode(memoryMode);
        if (parts.Count <= 1)
            return new PipelinePlan(1, "single-buffer", " (single part)");

        var snapshot = GetMemorySnapshot();
        int installedMaxDepth = MaxInstalledPipelineDepth(mode, snapshot);
        if (installedMaxDepth <= 1)
            return new PipelinePlan(1, "single-buffer", InstalledPipelineReason(mode, snapshot));

        long largest = Math.Max(1, parts.Max(p => p.Length));
        int allowedDepth = ClampPipelineDepthByAvailableMemory(installedMaxDepth, largest, snapshot, out string reason);
        allowedDepth = Math.Clamp(allowedDepth, 1, 4);
        return new PipelinePlan(allowedDepth, PipelineDepthLabel(allowedDepth), reason);
    }

    private static int MaxInstalledPipelineDepth(string mode, MemorySnapshot snapshot)
    {
        long total = snapshot.TotalBytes;

        if (mode == "low" || mode == "medium" || mode == "custom") return 1;

        if (mode == "auto")
        {
            // Auto stays conservative: allow double-buffer only on 32GB-class
            // systems and never escalate to triple/quad.  "Class" thresholds
            // intentionally account for Windows reporting 32/64/128GB systems
            // slightly below their marketed installed RAM.
            if (total > 0 && total < 30L * OneGiB) return 1;
            return 2;
        }

        if (mode == "full")
        {
            // UI10 retune: double-buffer is the proven fast path on normal
            // 64GB-class consumer systems.  Deeper buffering remains available
            // only for larger memory/workstation-class machines because tests
            // showed triple/quad can add overhead/contention instead of speed.
            if (total <= 0) return 2;
            if (total < 30L * OneGiB) return 1;
            if (total < 96L * OneGiB) return 2;
            if (total < 120L * OneGiB) return 3;
            return 4;
        }

        return 1;
    }

    private static string InstalledPipelineReason(string mode, MemorySnapshot snapshot)
    {
        if (mode == "low" || mode == "medium") return "";
        if (snapshot.TotalBytes > 0 && snapshot.TotalBytes < 30L * OneGiB)
            return " (installed RAM below multi-buffer threshold)";
        return "";
    }

    private static int ClampPipelineDepthByAvailableMemory(int maxDepth, long largestPartLength, MemorySnapshot snapshot, out string reason)
    {
        reason = "";
        if (maxDepth <= 1) return 1;
        if (snapshot.AvailableBytes <= 0) return Math.Clamp(maxDepth, 1, 2);

        for (int depth = Math.Clamp(maxDepth, 1, 4); depth >= 2; depth--)
        {
            long required = RequiredAvailableMemoryForPipelineDepth(depth, largestPartLength, snapshot);
            if (snapshot.AvailableBytes >= required)
            {
                if (depth < maxDepth) reason = $" (available RAM limited depth from {PipelineDepthLabel(maxDepth)} to {PipelineDepthLabel(depth)})";
                return depth;
            }
        }

        long doubleRequired = RequiredAvailableMemoryForPipelineDepth(2, largestPartLength, snapshot);
        reason = $" (available RAM below double-buffer threshold: {FormatBytes(snapshot.AvailableBytes)} available, {FormatBytes(doubleRequired)} wanted)";
        return 1;
    }

    private static bool CanStartPipelinedPrepare(PazPartPlan candidate, long alreadyQueuedBytes, WorkerPlan currentWorkerPlan, out string fallbackReason)
    {
        fallbackReason = "available RAM below threshold";
        var snapshot = GetMemorySnapshot();
        if (snapshot.AvailableBytes <= 0) return true;

        long reserve = SystemSafetyReserveBytes(snapshot);
        long workerHeadroom = Math.Max(OneGiB, currentWorkerPlan.Workers * 256L * OneMiB);
        long queuedFutureBytes = Math.Max(0, alreadyQueuedBytes) + Math.Max(0, candidate.Length);
        long required = reserve + queuedFutureBytes + workerHeadroom;
        if (snapshot.AvailableBytes >= required)
        {
            fallbackReason = "";
            return true;
        }

        fallbackReason = $"available RAM below threshold ({FormatBytes(snapshot.AvailableBytes)} available, {FormatBytes(required)} wanted)";
        return false;
    }

    private static string PipelineDepthLabel(int depth)
        => depth switch
        {
            4 => "quad-buffer",
            3 => "triple-buffer",
            2 => "double-buffer",
            _ => "single-buffer"
        };

    private static long RequiredAvailableMemoryForPipelineDepth(int depth, long largestPartLength, MemorySnapshot snapshot)
    {
        long reserve = SystemSafetyReserveBytes(snapshot);
        long perPart = Math.Max(StockStylePazPartTargetBytes, largestPartLength);
        long pipelinePayloadBytes = checked(perPart * Math.Max(1, depth));
        long pipelineOverhead = Math.Max(2L * OneGiB, depth * OneGiB);
        return reserve + pipelinePayloadBytes + pipelineOverhead;
    }

    private static long SystemSafetyReserveBytes(MemorySnapshot snapshot)
    {
        long total = snapshot.TotalBytes;
        if (total <= 0) return 12L * OneGiB;
        if (total < 30L * OneGiB) return 8L * OneGiB;
        if (total < 60L * OneGiB) return 12L * OneGiB;
        if (total < 120L * OneGiB) return 16L * OneGiB;
        return 24L * OneGiB;
    }


    public static string CurrentMemorySnapshotText()
        => FormatMemorySnapshot(GetMemorySnapshot());

    public static string CurrentProcessMemorySnapshotText()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            string workingSet = FormatBytes(process.WorkingSet64);
            string privateBytes = FormatBytes(process.PrivateMemorySize64);
            string managedHeap = FormatBytes(GC.GetTotalMemory(forceFullCollection: false));
            string gcHeap = "unknown";
            try
            {
                var info = GC.GetGCMemoryInfo();
                if (info.HeapSizeBytes > 0) gcHeap = FormatBytes(info.HeapSizeBytes);
            }
            catch { }
            return $"working set {workingSet}; private bytes {privateBytes}; managed heap {managedHeap}; GC heap {gcHeap}";
        }
        catch
        {
            try { return $"managed heap {FormatBytes(GC.GetTotalMemory(forceFullCollection: false))}"; }
            catch { return "unknown"; }
        }
    }

    public static void ReleaseCompletedBuildMemory(bool trimWorkingSet = false)
    {
        ForceReleaseLargeObjectHeap();
        if (trimWorkingSet) TryTrimProcessWorkingSet();
    }

    private static WorkerPlan SelectPrepareWorkerCount(int inputCount, long partLength, string memoryMode, int customPrepareWorkers)
    {
        var snapshot = GetMemorySnapshot();
        int cpu = Math.Max(1, Environment.ProcessorCount);
        string mode = NormalizeMemoryMode(memoryMode);
        bool lowCleanup = false;
        int workers;
        string label;

        if (inputCount < 8)
        {
            workers = 1;
            label = ModeLabel(mode, "small part");
        }
        else if (mode == "low")
        {
            workers = Math.Min(cpu, 2);
            label = "Low";
            lowCleanup = true;
        }
        else if (mode == "custom")
        {
            workers = customPrepareWorkers > 0 ? customPrepareWorkers : 1;
            label = $"Custom ({workers} requested)";
        }
        else if (mode == "medium")
        {
            workers = MediumWorkerCount(cpu);
            label = "Medium";
        }
        else if (mode == "full")
        {
            workers = MaxPerformanceWorkerCount(cpu, snapshot);
            label = "Max Performance";
        }
        else
        {
            workers = AutoWorkerCount(cpu, snapshot, out var autoLabel, out lowCleanup);
            label = "Auto / Recommended -> " + autoLabel;
        }

        workers = Math.Min(workers, inputCount);
        int ramCap = RamWorkerCap(snapshot);
        if (workers > ramCap)
        {
            workers = ramCap;
            label += $" (RAM-capped to {workers})";
        }

        workers = Math.Max(1, workers);
        workers = ThrottleWorkersForAvailableMemory(workers, partLength, snapshot);
        EnsureAvailableMemoryForPart(partLength, workers, ref snapshot);
        return new WorkerPlan(workers, label, lowCleanup || (snapshot.TotalBytes > 0 && snapshot.TotalBytes < 60L * OneGiB) || workers <= 2, snapshot);
    }

    private static int MediumWorkerCount(int cpu)
        => Math.Clamp((int)Math.Ceiling(cpu * 0.25), 2, 6);

    private static int MaxPerformanceWorkerCount(int cpu, MemorySnapshot snapshot)
    {
        double totalGb = snapshot.TotalBytes > 0 ? snapshot.TotalBytes / (double)OneGiB : 0;

        // Consumer hybrid CPUs such as 8P/16E Intel desktop parts report many
        // logical threads that are not equal. Prefer P-core-ish capacity first
        // instead of blindly feeding every logical thread. True workstation/high
        // core machines can still scale higher, and RAM caps still win below.
        if (cpu >= 48 && (totalGb == 0 || totalGb >= 120))
            return Math.Clamp((int)Math.Ceiling(cpu * 0.45), 12, 24);

        if (cpu >= 28)
            return 12;

        return Math.Clamp((int)Math.Ceiling(cpu * 0.50), 4, 16);
    }

    private static int AutoWorkerCount(int cpu, MemorySnapshot snapshot, out string label, out bool lowCleanup)
    {
        double totalGb = snapshot.TotalBytes > 0 ? snapshot.TotalBytes / (double)OneGiB : 0;
        double availGb = snapshot.AvailableBytes > 0 ? snapshot.AvailableBytes / (double)OneGiB : 0;
        lowCleanup = false;

        if ((totalGb > 0 && totalGb <= 20) || (availGb > 0 && availGb < 8))
        {
            label = "Low";
            lowCleanup = true;
            return Math.Min(cpu, 2);
        }
        if ((totalGb > 0 && totalGb <= 40) || (availGb > 0 && availGb < 16))
        {
            label = "Medium / RAM-safe";
            lowCleanup = true;
            return Math.Clamp((int)Math.Ceiling(cpu * 0.30), 2, 4);
        }
        if ((totalGb > 0 && totalGb >= 60) && (availGb == 0 || availGb >= 24))
        {
            label = "Medium / high-RAM";
            return Math.Clamp((int)Math.Ceiling(cpu * 0.25), 6, 8);
        }

        label = "Medium";
        return Math.Clamp((int)Math.Ceiling(cpu * 0.25), 2, 8);
    }

    private static int RamWorkerCap(MemorySnapshot snapshot)
    {
        int cap = 24;
        if (snapshot.TotalBytes > 0)
        {
            if (snapshot.TotalBytes < 30L * OneGiB) cap = Math.Min(cap, 2);
            else if (snapshot.TotalBytes < 60L * OneGiB) cap = Math.Min(cap, 8);
            else if (snapshot.TotalBytes < 120L * OneGiB) cap = Math.Min(cap, 16);
            // 128GB-class systems can allow workstation-style worker counts,
            // but current available RAM still wins.
        }
        if (snapshot.AvailableBytes > 0)
        {
            if (snapshot.AvailableBytes < 8L * OneGiB) cap = Math.Min(cap, 2);
            else if (snapshot.AvailableBytes < 16L * OneGiB) cap = Math.Min(cap, 4);
            else if (snapshot.AvailableBytes < 24L * OneGiB) cap = Math.Min(cap, 6);
            else if (snapshot.AvailableBytes < 32L * OneGiB) cap = Math.Min(cap, 12);
            else if (snapshot.AvailableBytes < 48L * OneGiB) cap = Math.Min(cap, 16);
            else if (snapshot.TotalBytes > 0 && snapshot.TotalBytes < 120L * OneGiB) cap = Math.Min(cap, 16);
        }
        return Math.Max(1, cap);
    }

    private static int ThrottleWorkersForAvailableMemory(int workers, long partLength, MemorySnapshot snapshot)
    {
        if (snapshot.AvailableBytes <= 0) return workers;
        while (workers > 1 && snapshot.AvailableBytes < RequiredAvailableMemory(partLength, workers)) workers--;
        return Math.Max(1, workers);
    }

    private static void EnsureAvailableMemoryForPart(long partLength, int workers, ref MemorySnapshot snapshot)
    {
        long required = RequiredAvailableMemory(partLength, workers);
        if (snapshot.AvailableBytes <= 0 || snapshot.AvailableBytes >= required) return;

        ForceReleaseLargeObjectHeap();
        snapshot = GetMemorySnapshot();
        if (snapshot.AvailableBytes <= 0 || snapshot.AvailableBytes >= required) return;

        throw new InvalidOperationException($"Not enough available RAM to safely prepare this PAZ part. Close other apps or select Low mode. Required: {FormatBytes(required)} available; detected: {FormatBytes(snapshot.AvailableBytes)} available.");
    }

    private static long RequiredAvailableMemory(long partLength, int workers)
    {
        // Each DDS worker briefly holds source bytes + partial/header bytes + prepared output,
        // while the completed part accumulates up to about one PAZ part of payloads.
        // Keep a conservative floor so 16GB/32GB systems slow down instead of paging hard.
        long workerOverhead = Math.Max(384L * OneMiB, Math.Min(OneGiB, partLength / Math.Max(1, workers)));
        return Math.Max(2L * OneGiB, partLength + (workers * workerOverhead) + OneGiB);
    }

    private static string NormalizeMemoryMode(string? memoryMode)
    {
        string v = (memoryMode ?? "").Trim().ToLowerInvariant();
        if (v.Contains("full") || v.Contains("max")) return "full";
        if (v.Contains("medium") || v.Contains("balanced")) return "medium";
        if (v.Contains("low") || v.Contains("safe")) return "low";
        if (v.Contains("custom")) return "custom";
        return "auto";
    }

    private static string ModeLabel(string mode, string detail)
        => mode switch
        {
            "full" => "Max Performance" + (string.IsNullOrWhiteSpace(detail) ? "" : " -> " + detail),
            "medium" => "Medium" + (string.IsNullOrWhiteSpace(detail) ? "" : " -> " + detail),
            "low" => "Low" + (string.IsNullOrWhiteSpace(detail) ? "" : " -> " + detail),
            "custom" => "Custom" + (string.IsNullOrWhiteSpace(detail) ? "" : " -> " + detail),
            _ => "Auto / Recommended" + (string.IsNullOrWhiteSpace(detail) ? "" : " -> " + detail)
        };

    private static string FormatMemorySnapshot(MemorySnapshot snapshot)
    {
        if (snapshot.TotalBytes <= 0 && snapshot.AvailableBytes <= 0) return "unknown";
        string total = snapshot.TotalBytes > 0 ? FormatBytes(snapshot.TotalBytes) : "unknown total";
        string available = snapshot.AvailableBytes > 0 ? FormatBytes(snapshot.AvailableBytes) : "unknown available";
        return $"{total} total / {available} available";
    }

    private static MemorySnapshot GetMemorySnapshot()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MEMORYSTATUSEX();
                status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                if (GlobalMemoryStatusEx(ref status))
                    return new MemorySnapshot((long)Math.Min((ulong)long.MaxValue, status.ullTotalPhys), (long)Math.Min((ulong)long.MaxValue, status.ullAvailPhys));
            }
            catch { }
        }

        try
        {
            var info = GC.GetGCMemoryInfo();
            return new MemorySnapshot(info.TotalAvailableMemoryBytes, 0);
        }
        catch { return new MemorySnapshot(0, 0); }
    }

    private static void ForceReleaseLargeObjectHeap()
    {
        try
        {
            for (int i = 0; i < 2; i++)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
        }
        catch { }
    }

    private static void TryTrimProcessWorkingSet()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private static long AlignedLength(long length)
    {
        long rem = length % PazAlignment;
        return rem == 0 ? length : checked(length + PazAlignment - rem);
    }

    private static string FormatBytes(long bytes)
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

    private static byte[] BuildMultiPamt(List<OverlayEntry> entries, List<(uint crc, uint length)> pazHeaders)
    {
        var uniqueDirs = entries.Select(e => e.DirPath ?? "").Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        using var folderBytes = new MemoryStream();
        var folderOffsets = new Dictionary<string, uint>();
        foreach (var dir in uniqueDirs)
        {
            var parts = string.IsNullOrEmpty(dir) ? new[] { "" } : dir.Split('/');
            for (int depth = 0; depth < parts.Length; depth++)
            {
                string key = string.Join('/', parts.Take(depth + 1));
                if (folderOffsets.ContainsKey(key)) continue;
                folderOffsets[key] = (uint)folderBytes.Position;
                uint parent = depth == 0 ? 0xFFFFFFFF : folderOffsets[string.Join('/', parts.Take(depth))];
                string name = depth == 0 ? parts[0] : "/" + parts[depth];
                var nb = Encoding.UTF8.GetBytes(name);
                BinaryUtil.WriteU32(folderBytes, parent); folderBytes.WriteByte((byte)nb.Length); folderBytes.Write(nb);
            }
        }
        using var nodeBytes = new MemoryStream();
        var nodeOffsets = new Dictionary<int, uint>();
        var dirEntries = new Dictionary<string, List<(int idx, OverlayEntry e)>>();
        for (int i = 0; i < entries.Count; i++)
        {
            string d = entries[i].DirPath ?? "";
            if (!dirEntries.ContainsKey(d)) dirEntries[d] = new();
            dirEntries[d].Add((i, entries[i]));
        }
        foreach (var d in dirEntries.Keys.ToList()) dirEntries[d] = dirEntries[d].OrderBy(x => x.e.Filename, StringComparer.Ordinal).ToList();
        foreach (var d in uniqueDirs)
        {
            if (!dirEntries.TryGetValue(d, out var list)) continue;
            foreach (var (idx, e) in list)
            {
                nodeOffsets[idx] = (uint)nodeBytes.Position;
                var nb = Encoding.UTF8.GetBytes(e.Filename);
                BinaryUtil.WriteU32(nodeBytes, 0xFFFFFFFF); nodeBytes.WriteByte((byte)nb.Length); nodeBytes.Write(nb);
            }
        }
        using var folderRecords = new MemoryStream();
        uint fileIndex = 0;
        foreach (var d in uniqueDirs)
        {
            uint count = (uint)(dirEntries.TryGetValue(d, out var list) ? list.Count : 0);
            uint pathHash = HashLittle.Compute(Encoding.UTF8.GetBytes(d), HashSeed);
            uint folderRef = folderOffsets.TryGetValue(d, out var fo) ? fo : 0;
            BinaryUtil.WriteU32(folderRecords, pathHash); BinaryUtil.WriteU32(folderRecords, folderRef); BinaryUtil.WriteU32(folderRecords, fileIndex); BinaryUtil.WriteU32(folderRecords, count);
            fileIndex += count;
        }
        using var fileRecords = new MemoryStream();
        foreach (var d in uniqueDirs)
        {
            if (!dirEntries.TryGetValue(d, out var list)) continue;
            foreach (var (idx, e) in list)
            {
                BinaryUtil.WriteU32(fileRecords, nodeOffsets[idx]); BinaryUtil.WriteU32(fileRecords, e.PazOffset); BinaryUtil.WriteU32(fileRecords, e.CompSize); BinaryUtil.WriteU32(fileRecords, e.DecompSize); BinaryUtil.WriteU16(fileRecords, unchecked((ushort)(e.PazIndex & 0xFF))); BinaryUtil.WriteU16(fileRecords, e.Flags);
            }
        }
        if (pazHeaders.Count == 0) throw new InvalidDataException("No PAZ files were generated for the overlay.");
        using var body = new MemoryStream();
        BinaryUtil.WriteU32(body, (uint)pazHeaders.Count);
        BinaryUtil.WriteU32(body, PamtConstant);
        BinaryUtil.WriteU32(body, 0);
        for (int i = 0; i < pazHeaders.Count; i++)
        {
            if (i > 0) BinaryUtil.WriteU32(body, (uint)i);
            BinaryUtil.WriteU32(body, pazHeaders[i].crc);
            BinaryUtil.WriteU32(body, pazHeaders[i].length);
        }
        BinaryUtil.WriteU32(body, (uint)folderBytes.Length); body.Write(folderBytes.ToArray());
        BinaryUtil.WriteU32(body, (uint)nodeBytes.Length); body.Write(nodeBytes.ToArray());
        BinaryUtil.WriteU32(body, (uint)uniqueDirs.Count); body.Write(folderRecords.ToArray());
        BinaryUtil.WriteU32(body, fileIndex); body.Write(fileRecords.ToArray());
        using var pamt = new MemoryStream();
        BinaryUtil.WriteU32(pamt, 0); pamt.Write(body.ToArray());
        byte[] pamtBytes = pamt.ToArray();
        uint outer = HashLittle.Compute(pamtBytes.AsSpan(12), HashSeed);
        BinaryUtil.W32(pamtBytes, 0, outer);
        return pamtBytes;
    }

    public static Dictionary<string, string> BuildFullPathMap(string pamtDir, string gameDir)
    {
        var p = Path.Combine(gameDir, pamtDir, "0.pamt");
        // IMPORTANT: overlay PAMT building needs the containing folder only,
        // not the full virtual path including the filename. The indexer uses
        // PamtFullPathMap.Parse(...) because it needs full paths for matching
        // and PATHC targeting. If we use that full-path map here, generated
        // overlay PAMTs become object/texture/foo.dds/foo.dds, which the game
        // accepts structurally but never resolves at runtime.
        return File.Exists(p) ? PamtFullPathMap.ParseFolderMap(p) : new Dictionary<string, string>();
    }

    private static Dictionary<uint, uint> BuildPathcLast4Map(string? pathcPath)
    {
        var map = new Dictionary<uint, uint>();
        if (string.IsNullOrEmpty(pathcPath) || !File.Exists(pathcPath)) return map;
        try
        {
            var p = PathcFile.Read(pathcPath);
            for (int i = 0; i < p.KeyHashes.Count && i < p.MapEntries.Count; i++)
            {
                int dds = (int)(p.MapEntries[i].Selector & 0xFFFF);
                if (dds < 0 || dds >= p.DdsRecords.Count || p.DdsRecords[dds].Length < 128) continue;
                map[p.KeyHashes[i]] = BinaryUtil.U32(p.DdsRecords[dds], 124);
            }
        }
        catch { }
        return map;
    }

    private static uint GetPathcLast4(Dictionary<uint, uint> pathcLast4Map, string virtualPath)
    {
        if (pathcLast4Map.Count == 0) return 0;
        uint h = PathcFile.PathHash("/" + virtualPath.Trim('/'));
        return pathcLast4Map.TryGetValue(h, out var last4) ? last4 : 0;
    }

    public static string Norm(string s) => s.Replace('\\','/').Trim().Trim('/').ToLowerInvariant();
    private static string S(Dictionary<string, object?> m, string key) => m.TryGetValue(key, out var v) ? Convert.ToString(v) ?? "" : "";
    private static int I(Dictionary<string, object?> m, string key, int def) => m.TryGetValue(key, out var v) && int.TryParse(Convert.ToString(v), out var i) ? i : def;
    private static bool B(Dictionary<string, object?> m, string key) => m.TryGetValue(key, out var v) && v is bool b ? b : bool.TryParse(Convert.ToString(v), out var bb) && bb;
    private static int InferCompType(string filename)
    {
        string ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch { ".dds" => 1, ".bnk" => 0, _ => 2 };
    }
}

public static class PamtFullPathMap
{
    public static Dictionary<string, string> ParseFolderMap(string pamtPath)
    {
        var result = new Dictionary<string, string>();
        byte[] data;
        try { data = File.ReadAllBytes(pamtPath); } catch { return result; }
        if (data.Length < 32) return result;
        try
        {
            int off = 16;
            uint pazCount = BinaryUtil.U32(data, 4);
            for (int i = 0; i < pazCount; i++) { off += 8; if (i < pazCount - 1) off += 4; }

            uint folderLen = BinaryUtil.U32(data, off); off += 4; int folderStart = off;
            var folders = new Dictionary<uint, (uint parent, string name)>();
            while (off < folderStart + folderLen)
            {
                uint rel = (uint)(off - folderStart);
                uint parent = BinaryUtil.U32(data, off);
                int slen = data[off + 4];
                string name = Encoding.UTF8.GetString(data, off + 5, slen);
                folders[rel] = (parent, name);
                off += 5 + slen;
            }
            string FolderPath(uint rf)
            {
                var parts = new List<string>();
                uint cur = rf; int g = 0;
                while (cur != 0xFFFFFFFF && g++ < 64 && folders.TryGetValue(cur, out var f))
                {
                    parts.Add(f.name); cur = f.parent;
                }
                parts.Reverse();
                return string.Concat(parts).Trim('/');
            }
            string root = folders.Values.FirstOrDefault(x => x.parent == 0xFFFFFFFF).name?.Trim('/') ?? "";

            uint nodeLen = BinaryUtil.U32(data, off); off += 4; int nodeStart = off;
            var nodes = new Dictionary<uint, (uint parent, string name)>();
            while (off < nodeStart + nodeLen)
            {
                uint rel = (uint)(off - nodeStart);
                uint parent = BinaryUtil.U32(data, off);
                int slen = data[off + 4];
                string name = Encoding.UTF8.GetString(data, off + 5, slen);
                nodes[rel] = (parent, name);
                off += 5 + slen;
            }
            string NodePath(uint rf)
            {
                var parts = new List<string>();
                uint cur = rf; int g = 0;
                while (cur != 0xFFFFFFFF && g++ < 64 && nodes.TryGetValue(cur, out var f))
                {
                    parts.Add(f.name); cur = f.parent;
                }
                parts.Reverse();
                return string.Concat(parts).Trim('/');
            }

            uint folderCount = BinaryUtil.U32(data, off); off += 4;
            var folderRecs = new List<(string path, uint index, uint count)>();
            for (int i = 0; i < folderCount; i++)
            {
                off += 4;
                uint fr = BinaryUtil.U32(data, off);
                uint fi = BinaryUtil.U32(data, off + 4);
                uint fc = BinaryUtil.U32(data, off + 8);
                folderRecs.Add((FolderPath(fr), fi, fc));
                off += 12;
            }
            var fileToFolder = new Dictionary<uint, string>();
            foreach (var fr in folderRecs)
                for (uint i = fr.index; i < fr.index + fr.count; i++) fileToFolder[i] = fr.path;

            uint fileCount = BinaryUtil.U32(data, off); off += 4;
            for (uint i = 0; i < fileCount; i++)
            {
                uint nr = BinaryUtil.U32(data, off); off += 20;
                string filename = NodePath(nr);
                string flat = string.IsNullOrEmpty(root) ? filename : $"{root}/{filename}";
                string folder = fileToFolder.TryGetValue(i, out var fp) ? fp : root;
                result[OverlayBuilder.Norm(flat)] = folder.Trim('/');
            }
        }
        catch { }
        return result;
    }

    public static Dictionary<string, string> Parse(string pamtPath)
    {
        var result = new Dictionary<string, string>();
        byte[] data;
        try { data = File.ReadAllBytes(pamtPath); } catch { return result; }
        if (data.Length < 32) return result;
        try
        {
            int off = 16;
            uint pazCount = BinaryUtil.U32(data, 4);
            for (int i = 0; i < pazCount; i++) { off += 8; if (i < pazCount - 1) off += 4; }
            uint folderLen = BinaryUtil.U32(data, off); off += 4; int folderStart = off;
            var folders = new Dictionary<uint, (uint parent, string name)>();
            while (off < folderStart + folderLen)
            {
                uint rel = (uint)(off - folderStart); uint parent = BinaryUtil.U32(data, off); int slen = data[off + 4]; string name = Encoding.UTF8.GetString(data, off + 5, slen); folders[rel] = (parent, name); off += 5 + slen;
            }
            string FolderPath(uint rf) { var parts = new List<string>(); uint cur = rf; int g = 0; while (cur != 0xFFFFFFFF && g++ < 64 && folders.TryGetValue(cur, out var f)) { parts.Add(f.name); cur = f.parent; } parts.Reverse(); return string.Concat(parts).Trim('/'); }
            string root = folders.Values.FirstOrDefault(x => x.parent == 0xFFFFFFFF).name?.Trim('/') ?? "";
            uint nodeLen = BinaryUtil.U32(data, off); off += 4; int nodeStart = off;
            var nodes = new Dictionary<uint, (uint parent, string name)>();
            while (off < nodeStart + nodeLen)
            {
                uint rel = (uint)(off - nodeStart); uint parent = BinaryUtil.U32(data, off); int slen = data[off + 4]; string name = Encoding.UTF8.GetString(data, off + 5, slen); nodes[rel] = (parent, name); off += 5 + slen;
            }
            string NodePath(uint rf) { var parts = new List<string>(); uint cur = rf; int g = 0; while (cur != 0xFFFFFFFF && g++ < 64 && nodes.TryGetValue(cur, out var f)) { parts.Add(f.name); cur = f.parent; } parts.Reverse(); return string.Concat(parts).Trim('/'); }
            uint folderCount = BinaryUtil.U32(data, off); off += 4;
            var folderRecs = new List<(string path, uint index, uint count)>();
            for (int i = 0; i < folderCount; i++) { off += 4; uint fr = BinaryUtil.U32(data, off); uint fi = BinaryUtil.U32(data, off + 4); uint fc = BinaryUtil.U32(data, off + 8); folderRecs.Add((FolderPath(fr), fi, fc)); off += 12; }
            var fileToFolder = new Dictionary<uint, string>();
            foreach (var fr in folderRecs) for (uint i = fr.index; i < fr.index + fr.count; i++) fileToFolder[i] = fr.path;
            uint fileCount = BinaryUtil.U32(data, off); off += 4;
            for (uint i = 0; i < fileCount; i++)
            {
                uint nr = BinaryUtil.U32(data, off); off += 20;
                string filename = NodePath(nr);
                string flat = string.IsNullOrEmpty(root) ? filename : $"{root}/{filename}";
                string folder = fileToFolder.TryGetValue(i, out var fp) ? fp : root;
                string full = string.IsNullOrEmpty(folder) ? filename : $"{folder.Trim('/')}/{filename}";
                result[OverlayBuilder.Norm(flat)] = full.Trim('/');
            }
        }
        catch { }
        return result;
    }
}
