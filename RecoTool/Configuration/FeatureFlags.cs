using System;

namespace RecoTool.Configuration
{
    /// <summary>
    /// Feature flags for enabling/disabling application features
    /// </summary>
    public static class FeatureFlags
    {
        /// <summary>
        /// Enable/disable multi-user features at runtime.
        /// When false (default): TodoList session tracking, heartbeat timer, editing indicators,
        /// background pushes/pulls, snapshot publish/pull on the network share, and the
        /// SyncMonitorService poll timer are all paused. The app starts in Solo mode by default
        /// so a fresh launch never blocks on the slow network share. The user can flip the
        /// "Solo mode" / "Multi-user" toggle in MainWindow to enable multi-user features for
        /// the current session.
        /// </summary>
        public static bool ENABLE_MULTI_USER { get; set; } = false;

        /// <summary>
        /// Fired whenever <see cref="ENABLE_MULTI_USER"/> changes. Subscribers can react
        /// (e.g. start/stop background workers) without having to poll the flag.
        /// </summary>
        public static event EventHandler<bool> MultiUserChanged;

        /// <summary>
        /// Update <see cref="ENABLE_MULTI_USER"/> and notify subscribers. No-op if the value
        /// is already what we want (avoids redundant Start/Stop cycles).
        /// </summary>
        public static void SetMultiUserEnabled(bool enabled)
        {
            if (ENABLE_MULTI_USER == enabled) return;
            ENABLE_MULTI_USER = enabled;
            try { MultiUserChanged?.Invoke(null, enabled); } catch { }
        }

        /// <summary>
        /// True when compiled with the UAT build configuration (defines UAT_ENV symbol).
        /// Used to switch DB paths, display UAT banners, and change the application title.
        /// </summary>
        public static bool IsUAT
        {
            get
            {
#if UAT_ENV
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Application display name (includes UAT suffix when in UAT mode).
        /// </summary>
        public static string AppTitle => IsUAT ? "RecoTool [UAT]" : "RecoTool";

        /// <summary>
        /// Referential DB parameter key override for UAT.
        /// When UAT, look for ReferentialDatabasePath_UAT in T_Param first.
        /// </summary>
        public static string ReferentialDbParamKey => IsUAT ? "ReferentialDatabasePath_UAT" : "ReferentialDatabasePath";

        /// <summary>
        /// Country DB directory parameter key override for UAT.
        /// </summary>
        public static string CountryDbDirParamKey => IsUAT ? "CountryDatabaseDirectory_UAT" : "CountryDatabaseDirectory";
    }
}
