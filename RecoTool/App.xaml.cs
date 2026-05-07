using System;
using System.IO;
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
using RecoTool.Infrastructure.DI;
using RecoTool.Services.Helpers;

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

            // Notre service offline-first (singleton pour tout l'app)
            services.AddSingleton<OfflineFirstService>();

            // Free API service (singleton): wraps authentication, throttling (max 3), and caching
            // Register concrete instance via factory to force parameterless ctor and avoid circular dependency
            services.AddSingleton<FreeApiService>(sp => new FreeApiService());
            services.AddSingleton<IFreeApiClient>(sp => sp.GetRequiredService<FreeApiService>());

            // Sync policy: centralize when background pushes and syncs are allowed
            services.AddSingleton<ISyncPolicy, SyncPolicy>();

            // Services métiers
            services.AddTransient<AmbreImportService>();
            services.AddTransient<ReconciliationService>(sp =>
            {
                var offline = sp.GetRequiredService<OfflineFirstService>();
                // Récupère la chaîne de connexion locale courante (nécessite que le pays courant soit déjà défini)
                var connStr = offline.GetCurrentLocalConnectionString();
                var currentUser = Environment.UserName ?? "Unknown";
                var countries = offline.Countries;
                return new ReconciliationService(connStr, currentUser, countries, offline);
            });
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

            // Repositories (transition: wraps existing services)
            services.AddTransient<IReconciliationRepository, ReconciliationRepository>();

            // Referential cache service (singleton for app-wide caching)
            services.AddSingleton<ReferentialCacheService>();

            // Fenêtres principales
            services.AddTransient<MainWindow>();
            services.AddTransient<ImportAmbreWindow>();
            services.AddTransient<ReconciliationPage>(sp =>
            {
                var recoSvc = sp.GetRequiredService<ReconciliationService>();
                var offline = sp.GetRequiredService<OfflineFirstService>();
                var repo = sp.GetRequiredService<IReconciliationRepository>();
                var freeApi = sp.GetRequiredService<FreeApiService>();
                return new ReconciliationPage(recoSvc, offline, repo, freeApi);
            });
            services.AddTransient<ReconciliationView>();

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
