using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace RenPack.Views;

public partial class MessageBox : ChromeWindow
{
    private bool _result;

    public MessageBox()
    {
        InitializeComponent();
        OkButton.Click += OnOk;
        CancelButton.Click += OnCancel;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOk(object? sender, RoutedEventArgs e) { _result = true; Close(_result); }
    private void OnCancel(object? sender, RoutedEventArgs e) { _result = false; Close(_result); }

    /// <summary>Zeigt eine Meldung. Mit <paramref name="showCancel"/> als Ja/Nein-Bestätigung.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, bool showCancel = false)
    {
        var box = new MessageBox();
        box.Bar.Title = title;
        box.Title = title;
        box.MessageText.Text = message;
        box.CancelButton.IsVisible = showCancel;
        box.OkButton.Content = showCancel ? "Ja" : "OK";
        if (showCancel) box.CancelButton.Content = "Nein";
        return await box.ShowDialog<bool>(owner);
    }
}
