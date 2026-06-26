using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexUsageToolbar;

internal static class Program
{
    private const string DefaultHost = "codex-vm";
    private const string DefaultRemoteCommand = "~/.local/bin/ccswitch-export codex --windows today,1d,7d,14d,30d --json";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = AppOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(AppOptions.HelpText);
                return 0;
            }

            if (!options.Once)
            {
                Console.Error.WriteLine("Only --once is implemented in Phase 2.");
                Console.WriteLine(AppOptions.HelpText);
                return 2;
            }

            var rawJson = options.JsonFile is null
                ? await new SshCommandClient(options).RunAsync()
                : await File.ReadAllTextAsync(options.JsonFile);
            var state = UsageJsonParser.Parse(rawJson);

            Console.Write(UsageFormatter.Format(state));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private sealed record AppOptions(
        bool Once,
        bool ShowHelp,
        string SshHost,
        string RemoteCommand,
        string? JsonFile,
        string? IdentityFile,
        int Port,
        int ConnectTimeoutSeconds,
        int CommandTimeoutSeconds)
    {
        public static string HelpText =>
            """
            Usage:
              CodexUsageToolbar --once [--ssh-host HOST] [--remote-command COMMAND] [--identity-file PATH] [--port 22]
              CodexUsageToolbar --once --json-file PATH

            Examples:
              CodexUsageToolbar --once --ssh-host jiaming@192.168.32.123
              CodexUsageToolbar --once --ssh-host codex-vm
              CodexUsageToolbar --once --json-file sample.json
            """;

        public static AppOptions Parse(string[] args)
        {
            var once = false;
            var showHelp = false;
            var sshHost = Environment.GetEnvironmentVariable("CODEX_USAGE_SSH_HOST") ?? DefaultHost;
            var remoteCommand = Environment.GetEnvironmentVariable("CODEX_USAGE_REMOTE_COMMAND") ?? DefaultRemoteCommand;
            string? jsonFile = null;
            string? identityFile = Environment.GetEnvironmentVariable("CODEX_USAGE_IDENTITY_FILE");
            var port = 22;
            var connectTimeoutSeconds = 3;
            var commandTimeoutSeconds = 5;

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
                        port = ParseInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--connect-timeout":
                        connectTimeoutSeconds = ParseInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--command-timeout":
                        commandTimeoutSeconds = ParseInt(RequireValue(args, ref i, arg), arg);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {arg}");
                }
            }

            return new AppOptions(
                once,
                showHelp,
                sshHost,
                remoteCommand,
                string.IsNullOrWhiteSpace(jsonFile) ? null : jsonFile,
                string.IsNullOrWhiteSpace(identityFile) ? null : identityFile,
                port,
                connectTimeoutSeconds,
                commandTimeoutSeconds);
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

        private static int ParseInt(string raw, string name)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                throw new ArgumentException($"{name} must be a positive integer");
            }

            return value;
        }
    }

    private sealed class SshCommandClient(AppOptions options)
    {
        public async Task<string> RunAsync()
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh",
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
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

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
                TryKill(process);
                throw new TimeoutException($"SSH command timed out after {options.CommandTimeoutSeconds} seconds");
            }

            if (process.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                throw new InvalidOperationException($"ssh failed with exit code {process.ExitCode}: {err}");
            }

            var json = stdout.ToString().Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("exporter returned empty output");
            }

            return json;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort only; the caller receives the timeout error.
            }
        }
    }

    private static class UsageJsonParser
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
                var quota = ParseQuota(root.GetProperty("quota"));
                var tokensRoot = root.GetProperty("tokens");
                var tokenWindows = new Dictionary<string, TokenWindow>(StringComparer.Ordinal);
                foreach (var window in RequiredWindows)
                {
                    if (!tokensRoot.TryGetProperty(window, out var item))
                    {
                        throw new InvalidOperationException($"missing token window: {window}");
                    }

                    tokenWindows[window] = new TokenWindow(
                        TotalTokens: RequireInt64(item, "total_tokens"),
                        HitTokens: RequireInt64(item, "hit_tokens"),
                        HitRate: RequireNullableDouble(item, "hit_rate") ?? 0,
                        Requests: RequireInt32(item, "requests"),
                        TotalCostUsd: RequireNullableDecimal(item, "total_cost_usd") ?? 0m);
                }

                var refreshRoot = root.GetProperty("refresh");
                var lastSuccessAt = RequireDateTime(refreshRoot, "last_success_at");
                var lastEventAt = OptionalDateTime(refreshRoot, "cc_switch_last_event_at");
                var stale = RequireBool(refreshRoot, "stale");

                return new UsageState(collectedAt, quota, tokenWindows, lastSuccessAt, lastEventAt, stale);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"invalid JSON: {ex.Message}", ex);
            }
        }

        private static QuotaState ParseQuota(JsonElement quotaRoot)
        {
            return new QuotaState(
                FiveHour: ParseQuotaWindow(quotaRoot.GetProperty("five_hour")),
                Weekly: ParseQuotaWindow(quotaRoot.GetProperty("weekly")));
        }

        private static QuotaWindow ParseQuotaWindow(JsonElement item)
        {
            return new QuotaWindow(
                Available: RequireBool(item, "available"),
                RemainingPercent: RequireNullableDouble(item, "remaining_percent"),
                ResetAt: OptionalDateTime(item, "reset_at"));
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
            var value = OptionalDateTime(root, name);
            if (value is null)
            {
                throw new InvalidOperationException($"missing or invalid datetime field: {name}");
            }

            return value.Value;
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

        private static int RequireInt32(JsonElement root, string name)
        {
            var value = RequireInt64(root, name);
            if (value > int.MaxValue)
            {
                throw new InvalidOperationException($"integer field is too large: {name}");
            }

            return (int)value;
        }

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

            if (item.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (!item.TryGetDouble(out var value))
            {
                throw new InvalidOperationException($"invalid numeric field: {name}");
            }

            return value;
        }

        private static decimal? RequireNullableDecimal(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var item))
            {
                throw new InvalidOperationException($"missing numeric field: {name}");
            }

            if (item.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (!item.TryGetDecimal(out var value))
            {
                throw new InvalidOperationException($"invalid numeric field: {name}");
            }

            return value;
        }
    }

    private sealed record UsageState(
        DateTimeOffset CollectedAt,
        QuotaState Quota,
        IReadOnlyDictionary<string, TokenWindow> Tokens,
        DateTimeOffset LastSuccessAt,
        DateTimeOffset? LastEventAt,
        bool Stale);

    private sealed record QuotaState(QuotaWindow FiveHour, QuotaWindow Weekly);

    private sealed record QuotaWindow(bool Available, double? RemainingPercent, DateTimeOffset? ResetAt);

    private sealed record TokenWindow(long TotalTokens, long HitTokens, double HitRate, int Requests, decimal TotalCostUsd);

    private static class UsageFormatter
    {
        public static string Format(UsageState state)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"5h left {FormatQuota(state.Quota.FiveHour)}, reset {FormatReset(state.Quota.FiveHour.ResetAt)}");
            sb.AppendLine($"Week left {FormatQuota(state.Quota.Weekly)}, reset {FormatReset(state.Quota.Weekly.ResetAt)}");

            foreach (var window in new[] { "today", "1d", "7d", "14d", "30d" })
            {
                var item = state.Tokens[window];
                sb.AppendLine(
                    $"{FormatWindowName(window)} {FormatTokens(item.TotalTokens)} tokens, " +
                    $"hit {FormatTokens(item.HitTokens)}, " +
                    $"hit {FormatPercent(item.HitRate)}, " +
                    $"req {item.Requests}, " +
                    $"cost {FormatCost(item.TotalCostUsd)}");
            }

            sb.AppendLine($"Last refresh {FormatTime(state.LastSuccessAt)}");
            if (state.LastEventAt is not null)
            {
                sb.AppendLine($"Last cc-switch event {FormatTime(state.LastEventAt.Value)}");
            }

            if (state.Stale)
            {
                sb.AppendLine("STALE");
            }

            return sb.ToString();
        }

        private static string FormatWindowName(string window) => window == "today" ? "Today" : window;

        private static string FormatQuota(QuotaWindow quota)
        {
            if (!quota.Available || quota.RemainingPercent is null)
            {
                return "--";
            }

            return $"{quota.RemainingPercent.Value:0.#}%";
        }

        private static string FormatReset(DateTimeOffset? resetAt)
        {
            if (resetAt is null)
            {
                return "--";
            }

            return FormatTime(resetAt.Value);
        }

        private static string FormatTime(DateTimeOffset value)
        {
            var local = value.ToLocalTime();
            var now = DateTimeOffset.Now;
            if (local.Date == now.Date)
            {
                return local.ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            return local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static string FormatTokens(long value)
        {
            var abs = Math.Abs(value);
            if (abs < 1_000)
            {
                return value.ToString("0", CultureInfo.InvariantCulture);
            }

            if (abs < 1_000_000)
            {
                return $"{value / 1_000d:0.#}k";
            }

            if (abs < 1_000_000_000)
            {
                return $"{value / 1_000_000d:0.#}M";
            }

            return $"{value / 1_000_000_000d:0.#}B";
        }

        private static string FormatPercent(double value)
        {
            return (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatCost(decimal value)
        {
            if (value >= 100m)
            {
                return "$" + Math.Round(value, 0).ToString("0", CultureInfo.InvariantCulture);
            }

            return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
