namespace RecoTool.Configuration
{
    /// <summary>
    /// Feature flags for enabling/disabling application features
    /// </summary>
    public static class FeatureFlags
    {
        /// <summary>
        /// Enable/disable multi-user features (TodoList session tracking, heartbeat, editing indicators)
        /// Set to false to disable all multi-user overhead (heartbeat timer, session tracking, etc.)
        /// </summary>
        public const bool ENABLE_MULTI_USER = true;

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
