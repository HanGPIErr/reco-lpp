using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace RecoTool.Services.UI
{
    /// <summary>
    /// Production binding of <see cref="IDialogService"/> over WPF's
    /// <see cref="MessageBox"/> and <see cref="OpenFileDialog"/> /
    /// <see cref="SaveFileDialog"/>. ViewModels never see these types directly.
    /// </summary>
    public sealed class WpfDialogService : IDialogService
    {
        public Task ShowInfoAsync(string title, string message)
        {
            MessageBox.Show(message ?? string.Empty, title ?? "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            var res = MessageBox.Show(message ?? string.Empty, title ?? "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            return Task.FromResult(res == MessageBoxResult.Yes);
        }

        public Task ShowErrorAsync(string title, string message)
        {
            MessageBox.Show(message ?? string.Empty, title ?? "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return Task.CompletedTask;
        }

        public Task<string> OpenFileAsync(string title, string filter, string initialDirectory = null)
        {
            var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = filter ?? "All files|*.*",
                Multiselect = false,
                CheckFileExists = true,
                CheckPathExists = true,
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory))
                dlg.InitialDirectory = initialDirectory;
            return Task.FromResult(dlg.ShowDialog() == true ? dlg.FileName : null);
        }

        public Task<string[]> OpenFilesAsync(string title, string filter, string initialDirectory = null)
        {
            var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = filter ?? "All files|*.*",
                Multiselect = true,
                CheckFileExists = true,
                CheckPathExists = true,
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory))
                dlg.InitialDirectory = initialDirectory;
            return Task.FromResult(dlg.ShowDialog() == true ? dlg.FileNames : new string[0]);
        }

        public Task<string> SaveFileAsync(string title, string filter, string defaultFileName = null)
        {
            var dlg = new SaveFileDialog
            {
                Title = title,
                Filter = filter ?? "All files|*.*",
                FileName = defaultFileName ?? string.Empty,
                AddExtension = true,
                OverwritePrompt = true,
            };
            return Task.FromResult(dlg.ShowDialog() == true ? dlg.FileName : null);
        }
    }
}
