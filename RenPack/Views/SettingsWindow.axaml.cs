using RenPack.ViewModels;

namespace RenPack.Views;

public partial class SettingsWindow : ChromeWindow, ISettingsUi
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsWindowViewModel vm) vm.Ui = this;
    }

    public void Close(bool saved) => Close((object?)saved);
}
