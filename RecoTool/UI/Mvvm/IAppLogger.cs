using System;

namespace RecoTool.UI.Mvvm
{
    /// <summary>
    /// Minimal logger abstraction. Lets the codebase gradually replace the scattered
    /// <c>System.Diagnostics.Debug.WriteLine</c> calls with a proper, swappable sink.
    /// <para>
    /// The default implementation is <see cref="DebugLogger"/> which routes to
    /// <see cref="System.Diagnostics.Debug"/>. Call <see cref="SetCurrent"/> once in
    /// <c>App</c> startup if you want to redirect to a file/Serilog/NLog sink later.
    /// </para>
    /// </summary>
    public interface IAppLogger
    {
        void Debug(string category, string message);
        void Info(string category, string message);
        void Warn(string category, string message, Exception ex = null);
        void Error(string category, string message, Exception ex = null);
    }

    /// <summary>Default sink: writes to <see cref="System.Diagnostics.Debug"/>.</summary>
    public sealed class DebugLogger : IAppLogger
    {
        public void Debug(string category, string message)
            => System.Diagnostics.Debug.WriteLine($"[DBG][{category}] {message}");
        public void Info(string category, string message)
            => System.Diagnostics.Debug.WriteLine($"[INF][{category}] {message}");
        public void Warn(string category, string message, Exception ex = null)
            => System.Diagnostics.Debug.WriteLine($"[WRN][{category}] {message}{(ex != null ? " :: " + ex.Message : "")}");
        public void Error(string category, string message, Exception ex = null)
            => System.Diagnostics.Debug.WriteLine($"[ERR][{category}] {message}{(ex != null ? " :: " + ex : "")}");
    }

    /// <summary>
    /// Static accessor so services that are not DI-resolved can still log with a single line
    /// (<c>AppLog.Current.Warn(...)</c>). Tests can swap the sink with <see cref="SetCurrent"/>.
    /// </summary>
    public static class AppLog
    {
        private static IAppLogger _current = new DebugLogger();
        public static IAppLogger Current => _current;
        public static void SetCurrent(IAppLogger logger) => _current = logger ?? new DebugLogger();
    }
}
