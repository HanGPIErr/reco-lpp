# Plan d'action — Refactoring testabilité **UI** et **Sync réseau**

État de départ : 526 tests sur le domaine métier (services, helpers, règles, import). Reste **non couvert** :

- **UI WPF** : ~26 250 lignes de code-behind dans `Windows/` et `UI/` — principalement du logic dans `*.xaml.cs` (MainWindow 1 902 LOC, HomePage 2 659 LOC, ReconciliationPage 2 934 LOC, ReconciliationView 2 072 LOC + 25 partials).
- **Sync réseau** : `OfflineFirstService.SyncOperations.cs` (1 015 LOC), `OfflineFirstService.cs` (3 699 LOC mixant paths/sync/locks), `Services/Sync/BackgroundSyncEngine.cs`, et la machinerie `Locks/Schema/Maintenance` éparpillée sur les partials.

Objectif final : pyramide de tests saine — tests unitaires rapides (ViewModels, sync logic pure), tests d'intégration moyens (sync end-to-end sur Access local), tests UI end-to-end limités (FlaUI sur les workflows critiques).

---

## Vue d'ensemble — 9 lots

| # | Lot | Effort | Risque | Bloquant pour |
|---|-----|--------|--------|---------------|
| 0 | Infrastructure cross-cutting (`IClock`, `IFileSystem`, DI) | 2-3j | Faible | 1, 4 |
| 1 | Découpage `OfflineFirstService` en partials cohérents | 3-5j | Moyen | 4, 5 |
| 2 | Extraction MVVM (ViewModels) sur 1 fenêtre pilote | 3-5j | Élevé (régressions UI) | 3, 7 |
| 3 | Généralisation MVVM aux autres fenêtres | 2-3 sem. | Moyen (tâche répétitive) | 7 |
| 4 | Abstractions sync (`INetworkPathProvider`, `ISyncEngine`, `IDistributedLock`) | 1 sem. | Moyen | 5, 6 |
| 5 | Refactor `OfflineFirstService` derrière les abstractions | 1-2 sem. | Élevé | 6 |
| 6 | Tests unitaires sync (Moq sur les abstractions) | 1 sem. | Faible | – |
| 7 | Tests unitaires ViewModels | 1-2 sem. | Faible | 8 |
| 8 | Tests d'intégration sync multi-instance (concurrence, locks, conflits) | 1 sem. | Moyen | – |
| 9 | Tests UI automatisés (FlaUI) sur 5-8 workflows critiques | 1 sem. | Moyen (flakiness) | – |

**Effort total estimé : 8-12 semaines à 1 dev temps plein.**

---

## Lot 0 — Infrastructure cross-cutting

**Objectif** : poser les abstractions partagées dont les lots suivants vont dépendre.

### Livrables

1. **`IClock`** dans `Infrastructure/Time/IClock.cs` :
   ```csharp
   public interface IClock
   {
       DateTime Now { get; }
       DateTime UtcNow { get; }
       DateTime Today { get; }
   }
   public sealed class SystemClock : IClock { /* DateTime.Now / UtcNow / Today */ }
   ```
   Remplace les ~250 occurrences directes de `DateTime.Now` dans le projet (vérifié via grep).

2. **`IFileSystem`** dans `Infrastructure/IO/IFileSystem.cs` (sous-ensemble minimal de `System.IO.Abstractions`) :
   ```csharp
   public interface IFileSystem
   {
       bool FileExists(string path);
       Stream OpenRead(string path);
       Stream OpenWrite(string path);
       void Copy(string from, string to, bool overwrite);
       void Delete(string path);
       string[] EnumerateFiles(string path, string searchPattern);
       DateTime GetLastWriteTimeUtc(string path);
       long GetFileSize(string path);
       // ... ajouts au fil des refactos
   }
   public sealed class SystemFileSystem : IFileSystem { /* délègue à System.IO */ }
   ```

3. **Container DI** au niveau `App.xaml.cs` : déjà partiellement en place (lignes 117-122 du fichier). Étendre pour :
   - Enregistrer `IClock`, `IFileSystem`
   - Préparer le terrain pour les ViewModels et les abstractions sync

4. **Tests** : `Infrastructure/Time/SystemClockTests.cs` (smoke), `Infrastructure/IO/SystemFileSystemTests.cs` (intégration sur tmpdir).

### Critères de validation

- Build OK
- Tous les tests existants verts
- 1 PR de référence montrant comment migrer une utilisation de `DateTime.Now` vers `_clock.Now` (ex : `RuleApplicationHelper.StampUserEdit`)

---

## Lot 1 — Découpage `OfflineFirstService` en partials cohérents

**Objectif** : préparer la chirurgie du Lot 5 en clarifiant les responsabilités. Aucun changement de comportement, juste de l'organisation.

### Livrables

État actuel des partials (à clarifier) :
- `OfflineFirstService.cs` (3 699 LOC) — fourre-tout principal, contient ctor + auth + connexions + sync + paths
- `OfflineFirstService.SyncOperations.cs` (1 015 LOC)
- `OfflineFirstService.Paths.cs` (293 LOC) — OK
- `OfflineFirstService.Schema.cs` (274 LOC) — OK
- `OfflineFirstService.Maintenance.cs` (194 LOC) — OK
- 11 autres partials < 200 LOC

**Cible** : éclater le `.cs` principal de 3 699 LOC en :
- `.Initialization.cs` — ctor, init, dispose
- `.Connections.cs` — `GetCountryConnectionString`, `GetCurrentLocalConnectionString`, etc.
- `.Lookups.cs` — `GetCountries`, `GetCountryByIdAsync`, `UserFields` (déjà dans `.Referentials.cs` partiellement)
- `.Sync.cs` — agréger SyncOperations + SyncGates + Push (déjà partiels)
- `.Status.cs` — `SetSyncStatusAsync`, événements de progression

Aucune méthode déplacée ne doit voir sa visibilité ou sa signature changer.

### Critères de validation

- 526 tests existants verts
- Aucune `public` méthode déplacée n'a vu sa signature changer
- Test : un grep `^\s*public\s` sur tous les partials retourne le même nombre de membres avant/après

---

## Lot 2 — Extraction MVVM sur `MainWindow` (pilote)

**Objectif** : démontrer le pattern MVVM sur une fenêtre représentative avant de l'industrialiser.

### Pourquoi `MainWindow` ?
- 1 902 LOC : assez pour montrer la valeur, pas trop pour bloquer
- Point d'entrée navigationnel — bien isoler son ViewModel facilite les autres
- Contient déjà la logique de country switch et de sync status

### Livrables

1. **Nouveau dossier** `ViewModels/` avec :
   - `ViewModelBase.cs` (INPC + DispatcherSafe `Dispatcher.InvokeAsync`)
   - `RelayCommand.cs` (impl. simple d'`ICommand`)
   - `MainWindowViewModel.cs` :
     - Properties : `CurrentCountry`, `Countries`, `SyncStatus`, `IsSyncing`, `LastSyncAt`, `CurrentUser`, `IsBusy`
     - Commands : `SwitchCountryCommand`, `ImportCommand`, `OpenReconciliationCommand`, `ExitCommand`, `RefreshCommand`
     - Dépendances injectées : `IOfflineFirstService`, `IReconciliationService`, `IClock`
     - Aucun reference à `Application.Current`, `MessageBox`, `Window` (la View se charge de la présentation)

2. **Refactor `MainWindow.xaml.cs`** :
   - Le code-behind ne contient plus que de la plomberie de View (focus, keyboard handlers WPF spécifiques)
   - `DataContext = ServiceLocator.Resolve<MainWindowViewModel>();` dans le ctor
   - Bindings dans `MainWindow.xaml` au lieu de mises à jour impératives

3. **Service `IDialogService`** pour les MessageBox / OpenFileDialog / ShowProgressWindow → encapsule la dépendance UI dans une interface mockable.

4. **Tests** : `RecoTool.Tests/ViewModels/MainWindowViewModelTests.cs` — 15+ tests Moq sur `IOfflineFirstService` + `IReconciliationService` + `IDialogService`.

### Critères de validation

- L'application se lance et fonctionne identiquement (test manuel)
- `MainWindow.xaml.cs` passe sous **300 LOC** (75% de réduction)
- Tests ViewModel verts en CI sans dispatcher WPF

### Risques

- **Régressions UI subtiles** : binding qui ne déclenche pas, focus mal géré. Mitigation : tests manuels exhaustifs + un test FlaUI pilote (Lot 9).

---

## Lot 3 — Généralisation MVVM

**Objectif** : appliquer le pattern du Lot 2 aux autres fenêtres.

### Ordre d'attaque (par ratio valeur/effort)

| Fenêtre | LOC code-behind | Logique métier | Priorité |
|---------|-----------------|----------------|----------|
| `HomePage` | 2 659 | Élevée (dashboard, KPIs) | **P1** |
| `ReconciliationPage` | 2 934 | Très élevée (rapprochement principal) | **P1** |
| `ReconciliationView` + 25 partials | 2 072 + 1 800 | Très élevée | **P1** |
| `ImportAmbreWindow` | 945 | Moyenne | P2 |
| `ReconciliationDetailWindow` | 1 507 | Moyenne | P2 |
| `RulesAdminWindow` | 588 | Faible | P3 |
| `DwingsButtonsWindow` | 561 | Faible | P3 |
| Autres dialogs (< 300 LOC) | ~3 000 cumulés | Faible | P3 |

### Stratégie

- **Big-bang strict interdit** : un VM par fenêtre, mergé indépendamment, déployable seul.
- Les partials `ReconciliationView/*.cs` sont en réalité déjà du code organisé par responsabilité — leur extraction en VMs spécialisés (`FilteringViewModel`, `EditingViewModel`, etc.) est plus facile que partir d'un `.xaml.cs` monolithique.

### Livrables par fenêtre

- 1 ViewModel + tests (10-30 par VM selon complexité)
- Code-behind réduit à < 30% de sa taille originale
- Comportement utilisateur identique (validation manuelle)

---

## Lot 4 — Abstractions sync

**Objectif** : isoler les dépendances externes de la sync derrière des interfaces.

### Livrables

1. **`INetworkPathProvider`** :
   ```csharp
   public interface INetworkPathProvider
   {
       string GetNetworkAmbreZipPath(string countryId);
       string GetNetworkReconciliationDbPath(string countryId);
       string GetReferentialNetworkPath();
       bool IsNetworkAvailable();
   }
   ```

2. **`IDistributedLock`** :
   ```csharp
   public interface IDistributedLock
   {
       Task<IAsyncDisposable> AcquireAsync(string scope, string ownerId, TimeSpan lease, CancellationToken ct);
       Task<bool> IsHeldByOtherAsync(string scope);
   }
   ```
   Remplace l'usage direct de fichier-lock dans `OfflineFirstService.Locks.cs`.

3. **`IChangeLog`** :
   ```csharp
   public interface IChangeLog
   {
       Task AppendAsync(string countryId, ChangeJournalEntry entry);
       Task<IList<ChangeJournalEntry>> ReadSinceAsync(string countryId, long sinceVersion);
       Task MarkAsSyncedAsync(string countryId, IEnumerable<long> entryIds);
   }
   ```

4. **`ISyncEngine`** (extraction de SyncOperations.cs) :
   ```csharp
   public interface ISyncEngine
   {
       Task<SyncResult> SynchronizeAsync(string countryId, SyncOptions opts, CancellationToken ct);
       Task PushLocalChangesAsync(string countryId);
       Task PullRemoteChangesAsync(string countryId, long sinceVersion);
   }
   ```

5. **Tests** : chaque interface a au moins une `Fake*` impl en mémoire dans le projet de tests.

---

## Lot 5 — Refactor `OfflineFirstService` derrière les abstractions

**Objectif** : `OfflineFirstService` devient un **assembleur** des composants ci-dessus, ne contenant plus de logique réseau directe.

### Plan

1. Injecter `IFileSystem`, `IClock`, `INetworkPathProvider`, `IDistributedLock`, `IChangeLog`, `ISyncEngine` dans le ctor.
2. Migrer méthode par méthode :
   - `CopyLocalToNetworkAsync` → délégué à `ISyncEngine`
   - `AcquireGlobalLockAsync` → `IDistributedLock`
   - `CleanupChangeLogAndCompactAsync` → `IChangeLog` + `IFileSystem`
   - `SynchronizeData` → `ISyncEngine`
   - `MarkAllLocalChangesAsSyncedAsync` → `IChangeLog`
   - etc.
3. La classe concrète `OfflineFirstService` passe de ~3 700 LOC à **~800-1 000 LOC** (state holder + façade).

### Critères de validation

- Tous les tests existants verts (526 tests)
- L'app fonctionne en réel (test manuel sur 1 country switch + 1 import + 1 push)
- `OfflineFirstService.SyncOperations.cs` passe à **< 200 LOC** ou disparaît (déplacé dans une impl de `ISyncEngine`)

### Risques

- **Très élevé** : c'est le lot le plus risqué. Mitigation :
  - Refactor *en parallèle* : créer `OfflineFirstServiceV2` qui délègue, garder l'ancien comme fallback
  - Feature flag pour basculer
  - 100% de couverture de l'ancienne API préservée (mêmes signatures)

---

## Lot 6 — Tests unitaires sync logic

**Objectif** : couvrir la logique de sync derrière les abstractions du Lot 4.

### Cibles

| Composant | Tests visés |
|-----------|-------------|
| `ISyncEngine` impl | 30-50 tests : pull/push/conflict resolution, optimistic concurrency, retry, partial failures |
| `IDistributedLock` impl | 15-20 tests : acquire/release, expiry, contention, owner mismatch |
| `IChangeLog` impl | 15-20 tests : append, read since, mark as synced, version monotonicity |
| `BackgroundSyncEngine` | 10-15 tests : scheduling, debounce, cancellation, error handling |

### Pattern

```csharp
public class SyncEngineTests
{
    private readonly Mock<IFileSystem> _fs = new();
    private readonly Mock<INetworkPathProvider> _net = new();
    private readonly Mock<IDistributedLock> _locks = new();
    private readonly Mock<IClock> _clock = new();
    private readonly Mock<IChangeLog> _changeLog = new();
    
    [Fact]
    public async Task Push_NoLocalChanges_NoOpAndReturnsZero()
    {
        _changeLog.Setup(x => x.ReadSinceAsync(It.IsAny<string>(), It.IsAny<long>()))
                  .ReturnsAsync(new List<ChangeJournalEntry>());
        var sut = new SyncEngine(_fs.Object, _net.Object, _locks.Object, _clock.Object, _changeLog.Object);
        var result = await sut.PushLocalChangesAsync("FR");
        result.PushedCount.Should().Be(0);
        _net.Verify(x => x.GetNetworkReconciliationDbPath("FR"), Times.Never);
    }
    // ... 30+ tests
}
```

---

## Lot 7 — Tests unitaires ViewModels

**Objectif** : couvrir tous les ViewModels créés aux Lots 2 et 3.

### Pattern

```csharp
public class ReconciliationViewModelTests
{
    private readonly Mock<IReconciliationService> _reco = new();
    private readonly Mock<IOfflineFirstService> _ofs = new();
    private readonly Mock<IDialogService> _dialog = new();
    
    [Fact]
    public async Task LoadCommand_LoadsRowsAndUpdatesIsBusy()
    {
        var vm = new ReconciliationViewModel(_reco.Object, _ofs.Object, _dialog.Object);
        _reco.Setup(x => x.GetAmbreDataAsync(It.IsAny<string>(), false))
             .ReturnsAsync(new List<DataAmbre> { new DataAmbre { ID = "X" } });
        
        vm.IsBusy.Should().BeFalse();
        await vm.LoadCommand.ExecuteAsync(null);
        vm.IsBusy.Should().BeFalse(); // reset à la fin
        vm.Rows.Should().HaveCount(1);
    }
}
```

### Estimations

- ~10-30 tests par ViewModel
- ~15 ViewModels totaux à terme
- **Cible : +200-300 tests ViewModels**

---

## Lot 8 — Tests d'intégration sync multi-instance

**Objectif** : valider la concurrence et la résolution de conflits entre 2+ instances de l'app.

### Setup

- Fixture `MultiInstanceSyncFixture` qui crée :
  - 1 BDD réseau partagée (Access)
  - 2 BDD locales par "instance" (`OFS_A`, `OFS_B`)
  - 2 instances `OfflineFirstService` avec `currentUser` distincts

### Scénarios

1. **Push concurrent** : A et B modifient la même ligne, push simultané → résolution par version
2. **Lock global** : A acquiert le lock import, B doit attendre / timeout
3. **Pull avec conflit** : A modifie ligne X locale, B push une version récente → A doit voir le conflit au pull
4. **Crash mid-sync** : simuler erreur réseau au milieu d'un push → cohérence du change log
5. **Lease expiry** : A acquiert le lock, crash, B peut acquérir après expiration

### Critères

- Tests skippables si Access non disponible (comme les autres intégration tests)
- Durée totale < 2 min pour l'ensemble du fichier
- 15-25 scénarios couvrant les paths critiques

---

## Lot 9 — Tests UI automatisés (FlaUI)

**Objectif** : couvrir 5-8 workflows critiques en UI réelle pour attraper les régressions de binding.

### Stack

- **FlaUI** (UI Automation .NET wrapper) — gratuit, OSS, ne nécessite pas Visual Studio
- Projet séparé `RecoTool.UITests` ciblant net48
- Lance `RecoTool.exe` dans un mode "test" (paramètre CLI ou variable d'env qui pointe vers une BDD seed-able)

### Workflows à couvrir

| # | Workflow | Pourquoi |
|---|----------|----------|
| 1 | Login + sélection country + landing sur HomePage | Smoke test app entière |
| 2 | Import AMBRE depuis fichier local + voir les nouvelles lignes | Pipeline d'import end-to-end |
| 3 | Édition d'une ligne (Action, KPI, Comments) + save | Persistance + audit user-edit |
| 4 | Linking via panier (PivotItems + ReceivableItems) | Logique métier UI critique |
| 5 | Filter + saved view round-trip | UserFilterService + UserViewPreferenceService UI |
| 6 | Dashboard KPIs visible et cohérent | Visualisation |
| 7 | Sync explicite (bouton refresh) → status mis à jour | Sync UI |
| 8 | Switch country mid-session | Path le plus fragile en pratique |

### Critères

- Durée totale < 5 min pour les 8 workflows
- Skippable en CI Linux (Windows requis)
- Capture d'écran systématique sur échec

---

## Anti-patterns à éviter

1. **"Refactor + tests dans le même PR"** sur >500 lignes — risque énorme de mélanger régression et écriture de tests. Toujours : refactor en 1 PR (sans changement comportemental, vérifié par les tests existants), tests dans une PR suivante.

2. **Tests UI qui dépendent de timing** (`Thread.Sleep(500)`) — utiliser les conditions explicites de FlaUI (`WaitFor.Element(...)`).

3. **Mocks trop bavards** — un test qui setup 10 mocks teste plus le mock framework que le code. Préférer des fakes hand-rolled pour les domaines complexes (`InMemoryChangeLog`, `InMemoryFileSystem`).

4. **Couverture comme objectif** — viser 100% sur le métier, **80% sur les ViewModels**, **30-40% sur la sync** (le reste est de la plomberie I/O testée en intégration).

---

## Métriques de succès

- **À la fin du Lot 5** : `OfflineFirstService` < 1 000 LOC, 100% des tests existants verts.
- **À la fin du Lot 7** : couverture ViewModels > 80%, tests rapides (< 30s pour la suite unitaire).
- **À la fin du Lot 9** : suite complète CI < 10 min, 0 test flaky en 100 runs consécutifs.
- **Suite globale** :
  - Aujourd'hui : **526 tests**, ~5s de runtime
  - Cible post-refactor : **~1 200-1 500 tests**, < 60s pour la suite unitaire + ViewModels, < 5 min pour intégration + UI

---

## Roadmap calendaire suggérée (10 semaines à 1 dev)

```
Sem 1     : Lot 0  (infra)        + Lot 1  (partials OFS)
Sem 2     : Lot 2  (MVVM pilote MainWindow) + Lot 4 démarrage (interfaces sync)
Sem 3-4   : Lot 4 finalisation    + Lot 5 démarrage refactor OFS
Sem 5     : Lot 5 finalisation    + Lot 6 (tests unitaires sync)
Sem 6-8   : Lot 3 (généralisation MVVM aux 14 autres fenêtres)
Sem 9     : Lot 7 (tests ViewModels) + Lot 8 (tests intégration multi-instance)
Sem 10    : Lot 9 (tests UI FlaUI) + nettoyage final + documentation
```

À 2 devs en parallèle (1 sur UI, 1 sur Sync) : **6-7 semaines**.

---

## Ce qui doit être validé AVANT de démarrer

1. **Décision sur le framework MVVM** : roll-your-own (RelayCommand maison comme dans le Lot 2) vs. CommunityToolkit.Mvvm (recommandé — auto-impl INPC, async commands, DI built-in).
2. **Décision sur le DI container** : Microsoft.Extensions.DependencyInjection (déjà partiellement utilisé dans `App.xaml.cs:117-122`) — confirmer.
3. **Branche de travail dédiée** : `refactor/ui-sync-testability` avec merges réguliers depuis `main` pour limiter la divergence.
4. **Plan de communication équipe** : prévenir des changements de structure UI, prévoir des phases de test utilisateur entre chaque lot UI.

---

## En résumé

Ce refactor n'est **pas négociable** si l'objectif est de tester l'UI et la sync de manière fiable. Mais il est :
- **Faisable de manière incrémentale** (chaque lot livre une valeur déployable)
- **Pas un big-bang** (lot 5 est le seul à risque élevé, isolé via feature flag)
- **Mesurable** (chaque lot a des critères de validation et un nombre de tests cible)

Démarrage recommandé : **Lot 0 + Lot 1 en parallèle**, validation, puis itérer.
