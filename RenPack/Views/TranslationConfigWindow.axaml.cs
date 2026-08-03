using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RenPack.Localization;
using RenPack.Services.Modding;

namespace RenPack.Views;

/// <summary>Konfigurations-Dialog fuer den Translation-Mod (E6). Der User
/// waehlt eine oder mehrere Zielsprachen und optional die Quellsprache
/// (default: auto-detect). Der Aufrufer (ModGeneratorWindow) fragt danach
/// die KI und uebergibt die fertigen Uebersetzungen dem Generator.</summary>
public partial class TranslationConfigWindow : ChromeWindow
{
    private readonly ObservableCollection<TargetLanguageChoice> _targets = new();

    /// <summary>Wird gesetzt wenn User Apply klickt (auch wenn 0 Sprachen
    /// gewaehlt sind — der Aufrufer prueft dann und zeigt eine Fehlermeldung).
    /// Bleibt <c>null</c> bei Cancel/Close.</summary>
    public TranslationSelection? Result { get; private set; }

    public TranslationConfigWindow()
    {
        InitializeComponent();

        // Zielsprachen: alle bekannten TargetLanguage-Werte als Checkbox.
        foreach (var lang in Enum.GetValues<TargetLanguage>())
            _targets.Add(new TargetLanguageChoice { Language = lang });
        TargetLanguageList.ItemsSource = _targets;
        TargetLanguageList.ItemTemplate = new FuncDataTemplate<TargetLanguageChoice>(
            (choice, _) =>
            {
                var cb = new CheckBox
                {
                    Content = $"{choice.Language.ToNativeName()} ({choice.Language.ToPromptName()})",
                    Margin = new Avalonia.Thickness(0, 2, 0, 2),
                };
                cb.Bind(CheckBox.IsCheckedProperty,
                    new Avalonia.Data.Binding(nameof(TargetLanguageChoice.IsSelected))
                    {
                        Mode = Avalonia.Data.BindingMode.TwoWay,
                    });
                cb.DataContext = choice;
                return cb;
            },
            supportsRecycling: true);

        // Quellsprache: erst "Auto-Detect" + dann alle Sprachen.
        SourceLanguageBox.Items.Add(L.T("Translate_Source_Auto"));
        foreach (var lang in Enum.GetValues<TargetLanguage>())
            SourceLanguageBox.Items.Add($"{lang.ToNativeName()} ({lang.ToPromptName()})");
        SourceLanguageBox.SelectedIndex = 0;

        OkButton.Click += (_, _) => ApplyAndClose();
        CancelButton.Click += (_, _) => { Result = null; Close(); };
    }

    /// <summary>Zeigt Statistik: Anzahl uebersetzbarer Strings + Warnung
    /// wenn's viele sind (KI-Kosten/Zeit).</summary>
    public void SetStats(int stringCount)
    {
        StatsText.Text = L.F("Translate_Stats_Format", stringCount);
    }

    private void ApplyAndClose()
    {
        var selected = _targets.Where(t => t.IsSelected).Select(t => t.Language).ToList();
        TargetLanguage? source = null;
        if (SourceLanguageBox.SelectedIndex > 0)
        {
            var langs = Enum.GetValues<TargetLanguage>();
            source = langs[SourceLanguageBox.SelectedIndex - 1];
        }
        Result = new TranslationSelection(selected, source);
        Close();
    }
}

/// <summary>Zwischen-Format: nur die User-Auswahl aus dem Dialog. Der
/// Aufrufer baut daraus die volle <see cref="TranslationConfig"/> nachdem
/// die KI die Uebersetzungen geliefert hat.</summary>
public sealed record TranslationSelection(
    IReadOnlyList<TargetLanguage> TargetLanguages,
    TargetLanguage? SourceLanguage);

/// <summary>Zeilen-Model fuer die Zielsprachen-Checkboxen-Liste.</summary>
public sealed class TargetLanguageChoice
{
    public TargetLanguage Language { get; set; }
    public bool IsSelected { get; set; }
}
