using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RecoTool.Services;
using RecoTool.Services.Policies;
using RecoTool.Domain.Repositories;
using RecoTool.Infrastructure.Repositories;
using RecoTool.Windows;
using RecoTool.Services.External;
using RecoTool.API;
using System.Globalization;
using RecoTool.Configuration;
using RecoTool.Infrastructure.DI;
using RecoTool.Infrastructure.Health;
using RecoTool.Infrastructure.Health.Checks;
using RecoTool.Infrastructure.Logging;
using RecoTool.Services.Helpers;
using Microsoft.Extensions.Logging;
using Serilog;

namespace RecoTool
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; }

        [STAThread]
        public static void Main()
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ─────────────────────────────────────────────────────────────────
            // PERF DIAGNOSTIC: WPF render tier
            // Tier 0 = software rendering (slow, scroll will never be smooth)
            // Tier 1 = partial hardware acceleration (ok)
            // Tier 2 = full hardware acceleration (best — what we want)
            //
            // Corporate VMs / Citrix / RDP sessions often run at Tier 0. If we
            // see this in the logs, no XAML optimization will rescue scroll perf.
            // Workarounds: enable GPU acceleration in the VM/RDP profile, or
            // ProcessRenderMode.Software → ProcessRenderMode.Default (default
            // is correct, but listed here for if a previous tweak forced it).
            // ─────────────────────────────────────────────────────────────────
            try
            {
                var renderTier = System.Windows.Media.RenderCapability.Tier >> 16;
                var tierLabel = renderTier switch
                {
                    0 => "SOFTWARE (slow)",
                    1 => "partial hardware",
                    2 => "full hardware",
                    _ => "unknown"
                };
                System.Diagnostics.Debug.WriteLine($"[Startup] WPF RenderCapability.Tier = {renderTier} ({tierLabel})");
                if (renderTier == 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Startup] WARNING: WPF is using software rendering. " +
                        "Scroll performance will be severely limited regardless of XAML optimizations. " +
                        "Investigate: GPU drivers, RDP/Citrix session, ProcessRenderMode.");
                }
            }
            catch { /* best-effort diagnostic */ }

            // Syncfusion license key — loaded from secrets.config (gitignored, never committed).
            // To set up: copy secrets.config.template to secrets.config and fill in your key.
            // Get your key at: https://www.syncfusion.com/account/manage-trials/start-trials
            try
            {
                var secretsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secrets.config");
                if (!File.Exists(secretsPath))
                    secretsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "secrets.config");
                if (File.Exists(secretsPath))
                {
                    foreach (var line in File.ReadAllLines(secretsPath))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("SYNCFUSION_LICENSE_KEY=", StringComparison.OrdinalIgnoreCase))
                        {
                            var key = trimmed.Substring("SYNCFUSION_LICENSE_KEY=".Length).Trim();
                            if (!string.IsNullOrWhiteSpace(key))
                                Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(key);
                            break;
                        }
                    }
                }
            }
            catch { /* secrets.config missing or malformed — app starts without license (watermark only) */ }

            var services = new ServiceCollection();

            #region Structured Logging
            // Serilog → ILoggerFactory bridge. Built BEFORE the rest of ConfigureServices so
            // every downstream registration can resolve ILogger<T> if needed. The legacy
            // LogHelper (file-based actions/perf/rules logs) keeps working unchanged and now
            // also forwards to this pipeline. See Infrastructure/Logging/LoggingSetup.cs.
            var loggerFactory = LoggingSetup.CreateLoggerFactory();
            services.AddSingleton<ILoggerFactory>(loggerFactory);
            services.AddLogging(builder => builder.AddSerilog(dispose: true));
            #endregion

            // ── Cross-cutting infrastructure (Lot 0) ──
            services.AddSingleton<RecoTool.Infrastructure.Time.IClock>(RecoTool.Infrastructure.Time.SystemClock.Instance);
            services.AddSingleton<RecoTool.Infrastructure.IO.IFileSystem>(RecoTool.Infrastructure.IO.SystemFileSystem.Instance);

            // ── UI service (Lot 2) ──
            services.AddSingleton<RecoTool.Services.UI.IDialogService, RecoTool.Services.UI.WpfDialogService>();

            // ── ViewModels (live VMs only — orphan VMs removed during cleanup) ──
            services.AddTransient<RecoTool.ViewModels.MainWindowViewModel>();
            services.AddTransient<RecoTool.ViewModels.HomePageViewModel>();
            services.AddTransient<RecoTool.ViewModels.ProgressWindowViewModel>();
            services.AddTransient<RecoTool.ViewModels.ImportAmbreViewModel>();
            // Register UserFilter / UserTodoList services as interfaces so VMs and
            // other consumers can be mocked in tests.
            services.AddTransient<IUserFilterService>(sp =>
                new UserFilterService(sp.GetRequiredService<OfflineFirstService>().ReferentialConnectionString,
                    Environment.UserName ?? "Unknown"));
            services.AddTransient<IUserTodoListService>(sp =>
                new UserTodoListService(sp.GetRequiredService<OfflineFirstService>().ReferentialConnectionString));
            services.AddTransient<IUserViewPreferenceService>(sp =>
                new UserViewPreferenceService(sp.GetRequiredService<OfflineFirstService>().ReferentialConnectionString,
                    Environment.UserName ?? "Unknown"));

            services.AddTransient<RecoTool.ViewModels.ReconciliationPageViewModel>(sp =>
                new RecoTool.ViewModels.ReconciliationPageViewModel(
                    sp.GetRequiredService<OfflineFirstService>(),
                    sp.GetRequiredService<ReconciliationService>(),
                    sp.GetRequiredService<IUserFilterService>(),
                    sp.GetRequiredService<IUserTodoListService>(),
                    sp.GetRequiredService<RecoTool.Services.UI.IDialogService>(),
                    sp.GetRequiredService<RecoTool.Infrastructure.Time.IClock>()));

            // Rules-related VMs : IRulesAdmin via TruthTableRepository.
            services.AddTransient<RecoTool.Services.Rules.IRulesAdmin>(sp =>
                new RecoTool.Services.Rules.TruthTableRepository(sp.GetRequiredService<OfflineFirstService>()));
            services.AddTransient<RecoTool.ViewModels.RulesAdminViewModel>();

            // ── Network path provider (used by NetworkShareHealthCheck) ──
            // Kept after the Sync V2 cleanup because the health check needs it.
            services.AddSingleton<RecoTool.Services.Sync.INetworkPathProvider>(sp =>
                new RecoTool.Services.Sync.OfflineFirstNetworkPathProvider(
                    sp.GetRequiredService<OfflineFirstService>()));

            // Notre service offline-first (singleton pour tout l'app)
            services.AddSingleton<OfflineFirstService>();
            // Expose-le aussi via l'interface pour que les services testables le résolvent
            services.AddSingleton<IOfflineFirstService>(sp => sp.GetRequiredService<OfflineFirstService>());

            // Free API service (singleton): wraps authentication, throttling (max 3), and caching
            // Register concrete instance via factory to force parameterless ctor and avoid circular dependency
            services.AddSingleton<FreeApiService>(sp => new FreeApiService());
            services.AddSingleton<IFreeApiClient>(sp => sp.GetRequiredService<FreeApiService>());

            // Sync policy: centralize when background pushes and syncs are allowed
            services.AddSingleton<ISyncPolicy, SyncPolicy>();

            // Services métiers
            services.AddTransient<AmbreImportService>();
            services.AddTransient<IAmbreImportService>(sp => sp.GetRequiredService<AmbreImportService>());
            services.AddTransient<ReconciliationService>(sp =>
            {
                var offline = sp.GetRequiredService<OfflineFirstService>();
                // Récupère la chaîne de connexion locale courante (nécessite que le pays courant soit déjà défini)
                var connStr = offline.GetCurrentLocalConnectionString();
                var currentUser = Environment.UserName ?? "Unknown";
                var countries = offline.Countries;
                // Inject IDataAmbreRepository so GetAmbreDataAsync routes through the repository pattern.
                // Cascades benefit: HomePageViewModel, ReconciliationMatchingService, DashboardExportService
                // all fall back to ReconciliationService.GetAmbreDataAsync — they now transparently
                // go through the repo. GetService (not GetRequiredService) keeps the registration
                // robust if the repo registration is removed later.
                var ambreRepo = sp.GetService<RecoTool.Domain.Repositories.IDataAmbreRepository>();
                var clock = sp.GetService<RecoTool.Infrastructure.Time.IClock>();
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<ReconciliationService>>();
                return new ReconciliationService(connStr, currentUser, countries, offline, clock, logger, ambreRepo);
            });
            // Expose ReconciliationService via its interface so VMs and other consumers
            // (HomePageViewModel, ReconciliationDetailViewModel, etc.) can be mocked in tests.
            services.AddTransient<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());

            // Lookup/Referential/Options services
            services.AddTransient<LookupService>(sp => new LookupService(sp.GetRequiredService<OfflineFirstService>()));
            services.AddTransient<ReferentialService>(sp =>
            {
                var offline = sp.GetRequiredService<OfflineFirstService>();
                var recoSvc = sp.GetRequiredService<ReconciliationService>();
                return new ReferentialService(offline, recoSvc?.CurrentUser);
            });
            services.AddTransient<OptionsService>(sp => new OptionsService(
                sp.GetRequiredService<ReconciliationService>(),
                sp.GetRequiredService<ReferentialService>(),
                sp.GetRequiredService<LookupService>()));

            #region Repository Pattern (Domain Repos)
            // Domain repositories for the most-touched Access tables. Concrete OleDb
            // impls live in RecoTool.Infrastructure.Repositories; in-memory fakes ship
            // from RecoTool.Tests for consumer unit tests.

            // Per-country Ambre rows. The connection-string factory routes a country id
            // (e.g. "FR") to the matching .accdb. We resolve OfflineFirstService lazily
            // because per-country files come and go as the user switches country.
            services.AddTransient<RecoTool.Domain.Repositories.IDataAmbreRepository>(sp =>
            {
                var offline = sp.GetRequiredService<OfflineFirstService>();
                var logger = sp.GetService<ILogger<RecoTool.Infrastructure.Repositories.DataAmbreRepository>>();
                return new RecoTool.Infrastructure.Repositories.DataAmbreRepository(
                    countryId => offline.GetAmbreConnectionString(countryId),
                    logger);
            });

            // Referential T_Ref_User_Fields (Actions / KPIs / Incident Types / …).
            // Connection string is resolved lazily so changes after startup are picked up.
            services.AddTransient<RecoTool.Domain.Repositories.IUserFieldsRepository>(sp =>
            {
                var offline = sp.GetRequiredService<OfflineFirstService>();
                var logger = sp.GetService<ILogger<RecoTool.Infrastructure.Repositories.UserFieldsRepository>>();
                return new RecoTool.Infrastructure.Repositories.UserFieldsRepository(
                    () => offline.ReferentialConnectionString,
                    logger);
            });
            #endregion

            // Referential cache service (singleton for app-wide caching)
            services.AddSingleton<ReferentialCacheService>();

            // Fenêtres principales — MVVM ctor explicite (les deux services sont résolus depuis DI)
            services.AddTransient<MainWindow>(sp => new MainWindow(
                sp.GetRequiredService<OfflineFirstService>(),
                sp.GetRequiredService<RecoTool.ViewModels.MainWindowViewModel>()));
            services.AddTransient<ImportAmbreWindow>();
            // ── Secondary windows (Option B — Wave 2) ──
            // Register so callers can resolve via App.ServiceProvider.GetRequiredService<>().
            // MEDI picks the ctor with the most resolvable parameters, which is the
            // MVVM-aware one for each of these windows.
            services.AddTransient<HomePage>();
            services.AddTransient<RulesAdminWindow>();
            services.AddTransient<RulesHealthWindow>();
            // RuleDebugWindow has only a parameterless ctor; registering it for
            // consistency so call sites can use the same DI resolution pattern.
            services.AddTransient<RuleDebugWindow>();
            services.AddTransient<ReconciliationPage>(sp =>
            {
                var recoSvc = sp.GetRequiredService<ReconciliationService>();
                var offline = sp.GetRequiredService<OfflineFirstService>();
                var freeApi = sp.GetRequiredService<FreeApiService>();
                return new ReconciliationPage(recoSvc, offline, freeApi);
            });
            services.AddTransient<ReconciliationView>();

            #region Health Checks
            // Startup probes: surface infrastructure failures BEFORE the user discovers
            // them via a hidden click path. See Infrastructure/Health/*. Each check is
            // registered as IStartupHealthCheck so the runner picks them all up via
            // IEnumerable<IStartupHealthCheck> injection. Concrete classes also stay
            // resolvable for individual tests/diagnostics.
            services.AddSingleton<RecoTool.Infrastructure.Health.Checks.LocalDatabaseHealthCheck>();
            services.AddSingleton<RecoTool.Infrastructure.Health.Checks.NetworkShareHealthCheck>();
            services.AddSingleton<RecoTool.Infrastructure.Health.Checks.FreeApiHealthCheck>();
            services.AddSingleton<RecoTool.Infrastructure.Health.IStartupHealthCheck>(sp =>
                sp.GetRequiredService<RecoTool.Infrastructure.Health.Checks.LocalDatabaseHealthCheck>());
            services.AddSingleton<RecoTool.Infrastructure.Health.IStartupHealthCheck>(sp =>
                sp.GetRequiredService<RecoTool.Infrastructure.Health.Checks.NetworkShareHealthCheck>());
            services.AddSingleton<RecoTool.Infrastructure.Health.IStartupHealthCheck>(sp =>
                sp.GetRequiredService<RecoTool.Infrastructure.Health.Checks.FreeApiHealthCheck>());
            services.AddSingleton<RecoTool.Infrastructure.Health.HealthCheckRunner>();
            #endregion

            ServiceProvider = services.BuildServiceProvider();
            ServiceLocator.Initialize(ServiceProvider);

            // Ensure OfflineFirstService is fully initialized BEFORE constructing any services/windows
            try
            {
                var offline = ServiceProvider.GetRequiredService<OfflineFirstService>();
                var policy = ServiceProvider.GetRequiredService<ISyncPolicy>();
                // Propagate policy to service (background pushes disabled by default)
                try { offline.AllowBackgroundPushes = policy.AllowBackgroundPushes; } catch { }
                // Complete referential load
                await offline.LoadReferentialsAsync();

                var currentCountry = offline.CurrentCountryId;

                // Authenticate Free API at startup (before any parallel calls during import)
                try
                {
                    var free = ServiceProvider.GetRequiredService<IFreeApiClient>();
                    await free.AuthenticateAsync();
                }
                catch (Exception exAuth)
                {
                    System.Diagnostics.Debug.WriteLine($"[Startup] FreeApi authentication warning: {exAuth.Message}");
                }

                // Ensure Control DB schema exists early (idempotent)
                try
                {
                    await offline.EnsureControlSchemaAsync();
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"[Startup] EnsureControlSchema warning: {ex2.Message}");
                }
            }
            catch (Exception ex)
            {
                // Do not block app start, but log for diagnostics
                System.Diagnostics.Debug.WriteLine($"[Startup] OfflineFirst initialization warning: {ex.Message}");
            }

            #region Health Checks
            // Run startup probes after OFS init (so the local DB / network share / Free API
            // are all in a meaningful state) but BEFORE the main window is shown. The
            // runner enforces an overall ~10s timeout — even a completely broken
            // environment cannot block startup longer than that.
            try
            {
                var runner = ServiceProvider.GetRequiredService<HealthCheckRunner>();
                var results = await runner.RunAllAsync().ConfigureAwait(true);
                var failures = results.Where(r => !r.Result.IsHealthy).ToList();
                if (failures.Count > 0)
                {
                    ShowHealthDialog(failures);
                }
            }
            catch (Exception exHealth)
            {
                // Health infrastructure itself broke — log but never block startup.
                System.Diagnostics.Debug.WriteLine($"[Startup] Health check infrastructure error: {exHealth.Message}");
            }
            #endregion

            var main = ServiceProvider.GetRequiredService<MainWindow>();
            main.Show();

            await Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                main.Topmost = true;
                main.Activate();
                main.Focus();
                main.Dispatcher.BeginInvoke(new Action(() => main.Topmost = false));

                var helper = new System.Windows.Interop.WindowInteropHelper(main);
                SetForegroundWindow(helper.Handle);
            }));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        #region Health Checks
        /// <summary>
        /// Surfaces startup health-check failures to the user.
        ///
        /// <para>
        /// Behaviour by environment:
        /// <list type="bullet">
        ///   <item><b>UAT</b> (<see cref="FeatureFlags.IsUAT"/> = true): show the detailed
        ///         <see cref="HealthCheckDialog"/> modally with a Continue / Exit choice.
        ///         If the user picks Exit, we shut the app down via <see cref="Application.Shutdown()"/>.</item>
        ///   <item><b>Production</b>: do not block the user with a modal — log every
        ///         failure, and only fire a brief <see cref="MessageBox"/> for critical
        ///         failures (local DB unavailable means nothing else will work; we want
        ///         the user to know before they try anything).</item>
        /// </list>
        /// </para>
        /// </summary>
        private void ShowHealthDialog(System.Collections.Generic.IList<(string Name, HealthCheckResult Result)> failures)
        {
            if (failures == null || failures.Count == 0) return;

            // Always log. The runner already logged each result, but emit one summary
            // line so a quick log scan shows "N failures at startup".
            try
            {
                var factory = ServiceProvider?.GetService<ILoggerFactory>();
                var logger = factory?.CreateLogger("RecoTool.App.StartupHealth");
                logger?.LogWarning("Startup health checks: {Count} failure(s) — {Names}",
                    failures.Count,
                    string.Join(", ", failures.Select(f => f.Name)));
            }
            catch { /* logging is best-effort during startup */ }

            if (FeatureFlags.IsUAT)
            {
                try
                {
                    var dialog = new HealthCheckDialog(failures);
                    var ok = dialog.ShowDialog();
                    if (ok == false && !dialog.UserChoseContinue)
                    {
                        // User explicitly chose Exit — terminate the app cleanly.
                        Shutdown();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Startup] Could not show health dialog: {ex.Message}");
                }
                return;
            }

            // Production path: show a single MessageBox only when a *critical* check failed
            // (the local DB — without it the user cannot even list reconciliations).
            var critical = failures.FirstOrDefault(f =>
                string.Equals(f.Name, "Local database", StringComparison.OrdinalIgnoreCase));
            if (critical.Result != null)
            {
                try
                {
                    MessageBox.Show(
                        critical.Result.Message,
                        "RecoTool — startup warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch { /* MessageBox can fail on headless test runners — ignore */ }
            }
        }
        #endregion

        protected override void OnExit(ExitEventArgs e)
        {
            // Disposez les singletons qui implémentent IDisposable
            if (ServiceProvider is IDisposable disp)
                disp.Dispose();

            AccessLinkManager.DisposeAll();

            base.OnExit(e);
        }
    }
}
