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
        bool StartHidden)
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
            var windowHeight = settings.Window?.Height ?? 210;
            var opacity = settings.Opacity ?? 0.95;
            var alwaysOnTop = settings.AlwaysOnTop ?? true;
            var startHidden = settings.StartHidden ?? false;

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
                startHidden);
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
            ContextMenuStrip = BuildMenu(),
        };
        _trayIcon.DoubleClick += (_, _) => ToggleWindow();

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
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _window.Dispose();
        base.ExitThreadCore();
    }
}

internal static class SettingsStore
{
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
    public WindowSettings? Window { get; set; }
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
    private UsageState? _state;
    private bool _loading;
    private bool _stale;
    private string? _error;
    private Point _dragStart;
    private bool _dragging;

    public FloatingUsageWindow(Program.AppOptions options)
    {
        Text = "Codex Usage";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(options.WindowWidth, options.WindowHeight);
        TopMost = options.AlwaysOnTop;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(9, 14, 24);
        Location = ResolveLocation(options);
        Opacity = options.Opacity;
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
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
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawBackground(g);
        DrawHeader(g);

        if (_state is null)
        {
            DrawCenteredMessage(g, _loading ? "Refreshing..." : "No data", _error);
            return;
        }

        DrawQuota(g, _state);
        DrawTokenBars(g, _state);
        DrawFooter(g, _state);
    }

    private void DrawBackground(Graphics g)
    {
        using var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(10, 15, 28), Color.FromArgb(18, 30, 46), 35f);
        g.FillRectangle(bg, ClientRectangle);
        using var border = new Pen(Color.FromArgb(80, 80, 220, 230), 1);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private void DrawHeader(Graphics g)
    {
        using var titleFont = new Font("Segoe UI Semibold", 12f);
        using var metaFont = new Font("Segoe UI", 8.5f);
        using var titleBrush = new SolidBrush(Color.FromArgb(232, 248, 255));
        using var metaBrush = new SolidBrush(Color.FromArgb(125, 190, 205));
        g.DrawString("Codex Usage", titleFont, titleBrush, 18, 14);
        g.DrawString(_stale ? "STALE" : _loading ? "SYNC" : "LIVE", metaFont, metaBrush, Width - 62, 18);
    }

    private void DrawQuota(Graphics g, UsageState state)
    {
        using var labelFont = new Font("Segoe UI", 8.5f);
        using var valueFont = new Font("Segoe UI Semibold", 10f);
        using var muted = new SolidBrush(Color.FromArgb(145, 160, 176));
        using var text = new SolidBrush(Color.FromArgb(232, 248, 255));

        g.DrawString("5h", labelFont, muted, 20, 46);
        g.DrawString(UsageFormatter.FormatQuotaForUi(state.Quota.FiveHour), valueFont, text, 46, 43);
        DrawMiniProgress(g, 115, 50, 70, state.Quota.FiveHour.RemainingPercent);

        g.DrawString("Week", labelFont, muted, 210, 46);
        g.DrawString(UsageFormatter.FormatQuotaForUi(state.Quota.Weekly), valueFont, text, 252, 43);
        DrawMiniProgress(g, 320, 50, 48, state.Quota.Weekly.RemainingPercent);
    }

    private static void DrawMiniProgress(Graphics g, int x, int y, int width, double? value)
    {
        var percent = Math.Clamp(value ?? 0, 0, 100) / 100.0;
        using var track = new Pen(Color.FromArgb(45, 80, 100), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(value is null ? Color.FromArgb(80, 95, 110) : Color.FromArgb(60, 220, 210), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, x, y, x + width, y);
        g.DrawLine(fill, x, y, x + (int)(width * percent), y);
    }

    private void DrawTokenBars(Graphics g, UsageState state)
    {
        var windows = new[] { "today", "1d", "7d", "14d", "30d" };
        var maxTokens = windows.Max(w => Math.Max(1, state.Tokens[w].TotalTokens));
        var y = 76;
        foreach (var window in windows)
        {
            var item = state.Tokens[window];
            DrawTokenRow(g, y, UsageFormatter.FormatWindowName(window), item, maxTokens);
            y += 23;
        }
    }

    private void DrawTokenRow(Graphics g, int y, string label, TokenWindow item, long maxTokens)
    {
        using var labelFont = new Font("Segoe UI Semibold", 8.5f);
        using var valueFont = new Font("Segoe UI", 8.5f);
        using var labelBrush = new SolidBrush(Color.FromArgb(220, 235, 242));
        using var valueBrush = new SolidBrush(Color.FromArgb(148, 168, 180));
        g.DrawString(label, labelFont, labelBrush, 20, y - 4);

        var barX = 82;
        var barY = y + 2;
        var barW = 130;
        var fillW = (int)(barW * Math.Clamp(item.TotalTokens / (double)maxTokens, 0, 1));
        using var track = new SolidBrush(Color.FromArgb(28, 47, 65));
        using var fill = new LinearGradientBrush(new Rectangle(barX, barY, Math.Max(1, fillW), 7), Color.FromArgb(65, 235, 210), Color.FromArgb(82, 145, 255), 0f);
        g.FillRoundedRectangle(track, new Rectangle(barX, barY, barW, 7), 4);
        g.FillRoundedRectangle(fill, new Rectangle(barX, barY, Math.Max(2, fillW), 7), 4);

        DrawHitRing(g, 238, y - 4, item.HitRate);
        g.DrawString($"{UsageFormatter.FormatTokens(item.TotalTokens)}  {UsageFormatter.FormatCost(item.TotalCostUsd)}", valueFont, valueBrush, 270, y - 5);
    }

    private static void DrawHitRing(Graphics g, int x, int y, double hitRate)
    {
        var rect = new Rectangle(x, y, 18, 18);
        using var track = new Pen(Color.FromArgb(38, 62, 80), 3);
        using var fill = new Pen(Color.FromArgb(80, 220, 245), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(track, rect, -90, 360);
        g.DrawArc(fill, rect, -90, (float)(360 * Math.Clamp(hitRate, 0, 1)));
    }

    private void DrawFooter(Graphics g, UsageState state)
    {
        using var font = new Font("Segoe UI", 8f);
        using var brush = new SolidBrush(_error is null ? Color.FromArgb(118, 140, 154) : Color.FromArgb(255, 180, 120));
        var text = _error is null
            ? $"Refresh {UsageFormatter.FormatTime(state.LastSuccessAt)}  Event {UsageFormatter.FormatTime(state.LastEventAt)}"
            : $"Using last good data - {_error}";
        g.DrawString(TrimToWidth(g, text, font, Width - 36), font, brush, 18, Height - 28);
    }

    private void DrawCenteredMessage(Graphics g, string message, string? detail)
    {
        using var font = new Font("Segoe UI Semibold", 10f);
        using var detailFont = new Font("Segoe UI", 8.5f);
        using var brush = new SolidBrush(Color.FromArgb(218, 238, 244));
        using var muted = new SolidBrush(Color.FromArgb(145, 160, 176));
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
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
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

    private static string FormatPercent(double value)
    {
        return (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private static string TrimForNotifyIcon(string value)
    {
        return value.Length <= 63 ? value : value[..60] + "...";
    }
}
