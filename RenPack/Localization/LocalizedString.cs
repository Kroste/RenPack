using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RenPack.Localization;

/// <summary>
/// Bindbarer Wrapper um einen einzelnen Localization-Key. Wird von
/// <see cref="TrExtension"/> erzeugt und im XAML per Binding an
/// <see cref="Value"/> konsumiert.
///
/// **Warum dieser Umweg?** Ein direktes Binding gegen den Indexer
/// <c>LocalizationService.Instance[Key]</c> braucht eine
/// <c>PropertyChanged("Item[]")</c>-Notification (WPF-Konvention) —
/// und die wird von Avalonia 12 nur unzuverlaessig verarbeitet:
/// Bindings in Fenstern, die gerade nicht den Fokus haben, bleiben
/// stale, bis das Fenster neu geladen wird. Das MainWindow uebernahm
/// die Sprachwahl aus dem SettingsWindow deshalb erst nach Neustart.
///
/// Der Wrapper hat eine ganz normale Property <see cref="Value"/> mit
/// regulaerem <c>PropertyChanged</c>-Notify — das versteht Avalonia
/// zuverlaessig. Beim Culture-Wechsel wird zentral
/// <see cref="NotifyAllChanged"/> aufgerufen; alle noch lebenden
/// Wrapper melden ihren <see cref="Value"/> als geaendert und alle
/// aktiven Bindings refreshen sofort in allen Fenstern.
///
/// **GC-Sicherheit:** die Registry haelt nur <see cref="WeakReference"/>s
/// auf die Wrapper — solange ein Fenster mit einem <c>{loc:Tr Key}</c>-
/// Binding existiert, haelt das Binding die Referenz stark. Sobald das
/// Fenster geschlossen wird, kann der Wrapper GC'd werden; beim naechsten
/// <see cref="NotifyAllChanged"/> raeumt die Registry tote Eintraege
/// automatisch auf.
/// </summary>
public sealed class LocalizedString : INotifyPropertyChanged
{
    public string Key { get; }
    public string Value => LocalizationService.Instance[Key];

    private static readonly List<WeakReference<LocalizedString>> _all = new();
    private static readonly object _lock = new();

    public LocalizedString(string key)
    {
        Key = key;
        lock (_lock) _all.Add(new WeakReference<LocalizedString>(this));
    }

    /// <summary>Feuert <c>PropertyChanged(nameof(Value))</c> auf jedem
    /// lebenden Wrapper und entsorgt tote WeakReferences.</summary>
    internal static void NotifyAllChanged()
    {
        lock (_lock)
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                if (_all[i].TryGetTarget(out var s))
                    s.OnPropertyChanged(nameof(Value));
                else
                    _all.RemoveAt(i);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
