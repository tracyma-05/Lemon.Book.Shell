using System.ComponentModel;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Lemon.Book.Shell.Infrastructures.Localization;

/// <summary>
/// XAML shorthand for a live-updating localized string: <c>Text="{loc:Localize Cover_Open}"</c>.
/// </summary>
/// <remarks>
/// Returns a binding to an observable of the string rather than the string itself, so switching language refreshes
/// it without reloading the page. No reflection: native AOT and trimmed builds keep working.
/// </remarks>
public sealed class LocalizeExtension : MarkupExtension
{
    public LocalizeExtension()
    {
        Key = string.Empty;
    }

    public LocalizeExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => CreateBinding(Key);

    internal static BindingBase CreateBinding(string key) => new LocalizedString(key).ToBinding();

    /// <summary>The string for a key now, and again each time the language changes.</summary>
    private sealed class LocalizedString(string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            var localization = LocalizationService.Instance;
            observer.OnNext(localization.GetString(key));

            void OnChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(LocalizationService.CurrentCulture))
                {
                    observer.OnNext(localization.GetString(key));
                }
            }

            localization.PropertyChanged += OnChanged;
            return new Unsubscriber(() => localization.PropertyChanged -= OnChanged);
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
