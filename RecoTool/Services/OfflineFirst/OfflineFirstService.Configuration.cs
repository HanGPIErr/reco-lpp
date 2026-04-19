using RecoTool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RecoTool.Services
{
    /// <summary>
    /// Application-level configuration helpers for <see cref="OfflineFirstService"/>:
    /// loads the referential database path (with UAT override), exposes <see cref="GetParameter"/>
    /// for T_Param entries, and reloads referentials when configuration changes.
    /// </summary>
    public partial class OfflineFirstService
    {
        /// <summary>
        /// Charge la configuration depuis App.config et initialise les paramètres
        /// </summary>
        private void LoadConfiguration()
        {
            try
            {
                // Load referential DB path from Settings
                var basePath = Properties.Settings.Default.ReferentialDB;

                // UAT override: if compiled with UAT_ENV, check for a _UAT suffixed setting
                // or an environment variable RECOTOOL_REFERENTIAL_UAT
                if (Configuration.FeatureFlags.IsUAT)
                {
                    var uatPath = System.Environment.GetEnvironmentVariable("RECOTOOL_REFERENTIAL_UAT");
                    if (!string.IsNullOrWhiteSpace(uatPath) && System.IO.File.Exists(uatPath))
                    {
                        basePath = uatPath;
                        System.Diagnostics.Debug.WriteLine($"[UAT] Using UAT referential from env var: {basePath}");
                    }
                    else
                    {
                        // Try convention: same folder, filename with _UAT suffix
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(basePath))
                            {
                                var dir = System.IO.Path.GetDirectoryName(basePath);
                                var name = System.IO.Path.GetFileNameWithoutExtension(basePath);
                                var ext = System.IO.Path.GetExtension(basePath);
                                var uatConvention = System.IO.Path.Combine(dir, $"{name}_UAT{ext}");
                                if (System.IO.File.Exists(uatConvention))
                                {
                                    basePath = uatConvention;
                                    System.Diagnostics.Debug.WriteLine($"[UAT] Using UAT referential by convention: {basePath}");
                                }
                            }
                        }
                        catch { }
                    }
                }

                _ReferentialDatabasePath = basePath;

                if (string.IsNullOrEmpty(_ReferentialDatabasePath))
                {
                    throw new InvalidOperationException(
                        "Le chemin de la base référentielle n'est pas configuré.\n" +
                        "Vérifiez Settings > ReferentialDB dans les propriétés du projet.\n" +
                        "Paramètre attendu: chemin vers Referential_Common.accdb");
                }

                if (!System.IO.File.Exists(_ReferentialDatabasePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[WARNING] Referential DB not found at: {_ReferentialDatabasePath}");
                }

                System.Diagnostics.Debug.WriteLine($"Configuration chargée. Base référentielle: {_ReferentialDatabasePath}" +
                    (Configuration.FeatureFlags.IsUAT ? " [UAT]" : ""));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur lors du chargement de la configuration: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Récupère un paramètre depuis la table T_Param chargée en mémoire
        /// </summary>
        /// <param name="key">Clé du paramètre</param>
        /// <returns>Valeur du paramètre ou null si non trouvé</returns>
        public string GetParameter(string key)
        {
            lock (_referentialLock)
            {
                var param = _params.FirstOrDefault(p => p.PAR_Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                return param?.PAR_Value;
            }
        }

        /// <summary>
        /// Retourne la liste des pays référentiels (copie). Charge les référentiels si nécessaire.
        /// </summary>
        public async Task<List<Country>> GetCountries()
        {
            if (!_referentialsLoaded)
                await LoadReferentialsAsync();
            lock (_referentialLock)
            {
                return new List<Country>(_countries);
            }
        }

        /// <summary>
        /// Initialise les paramètres depuis T_Param et charge la dernière country utilisée
        /// </summary>
        private void InitializePropertiesFromParams()
        {
            // Vérifier et définir les valeurs par défaut si nécessaires

            // Répertoire de données local pour offline-first
            string dataDirectory = GetParameter("DataDirectory");
            if (string.IsNullOrEmpty(dataDirectory))
            {
                // Valeur par défaut si non définie
                string defaultDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RecoTool", "Data");
                // Ajouter le paramètre par défaut (note: SetParameter n'existe pas encore, on l'ajoute au cache)
                lock (_referentialLock)
                {
                    var existingParam = _params.FirstOrDefault(p => p.PAR_Key == "DataDirectory");
                    if (existingParam == null)
                    {
                        _params.Add(new Param { PAR_Key = "DataDirectory", PAR_Value = defaultDataDir });
                    }
                }
            }

            // Préfixe des bases country
            string countryPrefix = GetParameter("CountryDatabasePrefix");
            if (string.IsNullOrEmpty(countryPrefix))
            {
                // Essayer avec d'autres noms possibles ou défaut
                countryPrefix = GetParameter("CountryDBPrefix") ?? GetParameter("CountryPrefix") ?? "DB_";
                // Ajouter le paramètre par défaut au cache
                lock (_referentialLock)
                {
                    var existingParam = _params.FirstOrDefault(p => p.PAR_Key == "CountryDatabasePrefix");
                    if (existingParam == null)
                    {
                        _params.Add(new Param { PAR_Key = "CountryDatabasePrefix", PAR_Value = countryPrefix });
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"Paramètres T_Param vérifiés:");
            System.Diagnostics.Debug.WriteLine($"  - DataDirectory: {GetParameter("DataDirectory")}");
            System.Diagnostics.Debug.WriteLine($"  - CountryDatabaseDirectory: {GetParameter("CountryDatabaseDirectory")}");
            System.Diagnostics.Debug.WriteLine($"  - CountryDatabasePrefix: {GetParameter("CountryDatabasePrefix")}");

            // Ne pas initialiser automatiquement un pays au démarrage.
            // Le pays sera défini explicitement par l'UI (sélection utilisateur),
            // afin d'éviter toute copie réseau->local avant choix explicite.
        }

        /// <summary>
        /// Recharge la configuration et les paramètres
        /// </summary>
        public async Task RefreshConfigurationAsync()
        {
            // Recharger la configuration de base
            LoadConfiguration();

            // Forcer le rechargement des référentiels
            lock (_referentialLock)
            {
                _referentialsLoaded = false;
            }

            await LoadReferentialsAsync();
        }
    }
}
