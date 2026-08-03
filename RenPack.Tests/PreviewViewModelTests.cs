using FluentAssertions;
using RenPack.Services;
using RenPack.ViewModels;
using Xunit;

namespace RenPack.Tests;

/// <summary>
/// Smoke-Tests fuer <see cref="PreviewViewModel"/> — ohne Avalonia-Fenster
/// oder DI-Container. Fake-Archive-Service liefert die Bytes-Streams, wir
/// pruefen dass die richtigen Preview-Modi (Text/Image/Media/Unsupported)
/// aktiviert werden und die HasContent/Placeholder-Flags stimmen.
///
/// Fuer echte End-to-End-Preview-Tests bräuchten wir Avalonia.Headless
/// (setzt auf einen aktiven UI-Dispatcher-Loop; mit xunit.v3 nicht offiziell
/// integriert) — die VM ist aber UI-frei und laesst sich so direkt testen.
/// </summary>
public sealed class PreviewViewModelTests
{
    private sealed class FakeArchiveService : IRenpyArchiveService
    {
        public byte[]? ReturnBytes { get; set; }
        public byte[]? ReadEntryBytes(string archivePath, RpaEntry entry, long maxBytes) => ReturnBytes;

        // Not needed by preview tests — throw so we notice if VM starts calling them.
        public RpaArchiveInfo ReadIndex(string archivePath) => throw new NotSupportedException();
        public void ExtractEntry(string archivePath, RpaEntry entry, string destinationFile)
            => throw new NotSupportedException();
        public int Extract(RpaArchiveInfo archive, IEnumerable<RpaEntry> entries, string destinationDirectory,
            IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public int ExtractAll(RpaArchiveInfo archive, string destinationDirectory,
            IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public int Create(string archivePath, string sourceDirectory,
            RpaVersion version = RpaVersion.V3_0, uint key = RenpyArchiveService.DefaultKey,
            IProgress<RpaProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static RpaEntry Entry(string name, long size) =>
        new(name, new[] { new RpaSegment(0, size, Array.Empty<byte>()) });

    [Fact]
    public async Task Unknown_extension_shows_placeholder()
    {
        var vm = new PreviewViewModel(new FakeArchiveService());
        await vm.LoadAsync("archive.rpa", Entry("data.bin", 100));
        vm.HasContent.Should().BeTrue();
        vm.Placeholder.Should().NotBeNullOrEmpty();
        vm.IsText.Should().BeFalse();
        vm.IsImage.Should().BeFalse();
    }

    [Fact]
    public async Task Text_extension_loads_text_content()
    {
        var svc = new FakeArchiveService { ReturnBytes = "hello world"u8.ToArray() };
        var vm = new PreviewViewModel(svc);
        await vm.LoadAsync("archive.rpa", Entry("script.rpy", 11));
        vm.IsText.Should().BeTrue();
        vm.TextContent.Should().Be("hello world");
        vm.Placeholder.Should().BeNull();
    }

    [Fact]
    public async Task Text_over_size_limit_shows_placeholder_instead_of_content()
    {
        // Default Text-Limit ist 512 KB — Entry mit 600 KB darf nichts laden.
        var vm = new PreviewViewModel(new FakeArchiveService { ReturnBytes = new byte[600 * 1024] });
        await vm.LoadAsync("archive.rpa", Entry("big.txt", 600 * 1024));
        vm.IsText.Should().BeFalse();
        vm.Placeholder.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Utf8_bom_is_stripped_from_text_preview()
    {
        // BOM + "hallo"
        var svc = new FakeArchiveService { ReturnBytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'a', (byte)'l', (byte)'l', (byte)'o' } };
        var vm = new PreviewViewModel(svc);
        await vm.LoadAsync("archive.rpa", Entry("script.rpy", 8));
        vm.TextContent.Should().Be("hallo");
    }

    [Fact]
    public void Clear_resets_all_state()
    {
        var vm = new PreviewViewModel(new FakeArchiveService());
        vm.HasContent = true;
        vm.TextContent = "abc";
        vm.Placeholder = "irgendwas";
        vm.IsMedia = true;
        vm.Clear();
        vm.HasContent.Should().BeFalse();
        vm.TextContent.Should().BeNull();
        vm.Placeholder.Should().BeNull();
        vm.IsMedia.Should().BeFalse();
        vm.IsPlayingInline.Should().BeFalse();
    }

    [Fact]
    public async Task Toggle_inline_playback_without_media_is_noop()
    {
        // Ohne Media-Service oder aktuelles Media-Temp-File soll der Toggle
        // still nichts tun (nicht crashen, nicht IsPlayingInline setzen).
        var vm = new PreviewViewModel(new FakeArchiveService(), media: null);
        vm.ToggleInlinePlaybackCommand.CanExecute(null).Should().BeTrue();
        vm.ToggleInlinePlaybackCommand.Execute(null);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        vm.IsPlayingInline.Should().BeFalse();
    }
}
