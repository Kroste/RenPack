namespace RenPack.ViewModels;

/// <summary>
/// Brücke vom ViewModel zu plattformabhängigen UI-Diensten (Datei-/Ordnerdialoge,
/// Meldungen). Die View (MainWindow) implementiert das via StorageProvider + MessageBox,
/// sodass das ViewModel testbar und UI-frei bleibt.
/// </summary>
public interface IUiInteractions
{
    Task<string?> PickOpenArchiveAsync();
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickSaveArchiveAsync(string suggestedName);
    Task<string?> PickSaveArchiveOrFileAsync(string suggestedName);
    Task<PackOptionsViewModel?> AskPackOptionsAsync();
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
    Task CopyToClipboardAsync(string text);
}
