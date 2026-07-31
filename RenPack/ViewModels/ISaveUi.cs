namespace RenPack.ViewModels;

/// <summary>UI-Brücke für das Save-Fenster (bewusst minimal — Save-Inspector
/// braucht nur Datei öffnen + Meldung).</summary>
public interface ISaveUi
{
    Task<string?> PickOpenSaveAsync();
    Task ShowMessageAsync(string title, string message);
}
