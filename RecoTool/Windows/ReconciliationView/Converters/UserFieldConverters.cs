using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using RecoTool.Models;

// NOTE: Namespace intentionally kept at RecoTool.Windows so XAML references using
// xmlns:local="clr-namespace:RecoTool.Windows" continue to resolve unchanged.
namespace RecoTool.Windows
{
    /// <summary>
    /// Item exposed by <see cref="UserFieldOptionsConverter"/> in ComboBox popups/filters.
    /// Keeping it alongside the converter avoids having to hunt across files.
    /// </summary>
    public class UserFieldOption
    {
        public int? USR_ID { get; set; }
        public string USR_FieldName { get; set; }
    }

    /// <summary>
    /// Produces the filtered list of UserField options shown in Action / KPI / IncidentType / ReasonNonRisky
    /// pickers. Caches results by <c>(category, accountSide)</c>; the cache is invalidated when the
    /// underlying <c>AllUserFields</c> reference changes.
    /// <para>
    /// Called from XAML via the top-of-page filter ComboBoxes AND from code-behind inside
    /// <c>ShowUserFieldEditPopup</c> — do not rename the class without updating those references.
    /// </para>
    /// </summary>
    public class UserFieldOptionsConverter : IMultiValueConverter
    {
        private static readonly Dictionary<(string category, string accountSide), List<UserFieldOption>> _cache
            = new Dictionary<(string, string), List<UserFieldOption>>();
        private static IReadOnlyList<UserField> _lastAllUserFields;

        // values: [0]=Account_ID (string), [1]=AllUserFields (IReadOnlyList<UserField>), [2]=CurrentCountry (Country)
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var accountId = values?.Length > 0 ? values[0]?.ToString() : null;
                var all = values?.Length > 1 ? values[1] as IReadOnlyList<UserField> : null;
                var country = values?.Length > 2 ? values[2] as Country : null;
                var category = parameter?.ToString();

                if (all == null || string.IsNullOrWhiteSpace(category))
                    return Array.Empty<object>();

                // Invalidate cache when the source list identity changes (e.g. after a country switch
                // that rebuilt the referential). Intentional reference equality — the list instance is
                // immutable once published by OfflineFirstService.
                if (!ReferenceEquals(all, _lastAllUserFields))
                {
                    _cache.Clear();
                    _lastAllUserFields = all;
                }

                // Determine account side. An empty string means "no side filter" (e.g. filters bar).
                string accountSide = "";
                if (country != null && !string.IsNullOrWhiteSpace(accountId))
                {
                    if (string.Equals(accountId.Trim(), country.CNT_AmbrePivot?.Trim(), StringComparison.OrdinalIgnoreCase))
                        accountSide = "P";
                    else if (string.Equals(accountId.Trim(), country.CNT_AmbreReceivable?.Trim(), StringComparison.OrdinalIgnoreCase))
                        accountSide = "R";
                }

                var cacheKey = (category.ToUpperInvariant(), accountSide);
                if (_cache.TryGetValue(cacheKey, out var cached))
                    return cached;

                var result = BuildOptionsList(all, category, accountSide);
                _cache[cacheKey] = result;
                return result;
            }
            catch { return Array.Empty<object>(); }
        }

        private static List<UserFieldOption> BuildOptionsList(IReadOnlyList<UserField> all, string category, string accountSide)
        {
            // "Incident Type" and "INC" both address the same category in the referential.
            bool isIncident = string.Equals(category, "Incident Type", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(category, "INC", StringComparison.OrdinalIgnoreCase);

            IEnumerable<UserField> query = isIncident
                ? all.Where(u => string.Equals(u.USR_Category, "Incident Type", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(u.USR_Category, "INC", StringComparison.OrdinalIgnoreCase))
                : all.Where(u => string.Equals(u.USR_Category, category, StringComparison.OrdinalIgnoreCase));

            if (accountSide == "P") query = query.Where(u => u.USR_Pivot);
            else if (accountSide == "R") query = query.Where(u => u.USR_Receivable);

            // Prepend a "none" option so the user can clear a previously-set value.
            var list = new List<UserFieldOption>
            {
                new UserFieldOption { USR_ID = null, USR_FieldName = string.Empty }
            };
            list.AddRange(query
                .OrderBy(u => u.USR_FieldName)
                .Select(u => new UserFieldOption { USR_ID = u.USR_ID, USR_FieldName = u.USR_FieldName }));
            return list;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
