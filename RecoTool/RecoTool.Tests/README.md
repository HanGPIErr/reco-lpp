# RecoTool.Tests

Projet de tests unitaires pour la solution **RecoTool**.

> Pour les tests d'intégration (qui nécessitent Microsoft Access Database Engine), voir le projet jumeau **`RecoTool.IntegrationTests`**.

## Stack

- Framework cible : `net48` (identique au projet principal RecoTool, qui est WPF .NET Framework 4.8)
- Test framework : **xUnit** (`xunit` 2.9, `xunit.runner.visualstudio` 2.8)
- Assertions : **FluentAssertions** 6.12
- Mocking : **Moq** 4.20
- Couverture : **coverlet.collector** 6.0

## Périmètre couvert

Tests unitaires purs, sans dépendance DB / réseau / WPF runtime.

### Helpers

| Fichier source                                   | Fichier de tests                            |
| ------------------------------------------------ | ------------------------------------------- |
| `Helpers/DataConversionHelper.cs`                | `Helpers/DataConversionHelperTests.cs`      |
| `Helpers/MultiUserHelper.cs`                     | `Helpers/MultiUserHelperTests.cs`           |
| `Helpers/DwingsLinkingHelper.cs` (+ `DwingsInvoiceLookup`) | `Helpers/DwingsLinkingHelperTests.cs` |

### Domain / Filters

| Fichier source                                   | Fichier de tests                                     |
| ------------------------------------------------ | ---------------------------------------------------- |
| `Domain/Filters/FilterBuilder.cs`                | `Domain/Filters/FilterBuilderTests.cs`               |
| `Domain/Filters/FilterSqlHelper.cs`              | `Domain/Filters/FilterSqlHelperTests.cs`             |

### Models

| Fichier source                                   | Fichier de tests                            |
| ------------------------------------------------ | ------------------------------------------- |
| `Models/BaseEntity.cs`                           | `Models/BaseEntityTests.cs`                 |
| `Models/DataAmbre.cs`                            | `Models/DataAmbreTests.cs`                  |
| `Models/Reconciliation.cs`                       | `Models/ReconciliationTests.cs`             |
| `Models/ImportChanges.cs`                        | `Models/ImportChangesTests.cs`              |
| `Models/DWINGSModels.cs`                         | `Models/DwingsModelsTests.cs`               |

### Services (purs)

| Fichier source                                   | Fichier de tests                                     |
| ------------------------------------------------ | ---------------------------------------------------- |
| `Services/BackgroundTaskQueue.cs`                | `Services/BackgroundTaskQueueTests.cs`               |
| `Services/LinkingBasket.cs`                      | `Services/LinkingBasketTests.cs`                     |
| `Services/OptionsService.cs`                     | `Services/OptionsServiceTests.cs`                    |
| `Services/ThemeManager.cs`                       | `Services/ThemeManagerTests.cs`                      |
| `Services/TransformationService.cs`              | `Services/TransformationServiceTests.cs`             |
| `Services/Cache/CacheService.cs`                 | `Services/Cache/CacheServiceTests.cs`                |
| `Services/Helpers/AccountSideCalculator.cs`      | `Services/Helpers/AccountSideCalculatorTests.cs`     |
| `Services/Helpers/DwingsDateHelper.cs`           | `Services/Helpers/DwingsDateHelperTests.cs`          |
| `Services/Helpers/EnumHelper.cs`                 | `Services/Helpers/EnumHelperTests.cs`                |
| `Services/Helpers/ValidationHelper.cs`           | `Services/Helpers/ValidationHelperTests.cs`          |
| `Services/Helpers/ReconciliationKpiCalculator.cs`| `Services/Helpers/ReconciliationKpiCalculatorTests.cs` |
| `Services/Helpers/ReconciliationViewEnricher.cs` | `Services/Helpers/ReconciliationViewEnricherTests.cs` |
| `Services/Policies/SyncPolicy.cs`                | `Services/Policies/SyncPolicyTests.cs`               |
| `Services/Queries/ReconciliationViewQueryBuilder.cs` (internal) | `Services/Queries/ReconciliationViewQueryBuilderTests.cs` (via reflection) |
| `Services/Rules/RuleProposal.cs`                 | `Services/Rules/RuleProposalTests.cs`                |
| `Services/Rules/TruthRule.cs`                    | `Services/Rules/TruthRuleTests.cs`                   |

### Services post-refactor (DI via interfaces)

Deux interfaces ont été extraites pour ouvrir la testabilité :

#### `IOfflineFirstService` (`Services/IOfflineFirstService.cs`)

Surface minimale (**9 membres**) :

- `string GetLocalAmbreDatabasePath(string countryId = null)`
- `string GetLocalDWDatabasePath(string countryId = null)`
- `string GetAmbreConnectionString(string countryId = null)`
- `string GetCountryConnectionString(string countryId)`
- `string ReferentialConnectionString { get; }`
- `List<UserField> UserFields { get; }`
- `string CurrentCountryId { get; }`
- `Country CurrentCountry { get; }`
- `Task SetSyncStatusAsync(string status, CancellationToken token = default)`

#### `IReconciliationService` (`Services/IReconciliationService.cs`)

Surface minimale (**6 membres**) :

- `string CurrentUser { get; }`
- `Task<List<DataAmbre>> GetAmbreDataAsync(string countryId, bool includeDeleted = false)`
- `Task<Reconciliation> GetOrCreateReconciliationAsync(string id)`
- `Task<bool> SaveReconciliationsAsync(IEnumerable<Reconciliation>, bool applyRulesOnEdit = true)`
- `Task<IReadOnlyList<DwingsInvoiceDto>> GetDwingsInvoicesAsync()`
- `Task<IReadOnlyList<DwingsGuaranteeDto>> GetDwingsGuaranteesAsync()`

#### `IDwingsService` (`Services/IDwingsService.cs`)

Surface minimale (**4 membres**) :

- `Task PrimeCachesAsync()`
- `void InvalidateCaches()`
- `Task<IReadOnlyList<DwingsInvoiceDto>> GetInvoicesAsync()`
- `Task<IReadOnlyList<DwingsGuaranteeDto>> GetGuaranteesAsync()`

Les méthodes statiques (`LoadFromPathAsync`, `InvalidateSharedCacheForPath`) restent statiques — pas d'état à mocker.

Les classes concrètes implémentent leurs interfaces respectives (zéro changement comportemental).

#### Services migrés vers les interfaces

| Service                          | Dépend de                              | Fichier de tests                                              |
| -------------------------------- | -------------------------------------- | ------------------------------------------------------------- |
| `LookupService`                  | `IOfflineFirstService`                 | `Services/LookupServiceTests.cs` *(Moq)*                     |
| `ReferentialService`             | `IOfflineFirstService`                 | `Services/ReferentialServiceTests.cs` *(Moq)*                |
| `DwingsService`                  | `IOfflineFirstService` (impl. `IDwingsService`) | `Services/DwingsServiceTests.cs` *(Moq)*                     |
| `ReconciliationMatchingService`  | `IReconciliationService` + `IOfflineFirstService` | `Services/ReconciliationMatchingServiceTests.cs` *(Moq, métier complet)* |
| `ReconciliationService` (champ `_dwingsService`) | `IDwingsService`                  | *(testé indirectement via `IReconciliationService`)* |
| `Rules/TruthTableRepository`     | `IOfflineFirstService`                 | *(à étendre)*                                                 |
| `Rules/RuleContextBuilder`       | `IReconciliationService` + `IOfflineFirstService` | `Services/Rules/RuleContextBuilderTests.cs` *(Moq, métier complet + seam loader)* |
| `Rules/RulesEngine`              | `IOfflineFirstService`                 | `Services/Rules/RulesEngineTests.cs` *(seed cache via `__TestSeedRules`)* |
| `Rules/RuleApplicationHelper`    | (statique, pure)                       | `Services/Rules/RuleApplicationHelperTests.cs` *(idempotence, lock user, output projection)* |
| `Ambre/AmbreImportValidator`     | (POCO, pure)                           | `Services/Ambre/AmbreImportValidatorTests.cs`                |
| `Ambre/AmbreDataProcessor`       | `IOfflineFirstService`                 | `Services/Ambre/AmbreDataProcessorTests.cs` *(Moq + filtre)*  |
| `Ambre/AmbreConfigurationLoader` | `IOfflineFirstService`                 | `Services/Ambre/AmbreConfigurationLoaderTests.cs` *(Moq + parse ATC tags)* |
| `UserFilterService`              | `string` connection string             | `Services/UserFilterServiceTests.cs` *(SanitizeWhereClause pure)* |
| `UserFieldUpdateService`         | (statique, pure)                       | `Services/UserFieldUpdateServiceTests.cs`                    |
| `ViewDataEnricher`               | (statique avec caches)                 | `Services/ViewDataEnricherTests.cs`                          |
| `ParameterService`               | `string` (déjà loosely coupled)        | `Services/ParameterServiceTests.cs`                          |

> **Sites d'appel non impactés** : tous les call sites passaient déjà un `OfflineFirstService` ou `ReconciliationService` concret, qui se cast implicitement vers les nouvelles interfaces. Aucune modification UI / wiring requise.

### Infrastructure & Utils

| Fichier source                                   | Fichier de tests                            |
| ------------------------------------------------ | ------------------------------------------- |
| `Infrastructure/DataAccess/DbConn.cs`            | `Infrastructure/DbConnTests.cs`             |
| `Infrastructure/Logging/LogHelper.cs`            | `Infrastructure/LogHelperTests.cs`          |
| `Utils/RequestCache.cs`                          | `Utils/RequestCacheTests.cs`                |

## Hors périmètre des tests unitaires

Les composants ci-dessous nécessitent **une infrastructure d'intégration** plutôt que des tests unitaires purs (voir `RecoTool.IntegrationTests`) :

- `Services/Ambre/*` (Import AMBRE complet : Excel + DB + transformations)
- `Services/Sync/*`, `Services/OfflineFirst/*` (réplication réseau + verrous)
- `Services/Snapshots/*` (lecture/écriture KPI snapshots en DB)
- `Services/External/*` (API externes)
- `Services/Rules/RulesEngine.cs`, `RuleProposalRepository.cs`, `RulesDiagnosticsService.cs` (DB-dependent)
- `Services/ReconciliationService*.cs` (orchestrateur monolithique fortement couplé à OleDb — **prochaine cible de refactor : extraire `IReconciliationService`**)
- `Services/ReferentialCacheService.cs` (requêtes OleDb)
- `Services/DashboardExportService.cs`, `KpiSnapshotService.cs`, `DatabaseRecreationService.cs`, `SyncMonitorService.cs` (DB / fichiers / network)
- `Services/TodoListSessionTracker.cs`, `User*Service.cs` (DB — déjà loosely coupled via `string connString` ; testables avec une fixture Access)
- `Services/ViewDataEnricher.cs` (DB enrichment)
- `Services/Helpers/ExternalReferenceHelper.cs` (DAO COM)
- `Domain/Repositories/IReconciliationRepository.cs` (interface — testée via implémentation concrète qui est DB)
- `Infrastructure/IO/ExcelHelper.cs` (lecture Excel)
- `Infrastructure/DataAccess/OleDbQueryExecutor.cs`, `OleDbSchemaHelper.cs`, `OleDbUtils.cs`, `ReferentialConnectionPool.cs` (OleDb)
- `Infrastructure/DI/ServiceLocator.cs` (résolution runtime)
- Code-behind WPF (`UI/`, `Windows/`, `App.xaml.cs`)

Pour ces zones, voir :
1. **`RecoTool.IntegrationTests`** pour les tests qui s'appuient sur une vraie BDD Access (smoke tests OleDb + utils déjà inclus).
2. Une refacto vers des **interfaces injectables** (`IOfflineFirstService`, `IReconciliationRepository`, `IDwingsService`…) permettrait de couvrir davantage de logique métier en pur unitaire.

## Exécution

### Visual Studio

1. Ouvrir la solution.
2. Ajouter `RecoTool.Tests/RecoTool.Tests.csproj` à la solution (clic droit sur la solution → Add → Existing Project).
3. **Test Explorer** (Ctrl+E, T) → Run All.

### Ligne de commande

```powershell
# Restaurer + compiler + lancer
dotnet test RecoTool.Tests/RecoTool.Tests.csproj -c Debug

# Avec couverture
dotnet test RecoTool.Tests/RecoTool.Tests.csproj --collect:"XPlat Code Coverage"
```

> **Note** : le projet cible `net48` ; il faut donc le SDK .NET Framework 4.8 dev tools (Build Tools ou Visual Studio 2022) installés.

## Conventions

- **Un fichier de test par classe** sous test (mirroir de l'arborescence du projet principal).
- Nommage : `MethodeTestée_Condition_RésultatAttendu`.
- `FluentAssertions` partout (`.Should().Be(...)`) pour des messages d'échec lisibles.
- `[Theory]` + `[InlineData]` pour les cas paramétrés.
- Pas de mock de filesystem ni de DB — uniquement de la logique pure ou des dépendances optionnelles.
- Les tests qui rendent visible un défaut connu de la production (ex. faiblesse de la garde anti-injection dans `FilterSqlHelper.ExtractNormalizedPredicate`) sont marqués comme tests de **régression documentaire** et commentés.

## Évolution

**Étapes déjà réalisées** :

1. Extraction d'`IOfflineFirstService` (**14 membres**) → 9+ services migrés + tests Moq ajoutés.
2. Extraction d'`IReconciliationService` (6 membres) → `ReconciliationMatchingService` et `RuleContextBuilder` migrés ; tests métier complets sur `PerformAutomaticMatchingAsync` et `ApplyManualOutgoingRuleAsync`.
3. Extraction d'`IDwingsService` (4 membres) → `DwingsService` y conforme + champ `_dwingsService` de `ReconciliationService` typé via l'interface.
4. Refactor `RulesEngine` → accepte `IOfflineFirstService` + ajout du seam `__TestSeedRules` (visible uniquement via `InternalsVisibleTo("RecoTool.Tests")`).
5. Refactor `RuleContextBuilder` → seam `__SetRelatedLinesLoaderForTesting` permet d'injecter un loader fake en remplacement de `LoadRelatedLinesAsync`.
6. Migration `AmbreDataProcessor` et `AmbreConfigurationLoader` vers `IOfflineFirstService` (extension de l'interface avec `SetSyncStatusAsync` + 5 lookups référentiels).
7. Tests métier complets sur `RulesEngine`, `RuleContextBuilder`, `RuleApplicationHelper`, `AmbreImportValidator`, `AmbreDataProcessor`, `AmbreConfigurationLoader`, `UserFieldUpdateService`, `ViewDataEnricher`.
8. Tests d'intégration ajoutés sur `UserFilterService`, `UserTodoListService`, `UserViewPreferenceService` via fixture Access.

**Prochaines étapes recommandées** (dans l'ordre de rentabilité) :

1. **`SnapshotComparisonService`** — possiblement testable, à explorer (genère des diffs entre snapshots, devrait être pure logique).
2. **`AmbreReconciliationUpdater.Rules.cs`** — application des règles à l'import, partiellement pure.
3. **`Rules/RuleProposalRepository`** — CRUD sur `T_RuleProposals`, intégration via fixture Access.
4. Pour les services restants couplés à OFS via 15+ méthodes (`AmbreDatabaseSynchronizer`, `KpiSnapshotService`, `SnapshotService`) : interfacer ferait ballonner l'API OFS au-delà du raisonnable. Stratégie alternative : **fake d'OFS hand-rolled** (classe concrète qui implémente `IOfflineFirstService` étendue avec un héritage local au projet de tests), ou tests d'intégration end-to-end.

Pour étendre la couverture aux autres services dépendants :

1. Extraire des **interfaces** (`IDwingsService`, `IReferentialService`…) depuis les classes existantes.
2. Modifier les constructeurs pour accepter ces interfaces (injection de dépendances).
3. Ajouter de nouvelles classes de tests utilisant **Moq** pour stubber ces dépendances.

Exemple :

```csharp
var mock = new Mock<IOfflineFirstService>();
mock.Setup(x => x.GetLocalAmbreDatabasePath("FR"))
    .Returns("C:\\fake\\path.accdb");

var sut = new LookupService(mock.Object);
// ...
```

Les zones les plus rentables à interfacer en priorité (forte logique métier, peu de DB pur) sont :

- ✅ ~~`OfflineFirstService` → `IOfflineFirstService` (fait)~~
- ✅ ~~`ReconciliationService` → `IReconciliationService` (fait)~~
- ✅ ~~`DwingsService` → `IDwingsService` (fait)~~
- `IReconciliationRepository` est déjà une interface — il "suffit" de fournir une implémentation in-memory et de l'utiliser dans les tests.
- `RulesEngine` (à interfacer pour rendre testable l'application des règles)
