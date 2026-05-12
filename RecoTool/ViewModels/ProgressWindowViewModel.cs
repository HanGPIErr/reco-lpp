using System;
using System.Threading;
using System.Windows.Input;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// ViewModel pour <c>ProgressWindow.xaml</c>. Petit dialog non-modal qui affiche
    /// la progression d'une opération longue ; le caller peut annuler via
    /// <see cref="CancellationToken"/>.
    /// </summary>
    public sealed class ProgressWindowViewModel : ViewModelBase
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private string _title = "Operation in progress…";
        private string _message;
        private int _progressPercent;
        private bool _isIndeterminate = true;
        private bool _canCancel = true;
        private bool _isClosing;

        public ProgressWindowViewModel(string title = null, bool canCancel = true)
        {
            if (!string.IsNullOrWhiteSpace(title)) _title = title;
            _canCancel = canCancel;
            CancelCommand = new RelayCommand(Cancel, () => CanCancel && !_isClosing);
        }

        // ── Properties ──

        public string Title { get => _title; set => SetField(ref _title, value); }

        public string Message { get => _message; set => SetField(ref _message, value); }

        public int ProgressPercent
        {
            get => _progressPercent;
            set
            {
                var clamped = Math.Max(0, Math.Min(100, value));
                if (SetField(ref _progressPercent, clamped))
                {
                    // As soon as a real value arrives, switch to determinate mode.
                    if (_isIndeterminate) IsIndeterminate = false;
                }
            }
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => SetField(ref _isIndeterminate, value);
        }

        public bool CanCancel
        {
            get => _canCancel;
            set => SetField(ref _canCancel, value);
        }

        /// <summary>Cancellation token to pass to the long-running operation.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        // ── Commands ──

        public ICommand CancelCommand { get; }

        // ── Events ──

        /// <summary>Fired when the user clicks Cancel.</summary>
        public event EventHandler CancelRequested;

        /// <summary>Fired when the operation completes (success/error/cancel). View closes itself.</summary>
        public event EventHandler<bool> CloseRequested;

        // ── Operations ──

        /// <summary>Updates message + percent in one shot from the worker.</summary>
        public void Report(string message, int percent)
        {
            Message = message;
            ProgressPercent = percent;
        }

        /// <summary>Called by the caller when the work is done.</summary>
        public void Complete(bool success)
        {
            _isClosing = true;
            CloseRequested?.Invoke(this, success);
        }

        private void Cancel()
        {
            try { _cts.Cancel(); } catch { }
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
