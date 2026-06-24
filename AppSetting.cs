using CDTextureOverlayBuilder.Core;
using System.Text.Json;

namespace CDTextureOverlayBuilder;

internal sealed class AppSettings
{
    private const int CurrentSettingsFormatVersion = 11;
    private const int DefaultWindowWidth = 1646;
    private const int DefaultWindowHeight = 905;

    public int SettingsFormatVersion { get; set; }
    public string SavedAppVersion { get; set; } = "";
    public string Language { get; set; } = "en";
    public string GameFolder { get; set; } = "";
    public string TextureFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string FilterPreset { get; set; } = "objects";
    public int WindowWidth { get; set; } = DefaultWindowWidth;
    public int WindowHeight { get; set; } = DefaultWindowHeight;
    public bool WindowMaximized { get; set; } = false;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public string PerformanceMemoryMode { get; set; } = "Auto";
    public string UiScaleMode { get; set; } = "Auto";
    public int CustomPrepareWorkers { get; set; } = 8;

    public static bool WasResetForVersionChange { get; private set; }
    public static string ResetReason { get; private set; } = "";

    private const string SettingsFolderName = "HDOverlayBuilder";
    private const string LegacySettingsFolderName = "HDUpscaleOverlayBuilder";

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SettingsFolderName);

    private static string LegacySettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LegacySettingsFolderName);

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    private static string LegacySettingsPath => Path.Combine(LegacySettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        WasResetForVersionChange = false;
        ResetReason = "";

        foreach (string path in new[] { SettingsPath, LegacySettingsPath }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (settings != null)
                {
                    if (!settings.IsCompatibleWithCurrentExe())
                    {
                        ResetForVersionChange(settings);
                        return CreateFresh();
                    }

                    settings.Normalize();
                    if (!string.Equals(path, SettingsPath, StringComparison.OrdinalIgnoreCase))
                        TryCopyLegacySettingsToCurrent(path);
                    return settings;
                }
            }
            catch
            {
                // Corrupt or unreadable settings should not block startup. Start
                // fresh and let the next save replace the bad file.
                TryDeleteFile(path);
            }
        }

        return CreateFresh();
    }

    public void Save()
    {
        SettingsFormatVersion = CurrentSettingsFormatVersion;
        SavedAppVersion = OverlayService.AppVersion;
        Normalize();

        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static AppSettings CreateFresh()
        => new()
        {
            SettingsFormatVersion = CurrentSettingsFormatVersion,
            SavedAppVersion = OverlayService.AppVersion,
            WindowWidth = DefaultWindowWidth,
            WindowHeight = DefaultWindowHeight,
            WindowX = int.MinValue,
            WindowY = int.MinValue
        };

    private bool IsCompatibleWithCurrentExe()
        => SettingsFormatVersion == CurrentSettingsFormatVersion
           && string.Equals(SavedAppVersion, OverlayService.AppVersion, StringComparison.OrdinalIgnoreCase);

    private void Normalize()
    {
        if (!L.IsSupportedLanguage(Language)) Language = "en";
        PerformanceMemoryMode = string.IsNullOrWhiteSpace(PerformanceMemoryMode) ? "Auto" : PerformanceMemoryMode.Trim();
        UiScaleMode = string.IsNullOrWhiteSpace(UiScaleMode) ? "Auto" : UiScaleMode.Trim();
        FilterPreset = string.IsNullOrWhiteSpace(FilterPreset) ? "objects" : FilterPreset.Trim();
        if (CustomPrepareWorkers <= 0) CustomPrepareWorkers = 8;
        if (WindowWidth <= 0) WindowWidth = DefaultWindowWidth;
        if (WindowHeight <= 0) WindowHeight = DefaultWindowHeight;
        if (WindowX == 0 && WindowY == 0 && WindowWidth == DefaultWindowWidth && WindowHeight == DefaultWindowHeight)
        {
            WindowX = int.MinValue;
            WindowY = int.MinValue;
        }
    }

    private static void ResetForVersionChange(AppSettings oldSettings)
    {
        WasResetForVersionChange = true;
        string oldVersion = string.IsNullOrWhiteSpace(oldSettings.SavedAppVersion) ? "older build" : oldSettings.SavedAppVersion;
        ResetReason = $"App settings/cache reset for this EXE version ({oldVersion} -> {OverlayService.AppVersion}).";

        TryDeleteFile(SettingsPath);
        TryDeleteFile(LegacySettingsPath);
        TryDeleteDirectory(Path.Combine(AppContext.BaseDirectory, "Cache"));
        TryDeleteDirectory(Path.Combine(AppContext.BaseDirectory, "State"));
    }


    private static void TryCopyLegacySettingsToCurrent(string legacyPath)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            if (!File.Exists(SettingsPath)) File.Copy(legacyPath, SettingsPath, overwrite: false);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
