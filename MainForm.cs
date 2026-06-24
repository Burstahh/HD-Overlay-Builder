using CDTextureOverlayBuilder.Core;
using System.Text;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CDTextureOverlayBuilder;


internal static class GuiFonts
{
    public static float UiScale { get; set; } = 1.0F;

    private static readonly PrivateFontCollection s_privateFonts = new();
    private static readonly InstalledFontCollection s_installedFonts = new();
    private static readonly List<GCHandle> s_pinnedFontBuffers = new();
    private static readonly FontFamily s_uiFontFamily = ResolveUiFontFamily();

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        float scaledSize = Math.Max(6F, size * Math.Max(0.75F, Math.Min(2.25F, UiScale)));
        try { return new Font(s_uiFontFamily, scaledSize, style); }
        catch { return new Font("Segoe UI", scaledSize, style); }
    }

    private static FontFamily ResolveUiFontFamily()
    {
        LoadEmbeddedNotoFonts();
        LoadLocalNotoFonts();

        foreach (var family in s_privateFonts.Families)
        {
            if (string.Equals(family.Name, "Noto Sans", StringComparison.OrdinalIgnoreCase)) return family;
        }

        foreach (var family in s_installedFonts.Families)
        {
            if (string.Equals(family.Name, "Noto Sans", StringComparison.OrdinalIgnoreCase)) return family;
        }

        return new FontFamily("Segoe UI");
    }

    private static void LoadEmbeddedNotoFonts()
    {
        var assembly = typeof(GuiFonts).Assembly;
        string[] names =
        {
            "Fonts.NotoSans-Regular.ttf",
            "Fonts.NotoSans-Bold.ttf",
            "Fonts.NotoSans-Italic.ttf",
            "Fonts.NotoSans-BoldItalic.ttf"
        };

        foreach (string name in names)
        {
            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                if (bytes.Length == 0) continue;

                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                s_pinnedFontBuffers.Add(handle);
                s_privateFonts.AddMemoryFont(handle.AddrOfPinnedObject(), bytes.Length);
            }
            catch { }
        }
    }

    private static void LoadLocalNotoFonts()
    {
        foreach (string path in CandidateNotoFontPaths())
        {
            try
            {
                if (File.Exists(path)) s_privateFonts.AddFontFile(path);
            }
            catch { }
        }
    }

    private static IEnumerable<string> CandidateNotoFontPaths()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] roots =
        {
            Path.Combine(baseDir, "Fonts"),
            Path.Combine(baseDir, "Resources", "Fonts"),
            Path.Combine(Environment.CurrentDirectory, "Fonts"),
            Path.Combine(Environment.CurrentDirectory, "Resources", "Fonts")
        };

        foreach (string dir in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(dir, "NotoSans-Regular.ttf");
            yield return Path.Combine(dir, "NotoSans-Bold.ttf");
            yield return Path.Combine(dir, "NotoSans-Italic.ttf");
            yield return Path.Combine(dir, "NotoSans-BoldItalic.ttf");
        }
    }
}

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private string _lang;
    private string _presetId;
    private bool _refreshingPreset;
    private bool _presetChanging;

    private readonly TextBox _game = new();
    private readonly TextBox _textures = new();
    private readonly TextBox _modName = new();
    private readonly Dictionary<string, CheckBox> _presetButtons = new();
    private readonly CheckBox _apply = new();
    private readonly CheckBox _unique = new();
    private readonly CheckBox _dry = new();
    private readonly CheckBox _backup = new();
    private readonly CheckBox _scan = new();
    private readonly CheckBox _multiTarget = new();
    private readonly CheckBox _updateExisting = new();
    private readonly ComboBox _memoryMode = new();
    private readonly NumericUpDown _customWorkers = new();
    private readonly TextProgressBar _progress = new();
    private readonly System.Windows.Forms.Timer _activityTimer = new() { Interval = 120 };
    private readonly RichTextBox _log = new();
    private readonly Dictionary<Control, string> _keys = new();
    private readonly Dictionary<Control, string> _sectionTitleLabels = new();
    private readonly Dictionary<Control, string> _tipTexts = new();
    private Panel? _inlineTipPanel;
    private Label? _inlineTipLabel;
    private Panel? _advancedPresetPanel;
    private Button? _advancedPresetToggle;
    private bool _showAdvancedFilters;
    private ComboBox _languageDrop = new();
    private ComboBox _uiScaleDrop = new();
    private bool _refreshingLanguageDrop;
    private bool _refreshingMemoryMode;
    private bool _refreshingUiScaleDrop;
    private string _uiScaleCode = "Auto";
    private float _uiScaleFactor = 1.0F;
    private Button _runBtn = new();
    private Button _cancelBtn = new();
    private Button _easyApplyBtn = new();
    private Button _updateBuildBtn = new();
    private Button _relinkBtn = new();
    private Label _statusPill = new();
    private Button? _chromeMaximizeButton;
    private ComboBox? _debugFailureDrop;
    private readonly Label _portCredit = new();
    private readonly string _runtimeLogPath = Path.Combine(AppContext.BaseDirectory, "builder_runtime.log");
    private string _lastReportPath = string.Empty;
    private bool _busy;
    private CancellationTokenSource? _currentBuildCancel;
    private bool _windowDragArmed;
    private Point _windowDragStartClient;
    private Control? _windowDragSource;
    private Panel? _mainScroller;
    private Panel? _dashboardHost;
    private TableLayoutPanel? _dashboardCanvas;
    private float _dashboardCanvasScale = 1.0F;
    private bool _applyingDashboardCanvasScale;
    private bool _dashboardCanvasScaleQueued;
    private readonly bool _resetWindowStateOnStartup;
    private bool _startupWindowPlacementWasReset;
    private bool _repairingWindowPlacement;
    private FormWindowState _lastObservedWindowState = FormWindowState.Normal;
    private Rectangle _lastSafeNormalWindowBounds = Rectangle.Empty;
    private Rectangle _pendingNativeMaximizeWorkArea = Rectangle.Empty;
    private float _dashboardCanvasBaseDpiBasis = 1.0F;
    private TableLayoutPanel? _bodyPanel;
    private readonly List<Label> _wrappingNotes = new();
    private Panel? _leftResponsiveSpacer;
    private Panel? _controlResponsiveSpacer;
    private TableLayoutPanel? _leftCard;
    private TableLayoutPanel? _controlCard;
    private static bool UseCustomChrome => false;
    private const int DefaultClientWidth = 1646;
    private const int DefaultClientHeight = 905;
    private const int DesignBodyWidth = 1618;
    private const int DesignBodyHeight = 809;
    private const int MinClientWidth = 1220;
    private const int MinClientHeight = 660;
    private const int MinBodyWidth = 1180;
    private const int MinBodyHeight = 700;
    private const float MinimumCanvasScale = 0.62F;
    private const float MaximumCanvasScale = 2.75F;
    private static readonly Color ShellBackColor = Color.FromArgb(8, 13, 22);
    private static readonly Color CardBackColor = Color.FromArgb(17, 28, 43);
    private static readonly Color CardBackColorAlt = Color.FromArgb(14, 23, 36);
    private static readonly Color CardBorderColor = Color.FromArgb(45, 64, 86);
    private static readonly Color MutedTextColor = Color.FromArgb(148, 165, 188);
    private static readonly Color InputBackColor = Color.FromArgb(9, 16, 27);
    private static readonly Color VersionButtonColor = Color.FromArgb(16, 32, 55);
    private static readonly Color PrimaryButtonColor = Color.FromArgb(45, 116, 246);
    private static readonly Color UpdateButtonColor = Color.FromArgb(219, 153, 26);
    private static readonly Color MaintenanceButtonColor = Color.FromArgb(26, 63, 105);
    private static readonly Color DangerButtonColor = Color.FromArgb(165, 31, 42);
    private static readonly Color SuccessButtonColor = Color.FromArgb(29, 97, 57);

    public MainForm()
    {
        _settings = AppSettings.Load();
        _resetWindowStateOnStartup = Program.ResetWindowRequested || IsShiftKeyDownForWindowReset();
        if (_resetWindowStateOnStartup)
        {
            ResetSavedWindowPlacement(inMemoryOnly: true);
            _startupWindowPlacementWasReset = true;
        }
        _lang = _settings.Language;
        _uiScaleCode = NormalizeUiScaleCode(_settings.UiScaleMode);
        _uiScaleFactor = ResolveUiScaleFactor(_uiScaleCode);
        _uiScaleFactor = ClampUiScaleForCurrentDisplay(_uiScaleFactor, _uiScaleCode);
        GuiFonts.UiScale = _uiScaleFactor;
        _presetId = OverlayService.GetPreset(_settings.FilterPreset).Id;
        _showAdvancedFilters = IsAdvancedFilterPresetId(_presetId);

        Text = "HD Overlay Builder";
        // Stability-first rollback from the failed live-scaling experiments:
        // keep one fixed dashboard layout, let our explicit Dpi()/UI-scale math
        // size controls once, and do not let WinForms autoscale fight that layout.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.None;
        // Use the standard Windows resizable frame for the main shell. The earlier
        // custom chrome looked cleaner, but it caused broken resize/window-control
        // behavior on some systems. Keep the internal dark/card styling only.
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MaximizeBox = true;
        MinimizeBox = true;
        SizeGripStyle = SizeGripStyle.Auto;
        ClientSize = GetInitialClientSize();
        ApplyResponsiveMinimumSize();
        StartPosition = FormStartPosition.Manual;
        ApplyInitialWindowPlacement();
        RememberNormalWindowBounds();
        BackColor = ShellBackColor;
        ForeColor = Color.FromArgb(230, 238, 248);
        Font = GuiFonts.UiFont(9F);
        DoubleBuffered = true;
        var appIcon = LoadAppIcon();
        if (appIcon is not null)
        {
            Icon = appIcon;
        }
        try { File.WriteAllText(_runtimeLogPath, $"HD Overlay Builder {OverlayService.AppVersion}{Environment.NewLine}", Encoding.UTF8); } catch { }

        BuildUi();
        ApplyLanguage();

        _activityTimer.Tick += (_, _) =>
        {
            if (!_busy) return;
            _progress.StepActivity();
        };

        _game.Text = _settings.GameFolder;
        _textures.Text = _settings.TextureFolder;
        _modName.Text = OverlayService.DefaultModName;
        _apply.Checked = true;
        _unique.Checked = true;
        _backup.Checked = true;
        _scan.Checked = true;
        _multiTarget.Checked = false;
        _updateExisting.Checked = false;
        InitMemoryModeDefaults();
        RefreshTooltips();

        Shown += (_, _) =>
        {
            UpdateMaximizedBounds();
            ApplyDashboardCanvasScale();
            Log("READY");

            // Keep startup diagnostics and automatic version-upgrade settings/cache resets
            // out of the public Activity Log. End users only need to see that the app is
            // ready; the detailed startup state still goes to builder_runtime.log for QA.
            if (AppSettings.WasResetForVersionChange && !string.IsNullOrWhiteSpace(AppSettings.ResetReason)) LogRuntimeOnly(AppSettings.ResetReason);
            LogRuntimeOnly(Program.ProcessPriorityStatus);
            LogRuntimeOnly($"UI scale: {UiScaleLogDescription()}");
            LogRuntimeOnly("Runtime log: " + _runtimeLogPath);
            if (_startupWindowPlacementWasReset) LogRuntimeOnly("Window placement reset to a safe centered position.");

            if (Program.DebugModeEnabled) Log("DEBUGMODE enabled (QA simulated failure UI visible). Launch without --debugmode for normal/public behavior.");
            if (string.IsNullOrWhiteSpace(_game.Text)) AutoDetect(silent: true);
            CheckIncompleteStartupArtifacts();
            if (!_startupWindowPlacementWasReset && _settings.WindowMaximized)
            {
                UpdateMaximizedBoundsForCurrentContext();
                WindowState = FormWindowState.Maximized;
                QueueWindowPlacementSafetyCheck();
            }
            UpdateChromeMaximizeButton();
            BeginInvoke(new Action(ApplyDashboardCanvasScale));
        };
        ResizeEnd += (_, _) => { RememberNormalWindowBounds(); UpdateMaximizedBounds(); UpdateChromeMaximizeButton(); ApplyDashboardCanvasScale(); SaveSettings(); };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Normal)
            {
                _pendingNativeMaximizeWorkArea = Rectangle.Empty;
                RememberNormalWindowBounds();
            }
            UpdateMaximizedBoundsForCurrentContext();
            UpdateChromeMaximizeButton();
            QueueDashboardCanvasScale();
            PositionPortCredit();
            QueueWindowPlacementSafetyCheck();
        };
        Move += (_, _) =>
        {
            if (WindowState == FormWindowState.Normal)
            {
                _pendingNativeMaximizeWorkArea = Rectangle.Empty;
                RememberNormalWindowBounds();
                UpdateMaximizedBoundsForCurrentContext();
            }
        };
        FormClosing += (_, _) => SaveSettings();
        Deactivate += (_, _) => HideInlineTip();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RefreshManualDpiLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RefreshManualDpiLayout();
    }

    private void RefreshManualDpiLayout()
    {
        UpdateMaximizedBounds();
        ApplyResponsiveMinimumSize();
        ApplyDashboardCanvasScale();
        PositionPortCredit();
    }

    private void ApplyResponsiveMinimumSize()
    {
        // Form.MinimumSize includes the non-client title bar/borders.  Convert
        // the stable dashboard client minimum to total window size so users
        // cannot shrink the native frame into clipped controls/scrollbars.
        try
        {
            MinimumSize = SizeFromClientSize(GetSafeMinimumClientSize());
        }
        catch
        {
            MinimumSize = GetSafeMinimumClientSize();
        }
    }

    private void ApplyResponsiveMinimumSizeForWorkingArea(Rectangle workArea)
    {
        if (workArea.IsEmpty)
        {
            ApplyResponsiveMinimumSize();
            return;
        }

        try
        {
            Size safeClient = GetSafeMinimumClientSize();
            int width = Math.Min(safeClient.Width, Math.Max(Dpi(640), workArea.Width - Dpi(24)));
            int height = Math.Min(safeClient.Height, Math.Max(Dpi(420), workArea.Height - Dpi(24)));
            MinimumSize = SizeFromClientSize(new Size(width, height));
        }
        catch
        {
            ApplyResponsiveMinimumSize();
        }
    }

    private Size GetSafeMinimumClientSize()
    {
        int width = Math.Max(Dpi(MinClientWidth), Dpi(DefaultClientWidth));
        int height = Math.Max(Dpi(MinClientHeight), Dpi(DefaultClientHeight));

        // Prefer the validated 1646x905 design canvas as the minimum usable
        // client area. If the current monitor cannot fit the full requested
        // UI scale, ClampUiScaleForCurrentDisplay reduces the effective scale
        // first; this method only prevents impossible minimum sizes.
        try
        {
            Rectangle work = Screen.FromControl(this)?.WorkingArea
                ?? Screen.PrimaryScreen?.WorkingArea
                ?? Rectangle.Empty;
            if (!work.IsEmpty)
            {
                int margin = Dpi(40);
                width = Math.Min(width, Math.Max(Dpi(640), work.Width - margin));
                height = Math.Min(height, Math.Max(Dpi(420), work.Height - margin));
            }
        }
        catch { }

        return new Size(width, height);
    }

    private int Dpi(int value)
    {
        int dpi = 96;
        try { dpi = DeviceDpi; } catch { }
        return (int)Math.Round(value * (dpi / 96.0) * _uiScaleFactor);
    }

    private const int WM_SETREDRAW = 0x000B;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int SB_BOTH = 3;
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int SC_SIZE = 0xF000;
    private const int SC_MOVE = 0xF010;
    private const int SC_MAXIMIZE = 0xF030;
    private const int SC_RESTORE = 0xF120;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int CS_DROPSHADOW = 0x00020000;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (UseCustomChrome)
            {
                cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                cp.ClassStyle |= CS_DROPSHADOW;
            }
            return cp;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTSTRUCT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINTSTRUCT ptReserved;
        public POINTSTRUCT ptMaxSize;
        public POINTSTRUCT ptMaxPosition;
        public POINTSTRUCT ptMinTrackSize;
        public POINTSTRUCT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(Rectangle r)
        {
            Left = r.Left;
            Top = r.Top;
            Right = r.Right;
            Bottom = r.Bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_GETMINMAXINFO)
        {
            ApplyCurrentMonitorMinMaxInfo(m.LParam);
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_SYSCOMMAND)
        {
            int command = m.WParam.ToInt32() & 0xFFF0;
            if (command == SC_MAXIMIZE)
            {
                RememberNormalWindowBounds();
                PrepareNativeMaximizeForCurrentMonitor();
            }
            else if (command == SC_RESTORE || command == SC_MOVE || command == SC_SIZE)
            {
                UpdateMaximizedBoundsForCurrentContext();
            }
        }
        else if (m.Msg == WM_NCLBUTTONDBLCLK)
        {
            RememberNormalWindowBounds();
            PrepareNativeMaximizeForCurrentMonitor();
        }

        if (UseCustomChrome && m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        if (UseCustomChrome && m.Msg == WM_NCHITTEST && WindowState != FormWindowState.Maximized)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                long lp = m.LParam.ToInt64();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                Point p = PointToClient(new Point(x, y));
                int grip = Math.Max(6, Dpi(10));
                bool left = p.X <= grip;
                bool right = p.X >= ClientSize.Width - grip;
                bool top = p.Y <= grip;
                bool bottom = p.Y >= ClientSize.Height - grip;

                if (left && top) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (right && top) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (left && bottom) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (right && bottom) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (left) { m.Result = (IntPtr)HTLEFT; return; }
                if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                if (top) { m.Result = (IntPtr)HTTOP; return; }
                if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }
            }
            return;
        }

        base.WndProc(ref m);

        if (m.Msg == WM_SYSCOMMAND || m.Msg == WM_EXITSIZEMOVE || m.Msg == WM_NCLBUTTONDBLCLK)
        {
            QueueWindowPlacementSafetyCheck();
        }
    }

    private Rectangle RectangleFromNative(RECT rect)
        => Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private bool TryGetMonitorInfoFromRect(Rectangle bounds, out Rectangle monitorArea, out Rectangle workArea)
    {
        monitorArea = Rectangle.Empty;
        workArea = Rectangle.Empty;
        try
        {
            RECT rect = new(bounds);
            IntPtr monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero && IsHandleCreated)
                monitor = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            monitorArea = RectangleFromNative(info.rcMonitor);
            workArea = RectangleFromNative(info.rcWork);
            return !monitorArea.IsEmpty && !workArea.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetMonitorInfoFromWindow(out Rectangle monitorArea, out Rectangle workArea)
    {
        monitorArea = Rectangle.Empty;
        workArea = Rectangle.Empty;
        try
        {
            IntPtr monitor = IsHandleCreated ? MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
            if (monitor == IntPtr.Zero) return false;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            monitorArea = RectangleFromNative(info.rcMonitor);
            workArea = RectangleFromNative(info.rcWork);
            return !monitorArea.IsEmpty && !workArea.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    private Rectangle GetNativeMaximizeTargetBounds()
    {
        Rectangle target = GetBestRestoreOrNormalBounds();
        if (target.Width <= 0 || target.Height <= 0) target = Bounds;
        if ((target.Width <= 0 || target.Height <= 0) && _lastSafeNormalWindowBounds.Width > 0 && _lastSafeNormalWindowBounds.Height > 0) target = _lastSafeNormalWindowBounds;
        return target;
    }

    private void PrepareNativeMaximizeForCurrentMonitor()
    {
        try
        {
            Rectangle target = GetNativeMaximizeTargetBounds();
            if (!TryGetMonitorInfoFromRect(target, out _, out Rectangle work))
            {
                Screen? screen = GetScreenForWindowRectangle(target) ?? Screen.FromControl(this) ?? Screen.PrimaryScreen;
                work = screen?.WorkingArea ?? Rectangle.Empty;
            }

            if (!work.IsEmpty)
            {
                _pendingNativeMaximizeWorkArea = work;
                MaximizedBounds = work;
                ApplyResponsiveMinimumSizeForWorkingArea(work);
            }
        }
        catch { }
    }

    private void ApplyCurrentMonitorMinMaxInfo(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return;
        try
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            Rectangle target = GetNativeMaximizeTargetBounds();
            Rectangle monitorArea;
            Rectangle workArea;
            if (!_pendingNativeMaximizeWorkArea.IsEmpty
                && TryGetMonitorInfoFromRect(_pendingNativeMaximizeWorkArea, out monitorArea, out workArea))
            {
                workArea = _pendingNativeMaximizeWorkArea;
            }
            else if (!TryGetMonitorInfoFromRect(target, out monitorArea, out workArea))
            {
                if (!TryGetMonitorInfoFromWindow(out monitorArea, out workArea)) return;
            }

            if (monitorArea.IsEmpty || workArea.IsEmpty) return;

            mmi.ptMaxPosition.X = workArea.Left - monitorArea.Left;
            mmi.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
            mmi.ptMaxSize.X = workArea.Width;
            mmi.ptMaxSize.Y = workArea.Height;
            mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, workArea.Width);
            mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, workArea.Height);

            // Do not let a saved high-DPI minimum size force Windows to choose
            // a different/larger monitor when the user maximizes from a smaller
            // side display. The scaled dashboard can fit smaller monitors; keep
            // minimum tracking conservative at the native shell level.
            mmi.ptMinTrackSize.X = Math.Min(Math.Max(640, mmi.ptMinTrackSize.X), Math.Max(640, workArea.Width));
            mmi.ptMinTrackSize.Y = Math.Min(Math.Max(420, mmi.ptMinTrackSize.Y), Math.Max(420, workArea.Height));

            Marshal.StructureToPtr(mmi, lParam, true);
        }
        catch { }
    }

    private void UpdateMaximizedBounds() => UpdateMaximizedBoundsForCurrentContext();

    private void UpdateMaximizedBoundsForCurrentContext()
    {
        try
        {
            Screen? screen = GetBestWindowScreen();
            Rectangle work = screen?.WorkingArea
                ?? Screen.PrimaryScreen?.WorkingArea
                ?? Rectangle.Empty;
            if (!work.IsEmpty) MaximizedBounds = work;
        }
        catch { }
    }

    private void RememberNormalWindowBounds()
    {
        try
        {
            if (WindowState != FormWindowState.Normal) return;
            Rectangle b = Bounds;
            if (b.Width <= 0 || b.Height <= 0) return;
            if (TryGetSafeSavedWindowPlacement(b, out Rectangle safe))
            {
                _lastSafeNormalWindowBounds = safe;
            }
        }
        catch { }
    }

    private Rectangle GetBestRestoreOrNormalBounds()
    {
        try
        {
            if (WindowState == FormWindowState.Normal && Bounds.Width > 0 && Bounds.Height > 0)
                return Bounds;

            Rectangle restore = RestoreBounds;
            if (restore.Width > 0 && restore.Height > 0 && Math.Abs(restore.X) < 100000 && Math.Abs(restore.Y) < 100000)
                return restore;

            if (_lastSafeNormalWindowBounds.Width > 0 && _lastSafeNormalWindowBounds.Height > 0)
                return _lastSafeNormalWindowBounds;
        }
        catch { }
        return Rectangle.Empty;
    }

    private Screen? GetScreenForWindowRectangle(Rectangle bounds)
    {
        try
        {
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                Point center = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                foreach (var screen in Screen.AllScreens)
                {
                    if (screen.WorkingArea.Contains(center) || screen.Bounds.Contains(center))
                        return screen;
                }

                Screen? best = null;
                long bestArea = -1;
                foreach (var screen in Screen.AllScreens)
                {
                    Rectangle hit = Rectangle.Intersect(bounds, screen.WorkingArea);
                    if (hit.IsEmpty) hit = Rectangle.Intersect(bounds, screen.Bounds);
                    long area = hit.Width * (long)hit.Height;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = screen;
                    }
                }
                if (best is not null && bestArea > 0) return best;

                return Screen.FromPoint(center);
            }
        }
        catch { }
        return null;
    }

    private Screen? GetBestWindowScreen()
    {
        try
        {
            Rectangle target = GetBestRestoreOrNormalBounds();
            Screen? fromBounds = GetScreenForWindowRectangle(target);
            if (fromBounds is not null) return fromBounds;

            return Screen.FromHandle(Handle) ?? Screen.FromControl(this) ?? Screen.PrimaryScreen;
        }
        catch
        {
            return Screen.PrimaryScreen;
        }
    }

    private void QueueWindowPlacementSafetyCheck()
    {
        if (_repairingWindowPlacement || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(EnsureWindowPlacementStillVisible));
        }
        catch { }
    }

    private void EnsureWindowPlacementStillVisible()
    {
        if (_repairingWindowPlacement || IsDisposed || !IsHandleCreated) return;
        if (WindowState == FormWindowState.Minimized) return;

        _repairingWindowPlacement = true;
        try
        {
            UpdateMaximizedBoundsForCurrentContext();

            if (WindowState == FormWindowState.Maximized)
            {
                Rectangle work = Rectangle.Empty;
                if (!TryGetMonitorInfoFromWindow(out _, out work))
                {
                    Screen? screen = GetBestWindowScreen();
                    work = screen?.WorkingArea
                        ?? Screen.PrimaryScreen?.WorkingArea
                        ?? Rectangle.Empty;
                }
                if (!work.IsEmpty)
                {
                    MaximizedBounds = work;

                    Rectangle visible = Rectangle.Intersect(Bounds, work);
                    bool escaped = visible.Width < Dpi(420)
                        || visible.Height < Dpi(320)
                        || visible.Width * (double)visible.Height < Bounds.Width * (double)Bounds.Height * 0.35;

                    if (escaped)
                    {
                        Rectangle restore = GetBestRestoreOrNormalBounds();
                        WindowState = FormWindowState.Normal;
                        if (restore.Width > 0 && restore.Height > 0)
                        {
                            Bounds = ClampWindowBoundsToWorkingArea(restore, work);
                            RememberNormalWindowBounds();
                        }
                        else
                        {
                            ClientSize = GetInitialClientSize();
                            CenterOnWorkingArea(work);
                            RememberNormalWindowBounds();
                        }
                        MaximizedBounds = work;
                        WindowState = FormWindowState.Maximized;
                    }
                }
            }
            else if (WindowState == FormWindowState.Normal)
            {
                if (!TryGetSafeSavedWindowPlacement(Bounds, out Rectangle safeBounds))
                {
                    ResetSavedWindowPlacement(inMemoryOnly: true);
                    CenterOnPrimaryWorkingArea();
                }
                else if (safeBounds.Location != Location)
                {
                    Location = safeBounds.Location;
                }
            }

            if (_lastObservedWindowState != WindowState)
            {
                _lastObservedWindowState = WindowState;
                ApplyDashboardCanvasScale();
            }
        }
        catch { }
        finally
        {
            _repairingWindowPlacement = false;
        }
    }

    private void HookWindowDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _windowDragArmed = true;
            _windowDragStartClient = e.Location;
            _windowDragSource = control;
        };
        control.MouseMove += (_, e) =>
        {
            if (!_windowDragArmed || _windowDragSource is null || e.Button != MouseButtons.Left) return;
            Size threshold = SystemInformation.DragSize;
            if (Math.Abs(e.X - _windowDragStartClient.X) < Math.Max(3, threshold.Width / 2)
                && Math.Abs(e.Y - _windowDragStartClient.Y) < Math.Max(3, threshold.Height / 2)) return;
            BeginWindowDrag(_windowDragSource, _windowDragStartClient);
        };
        control.MouseUp += (_, _) =>
        {
            _windowDragArmed = false;
            _windowDragSource = null;
        };
        control.DoubleClick += (_, _) => ToggleMaximizeRestore();
    }

    private void BeginWindowDrag(Control dragSource, Point startClientPoint)
    {
        _windowDragArmed = false;
        _windowDragSource = null;

        if (WindowState == FormWindowState.Maximized)
        {
            Point cursor = Cursor.Position;
            Rectangle sourceBounds = dragSource.RectangleToScreen(dragSource.ClientRectangle);
            float sourceRatio = sourceBounds.Width <= 0 ? 0.5F : (cursor.X - sourceBounds.Left) / (float)sourceBounds.Width;
            sourceRatio = Math.Max(0.12F, Math.Min(0.88F, sourceRatio));

            WindowState = FormWindowState.Normal;
            UpdateChromeMaximizeButton();
            UpdateMaximizedBounds();

            Rectangle work = Screen.FromPoint(cursor)?.WorkingArea ?? Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
            int x = cursor.X - (int)Math.Round(ClientSize.Width * sourceRatio);
            int y = work.IsEmpty ? cursor.Y - Dpi(14) : Math.Max(work.Top + Dpi(4), cursor.Y - Dpi(14));
            Location = new Point(x, y);
            RememberNormalWindowBounds();
        }

        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private void ToggleMaximizeRestore()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
        }
        else
        {
            RememberNormalWindowBounds();
            PrepareNativeMaximizeForCurrentMonitor();
            WindowState = FormWindowState.Maximized;
        }
        UpdateChromeMaximizeButton();
        QueueWindowPlacementSafetyCheck();
    }

    private void UpdateChromeMaximizeButton()
    {
        if (_chromeMaximizeButton is not null && !_chromeMaximizeButton.IsDisposed)
            _chromeMaximizeButton.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
    }

    private Control CreateWindowControls()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(Dpi(10), Dpi(14), 0, 0),
            Padding = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(34)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(34)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(34)));

        var min = ChromeButton("—", () => WindowState = FormWindowState.Minimized);
        _chromeMaximizeButton = ChromeButton("□", ToggleMaximizeRestore);
        var close = ChromeButton("×", Close, danger: true);
        panel.Controls.Add(min, 0, 0);
        panel.Controls.Add(_chromeMaximizeButton, 1, 0);
        panel.Controls.Add(close, 2, 0);
        UpdateChromeMaximizeButton();
        return panel;
    }

    private Button ChromeButton(string text, Action action, bool danger = false)
    {
        var b = new Button
        {
            Text = text,
            Width = Dpi(30),
            Height = Dpi(28),
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(Dpi(2), 0, 0, 0),
            BackColor = Color.FromArgb(18, 29, 45),
            ForeColor = danger ? Color.FromArgb(255, 150, 158) : Color.FromArgb(198, 214, 236),
            Font = GuiFonts.UiFont(10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TabStop = false
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(44, 63, 86);
        b.FlatAppearance.MouseOverBackColor = danger ? Color.FromArgb(138, 35, 45) : Color.FromArgb(32, 48, 70);
        b.FlatAppearance.MouseDownBackColor = danger ? Color.FromArgb(108, 26, 36) : Color.FromArgb(21, 34, 52);
        b.Click += (_, _) => action();
        return b;
    }



    private Size GetInitialClientSize()
    {
        // v1.4 UI baseline: default to the validated 1646x905 design canvas,
        // but restore a user's last normal size only after clamping it to the
        // currently connected primary working area. Invalid/offscreen saved
        // placement is handled separately and falls back to centered.
        int designWidth = Dpi(DefaultClientWidth);
        int designHeight = Dpi(DefaultClientHeight);
        Size safeMin = GetSafeMinimumClientSize();

        int width = _settings.WindowWidth > 0 ? _settings.WindowWidth : designWidth;
        int height = _settings.WindowHeight > 0 ? _settings.WindowHeight : designHeight;
        width = Math.Max(safeMin.Width, width);
        height = Math.Max(safeMin.Height, height);

        try
        {
            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
            if (!work.IsEmpty)
            {
                int margin = Dpi(80);
                width = Math.Min(width, Math.Max(safeMin.Width, work.Width - margin));
                height = Math.Min(height, Math.Max(safeMin.Height, work.Height - margin));
            }
        }
        catch { }

        return new Size(width, height);
    }

    private static bool IsShiftKeyDownForWindowReset()
    {
        try { return (Control.ModifierKeys & Keys.Shift) == Keys.Shift; }
        catch { return false; }
    }

    private void ApplyInitialWindowPlacement()
    {
        try
        {
            Rectangle wanted = new Rectangle(
                _settings.WindowX,
                _settings.WindowY,
                Math.Max(Width, Dpi(MinClientWidth)),
                Math.Max(Height, Dpi(MinClientHeight)));

            if (!_resetWindowStateOnStartup
                && _settings.WindowX != int.MinValue
                && _settings.WindowY != int.MinValue
                && TryGetSafeSavedWindowPlacement(wanted, out Rectangle safeBounds))
            {
                Location = safeBounds.Location;
                return;
            }

            ResetSavedWindowPlacement(inMemoryOnly: true);
            _startupWindowPlacementWasReset = true;
            CenterOnPrimaryWorkingArea();
        }
        catch
        {
            ResetSavedWindowPlacement(inMemoryOnly: true);
            _startupWindowPlacementWasReset = true;
            CenterToScreen();
        }
    }

    private bool TryGetSafeSavedWindowPlacement(Rectangle bounds, out Rectangle safeBounds)
    {
        safeBounds = Rectangle.Empty;
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        if (Math.Abs(bounds.X) > 100000 || Math.Abs(bounds.Y) > 100000) return false;

        Rectangle? bestWork = null;
        Rectangle bestVisible = Rectangle.Empty;
        foreach (var screen in Screen.AllScreens)
        {
            Rectangle work = screen.WorkingArea;
            if (work.IsEmpty) continue;
            Rectangle visible = Rectangle.Intersect(bounds, work);
            if (visible.Width * (long)visible.Height > bestVisible.Width * (long)bestVisible.Height)
            {
                bestVisible = visible;
                bestWork = work;
            }
        }

        Rectangle workArea = bestWork ?? Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        if (workArea.IsEmpty) return false;

        Rectangle titleBand = new(bounds.Left, bounds.Top, bounds.Width, Math.Min(Dpi(80), bounds.Height));
        Rectangle visibleTitle = Rectangle.Intersect(titleBand, workArea);
        bool titleReachable = visibleTitle.Width >= Dpi(180) && visibleTitle.Height >= Dpi(24);

        if (bestVisible.Width < Dpi(420) || bestVisible.Height < Dpi(320)) return false;

        double visibleArea = bestVisible.Width * (double)bestVisible.Height;
        double wantedArea = Math.Max(1.0, bounds.Width * (double)bounds.Height);
        if (visibleArea / wantedArea < 0.70) return false;
        if (!titleReachable) return false;

        safeBounds = ClampWindowBoundsToWorkingArea(bounds, workArea);
        return true;
    }

    private bool IsUsableSavedWindowBounds(Rectangle bounds)
        => TryGetSafeSavedWindowPlacement(bounds, out _);

    private Rectangle ClampWindowBoundsToWorkingArea(Rectangle bounds, Rectangle work)
    {
        int width = Math.Min(bounds.Width, Math.Max(Dpi(640), work.Width));
        int height = Math.Min(bounds.Height, Math.Max(Dpi(420), work.Height));
        int x = Clamp(bounds.X, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Clamp(bounds.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max < min) return min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private void ResetSavedWindowPlacement(bool inMemoryOnly)
    {
        _settings.WindowMaximized = false;
        _settings.WindowWidth = Dpi(DefaultClientWidth);
        _settings.WindowHeight = Dpi(DefaultClientHeight);
        _settings.WindowX = int.MinValue;
        _settings.WindowY = int.MinValue;
        if (!inMemoryOnly)
        {
            try { _settings.Save(); } catch { }
        }
    }

    private void CenterOnPrimaryWorkingArea()
    {
        Rectangle work;
        try { work = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty; }
        catch { work = Rectangle.Empty; }

        if (work.IsEmpty)
        {
            CenterToScreen();
            return;
        }

        CenterOnWorkingArea(work);
    }

    private void CenterOnWorkingArea(Rectangle work)
    {
        if (work.IsEmpty)
        {
            CenterToScreen();
            return;
        }

        int x = work.Left + Math.Max(0, (work.Width - Width) / 2);
        int y = work.Top + Math.Max(0, (work.Height - Height) / 2);
        Location = new Point(x, y);
    }

    private static Icon? LoadAppIcon(int desiredSize = 0)
    {
        try
        {
            using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream("CDTOB.ico");
            if (stream is not null)
            {
                using var icon = desiredSize > 0 ? new Icon(stream, desiredSize, desiredSize) : new Icon(stream);
                return (Icon)icon.Clone();
            }
        }
        catch { }

        try
        {
            string localIcon = Path.Combine(AppContext.BaseDirectory, "Resources", "CDTOB.ico");
            if (File.Exists(localIcon))
            {
                using var icon = desiredSize > 0 ? new Icon(localIcon, desiredSize, desiredSize) : new Icon(localIcon);
                return (Icon)icon.Clone();
            }
        }
        catch { }

        return null;
    }

    private void BuildUi()
    {
        var dashboardHost = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            // Keep the dashboard on the dark app surface. Do not use the native
            // scroll viewport for normal/default/maximized sizes; the dashboard
            // is fit-scaled to the available client area instead.
            AutoScroll = false,
            BackColor = BackColor,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        _dashboardHost = dashboardHost;
        Controls.Add(dashboardHost);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            Width = Dpi(DefaultClientWidth),
            Height = Dpi(DefaultClientHeight),
            Padding = new Padding(Dpi(14), Dpi(12), Dpi(14), Dpi(12)),
            BackColor = BackColor,
            RowCount = 2,
            ColumnCount = 1
        };
        _dashboardCanvas = shell;
        _dashboardCanvasBaseDpiBasis = CurrentDashboardDpiBasis();
        _dashboardCanvasScale = 1.0F;
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        dashboardHost.Controls.Add(shell);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 6,
            BackColor = BackColor,
            Margin = new Padding(0, 0, 0, Dpi(10)),
            Padding = new Padding(Dpi(2), 0, Dpi(2), 0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
            Margin = new Padding(0)
        };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var iconBox = new PictureBox
        {
            Width = Dpi(62),
            Height = Dpi(62),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, Dpi(12), 0),
            BackColor = Color.Transparent
        };
        try
        {
            using var icon = LoadAppIcon(Dpi(64));
            if (icon is not null) iconBox.Image = icon.ToBitmap();
        }
        catch { }
        brand.Controls.Add(iconBox, 0, 0);

        var titleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new BrandTitleLabel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = GuiFonts.UiFont(18.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(244, 214, 151),
            Margin = new Padding(0, Dpi(15), 0, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _keys[title] = "title";
        titleStack.Controls.Add(title, 0, 0);

        brand.Controls.Add(titleStack, 1, 0);
        header.Controls.Add(brand, 0, 0);
        if (UseCustomChrome)
        {
            HookWindowDrag(header);
            HookWindowDrag(brand);
            HookWindowDrag(iconBox);
            HookWindowDrag(titleStack);
            HookWindowDrag(title);
        }

        // UI Scale is placed left of Performance Mode because it affects the whole app layout.
        var uiScaleHeader = CreateHeaderUiScaleControl();
        header.Controls.Add(uiScaleHeader, 1, 0);

        var perfModeHeader = CreateHeaderPerformanceModeControl();
        header.Controls.Add(perfModeHeader, 2, 0);

        _languageDrop = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Dpi(154),
            Height = Dpi(34),
            FlatStyle = FlatStyle.Flat,
            BackColor = VersionButtonColor,
            ForeColor = Color.White,
            Font = GuiFonts.UiFont(9F, FontStyle.Bold),
            Margin = new Padding(Dpi(10), Dpi(18), 0, 0),
            IntegralHeight = false,
            UseWaitCursor = false
        };
        _languageDrop.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingLanguageDrop) return;
            if (_languageDrop.SelectedItem is LanguageChoice choice && _lang != choice.Code)
            {
                _lang = choice.Code;
                ApplyLanguage();
                SaveSettings();
            }
        };
        header.Controls.Add(_languageDrop, 3, 0);

        var ver = new Label
        {
            Text = "v" + OverlayService.AppVersion,
            AutoSize = true,
            Padding = new Padding(Dpi(12), Dpi(8), Dpi(12), Dpi(8)),
            BackColor = VersionButtonColor,
            ForeColor = Color.FromArgb(172, 202, 240),
            Font = GuiFonts.UiFont(8.5F, FontStyle.Bold),
            Margin = new Padding(Dpi(10), Dpi(18), 0, 0)
        };
        header.Controls.Add(ver, 4, 0);

        if (UseCustomChrome)
        {
            var windowControls = CreateWindowControls();
            header.Controls.Add(windowControls, 5, 0);
        }
        shell.Controls.Add(header, 0, 0);

        var scroller = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            AutoScrollMinSize = Size.Empty,
            BackColor = BackColor,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        _mainScroller = scroller;
        shell.Controls.Add(scroller, 0, 1);

        // Fixed-design dashboard area. The validated 1646x905 design is built
        // once, then the whole dashboard surface is fit-scaled uniformly by the
        // resize handler. Individual panels should not independently reflow.
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            Width = Dpi(DesignBodyWidth),
            Height = Dpi(DesignBodyHeight),
            ColumnCount = 3,
            RowCount = 1,
            BackColor = BackColor,
            Padding = new Padding(0),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        _bodyPanel = body;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scroller.Controls.Add(body);
        ResetBodyDesignCanvasSize();

        var leftStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = BackColor,
            Margin = new Padding(0, 0, Dpi(10), 0),
            Padding = new Padding(0)
        };
        leftStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        leftStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leftStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(leftStack, 0, 0);

        var gameCard = Card();
        gameCard.Margin = new Padding(0, 0, 0, Dpi(10));
        leftStack.Controls.Add(gameCard, 0, 0);
        gameCard.Controls.Add(Section("game_folders", 1));
        AddPathPicker(gameCard, "game", _game, () => BrowseFolder(_game), () => AutoDetect(false));
        AddPathPicker(gameCard, "textures", _textures, () => BrowseFolder(_textures));
        // Keep the internal mod/registry name fixed for compatibility, but do
        // not expose it as a GUI field.  This avoids wasting UI space while
        // preserving legacy Overlay Builder build/remove behavior.
        _modName.Text = OverlayService.DefaultModName;

        var textureCard = Card();
        textureCard.Margin = new Padding(0, 0, 0, Dpi(10));
        textureCard.Dock = DockStyle.Fill;
        textureCard.AutoSize = false;
        _leftCard = textureCard;
        textureCard.Resize += (_, _) => RefreshResponsiveSpacers();
        leftStack.Controls.Add(textureCard, 0, 1);
        textureCard.Controls.Add(Section("source", 2));
        textureCard.Controls.Add(NoteKey("filter_note"));
        AddPresetSelector(textureCard);
        _leftResponsiveSpacer = ResponsiveSpacer();
        textureCard.Controls.Add(_leftResponsiveSpacer);


        var center = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = BackColor,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, Dpi(10), 0),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(center, 1, 0);

        var opts = Card();
        opts.Margin = new Padding(0, 0, 0, Dpi(10));
        center.Controls.Add(opts, 0, 0);
        opts.Controls.Add(Section("options", 3));
        opts.Controls.Add(NoteKey("advanced_options_note"));
        AddCheck(opts, _apply, "apply");
        AddCheck(opts, _unique, "unique");
        AddCheck(opts, _dry, "dry");
        AddCheck(opts, _backup, "backup");
        AddCheck(opts, _scan, "scan");
        AddCheck(opts, _multiTarget, "multi_target");

        var ctrl = Card();
        ctrl.Dock = DockStyle.Fill;
        ctrl.AutoSize = false;
        ctrl.AutoSizeMode = AutoSizeMode.GrowOnly;
        ctrl.Margin = new Padding(0, 0, 0, Dpi(10));
        ctrl.MinimumSize = new Size(0, Dpi(380));
        _controlCard = ctrl;
        ctrl.Resize += (_, _) => RefreshResponsiveSpacers();
        center.Controls.Add(ctrl, 0, 1);
        ctrl.Controls.Add(Section("control", 4));
        _progress.Dock = DockStyle.Top;
        _progress.Margin = new Padding(Dpi(12), Dpi(4), Dpi(12), Dpi(8));
        _progress.Height = Dpi(24);
        _progress.Maximum = 100;
        _progress.Value = 0;
        _progress.ProgressText = L.Progress("READY", _lang);
        ctrl.Controls.Add(_progress);

        AddDebugModePanel(ctrl);

        // One click public friendly path for loose HD texture folders.
        // In the resizable UI this belongs with the action/progress controls,
        // not buried under the Texture Section picker.
        _easyApplyBtn = Btn("easy_apply", StartEasyApply, primary: true);
        _easyApplyBtn.Height = Dpi(44);
        _easyApplyBtn.Margin = new Padding(Dpi(12), Dpi(4), Dpi(12), Dpi(4));
        ctrl.Controls.Add(_easyApplyBtn);

        _updateBuildBtn = Btn("update_existing", StartUpdateExistingBuild, warning: true);
        _updateBuildBtn.Height = Dpi(42);
        ctrl.Controls.Add(_updateBuildBtn);

        _runBtn = Btn("build", StartBuild, ghost: true);
        _runBtn.Height = Dpi(40);
        ctrl.Controls.Add(_runBtn);

        _cancelBtn = Btn("cancel", RequestCancelBuild, danger: true);
        _cancelBtn.Height = Dpi(40);
        _cancelBtn.Enabled = true;
        _cancelBtn.Visible = false;
        _cancelBtn.ForeColor = Color.White;
        ctrl.Controls.Add(_cancelBtn);

        // Full width rows so names never clip to "Smart" / "Release".
        ctrl.Controls.Add(Btn("hold", SmartHold, ghost: true));
        ctrl.Controls.Add(Btn("release", ReleaseHold, ghost: true));

        ctrl.Controls.Add(Btn("manage", ManageInstalledBuilds, ghost: true));

        _relinkBtn = Btn("relink_after_update", StartRelinkAfterGameUpdate, ghost: true);
        _relinkBtn.Height = Dpi(40);
        ctrl.Controls.Add(_relinkBtn);

        ctrl.Controls.Add(Btn("remove", RemoveBuild, danger: true));
        _controlResponsiveSpacer = ResponsiveSpacer();
        ctrl.Controls.Add(_controlResponsiveSpacer);

        var right = LogCard();
        // Match the Texture Section and Control / Progress card bottom inset so
        // the three major panel bottoms share the same visual baseline.
        right.Margin = new Padding(0, 0, 0, Dpi(10));
        body.Controls.Add(right, 2, 0);

        InitInlineTooltip();
        InitPortCredit();
        RefreshWrappingNotes();
        RefreshResponsiveSpacers();

        dashboardHost.Resize += (_, _) => QueueDashboardCanvasScale();
    }


    private void QueueDashboardCanvasScale()
    {
        if (_dashboardCanvasScaleQueued || IsDisposed || !IsHandleCreated) return;
        _dashboardCanvasScaleQueued = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _dashboardCanvasScaleQueued = false;
                ApplyDashboardCanvasScale();
            }));
        }
        catch
        {
            _dashboardCanvasScaleQueued = false;
        }
    }


    private float CurrentDashboardDpiBasis()
    {
        int dpi = 96;
        try { dpi = DeviceDpi; } catch { }
        return Math.Max(0.25F, (dpi / 96.0F) * Math.Max(0.25F, _uiScaleFactor));
    }

    private void ApplyDashboardCanvasScale()
    {
        if (_dashboardHost is null || _dashboardCanvas is null
            || _dashboardHost.IsDisposed || _dashboardCanvas.IsDisposed
            || _applyingDashboardCanvasScale) return;

        int baseWidth = Math.Max(1, Dpi(DefaultClientWidth));
        int baseHeight = Math.Max(1, Dpi(DefaultClientHeight));
        int viewportWidth = Math.Max(1, _dashboardHost.ClientSize.Width);
        int viewportHeight = Math.Max(1, _dashboardHost.ClientSize.Height);

        // Fit the entire validated 1646x905 design into the available client
        // area. This keeps the dashboard from becoming a fixed 1x island in
        // maximized windows while also preventing right/bottom clipping.
        float fitScale = Math.Min(viewportWidth / (float)baseWidth, viewportHeight / (float)baseHeight);
        if (float.IsNaN(fitScale) || float.IsInfinity(fitScale) || fitScale <= 0F) fitScale = 1F;
        float targetScale = Math.Max(0.45F, Math.Min(MaximumCanvasScale, fitScale));

        _applyingDashboardCanvasScale = true;
        bool redrawSuspended = false;
        try
        {
            _dashboardHost.SuspendLayout();
            _dashboardCanvas.SuspendLayout();
            try
            {
                if (IsHandleCreated)
                {
                    SendMessage(Handle, WM_SETREDRAW, 0, 0);
                    redrawSuspended = true;
                }
            }
            catch { redrawSuspended = false; }

            float currentBasis = CurrentDashboardDpiBasis();
            float baseBasis = _dashboardCanvasBaseDpiBasis <= 0F ? currentBasis : _dashboardCanvasBaseDpiBasis;
            float desiredEffectiveScale = (currentBasis / baseBasis) * targetScale;
            if (float.IsNaN(desiredEffectiveScale) || float.IsInfinity(desiredEffectiveScale) || desiredEffectiveScale <= 0F)
                desiredEffectiveScale = targetScale;

            float ratio = _dashboardCanvasScale <= 0F ? desiredEffectiveScale : desiredEffectiveScale / _dashboardCanvasScale;
            if (Math.Abs(ratio - 1F) > 0.001F)
            {
                // Scale the dashboard from a single tracked effective scale. This
                // includes per-monitor DPI changes so mixed-DPI maximize/restore
                // does not compound or clip controls after repeated transitions.
                _dashboardCanvas.Scale(new SizeF(ratio, ratio));
                _dashboardCanvasScale = desiredEffectiveScale;
            }

            // Correct any pixel drift from repeated resize events so the root
            // dashboard bounds stay tied to the original 1646x905 design size.
            var wantedSize = new Size(
                Math.Max(1, (int)Math.Round(baseWidth * targetScale)),
                Math.Max(1, (int)Math.Round(baseHeight * targetScale)));
            if (_dashboardCanvas.Size != wantedSize)
                _dashboardCanvas.Size = wantedSize;

            PositionDashboardCanvas();
            HideRootScrollbars();
        }
        finally
        {
            try { _dashboardCanvas?.ResumeLayout(true); } catch { }
            try { _dashboardHost?.ResumeLayout(true); } catch { }
            if (redrawSuspended)
            {
                try { SendMessage(Handle, WM_SETREDRAW, 1, 0); } catch { }
            }
            _applyingDashboardCanvasScale = false;
            try
            {
                _dashboardCanvas?.Invalidate(true);
                _dashboardHost?.Invalidate(true);
                Invalidate(true);
            }
            catch { }
        }
    }

    private void HideRootScrollbars()
    {
        // The dashboard now scales to fit the available client area. The root
        // containers should stay dark and clean; native white scrollbars are
        // only visual noise in supported window sizes and were appearing from
        // stale AutoScroll state/min-size calculations.
        void CleanPanel(ScrollableControl? panel)
        {
            if (panel is null || panel.IsDisposed) return;
            try { panel.AutoScroll = false; } catch { }
            try { panel.AutoScrollMinSize = Size.Empty; } catch { }
            try
            {
                if (panel.IsHandleCreated) ShowScrollBar(panel.Handle, SB_BOTH, false);
            }
            catch { }
        }

        CleanPanel(_dashboardHost);
        CleanPanel(_mainScroller);
    }

    private void PositionDashboardCanvas()
    {
        if (_dashboardHost is null || _dashboardCanvas is null
            || _dashboardHost.IsDisposed || _dashboardCanvas.IsDisposed) return;

        // The host is intentionally not AutoScroll at normal sizes. Place the
        // fit-scaled dashboard on dark padding, centered only after it has been
        // scaled as large as it can safely fit.
        if (_dashboardHost.AutoScroll)
            _dashboardHost.AutoScroll = false;
        if (_dashboardHost.AutoScrollMinSize != Size.Empty)
            _dashboardHost.AutoScrollMinSize = Size.Empty;
        if (_mainScroller is not null && !_mainScroller.IsDisposed)
        {
            _mainScroller.AutoScroll = false;
            _mainScroller.AutoScrollMinSize = Size.Empty;
        }

        int x = Math.Max(0, (_dashboardHost.ClientSize.Width - _dashboardCanvas.Width) / 2);
        int y = Math.Max(0, (_dashboardHost.ClientSize.Height - _dashboardCanvas.Height) / 2);

        var wantedLocation = new Point(x, y);
        if (_dashboardCanvas.Location != wantedLocation)
            _dashboardCanvas.Location = wantedLocation;

        HideRootScrollbars();
    }


    private void ResetBodyDesignCanvasSize()
    {
        if (_mainScroller is null || _bodyPanel is null || _mainScroller.IsDisposed || _bodyPanel.IsDisposed) return;

        int targetWidth = Math.Max(Dpi(MinBodyWidth), Dpi(DesignBodyWidth));
        int targetHeight = Math.Max(Dpi(MinBodyHeight), Dpi(DesignBodyHeight));

        _bodyPanel.SuspendLayout();
        if (_bodyPanel.Width != targetWidth) _bodyPanel.Width = targetWidth;
        if (_bodyPanel.Height != targetHeight) _bodyPanel.Height = targetHeight;
        _bodyPanel.Location = new Point(0, 0);
        _bodyPanel.ResumeLayout(true);

        // Do not give the inner dashboard body a native scroll viewport. The
        // outer dashboard already fit-scales to the available dark client area,
        // and the form minimum size prevents normal clipping. Leaving an
        // AutoScrollMinSize here caused white native scrollbars to appear even
        // when the scaled dashboard fit correctly.
        _mainScroller.AutoScroll = false;
        _mainScroller.AutoScrollMinSize = Size.Empty;
        HideRootScrollbars();
        RefreshWrappingNotes();
        RefreshResponsiveSpacers();
    }


    private sealed class DebugFailureChoice
    {
        public string Code { get; }
        public string Label { get; }
        public DebugFailureChoice(string code, string label) { Code = code; Label = label; }
        public override string ToString() => Label;
    }

    private void AddDebugModePanel(TableLayoutPanel parent)
    {
        if (!Program.DebugModeEnabled) return;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.FromArgb(30, 38, 23),
            Padding = new Padding(Dpi(8)),
            Margin = new Padding(Dpi(12), Dpi(4), Dpi(12), Dpi(6))
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "QA DEBUGMODE - simulated failure",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = Color.FromArgb(255, 218, 126),
            Font = GuiFonts.UiFont(8.4F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, Dpi(4))
        };
        panel.Controls.Add(label, 0, 0);

        _debugFailureDrop = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = InputBackColor,
            ForeColor = Color.White,
            Font = GuiFonts.UiFont(8.2F, FontStyle.Bold),
            IntegralHeight = false,
            MaxDropDownItems = 12,
            Height = Dpi(28)
        };
        foreach (var point in DebugFailureInjector.FailurePoints)
            _debugFailureDrop.Items.Add(new DebugFailureChoice(point.Code, point.Label));
        _debugFailureDrop.SelectedIndex = 0;
        panel.Controls.Add(_debugFailureDrop, 0, 1);

        parent.Controls.Add(panel);
    }

    private string SelectedDebugFailurePointCode()
    {
        if (!Program.DebugModeEnabled || _debugFailureDrop is null) return DebugFailureInjector.None;
        return _debugFailureDrop.SelectedItem is DebugFailureChoice choice ? choice.Code : DebugFailureInjector.None;
    }


    private void InitPortCredit()
    {
        _portCredit.Text = ".NET Port by Burstahh";
        _portCredit.AutoSize = true;
        _portCredit.BackColor = BackColor;
        _portCredit.ForeColor = Color.FromArgb(82, 96, 114);
        _portCredit.Font = GuiFonts.UiFont(7.5F, FontStyle.Italic);
        _portCredit.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_portCredit);
        PositionPortCredit();
        _portCredit.BringToFront();
        Resize += (_, _) => PositionPortCredit();
    }

    private void PositionPortCredit()
    {
        _portCredit.Location = new Point(
            Math.Max(Dpi(8), ClientSize.Width - _portCredit.Width - Dpi(12)),
            Math.Max(Dpi(8), ClientSize.Height - _portCredit.Height - Dpi(6)));
    }

    private TableLayoutPanel Card()
    {
        var card = new ModernCardPanel
        {
            ColumnCount = 1,
            RowCount = 0,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = CardBackColor,
            FillColor = CardBackColor,
            BorderColor = CardBorderColor,
            CornerRadius = Dpi(4),
            Padding = new Padding(Dpi(10)),
            Margin = new Padding(0, 0, 0, Dpi(10))
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return card;
    }

    private TableLayoutPanel LogCard()
    {
        var card = new ModernCardPanel
        {
            Dock = DockStyle.Fill,
            MinimumSize = new Size(Dpi(390), Dpi(440)),
            BackColor = CardBackColor,
            FillColor = CardBackColor,
            BorderColor = CardBorderColor,
            CornerRadius = Dpi(4),
            Padding = new Padding(Dpi(10)),
            Margin = new Padding(0, 0, 0, Dpi(10)),
            RowCount = 3,
            ColumnCount = 1
        };
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var logHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Dpi(40),
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, Dpi(4)),
            Padding = new Padding(0)
        };
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logHeader.Controls.Add(Section("log", 5), 0, 0);
        _statusPill = new RoundedLabel
        {
            Text = L.Progress("READY", _lang),
            AutoSize = true,
            BackColor = Color.FromArgb(24, 83, 48),
            ForeColor = Color.FromArgb(181, 246, 197),
            Font = GuiFonts.UiFont(8F, FontStyle.Bold),
            Padding = new Padding(Dpi(9), Dpi(5), Dpi(9), Dpi(5)),
            Margin = new Padding(0, Dpi(8), Dpi(6), 0),
            TextAlign = ContentAlignment.MiddleCenter,
            CornerRadius = Dpi(4),
            BorderColor = Color.FromArgb(42, 111, 68)
        };
        logHeader.Controls.Add(_statusPill, 1, 0);
        card.Controls.Add(logHeader, 0, 0);

        _log.Dock = DockStyle.Fill;
        _log.MinimumSize = new Size(Dpi(320), Dpi(300));
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = InputBackColor;
        _log.ForeColor = Color.FromArgb(225, 235, 248);
        _log.Font = new Font("Consolas", Math.Max(8F, 9.2F * Math.Max(0.75F, Math.Min(2.25F, _uiScaleFactor))), FontStyle.Regular);
        _log.Margin = new Padding(Dpi(8), 0, Dpi(8), Dpi(10));
        card.Controls.Add(_log, 0, 1);
        var logButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            BackColor = Color.Transparent,
            Margin = new Padding(Dpi(6), 0, Dpi(6), Dpi(2)),
            Padding = new Padding(0)
        };
        var logButtonList = new List<Button>
        {
            SmallLogButton("open_last_report", OpenLastReport),
            SmallLogButton("open_runtime_log", () => OpenFileInExplorer(_runtimeLogPath)),
            SmallLogButton("copy_log", CopyLogToClipboard),
            SmallLogButton("clear", () => _log.Clear())
        };
        ConfigureLogButtonLayout(logButtons, logButtonList);
        logButtons.Resize += (_, _) => ConfigureLogButtonLayout(logButtons, logButtonList);
        card.Controls.Add(logButtons, 0, 2);
        return card;
    }

    private void ConfigureLogButtonLayout(TableLayoutPanel panel, IReadOnlyList<Button> buttons)
    {
        bool singleRow = panel.ClientSize.Width <= 0 || panel.ClientSize.Width >= Dpi(440);
        int rows = singleRow ? 1 : 2;
        int cols = singleRow ? 4 : 2;
        int targetHeight = Dpi(singleRow ? 34 : 68);

        panel.SuspendLayout();
        panel.Controls.Clear();
        panel.ColumnStyles.Clear();
        panel.RowStyles.Clear();
        panel.ColumnCount = cols;
        panel.RowCount = rows;
        panel.Height = targetHeight;
        for (int c = 0; c < cols; c++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / cols));
        for (int r = 0; r < rows; r++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(32)));

        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(Dpi(3), 0, Dpi(3), Dpi(4));
            panel.Controls.Add(b, singleRow ? i : i % 2, singleRow ? 0 : i / 2);
        }
        panel.ResumeLayout();
    }

    private Button SmallLogButton(string key, Action action)
    {
        var b = Btn(key, action, ghost: true);
        b.Height = Dpi(28);
        b.MinimumSize = new Size(Dpi(84), Dpi(26));
        b.Margin = new Padding(Dpi(3), 0, Dpi(3), Dpi(4));
        b.Font = GuiFonts.UiFont(8.1F, FontStyle.Bold);
        return b;
    }

    private void OpenLastReport()
    {
        if (!string.IsNullOrWhiteSpace(_lastReportPath) && File.Exists(_lastReportPath))
        {
            OpenFileInExplorer(_lastReportPath);
            return;
        }
        ShowStyledMessage("dialog_notice_title", L.T("no_last_report", _lang), L.T("dialog_notice_title", _lang), warning: true);
    }

    private void CopyLogToClipboard()
    {
        try { Clipboard.SetText(_log.Text ?? string.Empty); }
        catch (Exception ex) { ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true); }
    }

    private Control Section(string key, int step = 0)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Dpi(34),
            ColumnCount = step > 0 ? 2 : 1,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(Dpi(10), Dpi(6), Dpi(10), Dpi(4)),
            Padding = new Padding(0)
        };

        if (step > 0)
        {
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(36)));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var badge = new Label
            {
                Text = step.ToString(),
                AutoSize = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(32, 71, 134),
                ForeColor = Color.White,
                Font = GuiFonts.UiFont(8.8F, FontStyle.Bold),
                Margin = new Padding(0, Dpi(5), Dpi(8), Dpi(5)),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.None
            };
            header.Controls.Add(badge, 0, 0);
        }
        else
        {
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }

        var title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = GuiFonts.UiFont(11F, FontStyle.Bold),
            ForeColor = Color.White,
            Margin = new Padding(0),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _keys[title] = key;
        _sectionTitleLabels[title] = key;
        header.Controls.Add(title, step > 0 ? 1 : 0, 0);
        return header;
    }

    private Label NoteKey(string key)
    {
        var l = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = MutedTextColor,
            BackColor = Color.Transparent,
            Font = GuiFonts.UiFont(8.4F, FontStyle.Italic),
            Margin = new Padding(Dpi(12), 0, Dpi(12), Dpi(7)),
            MaximumSize = new Size(Dpi(260), 0),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _keys[l] = key;
        return l;
    }

    private Panel ResponsiveSpacer()
    {
        return new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 0,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
    }

    private void RefreshWrappingNotes()
    {
        foreach (var note in _wrappingNotes.ToArray())
        {
            if (!note.IsDisposed) RefreshWrappingNote(note);
        }
    }

    private void RefreshWrappingNote(Label note)
    {
        if (note.IsDisposed) return;
        int width = note.ClientSize.Width;
        if (width <= Dpi(40))
        {
            int parentWidth = note.Parent?.ClientSize.Width ?? Dpi(380);
            width = Math.Max(Dpi(120), parentWidth - note.Margin.Horizontal);
        }

        int textWidth = Math.Max(Dpi(80), width - note.Padding.Horizontal - Dpi(2));
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.HorizontalCenter;
        Size measured = TextRenderer.MeasureText(note.Text.Replace("\r\n", "\n"), note.Font, new Size(textWidth, int.MaxValue), flags);
        int desired = Math.Max(Dpi(24), measured.Height + note.Padding.Vertical + Dpi(5));
        if (Math.Abs(note.Height - desired) > 1) note.Height = desired;
    }

    private void RefreshResponsiveSpacers()
    {
        RefreshResponsiveSpacer(_leftCard, _leftResponsiveSpacer, 0.08F, 36);
        RefreshResponsiveSpacer(_controlCard, _controlResponsiveSpacer, 0.04F, 18);
    }

    private void RefreshResponsiveSpacer(TableLayoutPanel? parent, Panel? spacer, float fraction, int maxLogicalHeight)
    {
        if (parent is null || spacer is null || parent.IsDisposed || spacer.IsDisposed) return;
        if (parent.ClientSize.Height <= Dpi(260))
        {
            if (spacer.Height != 0) spacer.Height = 0;
            return;
        }

        int old = spacer.Height;
        try
        {
            spacer.Height = 0;
            int pref = parent.GetPreferredSize(new Size(Math.Max(Dpi(200), parent.ClientSize.Width), 0)).Height;
            int extra = Math.Max(0, parent.ClientSize.Height - pref - Dpi(12));
            int target = Math.Min(Dpi(maxLogicalHeight), (int)Math.Round(extra * fraction));
            if (Math.Abs(old - target) <= 1) target = old;
            spacer.Height = target;
        }
        catch
        {
            spacer.Height = old;
        }
    }

    private Label LabelKey(string key)
    {
        bool important = key == "game" || key == "textures" || key == "source";
        var l = new Label
        {
            AutoSize = key != "source",
            Dock = key == "source" ? DockStyle.Top : DockStyle.None,
            Height = key == "source" ? Dpi(26) : 0,
            ForeColor = important ? Color.FromArgb(236, 242, 250) : MutedTextColor,
            Font = important ? GuiFonts.UiFont(9F, FontStyle.Bold) : GuiFonts.UiFont(9F, FontStyle.Regular),
            Margin = new Padding(Dpi(12), Dpi(key == "source" ? 14 : 12), Dpi(12), Dpi(3)),
            TextAlign = key == "source" ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft
        };
        _keys[l] = key;
        return l;
    }

    private void AddPathPicker(Control parent, string labelKey, TextBox box, Action browse, Action? extraAction = null)
    {
        // Header row keeps the folder/detect automatically buttons beside the label
        // instead of stealing width from the path textbox.
        var header = new TableLayoutPanel
        {
            ColumnCount = extraAction != null ? 3 : 2,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Dpi(34),
            BackColor = Color.Transparent,
            Margin = new Padding(Dpi(12), Dpi(8), Dpi(12), 0),
            Padding = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (extraAction != null) header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(178)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(46)));

        var label = LabelKey(labelKey);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(0);
        header.Controls.Add(label, 0, 0);

        if (extraAction != null)
        {
            var ex = Btn("auto_detect", extraAction, ghost: true);
            ex.Dock = DockStyle.Fill;
            ex.Margin = new Padding(0, 0, Dpi(6), 0);
            ex.MinimumSize = new Size(Dpi(164), Dpi(32));
            ex.Height = Dpi(32);
            header.Controls.Add(ex, 1, 0);
        }

        var b = Btn("browse_icon", browse, primary: true);
        b.Dock = DockStyle.Fill;
        b.Margin = new Padding(0);
        b.MinimumSize = new Size(Dpi(38), Dpi(30));
        b.Height = Dpi(30);
        b.Font = new Font("Segoe UI Emoji", 11F, FontStyle.Bold);
        header.Controls.Add(b, extraAction != null ? 2 : 1, 0);
        parent.Controls.Add(header);

        StyleText(box);
        box.Dock = DockStyle.Top;
        box.Margin = new Padding(Dpi(12), Dpi(3), Dpi(12), Dpi(8));
        box.MinimumSize = new Size(Dpi(80), Dpi(28));
        parent.Controls.Add(box);
    }


    private static bool IsAdvancedFilterPresetId(string? id)
        => !string.IsNullOrWhiteSpace(id)
            && (id.StartsWith("pamt_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, "object_sublayer", StringComparison.OrdinalIgnoreCase));

    private void AddPresetSelector(Control parent)
    {
        var frame = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = CardBackColorAlt,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            BorderStyle = BorderStyle.None,
            Margin = new Padding(Dpi(8), 0, Dpi(8), Dpi(4)),
            Padding = new Padding(Dpi(9), Dpi(8), Dpi(9), Dpi(8))
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var mainPresets = OverlayService.Presets.Where(p => !IsAdvancedFilterPresetId(p.Id)).ToList();
        var rawPresets = OverlayService.Presets.Where(p => IsAdvancedFilterPresetId(p.Id)).ToList();

        CheckBox MakePresetCheck(FilterPresetDef preset)
        {
            var rb = new CheckBox
            {
                AutoSize = false,
                Height = Dpi(25),
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(230, 238, 248),
                BackColor = Color.Transparent,
                Font = GuiFonts.UiFont(8.65F, FontStyle.Regular),
                Margin = new Padding(0, 0, Dpi(4), Dpi(1)),
                Tag = preset.Id,
                Checked = preset.Id == _presetId,
                AutoEllipsis = true
            };
            rb.CheckedChanged += (_, _) =>
            {
                if (_refreshingPreset || _presetChanging) return;
                if (!rb.Checked)
                {
                    if (_presetId == (string)(rb.Tag ?? preset.Id))
                    {
                        _presetChanging = true;
                        rb.Checked = true;
                        _presetChanging = false;
                    }
                    return;
                }
                _presetChanging = true;
                _presetId = (string)(rb.Tag ?? preset.Id);
                if (IsAdvancedFilterPresetId(_presetId))
                    _showAdvancedFilters = true;
                foreach (var other in _presetButtons.Values)
                {
                    if (!ReferenceEquals(other, rb)) other.Checked = false;
                }
                _presetChanging = false;
                UpdateAdvancedPresetVisibility();
                SaveSettings();
            };
            _presetButtons[preset.Id] = rb;
            return rb;
        }

        int mainRows = Math.Max(1, (int)Math.Ceiling(mainPresets.Count / 2.0));
        var mainPanel = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = mainRows,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int r = 0; r < mainRows; r++) mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(26)));

        for (int i = 0; i < mainPresets.Count; i++)
        {
            int col = i < mainRows ? 0 : 1;
            int row = i < mainRows ? i : i - mainRows;
            mainPanel.Controls.Add(MakePresetCheck(mainPresets[i]), col, row);
        }
        frame.Controls.Add(mainPanel, 0, 0);

        _advancedPresetToggle = Btn("show_advanced_filters", ToggleAdvancedFilters, ghost: true);
        _advancedPresetToggle.Height = Dpi(32);
        _advancedPresetToggle.MinimumSize = new Size(Dpi(180), Dpi(30));
        _advancedPresetToggle.Margin = new Padding(0, Dpi(5), 0, Dpi(3));
        _advancedPresetToggle.Font = GuiFonts.UiFont(8.6F, FontStyle.Bold);
        HookInlineTip(_advancedPresetToggle);
        frame.Controls.Add(_advancedPresetToggle, 0, 1);

        _advancedPresetPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0, Dpi(3), 0, 0)
        };

        int rawRows = Math.Max(1, (int)Math.Ceiling(rawPresets.Count / 2.0));
        var rawPanel = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = rawRows,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        rawPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        rawPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int r = 0; r < rawRows; r++) rawPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(26)));

        for (int i = 0; i < rawPresets.Count; i++)
        {
            int col = i < rawRows ? 0 : 1;
            int row = i < rawRows ? i : i - rawRows;
            rawPanel.Controls.Add(MakePresetCheck(rawPresets[i]), col, row);
        }
        _advancedPresetPanel.Controls.Add(rawPanel);
        frame.Controls.Add(_advancedPresetPanel, 0, 2);

        UpdateAdvancedPresetVisibility();
        parent.Controls.Add(frame);
    }

    private void ToggleAdvancedFilters()
    {
        if (IsAdvancedFilterPresetId(_presetId) && _showAdvancedFilters)
        {
            ShowStyledMessage("dialog_notice_title", L.T("advanced_pamt_filter_selected", _lang),
                L.T("dialog_notice_title", _lang), warning: true);
            return;
        }
        _showAdvancedFilters = !_showAdvancedFilters;
        UpdateAdvancedPresetVisibility();
    }

    private void UpdateAdvancedPresetVisibility()
    {
        if (IsAdvancedFilterPresetId(_presetId))
            _showAdvancedFilters = true;

        if (_advancedPresetPanel is not null)
            _advancedPresetPanel.Visible = _showAdvancedFilters;

        if (_advancedPresetToggle is not null)
        {
            string key = _showAdvancedFilters ? "hide_advanced_filters" : "show_advanced_filters";
            _keys[_advancedPresetToggle] = key;
            _advancedPresetToggle.Text = ComposeControlText(_advancedPresetToggle, key);
        }
    }

    private void AddCheck(Control parent, CheckBox cb, string key)
    {
        cb.AutoSize = false;
        cb.Dock = DockStyle.Top;
        cb.Height = Dpi(28);
        cb.AutoEllipsis = true;
        cb.ForeColor = key == "multi_target" ? Color.FromArgb(255, 207, 128) : Color.FromArgb(230, 238, 248);
        cb.BackColor = Color.Transparent;
        cb.Font = GuiFonts.UiFont(9.0F, FontStyle.Regular);
        cb.Margin = new Padding(Dpi(12), Dpi(1), Dpi(12), Dpi(1));
        _keys[cb] = key;
        parent.Controls.Add(cb);
        HookInlineTip(cb);
    }

    private sealed class MemoryModeChoice
    {
        public string Code { get; init; } = "Auto";
        public string Text { get; init; } = "Auto / Recommended";
        public override string ToString() => Text;
    }

    private Control CreateHeaderPerformanceModeControl()
    {
        _memoryMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _memoryMode.Width = Dpi(178);
        _memoryMode.Height = Dpi(30);
        _memoryMode.FlatStyle = FlatStyle.Flat;
        _memoryMode.BackColor = VersionButtonColor;
        _memoryMode.ForeColor = Color.White;
        _memoryMode.Font = GuiFonts.UiFont(9.0F, FontStyle.Bold);
        _memoryMode.Margin = new Padding(Dpi(10), Dpi(18), 0, 0);
        _memoryMode.IntegralHeight = false;
        _memoryMode.DrawMode = DrawMode.OwnerDrawFixed;
        _memoryMode.DrawItem += (_, e) => DrawMemoryModeItem(e);
        _memoryMode.DropDown += (_, _) => HideInlineTip();
        _memoryMode.DropDownClosed += (_, _) => HideInlineTip();
        _memoryMode.Leave += (_, _) => HideInlineTip();
        _memoryMode.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingMemoryMode) return;
            _settings.PerformanceMemoryMode = SelectedMemoryModeCode();
            HideInlineTip();
            SaveSettings();
        };
        HookInlineTip(_memoryMode);

        return _memoryMode;
    }

    private void InitMemoryModeDefaults()
    {
        // Restore the saved performance mode into the visible dropdown, matching
        // UI Scale behavior.  Earlier builds kept Auto as a placeholder, which
        // made a saved value such as PerformanceMemoryMode=Full appear as the
        // generic "Performance Mode" text after relaunch.
        RefreshMemoryModeItems(_settings.PerformanceMemoryMode);
        if (_customWorkers.Minimum <= 0) _customWorkers.Minimum = 1;
        if (_customWorkers.Maximum < _customWorkers.Minimum) _customWorkers.Maximum = Math.Max(2, Math.Min(64, Environment.ProcessorCount * 2));
        _customWorkers.Value = Math.Max(_customWorkers.Minimum, Math.Min(_customWorkers.Maximum, _settings.CustomPrepareWorkers <= 0 ? 8 : _settings.CustomPrepareWorkers));
    }

    private void RefreshMemoryModeItems(string? desiredCode = null)
    {
        if (_memoryMode is null) return;
        string selected = NormalizeMemoryModeCode(desiredCode ?? SelectedMemoryModeCodeOrSaved());
        _refreshingMemoryMode = true;
        try
        {
            _memoryMode.Items.Clear();
            _memoryMode.Items.Add(new MemoryModeChoice { Code = "Auto", Text = L.T("memory_mode_auto", _lang) });
            _memoryMode.Items.Add(new MemoryModeChoice { Code = "Full", Text = L.T("memory_mode_full", _lang) });
            _memoryMode.Items.Add(new MemoryModeChoice { Code = "Medium", Text = L.T("memory_mode_medium", _lang) });
            _memoryMode.Items.Add(new MemoryModeChoice { Code = "Low", Text = L.T("memory_mode_low", _lang) });
            SelectMemoryMode(selected);
        }
        finally { _refreshingMemoryMode = false; }
    }

    private string SelectedMemoryModeCodeOrSaved()
    {
        if (_memoryMode.SelectedItem is MemoryModeChoice c) return c.Code;
        return NormalizeMemoryModeCode(_settings.PerformanceMemoryMode);
    }

    private void SelectMemoryMode(string? code)
    {
        code = NormalizeMemoryModeCode(code);
        for (int i = 0; i < _memoryMode.Items.Count; i++)
        {
            if (_memoryMode.Items[i] is MemoryModeChoice c && string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                _memoryMode.SelectedIndex = i;
                    _memoryMode.Invalidate();
                return;
            }
        }
        _memoryMode.SelectedIndex = 0;
        _memoryMode.Invalidate();
    }

    private string SelectedMemoryModeCode()
    {
        if (_memoryMode.SelectedItem is MemoryModeChoice c) return c.Code;
        return "Auto";
    }

    private void DrawMemoryModeItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            e.DrawBackground();
            using var brush = new SolidBrush(Color.White);
            TextRenderer.DrawText(e.Graphics, L.T("memory_mode", _lang), _memoryMode.Font,
                e.Bounds, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            return;
        }

        e.DrawBackground();
        string text = _memoryMode.Items[e.Index]?.ToString() ?? string.Empty;
        Color color = ((e.State & DrawItemState.Selected) == DrawItemState.Selected) ? SystemColors.HighlightText : _memoryMode.ForeColor;
        TextRenderer.DrawText(e.Graphics, text, _memoryMode.Font, e.Bounds, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private static string NormalizeMemoryModeCode(string? code)
    {
        string v = (code ?? "Auto").Trim();
        if (v.Equals("Full", StringComparison.OrdinalIgnoreCase) || v.Contains("Full", StringComparison.OrdinalIgnoreCase) || v.Contains("Max", StringComparison.OrdinalIgnoreCase)) return "Full";
        if (v.Equals("Medium", StringComparison.OrdinalIgnoreCase) || v.Contains("Medium", StringComparison.OrdinalIgnoreCase) || v.Contains("Balanced", StringComparison.OrdinalIgnoreCase)) return "Medium";
        if (v.Equals("Low", StringComparison.OrdinalIgnoreCase) || v.Contains("Low", StringComparison.OrdinalIgnoreCase) || v.Contains("Safe", StringComparison.OrdinalIgnoreCase)) return "Low";
        return "Auto";
    }


    private sealed class UiScaleChoice
    {
        public string Code { get; init; } = "Auto";
        public string Text { get; init; } = "Auto / Recommended";
        public override string ToString() => Text;
    }

    private Control CreateHeaderUiScaleControl()
    {
        _uiScaleDrop.DropDownStyle = ComboBoxStyle.DropDownList;
        _uiScaleDrop.Width = Dpi(132);
        _uiScaleDrop.Height = Dpi(30);
        _uiScaleDrop.FlatStyle = FlatStyle.Flat;
        _uiScaleDrop.BackColor = VersionButtonColor;
        _uiScaleDrop.ForeColor = Color.White;
        _uiScaleDrop.Font = GuiFonts.UiFont(9.0F, FontStyle.Bold);
        _uiScaleDrop.Margin = new Padding(Dpi(10), Dpi(18), 0, 0);
        _uiScaleDrop.IntegralHeight = false;
        _uiScaleDrop.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingUiScaleDrop) return;
            if (_uiScaleDrop.SelectedItem is UiScaleChoice choice)
            {
                string newCode = NormalizeUiScaleCode(choice.Code);
                if (!string.Equals(_uiScaleCode, newCode, StringComparison.OrdinalIgnoreCase))
                {
                    _uiScaleCode = newCode;
                    SaveSettings();
                    Log($"UI scale saved: {UiScaleDisplayName(_uiScaleCode)}. Restart the app to apply it. If the selected scale cannot fit this screen, it will be capped to a safe size.");
                }
            }
            HideInlineTip();
        };
        _uiScaleDrop.DropDown += (_, _) => HideInlineTip();
        _uiScaleDrop.DropDownClosed += (_, _) => HideInlineTip();
        _uiScaleDrop.Leave += (_, _) => HideInlineTip();
        HookInlineTip(_uiScaleDrop);
        RefreshUiScaleItems();
        return _uiScaleDrop;
    }

    private void RefreshUiScaleItems()
    {
        if (_uiScaleDrop is null) return;
        string selected = NormalizeUiScaleCode(_uiScaleCode);
        _refreshingUiScaleDrop = true;
        try
        {
            _uiScaleDrop.Items.Clear();
            _uiScaleDrop.Items.Add(new UiScaleChoice { Code = "Auto", Text = L.T("ui_scale_auto", _lang) });
            _uiScaleDrop.Items.Add(new UiScaleChoice { Code = "100", Text = "UI 100%" });
            _uiScaleDrop.Items.Add(new UiScaleChoice { Code = "125", Text = "UI 125%" });
            _uiScaleDrop.Items.Add(new UiScaleChoice { Code = "150", Text = "UI 150%" });
            _uiScaleDrop.Items.Add(new UiScaleChoice { Code = "175", Text = "UI 175%" });
            _uiScaleDrop.Items.Add(new UiScaleChoice { Code = "200", Text = "UI 200%" });
            SelectUiScale(selected);
        }
        finally { _refreshingUiScaleDrop = false; }
    }

    private void SelectUiScale(string? code)
    {
        code = NormalizeUiScaleCode(code);
        for (int i = 0; i < _uiScaleDrop.Items.Count; i++)
        {
            if (_uiScaleDrop.Items[i] is UiScaleChoice c && string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                _uiScaleDrop.SelectedIndex = i;
                return;
            }
        }
        _uiScaleDrop.SelectedIndex = 0;
    }

    private static string NormalizeUiScaleCode(string? code)
    {
        string v = (code ?? "Auto").Trim();
        if (v.Equals("100", StringComparison.OrdinalIgnoreCase) || v.Contains("100", StringComparison.OrdinalIgnoreCase)) return "100";
        if (v.Equals("125", StringComparison.OrdinalIgnoreCase) || v.Contains("125", StringComparison.OrdinalIgnoreCase)) return "125";
        if (v.Equals("150", StringComparison.OrdinalIgnoreCase) || v.Contains("150", StringComparison.OrdinalIgnoreCase)) return "150";
        if (v.Equals("175", StringComparison.OrdinalIgnoreCase) || v.Contains("175", StringComparison.OrdinalIgnoreCase)) return "175";
        if (v.Equals("200", StringComparison.OrdinalIgnoreCase) || v.Contains("200", StringComparison.OrdinalIgnoreCase)) return "200";
        return "Auto";
    }

    private float ResolveUiScaleFactor(string? code)
    {
        return NormalizeUiScaleCode(code) switch
        {
            "100" => 1.00F,
            "125" => 1.25F,
            "150" => 1.50F,
            "175" => 1.75F,
            "200" => 2.00F,
            _ => ComputeAutoUiScaleFactor()
        };
    }

    private float ClampUiScaleForCurrentDisplay(float requestedScale, string? code)
    {
        // UI scale participates in the base design size and is capped so the
        // 1646x905 dashboard can fit the current working area. The live resize
        // path then fit-scales the whole dashboard uniformly.
        float scale = Math.Max(0.70F, Math.Min(2.0F, requestedScale));
        try
        {
            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
            if (!work.IsEmpty)
            {
                int dpi = 96;
                try { dpi = DeviceDpi; } catch { }
                float dpiScale = Math.Max(0.70F, dpi / 96.0F);
                float margin = 80F;
                float maxTotalScale = Math.Min(
                    Math.Max(0.50F, (work.Width - margin) / DefaultClientWidth),
                    Math.Max(0.50F, (work.Height - margin) / DefaultClientHeight));
                float maxUiScale = Math.Max(0.70F, Math.Min(2.0F, maxTotalScale / dpiScale));
                scale = Math.Min(scale, maxUiScale);
            }
        }
        catch { }
        return scale;
    }

    private float ComputeAutoUiScaleFactor()
    {
        int dpi = 96;
        try { dpi = DeviceDpi; } catch { }
        if (dpi > 104) return 1.00F;

        try
        {
            Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
            int maxSide = Math.Max(bounds.Width, bounds.Height);
            int minSide = Math.Min(bounds.Width, bounds.Height);
            if (maxSide >= 5120 || (maxSide >= 3840 && minSide >= 2160)) return 1.25F;
            if (maxSide >= 3440 && minSide >= 1440) return 1.15F;
        }
        catch { }

        return 1.00F;
    }

    private string UiScaleDisplayName(string? code)
    {
        return NormalizeUiScaleCode(code) switch
        {
            "100" => "100%",
            "125" => "125%",
            "150" => "150%",
            "175" => "175%",
            "200" => "200%",
            _ => $"Auto / {(int)Math.Round(_uiScaleFactor * 100)}%"
        };
    }

    private string UiScaleLogDescription()
    {
        string mode = UiScaleDisplayName(_uiScaleCode);
        int dpi = 96;
        try { dpi = DeviceDpi; } catch { }
        string display = string.Empty;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
            if (!bounds.IsEmpty) display = $"; primary display {bounds.Width}x{bounds.Height}";
        }
        catch { }
        string effective = $"effective {(int)Math.Round(_uiScaleFactor * 100)}%";
        return $"{mode} app scale, {effective} (Windows DPI {dpi}){display}";
    }

    private Button Btn(string key, Action click, bool primary = false, bool ghost = false, bool danger = false, bool warning = false)
    {
        var b = new Button
        {
            Height = Dpi(36),
            AutoSize = false,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(Dpi(12), Dpi(3), Dpi(12), Dpi(3)),
            ForeColor = Color.White,
            MinimumSize = new Size(Dpi(180), Dpi(30)),
            AutoEllipsis = true,
            UseMnemonic = false,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        b.Font = key == "browse_icon"
            ? new Font("Segoe UI Emoji", 11F * Math.Max(0.75F, Math.Min(2.25F, _uiScaleFactor)), FontStyle.Bold)
            : GuiFonts.UiFont(9.05F, FontStyle.Bold);
        b.FlatAppearance.BorderSize = ghost ? 1 : 0;
        b.FlatAppearance.MouseOverBackColor = Lighten(ActionBackColor(key, primary, ghost, danger, warning), 18);
        b.FlatAppearance.MouseDownBackColor = Darken(ActionBackColor(key, primary, ghost, danger, warning), 14);
        b.BackColor = ActionBackColor(key, primary, ghost, danger, warning);
        b.FlatAppearance.BorderColor = ghost ? Color.FromArgb(52, 72, 98) : Color.FromArgb(70, 95, 126);
        b.Click += (_, _) => click();
        _keys[b] = key;
        return b;
    }

    private static Color ActionBackColor(string key, bool primary, bool ghost, bool danger, bool warning)
    {
        if (key == "update_existing") return UpdateButtonColor;
        if (key == "relink_after_update") return MaintenanceButtonColor;
        if (primary) return PrimaryButtonColor;
        if (danger) return DangerButtonColor;
        if (warning) return UpdateButtonColor;
        if (ghost) return Color.FromArgb(31, 43, 59);
        return VersionButtonColor;
    }

    private static Color Lighten(Color color, int amount)
        => Color.FromArgb(color.A, Math.Min(255, color.R + amount), Math.Min(255, color.G + amount), Math.Min(255, color.B + amount));

    private static Color Darken(Color color, int amount)
        => Color.FromArgb(color.A, Math.Max(0, color.R - amount), Math.Max(0, color.G - amount), Math.Max(0, color.B - amount));

    private void StyleText(TextBox box)
    {
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = InputBackColor;
        box.ForeColor = Color.White;
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0);
        box.MinimumSize = new Size(Dpi(120), Dpi(28));
    }

    private void ApplyLanguage()
    {
        foreach (var (ctrl, key) in _keys) ctrl.Text = ComposeControlText(ctrl, key);
        UpdateLanguageDropdown();
        if (_statusPill is not null && !_statusPill.IsDisposed)
        {
            _statusPill.Text = _busy ? L.Progress("WORKING", _lang) : L.Progress("READY", _lang);
        }
        _progress.ProgressText = L.Progress(_progress.ProgressText, _lang);
        _progress.Invalidate();
        RefreshPresetItems();
        RefreshMemoryModeItems();
        RefreshUiScaleItems();
        UpdateAdvancedPresetVisibility();
        ForceButtonTextWhite(this);
        ApplyLocalizedButtonMetrics();
        RefreshWrappingNotes();
        RefreshResponsiveSpacers();
        RefreshTooltips();
        QueueDashboardCanvasScale();
    }

    private void ApplyLocalizedButtonMetrics()
    {
        foreach (var (ctrl, key) in _keys.ToArray())
        {
            if (ctrl is not Button b || !string.Equals(key, "remove", StringComparison.OrdinalIgnoreCase))
                continue;

            // The Deutsch/Dutch danger action sits at the bottom of the fixed
            // dashboard and can visually clip at the lower edge. Keep this as
            // a tiny localized metrics adjustment only; do not touch the
            // validated Experiment 15/17 dashboard scaling model.
            bool compact = UsesCompactBottomDangerButtonLayout();
            int height = DashboardScaledDpi(compact ? 32 : 36);
            int minHeight = DashboardScaledDpi(compact ? 26 : 30);
            var margin = new Padding(
                DashboardScaledDpi(12),
                DashboardScaledDpi(compact ? 2 : 3),
                DashboardScaledDpi(12),
                DashboardScaledDpi(compact ? 1 : 3));

            if (b.Height != height) b.Height = height;
            var minimumSize = new Size(DashboardScaledDpi(180), minHeight);
            if (b.MinimumSize != minimumSize) b.MinimumSize = minimumSize;
            if (b.Margin != margin) b.Margin = margin;
        }
    }

    private bool UsesCompactBottomDangerButtonLayout()
        => string.Equals(_lang, "de", StringComparison.OrdinalIgnoreCase)
           || string.Equals(_lang, "nl", StringComparison.OrdinalIgnoreCase);

    private int DashboardScaledDpi(int logicalPixels)
    {
        float scale = _dashboardCanvasScale <= 0F ? 1F : _dashboardCanvasScale;
        return Math.Max(1, (int)Math.Round(Dpi(logicalPixels) * scale));
    }

    private string ComposeControlText(Control ctrl, string key)
    {
        string text = L.T(key, _lang);
        if (ctrl is Button) text = DecorateButtonText(key, text);
        return text;
    }

    private static string DecorateButtonText(string key, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (key == "browse_icon") return text;
        if (text.Length > 0 && !char.IsLetterOrDigit(text[0])) return text;

        string icon = key switch
        {
            "easy_apply" => "▶",
            "update_existing" => "↻",
            "build" => "⚙",
            "cancel" => "✕",
            "hold" => "◈",
            "release" => "↩",
            "manage" => "▣",
            "relink_after_update" => "⛓",
            "remove" => "✖",
            "open_last_report" => "📁",
            "open_runtime_log" => "📄",
            "copy_log" => "⧉",
            "clear" => "⌫",
            "auto_detect" => "⌖",
            _ => string.Empty
        };
        return string.IsNullOrEmpty(icon) ? text : icon + "  " + text;
    }

    private static void ForceButtonTextWhite(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button b) b.ForeColor = Color.White;
            if (child.HasChildren) ForceButtonTextWhite(child);
        }
    }

    private void UpdateLanguageDropdown()
    {
        if (_languageDrop is null) return;
        _refreshingLanguageDrop = true;
        try
        {
            if (_languageDrop.Items.Count == 0)
            {
                foreach (var lang in L.Languages)
                    _languageDrop.Items.Add(new LanguageChoice(lang.Code, lang.DisplayName));
            }

            for (int i = 0; i < _languageDrop.Items.Count; i++)
            {
                if (_languageDrop.Items[i] is LanguageChoice choice && choice.Code == _lang)
                {
                    _languageDrop.SelectedIndex = i;
                    return;
                }
            }
            _languageDrop.SelectedIndex = 0;
        }
        finally { _refreshingLanguageDrop = false; }
    }

    private void InitInlineTooltip()
    {
        if (_inlineTipPanel is not null) return;
        _inlineTipPanel = new Panel
        {
            Visible = false,
            BackColor = Color.FromArgb(23, 35, 50),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(Dpi(8)),
            AutoSize = false
        };
        _inlineTipLabel = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(232, 239, 248),
            Font = GuiFonts.UiFont(8.6F, FontStyle.Regular),
            MaximumSize = new Size(Dpi(390), 0)
        };
        _inlineTipPanel.Controls.Add(_inlineTipLabel);
        Controls.Add(_inlineTipPanel);
        _inlineTipPanel.BringToFront();
    }

    private void HookInlineTip(Control c)
    {
        c.MouseEnter += (_, _) => ShowInlineTip(c);
        c.MouseMove += (_, _) => PositionInlineTip(c);
        c.MouseLeave += (_, _) => HideInlineTip();
        c.Leave += (_, _) => HideInlineTip();
        c.LostFocus += (_, _) => HideInlineTip();
        c.Disposed += (_, _) => _tipTexts.Remove(c);
    }

    private void SetInlineTip(Control c, string text)
    {
        _tipTexts[c] = text;
    }

    private void ShowInlineTip(Control c)
    {
        if (_inlineTipPanel is null || _inlineTipLabel is null) return;
        if (!_tipTexts.TryGetValue(c, out var text) || string.IsNullOrWhiteSpace(text)) return;

        int maxWidth = Math.Max(Dpi(240), Math.Min(Dpi(390), ClientSize.Width - Dpi(36)));
        _inlineTipLabel.MaximumSize = new Size(maxWidth, 0);
        _inlineTipLabel.Text = text;
        var pref = _inlineTipLabel.GetPreferredSize(new Size(maxWidth, 0));
        _inlineTipLabel.Location = new Point(Dpi(8), Dpi(6));
        _inlineTipPanel.Size = new Size(pref.Width + Dpi(18), pref.Height + Dpi(14));
        PositionInlineTip(c);
        _inlineTipPanel.Visible = true;
        _inlineTipPanel.BringToFront();
    }

    private void PositionInlineTip(Control c)
    {
        if (_inlineTipPanel is null || !_inlineTipPanel.Visible && !_tipTexts.ContainsKey(c)) return;

        var below = PointToClient(c.PointToScreen(new Point(0, c.Height + 5)));
        int x = below.X;
        int y = below.Y;

        int maxX = Math.Max(8, ClientSize.Width - _inlineTipPanel.Width - 8);
        int maxY = Math.Max(8, ClientSize.Height - _inlineTipPanel.Height - 8);
        x = Math.Max(8, Math.Min(x, maxX));
        if (y > maxY)
        {
            var above = PointToClient(c.PointToScreen(new Point(0, -_inlineTipPanel.Height - 5)));
            y = above.Y;
        }
        y = Math.Max(8, Math.Min(y, maxY));
        _inlineTipPanel.Location = new Point(x, y);
    }

    private void HideInlineTip()
    {
        if (_inlineTipPanel is not null) _inlineTipPanel.Visible = false;
    }

    private void RefreshTooltips()
    {
        SetInlineTip(_apply, L.T("tip_apply", _lang));
        SetInlineTip(_unique, L.T("tip_unique", _lang));
        SetInlineTip(_dry, L.T("tip_dry", _lang));
        SetInlineTip(_backup, L.T("tip_backup", _lang));
        SetInlineTip(_scan, L.T("tip_scan", _lang));
        SetInlineTip(_multiTarget, L.T("tip_multi_target", _lang));
        SetInlineTip(_memoryMode, L.T("tip_memory_mode", _lang));
        SetInlineTip(_uiScaleDrop, L.T("tip_ui_scale", _lang));
        SetInlineTip(_easyApplyBtn, L.T("tip_easy_apply", _lang));
        SetInlineTip(_updateBuildBtn, L.T("tip_update_existing_button", _lang));
        SetInlineTip(_relinkBtn, L.T("tip_relink_after_update", _lang));
        if (_advancedPresetToggle is not null)
        {
            SetInlineTip(_advancedPresetToggle, L.T("tip_advanced_filters", _lang));
        }
    }

    private void RefreshPresetItems()
    {
        _refreshingPreset = true;
        try
        {
            foreach (var p in OverlayService.Presets)
            {
                if (_presetButtons.TryGetValue(p.Id, out var rb))
                {
                    rb.Text = p.Label(_lang);
                    rb.Checked = p.Id == _presetId;
                }
            }
        }
        finally { _refreshingPreset = false; }
    }

    private void ToggleLanguage()
    {
        _lang = _lang == "es" ? "en" : "es";
        ApplyLanguage();
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.Language = _lang;
        _settings.GameFolder = _game.Text;
        _settings.TextureFolder = _textures.Text;
        _settings.FilterPreset = _presetId;
        _settings.PerformanceMemoryMode = SelectedMemoryModeCode();
        _settings.UiScaleMode = NormalizeUiScaleCode(_uiScaleCode);
        _settings.CustomPrepareWorkers = (int)_customWorkers.Value;
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal) RememberNormalWindowBounds();
        Rectangle currentBounds = WindowState == FormWindowState.Maximized && _lastSafeNormalWindowBounds.Width > 0
            ? _lastSafeNormalWindowBounds
            : WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds;
        if (currentBounds.Width > 0 && currentBounds.Height > 0)
        {
            if (TryGetSafeSavedWindowPlacement(currentBounds, out _))
            {
                _settings.WindowWidth = Math.Max(MinClientWidth, currentBounds.Width);
                _settings.WindowHeight = Math.Max(MinClientHeight, currentBounds.Height);
                _settings.WindowX = currentBounds.X;
                _settings.WindowY = currentBounds.Y;
            }
            else
            {
                ResetSavedWindowPlacement(inMemoryOnly: true);
            }
        }
        _settings.Save();
    }

    private void BrowseFolder(TextBox target)
    {
        using var f = new FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text)) f.SelectedPath = target.Text;
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = f.SelectedPath;
            SaveSettings();
        }
    }

    private void LogRuntimeOnly(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        string line = $"{DateTime.Now:HH:mm:ss}  {L.Runtime(msg, _lang)}";
        try { File.AppendAllText(_runtimeLogPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    private void Log(string msg)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(msg))); return; }

        // Service-layer diagnostics can opt into runtime-only logging by using
        // this prefix. Keep the public Activity Log focused on user-actionable
        // status while preserving detailed QA data in builder_runtime.log.
        const string runtimeOnlyPrefix = "[runtime]";
        if (!string.IsNullOrWhiteSpace(msg) && msg.StartsWith(runtimeOnlyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            LogRuntimeOnly(msg[runtimeOnlyPrefix.Length..].TrimStart());
            return;
        }

        string line = $"{DateTime.Now:HH:mm:ss}  {L.Runtime(msg, _lang)}";
        try { File.AppendAllText(_runtimeLogPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
        AppendStyledLogLine(line);
    }

    private void AppendStyledLogLine(string line)
    {
        Color color = LogLineColor(line);
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    private static Color LogLineColor(string line)
    {
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(255, 126, 126);
        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase) || line.Contains("AMBIG", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(255, 206, 119);
        if (line.Contains("READY", StringComparison.OrdinalIgnoreCase) || line.Contains("FINISHED", StringComparison.OrdinalIgnoreCase) || line.Contains("complete", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(129, 230, 154);
        if (line.Contains("START", StringComparison.OrdinalIgnoreCase) || line.Contains("Building", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(151, 197, 255);
        return Color.FromArgb(225, 235, 248);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        // Cancel replaces the normal Build button while work is running.
        // This keeps the center button stack the same height and avoids the
        // bottom Restore button getting clipped on smaller/fixed layouts.
        _runBtn.Visible = !busy;
        _runBtn.Enabled = !busy;
        _easyApplyBtn.Enabled = !busy;
        _updateBuildBtn.Enabled = !busy;
        _relinkBtn.Enabled = !busy;
        _cancelBtn.Visible = busy;
        _cancelBtn.Enabled = true;
        _cancelBtn.ForeColor = Color.White;

        if (_statusPill is not null && !_statusPill.IsDisposed)
        {
            _statusPill.Text = L.Progress(busy ? "WORKING" : "READY", _lang);
            _statusPill.BackColor = busy ? Color.FromArgb(95, 67, 23) : Color.FromArgb(24, 83, 48);
            if (_statusPill is RoundedLabel statusRounded) statusRounded.BorderColor = busy ? Color.FromArgb(130, 94, 34) : Color.FromArgb(42, 111, 68);
            _statusPill.ForeColor = busy ? Color.FromArgb(255, 222, 145) : Color.FromArgb(181, 246, 197);
        }

        if (busy)
        {
            _progress.Activity = true;
            _activityTimer.Start();
            SetProgress(0, "Working");
        }
        else
        {
            _activityTimer.Stop();
            _progress.Activity = false;
            _progress.Invalidate();
            _currentBuildCancel?.Dispose();
            _currentBuildCancel = null;
        }
    }

    private void RequestCancelBuild()
    {
        if (!_busy || _currentBuildCancel is null || _currentBuildCancel.IsCancellationRequested) return;
        _currentBuildCancel.Cancel();
        _cancelBtn.Enabled = true;
        _cancelBtn.ForeColor = Color.White;
        SetProgress(Math.Max(1, _progress.Value), "CANCELLING");
        Log(L.IsSpanish(_lang) ? "Cancelación solicitada. Esperando el próximo punto seguro..." : "Cancel requested. Waiting for the current safe checkpoint...");
    }

    private void SetProgress(int value, string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(value, text))); return; }
        _progress.Value = Math.Max(0, Math.Min(100, value));
        _progress.ProgressText = L.Progress(text, _lang);
        _progress.Invalidate();
    }

    private enum BuildRunMode { Manual, EasyApply, UpdateExisting }

    private BuildOptions CurrentOptions(BuildRunMode runMode = BuildRunMode.Manual)
    {
        bool easyApply = runMode == BuildRunMode.EasyApply;
        bool updateExisting = runMode == BuildRunMode.UpdateExisting;
        var p = (easyApply || updateExisting) ? OverlayService.GetPreset("all") : OverlayService.GetPreset(_presetId);
        string gameDir = _game.Text.Trim();
        // Keep all generated tool data under one game side folder:
        // Crimson Desert\HDOverlayBuilder.  Reports/build manifests go
        // under HDOverlayBuilder\builds, while registry/backups remain
        // in the same root.  This avoids creating both old CDTextureOverlayBuilder
        // and CDTextureOverlayBuilds beside the game.
        string outDir = string.IsNullOrWhiteSpace(gameDir)
            ? Path.Combine(AppContext.BaseDirectory, "HDOverlayBuilder", "builds")
            : OverlayService.BuildOutputRoot(gameDir);
        string modName = string.IsNullOrWhiteSpace(_modName.Text) ? OverlayService.DefaultModName : _modName.Text.Trim();

        bool recommendedWorkflow = easyApply || updateExisting;
        bool applyToGame = recommendedWorkflow ? true : _apply.Checked;
        bool allowUniqueFilename = recommendedWorkflow ? true : _unique.Checked;
        bool dryRun = recommendedWorkflow ? false : _dry.Checked;
        bool scanConflicts = recommendedWorkflow ? true : _scan.Checked;
        bool looseDuplicatesToAllTargets = recommendedWorkflow ? false : _multiTarget.Checked;

        return new BuildOptions(
            gameDir,
            _textures.Text.Trim(),
            outDir,
            modName,
            applyToGame,
            allowUniqueFilename,
            dryRun,
            40.00,
            _backup.Checked,
            scanConflicts,
            looseDuplicatesToAllTargets,
            updateExisting,
            p.Pamt,
            p.Prefix,
            _lang,
            SelectedMemoryModeCode(),
            (int)_customWorkers.Value);
    }

    private void StartBuild() => StartBuildCore(BuildRunMode.Manual);

    private void StartEasyApply() => StartBuildCore(BuildRunMode.EasyApply);

    private void StartUpdateExistingBuild() => StartBuildCore(BuildRunMode.UpdateExisting);

    private static bool IsNoActiveManagedBuildRelinkException(InvalidOperationException ex)
    {
        return ex.Message.Contains("No active managed", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("overlay build registry", StringComparison.OrdinalIgnoreCase);
    }

    private void StartRelinkAfterGameUpdate()
    {
        if (_busy) { ShowStyledMessage("dialog_notice_title", L.T("busy", _lang), L.T("dialog_notice_title", _lang), warning: true); return; }
        string gameDir = _game.Text.Trim();
        if (string.IsNullOrWhiteSpace(gameDir) || !OverlayService.IsGameDir(gameDir))
        {
            ShowStyledMessage("dialog_notice_title", L.T("select_valid_game", _lang), L.T("dialog_notice_title", _lang), warning: true);
            return;
        }
        if (!ConfirmRelinkAfterGameUpdate()) return;
        SaveSettings();
        _currentBuildCancel = new CancellationTokenSource();
        var cancellationToken = _currentBuildCancel.Token;
        SetBusy(true);
        DebugFailureInjector.BeginOperation("Relink Overlays After Game Update", SelectedDebugFailurePointCode(), Log);
        Task.Run(() =>
        {
            try
            {
                var result = OverlayService.RelinkOverlaysAfterGameUpdate(gameDir, Log, SetProgress, cancellationToken);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Finished");
                    ShowBuildComplete(result);
                }));
            }
            catch (OperationCanceledException)
            {
                Log(L.IsSpanish(_lang) ? "Revinculación cancelada por el usuario." : "Relink cancelled by user.");
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Cancelled");
                    ShowStyledMessage("dialog_notice_title", L.T("cancelled_message", _lang), L.T("dialog_notice_title", _lang), warning: true);
                }));
            }
            catch (InvalidOperationException ex) when (IsNoActiveManagedBuildRelinkException(ex))
            {
                LogRuntimeOnly("Relink skipped: no active managed HD overlay build registry was found. " + ex);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(0, "Ready");
                    string msg = L.T("relink_no_managed_build", _lang);
                    Log(msg);
                    ShowStyledMessage("dialog_warning_title", msg, L.T("dialog_warning_title", _lang), warning: true);
                }));
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Error");
                    ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true);
                }));
            }
            finally { DebugFailureInjector.EndOperation(); BeginInvoke(new Action(() => SetBusy(false))); }
        });
    }

    private bool ConfirmRelinkAfterGameUpdate()
    {
        return ConfirmStyled(
            L.T("relink_confirm_title", _lang),
            L.T("relink_confirm_header", _lang),
            L.T("relink_confirm", _lang),
            acceptKey: "accept",
            warning: true);
    }

    private void CheckIncompleteStartupArtifacts()
    {
        try
        {
            string gameDir = _game.Text.Trim();
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return;
            var leftovers = OverlayService.FindIncompleteBuildArtifacts(gameDir);
            if (leftovers.Count == 0) return;
            string msg = string.Format(L.T("incomplete_cleanup_confirm", _lang), leftovers.Count);
            if (ConfirmStyled(L.T("incomplete_cleanup_title", _lang), L.T("incomplete_cleanup_header", _lang), msg, acceptKey: "accept", warning: true))
            {
                int cleaned = OverlayService.CleanupIncompleteBuildArtifacts(gameDir, Log);
                Log($"Startup cleanup finished. Incomplete artifact(s) deleted: {cleaned}.");
            }
        }
        catch (Exception ex) { Log($"WARN: startup cleanup check failed: {ex.Message}"); }
    }

    private bool ConfirmSafeTextureSourceRootIfNeeded(BuildRunMode runMode, BuildOptions options)
    {
        bool easyApply = runMode == BuildRunMode.EasyApply;
        bool updateExisting = runMode == BuildRunMode.UpdateExisting;
        if (!easyApply && !updateExisting) return true;

        var guard = OverlayService.AnalyzeTextureSourceRootSelection(options.TextureDir);
        if (!guard.Warn) return true;

        string children = string.IsNullOrWhiteSpace(guard.ChildFolderSummary)
            ? L.T("multi_source_unknown_children", _lang)
            : guard.ChildFolderSummary;
        string message = string.Format(L.T("multi_source_parent_warning", _lang), options.TextureDir, children);

        // Update Existing Build is state-sensitive, so require an explicit user
        // confirmation before allowing a parent/container folder that appears to
        // combine multiple separate texture sources. Easy Apply uses the same
        // guard as a warning because it is a full rebuild path.
        bool ok = ConfirmDangerOverride(
            "multi_source_parent_title",
            updateExisting ? L.T("multi_source_parent_header_update", _lang) : L.T("multi_source_parent_header_easy", _lang),
            message,
            dangerKey: "continue_anyway");
        if (ok)
        {
            Log($"WARN: selected DDS root appears to contain multiple separate texture source folders. Continuing after user confirmation: {options.TextureDir}");
            Log($"[runtime] Multi-source parent/container guard confirmed. DDS-bearing child folders: {children}");
        }
        return ok;
    }


    private void StartBuildCore(BuildRunMode runMode)
    {
        bool easyApply = runMode == BuildRunMode.EasyApply;
        bool updateExisting = runMode == BuildRunMode.UpdateExisting;
        if (_busy) { ShowStyledMessage("dialog_notice_title", L.T("busy", _lang), L.T("dialog_notice_title", _lang), warning: true); return; }
        if (easyApply && !ConfirmEasyApply()) return;
        SaveSettings();
        BuildOptions options;
        try { options = CurrentOptions(runMode); }
        catch (Exception ex) { ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true); return; }
        if (string.IsNullOrWhiteSpace(options.TextureDir) || !Directory.Exists(options.TextureDir))
        {
            string msg = L.T(runMode == BuildRunMode.Manual ? "missing_dds_folder_manual" : "missing_dds_folder", _lang);
            Log(msg);
            LogRuntimeOnly($"DDS texture folder validation failed before build start. Path: {options.TextureDir}");
            ShowStyledMessage("dialog_warning_title", msg, L.T("dialog_warning_title", _lang), warning: true);
            return;
        }
        if (!ConfirmSafeTextureSourceRootIfNeeded(runMode, options)) return;
        if (updateExisting && !OverlayService.HasActiveManagedBuildRegistry(options.GameDir))
        {
            ShowStyledMessage("dialog_notice_title", L.T("update_requires_active_build", _lang), L.T("dialog_notice_title", _lang), warning: true);
            return;
        }
        if (runMode == BuildRunMode.Manual && options.ApplyToGame && OverlayService.HasActiveManagedOverlays(options.GameDir))
        {
            ShowStyledMessage("dialog_notice_title", L.T("manual_active_build_blocked", _lang), L.T("dialog_notice_title", _lang), warning: true);
            return;
        }

        bool easyApplyHasExistingManagedBuild = easyApply && options.ApplyToGame && OverlayService.HasActiveManagedOverlays(options.GameDir);
        string debugOperationName = easyApplyHasExistingManagedBuild
            ? "Easy Apply over existing build"
            : easyApply
                ? "Fresh Easy Apply"
                : updateExisting
                    ? "Update Existing Build"
                    : "Manual Build / Advanced";

        _currentBuildCancel = new CancellationTokenSource();
        var cancellationToken = _currentBuildCancel.Token;
        SetBusy(true);
        Log(easyApply
            ? (L.IsSpanish(_lang) ? "===== APLICACIÓN FÁCIL =====" : "===== EASY APPLY =====")
            : updateExisting
                ? (L.IsSpanish(_lang) ? "===== ACTUALIZAR BUILD EXISTENTE =====" : "===== UPDATE EXISTING BUILD =====")
                : (L.IsSpanish(_lang) ? "===== CONSTRUIR MANUAL / AVANZADO =====" : "===== MANUAL BUILD / ADVANCED ====="));
        DebugFailureInjector.BeginOperation(debugOperationName, SelectedDebugFailurePointCode(), Log);
        if (easyApply)
        {
            Log(L.IsSpanish(_lang)
                ? "Aplicación Fácil ignora el filtro visible, aplica al juego, usa coincidencia Primario Seguro y hace reconstrucción limpia completa."
                : "Easy Apply ignores the visible filter, applies to game, uses Safe Primary matching by default, and performs a clean full rebuild.");
        }
        else if (updateExisting)
        {
            Log(L.IsSpanish(_lang)
                ? "Actualizar Build Existente usa los mismos valores seguros de Aplicación Fácil, carga el manifiesto local si coincide con la build activa y omite DDS sin cambios."
                : "Update Existing Build uses the same safe matching defaults as Easy Apply, loads the app-local source manifest when it matches the active build, and skips unchanged DDS targets.");
        }
        Task.Run(() =>
        {
            try
            {
                string easyApplyRollbackRoot = string.Empty;
                try
                {
                    if (easyApply && options.ApplyToGame && OverlayService.HasActiveManagedOverlays(options.GameDir))
                    {
                        Log(L.IsSpanish(_lang)
                            ? "Aplicación Fácil: build administrada existente detectada. Se quitará primero y luego se aplicará una build HD## nueva."
                            : "Easy Apply: existing managed build detected. Removing the current managed build first, then applying a fresh HD## build.");
                        easyApplyRollbackRoot = OverlayService.CreateEasyApplyRollbackBackup(options.GameDir, Log);
                        DebugFailureInjector.Check(DebugFailureInjector.EasyExistingAfterOverlaySafetyBackup, Log);
                        OverlayService.RemoveCurrentTextureBuild(options.GameDir, Log, deleteOverlays: true, progress: SetProgress, suppressMissingOverlayWarnings: true);
                        cancellationToken.ThrowIfCancellationRequested();
                        Log(L.IsSpanish(_lang)
                            ? "Aplicación Fácil: build administrada anterior quitada. Reiniciando memoria temporal antes de aplicar."
                            : "Easy Apply: previous managed build removed. Resetting transient memory before fresh apply.");
                        Log($"Easy Apply pre-apply reset memory before cleanup: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
                        OverlayBuilder.ReleaseCompletedBuildMemory(trimWorkingSet: true);
                        Log($"Easy Apply pre-apply reset memory after cleanup: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
                        Log(L.IsSpanish(_lang)
                            ? "Aplicación Fácil: iniciando aplicación nueva."
                            : "Easy Apply: starting fresh apply.");
                    }

                    var result = new OverlayService().BuildOrApply(options, Log, SetProgress, cancellationToken);
                    Log($"Post-return process memory before cleanup: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
                    OverlayBuilder.ReleaseCompletedBuildMemory(trimWorkingSet: true);
                    Log($"Post-return process memory after cleanup: {OverlayBuilder.CurrentProcessMemorySnapshotText()}.");
                    if (!string.IsNullOrWhiteSpace(easyApplyRollbackRoot)) OverlayService.DeleteEasyApplyRollbackBackup(easyApplyRollbackRoot, Log);
                    BeginInvoke(new Action(() =>
                    {
                        SetProgress(100, "Finished");
                        ShowBuildComplete(result);
                    }));
                }
                catch (Exception ex)
                {
                    if (DebugFailureInjector.IsSimulated(ex)) Log("Normal cleanup/rollback path entered (Easy Apply rollback restore).");
                    if (!string.IsNullOrWhiteSpace(easyApplyRollbackRoot)) OverlayService.RestoreEasyApplyRollbackBackup(options.GameDir, easyApplyRollbackRoot, Log);
                    if (DebugFailureInjector.IsSimulated(ex)) Log("Cleanup/rollback complete.");
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                Log(L.IsSpanish(_lang) ? "Construcción cancelada por el usuario." : "Build cancelled by user.");
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Cancelled");
                    ShowStyledMessage("dialog_notice_title", L.T("cancelled_message", _lang), L.T("dialog_notice_title", _lang), warning: true);
                }));
            }
            catch (DirectoryNotFoundException ex) when (ex.Message.Contains("texture folder", StringComparison.OrdinalIgnoreCase))
            {
                LogRuntimeOnly("Expected DDS texture folder validation error: " + ex);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(0, "Ready");
                    string msg = L.T(runMode == BuildRunMode.Manual ? "missing_dds_folder_manual" : "missing_dds_folder", _lang);
                    Log(msg);
                    ShowStyledMessage("dialog_warning_title", msg, L.T("dialog_warning_title", _lang), warning: true);
                }));
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Error");
                    ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true);
                }));
            }
            finally { DebugFailureInjector.EndOperation(); BeginInvoke(new Action(() => SetBusy(false))); }
        });
    }


    private void ShowBuildComplete(BuildResult result)
    {
        _lastReportPath = result.ReportPath;
        int total = result.MatchedCount + result.SkippedCount + result.AmbiguousCount;
        string headerText = result.Applied ? L.T("build_complete_header", _lang) : L.T("build_complete_dry_header", _lang);

        using var dlg = CreateStyledDialog(L.T("build_complete_title", _lang), Dpi(760), Dpi(390));
        var outer = CreateDialogOuter(dlg, 4);

        var header = new Label
        {
            Text = headerText,
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = GuiFonts.UiFont(14F, FontStyle.Bold),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 0, 10)
        };
        outer.Controls.Add(header, 0, 0);

        var summaryPanel = new ModernCardPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Color.FromArgb(16, 28, 43),
            FillColor = Color.FromArgb(16, 28, 43),
            BorderColor = Color.FromArgb(45, 64, 86),
            CornerRadius = 12,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 0, 0, 12)
        };
        summaryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) summaryPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(summaryPanel, 0, 1);

        void AddSummaryLine(int row, string labelKey, string value)
        {
            var line = new Label
            {
                Text = $"{L.T(labelKey, _lang)} {value}",
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoEllipsis = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = GuiFonts.UiFont(9.5F, row == 0 ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2),
                MaximumSize = new Size(700, 0)
            };
            summaryPanel.Controls.Add(line, 0, row);
        }

        AddSummaryLine(0, "build_complete_matched", $"{result.MatchedCount:n0} / {total:n0}");
        AddSummaryLine(1, "build_complete_ambiguous", result.AmbiguousCount.ToString("n0"));
        AddSummaryLine(2, "build_complete_not_found", result.NotFoundCount.ToString("n0"));
        AddSummaryLine(3, "build_complete_duplicate_sources", result.DuplicateSourceIgnoredCount.ToString("n0"));
        AddSummaryLine(4, "build_complete_failed_skipped", result.FailedSkippedCount.ToString("n0"));
        AddSummaryLine(5, "build_complete_elapsed", FormatElapsed(result.ElapsedSeconds));

        var reportPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = dlg.BackColor,
            Margin = new Padding(0)
        };
        reportPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        reportPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        reportPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var reportLabel = new Label
        {
            Text = L.T("build_complete_report", _lang),
            AutoSize = true,
            ForeColor = Color.FromArgb(184, 198, 214),
            Font = GuiFonts.UiFont(9.5F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        var reportLink = new LinkLabel
        {
            Text = result.ReportPath,
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = InputBackColor,
            ForeColor = Color.White,
            LinkColor = Color.FromArgb(122, 170, 255),
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(122, 170, 255),
            BorderStyle = BorderStyle.FixedSingle,
            Font = GuiFonts.UiFont(9F, FontStyle.Regular),
            Padding = new Padding(8, 0, 8, 0),
            Margin = new Padding(0),
            MinimumSize = new Size(0, 30),
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        reportLink.LinkArea = new LinkArea(0, result.ReportPath.Length);
        reportLink.LinkClicked += (_, _) => OpenFileInExplorer(result.ReportPath);
        var hint = new Label
        {
            Text = L.T("build_complete_open_hint", _lang),
            AutoSize = true,
            ForeColor = MutedTextColor,
            Font = GuiFonts.UiFont(8.5F, FontStyle.Italic),
            Margin = new Padding(0, 4, 0, 0)
        };
        reportPanel.Controls.Add(reportLabel, 0, 0);
        reportPanel.Controls.Add(reportLink, 0, 1);
        reportPanel.Controls.Add(hint, 0, 2);
        outer.Controls.Add(reportPanel, 0, 2);

        var ok = DialogButton(L.T("ok", _lang), primary: true);
        ok.DialogResult = DialogResult.OK;
        ok.Anchor = AnchorStyles.Right;
        outer.Controls.Add(ok, 0, 3);
        dlg.AcceptButton = ok;
        dlg.CancelButton = ok;
        dlg.ShowDialog(this);
    }

    private Form CreateStyledDialog(string title, int width = 520, int height = 260)
    {
        Size safeSize = ClampDialogSizeToScreen(new Size(width, height));
        var dlg = new StyledDialogForm(title, Dpi(14), Dpi(42))
        {
            Text = title,
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = safeSize,
            MinimumSize = new Size(Math.Min(safeSize.Width, Dpi(430)), Math.Min(safeSize.Height, Dpi(190))),
            BackColor = Color.FromArgb(10, 15, 24),
            ForeColor = Color.FromArgb(230, 238, 248),
            Font = GuiFonts.UiFont(9F),
            ShowIcon = false,
            ShowInTaskbar = false
        };
        return dlg;
    }

    private Size ClampDialogSizeToScreen(Size requested)
    {
        try
        {
            Rectangle work = Screen.FromControl(this)?.WorkingArea
                ?? Screen.PrimaryScreen?.WorkingArea
                ?? Rectangle.Empty;
            if (!work.IsEmpty)
            {
                int maxWidth = Math.Max(Dpi(420), work.Width - Dpi(96));
                int maxHeight = Math.Max(Dpi(260), work.Height - Dpi(96));
                requested.Width = Math.Min(requested.Width, maxWidth);
                requested.Height = Math.Min(requested.Height, maxHeight);
            }
        }
        catch { }
        requested.Width = Math.Max(Dpi(360), requested.Width);
        requested.Height = Math.Max(Dpi(190), requested.Height);
        return requested;
    }

    private static Control DialogHost(Form dlg)
        => dlg is StyledDialogForm styled ? styled.ContentHost : dlg;

    private TableLayoutPanel CreateDialogOuter(Form dlg, int rows)
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = rows,
            ColumnCount = 1,
            BackColor = dlg.BackColor,
            Padding = new Padding(Dpi(14))
        };
        for (int i = 0; i < rows - 1; i++)
            outer.RowStyles.Add(new RowStyle(i == rows - 2 ? SizeType.Percent : SizeType.AutoSize, i == rows - 2 ? 100 : 0));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        DialogHost(dlg).Controls.Add(outer);
        return outer;
    }

    private TableLayoutPanel CreateDialogOuterAuto(Form dlg, int rows)
    {
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = rows,
            ColumnCount = 1,
            BackColor = dlg.BackColor,
            Padding = new Padding(Dpi(14))
        };
        for (int i = 0; i < rows; i++) outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        DialogHost(dlg).Controls.Add(outer);
        return outer;
    }

    private Control DialogMessageBox(string message, int maxTextWidth, bool fill = true)
    {
        var box = new RoundedPanel
        {
            Dock = fill ? DockStyle.Fill : DockStyle.Top,
            AutoSize = !fill,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScroll = fill,
            BackColor = Color.FromArgb(16, 28, 43),
            BorderColor = Color.FromArgb(45, 64, 86),
            CornerRadius = Dpi(4),
            Padding = new Padding(Dpi(12)),
            Margin = new Padding(0, 0, 0, Dpi(6))
        };
        var msg = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(Math.Max(Dpi(260), maxTextWidth - Dpi(28)), 0),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(230, 238, 248),
            Font = GuiFonts.UiFont(9.5F, FontStyle.Regular),
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        box.Controls.Add(msg);
        return box;
    }

    private Size ComputeCompactDialogSize(string message, int minWidth, int maxWidth, int minHeight, int maxHeight)
    {
        using var font = GuiFonts.UiFont(9.5F, FontStyle.Regular);
        string normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        int longest = lines.Length == 0 ? 0 : lines.Max(line => TextRenderer.MeasureText(line.Length == 0 ? " " : line, font).Width);

        int scaledMinWidth = Dpi(minWidth);
        int scaledMaxWidth = Dpi(maxWidth);
        int scaledMinHeight = Dpi(minHeight);
        int scaledMaxHeight = Dpi(maxHeight);

        try
        {
            Rectangle work = Screen.FromControl(this)?.WorkingArea
                ?? Screen.PrimaryScreen?.WorkingArea
                ?? Rectangle.Empty;
            if (!work.IsEmpty)
            {
                scaledMaxWidth = Math.Min(scaledMaxWidth, Math.Max(Dpi(420), work.Width - Dpi(96)));
                scaledMaxHeight = Math.Min(scaledMaxHeight, Math.Max(Dpi(260), work.Height - Dpi(96)));
            }
        }
        catch { }

        int width = Math.Max(scaledMinWidth, Math.Min(scaledMaxWidth, longest + Dpi(120)));
        int textWidth = Math.Max(Dpi(280), width - Dpi(96));
        var measured = TextRenderer.MeasureText(normalized, font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        int height = Dpi(16) + Dpi(36) + Dpi(10) + Math.Max(Dpi(96), measured.Height + Dpi(36)) + Dpi(58) + Dpi(16);
        height = Math.Max(scaledMinHeight, Math.Min(scaledMaxHeight, height));
        return ClampDialogSizeToScreen(new Size(width, height));
    }

    private Button DialogButton(string text, bool primary = false, bool danger = false, bool warning = false)
    {
        var b = new Button
        {
            Text = text,
            Width = Dpi(132),
            Height = Dpi(38),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(55, 125, 245) : danger ? Color.FromArgb(147, 57, 57) : warning ? Color.FromArgb(145, 105, 42) : Color.FromArgb(31, 48, 68),
            ForeColor = Color.White,
            Font = GuiFonts.UiFont(9.5F, FontStyle.Bold),
            Margin = new Padding(Dpi(8), Dpi(10), 0, 0),
            UseMnemonic = false
        };
        b.FlatAppearance.BorderSize = primary || danger || warning ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(57, 78, 102);
        return b;
    }

    private DialogResult ShowStyledMessage(string titleKey, string message, string header, bool error = false, bool warning = false)
    {
        var size = ComputeCompactDialogSize(message, minWidth: 430, maxWidth: 720, minHeight: 195, maxHeight: 460);
        using var dlg = CreateStyledDialog(L.T(titleKey, _lang), size.Width, size.Height);
        var outer = CreateDialogOuter(dlg, 3);
        var head = new Label
        {
            Text = header,
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = GuiFonts.UiFont(13F, FontStyle.Bold),
            ForeColor = error ? Color.FromArgb(255, 155, 155) : warning ? Color.FromArgb(255, 215, 150) : Color.White,
            Margin = new Padding(0, 0, 0, Dpi(10))
        };
        outer.Controls.Add(head, 0, 0);
        outer.Controls.Add(DialogMessageBox(message, size.Width - Dpi(64)), 0, 1);
        var ok = DialogButton(L.T("ok", _lang), primary: !error && !warning, danger: error, warning: warning);
        ok.DialogResult = DialogResult.OK;
        ok.Anchor = AnchorStyles.Right;
        outer.Controls.Add(ok, 0, 2);
        dlg.AcceptButton = ok;
        dlg.CancelButton = ok;
        return dlg.ShowDialog(this);
    }

    private bool ConfirmEasyApply()
    {
        string message = L.T("easy_apply_confirm", _lang) + Environment.NewLine + Environment.NewLine + L.T("easy_apply_usage_warning", _lang);
        try
        {
            string gameDir = _game.Text.Trim();
            if (OverlayService.HasActiveManagedOverlays(gameDir))
            {
                message += Environment.NewLine + Environment.NewLine + L.T("easy_apply_existing_warning", _lang);
            }
        }
        catch { }
        var size = ComputeCompactDialogSize(message, minWidth: 620, maxWidth: 900, minHeight: 390, maxHeight: 700);
        using var dlg = CreateStyledDialog(L.T("easy_apply_confirm_title", _lang), size.Width, size.Height);
        var outer = CreateDialogOuter(dlg, 3);
        var head = new Label
        {
            Text = L.T("easy_apply_confirm_header", _lang),
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = GuiFonts.UiFont(13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 215, 150),
            Margin = new Padding(0, 0, 0, Dpi(10))
        };
        outer.Controls.Add(head, 0, 0);
        outer.Controls.Add(DialogMessageBox(message, size.Width - Dpi(64)), 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoSize = true,
            BackColor = dlg.BackColor,
            Margin = new Padding(0)
        };
        var accept = DialogButton(L.T("accept", _lang), warning: true);
        var cancel = DialogButton(L.T("cancel", _lang));
        accept.Width = Dpi(144);
        cancel.Width = Dpi(144);
        accept.Click += (_, __) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        cancel.Click += (_, __) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        outer.Controls.Add(buttons, 0, 2);
        dlg.AcceptButton = accept;
        dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK;
    }


    private bool ConfirmDangerOverride(string titleKey, string header, string message, string dangerKey = "continue_anyway")
    {
        var size = ComputeCompactDialogSize(message, minWidth: 590, maxWidth: 900, minHeight: 300, maxHeight: 690);
        using var dlg = CreateStyledDialog(L.T(titleKey, _lang), size.Width, size.Height);
        var outer = CreateDialogOuter(dlg, 3);
        var head = new Label
        {
            Text = header,
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = GuiFonts.UiFont(13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 215, 150),
            Margin = new Padding(0, 0, 0, Dpi(10))
        };
        outer.Controls.Add(head, 0, 0);
        outer.Controls.Add(DialogMessageBox(message, size.Width - Dpi(64)), 0, 1);

        // Dangerous override is intentionally on the left. Cancel is on the
        // right and is the default focused/Enter action; Esc also cancels.
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = dlg.BackColor,
            Margin = new Padding(0)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var danger = DialogButton(L.T(dangerKey, _lang), danger: true);
        var cancel = DialogButton(L.T("cancel", _lang), primary: true);
        danger.Width = Dpi(190);
        cancel.Width = Dpi(144);
        danger.Anchor = AnchorStyles.Left;
        cancel.Anchor = AnchorStyles.Right;
        danger.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        danger.Click += (_, __) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        cancel.Click += (_, __) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
        buttons.Controls.Add(danger, 0, 0);
        buttons.Controls.Add(cancel, 1, 0);
        outer.Controls.Add(buttons, 0, 2);

        dlg.AcceptButton = cancel;
        dlg.CancelButton = cancel;
        dlg.ActiveControl = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK;
    }

    private bool ConfirmStyled(string titleKey, string header, string message, string acceptKey = "yes", bool warning = false)
    {
        var size = ComputeCompactDialogSize(message, minWidth: 550, maxWidth: 880, minHeight: 250, maxHeight: 640);
        using var dlg = CreateStyledDialog(L.T(titleKey, _lang), size.Width, size.Height);
        var outer = CreateDialogOuter(dlg, 3);
        var head = new Label
        {
            Text = header,
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = GuiFonts.UiFont(13F, FontStyle.Bold),
            ForeColor = warning ? Color.FromArgb(255, 215, 150) : Color.White,
            Margin = new Padding(0, 0, 0, Dpi(10))
        };
        outer.Controls.Add(head, 0, 0);
        outer.Controls.Add(DialogMessageBox(message, size.Width - Dpi(64)), 0, 1);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoSize = true,
            BackColor = dlg.BackColor,
            Margin = new Padding(0)
        };
        var yes = DialogButton(L.T(acceptKey, _lang), primary: !warning, warning: warning);
        var no = DialogButton(L.T("cancel", _lang));
        yes.Width = Dpi(144);
        no.Width = Dpi(144);
        yes.Click += (_, __) => { dlg.DialogResult = DialogResult.Yes; dlg.Close(); };
        no.Click += (_, __) => { dlg.DialogResult = DialogResult.No; dlg.Close(); };
        buttons.Controls.Add(yes);
        buttons.Controls.Add(no);
        outer.Controls.Add(buttons, 0, 2);
        dlg.AcceptButton = yes;
        dlg.CancelButton = no;
        return dlg.ShowDialog(this) == DialogResult.Yes;
    }

    private bool ConfirmStyled(string message, bool warning = false)
    {
        var size = ComputeCompactDialogSize(message, minWidth: 480, maxWidth: 780, minHeight: 210, maxHeight: 500);
        using var dlg = CreateStyledDialog(L.T("dialog_confirm_title", _lang), size.Width, size.Height);
        var outer = CreateDialogOuter(dlg, 3);
        var head = new Label
        {
            Text = L.T("dialog_question_continue", _lang),
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = GuiFonts.UiFont(13F, FontStyle.Bold),
            ForeColor = warning ? Color.FromArgb(255, 215, 150) : Color.White,
            Margin = new Padding(0, 0, 0, Dpi(10))
        };
        outer.Controls.Add(head, 0, 0);
        outer.Controls.Add(DialogMessageBox(message, size.Width - Dpi(64)), 0, 1);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoSize = true,
            BackColor = dlg.BackColor,
            Margin = new Padding(0)
        };
        var yes = DialogButton(L.T("yes", _lang), primary: !warning, warning: warning);
        var no = DialogButton(L.T("no", _lang));
        yes.Width = Dpi(144);
        no.Width = Dpi(144);

        // Be explicit here instead of relying only on Button.DialogResult.
        // This prevents a "No" click from ever falling through as a confirmed action
        // on high DPI/custom styled dialogs or unusual Windows focus behavior.
        yes.Click += (_, __) => { dlg.DialogResult = DialogResult.Yes; dlg.Close(); };
        no.Click += (_, __) => { dlg.DialogResult = DialogResult.No; dlg.Close(); };

        buttons.Controls.Add(yes);
        buttons.Controls.Add(no);
        outer.Controls.Add(buttons, 0, 2);
        dlg.AcceptButton = yes;
        dlg.CancelButton = no;
        return dlg.ShowDialog(this) == DialogResult.Yes;
    }

    private void OpenFileInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return;
            }
            string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true);
        }
    }

    private static string FormatElapsed(double seconds)
    {
        int total = Math.Max(0, (int)Math.Round(seconds));
        int hours = total / 3600;
        int minutes = (total % 3600) / 60;
        int secs = total % 60;
        return hours > 0 ? $"{hours}h {minutes}m {secs}s" : $"{minutes}m {secs}s";
    }

    private void AutoDetect(bool silent)
    {
        var d = OverlayService.DetectGameDir();
        if (d != null)
        {
            _game.Text = d;
            Log(string.Format(L.T("game_detected", _lang), d));
            if (!silent) ShowStyledMessage("dialog_complete_title", string.Format(L.T("game_detected", _lang), d), L.T("dialog_complete_title", _lang));
        }
        else if (!silent) ShowStyledMessage("dialog_warning_title", L.T("detect_failed", _lang), L.T("dialog_warning_title", _lang), warning: true);
    }

    // Kept as a private maintenance helper, but no longer exposed in the main UI.
    // Restoring only meta while a build is applied can desync overlay folders from
    // meta/0.pathc + meta/0.papgt. Users should use Remove Current Build instead,
    // which removes managed overlays and restores/rebuilds meta safely.
    private void RestoreBackup()
    {
        if (_busy) return;
        string gameDir = _game.Text.Trim();
        if (!OverlayService.IsGameDir(gameDir)) { ShowStyledMessage("dialog_warning_title", L.T("select_valid_game", _lang), L.T("dialog_warning_title", _lang), warning: true); return; }
        var b = OverlayService.FindLatestMetaBackup(gameDir);
        if (b == null) { ShowStyledMessage("dialog_warning_title", L.T("no_backup", _lang), L.T("dialog_warning_title", _lang), warning: true); return; }
        if (!ConfirmStyled(string.Format(L.T("restore_confirm", _lang), b), warning: true)) return;
        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                OverlayService.RestoreMetaFromBackup(gameDir, b, Log);
                BeginInvoke(new Action(() => ShowStyledMessage("dialog_complete_title", L.T("restore_done", _lang), L.T("dialog_complete_title", _lang))));
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                BeginInvoke(new Action(() => ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true)));
            }
            finally { BeginInvoke(new Action(() => SetBusy(false))); }
        });
    }


    private static string SectionDisplayName(string? pamt, string? prefix, string lang)
    {
        pamt = string.IsNullOrWhiteSpace(pamt) ? "ALL" : pamt.Trim();
        prefix = string.IsNullOrWhiteSpace(prefix) ? "ALL" : prefix.Trim();
        return pamt switch
        {
            "0000" when prefix.StartsWith("object/texture/sublayer", StringComparison.OrdinalIgnoreCase) => L.T("preset_object_sublayer", lang),
            "0000" when prefix.StartsWith("object/texture", StringComparison.OrdinalIgnoreCase) => L.T("preset_objects", lang),
            "0001" when prefix.StartsWith("tree", StringComparison.OrdinalIgnoreCase) => L.T("preset_trees", lang),
            "0002" when prefix.StartsWith("texture", StringComparison.OrdinalIgnoreCase) => L.T("preset_shared", lang),
            "0007" when prefix.StartsWith("effect", StringComparison.OrdinalIgnoreCase) => L.T("preset_effects", lang),
            "0009" when prefix.StartsWith("character/texture", StringComparison.OrdinalIgnoreCase) => L.T("preset_characters", lang),
            "0012" when prefix.StartsWith("ui", StringComparison.OrdinalIgnoreCase) => L.T("preset_ui", lang),
            "0015" when prefix.StartsWith("leveldata", StringComparison.OrdinalIgnoreCase) => L.T("preset_leveldata", lang),
            "ALL" => L.T("preset_all", lang),
            _ => $"PAMT {pamt}"
        };
    }

    private string InstalledBuildDisplay(InstalledBuildSummary b)
    {
        string section = SectionDisplayName(b.TargetPamtDir, b.TargetFullPrefix, _lang);
        string newOverlays = b.OverlayDirs.Count == 0 ? "-" : string.Join(",", b.OverlayDirs);
        string updatedOverlays = b.UpdatedOverlayDirs.Count == 0 ? "-" : string.Join(",", b.UpdatedOverlayDirs);
        return $"{section} | {L.T("installed_new_overlays", _lang)}: {newOverlays} | {L.T("installed_updated_overlays", _lang)}: {updatedOverlays} | {L.T("installed_new_textures", _lang)}: {b.NewOverlayTextureCount:N0} | {L.T("installed_existing_matched_unchanged", _lang)}: {b.UpdatedExistingCount:N0} | {L.T("installed_total_matched", _lang)}: {b.MatchedCount:N0} | {CompactCreatedTime(b.CreatedAt)}";
    }

    private static string CompactOverlayDirs(IReadOnlyList<string> dirs)
    {
        if (dirs.Count == 0) return "-";

        var parsed = dirs
            .Select(ParseOverlayFolderName)
            .ToList();

        bool allRangeable = parsed.All(p => p.prefix.Length > 0 && p.number >= 0)
                            && parsed.Select(p => p.prefix).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        if (allRangeable && parsed.Count > 1)
        {
            var sorted = parsed.OrderBy(p => p.number).ToList();
            bool contiguous = true;
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].number != sorted[i - 1].number + 1) { contiguous = false; break; }
            }
            if (contiguous)
            {
                string prefix = sorted[0].prefix;
                int width = Math.Max(sorted[0].width, sorted[^1].width);
                return $"{prefix}{sorted[0].number.ToString().PadLeft(width, '0')}-{prefix}{sorted[^1].number.ToString().PadLeft(width, '0')} ({sorted.Count})";
            }
        }

        string joined = string.Join(", ", dirs);
        if (joined.Length <= 34) return joined;
        return string.Join(", ", dirs.Take(4)) + $", ... ({dirs.Count})";
    }

    private static (string prefix, int number, int width) ParseOverlayFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ("", -1, 0);
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        if (i == name.Length - 1) return ("", -1, 0);
        string prefix = name[..(i + 1)];
        string digits = name[(i + 1)..];
        return int.TryParse(digits, out int n) ? (prefix, n, digits.Length) : ("", -1, 0);
    }

    private static string CompactCreatedTime(string created)
    {
        if (DateTime.TryParse(created, out var dt)) return dt.ToString("yyyy-MM-dd HH:mm");
        return created.Replace("T", " ");
    }

    private void ManageInstalledBuilds()
    {
        if (_busy) return;
        string gameDir = _game.Text.Trim();
        if (!OverlayService.IsGameDir(gameDir)) { ShowStyledMessage("dialog_warning_title", L.T("select_valid_game", _lang), L.T("dialog_warning_title", _lang), warning: true); return; }
        List<InstalledBuildSummary> builds;
        try { builds = OverlayService.GetInstalledTextureBuilds(gameDir); }
        catch (Exception ex) { ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true); return; }
        if (builds.Count == 0) { ShowStyledMessage("dialog_notice_title", L.T("no_builds", _lang), L.T("dialog_notice_title", _lang), warning: true); return; }

        using var dlg = CreateStyledDialog(L.T("manage", _lang), Dpi(1060), Dpi(480));
        var outer = CreateDialogOuter(dlg, 3);
        var head = new Label
        {
            Text = L.T("manage", _lang),
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = GuiFonts.UiFont(13F, FontStyle.Bold),
            ForeColor = Color.White,
            Margin = new Padding(0, 0, 0, 8)
        };
        outer.Controls.Add(head, 0, 0);

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            ShowItemToolTips = true,
            BackColor = Color.FromArgb(16, 28, 43),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = GuiFonts.UiFont(9.0F, FontStyle.Regular)
        };
        list.Columns.Add(L.T("col_texture_section", _lang), Dpi(165));
        list.Columns.Add(L.T("col_new_overlay_folders", _lang), Dpi(155));
        list.Columns.Add(L.T("col_updated_overlays", _lang), Dpi(145));
        list.Columns.Add(L.T("col_new_textures", _lang), Dpi(105), HorizontalAlignment.Right);
        list.Columns.Add(L.T("col_existing_matched", _lang), Dpi(115), HorizontalAlignment.Right);
        list.Columns.Add(L.T("col_total_matched", _lang), Dpi(105), HorizontalAlignment.Right);
        list.Columns.Add(L.T("col_created", _lang), Dpi(145));
        foreach (var b in builds)
        {
            var item = new ListViewItem(SectionDisplayName(b.TargetPamtDir, b.TargetFullPrefix, _lang))
            {
                Tag = b,
                ToolTipText = InstalledBuildDisplay(b)
            };
            item.SubItems.Add(CompactOverlayDirs(b.OverlayDirs));
            item.SubItems.Add(CompactOverlayDirs(b.UpdatedOverlayDirs));
            item.SubItems.Add(b.NewOverlayTextureCount.ToString("N0"));
            item.SubItems.Add(b.UpdatedExistingCount.ToString("N0"));
            item.SubItems.Add(b.MatchedCount.ToString("N0"));
            item.SubItems.Add(CompactCreatedTime(b.CreatedAt));
            list.Items.Add(item);
        }
        outer.Controls.Add(list, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = dlg.BackColor,
            Margin = new Padding(0)
        };
        var remove = DialogButton(L.T("remove_selected", _lang), danger: true);
        var close = DialogButton(L.T("cancel", _lang));
        remove.Click += (_, __) => { dlg.DialogResult = DialogResult.Yes; dlg.Close(); };
        close.Click += (_, __) => { dlg.DialogResult = DialogResult.No; dlg.Close(); };
        buttons.Controls.Add(remove);
        buttons.Controls.Add(close);
        outer.Controls.Add(buttons, 0, 2);
        dlg.AcceptButton = remove;
        dlg.CancelButton = close;
        if (dlg.ShowDialog(this) != DialogResult.Yes) return;

        var selectedBuilds = list.Items
            .Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => (InstalledBuildSummary)i.Tag!)
            .ToList();
        var selectedIds = selectedBuilds.Select(b => b.ModId).ToList();
        if (selectedIds.Count == 0) { ShowStyledMessage("dialog_warning_title", L.T("select_builds", _lang), L.T("dialog_warning_title", _lang), warning: true); return; }
        int selectedOverlayCount = selectedBuilds.Sum(b => b.OverlayDirs.Count);
        int remainingBuildCount = Math.Max(0, builds.Count - selectedBuilds.Count);
        if (!ConfirmStyled(L.T("remove_selected_confirm", _lang), warning: true)) return;

        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                SetProgress(2, "REMOVE SELECTED: START");
                bool removed = OverlayService.RemoveSelectedTextureBuilds(gameDir, selectedIds, Log, SetProgress);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Finished");
                    string msg = removed
                        ? string.Format(L.T("remove_selected_done_detail", _lang), selectedOverlayCount, remainingBuildCount)
                        : L.T("nothing_to_remove", _lang);
                    ShowStyledMessage("dialog_complete_title", msg, L.T("dialog_complete_title", _lang));
                }));
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                BeginInvoke(new Action(() => ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true)));
            }
            finally { BeginInvoke(new Action(() => SetBusy(false))); }
        });
    }

    private void RemoveBuild()
    {
        if (_busy) return;
        string gameDir = _game.Text.Trim();
        if (!OverlayService.IsGameDir(gameDir)) { ShowStyledMessage("dialog_warning_title", L.T("select_valid_game", _lang), L.T("dialog_warning_title", _lang), warning: true); return; }
        if (!ConfirmStyled(L.T("remove_confirm", _lang), warning: true)) return;
        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                // Remove Current Build is intentionally a single confirm action now:
                // Yes removes the registered build and overlay/tool folders;
                // No cancels everything.  The old second prompt was confusing because
                // pressing No there still removed the active build by moving overlays.
                SetProgress(2, "REMOVE: START");
                bool removed = OverlayService.RemoveCurrentTextureBuild(gameDir, Log, deleteOverlays: true, progress: SetProgress);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Finished");
                    ShowStyledMessage("dialog_complete_title", removed ? L.T("remove_done", _lang) : L.T("nothing_to_remove", _lang), L.T("dialog_complete_title", _lang));
                }));
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                BeginInvoke(new Action(() => ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true)));
            }
            finally { BeginInvoke(new Action(() => SetBusy(false))); }
        });
    }

    private static bool IsNoActiveRegisteredOverlayHoldException(InvalidOperationException ex)
    {
        return ex.Message.Contains("No active registered overlays", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("Hold", StringComparison.OrdinalIgnoreCase);
    }

    private void SmartHold()
    {
        if (_busy) return;
        string gameDir = _game.Text.Trim();
        if (!OverlayService.IsGameDir(gameDir)) { ShowStyledMessage("dialog_warning_title", L.T("select_valid_game", _lang), L.T("dialog_warning_title", _lang), warning: true); return; }
        if (!ConfirmStyled(L.T("hold_confirm", _lang), warning: true)) return;
        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                SetProgress(2, "HOLD: START");
                OverlayService.HoldRegisteredOverlays(gameDir, Log, SetProgress);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Finished");
                    ShowStyledMessage("dialog_complete_title", L.T("hold_done", _lang), L.T("dialog_complete_title", _lang));
                }));
            }
            catch (InvalidOperationException ex) when (IsNoActiveRegisteredOverlayHoldException(ex))
            {
                LogRuntimeOnly("Smart Overlay Hold skipped: no active registered overlays were found for Hold. " + ex);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(0, "Ready");
                    string msg = L.T("hold_no_active", _lang);
                    Log(msg);
                    ShowStyledMessage("dialog_warning_title", msg, L.T("dialog_warning_title", _lang), warning: true);
                }));
            }
            catch (Exception ex) { Log(ex.ToString()); BeginInvoke(new Action(() => ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true))); }
            finally { BeginInvoke(new Action(() => SetBusy(false))); }
        });
    }

    private static bool IsNoHeldReleaseException(InvalidOperationException ex)
    {
        return ex.Message.Contains("No held", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("restore", StringComparison.OrdinalIgnoreCase);
    }

    private void ReleaseHold()
    {
        if (_busy) return;
        string gameDir = _game.Text.Trim();
        if (!OverlayService.IsGameDir(gameDir)) { ShowStyledMessage("dialog_warning_title", L.T("select_valid_game", _lang), L.T("dialog_warning_title", _lang), warning: true); return; }
        if (!ConfirmStyled(L.T("release_confirm", _lang), warning: true)) return;
        SetBusy(true);
        Task.Run(() =>
        {
            try
            {
                SetProgress(2, "RELEASE: START");
                OverlayService.ReleaseHoldAndReapply(gameDir, Log, SetProgress);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(100, "Finished");
                    ShowStyledMessage("dialog_complete_title", L.T("release_done", _lang), L.T("dialog_complete_title", _lang));
                }));
            }
            catch (InvalidOperationException ex) when (IsNoHeldReleaseException(ex))
            {
                LogRuntimeOnly("Release Hold + Reapply skipped: no held overlays were found to restore. " + ex);
                BeginInvoke(new Action(() =>
                {
                    SetProgress(0, "Ready");
                    string msg = L.T("release_no_held", _lang);
                    Log(msg);
                    ShowStyledMessage("dialog_warning_title", msg, L.T("dialog_warning_title", _lang), warning: true);
                }));
            }
            catch (Exception ex) { Log(ex.ToString()); BeginInvoke(new Action(() => ShowStyledMessage("dialog_error_title", L.Runtime(ex.Message, _lang), L.T("dialog_error_title", _lang), error: true))); }
            finally { BeginInvoke(new Action(() => SetBusy(false))); }
        });
    }

}

internal sealed record LanguageChoice(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}

internal sealed class StyledDialogForm : Form
{
    public Panel ContentHost { get; }

    public StyledDialogForm(string title, int cornerRadius, int titleBarHeight)
    {
        // Native dialog chrome is intentionally used here. It avoids the resize,
        // focus-border, and high-DPI clipping problems seen during the custom
        // chrome experiment while keeping the dialog body dark and app-styled.
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = true;
        MaximizeBox = false;
        MinimizeBox = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 15, 24);
        ForeColor = Color.FromArgb(230, 238, 248);
        Padding = new Padding(0);

        ContentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        Controls.Add(ContentHost);
    }
}

internal sealed class RoundedPanel : Panel
{
    public int CornerRadius { get; set; } = 12;
    public Color BorderColor { get; set; } = Color.FromArgb(45, 64, 86);

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outside = new SolidBrush(UiShapes.ParentFill(this, BackColor));
        e.Graphics.FillRectangle(outside, ClientRectangle);
        using var path = UiShapes.RoundedRect(new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), CornerRadius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiShapes.RoundedRect(new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), CornerRadius);
        using var pen = new Pen(BorderColor, 1F);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class RoundedLabel : Label
{
    public int CornerRadius { get; set; } = 8;
    public Color BorderColor { get; set; } = Color.Transparent;

    public RoundedLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outside = new SolidBrush(UiShapes.ParentFill(this, BackColor));
        e.Graphics.FillRectangle(outside, ClientRectangle);
        using var path = UiShapes.RoundedRect(new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), CornerRadius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (BorderColor.A > 0)
        {
            using var path = UiShapes.RoundedRect(new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), CornerRadius);
            using var pen = new Pen(BorderColor, 1F);
            e.Graphics.DrawPath(pen, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

internal sealed class BrandTitleLabel : Label
{
    public BrandTitleLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        string text = Text ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        SizeF measured = e.Graphics.MeasureString(text, Font);
        float y = Math.Max(0, (ClientSize.Height - measured.Height) / 2F);
        float x = 0;

        using var shadow = new SolidBrush(Color.FromArgb(130, 0, 0, 0));
        e.Graphics.DrawString(text, Font, shadow, x + 2F, y + 2F);

        var textRect = new RectangleF(x, y, Math.Max(1F, measured.Width + 4F), Math.Max(1F, measured.Height));
        using var glow = new SolidBrush(Color.FromArgb(70, 255, 190, 91));
        e.Graphics.DrawString(text, Font, glow, x + 1F, y);

        using var brush = new LinearGradientBrush(textRect, Color.FromArgb(255, 242, 197), Color.FromArgb(222, 149, 59), LinearGradientMode.Horizontal);
        e.Graphics.DrawString(text, Font, brush, x, y);
    }
}

internal static class UiShapes
{
    public static Color ParentFill(Control control, Color fallback)
    {
        try
        {
            Color c = control.Parent?.BackColor ?? fallback;
            return c.A == 0 ? fallback : c;
        }
        catch { return fallback; }
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int r = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        var path = new GraphicsPath();
        if (r <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int d = r * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ModernCardPanel : TableLayoutPanel
{
    public int CornerRadius { get; set; } = 12;
    public Color FillColor { get; set; } = Color.FromArgb(17, 28, 43);
    public Color BorderColor { get; set; } = Color.FromArgb(45, 64, 86);

    public ModernCardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outside = new SolidBrush(UiShapes.ParentFill(this, FillColor));
        e.Graphics.FillRectangle(outside, ClientRectangle);
        using var path = UiShapes.RoundedRect(new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), CornerRadius);
        using var brush = new SolidBrush(FillColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiShapes.RoundedRect(new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), CornerRadius);
        using var pen = new Pen(BorderColor, 1F);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class TextProgressBar : Control
{
    private int _value;
    public int Maximum { get; set; } = 100;
    public int Value
    {
        get => _value;
        set { _value = Math.Max(0, Math.Min(Maximum, value)); Invalidate(); }
    }
    public string ProgressText { get; set; } = "";
    public bool Activity { get; set; }
    private int _activityOffset;
    private int _activityFrame;

    public void StepActivity()
    {
        _activityOffset = (_activityOffset + 10) % 1000;
        _activityFrame = (_activityFrame + 1) % 4;
        Invalidate();
    }

    public TextProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(10, 17, 28);
        ForeColor = Color.White;
        Font = GuiFonts.UiFont(8.5F, FontStyle.Bold);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        Rectangle outer = new Rectangle(0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
        using var back = new SolidBrush(BackColor);
        g.FillRectangle(back, outer);

        int innerHeight = Math.Max(0, ClientSize.Height - 2);
        int fill = Maximum <= 0 ? 0 : (int)Math.Round((ClientSize.Width - 2) * (Value / (double)Maximum));
        if (fill > 0)
        {
            Rectangle fillRect = new Rectangle(1, 1, fill, innerHeight);
            using var brush = new LinearGradientBrush(fillRect, Color.FromArgb(45, 116, 246), Color.FromArgb(83, 151, 255), LinearGradientMode.Horizontal);
            g.FillRectangle(brush, fillRect);
        }
        if (Activity && Value < Maximum && ClientSize.Width > 16)
        {
            int bandWidth = Math.Max(36, ClientSize.Width / 7);
            int usableWidth = Math.Max(1, ClientSize.Width + bandWidth);
            int x = 1 + (_activityOffset % usableWidth) - bandWidth;
            using var activeBrush = new SolidBrush(Color.FromArgb(95, 118, 183, 255));
            g.FillRectangle(activeBrush, x, 1, bandWidth, innerHeight);
        }
        using var pen = new Pen(Color.FromArgb(55, 76, 103));
        g.DrawRectangle(pen, outer);
        string text = string.IsNullOrWhiteSpace(ProgressText) ? $"{Value}%" : ProgressText;
        if (Activity && Value < Maximum)
        {
            string dots = new string('.', _activityFrame);
            text = text.TrimEnd('.') + dots;
        }
        TextRenderer.DrawText(g, text, Font, ClientRectangle, Color.FromArgb(230, 240, 252), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

