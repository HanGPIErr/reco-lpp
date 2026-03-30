using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Documents;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.UI.ViewModels;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using RecoTool.Services.DTOs;
using RecoTool.Services.Rules;
using RecoTool.UI.Helpers;
using RecoTool.Helpers;
using RecoTool.Services.External;
using RecoTool.Services.Ambre;

namespace RecoTool.Windows
{
    /// <summary>
    /// Logique d'interaction pour ReconciliationView.xaml
    /// Vue de réconciliation avec filtres et données réelles
    /// </summary>
    public partial class ReconciliationView : UserControl, INotifyPropertyChanged
    {
        #region Fields and Properties

        private readonly ReconciliationService _reconciliationService;
        private readonly OfflineFirstService _offlineFirstService;
        private TodoListSessionTracker _todoSessionTracker; // Multi-user session tracking
        private int _currentTodoId = 0; // Currently active TodoList ID
        // MVVM bridge: lightweight ViewModel holder to gradually migrate bindings
        public ReconciliationViewViewModel VM { get; } = new ReconciliationViewViewModel();
        private string _currentCountryId;
        private string _currentView = "Default View";
        private bool _isLoading;
        private bool _canRefresh = true;
        private bool _initialLoaded;
        private string _initialFilterSql; // Capture initial filter state for reset
        private DispatcherTimer _filterDebounceTimer;
        private DispatcherTimer _highlightClearTimer;
        private DispatcherTimer _toastTimer;
        // (multi-user warning banner removed for performance — check-on-open only)
        private Action _toastClickAction;
        private string _toastTargetReconciliationId;
        private bool _isSyncRefreshInProgress;
        private const int HighlightDurationMs = 4000;
        private bool _syncEventsHooked;
        private bool _hasLoadedOnce; // set after first RefreshCompleted to avoid double-load on startup
        // Debounce timer for background push (avoid immediate sync on rapid edits)
        private DispatcherTimer _pushDebounceTimer;

        // Collections pour l'affichage (vue combinée)
        private ObservableCollection<ReconciliationViewData> _viewData;
        private List<ReconciliationViewData> _allViewData; // Toutes les données pour le filtrage
        // Paging / incremental loading
        private const int InitialPageSize = 500;
        private List<ReconciliationViewData> _filteredData; // Données filtrées complètes (pour totaux/scroll)
        private int _loadedCount; // Nombre actuellement affiché dans ViewData
        private bool _isLoadingMore; // Garde-fou
        private bool _scrollHooked; // Pour éviter double-hook
        private ScrollViewer _resultsScrollViewer;
        private Button _loadMoreFooterButton; // cache footer button to avoid repeated FindName on scroll
        // Filtre backend transmis au service (défini par la page au moment de l'ajout de vue)
        private string _backendFilterSql;

        // Données préchargées par la page parente (si présentes, on évite un fetch service)
        private IReadOnlyList<ReconciliationViewData> _preloadedAllData;

        // Propriétés de filtrage (legacy display-only field kept)
        private string _filterCountry;

        private readonly FreeApiService _freeApi;
        private System.Threading.CancellationTokenSource _freeApiCts;

        // (see further below for filter properties; using ScheduleApplyFiltersDebounced)

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler CloseRequested;

        private void OnPropertyChanged(string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Open read-only preview for MbawData
        private async void MbawCell_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var fe = sender as FrameworkElement;
                var row = fe?.DataContext as RecoTool.Services.DTOs.ReconciliationViewData;
                if (row == null) return;

                var dlg = new PreviewTextDialog
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.SetTitle("Mbaw Data");
                dlg.SetContent(row.MbawData ?? string.Empty);
                dlg.ShowDialog();
            }
            catch { }
        }

        // Allows parent page to set a custom title, optionally marking it as a ToDo title
        public void SetViewTitle(string title, bool isTodo)
        {
            try
            {
                var text = string.IsNullOrWhiteSpace(title) ? "Default View" : (isTodo ? $"ToDo: {title}" : title);
                
                // Update _currentView so UpdateViewTitle() preserves it
                _currentView = text;
                
                if (TitleText != null)
                {
                    TitleText.Text = text;
                    TitleText.ToolTip = title;
                }
            }
            catch { }
        }

        // Backward-compatible overload: defaults to non-ToDo
        public void SetViewTitle(string title) => SetViewTitle(title, false);

        // Simple window to toggle visibility of multiple SfDataGrid columns
        private sealed class ManageColumnsWindow : Window
        {
            private readonly Syncfusion.UI.Xaml.Grid.SfDataGrid _sfGrid;
            private readonly List<ColumnItem> _items = new List<ColumnItem>();

            private sealed class ColumnItem
            {
                public string Header { get; set; }
                public bool IsVisible { get; set; }
                public bool IsProtected { get; set; }
                public Syncfusion.UI.Xaml.Grid.GridColumn Column { get; set; }
            }

            public ManageColumnsWindow(Syncfusion.UI.Xaml.Grid.SfDataGrid sfGrid)
            {
                _sfGrid = sfGrid;
                Title = "Manage Columns";
                Width = 420;
                Height = 520;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                Content = BuildUI();
                // Load columns after window is loaded to ensure visual tree is ready
                this.Loaded += (s, e) => { try { LoadColumns(); } catch { } };
            }

            private UIElement BuildUI()
            {
                var root = new DockPanel { Margin = new Thickness(10) };

                var topBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                var selectAllBtn = new Button { Content = "Select All", Height = 26, Margin = new Thickness(0, 0, 6, 0) };
                var deselectAllBtn = new Button { Content = "Deselect All", Height = 26, Margin = new Thickness(0, 0, 6, 0) };
                var resetBtn = new Button { Content = "Reset", Height = 26 };
                selectAllBtn.Click += (s, e) => { foreach (var it in _items.Where(i => !i.IsProtected)) it.IsVisible = true; RefreshList(); };
                deselectAllBtn.Click += (s, e) => { foreach (var it in _items.Where(i => !i.IsProtected)) it.IsVisible = false; RefreshList(); };
                resetBtn.Click += (s, e) => { TryResetToDefaults(); };
                topBar.Children.Add(selectAllBtn);
                topBar.Children.Add(deselectAllBtn);
                topBar.Children.Add(resetBtn);
                DockPanel.SetDock(topBar, Dock.Top);

                var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var list = new StackPanel { Name = "ListPanel" };
                scroll.Content = list;

                var btnBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
                var okBtn = new Button { Content = "OK", Width = 80, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
                var cancelBtn = new Button { Content = "Cancel", Width = 80, Height = 28 };
                okBtn.Click += (s, e) => { try { Apply(); } catch { } this.DialogResult = true; };
                cancelBtn.Click += (s, e) => { this.DialogResult = false; };
                btnBar.Children.Add(okBtn);
                btnBar.Children.Add(cancelBtn);
                DockPanel.SetDock(btnBar, Dock.Bottom);

                root.Children.Add(topBar);
                root.Children.Add(btnBar);
                root.Children.Add(scroll);
                return root;
            }

            private void LoadColumns()
            {
                _items.Clear();
                if (_sfGrid?.Columns == null) return;
                // Protect first 6 indicator columns
                for (int i = 0; i < _sfGrid.Columns.Count; i++)
                {
                    var col = _sfGrid.Columns[i];
                    var header = col.HeaderText;
                    bool isProtected = i < 6; // keep indicators always visible
                    _items.Add(new ColumnItem
                    {
                        Header = string.IsNullOrWhiteSpace(header) ? $"Column {i + 1}" : header,
                        IsVisible = !col.IsHidden,
                        IsProtected = isProtected,
                        Column = col
                    });
                }
                RefreshList();
            }

            private void RefreshList()
            {
                var listPanel = FindDescendant<StackPanel>(this.Content as DependencyObject, "ListPanel");
                if (listPanel == null) return;
                listPanel.Children.Clear();
                foreach (var it in _items)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    var cb = new CheckBox { IsChecked = it.IsVisible, IsEnabled = !it.IsProtected, VerticalAlignment = VerticalAlignment.Center };
                    cb.Checked += (s, e) => it.IsVisible = true;
                    cb.Unchecked += (s, e) => it.IsVisible = false;
                    var tb = new TextBlock { Text = it.Header, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    if (it.IsProtected)
                    {
                        tb.Text += " (locked)";
                        tb.Foreground = Brushes.Gray;
                    }
                    row.Children.Add(cb);
                    row.Children.Add(tb);
                    listPanel.Children.Add(row);
                }
            }

            private void Apply()
            {
                foreach (var it in _items)
                {
                    try
                    {
                        if (it.IsProtected) continue;
                        it.Column.IsHidden = !it.IsVisible;
                    }
                    catch { }
                }
            }

            private void TryResetToDefaults()
            {
                try
                {
                    // Simple default: first 12 data columns visible besides indicators, others visible too
                    for (int i = 0; i < _items.Count; i++)
                    {
                        var it = _items[i];
                        if (it.IsProtected) { it.IsVisible = true; continue; }
                        it.IsVisible = true;
                    }
                    RefreshList();
                }
                catch { }
            }

            private static T FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
            {
                if (root == null) return null;
                if (root is T fe && (string.IsNullOrEmpty(name) || fe.Name == name)) return fe;
                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    var match = FindDescendant<T>(child, name);
                    if (match != null) return match;
                }
                return null;
            }
        }

        // Quick add rule based on the current line
        private async void QuickAddRuleFromLineMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var mi = sender as MenuItem;
                var row = mi?.DataContext as ReconciliationViewData;
                if (row == null || _offlineFirstService == null) return;

                // Build a seed rule from the selected row
                string accountSide = string.IsNullOrWhiteSpace(row.AccountSide) ? "*" : row.AccountSide.Trim().ToUpperInvariant();
                string guaranteeType = string.IsNullOrWhiteSpace(row.G_GUARANTEE_TYPE) ? "*" : row.G_GUARANTEE_TYPE.Trim().ToUpperInvariant();
                string txName = row.Category.HasValue ? Enum.GetName(typeof(TransactionType), row.Category.Value) : "*";
                bool hasDw = !(string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID) && string.IsNullOrWhiteSpace(row.DWINGS_GuaranteeID) && string.IsNullOrWhiteSpace(row.DWINGS_BGPMT));

                var seed = new TruthRule
                {
                    RuleId = $"USR_{row.ID}",
                    Enabled = true,
                    Priority = 100,
                    Scope = RuleScope.Both,
                    AccountSide = string.IsNullOrWhiteSpace(accountSide) ? "*" : accountSide,
                    GuaranteeType = string.IsNullOrWhiteSpace(guaranteeType) ? "*" : guaranteeType,
                    TransactionType = string.IsNullOrWhiteSpace(txName) ? "*" : txName,
                    HasDwingsLink = hasDw,
                    // Outputs from current row values
                    OutputActionId = row.Action,
                    OutputKpiId = row.KPI,
                    OutputIncidentTypeId = row.IncidentType,
                    ApplyTo = ApplyTarget.Self,
                    AutoApply = true
                };

                var win = new RuleEditorWindow(seed, _offlineFirstService)
                {
                    Owner = Window.GetWindow(this)
                };
                var ok = win.ShowDialog();
                if (ok == true && win.ResultRule != null)
                {
                    var repo = new TruthTableRepository(_offlineFirstService);
                    // Ensure table exists best-effort
                    try { await repo.EnsureRulesTableAsync(); } catch { }
                    var saved = await repo.UpsertRuleAsync(win.ResultRule);
                    if (saved)
                    {
                        UpdateStatusInfo($"Rule '{win.ResultRule.RuleId}' saved.");
                    }
                    else
                    {
                        ShowError("Failed to save rule (Upsert returned false)");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to add rule: {ex.Message}");
            }
        }

        // Lock first 6 frozen columns from being moved (SfDataGrid version)
        private void ResultsDataGrid_QueryColumnDragging(object sender, Syncfusion.UI.Xaml.Grid.QueryColumnDraggingEventArgs e)
        {
            try
            {
                int protectedCount = 6;
                // Prevent dragging of frozen columns and dropping into their range
                if (e.From < protectedCount || e.To < protectedCount)
                {
                    e.Cancel = true;
                }
            }
            catch { }
        }

        /// <summary>
        /// Remplit (ou re‑remplit) la collection déjà liée au DataGrid
        /// en conservant les éventuels tri déjà appliqués.
        /// </summary>
        private void RefreshViewData(IEnumerable<ReconciliationViewData> items)
        {
            // 1️⃣  Sauvegarde du tri actuel
            var view = CollectionViewSource.GetDefaultView(_viewData);
            var savedSorting = view?.SortDescriptions?.ToList() ?? new List<SortDescription>();

            // 2️⃣  Nettoyage de la collection existante (pas de nouvelle instance)
            _viewData.Clear();
            foreach (var i in items)
                _viewData.Add(i);

            // 3️⃣  Restauration du tri
            var newView = CollectionViewSource.GetDefaultView(_viewData);
            newView.SortDescriptions.Clear();
            foreach (var sd in savedSorting)
                newView.SortDescriptions.Add(sd);
        }

        // Auto-open DatePicker calendar on edit start for faster date selection
        private void DatePicker_OpenOnLoad(object sender, RoutedEventArgs e)
        {
            try
            {
                var dp = sender as DatePicker;
                if (dp == null) return;
                // Ensure French culture visual formatting if needed
                try { dp.Language = System.Windows.Markup.XmlLanguage.GetLanguage("fr-FR"); } catch { }
                // Open the popup calendar immediately
                try { dp.IsDropDownOpen = true; } catch { }
            }
            catch { }
        }

        // Set date to today when clicking the calendar button
        private void SetDateToToday_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var button = sender as Button;
                if (button == null) return;

                var fieldName = button.Tag as string;
                if (string.IsNullOrEmpty(fieldName)) return;

                // Find the DatePicker in the same Grid
                var grid = button.Parent as Grid;
                if (grid == null) return;

                var datePicker = grid.Children.OfType<DatePicker>().FirstOrDefault();
                if (datePicker == null) return;

                // Set to today
                datePicker.SelectedDate = DateTime.Today;

                // Get the data context (the row)
                var row = datePicker.DataContext as RecoTool.Services.DTOs.ReconciliationViewData;
                if (row == null) return;

                // Update the property based on field name
                switch (fieldName)
                {
                    case "ActionDate":
                        row.ActionDate = DateTime.Today;
                        break;
                    case "FirstClaimDate":
                        row.FirstClaimDate = DateTime.Today;
                        break;
                    case "LastClaimDate":
                        row.LastClaimDate = DateTime.Today;
                        break;
                    case "ToRemindDate":
                        row.ToRemindDate = DateTime.Today;
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting date to today: {ex.Message}");
            }
        }

        

        

        private void ResultsDataGrid_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Syncfusion.UI.Xaml.Grid.SfDataGrid sfGrid)
                {
                    TryHookResultsGridScroll(sfGrid);
                    // (Ctrl+C is intercepted at UserControl level via constructor PreviewKeyDown.)
                }
            }
            catch { }
        }

        private void ResultsDataGrid_CopyInterceptKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            try
            {
                if (e.Key != System.Windows.Input.Key.C
                    || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
                    return;

                var sfGrid = (sender as Syncfusion.UI.Xaml.Grid.SfDataGrid)
                          ?? this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;

                var col = sfGrid.CurrentColumn;
                var item = sfGrid.CurrentItem;

                if (col != null && item != null)
                {
                    string text = TryGetCellText(col, item);
                    if (!string.IsNullOrEmpty(text))
                    {
                        try { Clipboard.SetDataObject(text, copy: true); } catch { }
                        try { ShowToast("Copied", durationSeconds: 2); } catch { }
                        e.Handled = true;
                        return;
                    }
                }

                // Fallback: copy selected rows as tab-separated text
                try
                {
                    var items = sfGrid.SelectedItems?.Cast<ReconciliationViewData>().ToList();
                    if (items != null && items.Count > 0)
                    {
                        var cols = sfGrid.Columns
                            .Where(c => !c.IsHidden && !string.IsNullOrWhiteSpace(c.MappingName))
                            .ToList();
                        var sb = new System.Text.StringBuilder();
                        foreach (var it in items)
                        {
                            var values = cols.Select(c =>
                            {
                                try
                                {
                                    var mn = c.MappingName;
                                    if (string.IsNullOrWhiteSpace(mn)) return string.Empty;
                                    var p = it.GetType().GetProperty(mn);
                                    return p?.GetValue(it)?.ToString() ?? string.Empty;
                                }
                                catch { return string.Empty; }
                            });
                            sb.AppendLine(string.Join("\t", values));
                        }
                        var result = sb.ToString().TrimEnd();
                        if (!string.IsNullOrEmpty(result))
                        {
                            try { Clipboard.SetDataObject(result, copy: true); } catch { }
                            e.Handled = true;
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        // Open a popup view showing rows that share the same DWINGS_InvoiceID (BGI) or, if absent, the same InternalInvoiceReference
        private void MatchedIndicator_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Bring the window where the click occurred to front to ensure correct z-order
                try
                {
                    var owner = Window.GetWindow(this);
                    if (owner != null)
                    {
                        owner.Activate();
                        owner.Topmost = true; owner.Topmost = false; // bring-to-front trick without staying topmost
                    }
                }
                catch { }

                var tb = sender as TextBlock;
                var rowData = tb?.DataContext as ReconciliationViewData;
                OpenMatchedPopup(rowData);
            }
            catch { }
        }

        // PERF: Single ScrollToVerticalOffset instead of LineUp/LineDown loop
        private void ResultsDataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                var sfGrid = sender as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;
                if (_resultsScrollViewer == null)
                {
                    _resultsScrollViewer = VisualTreeHelpers.FindDescendant<ScrollViewer>(sfGrid);
                }
                if (_resultsScrollViewer != null)
                {
                    e.Handled = true;
                    double rowsPerNotch = 3.0;
                    double pixelDelta = (e.Delta / 120.0) * rowsPerNotch * 30.0;
                    double newOffset = _resultsScrollViewer.VerticalOffset - pixelDelta;
                    newOffset = Math.Max(0, Math.Min(newOffset, _resultsScrollViewer.ScrollableHeight));
                    _resultsScrollViewer.ScrollToVerticalOffset(newOffset);
                }
            }
            catch { }
        }

        private static string TryGetCellText(Syncfusion.UI.Xaml.Grid.GridColumn column, object dataItem)
        {
            try
            {
                // Use MappingName for reflection-based value retrieval
                var path = column.MappingName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var prop = dataItem.GetType().GetProperty(path);
                    if (prop != null)
                    {
                        var val = prop.GetValue(dataItem);
                        return val?.ToString() ?? string.Empty;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        // Populate context menu via SfDataGrid RecordContextMenu Opened event
        private void DataGridRow_ContextMenuOpening(object sender, RoutedEventArgs e)
        {
            try
            {
                var cm = sender as ContextMenu;
                if (cm == null) return;

                // SfDataGrid sets the ContextMenu DataContext to GridRecordContextMenuInfo
                var menuInfo = cm.DataContext as Syncfusion.UI.Xaml.Grid.GridRecordContextMenuInfo;
                var rowData = menuInfo?.Record as ReconciliationViewData;
                if (rowData == null) return;

                // Resolve the root submenus
                MenuItem actionRoot = cm.Items.OfType<MenuItem>().FirstOrDefault(mi => (mi.Tag as string) == "Action");
                MenuItem kpiRoot = cm.Items.OfType<MenuItem>().FirstOrDefault(mi => (mi.Tag as string) == "KPI");
                MenuItem incRoot = cm.Items.OfType<MenuItem>().FirstOrDefault(mi => (mi.Tag as string) == "Incident Type");
                MenuItem riskyRoot = cm.Items.OfType<MenuItem>().FirstOrDefault(mi => (mi.Tag as string) == "Risky");

                void Populate(MenuItem root, string category)
                {
                    if (root == null) return;
                    root.Items.Clear();

                    var options = GetUserFieldOptionsForRow(category, rowData).ToList();

                    var clearItem = new MenuItem { Header = $"Clear {category}", Tag = category, CommandParameter = null, DataContext = rowData };
                    clearItem.Click += QuickSetUserFieldMenuItem_Click;
                    bool hasValue = (string.Equals(category, "Action", StringComparison.OrdinalIgnoreCase) && rowData.Action.HasValue)
                                     || (string.Equals(category, "KPI", StringComparison.OrdinalIgnoreCase) && rowData.KPI.HasValue)
                                     || (string.Equals(category, "Risky", StringComparison.OrdinalIgnoreCase) && rowData.ReasonNonRisky.HasValue)
                                     || (string.Equals(category, "Incident Type", StringComparison.OrdinalIgnoreCase) && rowData.IncidentType.HasValue);
                    clearItem.IsEnabled = hasValue;
                    root.Items.Add(clearItem);

                    foreach (var opt in options)
                    {
                        var mi = new MenuItem
                        {
                            Header = opt.USR_FieldName,
                            Tag = category,
                            CommandParameter = opt.USR_ID,
                            DataContext = rowData,
                            IsCheckable = true,
                            IsChecked = (string.Equals(category, "Action", StringComparison.OrdinalIgnoreCase) && rowData.Action == opt.USR_ID)
                                        || (string.Equals(category, "KPI", StringComparison.OrdinalIgnoreCase) && rowData.KPI == opt.USR_ID)
                                        || (string.Equals(category, "Risky", StringComparison.OrdinalIgnoreCase) && rowData.ReasonNonRisky == opt.USR_ID)
                                        || (string.Equals(category, "Incident Type", StringComparison.OrdinalIgnoreCase) && rowData.IncidentType == opt.USR_ID)
                        };
                        mi.Click += QuickSetUserFieldMenuItem_Click;
                        root.Items.Add(mi);
                    }

                    root.IsEnabled = options.Any() || hasValue;
                }

                Populate(actionRoot, "Action");
                Populate(kpiRoot, "KPI");
                Populate(incRoot, "Incident Type");
                Populate(riskyRoot, "Risky");

                // Disable editing submenus on archived rows
                if (rowData.IsDeleted)
                {
                    if (actionRoot != null) actionRoot.IsEnabled = false;
                    if (kpiRoot != null) kpiRoot.IsEnabled = false;
                    if (incRoot != null) incRoot.IsEnabled = false;
                    if (riskyRoot != null) riskyRoot.IsEnabled = false;
                }

                // Wire "Add to Linking Basket" click handler dynamically (can't use Click= in Style ContextMenu)
                var linkingItem = cm.Items.OfType<MenuItem>().FirstOrDefault(mi => (mi.Tag as string) == "LinkingBasket");
                if (linkingItem != null)
                {
                    linkingItem.DataContext = rowData;
                    linkingItem.Header = $"🔗 Add to Linking Basket ({LinkingBasketCount})";
                    linkingItem.Click -= AddToLinkingBasket_Click;
                    linkingItem.Click += AddToLinkingBasket_Click;
                    linkingItem.IsEnabled = !rowData.IsDeleted;
                }

                // Add Set Comment action applicable to multi-selection
                try
                {
                    // Avoid duplicates: remove previous injected items
                    var existing = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "__SetComment__");
                    if (existing != null) cm.Items.Remove(existing);
                    foreach (var mi in cm.Items.OfType<MenuItem>().Where(m =>
                                 (m.Tag as string) == "__Take__"
                              || (m.Tag as string) == "__SetReminder__"
                              || (m.Tag as string) == "__MarkActionDone__"
                              || (m.Tag as string) == "__MarkActionPending__"
                              || (m.Tag as string) == "__AddRuleFromLine__"
                              || (m.Tag as string) == "__Copy__"
                              || (m.Tag as string) == "__SearchBGI__"
                              || (m.Tag as string) == "__DebugRules__"
                              || (m.Tag as string) == "__ActionStatusMenu__"
                              || (m.Tag as string) == "__SetFirstClaimToday__"
                              || (m.Tag as string) == "__SetLastClaimToday__"
                              || (m.Tag as string) == "__CheckWithFree__"
                              || (m.Tag as string) == "__OpenGrouped__"
                              || (m.Tag as string) == "__ProcessDwings__"
                              || (m.Tag as string) == "__SpiritGene__").ToList())
                    {
                        cm.Items.Remove(mi);
                    }
                    foreach (var sp in cm.Items.OfType<Separator>().Where(s => (s.Tag as string) == "__InjectedSep__").ToList())
                    {
                        cm.Items.Remove(sp);
                    }

                    cm.Items.Add(new Separator { Tag = "__InjectedSep__" });
                    
                    // Add "Open Other Account grouped lines" if cross-account match exists
                    if (rowData.IsMatchedAcrossAccounts)
                    {
                        var openGroupedItem = new MenuItem { Header = "Open Other Account grouped lines", Tag = "__OpenGrouped__", DataContext = rowData };
                        openGroupedItem.Click += (s2, e2) =>
                        {
                            try
                            {
                                OpenMatchedPopup(rowData);
                            }
                            catch { }
                        };
                        cm.Items.Add(openGroupedItem);
                    }
                    
                    bool isArchived = rowData.IsDeleted;

                    var takeItem = new MenuItem { Header = "Take (Assign to me)", Tag = "__Take__", DataContext = rowData, IsEnabled = !isArchived };
                    takeItem.Click += QuickTakeMenuItem_Click;
                    cm.Items.Add(takeItem);
                    var reminderItem = new MenuItem { Header = "Set Reminder Date…", Tag = "__SetReminder__", DataContext = rowData, IsEnabled = !isArchived };
                    reminderItem.Click += QuickSetReminderMenuItem_Click;
                    cm.Items.Add(reminderItem);
                    var actionStatusMenu = new MenuItem { Header = "Set Action Status", Tag = "__ActionStatusMenu__", IsEnabled = !isArchived };
                    var doneItem = new MenuItem { Header = "DONE", Tag = "__MarkActionDone__", DataContext = rowData };
                    doneItem.Click += QuickMarkActionDoneMenuItem_Click;
                    var pendingItem = new MenuItem { Header = "PENDING", Tag = "__MarkActionPending__", DataContext = rowData };
                    pendingItem.Click += QuickMarkActionPendingMenuItem_Click;
                    actionStatusMenu.Items.Add(doneItem);
                    actionStatusMenu.Items.Add(pendingItem);
                    cm.Items.Add(actionStatusMenu);

                    // Process DWINGS Blue Button – visible when the row is grouped (both P+R sides exist) and NOT archived
                    if (rowData.IsMatchedAcrossAccounts && !isArchived
                        && (!string.IsNullOrWhiteSpace(rowData.DWINGS_BGPMT) || !string.IsNullOrWhiteSpace(rowData.InternalInvoiceReference)))
                    {
                        var dwingsItem = new MenuItem
                        {
                            Header = "⚡ Process DWINGS Blue Button",
                            Tag = "__ProcessDwings__",
                            DataContext = rowData,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = System.Windows.Media.Brushes.RoyalBlue
                        };
                        dwingsItem.Click += SingleProcessDwings_Click;
                        cm.Items.Add(dwingsItem);
                    }

                    var firstClaimDate = new MenuItem { Header = "Set First Claim Date = ?", Tag = "__SetFirstClaimToday__", DataContext = rowData, IsEnabled = !isArchived };
                    firstClaimDate.Click += QuickSetFirstClaimTodayMenuItem_Click;
                    cm.Items.Add(firstClaimDate);
                    var lastClaimDate = new MenuItem { Header = "Set Last Claim Date = ?", Tag = "__SetLastClaimToday__", DataContext = rowData, IsEnabled = !isArchived };
                    lastClaimDate.Click += QuickSetLastClaimTodayMenuItem_Click;
                    cm.Items.Add(lastClaimDate);
                    var addRuleItem = new MenuItem { Header = "Add Rule based on this line…", Tag = "__AddRuleFromLine__", DataContext = rowData, IsEnabled = !isArchived };
                    addRuleItem.Click += QuickAddRuleFromLineMenuItem_Click;
                    cm.Items.Add(addRuleItem);
                    var commentItem = new MenuItem { Header = "Set Comment…", Tag = "__SetComment__", IsEnabled = !isArchived };
                    commentItem.Click += QuickSetCommentMenuItem_Click;
                    cm.Items.Add(commentItem);

                    // Copy submenu (ID / BGI Ref / All line with header)
                    var copyRoot = new MenuItem { Header = "Copy", Tag = "__Copy__" };
                    var copyId = new MenuItem { Header = "ID" };
                    copyId.Click += (s2, e2) => CopySelectionIds();
                    var copyDwInvoice = new MenuItem { Header = "DWINGS Invoice ID (BGI)" };
                    copyDwInvoice.Click += (s2, e2) => CopySelectionDwInvoiceId();
                    var copyDwBgpmt = new MenuItem { Header = "DWINGS BGPMT" };
                    copyDwBgpmt.Click += (s2, e2) => CopySelectionDwCommissionBgpmt();
                    var copyDwGuarantee = new MenuItem { Header = "DWINGS Guarantee ID" };
                    copyDwGuarantee.Click += (s2, e2) => CopySelectionDwGuaranteeId();
                    var copyAll = new MenuItem { Header = "All line (with header)" };
                    copyAll.Click += (s2, e2) => CopySelectionAllLines(includeHeader: true);
                    copyRoot.Items.Add(copyId);
                    copyRoot.Items.Add(copyDwInvoice);
                    copyRoot.Items.Add(copyDwBgpmt);
                    copyRoot.Items.Add(copyDwGuarantee);
                    copyRoot.Items.Add(new Separator());
                    copyRoot.Items.Add(copyAll);
                    cm.Items.Add(copyRoot);

                    var checkFreeItem = new MenuItem
                    {
                        Header = "Check with Free …",
                        Tag = "__CheckWithFree__",
                        DataContext = rowData               // la ligne sur laquelle on a cliqué
                    };
                    checkFreeItem.Click += QuickCheckWithFreeMenuItem_Click;
                    cm.Items.Add(checkFreeItem);

                    // Search BGI in DWINGS (open or reuse Invoice Finder)
                    var searchBgi = new MenuItem { Header = "Search BGI…", Tag = "__SearchBGI__", DataContext = rowData };
                    searchBgi.Click += SearchBgiMenuItem_Click;
                    cm.Items.Add(searchBgi);

                    // SpiritGene transaction search
                    var spiritItem = new MenuItem { Header = "🔍 Search in SpiritGene…", Tag = "__SpiritGene__", DataContext = rowData };
                    spiritItem.Click += SpiritGeneSearchMenuItem_Click;
                    cm.Items.Add(spiritItem);

                    // Debug Rules - show detailed rule evaluation
                    cm.Items.Add(new Separator { Tag = "__InjectedSep__" });
                    var debugRulesItem = new MenuItem { Header = "Debug Rules…", Tag = "__DebugRules__", DataContext = rowData };
                    debugRulesItem.Click += DebugRulesMenuItem_Click;
                    cm.Items.Add(debugRulesItem);
                }
                catch { }
            }
            catch { }
        }


        private async void QuickCheckWithFreeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Récupération de la ou des lignes concernées
                var rows = GetCurrentSelection();                // méthode déjà existante
                if (rows.Count == 0) return;

                // Cancel any running Free API batch
                try { _freeApiCts?.Cancel(); } catch { }
                _freeApiCts = new System.Threading.CancellationTokenSource();

                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    await CheckWithFreeAsync(rows, _freeApiCts.Token);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
            }
            catch (OperationCanceledException)
            {
                ShowToast("Free API check cancelled", durationSeconds: 2);
            }
            catch (Exception ex)
            {
                ShowError($"Erreur lors de l'appel Free API : {ex.Message}");
            }
        }

        /// <summary>
        /// Parcourt chaque <see cref="ReconciliationViewData"/> et interroge le service Free.
        /// Remplit les champs DWINGS (Invoice, BGPMT, Guarantee) ainsi que MbawData,
        /// puis **persiste chaque ligne en base** pour que les données ne soient pas perdues.
        /// </summary>
        private async Task CheckWithFreeAsync(IReadOnlyList<ReconciliationViewData> rows, System.Threading.CancellationToken ct)
        {
            int successCount = 0;
            int errorCount = 0;
            var savedRows = new List<ReconciliationViewData>();

            for (int i = 0; i < rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows[i];

                try
                {
                    // 1️⃣ Paramètres de recherche
                    var day = row.Operation_Date ?? row.Value_Date ?? DateTime.Today;
                    var reference = row.Reconciliation_Num ?? row.RawLabel ?? string.Empty;
                    var serviceCode = CurrentCountryObject?.CNT_ServiceCode;

                    // 2️⃣ Call Free API with per-request timeout (300s)
                    string payload;
                    using (var requestCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        requestCts.CancelAfter(TimeSpan.FromSeconds(300));
                        try
                        {
                            payload = await _freeApi.SearchAsync(day, reference, serviceCode);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Per-request timeout — mark as timeout, don't abort batch
                            row.MbawData = "Timeout";
                            errorCount++;
                            continue;
                        }
                    }

                    // 3️⃣ Mémorisation du résultat (ou du sentinel "Not found")
                    row.MbawData = string.IsNullOrWhiteSpace(payload) ? "Not found" : payload;

                    // 4️⃣ Extraction des tokens si le payload existe
                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        var foundBgpmt = DwingsLinkingHelper.ExtractBgpmtToken(payload);
                        var foundBgi = DwingsLinkingHelper.ExtractBgiToken(payload);
                        var foundGid = DwingsLinkingHelper.ExtractGuaranteeId(payload);

                        if (!string.IsNullOrWhiteSpace(foundGid) && string.IsNullOrWhiteSpace(row.DWINGS_GuaranteeID))
                            row.DWINGS_GuaranteeID = foundGid;

                        if (!string.IsNullOrWhiteSpace(foundBgi) && string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID))
                            row.DWINGS_InvoiceID = foundBgi;

                        if (!string.IsNullOrWhiteSpace(foundBgpmt) && string.IsNullOrWhiteSpace(row.DWINGS_BGPMT))
                            row.DWINGS_BGPMT = foundBgpmt;

                        // Fallback: use GuaranteeCache only if no structured G/N-ref was found by regex
                        if (string.IsNullOrWhiteSpace(row.DWINGS_GuaranteeID))
                        {
                            var officialGid = GuaranteeCache.FindGuaranteeId(payload);
                            if (officialGid != null)
                                row.DWINGS_GuaranteeID = officialGid;
                        }
                    }

                    // 5️⃣ CRITICAL: Persist to database so data survives close/reopen
                    try
                    {
                        await SaveEditedRowAsync(row);
                        savedRows.Add(row);
                        successCount++;
                    }
                    catch (Exception saveEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CheckWithFree] Save failed for {row.ID}: {saveEx.Message}");
                        errorCount++;
                    }
                }
                catch (OperationCanceledException) { throw; } // re-throw user cancellation
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CheckWithFree] Error for row {row.ID}: {ex.Message}");
                    errorCount++;
                }
            }

            // 6️⃣ Update KPIs to reflect changes in real-time
            try { UpdateKpis(_filteredData); } catch { }

            // 7️⃣ Notification UI
            var msg = $"{successCount}/{rows.Count} ligne(s) enrichies via Free API";
            if (errorCount > 0) msg += $" ({errorCount} erreur(s))";
            ShowToast(msg, durationSeconds: 4);

            // 8️⃣ Schedule background sync (debounced) instead of hard Refresh
            try { ScheduleBulkPushDebounced(); } catch { }
        }

        /// <summary>
        /// Reçoit un SQL de filtre sauvegardé depuis la page parente.
        /// S'il contient un snapshot JSON, restaure les champs UI et isole le WHERE pur
        /// pour le backend. Sinon, transmet tel quel au backend. N'effectue pas de chargement ici.
        /// </summary>
        public void ApplySavedFilterSql(string sql)
        {
            try
            {
                // Capture initial filter state for reset button
                _initialFilterSql = sql;
                
                if (string.IsNullOrWhiteSpace(sql))
                {
                    _backendFilterSql = null;
                    // Any change in backend filter invalidates previously preloaded data
                    _preloadedAllData = null;
                    return;
                }

                if (TryExtractPresetFromSql(sql, out var preset, out var pureWhere))
                {
                    // Restaurer l'UI de la vue selon le snapshot 
                    ApplyFilterPreset(preset);
                    // Recalculer une WHERE clause propre basée sur l'état courant (sans compte du preset)
                    _backendFilterSql = GenerateWhereClause();
                }
                else
                {
                    // Aucun snapshot: transmettre au backend en retirant le filtre compte s'il est présent
                    _backendFilterSql = StripAccountFromWhere(sql);
                }
                // Backend filter changed: drop preloaded data to force a fresh load
                _preloadedAllData = null;
            }
            catch
            {
                // En cas d'erreur de parsing, fallback sur le SQL brut
                _backendFilterSql = StripAccountFromWhere(sql);
                _preloadedAllData = null;
            }
        }

        /// <summary>
        /// Constructeur par défaut
        /// </summary>
        private ReconciliationView()
        {
            InitializeComponent();
            InitializeData();
            DataContext = this;
            Loaded += ReconciliationView_Loaded;
            Unloaded += ReconciliationView_Unloaded;
            InitializeFilterDebounce();
            InitializeQuickSearchCommand();
            InitializeShortcutCommands();
            SubscribeToSyncEvents();
            RefreshCompleted += (s, e) => _hasLoadedOnce = true;
            try { VM.PropertyChanged += VM_PropertyChanged; } catch { }
            // Intercept Ctrl+C at UserControl level (tunnels before Syncfusion's OnPreviewKeyDown).
            this.PreviewKeyDown += ResultsDataGrid_CopyInterceptKeyDown;
        }

        public ReconciliationView(ReconciliationService reconciliationService, OfflineFirstService offlineFirstService, FreeApiService freeApi) : this()
        {
            _reconciliationService = reconciliationService;
            _offlineFirstService = offlineFirstService;
            _freeApi = freeApi;

            // Synchroniser avec la country courante du service
            _currentCountryId = _offlineFirstService?.CurrentCountry?.CNT_Id;
            // Notifier que les référentiels sont disponibles pour les liaisons XAML
            OnPropertyChanged(nameof(AllUserFields));
            OnPropertyChanged(nameof(CurrentCountryObject));

            InitializeFromServices();
        }

        public void SetTodoSessionTracker(TodoListSessionTracker tracker, int todoId)
        {
            _todoSessionTracker = tracker;
            _currentTodoId = todoId;

            // Initialize the multi-user warning banner
            try
            {
                if (SessionWarningBanner != null && tracker != null && todoId > 0)
                {
                    _ = SessionWarningBanner.InitializeAsync(tracker, todoId);
                }
            }
            catch { }

            // Subscribe to rule application events to show floating toasts (edit/run-now)
            try
            {
                if (_reconciliationService != null)
                {
                    _reconciliationService.RuleApplied -= ReconciliationService_RuleApplied;
                    _reconciliationService.RuleApplied += ReconciliationService_RuleApplied;
                }
            }
            catch { }

            // Enable header context menu for column visibility
            this.Loaded += (s, e) =>
            {
                try
                {
                    var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                    if (sfGrid != null)
                    {
                        try { sfGrid.GotFocus -= ResultsDataGrid_GotFocus; } catch { }
                        try { sfGrid.GotFocus += ResultsDataGrid_GotFocus; } catch { }
                        sfGrid.PreviewMouseRightButtonUp -= ResultsDataGrid_PreviewMouseRightButtonUp;
                        sfGrid.PreviewMouseRightButtonUp += ResultsDataGrid_PreviewMouseRightButtonUp;
                        sfGrid.AllowSorting = true; // allow sorting on all columns
                        TryHookResultsGridScroll(sfGrid);
                    }
                }
                catch { }
            };

            try { this.GotFocus -= ReconciliationView_GotFocus; } catch { }
            try { this.GotFocus += ReconciliationView_GotFocus; } catch { }
        }

        private void ReconciliationView_GotFocus(object sender, RoutedEventArgs e)
        {
            try { ReconciliationViewFocusTracker.SetLastFocused(this); } catch { }
        }

        private void ResultsDataGrid_GotFocus(object sender, RoutedEventArgs e)
        {
            try { ReconciliationViewFocusTracker.SetLastFocused(this); } catch { }
        }

        // Open header context menu when user right-clicks a column header
        private void ResultsDataGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var sfGrid = sender as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;

                var source = e.OriginalSource as DependencyObject;
                var header = VisualTreeHelpers.FindParent<Syncfusion.UI.Xaml.Grid.GridHeaderCellControl>(source);
                if (header == null)
                {
                    // Not a header: let row context menu proceed
                    return;
                }

                e.Handled = true;

                // Determine which column was right-clicked
                var clickedColumn = header.Column;
                int clickedIdx = clickedColumn != null ? sfGrid.Columns.IndexOf(clickedColumn) : -1;
                int frozenCount = sfGrid.FrozenColumnCount;

                var cm = new ContextMenu();

                // Freeze/Unfreeze options
                if (clickedIdx >= 0)
                {
                    if (clickedIdx < frozenCount)
                    {
                        // Column is frozen — offer to unfreeze (set freeze boundary to this column)
                        var unfreezeItem = new MenuItem { Header = $"❄️ Unfreeze from here (freeze {clickedIdx})" };
                        unfreezeItem.Click += (s, ev) =>
                        {
                            try { sfGrid.FrozenColumnCount = clickedIdx; } catch { }
                        };
                        cm.Items.Add(unfreezeItem);
                    }
                    else
                    {
                        // Column is not frozen — offer to freeze up to this column
                        var freezeItem = new MenuItem { Header = $"🔒 Freeze up to here ({clickedIdx + 1} columns)" };
                        freezeItem.Click += (s, ev) =>
                        {
                            try { sfGrid.FrozenColumnCount = clickedIdx + 1; } catch { }
                        };
                        cm.Items.Add(freezeItem);
                    }

                    // Always show option to reset to default
                    var resetFreezeItem = new MenuItem { Header = "↩️ Reset freeze (6 columns)" };
                    resetFreezeItem.Click += (s, ev) =>
                    {
                        try { sfGrid.FrozenColumnCount = 6; } catch { }
                    };
                    cm.Items.Add(resetFreezeItem);
                    cm.Items.Add(new Separator());
                }

                // Manage columns option
                var manageItem = new MenuItem { Header = "⚙️ Manage columns…", FontWeight = FontWeights.SemiBold };
                manageItem.Click += (s, ev) =>
                {
                    try
                    {
                        var win = new ManageColumnsWindow(sfGrid);
                        try { win.Owner = Window.GetWindow(this); } catch { }
                        win.ShowDialog();
                    }
                    catch { }
                };
                cm.Items.Add(manageItem);

                cm.IsOpen = true;
            }
            catch { }
        }

        private void RulesAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new RulesAdminWindow();
                var owner = Window.GetWindow(this);
                if (owner != null) win.Owner = owner;
                win.Show();
            }
            catch { }
        }

        public void FlashLinkProposalHighlight()
        {
            try
            {
                var b = this.FindName("HeaderBorder") as Border;
                if (b == null) return;
                var prevBrush = b.BorderBrush;
                var prevThickness = b.BorderThickness;
                b.BorderBrush = Brushes.Red;
                b.BorderThickness = new Thickness(3);
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += (s, e) =>
                {
                    try
                    {
                        b.BorderBrush = prevBrush;
                        b.BorderThickness = prevThickness;
                    }
                    catch { }
                    finally { (s as DispatcherTimer)?.Stop(); }
                };
                t.Start();
            }
            catch { }
        }

        private void ReconciliationService_RuleApplied(object sender, ReconciliationService.RuleAppliedEventArgs e)
        {
            try
            {
                if (e == null) return;
                if (!string.Equals(e.Origin, "edit", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(e.Origin, "run-now", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => ReconciliationService_RuleApplied(sender, e));
                    return;
                }
                // Real-time update of the affected row (if visible)
                try
                {
                    var row = (_allViewData ?? new List<ReconciliationViewData>()).FirstOrDefault(r => string.Equals(r?.ID, e.ReconciliationId, StringComparison.OrdinalIgnoreCase));
                    if (row != null && !string.IsNullOrWhiteSpace(e.Outputs))
                    {
                        ApplyOutputsToRow(row, e.Outputs);
                    }
                }
                catch { }

                var summary = !string.IsNullOrWhiteSpace(e.Outputs) ? e.Outputs : e.Message;
                var text = string.IsNullOrWhiteSpace(summary)
                    ? $"Rule '{e.RuleId}' applied"
                    : $"Rule '{e.RuleId}' applied: {summary}";
                ShowToast(text, onClick: () =>
                {
                    try { OpenSingleReconciliationPopup(e.ReconciliationId); } catch { }
                });

                // Touch session file so other users see fresh activity
                try { _todoSessionTracker?.TouchSession(); } catch { }
            }
            catch { }
        }

        private void ShowToast(string text, Action onClick = null, int durationSeconds = 5)
        {
            try
            {
                var panel = this.FindName("ToastPanel") as Border;
                var tb = this.FindName("ToastText") as TextBlock;
                if (panel == null || tb == null) return;
                
                tb.Text = text ?? string.Empty;
                _toastClickAction = onClick;
                _toastTargetReconciliationId = null;

                // Fade in animation
                panel.Opacity = 0;
                panel.Visibility = Visibility.Visible;
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(300)
                };
                panel.BeginAnimation(Border.OpacityProperty, fadeIn);

                if (_toastTimer == null)
                {
                    _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
                    _toastTimer.Tick += (s, e) =>
                    {
                        try 
                        { 
                            // Fade out animation
                            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
                            {
                                From = 1,
                                To = 0,
                                Duration = TimeSpan.FromMilliseconds(300)
                            };
                            fadeOut.Completed += (_, __) => { panel.Visibility = Visibility.Collapsed; };
                            panel.BeginAnimation(Border.OpacityProperty, fadeOut);
                        } 
                        catch { }
                        finally { _toastTimer?.Stop(); }
                    };
                }
                else
                {
                    _toastTimer.Stop();
                    _toastTimer.Interval = TimeSpan.FromSeconds(durationSeconds);
                }
                _toastTimer.Start();
            }
            catch { }
        }

        private void ToastPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                _toastClickAction?.Invoke();
            }
            catch { }
            finally
            {
                try
                {
                    var panel = this.FindName("ToastPanel") as Border;
                    if (panel != null) panel.Visibility = Visibility.Collapsed;
                }
                catch { }
            }
        }

        private void OpenSingleReconciliationPopup(string reconciliationId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(reconciliationId)) return;
                var where = $"r.ID = '{reconciliationId.Replace("'", "''")}'";

                var targetView = new ReconciliationView(_reconciliationService, _offlineFirstService, _freeApi);
                try { targetView.SyncCountryFromService(refresh: false); } catch { }
                try { targetView.ApplySavedFilterSql(where); } catch { }
                try { targetView.Refresh(); } catch { }

                var win = new Window
                {
                    Title = $"Reconciliation - {reconciliationId}",
                    Content = targetView,
                    Owner = Window.GetWindow(this),
                    Width = 1100,
                    Height = 750,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                targetView.CloseRequested += (s, e) => { try { win.Close(); } catch { } };
                win.Show();
            }
            catch { }
        }

        private void ApplyOutputsToRow(ReconciliationViewData row, string outputs)
        {
            try
            {
                if (row == null || string.IsNullOrWhiteSpace(outputs)) return;
                var parts = outputs.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                int? newActionId = null;
                foreach (var p in parts)
                {
                    var kv = p.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    var key = kv[0].Trim();
                    var val = kv[1].Trim();
                    switch (key)
                    {
                        case "Action":
                            if (int.TryParse(val, out var a)) { row.Action = a; newActionId = a; }
                            break;
                        case "KPI":
                            if (int.TryParse(val, out var k)) row.KPI = k; break;
                        case "IncidentType":
                            if (int.TryParse(val, out var it)) row.IncidentType = it; break;
                        case "RiskyItem":
                            if (bool.TryParse(val, out var rb)) row.RiskyItem = rb; break;
                        case "ReasonNonRisky":
                            if (int.TryParse(val, out var rn)) row.ReasonNonRisky = rn; break;
                        case "ToRemind":
                            if (bool.TryParse(val, out var tr)) row.ToRemind = tr; break;
                        case "ToRemindDays":
                            if (int.TryParse(val, out var td))
                            {
                                try { row.ToRemindDate = DateTime.Today.AddDays(td); } catch { }
                            }
                            break;
                    }
                }

                // If Action was set, ensure status is PENDING and date is today, unless action is N/A
                if (newActionId.HasValue)
                {
                    try
                    {
                        var all = AllUserFields;
                        var isNA = UserFieldUpdateService.IsActionNA(newActionId.Value, all);
                        if (!isNA)
                        {
                            if (row.ActionStatus != false) row.ActionStatus = false; // PENDING
                            row.ActionDate = DateTime.Now;
                        }
                        else
                        {
                            row.ActionStatus = null;
                            row.ActionDate = null;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public ObservableCollection<ReconciliationViewData> ViewData
        {
            get => _viewData;
            set
            {
                _viewData = value;
                OnPropertyChanged(nameof(ViewData));
                // Keep VM collection in sync so the DataGrid (bound to VM.ViewData) updates
                try { VM.ViewData = value; } catch { }
            }
        }

        // Expose referentials for XAML bindings (ComboBox items/label resolution)
        public IReadOnlyList<UserField> AllUserFields => _offlineFirstService?.UserFields;
        public Country CurrentCountryObject => _offlineFirstService?.CurrentCountry;

        // Options moved to ReconciliationView.Options.cs

        public string CurrentView
        {
            get => _currentView;
            set
            {
                _currentView = value;
                OnPropertyChanged(nameof(CurrentView));
                UpdateViewTitle();
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                OnPropertyChanged(nameof(IsLoading));
                _canRefresh = !value;
                OnPropertyChanged(nameof(CanRefresh));
            }
        }

        #endregion

        #region Row ContextMenu (Quick Set: Action/KPI/Incident)
        // Moved to ReconciliationView.RowActions.cs
        #endregion

        #region IRefreshable Implementation

        public bool CanRefresh => _canRefresh && !string.IsNullOrEmpty(_currentCountryId);

        public event EventHandler RefreshStarted;
        public event EventHandler RefreshCompleted;

        /* moved to partial: DataLoading.cs (Refresh) */

        /* moved to partial: DataLoading.cs (RefreshAsync) */

        #endregion

        

        #region Bound Filter Properties

        public string FilterAccountId { get => VM.FilterAccountId; set { VM.FilterAccountId = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterAccountId)); ScheduleApplyFiltersDebounced(); } }
        public string FilterCurrency { get => VM.FilterCurrency; set { VM.FilterCurrency = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterCurrency)); ScheduleApplyFiltersDebounced(); } }

        public string FilterCountry { get => _filterCountry; set { _filterCountry = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterCountry)); ScheduleApplyFiltersDebounced(); } }
        public DateTime? FilterFromDate { get => VM.FilterFromDate; set { VM.FilterFromDate = value; OnPropertyChanged(nameof(FilterFromDate)); ScheduleApplyFiltersDebounced(); } }
        public DateTime? FilterToDate { get => VM.FilterToDate; set { VM.FilterToDate = value; OnPropertyChanged(nameof(FilterToDate)); ScheduleApplyFiltersDebounced(); } }
        public DateTime? FilterOperationDate { get => VM.FilterOperationDate; set { VM.FilterOperationDate = value; OnPropertyChanged(nameof(FilterOperationDate)); ScheduleApplyFiltersDebounced(); } }
        public DateTime? FilterDeletedDate { get => VM.FilterDeletedDate; set { VM.FilterDeletedDate = value; OnPropertyChanged(nameof(FilterDeletedDate)); ScheduleApplyFiltersDebounced(); } }
        public string FilterAmount { get => VM.FilterAmount; set { VM.FilterAmount = value; OnPropertyChanged(nameof(FilterAmount)); ScheduleApplyFiltersDebounced(); } }
        public string FilterReconciliationNum { get => VM.FilterReconciliationNum; set { VM.FilterReconciliationNum = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterReconciliationNum)); ScheduleApplyFiltersDebounced(); } }
        public string FilterRawLabel { get => VM.FilterRawLabel; set { VM.FilterRawLabel = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterRawLabel)); ScheduleApplyFiltersDebounced(); } }
        public string FilterEventNum { get => VM.FilterEventNum; set { VM.FilterEventNum = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterEventNum)); ScheduleApplyFiltersDebounced(); } }
        public string FilterComments { get => VM.FilterComments; set { VM.FilterComments = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterComments)); ScheduleApplyFiltersDebounced(); } }
        public string FilterClient { get => VM.FilterClient; set { VM.FilterClient = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterClient)); ScheduleApplyFiltersDebounced(); } }
        public string FilterDwGuaranteeId { get => VM.FilterDwGuaranteeId; set { VM.FilterDwGuaranteeId = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterDwGuaranteeId)); ScheduleApplyFiltersDebounced(); } }
        public string FilterDwCommissionId { get => VM.FilterDwCommissionId; set { VM.FilterDwCommissionId = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterDwCommissionId)); ScheduleApplyFiltersDebounced(); } }
        public string FilterDwInvoiceId { get => VM.FilterDwInvoiceId; set { VM.FilterDwInvoiceId = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterDwInvoiceId)); ScheduleApplyFiltersDebounced(); } }
        public string FilterDwRef { get => VM.FilterDwRef; set { VM.FilterDwRef = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterDwRef)); ScheduleApplyFiltersDebounced(); } }
        public string FilterStatus { get => VM.FilterStatus; set { VM.FilterStatus = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterStatus)); ScheduleApplyFiltersDebounced(); } }

        // New string-backed ComboBox filters
        public string FilterGuaranteeType { get => VM.FilterGuaranteeType; set { VM.FilterGuaranteeType = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterGuaranteeType)); ScheduleApplyFiltersDebounced(); } }

        public string FilterTransactionType { get => VM.CurrentFilter.TransactionType; set { VM.CurrentFilter.TransactionType = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterTransactionType)); ScheduleApplyFiltersDebounced(); } }

        // New: Transaction Type filter by enum id (matches DataAmbre.Category int)
        public int? FilterTransactionTypeId
        {
            get => VM.FilterTransactionTypeId;
            set
            {
                // Treat negative sentinel values (e.g., -1 for 'All') as null
                var coerced = (value.HasValue && value.Value < 0) ? (int?)null : value;
                VM.FilterTransactionTypeId = coerced;
                OnPropertyChanged(nameof(FilterTransactionTypeId));
                ScheduleApplyFiltersDebounced();
            }
        }

        // ID-backed referential filter wrappers
        public int? FilterActionId { get => VM.FilterActionId; set { VM.FilterActionId = value; OnPropertyChanged(nameof(FilterActionId)); ScheduleApplyFiltersDebounced(); } }
        public int? FilterKpiId { get => VM.FilterKpiId; set { VM.FilterKpiId = value; OnPropertyChanged(nameof(FilterKpiId)); ScheduleApplyFiltersDebounced(); } }
        public int? FilterIncidentTypeId { get => VM.FilterIncidentTypeId; set { VM.FilterIncidentTypeId = value; OnPropertyChanged(nameof(FilterIncidentTypeId)); ScheduleApplyFiltersDebounced(); } }

        public string FilterGuaranteeStatus { get => VM.FilterGuaranteeStatus; set { VM.FilterGuaranteeStatus = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterGuaranteeStatus)); ScheduleApplyFiltersDebounced(); } }

        // New: Assignee filter (user id string)
        public string FilterAssigneeId
        {
            get => VM.FilterAssigneeId;
            set { VM.FilterAssigneeId = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(nameof(FilterAssigneeId)); ScheduleApplyFiltersDebounced(); }
        }

        // New: Potential Duplicates filter (checkbox)
        public bool FilterPotentialDuplicates
        {
            get => VM.FilterPotentialDuplicates;
            set { VM.FilterPotentialDuplicates = value; OnPropertyChanged(nameof(FilterPotentialDuplicates)); ScheduleApplyFiltersDebounced(); }
        }

        // New: Unmatched and NewLines toggles
        public bool FilterUnmatched
        {
            get => VM.FilterUnmatched;
            set { VM.FilterUnmatched = value; OnPropertyChanged(nameof(FilterUnmatched)); ScheduleApplyFiltersDebounced(); }
        }

        public bool FilterNewLines
        {
            get => VM.FilterNewLines;
            set { VM.FilterNewLines = value; OnPropertyChanged(nameof(FilterNewLines)); ScheduleApplyFiltersDebounced(); }
        }

        // New filters: Action Done and Action Date range
        public bool? FilterActionDone
        {
            get => VM.FilterActionDone;
            set { VM.FilterActionDone = value; OnPropertyChanged(nameof(FilterActionDone)); ScheduleApplyFiltersDebounced(); }
        }

        public DateTime? FilterActionDate
        {
            get => VM.FilterActionDate;
            set { VM.FilterActionDate = value; OnPropertyChanged(nameof(FilterActionDate)); ScheduleApplyFiltersDebounced(); }
        }

        public bool? FilterToRemind
        {
            get => VM.FilterToRemind;
            set { VM.FilterToRemind = value; OnPropertyChanged(nameof(FilterToRemind)); ScheduleApplyFiltersDebounced(); }
        }

        public DateTime? FilterRemindDate
        {
            get => VM.FilterRemindDate;
            set { VM.FilterRemindDate = value; OnPropertyChanged(nameof(FilterRemindDate)); ScheduleApplyFiltersDebounced(); }
        }

        public string FilterLastReviewed
        {
            get => VM.FilterLastReviewed;
            set { VM.FilterLastReviewed = value == "All" ? null : value; OnPropertyChanged(nameof(FilterLastReviewed)); ScheduleApplyFiltersDebounced(); }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gestion du double-clic sur une ligne Ambre (code-behind pur)
        /// </summary>
        private void OnAmbreItemDoubleClick(DataAmbre item)
        {
            try
            {
                // Open detail of an Ambre line
                MessageBox.Show($"Ambre Detail - ID: {item.ID}\nAccount: {item.Account_ID}\nAmount: {item.SignedAmount:N2}\nCurrency: {item.CCY}\nDate: {item.Operation_Date:d}",
                    "Ambre Detail", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        // --- Copy helpers ---
        private List<ReconciliationViewData> GetCurrentSelection()
        {
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return new List<ReconciliationViewData>();
                var items = sfGrid.SelectedItems?.Cast<ReconciliationViewData>().ToList() ?? new List<ReconciliationViewData>();
                if (items.Count == 0 && sfGrid.CurrentItem is ReconciliationViewData one) items.Add(one);
                return items;
            }
            catch { return new List<ReconciliationViewData>(); }
        }

        private void CopySelectionIds()
        {
            try
            {
                var items = GetCurrentSelection();
                if (items.Count == 0) return;
                var sb = new StringBuilder();
                foreach (var it in items)
                {
                    sb.AppendLine(it?.ID ?? string.Empty);
                }
                Clipboard.SetText(sb.ToString());
            }
            catch { }
        }

        private void CopySelectionDwInvoiceId()
        {
            try
            {
                var items = GetCurrentSelection();
                if (items.Count == 0) return;
                var sb = new StringBuilder();
                foreach (var it in items)
                    sb.AppendLine(it?.DWINGS_InvoiceID ?? string.Empty);
                Clipboard.SetText(sb.ToString());
            }
            catch { }
        }

        private void CopySelectionDwCommissionBgpmt()
        {
            try
            {
                var items = GetCurrentSelection();
                if (items.Count == 0) return;
                var sb = new StringBuilder();
                foreach (var it in items)
                    sb.AppendLine(it?.DWINGS_BGPMT ?? string.Empty);
                Clipboard.SetText(sb.ToString());
            }
            catch { }
        }

        private void CopySelectionDwGuaranteeId()
        {
            try
            {
                var items = GetCurrentSelection();
                if (items.Count == 0) return;
                var sb = new StringBuilder();
                foreach (var it in items)
                    sb.AppendLine(it?.DWINGS_GuaranteeID ?? string.Empty);
                Clipboard.SetText(sb.ToString());
            }
            catch { }
        }

        private void SearchBgiMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = GetCurrentSelection().FirstOrDefault();
                if (row == null) return;

                // Find existing window or create new
                var existing = Application.Current?.Windows?.OfType<InvoiceFinderWindow>()?.FirstOrDefault();
                InvoiceFinderWindow win = existing ?? new InvoiceFinderWindow();
                if (win.Owner == null)
                {
                    try { win.Owner = Window.GetWindow(this); } catch { }
                }
                try { win.Show(); } catch { }
                try { win.Activate(); } catch { }

                if (!string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID))
                {
                    try { win.SetSearchInvoiceId(row.DWINGS_InvoiceID); } catch { }
                }
                else if (!string.IsNullOrWhiteSpace(row.DWINGS_GuaranteeID))
                {
                    try { win.SetSearchGuaranteeId(row.DWINGS_GuaranteeID); } catch { }
                }
            }
            catch { }
        }

        private async void DebugRulesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menuItem = sender as MenuItem;
                var rowData = menuItem?.DataContext as ReconciliationViewData;
                if (rowData == null || string.IsNullOrWhiteSpace(rowData.ID)) return;

                if (_reconciliationService == null)
                {
                    MessageBox.Show("Reconciliation service not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get debug information from the service
                var (context, evaluations) = await _reconciliationService.GetRuleDebugInfoAsync(rowData.ID);
                if (context == null || evaluations == null || evaluations.Count == 0)
                {
                    MessageBox.Show("Unable to load rule debug information for this line.", "Debug Rules", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Build summary strings
                var lineInfo = $"ID: {rowData.ID}  |  Account: {rowData.Account_ID}  |  Amount: {rowData.SignedAmount:N2}";
                var contextInfo = $"IsPivot: {context.IsPivot}  |  Country: {context.CountryId}  |  " +
                                 $"TransactionType: {context.TransactionType ?? "(null)"}  |  " +
                                 $"GuaranteeType: {context.GuaranteeType ?? "(null)"}  |  " +
                                 $"IsGrouped: {context.IsGrouped?.ToString() ?? "(null)"}  |  " +
                                 $"HasDwingsLink: {context.HasDwingsLink?.ToString() ?? "(null)"}";

                // Convert to display items
                var debugItems = new List<RuleDebugItem>();
                int displayOrder = 1;
                foreach (var eval in evaluations)
                {
                    var item = new RuleDebugItem
                    {
                        DisplayOrder = displayOrder++,
                        RuleName = eval.Rule.RuleId ?? "(unnamed)",
                        IsEnabled = eval.IsEnabled,
                        IsMatch = eval.IsMatch,
                        MatchStatus = eval.IsMatch ? "✓ MATCH" : (eval.IsEnabled ? "✗ No Match" : "⊘ Disabled"),
                        OutputAction = eval.Rule.OutputActionId.HasValue 
                            ? EnumHelper.GetActionName(eval.Rule.OutputActionId.Value, _offlineFirstService?.UserFields) 
                            : "-",
                        OutputKPI = eval.Rule.OutputKpiId.HasValue 
                            ? EnumHelper.GetKPIName(eval.Rule.OutputKpiId.Value, _offlineFirstService?.UserFields) 
                            : "-",
                        Conditions = eval.Conditions.Select(c => new ConditionDebugItem
                        {
                            Field = c.Field,
                            Expected = c.Expected,
                            Actual = c.Actual,
                            IsMet = c.IsMet,
                            Status = c.IsMet ? "✓" : "✗"
                        }).ToList()
                    };
                    debugItems.Add(item);
                }

                // Create and show the debug window
                var debugWindow = new RuleDebugWindow();
                debugWindow.SetDebugInfo(lineInfo, contextInfo, debugItems);
                try { debugWindow.Owner = Window.GetWindow(this); } catch { }
                debugWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error displaying rule debug information: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopySelectionBgiRef()
        {
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                var items = GetCurrentSelection();
                if (sfGrid == null || items.Count == 0) return;

                // Find the column bound to the displayed "BGI Ref" header
                string headerName = "BGI Ref";
                var col = sfGrid.Columns.FirstOrDefault(c => string.Equals(c.HeaderText, headerName, StringComparison.OrdinalIgnoreCase));
                // Use MappingName; fallback to data property if column not found
                string path = col?.MappingName;
                if (string.IsNullOrWhiteSpace(path)) path = nameof(ReconciliationViewData.Receivable_DWRefFromAmbre);

                var sb = new StringBuilder();
                foreach (var it in items)
                {
                    sb.AppendLine(GetPropertyString(it, path));
                }
                Clipboard.SetText(sb.ToString());
            }
            catch { }
        }

        private void CopySelectionAllLines(bool includeHeader)
        {
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                var items = GetCurrentSelection();
                if (sfGrid == null || items.Count == 0) return;

                // Build list of exportable columns in current display order
                var cols = sfGrid.Columns
                    .Where(c => !c.IsHidden)
                    .Where(c => !string.IsNullOrWhiteSpace(c.HeaderText))
                    .Where(c => !string.IsNullOrWhiteSpace(c.MappingName))
                    .ToList();

                var sb = new StringBuilder();
                // Header
                if (includeHeader)
                {
                    sb.AppendLine(string.Join("\t", cols.Select(c => c.HeaderText?.Trim())));
                }

                foreach (var it in items)
                {
                    var values = cols.Select(c => GetPropertyString(it, c.MappingName));
                    sb.AppendLine(string.Join("\t", values));
                }

                Clipboard.SetText(sb.ToString());
            }
            catch { }
        }

        private string GetPropertyString(object obj, string path)
        {
            if (obj == null || string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                var prop = obj.GetType().GetProperty(path);
                if (prop == null) return string.Empty;
                var val = prop.GetValue(obj);
                if (val == null) return string.Empty;
                // Format dates like grid
                if (val is DateTime dt) return dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                if (val is DateTime ndt) return ndt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                if (val is decimal dec) return dec.ToString("N2", CultureInfo.InvariantCulture);
                if (val is decimal ndec) return ndec.ToString("N2", CultureInfo.InvariantCulture);
                return Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch { return string.Empty; }
        }


        /// <summary>
        /// Obtient la valeur d'une TextBox
        /// </summary>
        private string GetTextBoxValue(string name)
        {
            var textBox = FindName(name) as TextBox;
            return textBox?.Text?.Trim();
        }

        /// <summary>
        /// Obtient une valeur décimale d'une TextBox
        /// </summary>
        private decimal? GetDecimalFromTextBox(string name)
        {
            var value = GetTextBoxValue(name);
            return decimal.TryParse(value, out decimal result) ? result : (decimal?)null;
        }

        /// <summary>
        /// Obtient la valeur d'un DatePicker
        /// </summary>
        private DateTime? GetDatePickerValue(string name)
        {
            var datePicker = FindName(name) as DatePicker;
            return datePicker?.SelectedDate;
        }

        /// <summary>
        /// Obtient la valeur entière d'un ComboBox
        /// </summary>
        private int? GetComboBoxIntValue(string name)
        {
            var comboBox = FindName(name) as ComboBox;
            if (comboBox?.SelectedValue != null && int.TryParse(comboBox.SelectedValue.ToString(), out int result))
                return result;
            return null;
        }

        /// <summary>
        /// Efface une TextBox
        /// </summary>
        private void ClearTextBox(string name)
        {
            var textBox = FindName(name) as TextBox;
            if (textBox != null) textBox.Text = string.Empty;
        }

        /// <summary>
        /// Efface un DatePicker
        /// </summary>
        private void ClearDatePicker(string name)
        {
            var datePicker = FindName(name) as DatePicker;
            if (datePicker != null) datePicker.SelectedDate = null;
        }

        /// <summary>
        /// Efface un ComboBox
        /// </summary>
        private void ClearComboBox(string name)
        {
            var comboBox = FindName(name) as ComboBox;
            if (comboBox != null) comboBox.SelectedIndex = -1;
        }

       

        #region Multi-User (placeholder — kept for SetTodoSessionTracker)
        #endregion

        #region Status Filtering

        private string _activeStatusFilter = null;

        private void KpiFilter_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var border = sender as System.Windows.Controls.Border;
                if (border == null) return;

                var filterType = border.Tag as string;
                if (string.IsNullOrEmpty(filterType)) return;

                // Toggle filter: if same filter is active, clear it; otherwise set new filter
                if (_activeStatusFilter == filterType)
                {
                    _activeStatusFilter = null;
                    ShowToast("Filter cleared");
                }
                else
                {
                    _activeStatusFilter = filterType;
                    ShowToast($"Filtering by: {filterType}");
                }

                // Update visual indication
                UpdateKpiFilterVisuals();
                ApplyStatusFilter();
            }
            catch (Exception ex)
            {
                ShowToast($"Error applying KPI filter: {ex.Message}");
            }
        }
        private void UpdateKpiFilterVisuals()
        {
            try
            {
                // Reset all KPI borders to default
                ResetKpiBorder("KpiToReviewBorder", "#FFEDD5", 1);
                ResetKpiBorder("KpiToRemindBorder", "#FFCC80", 1);
                ResetKpiBorder("KpiReviewedBorder", "#D1FAE5", 1);
                ResetKpiBorder("KpiNotLinkedBorder", "#EF9A9A", 1);
                ResetKpiBorder("KpiNotGroupedBorder", "#FFCC80", 1);
                ResetKpiBorder("KpiHasDifferencesBorder", "#FFF59D", 1);
                ResetKpiBorder("KpiMatchedBorder", "#A5D6A7", 1);

                // Highlight active filter
                if (!string.IsNullOrEmpty(_activeStatusFilter))
                {
                    var borderName = _activeStatusFilter switch
                    {
                        "ToReview" => "KpiToReviewBorder",
                        "ToRemind" => "KpiToRemindBorder",
                        "Reviewed" => "KpiReviewedBorder",
                        "NotLinked" => "KpiNotLinkedBorder",
                        "NotGrouped" => "KpiNotGroupedBorder",
                        "HasDifferences" => "KpiHasDifferencesBorder",
                        "Matched" => "KpiMatchedBorder",
                        _ => null
                    };

                    if (borderName != null)
                    {
                        var border = this.FindName(borderName) as System.Windows.Controls.Border;
                        if (border != null)
                        {
                            border.BorderThickness = new Thickness(3);
                            border.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // Blue highlight
                        }
                    }
                }
            }
            catch { }
        }

        private void ResetKpiBorder(string borderName, string defaultColor, double thickness)
        {
            try
            {
                var border = this.FindName(borderName) as System.Windows.Controls.Border;
                if (border != null)
                {
                    border.BorderThickness = new Thickness(thickness);
                    border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(defaultColor));
                }
            }
            catch { }
        }

        private void ApplyStatusFilter()
        {
            try
            {
                // Simply trigger the existing filter system
                // The status filter will be applied in ApplyFilters via VM.ApplyFilters
                ApplyFilters();
            }
            catch (Exception ex)
            {
                ShowToast($"Error applying status filter: {ex.Message}");
            }
        }

        /// <summary>
        /// Filter predicate for status-based filtering (called by VM.ApplyFilters if needed)
        /// </summary>
        private bool MatchesStatusFilter(ReconciliationViewData row)
        {
            if (string.IsNullOrEmpty(_activeStatusFilter)) return true;

            var color = row.StatusColor;
            return _activeStatusFilter switch
            {
                "ToReview" => !row.IsReviewed,
                "ToRemind" => row.HasActiveReminder, // Active reminders (ToRemind = true and ToRemindDate <= today)
                "Reviewed" => row.IsReviewed,
                "NotLinked" => color == "#F44336", // Red - No DWINGS link
                "NotGrouped" => !row.IsMatchedAcrossAccounts, // NOT grouped (no "G" in grid)
                "HasDifferences" => color == "#FFC107" || color == "#FF6F00", // Yellow or Dark Amber
                "Discrepancy" => color == "#FFC107" || color == "#FF6F00", // Yellow or Dark Amber (legacy)
                "Matched" => color == "#4CAF50", // Green - Balanced and grouped
                "Balanced" => color == "#4CAF50", // Green (legacy)
                _ => true
            };
        }

        #endregion

        #endregion
    }
}