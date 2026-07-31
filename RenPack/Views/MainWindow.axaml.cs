using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using NLog;
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
        OpenSaveButton.Click += OnOpenSave;
        SettingsButton.Click += OnSettings;
        DecompileFileButton.Click += OnDecompileFiles;
        DecompileFolderButton.Click += OnDecompileFolder;

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
            vm.Ui = this;
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
                vm.StatusText = $"Update verfügbar: Version {result.LatestVersion} (ⓘ öffnen).";
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
                Title = "Ordner mit .rpyc-Dateien wählen (rekursiv)",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;
            var root = folders[0].TryGetLocalPath();
            if (root is null) return;

            var batch = App.Services.GetRequiredService<RpycBatchService>();
            SetBusy(true, "Suche .rpyc-Dateien …");
            var progress = new Progress<(int done, int total, string current)>(p =>
                SetBusy(true, $"Dekompiliere {p.done}/{p.total}: {Path.GetFileName(p.current)}"));
            var result = await Task.Run(() => batch.DecompileDirectory(root, progress));
            SetBusy(false, $"Fertig: {result.Succeeded}/{result.Total} dekompiliert, {result.Failed} Fehler.");
            var msg = result.Failed == 0
                ? $"{result.Succeeded} von {result.Total} Datei(en) dekompiliert (rekursiv unter {root})."
                : $"{result.Succeeded} von {result.Total} erfolgreich, {result.Failed} fehlgeschlagen:\n\n"
                    + string.Join("\n", result.Errors.Take(10).Select(x => $"{Path.GetFileName(x.File)}: {x.Error}"));
            await MessageBox.ShowAsync(this, "Ordner-Dekompilierung fertig", msg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ordner-Dekompilierung fehlgeschlagen");
            SetBusy(false, "Fehler bei der Ordner-Dekompilierung.");
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
        var files = e.DataTransfer.TryGetFiles();
        var first = files?.OfType<IStorageFile>().FirstOrDefault();
        string? path = first?.TryGetLocalPath();
        if (path is not null)
            await vm.LoadArchiveAsync(path);
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

    public Task ShowMessageAsync(string title, string message) => MessageBox.ShowAsync(this, title, message);

    public Task<bool> ConfirmAsync(string title, string message) =>
        MessageBox.ShowAsync(this, title, message, showCancel: true);
}
