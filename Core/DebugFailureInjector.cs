using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CDTextureOverlayBuilder.Core;

public sealed class SimulatedFailureException : Exception
{
    public string OperationName { get; }
    public string FailurePoint { get; }

    public SimulatedFailureException(string operationName, string failurePoint, string message) : base(message)
    {
        OperationName = operationName;
        FailurePoint = failurePoint;
    }
}

public static class DebugFailureInjector
{
    public const string None = "none";
    public const string FreshAfterIncompleteOverlayCreation = "fresh_after_incomplete_overlay_creation";
    public const string FreshAfterFirstPazPartWrite = "fresh_after_first_paz_part_write";
    public const string EasyExistingAfterOverlaySafetyBackup = "easy_existing_after_overlay_safety_backup";
    public const string AfterMetaBackup = "after_meta_backup";
    public const string AfterPathcBeforePapgt = "after_pathc_before_papgt";
    public const string BeforeActiveRegistryWrite = "before_active_registry_write";
    public const string BeforeSourceManifestSave = "before_source_manifest_save";
    public const string RelinkAfterMetaBackup = "relink_after_meta_backup";
    public const string RelinkBeforeRegistryRefresh = "relink_before_registry_refresh";
    public const string HotfixAfterAffectedPazPartBackup = "hotfix_after_affected_paz_part_backup";
    public const string HotfixAfterFirstAffectedPazPartReplacement = "hotfix_after_first_affected_paz_part_replacement";
    public const string HotfixAfterPamtBeforePathc = "hotfix_after_pamt_before_pathc";

    private static readonly (string Code, string Label)[] s_points =
    {
        (None, "No simulated failure"),
        (FreshAfterIncompleteOverlayCreation, "Fresh Easy Apply / after incomplete HD## folder creation"),
        (FreshAfterFirstPazPartWrite, "Fresh Easy Apply / after first PAZ part write"),
        (EasyExistingAfterOverlaySafetyBackup, "Easy Apply over existing build / after overlay safety backup"),
        (AfterMetaBackup, "Build/apply / after meta backup"),
        (AfterPathcBeforePapgt, "Build/apply / after PATHC update before PAPGT rebuild"),
        (BeforeActiveRegistryWrite, "Build/apply / before active registry write"),
        (BeforeSourceManifestSave, "Build/apply / before source manifest save"),
        (RelinkAfterMetaBackup, "Relink / after meta backup"),
        (RelinkBeforeRegistryRefresh, "Relink / before registry/state refresh"),
        (HotfixAfterAffectedPazPartBackup, "Update Existing Build hotfix / after affected PAZ part backup"),
        (HotfixAfterFirstAffectedPazPartReplacement, "Update Existing Build hotfix / after first affected PAZ part replacement"),
        (HotfixAfterPamtBeforePathc, "Update Existing Build hotfix / after PAMT update before PATHC")
    };

    private static int s_triggered;
    private static Action<string>? s_log;

    public static bool Enabled { get; private set; }
    public static string SelectedPoint { get; private set; } = None;
    public static string OperationName { get; private set; } = string.Empty;

    public static IReadOnlyList<(string Code, string Label)> FailurePoints => s_points;

    public static string LabelFor(string? code)
    {
        string normalized = NormalizePoint(code);
        return s_points.FirstOrDefault(p => string.Equals(p.Code, normalized, StringComparison.OrdinalIgnoreCase)).Label ?? s_points[0].Label;
    }

    public static string StageLabelFor(string? code)
    {
        string label = LabelFor(code);
        int slash = label.IndexOf('/');
        return slash >= 0 ? label[(slash + 1)..].Trim() : label;
    }

    public static string NormalizePoint(string? code)
    {
        string c = (code ?? string.Empty).Trim();
        if (s_points.Any(p => string.Equals(p.Code, c, StringComparison.OrdinalIgnoreCase)))
            return s_points.First(p => string.Equals(p.Code, c, StringComparison.OrdinalIgnoreCase)).Code;
        return None;
    }

    public static void Configure(bool enabled)
    {
        Enabled = enabled;
    }

    public static void BeginOperation(string operationName, string selectedPoint, Action<string>? log)
    {
        if (!Enabled) return;
        OperationName = string.IsNullOrWhiteSpace(operationName) ? "Unknown operation" : operationName.Trim();
        SelectedPoint = NormalizePoint(selectedPoint);
        s_log = log;
        Interlocked.Exchange(ref s_triggered, 0);
        log?.Invoke("DEBUGMODE enabled");
        log?.Invoke($"Selected simulated failure: {OperationName} / {StageLabelFor(SelectedPoint)}");
    }

    public static void EndOperation()
    {
        OperationName = string.Empty;
        SelectedPoint = None;
        s_log = null;
        Interlocked.Exchange(ref s_triggered, 0);
    }

    public static bool IsSimulated(Exception ex) => ex is SimulatedFailureException;

    public static void Check(string point, Action<string>? log = null)
    {
        if (!Enabled) return;
        string normalized = NormalizePoint(point);
        if (normalized == None || !string.Equals(normalized, SelectedPoint, StringComparison.OrdinalIgnoreCase)) return;
        if (!PointAppliesToCurrentOperation(normalized)) return;
        if (Interlocked.Exchange(ref s_triggered, 1) != 0) return;

        var logger = log ?? s_log;
        logger?.Invoke($"Simulated failure triggered: {OperationName} / {StageLabelFor(normalized)}");
        throw new SimulatedFailureException(OperationName, normalized, $"Simulated debug failure: {OperationName} / {StageLabelFor(normalized)}");
    }

    private static bool PointAppliesToCurrentOperation(string point)
    {
        string op = OperationName ?? string.Empty;
        if (point.StartsWith("fresh_", StringComparison.OrdinalIgnoreCase))
            return op.IndexOf("Fresh Easy Apply", StringComparison.OrdinalIgnoreCase) >= 0;
        if (point.StartsWith("easy_existing_", StringComparison.OrdinalIgnoreCase))
            return op.IndexOf("Easy Apply over existing", StringComparison.OrdinalIgnoreCase) >= 0;
        if (point.StartsWith("relink_", StringComparison.OrdinalIgnoreCase))
            return op.IndexOf("Relink", StringComparison.OrdinalIgnoreCase) >= 0;
        if (point.StartsWith("hotfix_", StringComparison.OrdinalIgnoreCase))
            return op.IndexOf("Update Existing Build", StringComparison.OrdinalIgnoreCase) >= 0;
        return true;
    }
}
