using Avalonia.Controls;
using FluentAssertions;
using RenPack.ViewModels;
using RenPack.Views;
using Xunit;

namespace RenPack.Tests;

/// <summary>Headless-UI-Smoke-Tests: bauen echte Avalonia-Fenster
/// (ohne physischen Display, via <see cref="HeadlessAvaloniaRunner"/>)
/// und pruefen die grundlegende Verdrahtung — dass Windows sich
/// instanziieren lassen, DataContext sich binden laesst, Basis-
/// Controls existieren.
///
/// **Was wir NICHT testen:** komplette User-Flows, Rendering-Pixel,
/// echte Dialog-Roundtrips. Dafuer waere ein Ende-zu-Ende-Setup mit
/// vollem DI-Container noetig — den bauen wir erst wenn's konkret
/// weh tut. Die Smoke-Tests hier verhindern das offensichtlichste
/// Regressions-Risiko: XAML kaputt, Property-Bindings falsch, Ctor
/// wirft — und das bekommen wir hier ohne den vollen App-Stack.</summary>
public sealed class UiSmokeTests
{
    [Fact]
    public Task MainWindow_can_be_constructed_headless() =>
        HeadlessAvaloniaRunner.RunAsync(() =>
        {
            var window = new MainWindow();
            window.Should().NotBeNull();
            window.Title.Should().NotBeNullOrEmpty();
            window.Show();
            window.IsVisible.Should().BeTrue();
            window.Close();
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task SaveWindow_can_be_constructed_headless() =>
        HeadlessAvaloniaRunner.RunAsync(() =>
        {
            var window = new SaveWindow();
            window.Should().NotBeNull();
            window.Show();
            window.IsVisible.Should().BeTrue();
            window.Close();
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task ModGeneratorWindow_can_be_constructed_headless() =>
        HeadlessAvaloniaRunner.RunAsync(() =>
        {
            var window = new ModGeneratorWindow();
            window.Should().NotBeNull();
            window.Show();
            window.IsVisible.Should().BeTrue();
            window.Close();
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task SettingsWindow_can_be_constructed_headless() =>
        HeadlessAvaloniaRunner.RunAsync(() =>
        {
            var window = new SettingsWindow();
            window.Should().NotBeNull();
            window.Show();
            window.IsVisible.Should().BeTrue();
            window.Close();
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task AboutWindow_can_be_constructed_headless() =>
        HeadlessAvaloniaRunner.RunAsync(() =>
        {
            var window = new AboutWindow();
            window.Should().NotBeNull();
            window.Show();
            window.IsVisible.Should().BeTrue();
            window.Close();
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task MainWindow_datacontext_binds_to_viewmodel_properties() =>
        HeadlessAvaloniaRunner.RunAsync(() =>
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            window.DataContext.Should().BeSameAs(vm);
            window.Close();
        }, TestContext.Current.CancellationToken);
}
