using System;
using System.IO;
using Microsoft.Extensions.Logging;
using RecoTool.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace RecoTool.Infrastructure.Logging
{
    /// <summary>
    /// Centralised Serilog bootstrap for the RecoTool app.
    ///
    /// <para>
    /// Wires Serilog as the underlying provider for <see cref="ILoggerFactory"/> so the rest
    /// of the application can depend only on <see cref="ILogger{T}"/> via DI. The legacy
    /// <see cref="LogHelper"/> file outputs remain for backward compat during the transition.
    /// </para>
    ///
    /// <para>
    /// Configuration:
    /// <list type="bullet">
    ///   <item>Rolling daily file at <c>%APPDATA%/RecoTool/logs/recotool-.log</c></item>
    ///   <item>Wrapped in an async sink so logging never blocks the UI thread</item>
    ///   <item>Min level: <see cref="LogEventLevel.Debug"/> in UAT, <see cref="LogEventLevel.Information"/> otherwise</item>
    ///   <item>SelfLog routed to <see cref="Console.Error"/> so sink failures are visible during dev</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class LoggingSetup
    {
        private const string DefaultOutputTemplate =
            "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}";

        /// <summary>
        /// Builds a <see cref="ILoggerFactory"/> backed by Serilog.
        /// Safe to call once at application startup; the returned factory owns the underlying
        /// Serilog logger and should be disposed (via the DI container) on shutdown.
        /// </summary>
        public static ILoggerFactory CreateLoggerFactory()
        {
            // Surface Serilog's own internal errors (sink misconfig, IO failures...) so we
            // don't lose them silently during early app startup.
            try
            {
                Serilog.Debugging.SelfLog.Enable(msg =>
                {
                    try { Console.Error.WriteLine("[Serilog SelfLog] " + msg); }
                    catch { /* console may be unavailable in WPF — ignore */ }
                });
            }
            catch { /* best-effort */ }

            var logDir = GetLogDirectory();
            try { if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir); }
            catch { /* if we can't create the dir, the file sink will fail loudly via SelfLog */ }

            var minLevel = ResolveMinimumLevel();
            var logFilePath = Path.Combine(logDir, "recotool-.log");

            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Is(minLevel)
                .Enrich.FromLogContext()
                .WriteTo.Async(a => a.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: DefaultOutputTemplate,
                    shared: false,
                    retainedFileCountLimit: 31))
                .CreateLogger();

            // SerilogLoggerFactory disposes the underlying logger when the factory itself is
            // disposed (we pass dispose:true), so the DI container owns the lifecycle.
            return new SerilogLoggerFactory(serilogLogger, dispose: true);
        }

        /// <summary>
        /// <c>%APPDATA%/RecoTool/logs</c>. Kept separate from the legacy actions/perf/rules
        /// logs (which live at <c>%APPDATA%/RecoTool</c> directly) so file rotation doesn't
        /// touch them.
        /// </summary>
        internal static string GetLogDirectory()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "RecoTool", "logs");
        }

        private static LogEventLevel ResolveMinimumLevel()
        {
            try
            {
                return FeatureFlags.IsUAT ? LogEventLevel.Debug : LogEventLevel.Information;
            }
            catch
            {
                // FeatureFlags should never throw, but if its static ctor blows up at
                // startup we still want logging to work.
                return LogEventLevel.Information;
            }
        }
    }
}
