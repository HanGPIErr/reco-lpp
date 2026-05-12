using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RecoTool.Infrastructure.Health;

namespace RecoTool.Windows
{
    /// <summary>
    /// Modal dialog shown at startup when one or more <see cref="IStartupHealthCheck"/>
    /// implementations report failure. Lists each failure with its message and a short
    /// exception summary, and offers two outcomes:
    /// <list type="bullet">
    ///   <item><b>Continue</b> — close the dialog and let the app start anyway
    ///         (the user might still be able to work in offline mode).</item>
    ///   <item><b>Exit</b> — close the dialog and shut down the app.</item>
    /// </list>
    /// </summary>
    public partial class HealthCheckDialog : Window
    {
        /// <summary>True when the user clicked Continue. False (default) when they clicked Exit or closed the window.</summary>
        public bool UserChoseContinue { get; private set; }

        /// <summary>
        /// Single row in <see cref="FailuresList"/> — exposed as a public type so the
        /// XAML data template can bind to it.
        /// </summary>
        public sealed class FailureRow
        {
            public string Name { get; }
            public string Message { get; }
            public string ExceptionSummary { get; }
            public bool HasException => !string.IsNullOrWhiteSpace(ExceptionSummary);

            public FailureRow(string name, string message, Exception ex)
            {
                Name = name ?? "unknown";
                Message = message ?? string.Empty;
                ExceptionSummary = SummarizeException(ex);
            }

            private static string SummarizeException(Exception ex)
            {
                if (ex == null) return null;
                try
                {
                    // Keep it short — type + message. The full stack lands in Serilog.
                    var msg = ex.Message ?? string.Empty;
                    if (msg.Length > 240) msg = msg.Substring(0, 240) + "…";
                    return ex.GetType().Name + ": " + msg;
                }
                catch
                {
                    return null;
                }
            }
        }

        public HealthCheckDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Convenience constructor: builds the dialog and binds the list directly from
        /// the runner output. Use this from <c>App.xaml.cs</c>.
        /// </summary>
        public HealthCheckDialog(IEnumerable<(string Name, HealthCheckResult Result)> failures) : this()
        {
            if (failures == null) throw new ArgumentNullException(nameof(failures));
            SetFailures(failures);
        }

        /// <summary>
        /// Populates the dialog with the given failures. Public so the WPF designer
        /// can default-construct the dialog and tests can poke a list in.
        /// </summary>
        public void SetFailures(IEnumerable<(string Name, HealthCheckResult Result)> failures)
        {
            if (failures == null) throw new ArgumentNullException(nameof(failures));
            var rows = failures
                .Where(t => t.Result != null && !t.Result.IsHealthy)
                .Select(t => new FailureRow(t.Name, t.Result.Message, t.Result.Exception))
                .ToList();
            FailuresList.ItemsSource = rows;
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            UserChoseContinue = true;
            DialogResult = true;
            Close();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            UserChoseContinue = false;
            DialogResult = false;
            Close();
        }
    }
}
