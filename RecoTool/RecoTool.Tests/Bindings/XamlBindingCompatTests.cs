using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services.DTOs;
using RecoTool.Services.Rules;
using RecoTool.UI.Models;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.Bindings
{
    /// <summary>
    /// Vérifie que les chemins de binding référencés dans les fichiers XAML
    /// existent bien sur le ViewModel correspondant. Détecte les ruptures
    /// silencieuses (WPF binding errors) à test time plutôt qu'à runtime.
    ///
    /// <para>
    /// Approche : on parse le XAML en texte brut (regex sur <c>{Binding ...}</c>),
    /// on extrait l'éventuel <c>Path=</c> imbriqué (gérant <c>RelativeSource</c>),
    /// puis on résout chaque path racine via réflexion sur le VM. Pour les XAML
    /// qui contiennent des <c>DataGrid</c> ou <c>DataTemplate</c> avec des
    /// item-contexts différents, on accepte une liste de types fallback : un
    /// binding est valide s'il résout sur le VM OU sur l'un des types item.
    /// </para>
    ///
    /// <para>
    /// **Limitations connues** :
    /// - Approche permissive : un binding qui devrait viser le VM mais résout
    ///   par accident sur un type item ne sera pas signalé.
    /// - Ne vérifie pas la compatibilité de types entre source et target.
    /// - Les bindings <c>ElementName</c> sont ignorés (le named target n'est
    ///   pas un VM property).
    /// </para>
    /// </summary>
    public class XamlBindingCompatTests
    {
        /// <summary>Locates the RecoTool/Windows folder relative to the test assembly.</summary>
        private static string FindWindowsFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "RecoTool", "Windows");
                if (Directory.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir.FullName, "Windows");
                if (Directory.Exists(candidate) &&
                    File.Exists(Path.Combine(dir.FullName, "RecoTool.csproj")))
                    return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate RecoTool/Windows directory from test assembly.");
        }

        /// <summary>
        /// Extracts top-level binding paths from raw XAML text. Handles three shapes:
        /// <c>{Binding Foo}</c>, <c>{Binding Path=Foo}</c>, and bindings that contain
        /// <c>Path=Foo</c> after a RelativeSource/Source clause.
        /// </summary>
        internal static IReadOnlyList<string> ExtractBindingPaths(string xaml)
        {
            if (string.IsNullOrEmpty(xaml)) return Array.Empty<string>();

            var paths = new HashSet<string>(StringComparer.Ordinal);

            // 1) Find every {Binding ...} block (balanced via non-{ char class).
            //    Inside a binding expression, braces are unlikely except for nested {...}
            //    such as {RelativeSource ...}. We use a tolerant pattern that allows any
            //    content except an outer closing brace at top level.
            var binding = new Regex(@"\{Binding\b([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}", RegexOptions.IgnoreCase);

            // Detect bindings that target an explicit non-VM source. Those paths
            // resolve against that source (a named element, a static resource, a
            // self/templated parent, etc.) — NOT against the VM, so we must skip
            // them here. RelativeSource is a special case: it can target an
            // ancestor's DataContext (handled by the DataContext.X normalization
            // in AddPath), so we don't skip it.
            var sourceClauseRx = new Regex(@"(?<![A-Za-z])(Source|ElementName)\s*=", RegexOptions.IgnoreCase);

            foreach (Match m in binding.Matches(xaml))
            {
                var inner = m.Groups[1].Value;

                // Skip bindings that have an explicit non-VM source.
                if (sourceClauseRx.IsMatch(inner))
                    continue;

                // 2) Path=Foo.Bar (with or without preceding clauses)
                var pathRx = new Regex(@"\bPath\s*=\s*([A-Za-z_][A-Za-z0-9_\.\[\]]*)", RegexOptions.IgnoreCase);
                var pathMatch = pathRx.Match(inner);
                if (pathMatch.Success)
                {
                    AddPath(paths, pathMatch.Groups[1].Value);
                    continue;
                }

                // 3) Bare positional path: {Binding Foo.Bar}
                //    Skip if the inner part starts with a reserved keyword or a brace.
                var bareRx = new Regex(@"^\s*([A-Za-z_][A-Za-z0-9_\.\[\]]*)");
                var bareMatch = bareRx.Match(inner);
                if (bareMatch.Success)
                {
                    var bare = bareMatch.Groups[1].Value;
                    if (!IsReservedBindingKeyword(bare))
                        AddPath(paths, bare);
                }
            }

            return paths.ToArray();
        }

        private static void AddPath(HashSet<string> paths, string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var t = p.Trim();
            // Normalize ancestor lookups: `DataContext.X` is conventionally used with
            // a RelativeSource to reach an ancestor's VM. Treat it as just `X` so it
            // resolves on the VM root.
            if (t.StartsWith("DataContext.", StringComparison.OrdinalIgnoreCase))
                t = t.Substring("DataContext.".Length);
            if (string.IsNullOrEmpty(t)) return;
            paths.Add(t);
        }

        private static bool IsReservedBindingKeyword(string token)
        {
            return token.Equals("RelativeSource", StringComparison.OrdinalIgnoreCase)
                || token.Equals("ElementName", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Source", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Converter", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Mode", StringComparison.OrdinalIgnoreCase)
                || token.Equals("StringFormat", StringComparison.OrdinalIgnoreCase)
                || token.Equals("FallbackValue", StringComparison.OrdinalIgnoreCase)
                || token.Equals("TargetNullValue", StringComparison.OrdinalIgnoreCase)
                || token.Equals("UpdateSourceTrigger", StringComparison.OrdinalIgnoreCase)
                || token.Equals("NotifyOnTargetUpdated", StringComparison.OrdinalIgnoreCase)
                || token.Equals("XPath", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that a binding path resolves against a type by drilling through
        /// dotted segments. Indexer suffixes (<c>Foo[0]</c>) are stripped.
        /// </summary>
        internal static bool PathResolvesOn(Type rootType, string path, out string failureSegment)
        {
            failureSegment = null;
            if (rootType == null || string.IsNullOrEmpty(path)) return false;

            var current = rootType;
            var segments = path.Split('.');
            foreach (var raw in segments)
            {
                var seg = raw;
                var bracket = seg.IndexOf('[');
                if (bracket >= 0) seg = seg.Substring(0, bracket);

                var prop = current.GetProperty(seg, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop != null) { current = prop.PropertyType; continue; }

                var field = current.GetField(seg, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (field != null) { current = field.FieldType; continue; }

                failureSegment = seg;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Tries to resolve a path against the root VM type and any of the fallback
        /// item types (used for DataTemplate/DataGrid cell contexts).
        /// </summary>
        internal static bool ResolvesAgainstAny(string path, Type vmType, IEnumerable<Type> fallbackItemTypes)
        {
            if (PathResolvesOn(vmType, path, out var _)) return true;
            if (fallbackItemTypes == null) return false;
            foreach (var t in fallbackItemTypes)
            {
                if (PathResolvesOn(t, path, out var _)) return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helper self-tests
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("<TextBox Text=\"{Binding Foo}\"/>", "Foo")]
        [InlineData("<TextBox Text=\"{Binding Path=Foo}\"/>", "Foo")]
        [InlineData("<TextBox Text=\"{Binding Foo.Bar}\"/>", "Foo.Bar")]
        [InlineData("<TextBox Text=\"{Binding Foo, Mode=TwoWay}\"/>", "Foo")]
        [InlineData("<ComboBox ItemsSource=\"{Binding RelativeSource={RelativeSource AncestorType=Window}, Path=Scopes}\"/>", "Scopes")]
        public void ExtractBindingPaths_FindsBasicBindings(string xaml, string expected)
        {
            ExtractBindingPaths(xaml).Should().Contain(expected);
        }

        [Fact]
        public void ExtractBindingPaths_FiltersReservedKeywords()
        {
            var xaml = "<TextBox Text=\"{Binding ElementName=foo}\"/>";
            ExtractBindingPaths(xaml).Should().NotContain("ElementName");
        }

        [Fact]
        public void ExtractBindingPaths_HandlesMultipleBindingsInOneFile()
        {
            var xaml = "<Grid><TextBox Text=\"{Binding Foo}\"/><TextBox Text=\"{Binding Bar.Baz}\"/></Grid>";
            ExtractBindingPaths(xaml).Should().BeEquivalentTo(new[] { "Foo", "Bar.Baz" });
        }

        [Fact]
        public void ExtractBindingPaths_SkipsStaticResourceSource()
        {
            // Source={StaticResource X} targets a resource, NOT the VM.
            var xaml = "<TextBlock Foreground=\"{Binding Source={StaticResource BrushKey}, Path=Color}\"/>";
            ExtractBindingPaths(xaml).Should().NotContain("Color");
        }

        [Fact]
        public void ExtractBindingPaths_SkipsElementNameSource()
        {
            // ElementName=foo targets a named control's property, NOT the VM.
            var xaml = "<TextBox Text=\"{Binding SelectedItem, ElementName=ComboFoo}\"/>";
            ExtractBindingPaths(xaml).Should().NotContain("SelectedItem");
        }

        [Fact]
        public void ExtractBindingPaths_KeepsRelativeSourcePathAgainstVm()
        {
            // RelativeSource AncestorType=Window with Path=X resolves against the
            // ancestor's DataContext (the VM at the root). Must be captured.
            var xaml = "<ComboBox ItemsSource=\"{Binding RelativeSource={RelativeSource AncestorType=Window}, Path=Scopes}\"/>";
            ExtractBindingPaths(xaml).Should().Contain("Scopes");
        }

        private class HelperDummy
        {
            public string Foo { get; set; }
            public Nested Bar { get; set; }
        }
        private class Nested { public int Value { get; set; } }

        [Fact]
        public void PathResolvesOn_TopLevelProperty_Resolves()
            => PathResolvesOn(typeof(HelperDummy), "Foo", out var _).Should().BeTrue();

        [Fact]
        public void PathResolvesOn_NestedProperty_Resolves()
            => PathResolvesOn(typeof(HelperDummy), "Bar.Value", out var _).Should().BeTrue();

        [Fact]
        public void PathResolvesOn_MissingSegment_ReportsFailingPart()
        {
            PathResolvesOn(typeof(HelperDummy), "Bar.Missing", out var seg).Should().BeFalse();
            seg.Should().Be("Missing");
        }

        [Fact]
        public void ResolvesAgainstAny_FallbackTypeAccepted()
        {
            ResolvesAgainstAny("Value", typeof(HelperDummy), new[] { typeof(Nested) }).Should().BeTrue();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Per-window tests : XAML ↔ VM compat
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void RuleEditorWindow_AllBindings_ResolveOnRuleEditorViewModel()
        {
            AssertAllBindingsResolve("RuleEditorWindow.xaml", typeof(RuleEditorViewModel));
        }

        [Fact]
        public void RulesAdminWindow_AllBindings_ResolveOnVmOrTruthRuleItem()
        {
            // DataGrid item DataContext = TruthRule. The simple resolver accepts a path
            // that resolves on either the VM root or any item type provided.
            AssertAllBindingsResolve("RulesAdminWindow.xaml", typeof(RulesAdminViewModel),
                fallbackItemTypes: new[] { typeof(TruthRule) });
        }

        [Fact]
        public void ProgressWindow_AllBindings_ResolveOnProgressWindowViewModel()
        {
            AssertAllBindingsResolve("ProgressWindow.xaml", typeof(ProgressWindowViewModel));
        }

        [Fact]
        public void MainWindow_AllBindings_ResolveOnMainWindowViewModel()
        {
            AssertAllBindingsResolve("MainWindow.xaml", typeof(MainWindowViewModel),
                fallbackItemTypes: new[] { typeof(Country) });
        }

        [Fact]
        public void HomePage_AllBindings_ResolveOnVmOrItemTypes()
        {
            // TodoCards: TodoCard, AlertItems: HomeAlert, AssigneeLeaderboard: AssigneeStats,
            // CompletionEstimate is a single nested object accessed via dotted path.
            AssertAllBindingsResolve("HomePage.xaml", typeof(HomePageViewModel),
                fallbackItemTypes: new[] { typeof(TodoCard), typeof(HomeAlert), typeof(AssigneeStats), typeof(TodoCardCount) });
        }

        [Fact]
        public void ReconciliationPage_AllBindings_ResolveOnVmOrItemTypes()
        {
            AssertAllBindingsResolve("ReconciliationPage.xaml", typeof(ReconciliationPageViewModel),
                fallbackItemTypes: new[] { typeof(TodoListItem), typeof(UserFilter), typeof(UserFieldsPreference) });
        }

        [Fact]
        public void ReconciliationDetailWindow_AllBindings_ResolveOnVmOrLinkedItem()
        {
            AssertAllBindingsResolve("ReconciliationDetailWindow.xaml", typeof(ReconciliationDetailViewModel),
                fallbackItemTypes: new[] { typeof(LinkedItemRow) });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helper
        // ─────────────────────────────────────────────────────────────────────

        private static void AssertAllBindingsResolve(
            string xamlFileName,
            Type vmType,
            Type[] fallbackItemTypes = null)
        {
            var folder = FindWindowsFolder();
            var xamlPath = Path.Combine(folder, xamlFileName);
            File.Exists(xamlPath).Should().BeTrue(
                $"XAML file '{xamlFileName}' must exist under {folder}");

            var xaml = File.ReadAllText(xamlPath);
            var paths = ExtractBindingPaths(xaml);

            if (paths.Count == 0)
                return; // XAML may be empty of bindings (Click-based code-behind UI) — nothing to assert.

            var failures = new List<string>();
            foreach (var p in paths)
            {
                if (!ResolvesAgainstAny(p, vmType, fallbackItemTypes))
                    failures.Add($"  • '{p}'");
            }

            failures.Should().BeEmpty(
                $"All bindings in {xamlFileName} must resolve against {vmType.Name}" +
                (fallbackItemTypes != null && fallbackItemTypes.Length > 0
                    ? " or " + string.Join("/", fallbackItemTypes.Select(t => t.Name))
                    : "") +
                ".\nMissing paths:\n" + string.Join("\n", failures));
        }
    }
}
