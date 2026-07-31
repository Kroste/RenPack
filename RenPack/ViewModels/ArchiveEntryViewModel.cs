using CommunityToolkit.Mvvm.ComponentModel;
using RenPack.Services;

namespace RenPack.ViewModels;

/// <summary>Ein Eintrag der Archiv-Dateiliste (mit Auswahlhäkchen für gezieltes Extrahieren).</summary>
public sealed partial class ArchiveEntryViewModel : ObservableObject
{
    public RpaEntry Entry { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ArchiveEntryViewModel(RpaEntry entry) => Entry = entry;

    public string Path => Entry.Path;
    public long Size => Entry.Size;
    public string SizeDisplay => FormatSize(Entry.Size);

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
