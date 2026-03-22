using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using RecoTool.Services;

namespace RecoTool.Helpers
{
    /// <summary>
    /// Lightweight helper for multi-user session display.
    /// Only provides formatting utilities — warning dialogs are handled inline.
    /// </summary>
    public static class MultiUserHelper
    {
        /// <summary>
        /// Formats a duration for display
        /// </summary>
        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 30)
                return "just now";
            if (duration.TotalMinutes < 1)
                return $"{(int)duration.TotalSeconds} seconds";
            if (duration.TotalMinutes < 60)
                return $"{(int)duration.TotalMinutes} minute{((int)duration.TotalMinutes > 1 ? "s" : "")}";
            if (duration.TotalHours < 24)
                return $"{(int)duration.TotalHours} hour{((int)duration.TotalHours > 1 ? "s" : "")}";
            return $"{(int)duration.TotalDays} day{((int)duration.TotalDays > 1 ? "s" : "")}";
        }

        /// <summary>
        /// Gets a short summary of active sessions for tooltips / status bars.
        /// </summary>
        public static async Task<string> GetSessionSummaryAsync(TodoListSessionTracker sessionTracker, int todoId)
        {
            if (sessionTracker == null) return string.Empty;
            try
            {
                var sessions = await sessionTracker.GetActiveSessionsAsync(todoId);
                var active = sessions.Where(s => s.IsActive).ToList();
                if (active.Count == 0) return string.Empty;
                return $"{active.Count} user(s) on this TodoList";
            }
            catch { return string.Empty; }
        }
    }
}
