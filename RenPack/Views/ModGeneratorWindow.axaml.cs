using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using RenPack.Localization;
using RenPack.Services;
using RenPack.Services.Modding;
using RenPack.ViewModels;

namespace RenPack.Views;

public partial class ModGeneratorWindow : ChromeWindow, IModGeneratorUi
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

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

    public async Task<RenameConfig?> PromptRenameMappingsAsync(
        IReadOnlyList<RpyCharacter> characters,
        IReadOnlyList<RpySayStatement> sayStatements)
    {
        var dlg = new RenameConfigWindow();
        dlg.Load(characters);
        await dlg.ShowDialog(this);
        var baseConfig = dlg.Result;
        if (baseConfig is null) return null;

        // E4b: wenn User die KI-Body-Rewrite-Checkbox aktiviert hat,
        // Rewriter aufrufen und Vorschlaege im Preview-Dialog zeigen.
        // Rewrite laeuft entweder wenn Character-Mappings ODER Relations
        // vorhanden sind — bei nur-Relations (E4c) reicht das allein.
        bool hasRelations = baseConfig.RelationMappings is { Count: > 0 };
        if (!dlg.UseAiRewrite || (baseConfig.Mappings.Count == 0 && !hasRelations))
            return baseConfig;

        // Display-Name-Mappings bauen: der Body-Text enthaelt die
        // DisplayNames (z.B. "Sophia") — der Rewriter arbeitet damit.
        // Mappings vom User sind VarName → NewDisplay; wir mappen zusaetz-
        // lich OldDisplay → NewDisplay ueber die Character-Liste.
        var displayMappings = baseConfig.Mappings
            .Select(kv => (
                Old: characters.FirstOrDefault(c => c.VarName == kv.Key)?.DisplayName,
                New: kv.Value))
            .Where(t => !string.IsNullOrEmpty(t.Old))
            .ToDictionary(t => t.Old!, t => t.New, StringComparer.Ordinal);

        if (displayMappings.Count == 0 && !hasRelations) return baseConfig;

        var provider = TryCreateAiProvider();
        if (provider is null)
        {
            await ShowMessageAsync(
                L.T("RewritePreview_NoProvider_Title"),
                L.T("RewritePreview_NoProvider_Body"));
            return baseConfig;
        }

        // Progress: waehrend der KI-Calls kein extra Dialog — der User
        // sieht den Progress im ModGeneratorWindow-StatusText via VM.
        // Wenn der VM diese Info nicht durchreichen kann, ist ein simpler
        // ProgressDialog denkbar; fuer den MVP reicht ein Log + der
        // spaeter aufgehende Preview-Dialog.
        var rewriter = new KrosteAiRewriter(provider);
        IReadOnlyList<BodyTextEdit> proposals;
        try
        {
            proposals = await rewriter.ProposeRewritesAsync(
                sayStatements, displayMappings, baseConfig.RelationMappings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AI-Rewrite fehlgeschlagen");
            await ShowMessageAsync(
                L.T("RewritePreview_Failed_Title"),
                L.F("RewritePreview_Failed_Body_Format", ex.Message));
            return baseConfig;
        }

        if (proposals.Count == 0)
        {
            await ShowMessageAsync(
                L.T("RewritePreview_NoResults_Title"),
                L.T("RewritePreview_NoResults_Body"));
            return baseConfig;
        }

        var preview = new RewritePreviewWindow();
        preview.Load(proposals);
        await preview.ShowDialog(this);
        var acceptedEdits = preview.Result;
        if (acceptedEdits is null)
        {
            // Preview-Cancel: nur Character-Rename ohne Body-Rewrite.
            return baseConfig;
        }

        return baseConfig with { BodyTextEdits = acceptedEdits };
    }

    /// <summary>Ruft die aktuellen KI-Einstellungen ab und baut den passenden
    /// Provider. Wenn kein Provider konfiguriert ist, gibt <c>null</c> zurueck
    /// — Aufrufer zeigt dann die entsprechende Fehlermeldung.</summary>
    private static IAiProvider? TryCreateAiProvider()
    {
        try
        {
            var services = App.Services;
            var settingsSvc = services.GetRequiredService<AiSettingsService>();
            var factory = services.GetRequiredService<AiProviderFactory>();
            return factory.Create(settingsSvc.Current);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "TryCreateAiProvider fehlgeschlagen");
            return null;
        }
    }
}
