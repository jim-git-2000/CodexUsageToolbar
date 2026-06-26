using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace CodexUsageToolbar;

internal static class Program
{
    private const string DefaultHost = "codex-vm";
    private const string DefaultRemoteCommand = "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json";

    [STAThread]
    public static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options.ShowHelp)
        {
            AttachConsoleForCli();
            Console.WriteLine(AppOptions.HelpText);
            return 0;
        }

        if (options.Once)
        {
            AttachConsoleForCli();
            return RunOnceAsync(options).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext(options));
        return 0;
    }

    private static async Task<int> RunOnceAsync(AppOptions options)
    {
        try
        {
            var rawJson = options.JsonFile is null
                ? await new SshCommandClient(options).RunAsync()
                : await File.ReadAllTextAsync(options.JsonFile);
            var state = UsageJsonParser.Parse(rawJson);
            Console.Write(UsageFormatter.FormatConsole(state));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void AttachConsoleForCli()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = AttachConsole(-1);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    internal sealed record AppOptions(
        bool Once,
        bool ShowHelp,
        string SshHost,
        string RemoteCommand,
        string? JsonFile,
        string? IdentityFile,
        int Port,
        int ConnectTimeoutSeconds,
        int CommandTimeoutSeconds,
        int PollIntervalSeconds,
        int? WindowX,
        int? WindowY,
        int WindowWidth,
        int WindowHeight,
        double Opacity,
        bool AlwaysOnTop,
        bool StartHidden,
        bool ClickThrough,
        bool StartWithWindows,
        string AccentColor,
        string BackgroundMode,
        string QuotaLayout)
    {
        public static string HelpText =>
            """
            Usage:
              CodexUsageToolbar
              CodexUsageToolbar --once [--ssh-host HOST] [--remote-command COMMAND] [--identity-file PATH] [--port 22]
              CodexUsageToolbar --once --json-file PATH

            Environment:
              CODEX_USAGE_SSH_HOST
              CODEX_USAGE_REMOTE_COMMAND
              CODEX_USAGE_IDENTITY_FILE

            Config:
              settings.json in the current directory or exe directory.
            """;

        public static AppOptions Parse(string[] args)
        {
            var settings = SettingsStore.Load();
            var once = false;
            var showHelp = false;
            var sshHost = Environment.GetEnvironmentVariable("CODEX_USAGE_SSH_HOST") ?? settings.SshHost ?? DefaultHost;
            var remoteCommand = Environment.GetEnvironmentVariable("CODEX_USAGE_REMOTE_COMMAND") ?? settings.RemoteCommand ?? DefaultRemoteCommand;
            var jsonFile = settings.JsonFile;
            string? identityFile = Environment.GetEnvironmentVariable("CODEX_USAGE_IDENTITY_FILE") ?? settings.IdentityFile;
            var port = settings.Port ?? 22;
            var connectTimeoutSeconds = settings.ConnectTimeoutSeconds ?? 3;
            var commandTimeoutSeconds = settings.CommandTimeoutSeconds ?? 5;
            var pollIntervalSeconds = settings.PollIntervalSeconds ?? 60;
            var windowX = settings.Window?.X;
            var windowY = settings.Window?.Y;
            var windowWidth = settings.Window?.Width ?? 390;
            var windowHeight = settings.Window?.Height ?? 190;
            var opacity = settings.Opacity ?? 0.95;
            var alwaysOnTop = settings.AlwaysOnTop ?? true;
            var startHidden = settings.StartHidden ?? false;
            var clickThrough = settings.ClickThrough ?? false;
            var startWithWindows = settings.StartWithWindows ?? false;
            var accentColor = settings.AccentColor ?? ThemePalette.DefaultKey;
            var backgroundMode = ResolveChoice(settings.BackgroundMode, "dark", "dark", "light");
            var quotaLayout = ResolveChoice(settings.QuotaLayout, "ring", "ring", "bar");

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--once":
                        once = true;
                        break;
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;
                    case "--ssh-host":
                        sshHost = RequireValue(args, ref i, arg);
                        break;
                    case "--remote-command":
                        remoteCommand = RequireValue(args, ref i, arg);
                        break;
                    case "--json-file":
                        jsonFile = RequireValue(args, ref i, arg);
                        break;
                    case "--identity-file":
                        identityFile = RequireValue(args, ref i, arg);
                        break;
                    case "--port":
                        port = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--connect-timeout":
                        connectTimeoutSeconds = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--command-timeout":
                        commandTimeoutSeconds = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--poll-interval":
                        pollIntervalSeconds = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {arg}");
                }
            }

            pollIntervalSeconds = Math.Clamp(pollIntervalSeconds, 5, 3600);
            commandTimeoutSeconds = Math.Clamp(commandTimeoutSeconds, 1, 120);
            connectTimeoutSeconds = Math.Clamp(connectTimeoutSeconds, 1, 60);
            windowWidth = Math.Clamp(windowWidth, 320, 900);
            windowHeight = Math.Clamp(windowHeight, 180, 700);
            opacity = Math.Clamp(opacity, 0.35, 1.0);

            return new AppOptions(
                once,
                showHelp,
                sshHost,
                remoteCommand,
                string.IsNullOrWhiteSpace(jsonFile) ? null : jsonFile,
                string.IsNullOrWhiteSpace(identityFile) ? null : identityFile,
                port,
                connectTimeoutSeconds,
                commandTimeoutSeconds,
                pollIntervalSeconds,
                windowX,
                windowY,
                windowWidth,
                windowHeight,
                opacity,
                alwaysOnTop,
                startHidden,
                clickThrough,
                startWithWindows,
                ThemePalette.Resolve(accentColor).Key,
                backgroundMode,
                quotaLayout);
        }

        public static string ResolveChoice(string? value, string fallback, params string[] allowed)
        {
            foreach (var item in allowed)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(value, item))
                {
                    return item;
                }
            }

            return fallback;
        }

        private static string RequireValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{name} requires a value");
            }

            index++;
            return args[index];
        }

        private static int ParsePositiveInt(string raw, string name)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                throw new ArgumentException($"{name} must be a positive integer");
            }

            return value;
        }
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly Program.AppOptions _options;
    private readonly NotifyIcon _trayIcon;
    private readonly FloatingUsageWindow _window;
    private readonly System.Windows.Forms.Timer _timer;
    private ToolStripMenuItem? _clickThroughItem;
    private ToolStripMenuItem? _startWithWindowsItem;
    private ToolStripMenuItem? _alwaysOnTopItem;
    private bool _refreshing;

    public TrayAppContext(Program.AppOptions options)
    {
        _options = options;
        _window = new FloatingUsageWindow(options);
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Codex Usage Toolbar",
            Visible = true,
        };
        _trayIcon.ContextMenuStrip = BuildMenu();
        _trayIcon.DoubleClick += (_, _) => ToggleWindow();
        _window.RefreshRequested += async (_, _) => await RefreshAsync(force: true);
        if (options.StartWithWindows && !StartupManager.IsEnabled())
        {
            StartupManager.SetEnabled(true);
            if (_startWithWindowsItem is not null)
            {
                _startWithWindowsItem.Checked = true;
            }
        }

        _timer = new System.Windows.Forms.Timer { Interval = options.PollIntervalSeconds * 1000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        if (!options.StartHidden)
        {
            _window.Show();
        }

        _ = RefreshAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleWindow());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync(force: true));
        menu.Items.Add(new ToolStripSeparator());
        _clickThroughItem = new ToolStripMenuItem("Click-through") { CheckOnClick = true, Checked = _options.ClickThrough };
        _clickThroughItem.CheckedChanged += (_, _) =>
        {
            _window.SetClickThrough(_clickThroughItem.Checked);
            SettingsStore.SaveRuntimeFlags(clickThrough: _clickThroughItem.Checked);
        };
        menu.Items.Add(_clickThroughItem);

        _alwaysOnTopItem = new ToolStripMenuItem("Always on top") { CheckOnClick = true, Checked = _options.AlwaysOnTop };
        _alwaysOnTopItem.CheckedChanged += (_, _) =>
        {
            _window.TopMost = _alwaysOnTopItem.Checked;
            SettingsStore.SaveRuntimeFlags(alwaysOnTop: _alwaysOnTopItem.Checked);
        };
        menu.Items.Add(_alwaysOnTopItem);

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartupManager.IsEnabled() };
        _startWithWindowsItem.CheckedChanged += (_, _) =>
        {
            StartupManager.SetEnabled(_startWithWindowsItem.Checked);
            SettingsStore.SaveRuntimeFlags(startWithWindows: _startWithWindowsItem.Checked);
        };
        menu.Items.Add(_startWithWindowsItem);

        var themeMenu = new ToolStripMenuItem("Theme color");
        foreach (var theme in ThemePalette.Options)
        {
            var item = new ToolStripMenuItem(theme.Label)
            {
                Checked = StringComparer.OrdinalIgnoreCase.Equals(theme.Key, _options.AccentColor),
                Tag = theme.Key,
            };
            item.Click += (_, _) =>
            {
                var key = (string)item.Tag!;
                _window.SetAccent(ThemePalette.Resolve(key));
                SettingsStore.SaveRuntimeFlags(accentColor: key);
                foreach (ToolStripItem sibling in themeMenu.DropDownItems)
                {
                    if (sibling is ToolStripMenuItem siblingItem)
                    {
                        siblingItem.Checked = ReferenceEquals(siblingItem, item);
                    }
                }
            };
            themeMenu.DropDownItems.Add(item);
        }

        menu.Items.Add(themeMenu);
        menu.Items.Add("Open settings file", null, (_, _) => SettingsStore.OpenSettingsFile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void ToggleWindow()
    {
        if (_window.Visible)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
            _window.Activate();
        }
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_refreshing && !force)
        {
            return;
        }

        _refreshing = true;
        _window.SetLoading();
        try
        {
            var rawJson = _options.JsonFile is null
                ? await new SshCommandClient(_options).RunAsync()
                : await File.ReadAllTextAsync(_options.JsonFile);
            var state = UsageJsonParser.Parse(rawJson);
            LastGoodStore.Save(rawJson);
            _window.SetState(state, stale: false, error: null);
            _trayIcon.Text = UsageFormatter.FormatTrayText(state, stale: false);
        }
        catch (Exception ex)
        {
            var lastGood = LastGoodStore.Load();
            if (lastGood is not null)
            {
                try
                {
                    var state = UsageJsonParser.Parse(lastGood);
                    _window.SetState(state, stale: true, error: ex.Message);
                    _trayIcon.Text = UsageFormatter.FormatTrayText(state, stale: true);
                    return;
                }
                catch
                {
                    // Fall through to direct error display.
                }
            }

            _window.SetError(ex.Message);
            _trayIcon.Text = "Codex Usage Toolbar - refresh failed";
        }
        finally
        {
            _refreshing = false;
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _window.SaveWindowSettings();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _window.Dispose();
        base.ExitThreadCore();
    }
}

internal static class SettingsStore
{
    public static string PreferredSettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        foreach (var path in GetCandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        return new AppSettings();
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        yield return Path.Combine(Environment.CurrentDirectory, "settings.json");

        var exeDirectory = AppContext.BaseDirectory;
        var exeSettings = Path.Combine(exeDirectory, "settings.json");
        if (!string.Equals(exeSettings, Path.Combine(Environment.CurrentDirectory, "settings.json"), StringComparison.OrdinalIgnoreCase))
        {
            yield return exeSettings;
        }
    }

    public static void OpenSettingsFile()
    {
        var path = GetCandidatePaths().FirstOrDefault(File.Exists) ?? PreferredSettingsPath;
        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings(), JsonOptions), Encoding.UTF8);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    public static void SaveWindowBounds(Rectangle bounds)
    {
        Save(settings =>
        {
            settings.Window ??= new WindowSettings();
            settings.Window.X = bounds.X;
            settings.Window.Y = bounds.Y;
            settings.Window.Width = bounds.Width;
            settings.Window.Height = bounds.Height;
        });
    }

    public static void SaveRuntimeFlags(
        bool? clickThrough = null,
        bool? alwaysOnTop = null,
        bool? startWithWindows = null,
        string? accentColor = null,
        string? backgroundMode = null,
        string? quotaLayout = null)
    {
        Save(settings =>
        {
            if (clickThrough is not null) settings.ClickThrough = clickThrough;
            if (alwaysOnTop is not null) settings.AlwaysOnTop = alwaysOnTop;
            if (startWithWindows is not null) settings.StartWithWindows = startWithWindows;
            if (!string.IsNullOrWhiteSpace(accentColor)) settings.AccentColor = ThemePalette.Resolve(accentColor).Key;
            if (!string.IsNullOrWhiteSpace(backgroundMode)) settings.BackgroundMode = Program.AppOptions.ResolveChoice(backgroundMode, "dark", "dark", "light");
            if (!string.IsNullOrWhiteSpace(quotaLayout)) settings.QuotaLayout = Program.AppOptions.ResolveChoice(quotaLayout, "ring", "ring", "bar");
        });
    }

    private static void Save(Action<AppSettings> update)
    {
        var path = GetCandidatePaths().FirstOrDefault(File.Exists) ?? PreferredSettingsPath;
        var settings = Load();
        update(settings);
        var options = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(settings, options), Encoding.UTF8);
    }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexUsageToolbar";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

internal sealed class AppSettings
{
    public string? SshHost { get; set; }
    public string? RemoteCommand { get; set; }
    public string? IdentityFile { get; set; }
    public string? JsonFile { get; set; }
    public int? Port { get; set; }
    public int? PollIntervalSeconds { get; set; }
    public int? ConnectTimeoutSeconds { get; set; }
    public int? CommandTimeoutSeconds { get; set; }
    public double? Opacity { get; set; }
    public bool? AlwaysOnTop { get; set; }
    public bool? StartHidden { get; set; }
    public bool? ClickThrough { get; set; }
    public bool? StartWithWindows { get; set; }
    public string? AccentColor { get; set; }
    public string? BackgroundMode { get; set; }
    public string? QuotaLayout { get; set; }
    public WindowSettings? Window { get; set; }
}

internal sealed record ThemeOption(string Key, string Label, Color Accent);

internal static class ThemePalette
{
    public const string DefaultKey = "cyan";

    public static readonly ThemeOption[] Options =
    [
        new("cyan", "Cyan", Color.FromArgb(65, 235, 210)),
        new("blue", "Blue", Color.FromArgb(92, 166, 255)),
        new("green", "Green", Color.FromArgb(97, 218, 139)),
        new("purple", "Purple", Color.FromArgb(185, 132, 255)),
        new("amber", "Amber", Color.FromArgb(245, 182, 84)),
    ];

    public static ThemeOption Resolve(string? key)
    {
        foreach (var option in Options)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(option.Key, key))
            {
                return option;
            }
        }

        return Options[0];
    }

    public static Color WithAlpha(Color color, int alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}

internal sealed class WindowSettings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

internal sealed class FloatingUsageWindow : Form
{
    private const int CompactHeight = 190;
    private const int ExpandedHeight = 360;
    private const int ResizeGrip = 8;
    private const int WmNcHitTest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int WsExTransparent = 0x20;
    private const int GwlExStyle = -20;

    private UsageState? _state;
    private bool _loading;
    private bool _stale;
    private string? _error;
    private Point _dragStart;
    private bool _dragging;
    private bool _expanded;
    private bool _clickThrough;
    private bool _lightMode;
    private bool _barLayout;
    private Rectangle _modeButton;
    private Rectangle _layoutButton;
    private Rectangle _toggleButton;
    private Rectangle _refreshButton;
    private int _compactHeight = CompactHeight;
    private int _expandedHeight = ExpandedHeight;
    private ThemeOption _theme;

    public event EventHandler? RefreshRequested;

    public FloatingUsageWindow(Program.AppOptions options)
    {
        _theme = ThemePalette.Resolve(options.AccentColor);
        Text = "Codex Usage";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(options.WindowWidth, options.WindowHeight);
        MinimumSize = new Size(360, CompactHeight);
        TopMost = options.AlwaysOnTop;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(9, 14, 24);
        Location = ResolveLocation(options);
        Opacity = options.Opacity;
        _clickThrough = options.ClickThrough;
        _lightMode = StringComparer.OrdinalIgnoreCase.Equals(options.BackgroundMode, "light");
        _barLayout = StringComparer.OrdinalIgnoreCase.Equals(options.QuotaLayout, "bar");
        _compactHeight = Math.Max(CompactHeight, Height);
        _expandedHeight = Math.Max(ExpandedHeight, Height);
    }

    private static Point ResolveLocation(Program.AppOptions options)
    {
        if (options.WindowX is not null && options.WindowY is not null)
        {
            return new Point(options.WindowX.Value, options.WindowY.Value);
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return new Point(area.Right - options.WindowWidth - 28, area.Top + 80);
    }

    public void SetLoading()
    {
        _loading = true;
        Invalidate();
    }

    public void SetState(UsageState state, bool stale, string? error)
    {
        _state = state;
        _stale = stale;
        _error = error;
        _loading = false;
        Invalidate();
    }

    public void SetError(string error)
    {
        _state = null;
        _error = error;
        _loading = false;
        Invalidate();
    }

    public void SaveWindowSettings()
    {
        SettingsStore.SaveWindowBounds(Bounds);
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        ApplyClickThrough();
    }

    public void SetAccent(ThemeOption theme)
    {
        _theme = theme;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsInResizeZone(e.Location))
        {
            return;
        }

        if (_toggleButton.Contains(e.Location))
        {
            ToggleExpanded();
            return;
        }

        if (_modeButton.Contains(e.Location))
        {
            ToggleBackgroundMode();
            return;
        }

        if (_layoutButton.Contains(e.Location))
        {
            ToggleQuotaLayout();
            return;
        }

        if (_refreshButton.Contains(e.Location))
        {
            RefreshRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStart = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Left += e.X - _dragStart.X;
            Top += e.Y - _dragStart.Y;
        }
        else
        {
            Cursor = GetResizeCursor(e.Location);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyClickThrough();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_expanded)
        {
            _expandedHeight = Math.Max(ExpandedHeight, Height);
        }
        else
        {
            _compactHeight = Math.Max(CompactHeight, Height);
        }

        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest && !_clickThrough)
        {
            base.WndProc(ref m);
            var raw = unchecked((int)m.LParam.ToInt64());
            var p = PointToClient(new Point((short)(raw & 0xFFFF), (short)(raw >> 16)));
            var left = p.X <= ResizeGrip;
            var right = p.X >= Width - ResizeGrip;
            var top = p.Y <= ResizeGrip;
            var bottom = p.Y >= Height - ResizeGrip;

            if (left && top) { m.Result = HtTopLeft; return; }
            if (right && top) { m.Result = HtTopRight; return; }
            if (left && bottom) { m.Result = HtBottomLeft; return; }
            if (right && bottom) { m.Result = HtBottomRight; return; }
            if (left) { m.Result = HtLeft; return; }
            if (right) { m.Result = HtRight; return; }
            if (top) { m.Result = HtTop; return; }
            if (bottom) { m.Result = HtBottom; return; }
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawBackground(g);
        DrawResizeHint(g);
        DrawHeader(g);

        if (_state is null)
        {
            DrawCenteredMessage(g, _loading ? "Refreshing..." : "No data", _error);
            return;
        }

        DrawQuotaPanel(g, _state);
        if (_expanded)
        {
            DrawTokenTable(g, _state);
        }

        DrawFooter(g, _state);
    }

    private void DrawBackground(Graphics g)
    {
        var start = _lightMode ? Color.FromArgb(244, 248, 251) : Color.FromArgb(10, 15, 28);
        var end = _lightMode ? Color.FromArgb(224, 235, 242) : Color.FromArgb(18, 30, 46);
        using var bg = new LinearGradientBrush(ClientRectangle, start, end, 35f);
        g.FillRectangle(bg, ClientRectangle);
        using var border = new Pen(ThemePalette.WithAlpha(_theme.Accent, _lightMode ? 135 : 85), 1);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private void DrawResizeHint(Graphics g)
    {
        if (_clickThrough)
        {
            return;
        }

        using var pen = new Pen(ThemePalette.WithAlpha(_theme.Accent, 95), 1);
        var x = Width - 18;
        var y = Height - 18;
        g.DrawLine(pen, x + 10, y + 2, x + 2, y + 10);
        g.DrawLine(pen, x + 14, y + 6, x + 6, y + 14);
    }

    private void DrawHeader(Graphics g)
    {
        using var titleFont = new Font("Segoe UI Semibold", 12f);
        using var metaFont = new Font("Segoe UI", 8.5f);
        using var titleBrush = new SolidBrush(PrimaryTextColor());
        using var metaBrush = new SolidBrush(MutedTextColor());

        var buttonY = 14;
        var gap = 6;
        _refreshButton = new Rectangle(Width - 82, buttonY, 64, 26);
        _toggleButton = new Rectangle(_refreshButton.Left - 72 - gap, buttonY, 72, 26);
        _layoutButton = new Rectangle(_toggleButton.Left - 52 - gap, buttonY, 52, 26);
        _modeButton = new Rectangle(_layoutButton.Left - 56 - gap, buttonY, 56, 26);

        var titleWidth = Math.Max(38, _modeButton.Left - 26);
        g.DrawString(TrimToWidth(g, "Codex Usage", titleFont, titleWidth), titleFont, titleBrush, 18, 14);
        g.DrawString(_stale ? "STALE" : _loading ? "SYNC" : "LIVE", metaFont, metaBrush, 18, 42);

        DrawHeaderButton(g, _modeButton, _lightMode ? "Dark" : "Light");
        DrawHeaderButton(g, _layoutButton, _barLayout ? "Ring" : "Bar");
        DrawHeaderButton(g, _toggleButton, _expanded ? "Hide" : "Tokens");
        DrawHeaderButton(g, _refreshButton, "Refresh");
    }

    private void DrawHeaderButton(Graphics g, Rectangle bounds, string label)
    {
        using var buttonBrush = new SolidBrush(_lightMode ? Color.FromArgb(232, 241, 246) : Color.FromArgb(32, 58, 78));
        using var buttonBorder = new Pen(ThemePalette.WithAlpha(_theme.Accent, _lightMode ? 190 : 160), 1);
        using var buttonText = new SolidBrush(_lightMode ? Color.FromArgb(24, 45, 58) : ThemePalette.WithAlpha(_theme.Accent, 245));
        using var buttonFont = new Font("Segoe UI Semibold", 8.5f);
        g.FillRoundedRectangle(buttonBrush, bounds, 6);
        g.DrawRoundedRectangle(buttonBorder, bounds, 6);
        var labelSize = g.MeasureString(label, buttonFont);
        g.DrawString(label, buttonFont, buttonText, bounds.Left + (bounds.Width - labelSize.Width) / 2, bounds.Top + 5);
    }

    private void DrawQuotaPanel(Graphics g, UsageState state)
    {
        if (_barLayout)
        {
            DrawQuotaBars(g, state);
            return;
        }

        var top = 72;
        var panelHeight = Math.Min(112, Height - 108);
        var half = (Width - 40) / 2;
        DrawQuotaCircle(g, new Rectangle(20, top, half - 8, panelHeight), "5h", state.Quota.FiveHour);
        DrawQuotaCircle(g, new Rectangle(28 + half, top, half - 8, panelHeight), "Week", state.Quota.Weekly);
    }

    private void DrawQuotaCircle(Graphics g, Rectangle bounds, string label, QuotaWindow quota)
    {
        var diameter = Math.Min(74, Math.Max(54, bounds.Height - 28));
        var circle = new Rectangle(bounds.Left, bounds.Top + 4, diameter, diameter);
        var percent = quota.Available && quota.RemainingPercent is not null
            ? Math.Clamp(quota.RemainingPercent.Value, 0, 100)
            : 0;
        using var track = new Pen(TrackColor(), 8);
        using var fill = new Pen(quota.Available ? _theme.Accent : Color.FromArgb(76, 91, 108), 8)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(track, circle, -90, 360);
        g.DrawArc(fill, circle, -90, (float)(360 * percent / 100.0));

        using var labelFont = new Font("Segoe UI Semibold", 10f);
        using var valueFont = new Font("Segoe UI Semibold", 17f);
        using var smallFont = new Font("Segoe UI", 8.5f);
        using var titleBrush = new SolidBrush(PrimaryTextColor());
        using var valueBrush = new SolidBrush(PrimaryTextColor());
        using var mutedBrush = new SolidBrush(MutedTextColor());
        var textX = circle.Right + 12;
        g.DrawString(label, labelFont, titleBrush, textX, bounds.Top + 4);
        g.DrawString(UsageFormatter.FormatQuotaForUi(quota), valueFont, valueBrush, textX, bounds.Top + 26);
        var reset = quota.Available ? $"Reset {UsageFormatter.FormatTime(quota.ResetAt)}" : "Unavailable";
        g.DrawString(reset, smallFont, mutedBrush, textX, bounds.Top + 62);
    }

    private void DrawQuotaBars(Graphics g, UsageState state)
    {
        DrawQuotaBar(g, new Rectangle(20, 74, Width - 40, 44), "5h", state.Quota.FiveHour);
        DrawQuotaBar(g, new Rectangle(20, 124, Width - 40, 44), "Week", state.Quota.Weekly);
    }

    private void DrawQuotaBar(Graphics g, Rectangle bounds, string label, QuotaWindow quota)
    {
        var percent = quota.Available && quota.RemainingPercent is not null
            ? Math.Clamp(quota.RemainingPercent.Value, 0, 100)
            : 0;
        using var labelFont = new Font("Segoe UI Semibold", 9.5f);
        using var valueFont = new Font("Segoe UI Semibold", 16f);
        using var smallFont = new Font("Segoe UI", 8f);
        using var titleBrush = new SolidBrush(PrimaryTextColor());
        using var valueBrush = new SolidBrush(PrimaryTextColor());
        using var mutedBrush = new SolidBrush(MutedTextColor());
        g.DrawString(label, labelFont, titleBrush, bounds.Left, bounds.Top);
        var value = UsageFormatter.FormatQuotaForUi(quota);
        var valueSize = g.MeasureString(value, valueFont);
        g.DrawString(value, valueFont, valueBrush, bounds.Right - valueSize.Width, bounds.Top - 3);

        var reset = quota.Available ? $"Reset {UsageFormatter.FormatTime(quota.ResetAt)}" : "Unavailable";
        g.DrawString(reset, smallFont, mutedBrush, bounds.Left + 42, bounds.Top + 2);

        var barTop = bounds.Top + 27;
        var bar = new Rectangle(bounds.Left, barTop, bounds.Width, 10);
        using var trackBrush = new SolidBrush(TrackColor());
        using var fillBrush = new SolidBrush(quota.Available ? _theme.Accent : DisabledColor());
        g.FillRoundedRectangle(trackBrush, bar, 5);
        var fillWidth = Math.Max(0, (int)Math.Round(bar.Width * percent / 100.0));
        if (fillWidth > 0)
        {
            var fill = new Rectangle(bar.Left, bar.Top, fillWidth, bar.Height);
            if (fillWidth < bar.Height)
            {
                g.FillRectangle(fillBrush, fill);
            }
            else
            {
                g.FillRoundedRectangle(fillBrush, fill, 5);
            }
        }
    }

    private void DrawTokenTable(Graphics g, UsageState state)
    {
        var y = 194;
        if (Height < 295)
        {
            return;
        }

        using var line = new Pen(TrackColor(), 1);
        g.DrawLine(line, 20, y - 12, Width - 20, y - 12);
        using var headFont = new Font("Segoe UI Semibold", 8.5f);
        using var rowFont = new Font("Segoe UI", 8.5f);
        using var headBrush = new SolidBrush(ThemePalette.WithAlpha(_theme.Accent, 220));
        using var rowBrush = new SolidBrush(PrimaryTextColor());
        using var mutedBrush = new SolidBrush(MutedTextColor());
        var cols = GetTableColumns();
        DrawCells(g, headFont, headBrush, y, cols, "Range", "Tokens", "Hit", "Hit%", "Req", "Cost");
        y += 24;
        foreach (var window in new[] { "today", "1d", "7d", "14d", "30d" })
        {
            var item = state.Tokens[window];
            DrawCells(
                g,
                rowFont,
                window == "today" ? rowBrush : mutedBrush,
                y,
                cols,
                UsageFormatter.FormatWindowName(window),
                UsageFormatter.FormatTokens(item.TotalTokens),
                UsageFormatter.FormatTokens(item.HitTokens),
                UsageFormatter.FormatPercent(item.HitRate),
                item.Requests.ToString(CultureInfo.InvariantCulture),
                UsageFormatter.FormatCost(item.TotalCostUsd));
            y += 22;
        }
    }

    private int[] GetTableColumns()
    {
        var left = 22;
        var usable = Math.Max(340, Width - 44);
        return
        [
            left,
            left + (int)(usable * 0.16),
            left + (int)(usable * 0.36),
            left + (int)(usable * 0.55),
            left + (int)(usable * 0.70),
            left + (int)(usable * 0.82),
        ];
    }

    private static void DrawCells(Graphics g, Font font, Brush brush, int y, int[] x, params string[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            g.DrawString(values[i], font, brush, x[i], y);
        }
    }

    private void DrawFooter(Graphics g, UsageState state)
    {
        using var font = new Font("Segoe UI", 8f);
        using var brush = new SolidBrush(_error is null ? MutedTextColor() : Color.FromArgb(210, 106, 35));
        var text = _error is null
            ? $"Refresh {UsageFormatter.FormatTime(state.LastSuccessAt)}  Event {UsageFormatter.FormatTime(state.LastEventAt)}"
            : $"Using last good data - {_error}";
        g.DrawString(TrimToWidth(g, text, font, Width - 36), font, brush, 18, Height - 28);
    }

    private void ToggleBackgroundMode()
    {
        _lightMode = !_lightMode;
        SettingsStore.SaveRuntimeFlags(backgroundMode: _lightMode ? "light" : "dark");
        Invalidate();
    }

    private void ToggleQuotaLayout()
    {
        _barLayout = !_barLayout;
        SettingsStore.SaveRuntimeFlags(quotaLayout: _barLayout ? "bar" : "ring");
        Invalidate();
    }

    private void ToggleExpanded()
    {
        if (_expanded)
        {
            _expandedHeight = Math.Max(ExpandedHeight, Height);
            _expanded = false;
            Height = Math.Max(CompactHeight, _compactHeight);
        }
        else
        {
            _compactHeight = Math.Max(CompactHeight, Height);
            _expanded = true;
            Height = Math.Max(ExpandedHeight, _expandedHeight);
        }

        Invalidate();
    }

    private Color PrimaryTextColor() => _lightMode ? Color.FromArgb(20, 34, 46) : Color.FromArgb(232, 248, 255);

    private Color MutedTextColor() => _lightMode ? Color.FromArgb(82, 104, 120) : Color.FromArgb(140, 160, 174);

    private Color TrackColor() => _lightMode ? Color.FromArgb(198, 214, 224) : Color.FromArgb(38, 62, 80);

    private Color DisabledColor() => _lightMode ? Color.FromArgb(150, 164, 176) : Color.FromArgb(76, 91, 108);

    private bool IsInResizeZone(Point p)
    {
        return p.X <= ResizeGrip ||
               p.X >= Width - ResizeGrip ||
               p.Y <= ResizeGrip ||
               p.Y >= Height - ResizeGrip;
    }

    private Cursor GetResizeCursor(Point p)
    {
        if (_clickThrough)
        {
            return Cursors.Default;
        }

        var left = p.X <= ResizeGrip;
        var right = p.X >= Width - ResizeGrip;
        var top = p.Y <= ResizeGrip;
        var bottom = p.Y >= Height - ResizeGrip;

        if ((left && top) || (right && bottom))
        {
            return Cursors.SizeNWSE;
        }

        if ((right && top) || (left && bottom))
        {
            return Cursors.SizeNESW;
        }

        if (left || right)
        {
            return Cursors.SizeWE;
        }

        if (top || bottom)
        {
            return Cursors.SizeNS;
        }

        return Cursors.Default;
    }

    private void ApplyClickThrough()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var style = GetWindowLong(Handle, GwlExStyle);
        style = _clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(Handle, GwlExStyle, style);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void DrawCenteredMessage(Graphics g, string message, string? detail)
    {
        using var font = new Font("Segoe UI Semibold", 10f);
        using var detailFont = new Font("Segoe UI", 8.5f);
        using var brush = new SolidBrush(PrimaryTextColor());
        using var muted = new SolidBrush(MutedTextColor());
        g.DrawString(message, font, brush, 20, 82);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            g.DrawString(TrimToWidth(g, detail, detailFont, Width - 40), detailFont, muted, 20, 110);
        }
    }

    private static string TrimToWidth(Graphics g, string text, Font font, int width)
    {
        if (g.MeasureString(text, font).Width <= width)
        {
            return text;
        }

        while (text.Length > 4 && g.MeasureString(text + "...", font).Width > width)
        {
            text = text[..^1];
        }

        return text + "...";
    }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedPath(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = CreateRoundedPath(bounds, radius);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return (GraphicsPath)path.Clone();
    }
}

internal sealed class SshCommandClient(Program.AppOptions options)
{
    public async Task<string> RunAsync()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("BatchMode=yes");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add($"ConnectTimeout={options.ConnectTimeoutSeconds}");
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(options.Port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.IdentityFile))
        {
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(options.IdentityFile);
        }

        process.StartInfo.ArgumentList.Add(options.SshHost);
        process.StartInfo.ArgumentList.Add(options.RemoteCommand);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ssh process");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.CommandTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw new TimeoutException($"SSH command timed out after {options.CommandTimeoutSeconds} seconds");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh failed with exit code {process.ExitCode}: {stderr.ToString().Trim()}");
        }

        var json = stdout.ToString().Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("exporter returned empty output");
        }

        return json;
    }
}

internal static class LastGoodStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageToolbar");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "last-good.json");

    public static void Save(string json)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, json, Encoding.UTF8);
    }

    public static string? Load()
    {
        return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8) : null;
    }
}

internal static class UsageJsonParser
{
    private static readonly string[] RequiredWindows = ["today", "1d", "7d", "14d", "30d"];

    public static UsageState Parse(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            RequireString(root, "schema_version", out var schemaVersion);
            if (schemaVersion != "1.0")
            {
                throw new InvalidOperationException($"unsupported schema_version: {schemaVersion}");
            }

            RequireString(root, "app", out var app);
            if (app != "codex")
            {
                throw new InvalidOperationException($"expected app=codex, got app={app}");
            }

            var collectedAt = RequireDateTime(root, "collected_at");
            var quota = new QuotaState(
                ParseQuotaWindow(root.GetProperty("quota").GetProperty("five_hour")),
                ParseQuotaWindow(root.GetProperty("quota").GetProperty("weekly")));
            var tokensRoot = root.GetProperty("tokens");
            var tokenWindows = new Dictionary<string, TokenWindow>(StringComparer.Ordinal);
            foreach (var window in RequiredWindows)
            {
                if (!tokensRoot.TryGetProperty(window, out var item))
                {
                    throw new InvalidOperationException($"missing token window: {window}");
                }

                tokenWindows[window] = new TokenWindow(
                    RequireInt64(item, "total_tokens"),
                    RequireInt64(item, "hit_tokens"),
                    RequireNullableDouble(item, "hit_rate") ?? 0,
                    RequireInt32(item, "requests"),
                    RequireNullableDecimal(item, "total_cost_usd") ?? 0m);
            }

            var refreshRoot = root.GetProperty("refresh");
            return new UsageState(
                collectedAt,
                quota,
                tokenWindows,
                RequireDateTime(refreshRoot, "last_success_at"),
                OptionalDateTime(refreshRoot, "cc_switch_last_event_at"),
                RequireBool(refreshRoot, "stale"));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"invalid JSON: {ex.Message}", ex);
        }
    }

    private static QuotaWindow ParseQuotaWindow(JsonElement item)
    {
        return new QuotaWindow(
            RequireBool(item, "available"),
            RequireNullableDouble(item, "remaining_percent"),
            OptionalDateTime(item, "reset_at"));
    }

    private static void RequireString(JsonElement root, string name, out string value)
    {
        if (!root.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"missing or invalid string field: {name}");
        }

        value = item.GetString() ?? "";
    }

    private static DateTimeOffset RequireDateTime(JsonElement root, string name)
    {
        return OptionalDateTime(root, name) ?? throw new InvalidOperationException($"missing or invalid datetime field: {name}");
    }

    private static DateTimeOffset? OptionalDateTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (item.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(item.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            throw new InvalidOperationException($"invalid datetime field: {name}");
        }

        return value;
    }

    private static bool RequireBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var item) ||
            (item.ValueKind != JsonValueKind.True && item.ValueKind != JsonValueKind.False))
        {
            throw new InvalidOperationException($"missing or invalid boolean field: {name}");
        }

        return item.GetBoolean();
    }

    private static int RequireInt32(JsonElement root, string name) => checked((int)RequireInt64(root, name));

    private static long RequireInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var item) || !item.TryGetInt64(out var value))
        {
            throw new InvalidOperationException($"missing or invalid integer field: {name}");
        }

        return value;
    }

    private static double? RequireNullableDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var item))
        {
            throw new InvalidOperationException($"missing numeric field: {name}");
        }

        return item.ValueKind == JsonValueKind.Null ? null : item.GetDouble();
    }

    private static decimal? RequireNullableDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var item))
        {
            throw new InvalidOperationException($"missing numeric field: {name}");
        }

        return item.ValueKind == JsonValueKind.Null ? null : item.GetDecimal();
    }
}

internal sealed record UsageState(
    DateTimeOffset CollectedAt,
    QuotaState Quota,
    IReadOnlyDictionary<string, TokenWindow> Tokens,
    DateTimeOffset LastSuccessAt,
    DateTimeOffset? LastEventAt,
    bool Stale);

internal sealed record QuotaState(QuotaWindow FiveHour, QuotaWindow Weekly);

internal sealed record QuotaWindow(bool Available, double? RemainingPercent, DateTimeOffset? ResetAt);

internal sealed record TokenWindow(long TotalTokens, long HitTokens, double HitRate, int Requests, decimal TotalCostUsd);

internal static class UsageFormatter
{
    public static string FormatConsole(UsageState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"5h left {FormatQuotaForUi(state.Quota.FiveHour)}, reset {FormatTime(state.Quota.FiveHour.ResetAt)}");
        sb.AppendLine($"Week left {FormatQuotaForUi(state.Quota.Weekly)}, reset {FormatTime(state.Quota.Weekly.ResetAt)}");
        foreach (var window in new[] { "today", "1d", "7d", "14d", "30d" })
        {
            var item = state.Tokens[window];
            sb.AppendLine($"{FormatWindowName(window)} {FormatTokens(item.TotalTokens)} tokens, hit {FormatTokens(item.HitTokens)}, hit {FormatPercent(item.HitRate)}, req {item.Requests}, cost {FormatCost(item.TotalCostUsd)}");
        }

        sb.AppendLine($"Last refresh {FormatTime(state.LastSuccessAt)}");
        if (state.LastEventAt is not null)
        {
            sb.AppendLine($"Last cc-switch event {FormatTime(state.LastEventAt.Value)}");
        }

        return sb.ToString();
    }

    public static string FormatTrayText(UsageState state, bool stale)
    {
        var today = state.Tokens["today"];
        return TrimForNotifyIcon($"Codex {(stale ? "STALE" : "LIVE")} - Today {FormatTokens(today.TotalTokens)}, {today.Requests} req, {FormatCost(today.TotalCostUsd)}");
    }

    public static string FormatWindowName(string window) => window == "today" ? "Today" : window;

    public static string FormatTokens(long value)
    {
        var abs = Math.Abs(value);
        if (abs < 1_000) return value.ToString("0", CultureInfo.InvariantCulture);
        if (abs < 1_000_000) return $"{value / 1_000d:0.#}k";
        if (abs < 1_000_000_000) return $"{value / 1_000_000d:0.#}M";
        return $"{value / 1_000_000_000d:0.#}B";
    }

    public static string FormatCost(decimal value)
    {
        return value >= 100m
            ? "$" + Math.Round(value, 0).ToString("0", CultureInfo.InvariantCulture)
            : "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static string FormatQuotaForUi(QuotaWindow quota)
    {
        return !quota.Available || quota.RemainingPercent is null
            ? "--"
            : $"{quota.RemainingPercent.Value:0.#}%";
    }

    public static string FormatTime(DateTimeOffset? value)
    {
        if (value is null) return "--";
        var local = value.Value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string FormatPercent(double value)
    {
        return (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private static string TrimForNotifyIcon(string value)
    {
        return value.Length <= 63 ? value : value[..60] + "...";
    }
}
