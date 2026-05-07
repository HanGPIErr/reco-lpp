# Migration .NET Framework 4.8 → .NET 8 — Guide de finalisation

> Cette migration a été appliquée par tooling. Avant de compiler, suivre les étapes ci-dessous.

## Étape 1 — Nettoyer l'ancien build (CRITIQUE)

Les répertoires `bin/` et `obj/` contiennent des artefacts .NET Framework 4.8 (notamment
`obj/Debug/.NETFramework,Version=v4.8.AssemblyAttributes.cs`) qui généreraient des erreurs
"duplicate attribute" si le SDK les inclut.

```powershell
# Depuis la racine de la solution
Remove-Item -Recurse -Force .\RecoTool\bin, .\RecoTool\obj, .\OfflineFirstAccess\bin, .\OfflineFirstAccess\obj
```

## Étape 2 — Vérifier le SDK installé

```powershell
dotnet --list-sdks
```

Tu dois voir au moins `8.0.x`. Sinon : <https://dotnet.microsoft.com/download/dotnet/8.0>
(prendre le **SDK**, pas le runtime seul).

## Étape 3 — Restaurer les paquets

```powershell
dotnet restore RecoTool.sln
```

Attendu :
- `OfflineFirstAccess` restaure `System.Data.OleDb 9.0.0`
- `RecoTool` restaure tous les Microsoft.Extensions.* + LiveCharts.Wpf 0.9.7 (avec un
  warning **NU1701** que j'ai déjà mis en `NoWarn` — c'est normal).

## Étape 4 — Build

```powershell
dotnet build RecoTool.sln -c Debug
```

**Erreurs attendues / faciles à corriger :**

| Erreur probable | Fichier | Fix |
|------------------|---------|-----|
| `error CS0103: 'ApplicationDeployment' does not exist` | un fichier oublié | Idem que MainWindow.xaml.cs : `using System.Deployment.Application;` à retirer + utiliser `Assembly.GetExecutingAssembly().GetName().Version` |
| `error CS0234: 'System.Web' does not exist` | un autre fichier qui contiendrait l'usage | Retirer `using System.Web;` (vérifié déjà : pas d'autres usages réels) |
| `error CS0246: 'PrimaryInteropAssemblies'...` (DAO/Access) | Reference Office Interop | La PIA Microsoft.Office.Interop.Access est installée par Office ; si erreur, supprimer la `<COMReference>` Access et/ou installer "Office Developer Tools" |
| Warning `NU1701` LiveCharts.Wpf | LiveCharts | Déjà supprimé via `<NoWarn>` |

**Erreurs runtime potentielles :**

1. **LiveCharts crash au premier UseControl HomePage** — si ça arrive, Phase 2 obligatoire :
   migration vers LiveCharts2 (référence : <https://livecharts.dev>) ou OxyPlot.Wpf.
   Le code des charts est concentré dans `Windows/HomePage.xaml.cs:1796-1809` et `HomePage.xaml`.

2. **`Encoding 'Windows-1252' not supported`** — si tu importes des CSV anciens encodages.
   Fix : dans App.OnStartup, ajouter `Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);`
   (n'a pas été détecté dans le code mais peut surgir).

3. **`COMException 0x80040154` au démarrage** — la PIA Office n'est pas dans le GAC ou
   Office n'est pas installé sur le poste de test. Solution : installer Office, ou
   désactiver `EmbedInteropTypes=true` pour debug.

4. **`SqlException: ... access permission denied` sur Access DB** — peu probable mais
   sur certains domaines, le provider OleDb 9.0.x demande la prov ACE 64-bit.
   Vérifier que `Microsoft.ACE.OLEDB.16.0` (ou `12.0`) est installé.

## Étape 5 — Tester l'app

Démarrer (F5 dans Visual Studio, ou `dotnet run --project RecoTool`).

Checklist sanity :
- [ ] Splash + chargement initial → Home s'affiche
- [ ] Sélection pays → KPIs et charts mis à jour
- [ ] Clic ToDo → ouvre la ReconciliationPage
- [ ] Add View → ReconciliationView s'ouvre, données chargées
- [ ] Scroll fluide
- [ ] Édition cellule (Action / KPI / Comments) → sauvegarde OK
- [ ] Linking basket → fonctionne
- [ ] Retour sur Home → instantané (le QW-1 anti-refresh)
- [ ] Import Ambre (si tu peux tester) → OLE DB OK
- [ ] Outlook "Report Missing Invoices" → COM Excel/Outlook OK

## Étape 6 — Mesurer les gains

Comparer avant/après avec les logs `LogPerf` que tu as déjà.
Cibles :
- Démarrage à froid : −30 à −40 %
- Stutters scroll : pratiquement éliminés (GC pauses /2-3)
- Hot-loops après warmup : −20 à −30 %

## ROLLBACK (si ça casse)

Les anciens .csproj sont sauvegardés en `.csproj.netfx48.bak`. Pour rollback :

```powershell
# Depuis la racine
Move-Item -Force .\RecoTool\RecoTool.csproj.netfx48.bak .\RecoTool\RecoTool.csproj
Move-Item -Force .\OfflineFirstAccess\OfflineFirstAccess.csproj.netfx48.bak .\OfflineFirstAccess\OfflineFirstAccess.csproj

# Et reverter les 3 fichiers code modifiés via git :
git checkout RecoTool/App.xaml.cs RecoTool/App.config RecoTool/API/FREEData.cs RecoTool/Windows/MainWindow.xaml.cs
```

## Phase 2 — Si LiveCharts pose problème

Plan B au cas où LiveCharts.Wpf 0.9.7 ne survive pas .NET 8 à l'exécution :

### Option A — LiveCharts2 (effort : 2-3 j)
- Remplacer le NuGet `LiveCharts.Wpf` 0.9.7 par `LiveChartsCore.SkiaSharpView.WPF` 2.x
- Renommer le namespace : `LiveCharts.Wpf` → `LiveChartsCore.SkiaSharpView.WPF`
- API similaire (`SeriesCollection`, `ChartValues`) mais quelques renommages
- Le rendu SkiaSharp est plus rapide que WPF natif sur les charts denses

### Option B — OxyPlot (effort : 4-5 j)
- `OxyPlot.Wpf` NuGet
- API totalement différente (`PlotModel`, `LineSeries`, etc.)
- Plus mainstream, plus stable, mais ré-écriture des 9 charts

**Recommandation** : tenter LiveCharts2 d'abord. Si HomePage rend correctement, fini.
