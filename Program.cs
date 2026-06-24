using System;
using System.Windows.Forms;
using System.Diagnostics;

namespace CDTextureOverlayBuilder;

internal static class Program
{
    public static string ProcessPriorityStatus { get; private set; } = "Process priority: Normal";
    public static bool DebugModeEnabled { get; private set; }
    public static bool ResetWindowRequested { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        string[] launchArgs = args ?? Array.Empty<string>();
        DebugModeEnabled = Array.Exists(launchArgs, a => string.Equals(a, "--debugmode", StringComparison.OrdinalIgnoreCase));
        ResetWindowRequested = Array.Exists(launchArgs, a => string.Equals(a, "--reset-window", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--reset-window-state", StringComparison.OrdinalIgnoreCase));
        CDTextureOverlayBuilder.Core.DebugFailureInjector.Configure(DebugModeEnabled);
        TrySetHighPriority();
        // DPI awareness is configured in the project file with
        // <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>.
        // ApplicationConfiguration.Initialize() applies that source generated setup.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void TrySetHighPriority()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.PriorityClass = ProcessPriorityClass.High;
            ProcessPriorityStatus = "Process priority: High";
        }
        catch
        {
            ProcessPriorityStatus = "Process priority: High request failed; continuing at normal priority";
        }
    }
}
