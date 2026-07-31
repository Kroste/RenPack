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
