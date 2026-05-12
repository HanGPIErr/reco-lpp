using System;
using System.Data.OleDb;
using System.IO;

namespace RecoTool.IntegrationTests.Fixtures
{
    /// <summary>
    /// Détecte si le runtime ACE OLE DB est disponible sur la machine de test.
    /// Permet de skipper proprement les tests d'intégration sur les CI/postes
    /// qui n'ont pas Microsoft Access Database Engine installé.
    /// </summary>
    public static class AccessAvailable
    {
        private static readonly Lazy<bool> _ace16 = new Lazy<bool>(() => HasProvider("Microsoft.ACE.OLEDB.16.0"));
        private static readonly Lazy<bool> _ace12 = new Lazy<bool>(() => HasProvider("Microsoft.ACE.OLEDB.12.0"));

        public static bool Ace16 => _ace16.Value;
        public static bool Ace12 => _ace12.Value;
        public static bool AnyAce => Ace12 || Ace16;

        public static string PreferredProvider
            => Ace16 ? "Microsoft.ACE.OLEDB.16.0"
             : Ace12 ? "Microsoft.ACE.OLEDB.12.0"
             : null;

        /// <summary>
        /// Skip reason text suitable for [SkippableFact] / xunit's Skip.IfNot.
        /// </summary>
        public static string SkipReasonOrNull
            => AnyAce ? null
             : "Microsoft Access Database Engine (ACE.OLEDB) is not installed on this machine — integration test skipped.";

        private static bool HasProvider(string providerName)
        {
            try
            {
                // Tente de construire et d'ouvrir une connexion sur un fichier inexistant.
                // Si le provider est manquant, l'erreur 'provider not registered' est jetée
                // immédiatement et on retourne false. Sinon, l'erreur d'ouverture (fichier
                // introuvable) confirme que le provider est bien là.
                var cs = $"Provider={providerName};Data Source={Path.GetTempFileName()}_does_not_exist.accdb;";
                using (var c = new OleDbConnection(cs))
                {
                    try { c.Open(); }
                    catch (System.Data.OleDb.OleDbException ex)
                    {
                        // 'provider not registered' → false. Tout autre code → true.
                        if (ex.Message != null && ex.Message.IndexOf("not registered", StringComparison.OrdinalIgnoreCase) >= 0)
                            return false;
                        return true;
                    }
                    catch (InvalidOperationException ex) when (ex.Message?.IndexOf("provider", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
