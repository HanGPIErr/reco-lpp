using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// Base class for all ViewModels. Provides INotifyPropertyChanged plumbing
    /// + a <see cref="SetField"/> helper that raises change notifications only
    /// when the value actually changes.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raises <see cref="PropertyChanged"/> for the given member.</summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Assigns <paramref name="newValue"/> to <paramref name="field"/> and raises
        /// <see cref="PropertyChanged"/> only when the value differs (avoids cascade
        /// of WPF re-bindings on every set).
        /// </summary>
        /// <returns>True when the value changed (and notification was raised).</returns>
        protected bool SetField<T>(ref T field, T newValue, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, newValue)) return false;
            field = newValue;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
