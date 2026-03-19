using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;   // only for MessageBox
using System.Windows.Threading;

namespace RecoTool.API
{
    /// <summary>
    /// Performs the Smart‑Card login in a **modal** WebView2 dialog.
    /// The public <c>AuthenticateAsync</c> method returns **only after**
    /// the dialog has been closed (success, cancel or timeout).
    /// </summary>
    internal class FreeAuth : IDisposable
    {
        private const string BASE_URL = "https://free.group.echonet";
        private readonly CookieContainer _cookieContainer;

        // The thread that owns the WPF dispatcher.
        private Thread? _uiThread;        // Created once, lives for the lifetime of the app.
        private Dispatcher? _dispatcher;

        // Completion source that tells the caller whether login succeeded.
        private TaskCompletionSource<bool> _tcs = null!;

        // Optional timeout for the whole login flow.
        private static readonly TimeSpan AUTH_TIMEOUT = TimeSpan.FromMinutes(2);

        public FreeAuth(CookieContainer cookieContainer)
        {
            _cookieContainer = cookieContainer ?? throw new ArgumentNullException(nameof(cookieContainer));
        }

        /// <summary>
        /// Starts the STA thread (if it does not exist yet) and shows the modal dialog.
        /// The method completes only when the user finishes the login,
        /// cancels it, or the timeout expires.
        /// </summary>
        public async Task<bool> AuthenticateAsync()
        {
            // -----------------------------------------------------------------
            // 0️⃣  Make sure we have a UI thread with a Dispatcher.
            // -----------------------------------------------------------------
            EnsureUiThread();

            // -----------------------------------------------------------------
            // 1️⃣  Prepare the completion source – it will be set from the UI thread.
            // -----------------------------------------------------------------
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // -----------------------------------------------------------------
            // 2️⃣  Marshal *all* UI creation + ShowDialog onto the UI dispatcher.
            // -----------------------------------------------------------------
            await _dispatcher!.InvokeAsync(() =>
            {
                // -------------------------------------------------------------
                // 2a️⃣  Build the Window and embed a WebView2 control.
                // -------------------------------------------------------------
                var window = new Window
                {
                    Title = "Free – Smart‑Card authentication",
                    Width = 860,
                    Height = 660,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = false,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.ToolWindow
                };

                var webView = new WebView2();
                window.Content = webView;

                // -------------------------------------------------------------
                // 2b️⃣  Cancel handling – user clicks the X (or Alt‑F4).
                // -------------------------------------------------------------
                window.Closing += (_, __) =>
                {
                    // If the task has not yet been completed we treat the
                    // close as a cancellation.
                    if (!_tcs.Task.IsCompleted)
                        _tcs.TrySetResult(false);
                };

                // -----------------------------------------------------------------
                // 2c️⃣  When navigation finishes we collect the cookies.
                // -----------------------------------------------------------------
                webView.NavigationCompleted += async (s, e) =>
                {
                    if (!e.IsSuccess) return;

                    // Still on the login page ? → keep waiting.
                    if (((WebView2)s).Source.AbsolutePath.Contains("/front/login"))
                        return;

                    // -------------------------------------------------------------
                    // Retrieve **all** cookies for the domain.
                    // -------------------------------------------------------------
                    var cookies = await ((WebView2)s).CoreWebView2.CookieManager
                                                      .GetCookiesAsync(string.Empty);
                    foreach (var c in cookies)
                    {
                        _cookieContainer.Add(new Cookie(c.Name, c.Value, "/", "free.group.echonet"));
                    }

                    // If at least one cookie is present we consider the login successful.
                    if (_cookieContainer.GetCookies(new Uri(BASE_URL)).Count > 0)
                    {
                        // *** FIRST: mark the task as successful ***
                        if (!_tcs.Task.IsCompleted)
                            _tcs.TrySetResult(true);

                        // *** THEN close the window ***
                        // (still on the UI thread, so we can call Close directly)
                        window.Close();
                    }
                    // else: no cookie – keep the dialog open, the user may try again.
                };

                // -------------------------------------------------------------
                // 2d️⃣  Initialise WebView2 and navigate to the login page.
                // -------------------------------------------------------------
                window.Loaded += async (_, __) =>
                {
                    try
                    {
                        // Isolate this dialog – a temporary user‑data folder.
                        string userDataFolder = Path.Combine(
                            Path.GetTempPath(),
                            $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}_FreeAuth_{Guid.NewGuid()}");

                        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                        await webView.EnsureCoreWebView2Async(env);

                        // Navigate to the Free login page.
                        webView.Source = new Uri($"{BASE_URL}/front/login");
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(
                            $"Erreur d’initialisation du composant WebView2 :\n{ex.Message}",
                            "Free – Authentification", MessageBoxButton.OK, MessageBoxImage.Error);

                        // If we cannot initialise the control we treat it as a cancellation.
                        _tcs.TrySetResult(false);
                    }
                };

                // -------------------------------------------------------------
                // 2e️⃣  Show the dialog **modally** – this blocks the UI thread
                //      until the window is closed (or the TCS is completed).
                // -------------------------------------------------------------
                window.ShowDialog();   // modal, UI thread stays responsive
            });

            // -----------------------------------------------------------------
            // 3️⃣  Wait for either the authentication result or the timeout.
            // -----------------------------------------------------------------
            var finished = await Task.WhenAny(_tcs.Task, Task.Delay(AUTH_TIMEOUT));

            // If the timeout fired **or** the task completed with `false` → authentication failed.
            if (finished != _tcs.Task || !_tcs.Task.Result)
                return false;

            // Success – we have at least one cookie.
            return true;
        }

        // -----------------------------------------------------------------
        // 4️⃣  Ensure a dedicated STA thread with a Dispatcher exists.
        // -----------------------------------------------------------------
        private void EnsureUiThread()
        {
            // If the thread already exists and the dispatcher is running – nothing to do.
            if (_uiThread != null && _uiThread.IsAlive && _dispatcher != null)
                return;

            // -----------------------------------------------------------------
            // 4a️⃣  Create a new STA thread that starts a Dispatcher loop.
            // -----------------------------------------------------------------
            var ready = new ManualResetEventSlim(false);
            _uiThread = new Thread(() =>
            {
                // Every WPF thread needs a Dispatcher.
                _dispatcher = Dispatcher.CurrentDispatcher;

                // Signal to the calling thread that the dispatcher is ready.
                ready.Set();

                // Start the WPF message loop – this call blocks until
                // Dispatcher.InvokeShutdown() is called (we never call it;
                // the thread lives for the whole app lifetime).
                Dispatcher.Run();
            });

            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;   // does not block process exit
            _uiThread.Start();

            // Wait until the dispatcher is ready before we return.
            ready.Wait();
        }

        // -----------------------------------------------------------------
        // 5️⃣  Helper – raw cookie header (kept for compatibility)
        // -----------------------------------------------------------------
        public string GetStoredCookies()
        {
            return _cookieContainer.GetCookieHeader(new Uri(BASE_URL));
        }

        // -----------------------------------------------------------------
        // 6️⃣  IDisposable – shut down the UI thread cleanly.
        // -----------------------------------------------------------------
        public void Dispose()
        {
            // If the dialog is still open (rare, e.g. the user closed the window
            // very quickly before the TCS was set) we ask the dispatcher to shut
            // down – this will close any remaining window.
            try { _dispatcher?.InvokeShutdown(); } catch { }

            // The UI thread is a background thread, so we do not need to
            // Join it; it will be terminated automatically when the process exits.
        }
    }
}