using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using RenPack.Localization;
using RenPack.Services;
using RenPack.ViewModels;

namespace RenPack.Views;

public partial class MainWindow : ChromeWindow, IUiInteractions
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public MainWindow()
    {
        InitializeComponent();
        AboutButton.Click += OnAbout;
        ToastAboutButton.Click += OnAbout;
        OpenSaveButton.Click += OnOpenSave;
        SettingsButton.Click += OnSettings;
        DecompileFileButton.Click += OnDecompileFiles;
        DecompileFolderButton.Click += OnDecompileFolder;

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        // Ctrl+F fokussiert die Filter-TextBox. KeyBinding im XAML geht
        // hier nicht sauber, weil wir das Ziel (FilterBox) referenzieren
        // muessen — deshalb im Code-behind ueber Preview-Tunneling.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Doppelklick auf einen Datei-Eintrag → einzelne Datei
    /// extrahieren.</summary>
    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ExtractHighlightedCommand.CanExecute(null))
            vm.ExtractHighlightedCommand.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Ui = this;
            vm.DecompileFolderRequested += async path => await RunFolderDecompileAsync(path);
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Dezenter Auto-Update-Check beim Start (nicht blockierend, Fehler nur geloggt).
        try
        {
            var update = App.Services.GetRequiredService<UpdateService>();
            var result = await update.CheckForUpdateAsync();
            if (result.UpdateAvailable && DataContext is MainWindowViewModel vm)
                vm.UpdateToast = L.F("Update_AvailableFormat", result.LatestVersion ?? "?");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Auto-Update-Check beim Start fehlgeschlagen");
        }
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        try
        {
            var update = App.Services.GetRequiredService<UpdateService>();
            await new AboutWindow(update).ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Über-Fenster konnte nicht geöffnet werden");
        }
    }

    private async void OnOpenSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = App.Services.GetRequiredService<SaveWindowViewModel>();
            var win = new SaveWindow { DataContext = vm };
            await win.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save-Fenster konnte nicht geöffnet werden");
        }
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            var vm = App.Services.GetRequiredService<SettingsWindowViewModel>();
            var win = new SettingsWindow { DataContext = vm };
            await win.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Einstellungs-Fenster konnte nicht geöffnet werden");
        }
    }

    // ---- .rpyc-Dekompilierung ----------------------------------------------

    private async void OnDecompileFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Ren'Py-Skripte dekompilieren",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Ren'Py-Skripte (*.rpyc)") { Patterns = ["*.rpyc"] },
                    new FilePickerFileType("Alle Dateien") { Patterns = ["*"] },
                ],
            });
            if (picked.Count == 0) return;

            var batch = App.Services.GetRequiredService<RpycBatchService>();
            SetBusy(true, $"Dekompiliere {picked.Count} Datei(en) …");
            int ok = 0, failed = 0;
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var f in picked)
                {
                    var path = f.TryGetLocalPath();
                    if (path is null) { failed++; continue; }
                    try { batch.DecompileFile(path); ok++; }
                    catch (Exception ex) { failed++; errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
                }
            });
            SetBusy(false, $"Fertig: {ok} erfolgreich, {failed} fehlgeschlagen.");
            var msg = failed == 0
                ? $"{ok} Datei(en) dekompiliert. Ergebnis liegt neben der Original-.rpyc als .rpy."
                : $"{ok} erfolgreich, {failed} fehlgeschlagen:\n\n" + string.Join("\n", errors.Take(10));
            await MessageBox.ShowAsync(this, "Dekompilieren fertig", msg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Dekompilieren-Datei fehlgeschlagen");
            SetBusy(false, "Fehler beim Dekompilieren.");
        }
    }

    private async void OnDecompileFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L.T("Decompile_PickFolderTitle"),
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;
            var root = folders[0].TryGetLocalPath();
            if (root is null) return;
            await RunFolderDecompileAsync(root);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ordner-Dekompilierung fehlgeschlagen");
            SetBusy(false, L.T("Decompile_FolderFailed"));
        }
    }

    /// <summary>Ruft die eigentliche Batch-Dekompilierung fuer einen
    /// gewaehlten Ordner auf. Wird sowohl vom Ordner-Picker als auch
    /// vom Recent-Dropdown genutzt.</summary>
    private async Task RunFolderDecompileAsync(string root)
    {
        try
        {
            (DataContext as MainWindowViewModel)?.RecentService?.AddDecompileFolder(root);

            // Bestehende .rpy neben .rpyc? Dann "nur neuere"-Modus anbieten,
            // das spart bei Re-Runs Minuten.
            bool skipUpToDate = await MessageBox.ShowAsync(this,
                L.T("Decompile_SkipUpToDate_Title"),
                L.T("Decompile_SkipUpToDate_Body"),
                showCancel: true);

            var batch = App.Services.GetRequiredService<RpycBatchService>();
            SetBusy(true, L.T("Decompile_Scanning"));
            var progress = new Progress<(int done, int total, string current)>(p =>
                SetBusy(true, L.F("Decompile_ProgressFormat", p.done, p.total, Path.GetFileName(p.current))));
            var result = await Task.Run(() => batch.DecompileDirectory(root, progress, skipUpToDate));
            SetBusy(false, L.F("Decompile_DoneStatusFormat",
                result.Succeeded, result.Total, result.Failed, result.Skipped));
            var msg = result.Failed == 0
                ? L.F("Decompile_DoneMsgFormat",
                    result.Succeeded, result.Total, result.Skipped, root)
                : L.F("Decompile_DoneWithErrorsFormat",
                    result.Succeeded, result.Total, result.Failed, result.Skipped) + "\n\n"
                    + string.Join("\n", result.Errors.Take(10).Select(x => $"{Path.GetFileName(x.File)}: {x.Error}"));
            await MessageBox.ShowAsync(this, L.T("Decompile_DoneTitle"), msg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ordner-Dekompilierung fehlgeschlagen");
            SetBusy(false, L.T("Decompile_FolderFailed"));
        }
    }

    private void SetBusy(bool busy, string status)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IsBusy = busy;
        vm.ProgressIndeterminate = busy;
        vm.StatusText = status;
    }

    // ---- Drag & Drop: Archiv fallen lassen zum Öffnen -----------------------

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToList();
        var first = files?.FirstOrDefault();
        string? path = first?.TryGetLocalPath();
        if (path is null) return;

        // Nach Extension weiterleiten: .rpa in die eigene Archiv-Anzeige,
        // .save direkt in den Save-Editor, .rpyc als Ein-Datei-Decompile.
        // Mehrere .rpyc: Batch.
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".save")
        {
            var svm = App.Services.GetRequiredService<SaveWindowViewModel>();
            var win = new SaveWindow { DataContext = svm };
            _ = svm.LoadSaveAsync(path); // laeuft parallel zum ShowDialog
            await win.ShowDialog(this);
            return;
        }
        if (ext == ".rpyc")
        {
            var rpycPaths = files!.Select(f => f.TryGetLocalPath())
                .Where(p => p is not null && Path.GetExtension(p).Equals(".rpyc", StringComparison.OrdinalIgnoreCase))
                .Cast<string>().ToList();
            await DecompileRpycListAsync(rpycPaths);
            return;
        }
        // Default: als Archiv laden (.rpa oder alles Andere).
        await vm.LoadArchiveAsync(path);
    }

    /// <summary>Dekompiliert eine Liste bereits ausgewaehlter .rpyc-Pfade
    /// (Drag&amp;Drop oder Datei-Picker), mit Fortschrittsstatus.</summary>
    private async Task DecompileRpycListAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var batch = App.Services.GetRequiredService<RpycBatchService>();
        SetBusy(true, L.F("Decompile_ProgressFormat", 0, paths.Count, ""));
        int ok = 0, failed = 0;
        var errors = new List<string>();
        await Task.Run(() =>
        {
            for (int i = 0; i < paths.Count; i++)
            {
                var f = paths[i];
                try { batch.DecompileFile(f); ok++; }
                catch (Exception ex) { failed++; errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
        });
        SetBusy(false, L.F("Decompile_DoneStatusFormat", ok, paths.Count, failed, 0));
        var msg = failed == 0
            ? L.F("Decompile_DoneMsgFormat", ok, paths.Count, 0, "")
            : string.Join("\n", errors.Take(10));
        await MessageBox.ShowAsync(this, L.T("Decompile_DoneTitle"), msg);
    }

    // ---- IUiInteractions ----------------------------------------------------

    public async Task<string?> PickOpenArchiveAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Ren'Py-Archiv öffnen",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Ren'Py-Archive (*.rpa)") { Patterns = ["*.rpa"] },
                new FilePickerFileType("Alle Dateien") { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickOpenArchivesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Main_BatchExtract"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Ren'Py-Archive (*.rpa)") { Patterns = ["*.rpa"] },
                new FilePickerFileType("Alle Dateien") { Patterns = ["*"] },
            ],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>()];
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveArchiveAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Archiv speichern unter",
            SuggestedFileName = suggestedName,
            DefaultExtension = "rpa",
            FileTypeChoices =
            [
                new FilePickerFileType("Ren'Py-Archive (*.rpa)") { Patterns = ["*.rpa"] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Save-Dialog fuer eine einzelne Datei aus dem Archiv
    /// (behaelt die Original-Extension bei, kein Format-Filter).</summary>
    public async Task<string?> PickSaveArchiveOrFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Datei speichern unter",
            SuggestedFileName = suggestedName,
        });
        return file?.TryGetLocalPath();
    }

    public Task ShowMessageAsync(string title, string message) => MessageBox.ShowAsync(this, title, message);

    public Task<bool> ConfirmAsync(string title, string message) =>
        MessageBox.ShowAsync(this, title, message, showCancel: true);

    public async Task CopyToClipboardAsync(string text)
    {
        var cb = Clipboard;
        if (cb is not null) await cb.SetTextAsync(text);
    }

    public async Task<PackOptionsViewModel?> AskPackOptionsAsync()
    {
        var vm = new PackOptionsViewModel();
        var dlg = new PackOptionsWindow { DataContext = vm };
        await dlg.ShowDialog(this);
        return dlg.Confirmed ? vm : null;
    }
}
