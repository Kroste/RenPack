using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using NLog;
using RenPack.ViewModels;

namespace RenPack.Views;

public partial class SaveWindow : ChromeWindow, ISaveUi
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public SaveWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SaveWindowViewModel vm) vm.Ui = this;
    }

    // ---- Drag & Drop --------------------------------------------------------

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not SaveWindowViewModel vm) return;
        if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;
        var files = e.DataTransfer.TryGetFiles();
        var path = files?.OfType<IStorageFile>().FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await vm.LoadSaveAsync(path);
    }

    // ---- ISaveUi ------------------------------------------------------------

    public async Task<string?> PickOpenSaveAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Ren'Py-Save öffnen",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Ren'Py-Saves (*.save)") { Patterns = ["*.save"] },
                new FilePickerFileType("Alle Dateien") { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveTargetAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save speichern unter",
            SuggestedFileName = suggestedName,
            DefaultExtension = "save",
            FileTypeChoices =
            [
                new FilePickerFileType("Ren'Py-Saves (*.save)") { Patterns = ["*.save"] },
            ],
        });
        return file?.TryGetLocalPath();
    }

    public Task ShowMessageAsync(string title, string message) =>
        MessageBox.ShowAsync(this, title, message);

    public Task<bool> ConfirmAsync(string title, string message) =>
        MessageBox.ShowAsync(this, title, message, showCancel: true);
}
