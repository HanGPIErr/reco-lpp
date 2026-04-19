using System;
using System.Windows.Input;

namespace RecoTool.UI.Mvvm
{
    /// <summary>
    /// Minimal <see cref="ICommand"/> implementation backed by delegates. Fires <see cref="CanExecuteChanged"/>
    /// through the WPF <see cref="CommandManager.RequerySuggested"/> event, which is what XAML Button/MenuItem
    /// bindings already listen to — so commands refresh automatically on UI focus changes.
    /// <para>
    /// Use <see cref="RaiseCanExecuteChanged"/> to force a re-query when CanExecute depends on async state.
    /// </para>
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute == null ? (Predicate<object>)null : _ => canExecute())
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
        }

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object parameter) => _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        /// <summary>
        /// Force-refresh the CanExecute state of every command in the app.
        /// (CommandManager.InvalidateRequerySuggested is coalesced by WPF onto the next frame.)
        /// </summary>
        public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
}
