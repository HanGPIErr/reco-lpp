# RecoTool — Propositions d'évolution

> Note : ce document complète `PERF_AUDIT.md`. Il liste des fonctionnalités, intégrations
> et chantiers techniques au-delà des Quick Wins de performance déjà appliqués.
> Échelle : ★ = fort impact métier · ◆ = intéressant · ◇ = exploratoire.

---

## 1. Productivité utilisateur (gros levier court terme)

### ★ 1.1 Coller-pour-rechercher (clipboard → highlight)
Les utilisateurs reçoivent souvent des listes de BGI / IDs par email. Permettre :
- `Ctrl+Shift+V` dans la `ReconciliationView` ouvre un dialog "Coller des références".
- Détection auto du type (BGI, BGPMT, Invoice ID, Reconciliation_Num) par regex.
- Filtre la grille pour ne laisser que les lignes correspondantes (avec un compteur "X/Y trouvées, Z manquantes" + bouton "Copier les manquantes").
- Highlight visuel pendant 5 s pour les nouvelles lignes filtrées.

**Effort** : 1-2 j. Réutilise `_quickSearchTerm` + `ApplyQuickSearch`.

### ★ 1.2 Édition par lots avec preview
Aujourd'hui il y a `LinkingBasket` + actions par menu contextuel. Manque :
- Un dialog "Bulk Edit" : sélectionner N lignes → choisir Action/KPI/IncidentType/Assignee à appliquer → **prévisualisation** ("23 rows will get Action=Trigger, 5 will overwrite an existing Action") → confirm.
- Mode "additif" vs "écrasant" (préserver les valeurs déjà saisies par défaut).
- Génération d'un commentaire automatique : "[Bulk edit by gianni - 2026-05-07] Action set to Trigger".

**Effort** : 2 j. Branche sur `UserFieldUpdateService` + `ScheduleBulkPushDebounced`.

### ★ 1.3 Vue "Quoi de neuf depuis ma dernière visite"
Le DTO porte déjà `IsNewlyAdded` (CreationDate=today) et `IsUpdated` (snapshot diff).
- Ajouter un bouton "Recent activity" qui filtre instantanément sur ces deux flags.
- Persister `LastSeenTimestamp` par utilisateur côté `UserViewPreferenceService`.
- Compteur badge sur le bouton Home : "12 changements depuis ta dernière visite".

**Effort** : 1 j.

### ◆ 1.4 Annotations / sticky notes par ligne
Distinct du champ Comments (chronologique). Une note libre par utilisateur, visible en hover, persistée dans une nouvelle table `T_RowAnnotations(UserId, RecoId, Note, Color, UpdatedAt)`.
- Couleur cliquable comme post-it.
- Sert à "se laisser un mot à soi-même" sans polluer le fil de comments partagé.

**Effort** : 1 j.

### ◆ 1.5 Palette de commandes (Ctrl+K)
Inspiré VS Code / Linear : `Ctrl+K` ouvre un input flottant qui propose des actions :
- "Open ToDo: …", "Apply filter: …", "Switch country: …", "Set Action on selection: …"
- Auto-complétion sur les Saved Filters, Saved Views, ToDoLists.
- Permet à un utilisateur clavier-only d'aller 3× plus vite.

**Effort** : 2 j (couche de recherche + dispatch d'actions).

### ◆ 1.6 Undo/Redo local sur édits
Les utilisateurs hésitent à faire des bulk edits car "et si je me trompe ?".
- Stack d'`ICommand`/`IUndoableAction` côté `ReconciliationView` ; `Ctrl+Z` / `Ctrl+Shift+Z`.
- 50 derniers édits gardés en mémoire.
- Toast "1 edit undone — Ctrl+Y to redo".

**Effort** : 2 j (le plus dur est la sérialisation des états avant/après).

---

## 2. Collaboration & multi-utilisateurs

### ★ 2.1 Centre de notifications in-app
Aujourd'hui : toasts éphémères. Manque un historique consultable.
- Drawer latéral cliquable avec :
  - Mentions reçues (`@me` dans des commentaires).
  - Actions de règles auto qui ont touché vos lignes assignées.
  - Synchros qui ont apporté ≥ N changements.
  - Conflits multi-user (lecture/édition simultanée).
- Marquer comme lu / aller à la ligne en un clic.

**Effort** : 3 j. Réutilise les events `SyncPulledChanges`, `Mentions.cs`, `_todoSessionTracker`.

### ★ 2.2 Partage de Saved Filters / Saved Views
Les `UserFilter` et `UserViewPreset` sont per-user. Permettre :
- Marquer une vue comme "Shared" → visible (en lecture seule) par tous les utilisateurs du même pays.
- Le créateur peut autoriser ou non la duplication ("Copier dans mes vues").
- Petit indicateur "👥 partagé par Marie" en face du nom.

**Effort** : 1-2 j (ajouter colonne `IsShared` + scope dans `UserViewPreferenceService`).

### ◆ 2.3 Affectation/handoff explicite
- Bouton "Reassign to…" sur sélection.
- Génère commentaire auto + notifie le destinataire (notif center).
- Optionnel : avec deadline "à clôturer avant…".

**Effort** : 1 j.

### ◆ 2.4 Activity feed par ToDoList
La `ToDoList` est centrale dans le workflow. Une vue "Activity" :
- Timeline des édits + commentaires des dernières 48 h pour cette liste.
- Filtrable par utilisateur.
- Aide aux passations / debriefs d'équipe.

**Effort** : 2 j si la table `T_ChangeLog` existe déjà ; 3-4 j sinon.

---

## 3. Reporting & analytics

### ★ 3.1 Tableau de bord par correspondant
Vous avez déjà `Report Missing Invoices` (export Outlook). En extension :
- Page "Correspondents" listant chaque `G_NAME1` / `G_PARTY_ID` avec :
  - Nb d'invoices ouvertes
  - Montant en suspens
  - Délai moyen de réponse (basé sur dates de claim/délétions historiques)
  - Heatmap mensuelle (intensité = nombre d'incidents)
- Bouton "Send recap email" pré-rempli (HTML + table).

**Effort** : 3-4 j. Réutilise `DashboardAnalyticsService` + `KpiSnapshotService`.

### ◆ 3.2 Rapports planifiés
- "Envoie-moi le snapshot dashboard tous les lundis à 8 h" → CSV ou PDF en pièce jointe Outlook.
- Storage du planning : tâche Windows Scheduler créée par l'app + petit runner CLI léger.

**Effort** : 3 j. Une `Schedule` est déjà disponible côté Cowork (référence skill), mais pour une vraie persistance c'est Windows Task Scheduler.

### ◆ 3.3 Vue Sankey "flow des statuts"
- Diagramme Sankey : combien de rows passent de "Not Linked" → "To Review" → "Reviewed" sur 30 j.
- Met en évidence les goulots du processus.

**Effort** : 2 j (LiveCharts ne fait pas Sankey natif → utiliser ScottPlot ou OxyPlot).

### ◆ 3.4 Détection d'anomalies
Sur les snapshots quotidiens :
- Row dont l'`Amount` s'écarte de 3σ de la moyenne par CCY/Account.
- Spike soudain du nombre de "NotLinked" pour un correspondant donné.
- Alerte affichée dans la HomePage (bandeau).

**Effort** : 2-3 j (statistique simple en C#).

### ◇ 3.5 Cohort analysis : time-to-resolution
- Pour chaque "cohorte" (semaine de création), durée médiane jusqu'à `DeleteDate`.
- Permet de mesurer l'impact concret des process changes.

**Effort** : 2 j.

---

## 4. Qualité de données / smart features

### ★ 4.1 Suggestions de matching automatique (sans LLM)
Pour les rows `Not Linked` :
- Score de similarité avec les invoices DWINGS sur (montant ±tolerance, devise, fenêtre de date, party name fuzzy).
- Top 3 candidats proposés dans un panneau latéral "Suggestions" sur la ligne sélectionnée.
- Click = link instant. Apprentissage léger : tracker le taux d'acceptation par règle de scoring.

**Effort** : 3-4 j. La similarité Levenshtein sur party name + numérique est suffisant ; pas besoin d'IA.

### ★ 4.2 Détection de doublons
Aujourd'hui il y a un flag `PotentialDuplicates`. Le porter à un vrai outil :
- Vue "Duplicates Inspector" : groupes proposés (par fingerprint = round amount + date ± 5 j + currency + party).
- Action "Merge / dismiss / mark as related".
- Audit trail des merges.

**Effort** : 3 j.

### ◆ 4.3 Validation rules engine étendu
`ReconciliationRules.cs` existe déjà. Manque :
- UI graphique pour qu'un power-user crée des règles "if X then Y" sans toucher au code.
- Mode "Simulation" (preview ce qui changerait sans appliquer).
- Versioning des règles + historique d'application.

**Effort** : 4-5 j (UI builder de prédicat).

### ◇ 4.4 IA / LLM (côté local ou Azure)
Si l'environnement le permet, intégration LLM (Azure OpenAI ou Claude API) pour :
- **Filtrage en langage naturel** : "show me unmatched receivables over 10 k EUR from last month assigned to Marie" → traduit en `FilterState`.
- **Résumé de fil de commentaires** sur les rows à long historique.
- **Suggestion d'Action/KPI** prenant en compte commentaires + label + montant + correspondant.
Demande validation infosec/conformité (envoi de données au LLM).

**Effort** : 3-5 j (gros sur l'aspect compliance, pas le code).

---

## 5. Intégrations

### ★ 5.1 Drag-and-drop d'email Outlook → ligne
- Glisser un mail Outlook sur une ligne de la grille → l'attache (stockage : path UNC ou base64 dans `T_EmailAttachments`).
- Visible via une icône 📎 et clic = ouvrir le `.msg`.
- Commentaire auto : "Email attached: subject - from - date".

**Effort** : 2 j (Outlook drag-source MAPI / `FileGroupDescriptor`).

### ◆ 5.2 "Send to correspondent" depuis sélection
Étend votre `ReportMissingInvoices_Click` :
- Sélection multiple de rows (ToDoList ouverte) → "Compose mail" → template + variables (`{{Receiver}}`, `{{TotalMissing}}`, table HTML générée).
- Templates par catégorie (claim initial, relance, escalade).
- Pré-rempli vers `mail.To` extrait de `I_RECEIVER_NAME` lookup correspondants.

**Effort** : 2 j.

### ◆ 5.3 Companion Excel (Add-in)
Pour les analystes qui veulent croiser avec leurs propres tableaux :
- Add-in léger qui appelle directement le service (ou copie de la BDD Access locale).
- Function `=RECOTOOL.GetMissingAmount("BGI12345")` côté cellule.

**Effort** : 4-5 j (VSTO / Excel add-in).

### ◇ 5.4 Webhooks sortants
Sur événements (action set to Trigger, link confirmed, etc.) → POST JSON vers une URL configurable. Pour intégration future avec d'autres systèmes (Teams, Power Automate, Jira).

**Effort** : 2 j.

---

## 6. UX / qualité de vie

### ★ 6.1 Auto-save layout par Saved View
Aujourd'hui un layout (column widths/order/visibility) est sauvegardé manuellement.
- Détection du changement de layout (resize/drag/hide column) → debounce 3 s → save silencieux dans `UserViewPreferenceService` pour le `SelectedSavedView` courant.
- Toast discret "Layout saved" la première fois pour rassurer.

**Effort** : ½ j.

### ★ 6.2 Mode sombre
- Le thème actuel utilise `BNPMainGreenBrush` etc. → le rendre dynamique (DynamicResource déjà en place dans certains endroits).
- Toggle dans les Settings, persisté côté user preferences.
- Beaucoup d'utilisateurs travaillent sur écran toute la journée.

**Effort** : 2-3 j (revoir tous les hex hard-codés en XAML).

### ◆ 6.3 Mini-map / overview de la grille
Sidebar verticale qui montre une vue compressée de TOUTES les lignes (1 px par row, couleur = StatusColorBrush) avec rectangle déplaçable pour le viewport. Permet de scroller à 10 000 lignes en un clic.

**Effort** : 2 j.

### ◆ 6.4 "Pin row" / Watch list
- Épingler une ligne (étoile dans la 1re colonne) → la ligne reste visible en haut quel que soit le filtre/tri.
- Watch list = page dédiée listant les rows épinglées de l'utilisateur, avec alerte si une étoile change d'état (linked, deleted, etc.).

**Effort** : 1-2 j.

### ◆ 6.5 Customizable conditional formatting
- L'utilisateur définit ses propres règles "si Amount > 100k && Action == null → fond rouge".
- Stockage utilisateur. UI similaire aux règles de mise en forme conditionnelle Excel.

**Effort** : 3 j.

### ◇ 6.6 "Recettes" de réconciliation
Une "recipe" = chaîne d'actions enregistrée (filter X → sélectionne tout → set Action=Trigger → set Assignee=me).
- Rejouable d'un clic (`Ctrl+Shift+R` "Run recipe").
- Partageable.
- Économise 10+ clics par cycle quand un utilisateur fait toujours la même séquence.

**Effort** : 3 j.

---

## 7. Architecture, fiabilité, dette technique

### ★ 7.1 Migration vers .NET 8 (Windows Desktop)
Bénéfices directs :
- Démarrage 30-50 % plus rapide (native AOT pour parties non-WPF).
- GC plus efficace → moins de stutters scroll.
- Accès aux `System.Text.Json` complet, `IAsyncEnumerable` (utile pour le preload streaming), `Span<T>` partout.
- WPF .NET 8 améliore le rendu DirectX (notamment HDPI).

**Effort** : 5-10 j selon l'utilisation de packages legacy. Le plus gros risque = Syncfusion versions ; vérifier qu'on a la licence sur la version .NET 8.

### ★ 7.2 Logging structuré (Serilog) + log viewer
Aujourd'hui il y a `LogPerf`/`LogAction`/`Debug.WriteLine` éparpillés.
- Un Serilog avec sinks : fichier rolling, eventlog, et SQLite local pour query.
- Une fenêtre interne "Show logs" filtrable (level/category/timeframe) pour debug rapide en prod.

**Effort** : 2-3 j.

### ★ 7.3 Crash reporter + auto-update
- Sur exception non gérée → dialog "Send report?" avec capture (stack, last 100 log lines, version).
- Email automatique vers une boîte ops (ou ticket Jira via webhook).
- Auto-update via ClickOnce ou Squirrel : MAJ silencieuse au prochain démarrage.

**Effort** : 3 j.

### ◆ 7.4 Tests automatisés sur jeu de données figé
Création d'une suite "regression dataset" :
- Snapshot Access DB avec 50 cas tordus (matching ambigu, devises mixtes, basket multi-rows, etc.).
- Tests `ReconciliationViewEnricher.CalculateMissingAmounts` etc. sur ces cas.
- Permet de refactor sans régression.

**Effort** : 3-5 j (le plus dur = construire le dataset).

### ◆ 7.5 Backup/restore des préférences utilisateur
- Bouton "Export my settings" (saved filters + views + layouts + window state) → fichier JSON.
- Re-import après reformatage poste / changement de PC.

**Effort** : 1 j.

### ◆ 7.6 Cache disque entre sessions (warm start)
Mentionné dans `PERF_AUDIT.md` (S-2). Sérialiser `_reconciliationViewData` + `_reconciliationHistoricalData` à la fermeture (BSON/MessagePack), recharger en <100 ms au démarrage avant la sync delta.

**Effort** : 2-3 j.

### ◆ 7.7 Pré-aggrégation côté SQL pour MissingAmount/IsMatchedAcrossAccounts
Mentionné dans `PERF_AUDIT.md` (S-3). Sous-requête SQL qui fait le `GROUP BY` par `InternalInvoiceReference` et retourne directement les valeurs agrégées → coupe la passe O(N) côté client.

**Effort** : 2 j.

### ◇ 7.8 Index Access ciblés
Vérifier la présence d'index sur :
- `T_Reconciliation.ID` (PK déjà), `T_Reconciliation.DeleteDate` (filtre quasi-systématique)
- `T_DataAmbre.Account_ID`, `T_DataAmbre.Operation_Date`, `T_DataAmbre.Reconciliation_Num`
- DWINGS : `Invoice.INVOICE_ID`, `Guarantee.GUARANTEE_ID`, `Invoice.BGPMT`

**Effort** : ½ j (mais nécessite accès admin sur la base partagée).

---

## 8. Idées plus prospectives

### ◇ 8.1 Mode "single-pane-of-glass"
Dans la même fenêtre, écran scindé : à gauche la grille filtrée, à droite un panneau "Detail" sur la row sélectionnée avec onglets :
- Comments + @mentions
- DWINGS Invoice raw
- DWINGS Guarantee raw
- Activity log de la row
- Suggested matches (cf. 4.1)

**Effort** : 4 j. Réduit énormément les ouvertures de dialogs.

### ◇ 8.2 Real-time presence cursors
Vous avez déjà presence par row. L'aller plus loin façon Google Docs :
- Voir le curseur des autres utilisateurs en live sur la grille.
- "Marie est en train d'éditer cette ligne" → bloquer l'édition côté courant avec banner.

**Effort** : 5+ j (besoin d'un canal pub/sub : SignalR sur lan, ou polling intensif sur Access).

### ◇ 8.3 Mobile read-only companion
Pour les managers qui veulent regarder les KPIs en mobilité :
- Petite app web React/Blazor en read-only.
- Lit les snapshots quotidiens KPI via API HTTP minimal.
- Notif push sur seuils.

**Effort** : grosse story (3-4 semaines).

### ◇ 8.4 "Explain this row" via LLM
Click droit sur une row → "Why is this NotLinked?" → analyse rapide des champs et du contexte par LLM, retourne un texte court ("Probably because the BGI in label is BGI98765 but DWINGS contains BGI98756 — typo? Click to suggest match.")

**Effort** : 2 j si le LLM est déjà accessible, sinon dépend du contexte infra.

---

## 9. Priorisation suggérée (timeline indicative)

| Sprint | Items | Pourquoi maintenant |
|--------|-------|----------------------|
| Sprint 1 (1 semaine) | 6.1 Auto-save layout · 1.3 What's new · 4.1 Match suggestions · 7.2 Serilog | Quick wins quotidiens + base tech pour le reste |
| Sprint 2 (1-2 semaines) | 1.1 Paste-to-find · 1.2 Bulk edit preview · 2.1 Notification center | Productivité directe utilisateur |
| Sprint 3 (2 semaines) | 3.1 Correspondent dashboard · 5.1 Drag email Outlook · 4.2 Duplicates inspector | Valeur métier visible côté management |
| Sprint 4 (2-3 semaines) | 6.2 Dark mode · 1.5 Command palette · 7.5 Backup prefs · 7.3 Crash reporter | UX premium + fiabilité |
| Sprint 5 (3+ semaines) | 7.1 .NET 8 · 7.4 Tests regression · 8.1 Single-pane-of-glass | Investissement structurel |

---

## 10. Critères de tri (à valider avec l'équipe)

Avant de t'engager, je suggère de prioriser sur **3 axes** :
1. **Combien de clics économisés / utilisateur / jour** (productivité tangible).
2. **Risque de régression** (les chantiers tech doivent passer à travers une suite de tests).
3. **Visibilité management** (dashboards, reports → ils financent les évolutions).

Items qui marquent les 3 axes : **1.2 Bulk edit**, **3.1 Correspondent dashboard**, **4.1 Match suggestions**.
