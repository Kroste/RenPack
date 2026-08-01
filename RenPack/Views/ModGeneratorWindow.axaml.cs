using Avalonia.Platform.Storage;
using RenPack.ViewModels;

namespace RenPack.Views;

public partial class ModGeneratorWindow : ChromeWindow, IModGeneratorUi
{
    public ModGeneratorWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ModGeneratorViewModel vm) vm.Ui = this;
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

    public Task ShowMessageAsync(string title, string message) =>
        MessageBox.ShowAsync(this, title, message);

    public Task<bool> ConfirmAsync(string title, string message) =>
        MessageBox.ShowAsync(this, title, message, showCancel: true);
}
