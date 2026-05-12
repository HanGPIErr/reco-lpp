# RecoTool.IntegrationTests

Tests d'intégration pour **RecoTool**.

## Différence avec `RecoTool.Tests`

- **`RecoTool.Tests`** = tests unitaires purs, isolés, qui tournent partout (CI Linux/Mac, machine sans Office, etc.).
- **`RecoTool.IntegrationTests`** = tests qui ouvrent une vraie BDD Access (`.accdb`) via Microsoft.ACE.OLEDB. Ils nécessitent **Windows** + **Microsoft Access Database Engine 2010/2016 (x64)** installé sur la machine.

## Pré-requis

1. **Windows x64** avec **.NET Framework 4.8 Developer Pack**.
2. **Microsoft Access Database Engine 2016 Redistributable (x64)** :
   <https://www.microsoft.com/en-us/download/details.aspx?id=54920>
   - Sans ça, tous les tests sont automatiquement **skippés** (pas d'échec).
3. **ADOX (DAO/ADO Ext)** disponible — fourni avec le moteur Access ci-dessus.

## Comment ça fonctionne

Chaque test crée une BDD Access **temporaire** dans `%TEMP%\RecoTool.IntegrationTests_<guid>.accdb` via la fixture `TempAccessDbFixture` (utilise ADOX `Catalog.Create` en COM late-binding pour éviter une dépendance hard sur `Microsoft.Office.Interop`).

La fixture est **partagée** par classe de test (`IClassFixture<>`) puis nettoyée à la fin (`IDisposable`).

Tous les tests utilisent `[SkippableFact]` (package `Xunit.SkippableFact`) avec un `Skip.IfNot(AccessAvailable.AnyAce, …)` au début. Donc :

- ACE installé → tests s'exécutent.
- ACE absent → tests **skippés** avec un message clair (et le build CI reste vert).

## Périmètre actuel

| Fichier de test                                       | Couverture                                                                          |
| ----------------------------------------------------- | ----------------------------------------------------------------------------------- |
| `DataAccess/OleDbConnectionTests.cs`                  | Ouverture de connexion sur `.accdb` créé à la volée + résolution `DbConn`           |
| `DataAccess/OleDbUtilsIntegrationTests.cs`            | `GetMaxVersionAsync`, `GetMaxLastModifiedAsync`, `OpenWithTimeoutAsync`             |
| `Services/BasicQueryRoundtripTests.cs`                | Smoke test CREATE TABLE / INSERT / SELECT avec types AMBRE                          |
| `Services/UserFilterServiceIntegrationTests.cs`       | `UserFilterService` Save/Load/List sur T_Ref_User_Filter                            |
| `Services/UserTodoListServiceIntegrationTests.cs`     | `UserTodoListService` Ensure/Upsert/List/Delete sur T_Ref_TodoList                  |
| `Services/UserViewPreferenceServiceIntegrationTests.cs` | `UserViewPreferenceService` Insert/Update/GetAll sur T_Ref_User_Fields_Preference  |

## Étendre

Pour ajouter des tests d'intégration sur un service métier (par exemple `LookupService`) :

1. Construisez le schéma minimal nécessaire dans `TempAccessDbFixture.ExecuteNonQuery(...)`.
2. Insérez les données fixture.
3. Instanciez le service avec un mock léger d'`OfflineFirstService` qui renvoie le chemin de la fixture en guise de "local AMBRE path".
4. Appelez la méthode publique et vérifiez le résultat.

Exemple de squelette :

```csharp
public class LookupServiceIntegrationTests : IClassFixture<TempAccessDbFixture>
{
    [SkippableFact]
    public async Task GetCurrenciesAsync_ReturnsDistinctCurrencies()
    {
        Skip.IfNot(AccessAvailable.AnyAce, AccessAvailable.SkipReasonOrNull);
        Skip.IfNot(_fx.Created, "Fixture not ready");

        _fx.ExecuteNonQuery("CREATE TABLE T_Data_Ambre (CCY TEXT(3), DeleteDate DATETIME)");
        _fx.ExecuteNonQuery("INSERT INTO T_Data_Ambre VALUES ('EUR', NULL)");
        _fx.ExecuteNonQuery("INSERT INTO T_Data_Ambre VALUES ('USD', NULL)");
        _fx.ExecuteNonQuery("INSERT INTO T_Data_Ambre VALUES ('EUR', NULL)");

        // ... mock OfflineFirstService.GetLocalAmbreDatabasePath("FR") -> _fx.Path
        // ... assert que GetCurrenciesAsync("FR") renvoie ["EUR","USD"] (distinct, trié)
    }
}
```

> **Pourquoi ces tests ne sont pas dans `RecoTool.Tests` ?**
> Parce qu'ils touchent du COM/Win32 (ACE, ADOX) et ne peuvent pas tourner sur tous les runners CI. Le projet d'intégration est isolé pour rester optionnel.
