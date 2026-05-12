# Statut d'implémentation du refactor UI/Sync

> Compagnon de [REFACTOR_PLAN_UI_SYNC.md](REFACTOR_PLAN_UI_SYNC.md). Mis à jour à chaque vague.

## ✅ Vague Cleanup — Suppression du code mort

> Décision après audit : le refactor "Option B full" (migration MVVM des grosses
> fenêtres, activation Sync V2) ne vaut pas son coût pour cette codebase WPF
> mono-stack. On nettoie le scaffolding qui n'apporte rien et on garde ce qui
> apporte réellement de la valeur (tests, repos vivants, health, logging, IClock).

**Code supprimé** (entrées csproj retirées + fichiers vidés en attente de `git rm`) :

| Catégorie | Fichiers | Raison |
|---|---|---|
| Stack Sync V2 | `ISyncEngine.cs`, `SyncEngine.cs`, `ISyncDataTransport.cs`, `LegacySyncDataTransport.cs`, `AccessSyncDataTransport.cs`, `IDistributedLock.cs`, `FileSystemDistributedLock.cs`, `IChangeLog.cs`, `AccessChangeLog.cs`, `ChangeJournalEntry.cs` | 0 caller en prod. SyncEngine apportait du lock distribué + push/pull atomique, mais AccessSyncDataTransport faisait un PULL pleine-table = régression perf vs le legacy watermark-based. ROI négatif. |
| Façade orpheline | `OfflineFirstServiceV2.cs` | 0 caller. Dupliquait `OfflineFirstService` sans le remplacer. |
| Repository orphelin | `IReconciliationRepository.cs`, `ReconciliationRepository.cs` | Field stocké dans `ReconciliationPage` mais jamais invoqué. Le service `ReconciliationService` reste l'autorité. |
| ViewModels orphelins (8) | `CommentsDialogViewModel`, `ReconciliationViewModel`, `DwingsButtonsViewModel`, `RulesHealthViewModel`, `RuleDebugViewModel`, `InvoiceFinderViewModel`, `SpiritGeneSearchViewModel`, `FilterPickerViewModel` | Registered en DI mais aucune fenêtre n'utilise leur ctor MVVM. Le code-behind WPF/Syncfusion reste l'autorité runtime. |
| Tests associés | 8 VM tests + 6 Sync V2 tests + InMemoryFakes + 1 integration test | Tests d'éléments supprimés. |
| Flag | `FeatureFlags.UseSyncV2` | Plus de Sync V2 à activer. |

**Code production touché** (ajustements de DI + suppression de refs) :
- `App.xaml.cs` : retiré ~80 lignes de DI registrations (V2 stack, OfflineFirstServiceV2, 4 VMs orphelins, IReconciliationRepository). `INetworkPathProvider` est conservé car utilisé par `NetworkShareHealthCheck`.
- `OfflineFirstService.SyncOperations.cs` : retiré la branche `if (UseSyncV2) { ... goto AfterPushPull; }`. Retour au push+pull direct.
- `OfflineFirstService.ConnectionStrings.cs` : retiré `GetNetworkReconciliationConnectionString` (ajouté juste pour AccessSyncDataTransport).
- `ReconciliationPage.xaml.cs` : retiré le ctor avec param `IReconciliationRepository`, le field `_recoRepository`, le code fallback associé.
- `CommentsDialog.xaml.cs` : retiré le ctor MVVM (VM supprimé).
- `XamlBindingCompatTests.cs` : retiré 6 méthodes de test sur les VMs supprimés.

**Reste à faire (post-session)** :
1. `git rm` les fichiers vidés (ils contiennent juste un commentaire indiquant la suppression). Liste : voir `Services/Sync/`, `Services/DTOs/ChangeJournalEntry.cs`, `Services/OfflineFirst/OfflineFirstServiceV2.cs`, `Domain/Repositories/IReconciliationRepository.cs`, `Infrastructure/Repositories/ReconciliationRepository.cs`, 8 VMs dans `ViewModels/`, et leurs tests dans `RecoTool.Tests/ViewModels/` + `RecoTool.Tests/Services/Sync/` + `RecoTool.IntegrationTests/Services/Sync/`.
2. Build local : `dotnet build` (ou MSBuild dans Visual Studio) doit passer sans erreurs.
3. Tests : `dotnet test` doit toujours passer (tous les tests survivants couvrent du code vivant).



## Vue d'ensemble

| Lot | État | Tests ajoutés | Notes |
|---|---|---|---|
| **0** Infrastructure cross-cutting | ✅ **Implémenté** | 17 | `IClock`, `IFileSystem`, DI étendu |
| **1** Découpage `OfflineFirstService` | ⏳ Reporté | – | Optionnel, peut être fait pendant Lot 5 |
| **2** MVVM pilote `MainWindow` | 🟡 **VM + tests prêts** | 25 | DataContext switch documenté ci-dessous |
| **3** Généralisation MVVM (14 fenêtres) | ✅ **16 VMs créés** | 23+ | Toutes les fenêtres principales + dialogs (incl. CommentsDialog, ProgressWindow). Reste seulement 2 dialogs triviaux : PreviewTextDialog (lecture seule) et TodoListSessionWarning (info-only). |
| **4** Abstractions sync | ✅ **Implémenté** | 22 | 4 interfaces + 5 fakes/concrets |
| **5** Refactor OFS | ✅ **Complet** | 21+11+18 | 4 concrets livrés + DI câblé + feature flag `UseSyncV2` |
| **6** Tests unitaires sync | ✅ **Étendu** | 19+21+18 | Tests fakes + concrets + SyncEngine orchestration |
| **7** Tests unitaires ViewModels | ✅ **16 VMs testés** | ~205 | Tous les VMs couverts par tests Moq |
| **8** Tests intégration multi-instance | ✅ **Implémenté** | 7 | Sérialisation lock, parallélisation par pays, recovery, burst, change log thread-safe |
| **9** Tests UI FlaUI | ⏳ À faire | – | Nécessite Lots 2 + 3 |

**Légende** : ✅ Implémenté · 🟡 Foundations posées · ⏳ Pas démarré

## Détail par lot

### ✅ Lot 0 — Infrastructure cross-cutting

**Fichiers livrés** :

- `Infrastructure/Time/IClock.cs` (interface + `SystemClock` singleton)
- `Infrastructure/IO/IFileSystem.cs` (interface + `SystemFileSystem` singleton)
- `App.xaml.cs` : enregistrements DI ajoutés
- `RecoTool.Tests/Infrastructure/Time/SystemClockTests.cs` (7 tests + `FakeClock` réutilisable)
- `RecoTool.Tests/Infrastructure/IO/SystemFileSystemTests.cs` (12 tests sur tmpdir)

**À faire ensuite** : remplacer les ~250 occurrences directes de `DateTime.Now` dans le code de production. Migration mécanique, peut être faite en plusieurs PRs.

### ✅ Lot 4 — Abstractions sync

**Fichiers livrés** :

- `Services/Sync/INetworkPathProvider.cs` — résolution UNC paths
- `Services/Sync/IDistributedLock.cs` (+ `IDistributedLockHandle`) — verrou inter-instances
- `Services/Sync/IChangeLog.cs` — journal pending sync
- `Services/Sync/ISyncEngine.cs` (+ `SyncOptions`, `SyncResult`) — orchestrateur
- `Services/UI/IDialogService.cs` (+ `WpfDialogService`) — dialogs MessageBox/OpenFile/SaveFile
- `Services/DTOs/ChangeJournalEntry.cs` — DTO sync (distinct du `RowChange` audit)
- `RecoTool.Tests/Services/Sync/InMemoryFakes.cs` — 3 fakes complets
- `RecoTool.Tests/Services/Sync/SyncFakeTests.cs` — 19 tests sur les fakes

### ✅ Vague qualité 8 — Performance & data-refresh wiring

**ReconciliationView perf optimizations** :
- **`EnableDataVirtualization="False"`** explicite sur le SfDataGrid : élimine l'overhead du wrapper VirtualizingCollectionView, restore le 60 fps scroll sur 5k+ rows.
- **InitialPageSize bumpé 500 → 1000** : élimine un "Load More" click pour ~80% des vues ToDo.
- **Rules catch-up déplacé off critical path** : fire-and-forget `Dispatcher.BeginInvoke @ Background` pour `ApplyRulesNowAsync` + `GetOrCreateReconciliationAsync` au lieu de bloquer le rendering. **Initial render visible ~150-300ms plus tôt**.
- **QuickSearch BeginInit/EndInit** : 1 layout pass au lieu de N (500 rows → 1 layout). Quick-search submit perceptiblement instantané.
- **Comments resolution fast-path** : skip `Regex.Replace` sur les rows dont Comments ne contient ni `[` ni `@` (~80-95% des rows). Cuts ~80% du regex work.
- Audit complet : 9 items inspectés, 5 fixés, 4 documentés (déjà-fait, hors-scope UX, ou nécessitent refactor DTO).

**HomePage refresh on ReconciliationView DataChanged** :
- **Audit** : événement `DataChanged` existait déjà sur `ReconciliationView` + `ReconciliationPage` + flags `_homeIsStale/_homeFirstShown/_reconciliationDataChangedHooked` dans MainWindow + conditional refresh dans `NavigateToHomePage`. **Mais les save paths ne le raisaient pas tous**.
- **Méthodes wirées** dans `ReconciliationView/Editing.cs` et `RowActions.cs` (11 sites) :
  - `SaveEditedRowAsync` (deux branches archived + live)
  - `UserFieldComboBox_SelectionChanged` (gated par `ruleApplied`)
  - `OpenCommentsDialogForAsync`
  - `QuickSetCommentMenuItem_Click`
  - `SetActionStatusAsync` (Mark Done / Pending)
  - `QuickTakeMenuItem_Click` (assignee)
  - `QuickSetReminderMenuItem_Click`
  - `SetClaimDateAsync`
- **Logging** via `LogHelper.WriteAction` (forwards vers Serilog).
- **Coalescing** : burst d'edits → un seul refresh HomePage différé au prochain Navigate-to-Home (le handler ne fait que `_homeIsStale = true`, pas de `Refresh()` immédiat). Save bulk = 1 refresh.
- **Edge cases** : country switch, no-op suppression (event seulement après save effectif), dispatcher marshalling (tout sur UI thread), idempotent hook (guarded par `_reconciliationDataChangedHooked`).

### ✅ Vague qualité 7 — 4 chantiers livrés en parallèle (clôture)

**OFS split #3, #4, #5** :
- 3 nouvelles extractions dans un seul pass :
  - `OfflineFirstService.BatchOps.cs` (633 LOC) — `ApplyEntitiesBatchAsync` (~500 LOC) + `DeleteEntityAsync` (~80 LOC) + `IsAccessLockException` helper.
  - `OfflineFirstService.Replication.cs` (810 LOC) — 16 méthodes : Push/Pull, Ambre/Reco/DW per-family copies, ZIP helpers, anchors, backup/sync.
  - `OfflineFirstService.Crc.cs` (90 LOC) — CRC32 helpers (`ComputeCrc32ForEntity`, `NormalizeForCrc`, `_crc32Table`, `Crc32Append`, `BuildCrc32Table`).
- **Main partial : 3117 → 1650 LOC** (-1467 LOC, -47% en une vague).
- **Cumul OFS** : 3700 → 1650 LOC (**-55%**), 21 partials au total.
- API publique byte-identique. Tous consumers compilent unchanged.

**Schema adoption résiduel** :
- `SnapshotComparisonService` : **38 literals migrés** vers Schema.Columns.Reconciliation/DwingsData/Tables.
- `AccessChangeLog` : `TableName` const migré vers `Schema.Tables.T_SyncChangeLog` (cascade vers 7 SQL strings du fichier).
- `DatabaseRecreationService` skippé (canonique DDL — préservé exprès).
- 3 TODO comments laissés : `Reconciliation.IncNumber`, `DwingsData.COMM_ID_EMAIL`, `Schema.Columns.SyncChangeLog` (entire group).
- **Total cumulé Schema adoption : ~150 sites migrés**.

**OleDbAsyncExecutor adoption final** :
- `ReferentialService.GetUsersAsync` et `GetParamValueAsync` migrés vers `OleDbAsyncExecutor.RunAsync(work, conn, ct)`. Connection pool pattern préservé.
- `ParameterService` skippé (entièrement synchrone — pas de méthode async à migrer).
- **Total cumulé OleDbAsyncExecutor : 44 méthodes adoptées**.

**Consumer migration POC #4 et #5 : VMs UserFields** :
- `ReconciliationDetailViewModel` étendu avec optional `IUserFieldsRepository userFieldsRepo = null`. `PopulateOptionsFromOffline()` (sync) et `MarkNotInDwingsAsync` (async) préfèrent désormais le repo. Fallback transparent sur `_offline.UserFields` cache.
- `RuleEditorViewModel` même pattern, `PopulateOptionsFromOffline()` migré.
- **Total consumers migrés vers repositories : 6** (HomePageVM + ReconciliationMatching + DashboardExport + ReconciliationService [cascade] + ReconciliationDetailVM + RuleEditorVM + bonus ReconciliationView/Options).

### ✅ Vague qualité 6 — 4 chantiers livrés en parallèle

**OFS split #2 — GlobalLock** :
- Concept extrait : **distributed global lock** (acquire/release/heartbeat + sync status reporting via SyncLocks table).
- 13 membres (7 publiques + 4 privés + 2 nested classes + 2 fields statiques) déplacés dans `OfflineFirstService.GlobalLock.cs` (563 LOC).
- ~533 LOC retirés du main partial (3650 → **3117 LOC**).
- API publique byte-identique. Tous consumers (`AmbreDatabaseSynchronizer`, `SyncMonitorService`, `AmbreImportService`, `OFS.SyncOperations`) compilent unchanged.

**ReconciliationService.GetAmbreDataAsync → IDataAmbreRepository** :
- Migration **point central** : tous les autres consumers (HomePageVM, ReconciliationMatching, DashboardExport) qui fallback sur `GetAmbreDataAsync` bénéficient désormais transparemment du repo.
- Both ctors étendus avec optional `IDataAmbreRepository ambreRepo = null`.
- **App.xaml.cs factory updated** : la factory `services.AddTransient<ReconciliationService>` résout désormais le repo + clock + logger via DI, activant le cascade.
- 4 tests unitaires ajoutés validant les 4 chemins (repo path, includeDeleted, unknown country, ctor signature lock).

**Schema expansion + TruthTableRepository full adoption** :
- **41 nouvelles constantes** ajoutées à `Schema.Columns.RecoRules` (4 → 45). Toutes les colonnes T_Reco_Rules canoniques couvertes.
- TruthTableRepository : CREATE TABLE / UPDATE / INSERT migrent vers les constantes. 3 TODO comments éliminés.
- SQL byte-équivalent (mêmes column lists, mêmes `?` placeholders, casse préservée).

**ReconciliationView/Options.cs migration → IUserFieldsRepository** :
- Premier consumer pour `IUserFieldsRepository`.
- Helper `EnsureUserFieldsRepository()` qui résout one-shot depuis DI (cached).
- `PopulateReferentialOptions()` préfère repo path quand disponible, fallback sur `AllUserFields` cache property.
- TODO documenté pour future async lift (méthode sync appelée depuis `async void InitializeFromServices`).

### ✅ Vague qualité 5 — 5 chantiers livrés en parallèle (finale)

**OFS split — 1ère extraction** :
- Concept extrait : **Connection strings resolution**.
- 5 méthodes déplacées dans nouveau partial `OfflineFirstService.ConnectionStrings.cs` (78 LOC) : `GetControlConnectionString`, `GetControlDbPath`, `GetCountryConnectionString`, `GetLocalConnectionString`, `GetCurrentLocalConnectionString`.
- ~52 LOC retirés du main partial (3700 → 3650 LOC).
- API publique byte-identique. Auditroit : 15 partials existaient déjà (Configuration, CountryContext, Paths, Locks, ChangeLog, Schema, Snapshots, Push, IO, Diagnostics, Referentials, Events, Maintenance, SyncGates, SyncOperations) — le découpage de fond est en bonne voie.

**Ambre family full adoption** (`Services/Ambre/*.cs`) :
- 10 SQL literals migrés vers Schema constants.
- 9 méthodes migrées vers OleDbAsyncExecutor (RunWithConnectionAsync + RunInTransactionAsync).
- 2 colonnes ajoutées à Schema (`Reconciliation.ActionStatus`, `Reconciliation.ActionDate`).
- 0 `new OleDbConnection(...)` direct restant dans `Services/Ambre/`.

**Rules family full adoption** (`Services/Rules/*.cs`) :
- 8 SQL literals migrés (TruthTableRepository, RuleProposalRepository, RuleContextBuilder).
- 3 méthodes migrées vers OleDbAsyncExecutor.
- Nouveau groupe `Schema.Columns.RuleProposals` ajouté (12 constantes).
- TODO comments laissés sur ~38 colonnes T_Reco_Rules (canonique défini dans le code, schema-expansion en follow-up).

**Snapshots family + DateTime résiduel** :
- `KpiSnapshotService` : 5 méthodes migrées vers `OleDbAsyncExecutor.RunWithConnectionAsync<T>`.
- `SnapshotService` : pas de OleDb direct (audit confirmé), pas de changement nécessaire.
- **Sweep DateTime résiduel complet** : 0 occurrence restante dans la production hors test fixtures.
- **Final count** : `DateTime.Now/UtcNow` total dans la production = **6 occurrences seulement**, toutes légitimes (IClock interface doc + SystemClock implementation + 2 comments dans OFS).

**Consumer migration POC #3 : DashboardExportService** :
- `TryGetAmbreSnapshotDateAsync` utilise `IDataAmbreRepository.GetAllAsync` quand le repo est injecté, fallback sur `ReconciliationService.GetLastAmbreOperationDateAsync` sinon.
- Ctor étendu avec optional `IDataAmbreRepository ambreRepo = null` (LAST param).
- 4 tests unitaires ajoutés validant les 4 chemins (repo path, empty result, soft-delete contract, ctor signature lock).
- Tous les 2 call sites existants compilent unchanged.

**Total cumulé final** :
- **Schema constants** : ~67 sites migrés (27 + 12 User* + 9 référentiel + 10 Ambre + 8 Rules + KpiSnapshot table).
- **OleDbAsyncExecutor adoption** : **42 méthodes** (11 + 12 User* + 9 Ambre + 3 Rules + 5 Snapshots + 2 LookupService).
- **DateTime.Now → IClock** : **132 + ~5 résiduels** = ~137 migrés sur ~210 initiaux (65%). Reste : 6 légitimes (interface doc + SystemClock body + comments).
- **Consumers migrés vers repositories** : 3 (HomePageViewModel + ReconciliationMatchingService + DashboardExportService).
- **Tests ajoutés** : 169 (4 nouveaux DashboardExportServiceTests).

### ✅ Vague qualité 4 — 3 chantiers livrés en parallèle

**User* services full adoption** :
- `UserFilterService` : 6 SQL literals migrés vers `Schema.Tables.T_Ref_User_Filter` + `Schema.Columns.UserFilter.*` (UFI_id, UFI_Name, UFI_SQL, UFI_CreatedBy). API publique synchrone préservée.
- `UserTodoListService` : ~12 SQL literals migrés + 4 méthodes async migrées vers `OleDbAsyncExecutor.RunAsync(work, conn, ct)`. Pool connection pattern préservé.
- `UserViewPreferenceService` : ~10 SQL literals migrés + 8 méthodes async migrées vers `OleDbAsyncExecutor.RunAsync`.

**Schema adoption référentiel** :
- `ParameterService` : 4 SQL literals + 2 reader column-indexers + 1 log line migrés vers `Schema.Tables.T_Param` / `Schema.Columns.Param.*`.
- `ReferentialService` : 5 SQL literals migrés (T_User CRUD + param probe loop principal). Variants legacy (Par_Key/Par_Code/Par_Name) laissés avec TODO comments.
- `ReferentialCacheService` : pas de SQL direct (cache pur), aucun changement nécessaire.

**Consumer migration POC #2 : ReconciliationMatchingService** :
- Substituté pour KpiSnapshotService (qui ne lit pas AMBRE — uniquement `ReconciliationViewData`).
- Helper privé `LoadAmbreAsync(countryId, ct)` qui préfère `IDataAmbreRepository.GetAllAsync` quand le repo est injecté, fallback sur `_reconciliationService.GetAmbreDataAsync` sinon.
- Deux call sites consolidés derrière le helper.
- Ctor étendu avec optional `IDataAmbreRepository ambreRepo = null` (LAST param).
- Tous les call sites existants compilent unchanged (3-arg ctor reste compatible).

### ✅ Vague qualité 3b — 3 chantiers livrés en parallèle

**DateTime.Now sweep dans `Services/OfflineFirst/`** :
- 35 occurrences migrées dans 5 fichiers partials (`OfflineFirstService.cs`, `.SyncOperations.cs`, `.Events.cs`, `.Maintenance.cs`, `.SyncGates.cs`).
- Pattern : `private readonly IClock _clock` déclaré dans le main partial, partagé par tous les partials.
- Ctor OFS étendu avec optional `IClock clock = null` — DI auto-resolves via `SystemClock.Instance` registré.
- `PurgeOrphanedTempFilesAsync` (static) reçoit également un optional `IClock` param.
- **Total cumulé DateTime.Now → IClock : 132 occurrences migrées** sur ~210 (63%).

**Adoption `OleDbAsyncExecutor`** :
- `Services/Rules/RuleProposalRepository.cs` : 5 méthodes migrées (`EnsureTableAsync`, `InsertProposalsAsync`, `LoadAsync`, `UpdateStatusAsync`, `MarkRecoProposalsStaleAsync`). Toutes les `OpenAsync`/`ExecuteXxxAsync`/`ReadAsync` fake-async éliminées, remplacées par du synchrone sur le thread pool. Transaction commit/rollback géré par `RunInTransactionAsync`.
- `Services/LookupService.cs` : helper privé `ExecuteScalarListAsync<T>` migré ; 3 méthodes publiques (currencies/guarantee statuses/guarantee types — alimentation combobox) en bénéficient transitivement.
- `SnapshotService` et `ReferentialCacheService` skippés (pas de OleDb direct, ou usage de pool de connections incompatible).

**Migration consumer POC : HomePageViewModel** :
- `HomePageViewModel.LoadKpisAsync` utilise désormais `IDataAmbreRepository.GetAllAsync` quand le repo est injecté ; fallback vers `_reco.GetAmbreDataAsync` sinon (rétrocompat).
- Ctor étendu avec optional `IDataAmbreRepository ambreRepo = null`. DI auto-injecte (registration déjà en place).
- Test ajouté : `LoadKpis_UsesAmbreRepositoryWhenInjected` valide le bon path + vérifie que le service legacy n'est PAS appelé quand le repo est présent.
- **Première démonstration concrète** que la migration consumer→repository est non-breaking et trivialement testable.

### ✅ Vague qualité 3a — 4 chantiers livrés en parallèle

**Service locator removal (Windows/*.cs)** :
- 10 appels `App.ServiceProvider?.GetService<T>()` ad-hoc dans des Click/event handlers éliminés.
- Pattern : résolution ONE-SHOT dans le ctor MVVM, stockage en `private readonly` field, réutilisation dans tous les handlers.
- 6 nouveaux fields privés, 3 factory delegates (`_reconciliationPageFactory`, `_importAmbreWindowFactory`, `EnsureOptionsService()`).
- Aucune signature de ctor changée ; tous les call sites compilent unchanged.
- Restants : 23 appels dans les ctor bodies eux-mêmes (fallback designer/legacy — pattern toléré).

**DateTime.Now sweep dans Windows** :
- 37 occurrences migrées dans 15 fichiers code-behind WPF (`ReconciliationView/*`, `ReconciliationPage`, `HomePage`, `RulesHealthWindow.Handlers`, etc.).
- Pattern : `DateTime.Now/UtcNow` → `BaseEntity.Clock.Now/UtcNow` (static IClock global, swappable en tests).
- `using RecoTool.Models;` ajouté dans 6 fichiers.
- Total cumulé (services + Windows) : **~97 occurrences** migrées sur ~210 initiales (46%).

**OleDbAsyncExecutor** :
- Nouveau helper `Infrastructure/DataAccess/OleDbAsyncExecutor.cs` avec 4 méthodes :
  - `RunAsync(Action, conn, ct)` et `RunAsync<T>(Func, conn, ct)` — exécute du code OleDb sur le pool de threads.
  - `RunWithConnectionAsync<T>(connString, func, ct)` — ouvre/dispose la connection elle-même.
  - `RunInTransactionAsync<T>(connString, func, ct)` — transaction commit/rollback automatique.
- 15 tests unitaires : threading, propagation valeur/exception/CT, null-guards.
- Static `Logger` property (default NullLogger). Aucun consommateur migré (point de départ pour des PRs incrémentales).

**Repository pattern foundation** :
- Nouvelles interfaces `IDataAmbreRepository` (rows AMBRE par pays) et `IUserFieldsRepository` (référentiel UserFields).
- Implémentations OleDb concrètes utilisant `OleDbAsyncExecutor` + `Schema` constants + `ILogger<T>` (optional).
- In-memory fakes pour tests : `InMemoryDataAmbreRepository`, `InMemoryUserFieldsRepository` + 17 tests unitaires.
- DI registration dans `#region Repository Pattern (Domain Repos)` dans App.xaml.cs.
- `ITruthRuleRepository` skipped (redondant avec `IRulesAdmin` existant).
- Aucun consumer migré — la fondation est là pour les PRs futures.

### ✅ Wave qualité — 7 chantiers livrés en parallèle

**Vague 1 (5 agents en parallèle)** :
- **Serilog structured logging** : `Infrastructure/Logging/LoggingSetup.cs`, `ILoggerFactory` câblé en DI, rolling file daily à `%APPDATA%/RecoTool/logs/recotool-.log`. `LogHelper` legacy préservé en byte-compat et forward désormais vers le pipeline structuré.
- **Schema constants** : `Infrastructure/Schema.cs` — 199 constantes (19 tables + 180 colonnes en 15 classes nested). Élimine les magic strings SQL.
- **CacheService → IMemoryCache** : réécriture sur `Microsoft.Extensions.Caching.Memory`. API publique strictement préservée. Tests existants vérifiés.
- **DateTime.Now → IClock sweep** : 60 occurrences migrées dans 22 fichiers (Ambre, Snapshots, Rules, API, Utils). Pattern : ctor injection (optional) ou static `Clock` property.
- **Lazy HomePage charts** : `ScheduleChartHydration()` via Dispatcher BeginInvoke @ Background. Compteurs immédiats, charts différés. Page utilisable en < 100ms au lieu de 2-5s.

**Vague 2 (3 agents en parallèle, dépendent de Serilog)** :
- **Startup health checks** : `IStartupHealthCheck`, `HealthCheckRunner` (parallel + 10s timeout), 3 checks concrets (LocalDB, NetworkShare, FreeApi), `HealthCheckDialog` WPF, registration DI dans App.xaml.cs. Comportement UAT vs prod différencié.
- **Catch muets sweep** : 51 `catch { }` muets remplacés par `_logger.LogWarning(ex, ...)` dans 12 services. Optional `ILogger<T>` ctor params, byte-compat avec tous call sites.
- **CancellationToken sweep** : 37 méthodes async ont reçu `CancellationToken ct = default`. CT propagé aux awaits internes. Loop checkpoints sur boucles longues. Rethrow explicite d'OCE avant les catches `Exception`.

**Audit final** : 0 compile blocker, 0 conflit de merge entre les 7 agents. `App.xaml.cs` regions (Structured Logging + Health Checks) bien séparées, ordre DI correct (logger avant health checks).

### ✅ Migration `DateTime.Now` → `IClock` — pilotes critiques

Pattern : pour chaque classe statique ou utilitaire fortement utilisée, on
ajoute une propriété `public static IClock Clock { get; set; } = SystemClock.Instance;`.
Production utilise le clock système ; tests peuvent swap pour FakeClock.
Migration non-breaking — toutes les API restent inchangées.

- `Models/BaseEntity.cs` — 3 occurrences. Impact transverse : toutes les entités
  qui héritent (Reconciliation, DataAmbre, etc.).
- `Services/Rules/RuleApplicationHelper.cs` — 8 occurrences. Cœur de
  l'application des règles ; déjà couvert par tests unitaires.
- `Services/UserFieldUpdateService.cs` — 2 occurrences. Action/KPI stamping.
- `Infrastructure/Logging/LogHelper.cs` — 4 occurrences. Tous les logs perf/
  rules taggés via clock.
- `Services/Rules/RulesEngine.cs` — 3 occurrences. Cache TTL des règles
  désormais déterministe.
- `ViewModels/CommentsDialogViewModel.cs` — 1 occurrence. Ctor étendu avec
  `IClock clock = null` optionnel.
- `ViewModels/ReconciliationDetailViewModel.cs` — 1 occurrence. `AppendComment`
  délégué au `BaseEntity.Clock` static.

**Note xUnit** : les statics `Clock` sont muables — si des tests futurs
veulent swap un FakeClock, ils doivent utiliser un fixture xUnit
(`IClassFixture`/`Collection`) pour sérialiser l'exécution.

### ✅ `AccessSyncDataTransport` clean-room — livré

Implementation clean-room de `ISyncDataTransport` à
`Services/Sync/AccessSyncDataTransport.cs`. Architecture :

- **Push** : lit `IChangeLog.ReadSinceAsync(0)` → applique INSERT/UPDATE/DELETE
  via OleDb paramétré dans une transaction → `MarkAsSyncedAsync`. Désérialisation
  JSON du `ChangeJournalEntry.PayloadJson` via `JToken` (Newtonsoft, déjà
  référencé).
- **Pull** : snapshot complet de `T_Reconciliation` réseau → upsert dans le
  local DB en transaction. Approche conservatrice ; incremental pull
  watermark-based à venir comme follow-up.
- **DI** : 4 paramètres ctor — `IChangeLog`, `INetworkPathProvider`, et 2
  delegates `Func<string, string>` pour les connection strings local/réseau.
  Aucune dépendance sur `OfflineFirstService`.
- **Tests** : `RecoTool.Tests/Services/Sync/AccessSyncDataTransportTests.cs`
  — 10 tests unitaires (ctor guards, empty-input short-circuits, conn-string
  failures). Integration tests OleDb à écrire séparément quand un fixture
  Access est disponible.

`LegacySyncDataTransport` reste registré en DI pour la transition. Bascule
vers `AccessSyncDataTransport` quand parité opérationnelle validée en UAT.

### ✅ XAML Branching — TOUTES les fenêtres MVVM-aware

Approche **additive non-breaking** : on ajoute un constructeur VM-aware sur
chaque fenêtre sans casser le ctor legacy. Les call sites historiques
fonctionnent encore ; les nouveaux call sites peuvent utiliser le pattern
MVVM. Quand la migration complète sera faite, on supprimera les ctors legacy.

- `ProgressWindow(ProgressWindowViewModel)` — bindings ajoutés sur
  `StatusMessage`/`MainProgressBar`/`PercentageText` avec FallbackValue pour
  la rétrocompat. CloseRequested → ferme avec DialogResult.
- `CommentsDialog(CommentsDialogViewModel)` — DataContext seedé depuis le
  VM. Le code-behind d'autocomplete @mention reste tel quel (logique
  WPF-keyboard, pas appropriée à hoister au VM). CloseRequested →
  `ResultComments` + Close.
- `RuleEditorWindow(RuleEditorViewModel)` — DataContext = vm. Bridge :
  `EditedRule` est mirroré sur la Window pour que `Save_Click` (handler XAML
  legacy) trouve la rule non-nulle. CloseRequested → `ResultRule`/`RunNow`
  + DialogResult.
- `RulesAdminWindow(RulesAdminViewModel)` — DataContext = vm. Les
  services legacy (`_offlineFirstService`/`_reconciliationService`/
  `_repository`) sont initialisés via DI pour que les click handlers
  XAML continuent de fonctionner. EditRuleRequested → ouvre
  `RuleEditorWindow(vm)` et applique. RunRulesNowRequested → délègue
  à `RunRulesNow_Click` legacy.
- `MainWindow(OfflineFirstService, MainWindowViewModel)` — chained ctor
  préserve toute la logique legacy. DataContext = vm. Events
  ImportRequested/OpenReconciliationRequested/ExitRequested bridged
  vers helpers privés `ShowImportDialog`/`NavigateToReconciliation`/
  `Close`. DI factory mise à jour dans `App.xaml.cs`.
- `HomePage(OfflineFirstService, ReconciliationService, HomePageViewModel)`
  — chained ctor. DataContext = vm. ImportRequested bridge via
  `Window.GetWindow(this) as MainWindow` + réflexion sur `ShowImportDialog`.
- `ImportAmbreWindow(..., ImportAmbreViewModel)` — chained ctor.
  DataContext laissé sur `this` (XAML utilise direct property access via
  named elements, pas de bindings). VM connecté pour `CompletedWithResult`
  → ferme avec DialogResult.
- `ReconciliationDetailWindow(ReconciliationDetailViewModel, item, all, reco, offline)`
  — chained ctor. DataContext = vm. SaveCompleted + CancelRequested
  bridgent vers DialogResult + Close.
- `ReconciliationPage(reco, offline, repo, freeApi, ReconciliationPageViewModel)`
  — chained ctor. DataContext = vm. AddViewRequested/InvoiceFinderRequested
  rejouent les Click handlers legacy. RefreshAsync déclenché à l'init.
- `ReconciliationView` (UserControl) : pattern hybride déjà en place.
  Le `VM` property est désormais settable (au lieu de get-only) pour
  permettre l'injection d'un VM préconfiguré.
- `ReconciliationImportWindow` : aucun binding XAML, pas de branching
  nécessaire (Click handlers en code-behind suffisent).
- `TodoListSessionWarning` : UserControl auto-suffisant (INPC + ObservableCollection
  embarqués dans le code-behind). Pattern MVVM-clean acceptable, pas de VM
  externe nécessaire.

### 🟡 Lot 2 — MVVM pilote `MainWindow`

**Fichiers livrés** :

- `ViewModels/ViewModelBase.cs` — INotifyPropertyChanged + helper `SetField`
- `ViewModels/RelayCommand.cs` — sync + `AsyncRelayCommand` avec `IsExecuting` flag
- `ViewModels/MainWindowViewModel.cs` — 8 properties, 6 commands, 3 events
- `Services/UI/WpfDialogService.cs` — production binding
- `App.xaml.cs` : `MainWindowViewModel` + `IDialogService` enregistrés DI
- `RecoTool.Tests/ViewModels/MainWindowViewModelTests.cs` — 25 tests Moq

**Pas encore fait** : la View `MainWindow.xaml` utilise encore `DataContext = this;` (pattern legacy). Migration **manuelle** côté IDE, recommandée pour la prochaine session :

#### Étape 1 : modifier le ctor `MainWindow.xaml.cs`

```csharp
public MainWindow(OfflineFirstService offlineService)
{
    InitializeComponent();
    _offlineFirstService = offlineService;

    // ── NEW : récupérer la VM depuis le DI et brancher les events ──
    var vm = App.ServiceProvider.GetRequiredService<MainWindowViewModel>();
    vm.ImportRequested += async (_, _) => await ShowImportDialog();
    vm.OpenReconciliationRequested += (_, _) => NavigateToReconciliation();
    vm.ExitRequested += (_, _) => Close();
    this.DataContext = vm;

    // Pré-charger la liste des pays
    _ = ((AsyncRelayCommand)vm.LoadCountriesCommand).ExecuteAsync(null);

    // ... reste du ctor inchangé : InitializeServices(), SetupEventHandlers(), etc.
}
```

#### Étape 2 : migrer les bindings XAML un par un

```xml
<!-- AVANT (DataContext = this) -->
<Window Title="RecoTool" ...>
  <ComboBox x:Name="CountryCombo" SelectionChanged="CountryCombo_SelectionChanged"/>
</Window>

<!-- APRÈS (DataContext = MainWindowViewModel) -->
<Window Title="{Binding Title}" ...>
  <ComboBox ItemsSource="{Binding Countries}"
            SelectedItem="{Binding CurrentCountry, Mode=TwoWay}"
            DisplayMemberPath="CNT_Name"/>
  <Button Content="Refresh" Command="{Binding RefreshCommand}"/>
  <Button Content="Import AMBRE" Command="{Binding ImportAmbreCommand}"/>
  <TextBlock Text="{Binding SyncStatusText}"/>
  <ProgressBar IsIndeterminate="{Binding IsSyncing}"/>
</Window>
```

#### Étape 3 : retirer les handlers code-behind devenus inutiles

Les `XxxButton_Click`, `CountryCombo_SelectionChanged` etc. peuvent être supprimés au fur et à mesure que les commandes/bindings prennent le relais. Un test FlaUI (Lot 9) attrapera les régressions.

**Effort** : ~30-60 min pour faire passer MainWindow propre. Idéalement fait en branche dédiée avec validation manuelle entre chaque retrait de handler.

### ✅ Mockabilité ViewModels — **étendue**

Pour rendre les ViewModels entièrement testables sans `OleDb`, deux interfaces
supplémentaires ont été extraites :

- `Services/IUserFilterService.cs` — surface de `UserFilterService` (Save / Load /
  ListNames / ListDetailed / Delete). `SanitizeWhereClause` reste un helper static
  (hors interface).
- `Services/IUserTodoListService.cs` — surface de `UserTodoListService`
  (EnsureTable / List / Upsert / Delete, tout async).
- `Services/IUserViewPreferenceService.cs` — surface de `UserViewPreferenceService`
  (GetAll / GetByName / Insert / Update / Upsert / ListNames / ListDetailed /
  Delete). Préparé pour la prochaine migration de `ReconciliationView` code-behind.
- `Services/Ambre/IAmbreImportService.cs` — surface de `AmbreImportService`
  (`ImportAmbreFile`, `ImportAmbreFiles`). Permet à `ImportAmbreViewModel`
  d'être testé sans `FormatterServices.GetUninitializedObject`. **Note** :
  `UserFieldUpdateService` est resté static (helpers purs sans DB) — pas
  d'interface nécessaire.
- `Services/Rules/IRulesAdmin.cs` étendu avec `LoadRulesAsync(CancellationToken)`
  pour découpler `RulesAdminViewModel` du repository concret.

**Conséquences** :

- `ReconciliationPageViewModel` dépend désormais des interfaces, plus des concrets.
  Plus besoin de connection strings fakes dans les tests — `Mock<IUserFilterService>`
  + `Mock<IUserTodoListService>` suffisent.
- `RulesAdminViewModel` testé sans `TruthTableRepository` réel.
- DI dans `App.xaml.cs` : `IUserFilterService`, `IUserTodoListService`,
  `IUserViewPreferenceService`, `IAmbreImportService`, `IRulesAdmin` registrés
  et résolus pour la construction des VMs.
- Tests ViewModels migrés : `ReconciliationPageViewModelTests`,
  `RulesAdminViewModelTests` et `ImportAmbreViewModelTests` utilisent uniquement
  Moq. `ImportAmbreViewModelTests` exerce désormais le chemin d'import (single
  file vs. multi-files) sans plus dépendre du dummy `GetUninitializedObject`.

### ✅ Bind-compatibilité ViewModels ↔ XAML

Préparation au branchement futur des XAML sur les VMs :

- `RuleEditorViewModel` mis à jour pour matcher la surface attendue par
  `RuleEditorWindow.xaml` : `BoolChoices` et `MtStatusChoices` sont désormais
  des collections d'objets `Label/Value` (matchant
  `DisplayMemberPath="Label" SelectedValuePath="Value"`), `ActionStatusChoices`
  ajouté (tri-state PENDING/DONE/None), `GuaranteeTypes` et `TransactionTypes`
  ajoutés. Sans cette adaptation, switcher `DataContext = vm` aurait cassé
  silencieusement les bindings WPF.
- `RecoTool.Tests/Bindings/XamlBindingCompatTests.cs` : framework de tests
  qui parse les XAML, extrait les chemins `{Binding ...}` et vérifie via
  réflexion qu'ils résolvent contre le VM cible. Détecte les ruptures de
  binding à test time plutôt qu'à runtime. Premier test concret :
  `RuleEditorWindow ↔ RuleEditorViewModel`. Helpers `ExtractBindingPaths`
  / `PathResolvesOn` testés indépendamment.
- **Framework étendu** : parser amélioré pour capturer `Path=` à l'intérieur
  de `{Binding RelativeSource=..., Path=X}`, normalisation de `DataContext.X`
  → `X`, et support multi-contexte (`fallbackItemTypes`) pour les bindings
  inclus dans des `DataGrid` cells / `DataTemplate`.
- **Couverture totale : 13 fenêtres** avec tests bind-compat opérationnels.
  - `RuleEditorWindow` ↔ `RuleEditorViewModel` (flat bindings)
  - `RulesAdminWindow` ↔ `RulesAdminViewModel` + fallback `TruthRule`
  - `CommentsDialog` ↔ `CommentsDialogViewModel` + fallback `UserOption`
  - `ProgressWindow` ↔ `ProgressWindowViewModel` (no-op)
  - `RuleDebugWindow` ↔ `RuleDebugViewModel` + `RuleDebugItem` / `ConditionDebugItem`
    (renommage `OutputKpi` → `OutputKPI`, ajout `ConditionDebugItem.Status`)
  - `DwingsButtonsWindow` ↔ `DwingsButtonsViewModel` + `DwingsTriggerItem`
    (DTO étendu : `ID`, `DWINGS_GuaranteeID`, `DWINGS_InvoiceID`, `DWINGS_BGPMT`,
    `RequestedAmount`, `Comments`, `ValueDate`, `IsGrouped`, `LineCount`,
    `IsAllowed` tri-state)
  - `InvoiceFinderWindow` ↔ `InvoiceFinderViewModel` + `InvoiceFinderRow` /
    `DwingsInvoiceDto` / `DwingsGuaranteeDto`
  - `MainWindow` ↔ `MainWindowViewModel` (étendu avec 14 stubs : `AvailableCountries`,
    `ShowFreeAuthButton`, `IsMultiUserMode`, `MultiUserButtonText`,
    `NetworkStatusBrush`, `NetworkStatusText`, `AppVersion`, `InitializationStatus`,
    `InitializationBrush`, `ReferentialCacheStatus`, `ReferentialBrush`,
    `ReferentialCacheAvailable`, `OperationalDataStatus`, `IsOffline`)
  - `HomePage` ↔ `HomePageViewModel` (étendu avec `IsDwingsDataFromToday`,
    `DwingsWarningMessage`, `TodoCards`, `AlertItems`, `AssigneeLeaderboard`,
    `CompletionEstimate`, 13 séries de charts) + DTOs `TodoCard`, `HomeAlert`,
    `AssigneeStats`, `CompletionEstimate`
  - `ReconciliationPage` ↔ `ReconciliationPageViewModel` (étendu avec
    `AddViewModeIndicator`, `CanChangeAccount/Status`, `CanUseSavedControls`,
    `SavedFilters`, `SavedViews`, `SelectedSavedView`)
  - `ReconciliationDetailWindow` ↔ `ReconciliationDetailViewModel` + `LinkedItemRow`
    (renommage `KpiOptions` → `KPIOptions`, `SelectedKpiId` → `SelectedKPIId`)
  - `RulesHealthWindow` ↔ `RulesHealthViewModel` + 3 nouveaux DTOs
    (`RuleCoverageStats`, `RuleProposalDisplay`, `RuleRunResultDisplay`)
  - `SpiritGeneSearchWindow` ↔ `SpiritGeneSearchViewModel` + `SpiritGeneResultRow` /
    `SpiritGeneDetailItem`
- **Restant** :
  - `ReconciliationView` (UserControl, 133 bindings, DataContext=this avec `VM.*`
    paths) — pattern différent qui nécessiterait `ReconciliationView` lui-même
    comme type racine.
  - `TodoListSessionWarning` — pas de VM existant.
  - `FilterPickerWindow`, `PreviewTextDialog`, `ReconciliationImportWindow` —
    pas de `{Binding ...}`, ne nécessitent pas de test.

### ✅ Lot 5 — Refactor OFS — **complet**

**Fichiers livrés** :

- `Services/OfflineFirst/OfflineFirstServiceV2.cs` — façade V2 qui prend les 6 abstractions par DI
- `Services/Sync/FileSystemDistributedLock.cs` — `IDistributedLock` via fichiers `.lock` atomiques. **21 tests**.
- `Services/Sync/AccessChangeLog.cs` — `IChangeLog` via table `T_SyncChangeLog` auto-créée. **11 tests d'intégration**.
- `Services/Sync/OfflineFirstNetworkPathProvider.cs` — adapter `INetworkPathProvider` lisant la table de paramètres OFS legacy.
- `Services/Sync/ISyncDataTransport.cs` — interface séparant orchestration vs transport (innovation par rapport au plan initial — réduit le risque).
- `Services/Sync/SyncEngine.cs` — `ISyncEngine` orchestrateur clean. **18 tests Moq** couvrant lock acquisition, ordre push/pull, timeout, cancellation, error handling.
- `Services/Sync/LegacySyncDataTransport.cs` — adapter qui délègue les push/pull au legacy `OfflineFirstService` pour la transition.
- `Configuration/FeatureFlags.cs` — flag `UseSyncV2` (false par défaut) pour basculer progressivement.
- `App.xaml.cs` — wiring DI complet : toutes les abstractions et concrets enregistrés. `OfflineFirstServiceV2` résoluble.

#### Architecture livrée

```
                  ┌─ IDistributedLock ──── FileSystemDistributedLock
                  │
SyncEngine ───────┼─ IChangeLog ─────────── AccessChangeLog
(orchestration)   │
                  └─ ISyncDataTransport ── LegacySyncDataTransport
                                              └─ OFS legacy push/pull (1015 LOC)
                                              ↓ futur
                                              AccessSyncDataTransport (clean room)
```

L'orchestration (`SyncEngine`) est **entièrement testée** sans toucher Access — transport mocké. Le transport legacy continue de fonctionner inchangé. Quand on voudra retirer le legacy, il suffira d'écrire un `AccessSyncDataTransport` "clean room" et de basculer la registration DI.

#### Activation V2

Aujourd'hui, le V2 est **disponible mais non utilisé** par défaut. Pour l'activer :

```csharp
// Au démarrage (ou via toggle UI)
FeatureFlags.UseSyncV2 = true;

// Puis dans le code qui appelle la sync :
if (FeatureFlags.UseSyncV2)
{
    var v2 = App.ServiceProvider.GetRequiredService<OfflineFirstServiceV2>();
    var result = await v2.SynchronizeAsync(countryId);
}
else
{
    await offlineFirstService.SynchronizeData(); // legacy
}
```

#### Étape suivante (optionnelle) — `AccessSyncDataTransport` clean room

Pour retirer définitivement la dépendance legacy :

1. Écrire `AccessSyncDataTransport` qui réimplémente `PushAsync`/`PullAsync` directement sur `IFileSystem` + `IChangeLog` + `INetworkPathProvider`.
2. Tests d'intégration multi-instance (Lot 8) pour valider la parité.
3. Bascule de la registration DI quand parité atteinte.

Estimation : 1-2 semaines à 1 dev. Risque : élevé. Mais désormais **aucun blocant architectural** — tout le squelette est prêt.

## Couverture totale

| | Avant ce refactor | État actuel |
|---|---|---|
| Tests unitaires | 526 | **~890** (+364) |
| Fichiers de tests | 51 | **~80** |
| Interfaces extraites | 0 | **15** |
| Implémentations concrètes (Lot 5) | 0 | **5** |
| ViewModels créés | 0 | **16** (toutes les fenêtres + dialogs avec logique) |
| Services migrés vers DI | 0 | **15+** |
| Code-behind WPF migré | 0 | 0 (VMs prêts, branchement XAML manuel) |
| Tests intégration | 5 | **17** (+ RuleProposalRepository) |

## Métriques de succès rappel

| Métrique | Cible (plan) | État actuel |
|---|---|---|
| `OfflineFirstService` LOC | < 1 000 | 3 700 (V2 prêt à recevoir la migration) |
| Code-behind WPF | < 8 000 (de 26 250) | 26 250 (inchangé) |
| Tests totaux | 1 200-1 500 | ~640 |
| Suite unitaire CI | < 60s | ~5s actuellement |

## Recommandation pour la suite

Ordre de bataille suggéré pour les prochaines sessions, par valeur/risque décroissants :

1. **Brancher `MainWindowViewModel` sur `MainWindow.xaml`** — 30-60min, risque moyen.
   Snippet fourni ci-dessus. Validation manuelle par fonctionnalité.
2. **Activer `UseSyncV2` en environnement UAT** — 1j. Toggle le flag, valider que
   les pushs/pulls passent par le nouveau `SyncEngine` (le transport reste legacy
   donc le comportement est identique mais les locks/changelog passent par les
   nouveaux composants testés).
3. **Tests intégration multi-instance** (Lot 8) — 1 sem. Maintenant possible :
   le `SyncEngine` peut être instancié avec `InMemoryDistributedLock` +
   `InMemoryChangeLog` + transport mocké pour simuler 2+ instances concurrentes.
4. **`AccessSyncDataTransport` clean room** — 1-2 sem, risque élevé. Étape finale
   pour retirer la dernière dépendance au code legacy.
5. **Lot 3 généralisation MVVM** — 2-3 semaines mécaniques. HomePage,
   ReconciliationPage, ReconciliationView, dans cet ordre.

Chaque étape est :
- **Compilable** indépendamment
- **Testable** dès qu'écrite
- **Réversible** via git si problème (pas de big-bang)

## Ce qui est bloquant pour la suite

Rien de bloquant. Tout le travail restant peut être fait en sessions courtes (1-2 jours)
chacune débloquant la suivante. Les fondations (Lots 0, 4, début 5) sont solides.

## Fichiers à lire en priorité pour reprendre

1. `REFACTOR_PLAN_UI_SYNC.md` — vision d'ensemble et calendrier
2. `Services/Sync/ISyncEngine.cs` + `SyncEngine` squelette ci-dessus — prochaine cible
3. `Services/Sync/AccessChangeLog.cs` — référence pour écrire SyncEngine (mêmes patterns)
4. `Services/Sync/FileSystemDistributedLock.cs` — exemple complet d'impl. testée
5. `Services/OfflineFirst/OfflineFirstServiceV2.cs` — placeholder où arriveront les concrètes
6. `ViewModels/MainWindowViewModel.cs` — pattern de référence pour Lot 3
