using System;
using System.Data.OleDb;
using System.IO;
using ADOX = System.Type;

namespace RecoTool.IntegrationTests.Fixtures
{
    /// <summary>
    /// Crée une BDD Access vide pour un test d'intégration et la nettoie en sortie.
    /// Utilise ADOX (COM, en référence dynamique) pour éviter une dépendance hard
    /// sur l'assembly d'interop. Si ADOX n'est pas disponible (environnement CI sans
    /// Access Engine installé), <see cref="Created"/> sera <c>false</c> et les tests
    /// concernés peuvent être skippés via <see cref="AccessAvailable"/>.
    /// </summary>
    public class TempAccessDbFixture : IDisposable
    {
        public string Path { get; }
        public string ConnectionString { get; }
        public bool Created { get; }

        public TempAccessDbFixture()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RecoTool.IntegrationTests_{Guid.NewGuid():N}.accdb");

            try
            {
                Created = TryCreateEmptyDb(Path);
                if (Created)
                {
                    ConnectionString = $"Provider={AccessAvailable.PreferredProvider};Data Source={Path};";
                }
            }
            catch
            {
                Created = false;
            }
        }

        /// <summary>
        /// Exécute du SQL sur la base temporaire. À utiliser dans les Setups de test
        /// pour créer des tables et insérer des fixtures.
        /// </summary>
        public void ExecuteNonQuery(string sql)
        {
            if (!Created) throw new InvalidOperationException("Access DB was not created.");
            using (var c = new OleDbConnection(ConnectionString))
            {
                c.Open();
                using (var cmd = new OleDbCommand(sql, c))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { }
        }

        // ----- Helpers -----

        private static bool TryCreateEmptyDb(string path)
        {
            try
            {
                // ADOX is a COM type — load via late binding to avoid build-time dependency.
                var adoxType = Type.GetTypeFromProgID("ADOX.Catalog");
                if (adoxType == null) return false;

                dynamic catalog = Activator.CreateInstance(adoxType);
                try
                {
                    var cs = $"Provider={AccessAvailable.PreferredProvider};Data Source={path};";
                    catalog.Create(cs);
                    return File.Exists(path);
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(catalog);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
