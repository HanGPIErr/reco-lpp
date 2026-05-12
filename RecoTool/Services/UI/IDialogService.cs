using System.Threading.Tasks;

namespace RecoTool.Services.UI
{
    /// <summary>
    /// Encapsulates WPF dialog primitives so that ViewModels can stay free of
    /// any reference to <c>System.Windows.MessageBox</c>, <c>OpenFileDialog</c>,
    /// and friends — making them unit-testable.
    ///
    /// <para>
    /// Production binding: <c>WpfDialogService</c> in the main project (uses real
    /// <c>MessageBox.Show</c>, <c>OpenFileDialog</c>, etc.).
    /// </para>
    ///
    /// <para>
    /// Tests inject a fake that records calls and returns scripted answers.
    /// </para>
    /// </summary>
    public interface IDialogService
    {
        /// <summary>Pops up an OK-only information box.</summary>
        Task ShowInfoAsync(string title, string message);

        /// <summary>Pops up a Yes/No confirmation. Returns true on Yes.</summary>
        Task<bool> ConfirmAsync(string title, string message);

        /// <summary>Pops up an error message.</summary>
        Task ShowErrorAsync(string title, string message);

        /// <summary>
        /// Shows a file-open dialog. Returns the selected path or null if the user cancelled.
        /// </summary>
        /// <param name="filter">Win32 filter string (e.g. "Excel files|*.xlsx;*.xls").</param>
        /// <param name="initialDirectory">Optional starting folder.</param>
        Task<string> OpenFileAsync(string title, string filter, string initialDirectory = null);

        /// <summary>
        /// Shows a multi-file open dialog. Returns the selected paths (empty array on cancel).
        /// </summary>
        Task<string[]> OpenFilesAsync(string title, string filter, string initialDirectory = null);

        /// <summary>
        /// Shows a file-save dialog. Returns the chosen path or null on cancel.
        /// </summary>
        Task<string> SaveFileAsync(string title, string filter, string defaultFileName = null);
    }
}
