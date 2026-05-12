using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// Minimal <see cref="ICommand"/> implementation for binding ViewModel actions
    /// to WPF buttons / menu items. Sync version.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(WrapAction(execute), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        // Validates the no-arg overload's argument BEFORE the chained ctor runs.
        // (Constructor chaining is evaluated first, so a lambda wrapping a null
        // delegate would otherwise reach the chained ctor without tripping its
        // null check.)
        private static Action<object> WrapAction(Action execute)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            return _ => execute();
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object parameter) => _execute(parameter);

        /// <summary>Forces a CanExecute re-evaluation.</summary>
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Async-aware <see cref="ICommand"/>. Tracks an "is executing" flag to disable
    /// the bound control while the async operation runs (prevents double-clicks).
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Func<object, bool> _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute = null)
            : this(WrapFunc(execute), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        public AsyncRelayCommand(Func<object, Task> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        private static Func<object, Task> WrapFunc(Func<Task> execute)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            return _ => execute();
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool IsExecuting => _isExecuting;

        public bool CanExecute(object parameter)
            => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object parameter) => await ExecuteAsync(parameter);

        /// <summary>Awaitable version, used by tests.</summary>
        public async Task ExecuteAsync(object parameter)
        {
            if (!CanExecute(parameter)) return;

            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            try { await _execute(parameter); }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
