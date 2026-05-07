# RecoTool — État des lieux performance & propositions d'amélioration

> Cible : améliorer la latence perçue à l'usage (ouverture d'une `ReconciliationView`, retour sur `HomePage`, scroll dans la grille).
> Contrainte : conserver `IsDeferredScrolling=false` (le contenu doit suivre le scroll, pas seulement s'afficher au relâchement).

---

## 1. Synthèse exécutive

Trois goulots dominent l'expérience :

1. **Ouverture d'une `ReconciliationView`** — la chaîne de chargement effectue **3 passes complètes d'enrichissement** sur l'intégralité des lignes, plus quelques passes "secondaires" lancées en cascade synchrone depuis le thread UI. Sur un jeu de données moyen (~10–30 k lignes) cela coûte plusieurs secondes même quand le cache contient déjà tout.
2. **Retour sur `HomePage`** — `MainWindow.NavigateToHomePage` retest `_homePage.IsLoaded == false` à chaque navigation. Ce flag passe à `false` dès que `MainContent.Content` change, donc **la Home se recharge intégralement à chaque retour**, ce qui inclut deux requêtes OLE DB (live + historique), 9 graphiques LiveCharts reconstruits et un `Mouse.OverrideCursor = Wait` pendant tout ce temps.
3. **Scroll de la grille** — UI virtualization fonctionne, mais quelques propriétés Syncfusion + colonnes templatées coûteuses surchargent le coût par recyclage de ligne. La latence vient surtout du **recyclage des cellules frozen** (Status `GridTemplateColumn`) et de `LiveDataUpdateMode="AllowDataShaping"` combiné à `EnableDataVirtualization="True"` sur une source en mémoire.

Les corrections sont majoritairement des **suppressions de travail redondant** (pas de réécriture de logique métier) ; toutes restent compatibles avec `IsDeferredScrolling=false`.

---

## 2. Inventaire détaillé du chemin "Ouvrir une vue"

### 2.1 Chaîne d'appel actuelle

```
ReconciliationPage.AddViewForCurrentSelectionAsync(...)
└─> AwaitSafeToOpenViewAsync()                          // attend la synchro
└─> AddReconciliationView(asPopup)
    └─> new ReconciliationView(...)                     // .ctor instancie le XAML, hooke timers, lance presence
    └─> ConfigureAndPreloadView(view)
        ├─ view.IsLoading = true
        ├─ view.SyncCountryFromService(false)
        │   ├─ StartPresenceEngine()
        │   ├─ LoadCurrencyOptionsAsync()        (await async, 1 SELECT DISTINCT)
        │   ├─ LoadGuaranteeTypeOptionsAsync()   (await async)
        │   └─ LoadGuaranteeStatusOptionsAsync() (await async)
        ├─ view.UpdateExternalFilters(...)
        │   └─ ApplyFilters()                    // ⚠ s'exécute sur _allViewData encore vide
        ├─ apply saved-filter SQL + load layout JSON (background Task.Run)
        └─ Task.Run:
            ├─ localSvc.EnsureDwingsCachesInitializedAsync()  (1)
            ├─ localRepo.GetReconciliationViewAsync(...)      // ⚠ bypass _recoViewDataCache
            └─ Dispatcher.InvokeAsync:
                ├─ view.InitializeWithPreloadedData(list, sql)
                └─ view.Refresh()
                    └─ RefreshAsync()
                        └─ LoadReconciliationDataAsync()
                            ├─ EnsureDwingsCachesInitializedAsync()                (2)
                            ├─ ReapplyEnrichmentsToListAsync()                     ▶ Pass 1 enrichissement
                            │   ├─ EnrichWithDwingsInvoices
                            │   ├─ EnrichRowsWithDwingsProperties
                            │   ├─ RetryUnlinkedReceivableBgi
                            │   ├─ CalculateMissingAmounts
                            │   └─ ComputeAndApplyGroupBalances
                            ├─ ViewDataEnricher.EnrichAll(_allViewData, ...)        ▶ Pass 2 — N PreCalculateDisplayProperties
                            ├─ _ = ApplyRecentActivityAsync()                       (cross-DB diff, fire-and-forget)
                            ├─ Rules catch-up : ApplyRulesNowAsync(catchUpIds)      (boucle sur _allViewData)
                            ├─ foreach row : ResolveCommentsForDisplay              (parse mention, regex)
                            ├─ ApplyFilters()                                        ▶ Pass 3
                            │   ├─ VM.ApplyFilters(_allViewData, excludeTxType:true) // 1er full-scan
                            │   ├─ VM.UpdateTransactionTypeOptionsForData(...)
                            │   ├─ VM.ApplyFilters(_allViewData)                    // 2e full-scan
                            │   ├─ reset MissingAmount/IsMatched sur _allViewData
                            │   ├─ ComputeMatchedAcrossAccounts
                            │   ├─ CalculateMissingAmounts
                            │   ├─ ComputeAndApplyGroupBalances
                            │   ├─ AssignInvoiceGroupColors
                            │   └─ foreach row : PreCalculateDisplayProperties       // re-pré-calcul intégral
                            └─ UpdateKpis(_filteredData)
```

### 2.2 Hot-spots identifiés

| # | Hot-spot | Fichier / méthode | Coût ordre de grandeur |
|---|----------|-------------------|------------------------|
| H1 | **Triple enrichissement** des mêmes lignes (Reapply + EnrichAll + ApplyFilters → PreCalculate) | `Services/ReconciliationService.cs:432`, `Services/ViewDataEnricher.cs:24`, `Windows/ReconciliationView/Filtering.cs:77` | O(N) × 3 + N allocations brushes ; sur 20 k lignes ≈ 600 ms perdues |
| H2 | **Double `VM.ApplyFilters`** (le 1er passage sert à recalculer la liste des `TransactionType`) | `Windows/ReconciliationView/Filtering.cs:22-26` | O(N) × 2 sur tous les filtres |
| H3 | **`EnsureDwingsCachesInitializedAsync` appelé 3 fois** dans la même chaîne (ConfigureAndPreload, GetReconciliationViewAsync, LoadReconciliationDataAsync) | idem | déjà guardé par flag → coût = 3× verrou + await ; mineur mais pollue les traces |
| H4 | **`localRepo` court-circuite le cache service** : `_recoViewDataCache` n'est rempli que par `BuildReconciliationViewAsyncCore` du service | `Windows/ReconciliationPage.xaml.cs:2159-2161` | force un OLE DB query à chaque ouverture, même 2 vues identiques d'affilée |
| H5 | **Rules catch-up** s'exécute pour TOUTES les vues, à TOUTES les ouvertures, même quand `catchUpIds.Count == 0` (le LINQ traverse encore _allViewData) | `Windows/ReconciliationView/DataLoading.cs:209-255` | O(N) toujours payé ; quand des matches existent, déclenche une 4e passe de re-fetch row par row (`GetOrCreateReconciliationAsync`) sur le thread UI |
| H6 | **`ResolveCommentsForDisplay` dans une boucle for-each** sur _allViewData (regex sur chaque commentaire) | `Windows/ReconciliationView/DataLoading.cs:258-266` | O(N) regex + dict lookup ; 50–150 ms sur 20 k |
| H7 | **3 `LoadXxxOptionsAsync` séquentiels** (Currency, GuaranteeType, GuaranteeStatus) en `await` série | `Windows/ReconciliationView/DataLoading.cs:55-58` + `SyncCountryFromService` | 3 round-trips OLE DB sériels |
| H8 | **Dispatcher cascade** : `Refresh()` est appelé sur le thread UI dans `Dispatcher.InvokeAsync` après le pré-load → toute la suite (Pass 1 + 2 + 3, rules, comments) se passe sur le thread UI, gelant le rendu de la fenêtre | `Windows/ReconciliationPage.xaml.cs:2166` | cause directe du "freeze" pendant l'ouverture |
| H9 | **`view.IsLoading = true` est posé dans `ConfigureAndPreloadView`** mais l'overlay de chargement repose sur des bindings StatusInfo : si l'utilisateur clique pendant ce temps, ça empile des actions sur la UI gelée | `Windows/ReconciliationPage.xaml.cs:2065` | UX : pas un coût CPU mais accentue la perception de lenteur |

---

## 3. Inventaire détaillé du chemin "Retour sur HomePage"

### 3.1 Comportement actuel

```
HomeButton_Click
├─ Mouse.OverrideCursor = Wait
├─ NavigateToHomePage()
│   ├─ if (_homePage == null) new HomePage(...)
│   ├─ else _homePage.UpdateServices(...)
│   │       if (countryChanged) _homePage.Refresh()
│   ├─ NavigateToPage(_homePage)               // MainContent.Content = _homePage
│   └─ if (_homePage.IsLoaded == false) _homePage.Refresh()   // ⚠ TOUJOURS vrai à la 1re reaffectation
└─ await WaitForCurrentPageRefreshAsync(10s)
```

`UserControl.IsLoaded` redevient `false` dès qu'on retire le contrôle de l'arbre visuel ; ré-affecter `MainContent.Content` ne le repasse à `true` qu'**après** que le hook `Loaded` ait fini d'être traité — donc à l'instant du test (ligne 1267), il vaut `false` à chaque retour. Conséquence : **`Refresh()` est déclenché à chaque navigation vers Home**, même s'il n'y a aucun changement.

### 3.2 Coût d'un `Refresh()` Home

```
LoadDashboardDataAsync
├─ EnsureCountryReadyAsync (peut attendre 200 ms)
├─ LoadLiveDashboardAsync
│   ├─ LoadRealDataFromDatabase
│   │   ├─ TryGetCachedReconciliationView(live)  // ✓ peut être instantané
│   │   ├─ ou GetReconciliationViewAsync(live)   // ⚠ sinon OLE DB lourd
│   │   ├─ TryGetCachedReconciliationView(historical)
│   │   └─ ou GetReconciliationViewAsync(historical, includeDeleted:true)  // ⚠ 2e jeu, plus gros
│   ├─ AnalyzeAccountDistribution()
│   ├─ UpdateKPISummary()           // single-pass, OK
│   ├─ UpdateCharts()                // 9 graphiques LiveCharts
│   │   ├─ UpdateKPIChart            (alloue SeriesCollection + ChartValues)
│   │   ├─ UpdateCurrencyChart
│   │   ├─ UpdateActionChart
│   │   ├─ UpdateKpiRiskChart
│   │   ├─ UpdateReceivablePivotMiniCharts
│   │   ├─ UpdateReceivablePivotByActionChart
│   │   ├─ UpdateReceivablePivotByCurrencyChart
│   │   ├─ UpdateDeletionDelayChart           // sur historical (gros)
│   │   └─ UpdateNewDeletedDailyChart         // sur historical
│   ├─ UpdateCountryInfo()
│   ├─ LoadTodoCardsAsync()         // queries TodoListItem + per-card live data
│   ├─ UpdateAnalytics()            // alerts/leaderboard/completion + 2 chart series
│   └─ RefreshTodoCardSessionsAsync()  // lecture fichier session multi-user
```

### 3.3 Hot-spots identifiés

| # | Hot-spot | Fichier / méthode | Coût |
|---|----------|-------------------|------|
| H10 | **Refresh systématique sur retour** dû au test `IsLoaded == false` | `Windows/MainWindow.xaml.cs:1267-1271` | 1×LoadLiveDashboard à chaque retour |
| H11 | **9 chart updates en chaîne** sur le thread UI, allouent à chaque fois des `SeriesCollection`/`ChartValues` neufs | `Windows/HomePage.xaml.cs:1796-1809` | LiveCharts est lent à instancier ; 200–500 ms cumulés |
| H12 | **`UpdateDeletionDelayChart` + `UpdateNewDeletedDailyChart` itèrent sur `_reconciliationHistoricalData`** (jeu plus volumineux que live) | `Windows/HomePage.xaml.cs:333+` | linéaire sur tout l'historique inclut deleted |
| H13 | **`WaitForCurrentPageRefreshAsync(10s)`** — bloque jusqu'à 10 s même si rien n'est en cours | `Windows/MainWindow.xaml.cs:1333` | UX : cursor wait visible si l'event Refresh se croise mal |
| H14 | **`UpdateAnalytics` recrée `ObservableCollection<AlertItem>` + `ObservableCollection<AssigneeStats>` + 2 `SeriesCollection`** à chaque refresh | `Windows/HomePage.xaml.cs:1814-1872` | Bindings se ré-évaluent intégralement, animations LiveCharts se replay |

---

## 4. Inventaire détaillé du scroll grille

### 4.1 Configuration actuelle (`ReconciliationView.xaml`, ligne 1107-1151)

```xml
<syncfusion:SfDataGrid x:Name="ResultsDataGrid"
    EnableDataVirtualization="True"            ⚠
    LiveDataUpdateMode="AllowDataShaping"      ⚠
    AllowResizingColumns="True"
    AllowDraggingColumns="True"
    EditTrigger="OnDoubleTap"
    NavigationMode="Cell"
    SelectionMode="Extended"
    FrozenColumnCount="6"
    GridLinesVisibility="Horizontal"
    UseLayoutRounding="True"
    SnapsToDevicePixels="True"
    TextOptions.TextFormattingMode="Display"
    TextOptions.TextRenderingMode="ClearType"
    TextOptions.TextHintingMode="Fixed"
    RenderOptions.BitmapScalingMode="LowQuality"
    RenderOptions.ClearTypeHint="Enabled"
    ScrollViewer.CanContentScroll="True"
    ColumnSizer="None"
    AllowResizingHiddenColumns="False"
    AllowTriStateSorting="False"
    RowHeight="30"
    HeaderRowHeight="36">
```

### 4.2 Hot-spots scroll

| # | Hot-spot | Détail |
|---|----------|--------|
| H15 | **`EnableDataVirtualization="True"`** sur une `ObservableCollection<T>` en mémoire | Cette propriété est destinée aux sources externes paginées (`VirtualizingCollectionView` Syncfusion). Avec une collection en mémoire, elle ajoute un wrapper inutile et perturbe `LiveDataUpdateMode`. **L'UI virtualization** (recyclage des lignes visibles uniquement) n'a PAS besoin de cette propriété — elle est active par défaut. À retirer. |
| H16 | **`LiveDataUpdateMode="AllowDataShaping"`** | Force la grille à réécouter chaque `INotifyPropertyChanged` pour re-trier/re-grouper en live. Comme la majorité de vos rows tirent de très nombreux `OnPropertyChanged` lors d'un Refresh ou d'un edit, la grille fait des dizaines de "re-shape" inutiles. Préférer `Default` (valeur par défaut) ou `AllowSummaryUpdate` si seul le footer compte. |
| H17 | **Colonne Status (frozen, GridTemplateColumn)** | StackPanel avec 4 enfants conditionnels : presence avatar, badge N/U, ellipse statut, pill "G". Frozen = re-rendue à chaque scroll horizontal, et chaque cellule recyclée recrée 4 `FrameworkElement` + 3 `Border`. Possible d'aplatir en `GridTextColumn` + `CellStyle` : displayer en single-glyph string + brushes pré-calculés (vous avez déjà tout dans le DTO — `StatusIndicator`, `StatusBadgeBgBrush`, `IsMatchedAcrossAccountsVisibility`). Reste l'avatar de présence à gérer mais c'est une `Visibility.Collapsed` 99% du temps. |
| H18 | **Colonne Comments (GridTemplateColumn)** : DockPanel + Grid + TextBlock + Border + 2 textBlocks | Chaque cellule recyclée instancie ~6 éléments. Idem : pré-calculer un seul `LastComment` (déjà fait) et un compteur display, puis `GridTextColumn` simple. |
| H19 | **CellStyle avec Setter Binding sur GridCell** (Action, ActionStatus, MissingAmount, Counterpart, InternalInvoiceRef, LocalSignedAmount, SignedAmount) | Chaque `<Setter Property="X" Value="{Binding ...}"/>` à l'intérieur d'un `Style` ciblant `syncfusion:GridCell` instancie une `Binding` par cellule visible — soit ~30 lignes × ~7 cellules = ~210 bindings ré-attachés à chaque scroll de page. Préférer un `CellTemplate` minimal **sans CellStyle** quand un binding est présent, ou utiliser `DisplayBinding` + `ConverterParameter` pré-cuit. Mieux : cibler `GridTextColumn.CellStyle` via `StaticResource` partagé et ne pas binder de brush (poser la couleur directement par chaîne). |
| H20 | **`AllowDraggingColumns="True"`** | Ajoute un hit-test header sur chaque mouseMove. Si le drag de colonne n'est pas critique pour l'utilisateur final, le passer à `False` réduit le coût input/scroll. |
| H21 | **`FrozenColumnCount=6`** + colonne 1 = `GridTemplateColumn` lourde | Les colonnes frozen se redessinent à chaque scroll horizontal et chaque scroll vertical (re-layout). Conserver 6 frozen est OK mais avec une template lourde sur la 1re, c'est le pire des deux mondes. Solution : aplatir Status (H17) ou réduire à 3-4 frozen. |
| H22 | **`Foreground` lié à `BNPMainGreenBrush` via `DynamicResource`** dans le titre + KPI cards | DynamicResource refait un lookup à chaque mesure. Sur un en-tête statique, basculer en `StaticResource` (le thème ne change pas en cours d'exécution). Mineur globalement mais cumulé sur 30+ TextBlocks de l'en-tête, mesurable. |

### 4.3 Ce qui est déjà bien

- Pré-calcul des brushes / visibilities sur le DTO ✓
- `BeginInit/EndInit` autour des `Clear/AddRange` ✓
- `TextFormattingMode=Display` + `UseLayoutRounding=True` ✓
- Cache `SolidColorBrush` partagé via `GetCachedBrush` ✓
- Pagination incrémentale 500 lignes + scroll-bottom hook ✓
- `GridTextColumn` au lieu de `GridTemplateColumn` pour la majorité des colonnes éditables (Action, KPI, ActionStatus, etc.) ✓

---

## 5. Propositions d'amélioration — par ordre d'impact

> Toutes les propositions ci-dessous sont **compatibles avec `IsDeferredScrolling=false`** : elles diminuent le coût par image rendue, pas la fréquence de mise à jour.

### 5.1 Quick wins (≤ 1/2 journée chacun, gros impact)

#### QW-1 : Supprimer le `Refresh()` automatique sur retour Home
**Fichier :** `Windows/MainWindow.xaml.cs:1265-1272`
**Problème :** `_homePage.IsLoaded == false` est toujours vrai juste après le swap de Content.
**Correctif :** Tracer un flag `_homeDataLoadedOnce` côté MainWindow ; ne refresh que si :
- (a) c'est le premier `NavigateToHomePage` après démarrage, OU
- (b) le pays a changé, OU
- (c) un signal externe (`DataChanged` propagé depuis ReconciliationView/sync) a invalidé les données.

```csharp
// Dans MainWindow
private bool _homeFirstShown;

private void NavigateToHomePage()
{
    if (_homePage == null)
    {
        _homePage = new HomePage(_offlineFirstService, _reconciliationService);
    }
    else
    {
        var prevCid = _homePage.CurrentCountryId;
        _homePage.UpdateServices(_offlineFirstService, _reconciliationService);
        var newCid = _offlineFirstService?.CurrentCountryId;
        bool countryChanged = !string.IsNullOrEmpty(newCid) && !string.Equals(prevCid, newCid, StringComparison.OrdinalIgnoreCase);
        if (countryChanged || _homeIsStale)
        {
            _homePage.Refresh();
            _homeIsStale = false;
        }
    }

    NavigateToPage(_homePage);
    UpdateNavigationButtons("Home");

    if (!_homeFirstShown && !string.IsNullOrEmpty(_offlineFirstService?.CurrentCountryId))
    {
        _homePage.Refresh();
        _homeFirstShown = true;
    }
}
```

Et invalider depuis les vues : `mainWindow._homeIsStale = true;` quand `ReconciliationView.DataChanged` se déclenche.

**Gain attendu :** retour Home **quasi-instantané** (les graphes restent affichés, pas de Wait cursor, pas de re-query OLE DB).

#### QW-2 : Mettre fin à la triple enrichissement à l'ouverture
**Fichiers :**
- `Windows/ReconciliationView/DataLoading.cs:158-187`
- `Services/ViewDataEnricher.cs:24`
- `Windows/ReconciliationView/Filtering.cs:77-79`

**Problème :** `PreCalculateDisplayProperties()` et les calculs MissingAmount/IsMatched s'exécutent 3 fois sur les mêmes lignes pour une seule ouverture.

**Correctif :**
1. Dans `LoadReconciliationDataAsync`, **supprimer** l'appel `await ReapplyEnrichmentsToListAsync(...)` quand on vient d'un fetch frais : `BuildReconciliationViewAsyncCore` a déjà tout fait. Garder Reapply uniquement pour le path "preloaded data" car la liste peut avoir vieilli.
2. Dans `ViewDataEnricher.EnrichRow`, **ne pas appeler** `PreCalculateDisplayProperties()` si on sait que `ApplyFilters` va tourner derrière (cas premier load) — ou inversement, faire en sorte qu'`ApplyFilters` ne re-pré-calcule que les rows dont l'`IsMatchedAcrossAccounts`/`MissingAmount` a réellement changé.
3. Centraliser la séquence en une **passe unique** :
   ```
   LoadReconciliationData → EnrichWithDwingsInvoices + EnrichRowsWithDwingsProperties
                          → CalculateMissingAmounts + ComputeAndApplyGroupBalances
                          → ComputeMatchedAcrossAccounts
                          → AssignInvoiceGroupColors
                          → ViewDataEnricher.RefreshDisplayLabels (juste les Display strings)
                          → foreach row: PreCalculateDisplayProperties()  // UNE SEULE FOIS
   ```

**Gain attendu :** ~60% de temps en moins sur le post-fetch. Sur 20 k lignes : ~400-600 ms de moins.

#### QW-3 : Ne plus court-circuiter le cache via le Repository
**Fichier :** `Windows/ReconciliationPage.xaml.cs:2159-2161`

**Problème :** la branche `localRepo.GetReconciliationViewAsync` ne touche pas le `_recoViewDataCache`. Ouvrir 2 vues identiques → 2 fetchs OLE DB.

**Correctif :** privilégier `localSvc.GetReconciliationViewAsync(...)` qui passe par le cache `Lazy<Task>` + `_recoViewDataCache`. Le repository peut rester l'implémentation interne du service.

```csharp
list = await localSvc.GetReconciliationViewAsync(countryId, backendSql, includeDeleted: wantArchived).ConfigureAwait(false);
```

**Gain attendu :** ouverture d'une vue identique = **0 OLE DB**, instantanée (~50 ms vs ~1.5 s sur gros pays).

#### QW-4 : Une seule passe `ApplyFilters` au lieu de deux
**Fichier :** `Windows/ReconciliationView/Filtering.cs:21-26`

**Problème :** premier passage avec `excludeTransactionType:true` juste pour calculer la liste des `TransactionType` à proposer dans le combo. Sur 20 k lignes c'est un O(N) gratuit.

**Correctif :** lors de la passe principale, calculer en parallèle (`var seenTxTypes = new HashSet<int>()`) les types rencontrés. Ne plus appeler `VM.ApplyFilters` deux fois.

Alternative plus simple : ne mettre à jour la liste des `TransactionType` qu'**au moment où le combo est ouvert** (`DropDownOpened`).

**Gain attendu :** ~50% de la durée de `ApplyFilters` sur les gros datasets.

#### QW-5 : Retirer `EnableDataVirtualization` et baisser `LiveDataUpdateMode`
**Fichier :** `Windows/ReconciliationView.xaml:1138, 1142`

**Correctif :**
```xml
<!-- AVANT -->
EnableDataVirtualization="True"
LiveDataUpdateMode="AllowDataShaping"

<!-- APRÈS -->
EnableDataVirtualization="False"
LiveDataUpdateMode="Default"
```

`EnableDataVirtualization` n'est utile qu'avec un `VirtualizingCollectionView` (data paginée côté serveur). Avec une `ObservableCollection<T>` chargée en mémoire, on perd en perf sans rien gagner. L'UI virtualization (recyclage de lignes visibles) reste active par défaut, ce qui est ce qu'on veut.

`LiveDataUpdateMode=Default` empêche la re-shape sur chaque `INotifyPropertyChanged`. Les KPI sont déjà recalculés explicitement après edit via `UpdateKpis`.

**Gain attendu :** scroll **nettement plus fluide**, surtout sur les datasets >5 k lignes ; coupe les latences observées au survol clavier-flèche.

#### QW-6 : Charger les options en parallèle
**Fichier :** `Windows/ReconciliationView/DataLoading.cs:53-58`

**Correctif :**
```csharp
await Task.WhenAll(
    LoadAssigneeOptionsAsync(),
    LoadGuaranteeStatusOptionsAsync(),
    LoadGuaranteeTypeOptionsAsync(),
    LoadCurrencyOptionsAsync()
).ConfigureAwait(true);
```

**Gain attendu :** divisé par ~4 le temps des 4 SELECT DISTINCT (de ~120 ms à ~30 ms cumulés).

### 5.2 Améliorations moyennes (1–2 j chacune)

#### M-1 : Aplatir la colonne Status frozen en `GridTextColumn`
**Fichier :** `Windows/ReconciliationView.xaml:1167-1215`

Garder un seul `GridTextColumn` qui affiche un glyphe composé (par ex. `"●N G"`) avec `CellStyle` reliant brushes pré-calculés. Le badge presence reste, mais devient un simple `<Run>` dans un `TextBlock` (au lieu d'un Border + Ellipse + StackPanel).

Pour conserver visuellement le rendu, on peut utiliser une seule `GridTemplateColumn` mais simplifiée :
- 1 `Grid` plat (pas de StackPanel imbriqué)
- 3 `TextBlock` (au lieu de 4 `Border` + `Ellipse` + `Border`)
- toutes les `Visibility` viennent du DTO (déjà fait)

**Gain attendu :** ~30% de temps de recyclage de ligne en moins → meilleur framerate au scroll vertical, surtout en présence de scroll horizontal sur les colonnes frozen.

#### M-2 : `Refresh()` post-load déplacé hors du thread UI
**Fichier :** `Windows/ReconciliationPage.xaml.cs:2140-2175`

**Correctif :** dans `ConfigureAndPreloadView`, faire faire le travail d'enrichissement (Reapply + ApplyFilters logique) dans `Task.Run` puis seulement la mise à jour de `_viewData` (Clear/Add) sur le Dispatcher.

```csharp
await Task.Run(async () =>
{
    var enriched = await PreEnrichOnBackgroundAsync(list, countryId);
    await view.Dispatcher.InvokeAsync(() =>
    {
        view.InitializeWithPreloadedData(enriched, backendSql);
        view.RefreshFromPreloaded();          // version qui ne re-fait pas ce qui est déjà fait
    });
});
```

**Gain attendu :** la fenêtre reste réactive pendant l'ouverture (resize, focus, autres clics). Le coût n'est pas plus court mais perçu comme tel.

#### M-3 : Charger les charts Home en différé / progressivement
**Fichier :** `Windows/HomePage.xaml.cs:1796-1809`

**Correctif :**
- Afficher la `HomePage` immédiatement (KPIs + cartes Todo).
- Programmer les 9 charts en `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` un par un (ou par batches de 3).
- Recycler les `SeriesCollection` existants au lieu de toujours `new SeriesCollection()` : si vous mutez `Values.Clear()` + `AddRange` sur un `ChartValues<T>`, LiveCharts évite la ré-instanciation des `Series`.

**Gain attendu :** "Home" affichée en <300 ms perçus ; les charts arrivent par vagues de 30-50 ms chacun.

#### M-4 : Cache LiveCharts/Analytics insensible au pays
- `UpdateAnalytics` recalcule `AlertItems`, `AssigneeLeaderboard`, `CompletionEstimate`, `ReviewTrendSeries`, `MatchedRateTrendSeries` **à chaque retour Home**.
- Si les données live n'ont pas changé (cf. flag `_homeIsStale` du QW-1), garder les `ObservableCollection`/`SeriesCollection` existantes et ne rien rebinder.

#### M-5 : Réduire les colonnes templated coûteuses (Comments, Mbaw)
- Comments : remplacer la cellule par un `GridTextColumn` simple sur `LastComment` + `CellStyle` qui pose un `ToolTip="{Binding Comments}"`. Le badge compteur ne s'affiche qu'au tooltip / au hover. Si on veut garder le badge visible, le faire en suffixe textuel `"💬2 Mon commentaire"` (zéro élément en plus).
- Mbaw : pareil, `GridTextColumn` + style + `Foreground` pré-calculé. La loupe `🔍` peut être ajoutée en préfixe au `MbawData`.

**Gain attendu :** ~15-25% de scroll plus fluide. Surtout perceptible sur slow CPU (postes utilisateurs corporate).

#### M-6 : Désactiver `AllowDraggingColumns` par défaut
**Fichier :** `Windows/ReconciliationView.xaml:1113`

Si peu d'utilisateurs déplacent les colonnes, le passer à `False`. Optionnel : toggle Manage-Columns existante reste dispo.

#### M-7 : `Rules catch-up` → en arrière-plan, pas synchrone
**Fichier :** `Windows/ReconciliationView/DataLoading.cs:209-255`

Aujourd'hui le bloc rules catch-up est `await`-é. Il devrait être `_ = Task.Run(...)` après que la grille soit affichée, et lors de la complétion lever `DataChanged` pour rebinder les rows touchées (pas tout le grid).

**Gain attendu :** rend la grille affichable ~150-300 ms plus tôt sur les gros pays.

#### M-8 : `ResolveCommentsForDisplay` mis en cache global
**Fichier :** `Windows/ReconciliationView/DataLoading.cs:258-266` + `Mentions.cs`

Le mapping `[uid:xxx] → DisplayName` est statique sur la durée d'une session. Mémoriser les regex compilées et le dict UID→Name (déjà sans doute géré dans `Mentions.cs`) ; ne ré-exécuter sur la ligne que si `Comments` a changé (`HasResolvedComments` flag sur le DTO).

#### M-9 : Pré-créer la `ReconciliationView` avant l'ouverture
- Créer une instance "warmed up" en background dès que la page Reconciliation est affichée, prête à recevoir les données. Quand l'utilisateur clique "Add View", la création XAML (`InitializeComponent` sur ~1500 lignes de XAML compilé) est déjà payée.
- Ou, plus simple : laisser une instance "vide" cachée dans le `ViewsPanel` derrière une Visibility, et la "réveiller" au premier clic.

**Gain attendu :** ~150-300 ms de moins sur l'ouverture (le coût d'`InitializeComponent` n'est pas négligeable avec autant de templates).

### 5.3 Améliorations structurelles (gros chantiers)

#### S-1 : Index sur la BDD Access pour la requête `ReconciliationView`
**Inspecter le plan d'exécution** de `ReconciliationViewQueryBuilder.Build` sur Access. Vérifier que les colonnes utilisées dans les `JOIN`/`WHERE` (`Account_ID`, `Operation_Date`, `DeleteDate`, `DWINGS_InvoiceID`, etc.) ont bien des index. Une seule fois, mais peut diviser par 2-3 le temps de la grosse requête.

#### S-2 : Cache disque (LiteDB / fichier sérialisé) entre sessions
- À la fermeture, sérialiser `_reconciliationViewData` et `_reconciliationHistoricalData` (BSON / MessagePack).
- Au prochain démarrage, charger en <100 ms avant de lancer la sync delta. Le UI se peuple immédiatement avec les données "d'hier", puis se rafraîchit silencieusement quand la sync revient.
- Surtout intéressant pour la HomePage : les charts s'affichent sans aller en BDD.

#### S-3 : Pré-aggrégation côté SQL pour `MissingAmount` / `IsMatchedAcrossAccounts`
Les passes `CalculateMissingAmounts` + `ComputeMatchedAcrossAccounts` sont des `GROUP BY` clientés. Ajouter une sous-requête au `ReconciliationViewQueryBuilder` qui retourne déjà :
- `CounterpartTotalAmount`, `CounterpartCount`, `MissingAmount`
- `IsMatchedAcrossAccounts` (bool)
en s'appuyant sur un `SELECT InternalInvoiceReference, SUM(SignedAmount), COUNT(*) GROUP BY InternalInvoiceReference, AccountSide`.

Économie : O(N) côté client devient O(1) par row au moment du chargement.

#### S-4 : Migrer le DTO `ReconciliationViewData` vers `class avec champs immuables` après load
Aujourd'hui le DTO supporte `INotifyPropertyChanged` partout, ce qui multiplie le coût des passes d'enrichissement (chaque setter émet un event, recalculant des caches dépendants). Pour les colonnes affichées en lecture seule (90% des cellules), rendre les setters silencieux pendant le batch d'init :

```csharp
public void BeginBatchUpdate() => _suspendNotifications = true;
public void EndBatchUpdate() {
    _suspendNotifications = false;
    PreCalculateDisplayProperties();   // une seule passe
    OnPropertyChanged(null);           // notify all once
}
```

Et dans les enrichers : envelopper dans `try { row.BeginBatchUpdate(); ... } finally { row.EndBatchUpdate(); }`.

**Gain attendu :** suppression d'~80% des `OnPropertyChanged` parasites pendant l'enrichissement.

---

## 6. Plan d'attaque proposé (ordre recommandé)

| Étape | Items | Effort | Impact perçu |
|-------|-------|--------|--------------|
| 1 | QW-1 (Home navigation) + QW-3 (cache via service) + QW-5 (LiveDataUpdateMode + EnableDataVirtualization) | ½ j | **Très élevé** (retour Home instantané, scroll fluide) |
| 2 | QW-2 (triple enrichissement) + QW-4 (double ApplyFilters) + QW-6 (parallèle options) | 1 j | **Élevé** (ouverture vue ~−40%) |
| 3 | M-2 (offload UI thread) + M-7 (rules catch-up async) | 1 j | Vue affichée plus tôt, ressentie comme rapide |
| 4 | M-1 + M-5 (templated columns aplaties) | 1–2 j | Scroll mesurablement plus fluide |
| 5 | M-3 + M-4 (Home incremental + chart cache) | 1 j | Home réactive même sur gros pays |
| 6 | S-1 (index BDD) | ½ j | Réduit le pire cas |
| 7 | S-3, S-4, S-2 | chantier | Gains importants à long terme |

---

## 7. Comment mesurer le progrès

Vous avez déjà une infra `LogPerf` correcte (`ReconciliationView` logue `dbMs`/`filterMs`/`totalMs`). Pour valider chaque étape :

1. Ajouter au démarrage un log "OpenView_Total" (du clic AddView jusqu'au premier render visible) — capturer dans `ConfigureAndPreloadView` un `Stopwatch` et stoppé dans `ResultsDataGrid_Loaded`.
2. Ajouter "HomeReturn_Total" dans `MainWindow.HomeButton_Click`.
3. Pour le scroll, activer `WPF Performance Suite` (Visual Profiler) ou en plus simple ajouter `CompositionTarget.Rendering` instrumenté qui calcule le frame time pendant un drag scrollbar et logue le P95.

Critère de succès cible :
- Ouverture vue (cache chaud) : **< 500 ms** (vs ~1.5–3 s actuellement sur 20 k lignes)
- Retour Home (mêmes données) : **< 100 ms** (vs ~1–2 s)
- Scroll : **60 fps stable** (≤ 16 ms par frame) en glissant la scrollbar sur 5 000 lignes.

---

## 8. Annexe — fichiers cités

- `Windows/MainWindow.xaml.cs` : `NavigateToHomePage` (1241), `HomeButton_Click` (1326), `WaitForCurrentPageRefreshAsync` (~282-307)
- `Windows/HomePage.xaml.cs` : `LoadDashboardDataAsync` (1059), `LoadLiveDashboardAsync` (1103), `LoadRealDataFromDatabase` (1621), `UpdateCharts` (1796), `UpdateAnalytics` (1814)
- `Windows/ReconciliationPage.xaml.cs` : `AddReconciliationView` (1909), `ConfigureAndPreloadView` (2062)
- `Windows/ReconciliationView.xaml` : grille principale (1107)
- `Windows/ReconciliationView/DataLoading.cs` : `LoadReconciliationDataAsync` (134), Rules catch-up (209-255)
- `Windows/ReconciliationView/Filtering.cs` : `ApplyFilters` (16)
- `Windows/ReconciliationView/Paging.cs` : `LoadMorePage` (55)
- `Services/ReconciliationService.cs` : `GetReconciliationViewAsync` (420), `ReapplyEnrichmentsAsync` (458), `BuildReconciliationViewAsyncCore` (553)
- `Services/ViewDataEnricher.cs` : `EnrichRow` (27), `EnrichAll` (17)
- `Services/Helpers/ReconciliationViewEnricher.cs` : `EnrichWithDwingsInvoices` (21), `RetryUnlinkedReceivableBgi` (162), `ComputeAndApplyGroupBalances` (219), `CalculateMissingAmounts` (259), `AssignInvoiceGroupColors` (396)
- `Services/DTOs/ReconciliationViewData.cs` : `PreCalculateDisplayProperties` (885)
- `UI/ViewModels/ReconciliationViewViewModel.cs` : `ApplyFilters` (298)
- `Infrastructure/DataAccess/OleDbQueryExecutor.cs` : reflection-based mapping
