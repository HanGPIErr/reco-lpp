using System;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using RecoTool.Infrastructure.DI;
using RecoTool.Infrastructure.Time;

namespace RecoTool.Infrastructure.Logging
{
    /// <summary>
    /// Legacy file-based logger. Writes tab-separated lines to:
    /// <c>%APPDATA%/RecoTool/actions.log</c>, <c>perf.log</c>, and <c>rules-YYYYMMDD.log</c>.
    ///
    /// <para>
    /// <b>Deprecated</b> — new code should inject <see cref="ILogger{T}"/> via DI instead.
    /// Serilog (configured by <see cref="LoggingSetup"/>) is the structured backbone. This
    /// class is preserved only so existing call sites keep compiling and writing to the
    /// same tab-separated files during the transition. Each write is also forwarded to
    /// the structured pipeline (best-effort) so downstream consumers can opt-in early.
    /// </para>
    /// </summary>
    public static class LogHelper
    {
        /// <summary>Clock used for log timestamps. Defaults to <see cref="SystemClock.Instance"/>; swappable for tests.</summary>
        public static IClock Clock { get; set; } = SystemClock.Instance;

        // Lazy: we cannot resolve ILogger at static-init time because LogHelper may be
        // called before App.OnStartup wires ServiceProvider. We attempt resolution on
        // every call but cache once we succeed.
        private static ILogger _structuredLogger;

        private static ILogger TryGetStructuredLogger()
        {
            var cached = _structuredLogger;
            if (cached != null) return cached;

            try
            {
                if (!ServiceLocator.IsInitialized) return null;

                var factory = ServiceLocator.GetService<ILoggerFactory>();
                if (factory == null) return null;

                var logger = factory.CreateLogger("RecoTool.Infrastructure.Logging.LogHelper");
                _structuredLogger = logger;
                return logger;
            }
            catch
            {
                return null;
            }
        }

        private static string EnsureAppDir()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecoTool");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static void WriteAction(string action, string details)
        {
            try
            {
                var dir = EnsureAppDir();
                var path = Path.Combine(dir, "actions.log");
                var user = Environment.UserName;
                var line = $"{Clock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\t{user}\t{action}\t{details}";
                File.AppendAllLines(path, new[] { line }, Encoding.UTF8);
            }
            catch { /* swallow logging errors */ }

            try
            {
                var logger = TryGetStructuredLogger();
                if (logger != null && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Action {Action} {Details} (user={User})",
                        action, details, Environment.UserName);
                }
            }
            catch { /* structured logging must never throw on the caller's path */ }
        }

        public static void WritePerf(string area, string details)
        {
            try
            {
                var dir = EnsureAppDir();
                var path = Path.Combine(dir, "perf.log");
                var line = $"{Clock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\t{area}\t{details}";
                File.AppendAllLines(path, new[] { line }, Encoding.UTF8);
            }
            catch { /* swallow logging errors */ }

            try
            {
                var logger = TryGetStructuredLogger();
                if (logger != null && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("Perf {Area} {Details}", area, details);
                }
            }
            catch { /* best-effort */ }
        }

        public static void WriteRuleApplied(string origin, string countryId, string recoId, string ruleId, string outputs, string message)
        {
            try
            {
                var dir = EnsureAppDir();
                var file = $"rules-{Clock.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log";
                var path = Path.Combine(dir, file);
                var user = Environment.UserName;
                var ts = Clock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                // Columns: ts, user, origin(import/edit/run-now), country, recoId, ruleId, outputs, message
                var safeOutputs = outputs?.Replace('\t', ' ');
                var safeMsg = message?.Replace('\t', ' ');
                var line = string.Join("\t", new[] { ts, user, origin ?? string.Empty, countryId ?? string.Empty, recoId ?? string.Empty, ruleId ?? string.Empty, safeOutputs ?? string.Empty, safeMsg ?? string.Empty });
                File.AppendAllLines(path, new[] { line }, Encoding.UTF8);
            }
            catch { /* best-effort */ }

            try
            {
                var logger = TryGetStructuredLogger();
                if (logger != null && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "RuleApplied {Origin} country={CountryId} reco={RecoId} rule={RuleId} outputs={Outputs} msg={Message}",
                        origin, countryId, recoId, ruleId, outputs, message);
                }
            }
            catch { /* best-effort */ }
        }
    }
}
