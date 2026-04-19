using System.Collections.Generic;
using System.Windows.Media;

namespace RecoTool.Services.DTOs
{
    /// <summary>
    /// Per-row marker flagging rows whose reconciliation fields differ from the most recent
    /// import snapshot. Bound by the grid's <c>VirtualizingCellsControl</c> style through
    /// <see cref="ActivityIndicatorBrush"/> to render a 3 px left edge on "recently moved" rows.
    /// </summary>
    /// <remarks>
    /// Set in bulk by <c>ReconciliationView.Activity.ApplyRecentActivityAsync</c> right after a
    /// fresh data load, using the ID set returned by
    /// <see cref="RecoTool.Services.Snapshots.SnapshotComparisonService.GetRowIdsChangedSinceLastRunAsync"/>.
    /// The brush is a static frozen instance so toggling the flag is a single virtual-call
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/> notification — no allocation.
    /// </remarks>
    public partial class ReconciliationViewData
    {
        // Frozen for thread-safe, alloc-free binding. Blue #2196F3 matches the app's accent palette.
        private static readonly SolidColorBrush _activityBrushTransparent = CreateFrozenBrush(0x00, 0x00, 0x00, 0x00);
        private static readonly SolidColorBrush _activityBrushActive      = CreateFrozenBrush(0xFF, 0x21, 0x96, 0xF3);

        private bool _hasRecentActivity;

        /// <summary>
        /// <c>true</c> when this row has changed since the last import snapshot. Drives the grid's
        /// left-edge indicator + the tooltip content.
        /// </summary>
        public bool HasRecentActivity
        {
            get => _hasRecentActivity;
            set
            {
                if (_hasRecentActivity == value) return;
                _hasRecentActivity = value;
                OnPropertyChanged(nameof(HasRecentActivity));
                OnPropertyChanged(nameof(ActivityIndicatorBrush));
            }
        }

        /// <summary>
        /// Resolved by the row style's <c>BorderBrush</c> setter. Transparent when the row is
        /// unchanged since the last import — the grid keeps its default 3 px gutter invisible.
        /// </summary>
        public SolidColorBrush ActivityIndicatorBrush =>
            _hasRecentActivity ? _activityBrushActive : _activityBrushTransparent;

        private IReadOnlyList<RowChange> _recentChanges;

        /// <summary>
        /// Field-level diff for this row between the last import snapshot and the live DB. Populated
        /// in bulk by <c>ReconciliationView.Activity.ApplyRecentActivityAsync</c> so the per-row
        /// tooltip can render immediately without a second round-trip.
        /// </summary>
        public IReadOnlyList<RowChange> RecentChanges
        {
            get => _recentChanges;
            set
            {
                if (!ReferenceEquals(_recentChanges, value))
                {
                    _recentChanges = value;
                    _changesByField = null; // invalidate lookup; rebuilt lazily on first hit
                    OnPropertyChanged(nameof(RecentChanges));
                    OnPropertyChanged(nameof(HasRecentChangesDetails));
                    OnPropertyChanged(nameof(LastImportDateDisplay));
                }
            }
        }

        /// <summary>
        /// <c>true</c> when <see cref="RecentChanges"/> carries at least one entry. Lets the XAML
        /// tooltip template hide the "No details" placeholder when details are available.
        /// </summary>
        public bool HasRecentChangesDetails => _recentChanges != null && _recentChanges.Count > 0;

        /// <summary>
        /// Localised string form of the import timestamp (e.g. <c>"19/04/2026 14:32"</c>). Bound
        /// from the row tooltip header — we build the "Changed since …" label off this.
        /// </summary>
        /// <remarks>
        /// All <see cref="RowChange"/> entries from a single run share the same
        /// <see cref="RowChange.TimestampUtc"/> (the snapshot's <c>StartedUtc</c>), so reading the
        /// first entry is sufficient. Returns <see cref="string.Empty"/> when the row has no
        /// detectable activity — the XAML falls back to the plain "Changed since last import"
        /// label via <c>StringFormat</c>/<c>TargetNullValue</c> so the tooltip stays readable if
        /// the timestamp propagation ever drops a run.
        /// </remarks>
        public string LastImportDateDisplay
        {
            get
            {
                var changes = _recentChanges;
                if (changes == null || changes.Count == 0) return string.Empty;
                var ts = changes[0].TimestampUtc;
                if (ts == default) return string.Empty;
                // Stored UTC, shown local — the user cares about "when it happened for me".
                return ts.ToLocalTime().ToString("dd/MM/yyyy HH:mm",
                    System.Globalization.CultureInfo.CurrentCulture);
            }
        }

        // Lazy field-keyed lookup so the cell converters can resolve a change in O(1) instead of
        // scanning the RecentChanges list N-per-cell on each render pass. Invalidated by the
        // RecentChanges setter.
        private Dictionary<string, RowChange> _changesByField;

        /// <summary>
        /// Returns the <see cref="RowChange"/> entry that tracks <paramref name="fieldName"/> for
        /// this row, or <c>null</c> if the field didn't change since the last import. Called by
        /// <c>CellChangedBackgroundConverter</c> / <c>CellChangedTooltipConverter</c> — case
        /// insensitive to match SfDataGrid's <c>MappingName</c> conventions.
        /// </summary>
        public RowChange GetChangeForField(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return null;
            var recent = _recentChanges;
            if (recent == null || recent.Count == 0) return null;

            var dict = _changesByField;
            if (dict == null)
            {
                dict = new Dictionary<string, RowChange>(recent.Count, System.StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < recent.Count; i++)
                {
                    var c = recent[i];
                    if (c == null || string.IsNullOrEmpty(c.FieldName)) continue;
                    // Last write wins — duplicates are unexpected but harmless.
                    dict[c.FieldName] = c;
                }
                _changesByField = dict;
            }
            return dict.TryGetValue(fieldName, out var rc) ? rc : null;
        }

        // Local overload because the static CreateFrozenBrush in Caches takes 3 bytes (opaque RGB).
        // We need an alpha-aware variant so the transparent fallback doesn't eat input hit-test area.
        private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }
}
