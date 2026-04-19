using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RecoTool.UI.Mvvm
{
    /// <summary>
    /// Minimal, allocation-friendly base class for view models and bindable models.
    /// <para>
    /// Usage:
    /// <code>
    /// private string _name;
    /// public string Name
    /// {
    ///     get =&gt; _name;
    ///     set =&gt; Set(ref _name, value);
    /// }
    /// </code>
    /// </para>
    /// Intentionally kept small (no MVVM Toolkit dependency) so it drops into the existing
    /// .NET Framework 4.8 codebase without extra NuGet packages.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises <see cref="PropertyChanged"/> for the given property name.
        /// <see cref="CallerMemberNameAttribute"/> lets you call <c>OnPropertyChanged()</c>
        /// with no argument from inside a property setter.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Raises <see cref="PropertyChanged"/> for several properties in one call.
        /// Useful when a single operation affects multiple computed properties.
        /// </summary>
        protected void OnPropertiesChanged(params string[] propertyNames)
        {
            if (propertyNames == null) return;
            var handler = PropertyChanged;
            if (handler == null) return;
            foreach (var name in propertyNames)
                handler(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>
        /// Sets a backing field and raises <see cref="PropertyChanged"/> only if the value actually changed
        /// (using <see cref="EqualityComparer{T}.Default"/>). Returns true when a change occurred, false otherwise.
        /// </summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Same as <see cref="Set{T}(ref T, T, string)"/> but additionally invokes a callback
        /// after the change — convenient to notify dependent computed properties in one line.
        /// </summary>
        protected bool Set<T>(ref T field, T value, System.Action onChanged, [CallerMemberName] string propertyName = null)
        {
            if (!Set(ref field, value, propertyName)) return false;
            onChanged?.Invoke();
            return true;
        }
    }
}
