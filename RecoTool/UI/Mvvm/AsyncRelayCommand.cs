using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RecoTool.UI.Mvvm
{
    /// <summary>
    /// Async-aware <see cref="ICommand"/>. While the backing task is running:
    /// <list type="bullet">
    /// <item>exposes <see cref="IsExecuting"/> (bindable to a spinner / disable UI);</item>
    /// <item>blocks re-entry so spamming a button won't fire the operation twice;</item>
    /// <item>raises <see cref="CanExecuteChanged"/> when the busy state flips.</item>
    /// </list>
    /// Exceptions are captured and optionally forwarded to an error handler so they don't
    /// crash the process (async void would otherwise).
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand, System.ComponentModel.INotifyPropertyChanged
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Predicate<object> _canExecute;
        private readonly Action<Exception> _onException;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool> canExecute = null, Action<Exception> onException = null)
            : this(_ => executeAsync(), canExecute == null ? (Predicate<object>)null : _ => canExecute(), onException)
        {
            if (executeAsync == null) throw new ArgumentNullException(nameof(executeAsync));
        }

        public AsyncRelayCommand(Func<object, Task> executeAsync, Predicate<object> canExecute = null, Action<Exception> onException = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
            _onException = onException;
        }

        public bool IsExecuting
        {
            get => _isExecuting;
            private set
            {
                if (_isExecuting == value) return;
                _isExecuting = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExecuting)));
                RaiseCanExecuteChanged();
            }
        }

        public bool CanExecute(object parameter)
        {
            if (_isExecuting) return false;
            return _canExecute?.Invoke(parameter) ?? true;
        }

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter)) return;
            IsExecuting = true;
            try
            {
                await _executeAsync(parameter).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (_onException != null)
                    _onException(ex);
                else
                    System.Diagnostics.Debug.WriteLine($"[AsyncRelayCommand] Unhandled: {ex}");
            }
            finally
            {
                IsExecuting = false;
            }
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
}
