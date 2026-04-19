using RecoTool.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RecoTool.Services
{
    /// <summary>
    /// Read-only accessors over in-memory referential data owned by <see cref="OfflineFirstService"/>:
    /// Ambre import fields / transforms / transaction codes, user fields and user filters.
    /// Returns defensive copies under <c>_referentialLock</c> to guarantee caller immutability.
    /// </summary>
    public partial class OfflineFirstService
    {
        /// <summary>
        /// Récupère tous les champs d'import Ambre
        /// </summary>
        /// <returns>Liste des champs d'import</returns>
        public List<AmbreImportField> GetAmbreImportFields()
        {
            lock (_referentialLock)
            {
                return new List<AmbreImportField>(_ambreImportFields);
            }
        }

        /// <summary>
        /// Récupère toutes les transformations Ambre
        /// </summary>
        /// <returns>Liste des transformations</returns>
        public List<AmbreTransform> GetAmbreTransforms()
        {
            lock (_referentialLock)
            {
                return new List<AmbreTransform>(_ambreTransforms);
            }
        }

        /// <summary>
        /// Récupère tous les codes de transaction Ambre
        /// </summary>
        /// <returns>Liste des codes de transaction</returns>
        public List<AmbreTransactionCode> GetAmbreTransactionCodes()
        {
            lock (_referentialLock)
            {
                return new List<AmbreTransactionCode>(_ambreTransactionCodes);
            }
        }

        /// <summary>
        /// Expose les champs utilisateur en mémoire (copie pour immutabilité côté appelant)
        /// </summary>
        public List<UserField> UserFields
        {
            get
            {
                lock (_referentialLock)
                {
                    return new List<UserField>(_userFields);
                }
            }
        }

        /// <summary>
        /// Récupère la liste des filtres utilisateur en mémoire (copie)
        /// </summary>
        public Task<List<UserFilter>> GetUserFilters()
        {
            lock (_referentialLock)
            {
                return Task.FromResult(new List<UserFilter>(_userFilters));
            }
        }

        /// <summary>
        /// Recharge les filtres utilisateur depuis la base référentielle
        /// (appelé après création/modification/suppression d'un filtre)
        /// </summary>
        public async Task RefreshUserFiltersAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_ReferentialDatabasePath))
                {
                    LoadConfiguration();
                }

                var refCs = ReferentialConnectionString;
                if (string.IsNullOrWhiteSpace(refCs)) return;

                var filters = new List<UserFilter>();
                using (var conn = new System.Data.OleDb.OleDbConnection(refCs))
                {
                    await conn.OpenAsync().ConfigureAwait(false);
                    using (var cmd = new System.Data.OleDb.OleDbCommand("SELECT UFI_Name, UFI_SQL FROM T_Ref_User_Filter ORDER BY UFI_Name", conn))
                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var name = reader.IsDBNull(0) ? null : reader.GetString(0);
                            var sql = reader.IsDBNull(1) ? null : reader.GetString(1);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                filters.Add(new UserFilter { UFI_Name = name, UFI_SQL = sql });
                            }
                        }
                    }
                }

                lock (_referentialLock)
                {
                    _userFilters = filters;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OfflineFirstService] Error refreshing user filters: {ex.Message}");
            }
        }
    }
}
