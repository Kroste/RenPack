using RenPack.ViewModels;

namespace RenPack.Views;

public partial class PackOptionsWindow : ChromeWindow, IPackOptionsUi
{
    public bool Confirmed { get; private set; }

    public PackOptionsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PackOptionsViewModel vm) vm.Ui = this;
    }

    public void Close(bool confirmed)
    {
        Confirmed = confirmed;
        Close();
    }
}
