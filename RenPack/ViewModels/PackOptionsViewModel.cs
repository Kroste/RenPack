using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>ViewModel fuer das kleine Pack-Optionen-Dialogfenster, das
/// vor jedem "Ordner packen" die Format-Version und den Verschluesselungs-
/// Key erfragt. Default: RPA-3.0 + <see cref="RenpyArchiveService.DefaultKey"/>.</summary>
public sealed partial class PackOptionsViewModel : ObservableObject
{
    public IPackOptionsUi? Ui { get; set; }

    public ObservableCollection<RpaVersion> Formats { get; } =
        [RpaVersion.V3_0, RpaVersion.V3_2, RpaVersion.V2_0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsKey))]
    private RpaVersion _selectedFormat = RpaVersion.V3_0;

    /// <summary>Key als Hex-String ohne "0x"-Praefix. Wird beim OK
    /// per <see cref="uint.TryParse(string?, NumberStyles, IFormatProvider?, out uint)"/>
    /// interpretiert.</summary>
    [ObservableProperty] private string _keyHex =
        RenpyArchiveService.DefaultKey.ToString("x8", CultureInfo.InvariantCulture);

    [ObservableProperty] private string _keyError = "";

    /// <summary>RPA-2.0 hat kein Key-Feld (kein XOR). Bei V2_0 blenden
    /// wir das Feld aus.</summary>
    public bool NeedsKey => SelectedFormat != RpaVersion.V2_0;

    /// <summary>Ergebnis nach OK: Format + geparster Key.</summary>
    public RpaVersion ResultFormat { get; private set; }
    public uint ResultKey { get; private set; }

    [RelayCommand]
    private void ResetKey() =>
        KeyHex = RenpyArchiveService.DefaultKey.ToString("x8", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void Ok()
    {
        KeyError = "";
        uint key = 0;
        if (NeedsKey)
        {
            var input = (KeyHex ?? "").Trim();
            if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) input = input[2..];
            if (!uint.TryParse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key))
            {
                KeyError = Localization.L.T("Pack_InvalidKey");
                return;
            }
        }
        ResultFormat = SelectedFormat;
        ResultKey = key;
        Ui?.Close(confirmed: true);
    }

    [RelayCommand]
    private void Cancel() => Ui?.Close(confirmed: false);
}

public interface IPackOptionsUi
{
    void Close(bool confirmed);
}
