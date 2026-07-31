using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace RenPack.Controls;

/// <summary>
/// Kroste-Standard-Titelleiste für Fenster mit SystemDecorations="BorderOnly":
/// Drag zum Verschieben, Doppelklick zum Maximieren, eigene Min/Max/Close-Buttons.
/// </summary>
public partial class TitleBar : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<TitleBar, string?>(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Optionaler Zusatzinhalt links der Fensterbuttons (z.B. ein ⓘ-Button).</summary>
    public static readonly StyledProperty<object?> RightContentProperty =
        AvaloniaProperty.Register<TitleBar, object?>(nameof(RightContent));

    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public TitleBar()
    {
        InitializeComponent();
        MinButton.Click += (_, _) => { if (Host is { } w) w.WindowState = WindowState.Minimized; };
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Host?.Close();
        Bar.PointerPressed += OnBarPointerPressed;
        Bar.DoubleTapped += (_, _) => ToggleMaximize();
    }

    // ACHTUNG (Avalonia 12): VisualRoot ist NICHT mehr das Window — die Visual-
    // Wurzel ist jetzt der interne TopLevelHost, das Window nur noch dessen Kind.
    // "VisualRoot as Window" liefert null und macht alle Handler zu stillen No-Ops!
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
            TitleText.Text = Title;
        else if (change.Property == RightContentProperty)
            RightSlot.Content = RightContent;
    }

    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            Host?.BeginMoveDrag(e);
    }

    private void ToggleMaximize()
    {
        if (Host is { } w)
            w.WindowState = w.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
    }
}
