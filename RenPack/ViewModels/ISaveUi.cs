namespace RenPack.ViewModels;

/// <summary>UI-Brücke für das Save-Fenster.</summary>
public interface ISaveUi
{
    Task<string?> PickOpenSaveAsync();
    Task<string?> PickSaveTargetAsync(string suggestedName);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
}
