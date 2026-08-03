using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NLog;
using RenPack.Localization;
using RenPack.Services.Modding;

namespace RenPack.ViewModels;

/// <summary>
/// UI-State fuer den „Knopf-fuer-Dumme"-Mod-Generator: User waehlt den
/// Spiel-Ordner (Root oder <c>game/</c>), klickt einen Button, und die
/// gesamte Pipeline (decompile → analyze → generate → deploy → cleanup)
/// laeuft durch <see cref="OneClickModBuilder"/>.
///
/// Zeigt live den Phase-Status („Dekompiliere script.rpyc..."), am Ende
/// die Ergebnis-Statistik. Wenn im gewaehlten Spiel bereits ein Mod
/// installiert ist (Manifest gefunden), wird zusaetzlich ein
/// „Mod entfernen"-Button freigeschaltet.
/// </summary>
public sealed partial class ModGeneratorViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly OneClickModBuilder _builder = new();

    public IModGeneratorUi? Ui { get; set; }

    // ---- Spiel-Ordner -----------------------------------------------------

    /// <summary>Vom User gewaehlter Ordner — entweder Spiel-Root oder direkt
    /// <c>game/</c>. Der Builder findet das echte <c>game/</c> selbst.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyPropertyChangedFor(nameof(HasInstalledMod))]
    [NotifyPropertyChangedFor(nameof(ResolvedGameDir))]
    [NotifyPropertyChangedFor(nameof(CanLaunchGame))]
    private string _gameFolder = "";

    /// <summary>Der aufgeloeste <c>game/</c>-Pfad (falls User nur den Root
    /// gewaehlt hat) — nur zur Anzeige, damit der User sieht was wir treffen.</summary>
    public string? ResolvedGameDir => string.IsNullOrWhiteSpace(GameFolder)
        ? null
        : OneClickModBuilder.ResolveGameDir(GameFolder);

    /// <summary>True, wenn im gewaehlten Spiel bereits ein KrosteMod-
    /// Manifest liegt (dann Uninstall-Button anzeigen).</summary>
    public bool HasInstalledMod => !string.IsNullOrWhiteSpace(GameFolder)
        && _builder.FindInstalledManifest(GameFolder) is not null;

    // ---- Mod-Typ ----------------------------------------------------------

    public ObservableCollection<ModType> AvailableModTypes { get; } =
    [
        new ModType(ModTypeId.Walkthrough, "Walkthrough"),
        new ModType(ModTypeId.Cheat, "Cheat menu (F11)"),
        new ModType(ModTypeId.Rename, "Character rename"),
        new ModType(ModTypeId.Translate, "Translation (AI)"),
    ];

    [ObservableProperty] private ModType _selectedModType;

    // ---- Status / Progress ------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickFolderCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _progressDetail = "";

    /// <summary>Nach erfolgreichem Build gefuellt — dient als Erfolgsanzeige
    /// und blendet die Statistik-Karte ein.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ResultFilesText))]
    [NotifyPropertyChangedFor(nameof(ResultChoicesText))]
    [NotifyPropertyChangedFor(nameof(ResultDirText))]
    [NotifyPropertyChangedFor(nameof(CanLaunchGame))]
    private OneClickResult? _lastResult;

    public bool HasResult => LastResult is not null;
    public string ResultFilesText => LastResult?.DeployedFileCount.ToString() ?? "";
    public string ResultChoicesText => LastResult?.Analysis.Choices.Count.ToString() ?? "";
    public string ResultDirText => LastResult?.GameDir ?? "";

    public ModGeneratorViewModel()
    {
        _selectedModType = AvailableModTypes[0];
    }

    // ---- Commands ---------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task PickFolderAsync()
    {
        if (Ui is null) return;
        var picked = await Ui.PickFolderAsync(L.T("Mod_PickGame_Title"));
        if (picked is not null) GameFolder = picked;
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        if (Ui is null) return;

        // Vor-Check: gibt schon einen Mod → User warnen.
        if (HasInstalledMod)
        {
            bool proceed = await Ui.ConfirmAsync(
                L.T("Mod_AlreadyInstalled_Title"),
                L.T("Mod_AlreadyInstalled_Body"));
            if (!proceed) return;
        }

        IsBusy = true;
        LastResult = null;
        StatusText = L.T("Mod_Building");
        ProgressDetail = "";

        var pickedType = SelectedModType.Id;
        var pickedFolder = GameFolder;

        var progress = new Progress<OneClickProgress>(p =>
        {
            var phaseText = p.Phase switch
            {
                OneClickPhase.Scanning => L.T("Mod_Phase_Scanning"),
                OneClickPhase.Decompiling => L.F("Mod_Phase_Decompiling_Format", p.Done, p.Total),
                OneClickPhase.Analyzing => L.T("Mod_Phase_Analyzing"),
                OneClickPhase.Generating => L.T("Mod_Phase_Generating"),
                OneClickPhase.Deploying => L.F("Mod_Phase_Deploying_Format", p.Done, p.Total),
                OneClickPhase.Cleaning => L.T("Mod_Phase_Cleaning"),
                _ => "",
            };
            StatusText = phaseText;
            ProgressDetail = p.CurrentFile;
        });

        // Rename-Provider: der Builder ruft das im Background-Thread mitten
        // in der Pipeline auf. Wir dispatchen den Dialog synchron auf den
        // UI-Thread und blocken den Worker bis der User geantwortet hat.
        RenameConfig? RenamePrompt(IReadOnlyList<RpyCharacter> characters,
            IReadOnlyList<RpySayStatement> says)
        {
            if (Ui is null) return null;
            var tcs = new TaskCompletionSource<RenameConfig?>();
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var cfg = await Ui.PromptRenameMappingsAsync(characters, says);
                    tcs.TrySetResult(cfg);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task.GetAwaiter().GetResult();
        }

        // Translation-Provider: analog zu RenamePrompt. Die UI-Impl fragt
        // Zielsprachen ab, ruft die KI und liefert die fertigen Uebersetzungen.
        TranslationConfig? TranslationPrompt(ModAnalysis analysis)
        {
            if (Ui is null) return null;
            var tcs = new TaskCompletionSource<TranslationConfig?>();
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var cfg = await Ui.PromptTranslationConfigAsync(analysis);
                    tcs.TrySetResult(cfg);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task.GetAwaiter().GetResult();
        }

        try
        {
            var result = await Task.Run(() =>
                _builder.Build(pickedFolder, pickedType, progress, default,
                    RenamePrompt, TranslationPrompt));
            LastResult = result;
            StatusText = L.F("Mod_BuildDoneFormat", result.DeployedFileCount, result.GameDir);
            ProgressDetail = "";
            OnPropertyChanged(nameof(HasInstalledMod));

            await Ui.ShowMessageAsync(
                L.T("Mod_BuildDone_Title"),
                L.F("Mod_BuildDone_Body_Format",
                    result.DeployedFileCount,
                    result.Analysis.Choices.Count,
                    result.GameDir));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "One-Click-Mod-Build fehlgeschlagen");
            StatusText = L.F("Mod_BuildFailedFormat", ex.Message);
            ProgressDetail = "";
            await Ui.ShowMessageAsync(L.T("Mod_BuildFailed_Title"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (Ui is null) return;

        bool confirmed = await Ui.ConfirmAsync(
            L.T("Mod_Uninstall_Confirm_Title"),
            L.T("Mod_Uninstall_Confirm_Body"));
        if (!confirmed) return;

        IsBusy = true;
        StatusText = L.T("Mod_Uninstalling");
        ProgressDetail = "";

        var pickedFolder = GameFolder;
        try
        {
            var result = await Task.Run(() =>
            {
                var gameDir = OneClickModBuilder.ResolveGameDir(pickedFolder)
                    ?? throw new DirectoryNotFoundException(pickedFolder);
                return _builder.Uninstall(gameDir);
            });
            StatusText = L.F("Mod_UninstallDoneFormat",
                result.RemovedFiles, result.RestoredBackups);
            LastResult = null;
            OnPropertyChanged(nameof(HasInstalledMod));
            await Ui.ShowMessageAsync(
                L.T("Mod_UninstallDone_Title"),
                L.F("Mod_UninstallDone_Body_Format",
                    result.RemovedFiles, result.RestoredBackups));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mod-Uninstall fehlgeschlagen");
            StatusText = L.F("Mod_BuildFailedFormat", ex.Message);
            await Ui.ShowMessageAsync(L.T("Mod_UninstallFailed_Title"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    private bool CanInteract() => !IsBusy;
    private bool CanBuild() => !IsBusy && !string.IsNullOrWhiteSpace(GameFolder);
    private bool CanUninstall() => !IsBusy && HasInstalledMod;

    /// <summary>„▶ Spiel starten" — sucht das ausfuehrbare im Spiel-Root
    /// (nicht <c>game/</c>!) und oeffnet es via <c>Process.Start</c> mit
    /// <c>UseShellExecute</c>. Ren'Py-Games liefern typischerweise:
    /// <list type="bullet">
    ///   <item>Linux: <c>&lt;GameName&gt;.sh</c> (Shell-Launcher)</item>
    ///   <item>Windows: <c>&lt;GameName&gt;.exe</c></item>
    ///   <item>macOS: <c>&lt;GameName&gt;.app/</c> — als Ordner oeffnet
    ///     Finder das Bundle</item>
    /// </list>
    /// Nur sichtbar wenn HasResult (also gerade erfolgreich installiert)
    /// ODER HasInstalledMod (Manifest liegt bereits im Ordner). </summary>
    public bool CanLaunchGame => (HasResult || HasInstalledMod)
        && !string.IsNullOrWhiteSpace(GameFolder)
        && FindGameLauncher() is not null;

    [RelayCommand]
    private void LaunchGame()
    {
        var launcher = FindGameLauncher();
        if (launcher is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true });
            Log.Info("Spiel gestartet: {path}", launcher);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Spiel-Start fehlgeschlagen: {path}", launcher);
        }
    }

    private string? FindGameLauncher()
    {
        var gameDir = OneClickModBuilder.ResolveGameDir(GameFolder);
        if (gameDir is null) return null;
        // gameDir zeigt auf .../game/, wir wollen den PARENT (Spiel-Root).
        var root = Path.GetDirectoryName(gameDir);
        if (root is null || !Directory.Exists(root)) return null;

        // Priorisierung: aktuelle Plattform zuerst, dann Fallbacks.
        string[] preferredExts = OperatingSystem.IsWindows()
            ? [".exe", ".sh", ".py"]
            : OperatingSystem.IsMacOS()
                ? [".app", ".sh", ".py"]
                : [".sh", ".py", ".exe"];

        foreach (var ext in preferredExts)
        {
            var match = Directory.EnumerateFileSystemEntries(root, "*" + ext)
                .Where(p => !Path.GetFileName(p).StartsWith('_')) // _venv/_python skippen
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
                .FirstOrDefault();
            if (match is not null) return match;
        }
        return null;
    }
}

public interface IModGeneratorUi
{
    Task<string?> PickFolderAsync(string title);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
    /// <summary>Zeigt einen Dialog zum Eingeben der Rename-Mappings.
    /// Der User sieht die vom Analyzer erkannten Character und kann pro
    /// Character einen neuen Anzeigenamen eintragen. Leere Zeilen =
    /// unveraendert. Rueckgabe <c>null</c> = Cancel → Build abbrechen.
    ///
    /// Optionaler <paramref name="sayStatements"/>-Parameter: wenn User im
    /// Rename-Dialog die „auch Body-Text umschreiben (KI)"-Checkbox
    /// aktiviert, holt sich die UI-Impl den KrosteAiRewriter und praesen-
    /// tiert einen Preview-Dialog. Die akzeptierten Edits landen in
    /// <see cref="RenameConfig.BodyTextEdits"/>.</summary>
    Task<RenameConfig?> PromptRenameMappingsAsync(
        IReadOnlyList<RpyCharacter> characters,
        IReadOnlyList<RpySayStatement> sayStatements);

    /// <summary>E6: zeigt Dialog zum Auswaehlen der Zielsprachen fuer den
    /// Translation-Mod. Nach der Auswahl ruft die UI-Impl den KI-Provider
    /// mit den zu uebersetzenden Strings und liefert die fertige
    /// <see cref="TranslationConfig"/> mit den akzeptierten Uebersetzungen
    /// zurueck. <c>null</c> = Cancel.</summary>
    Task<TranslationConfig?> PromptTranslationConfigAsync(ModAnalysis analysis);
}

public sealed record ModType(ModTypeId Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
