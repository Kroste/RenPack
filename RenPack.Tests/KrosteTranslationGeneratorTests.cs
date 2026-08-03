using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

public sealed class KrosteTranslationGeneratorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-translation-test-{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Writes_tl_file_per_target_language()
    {
        Directory.CreateDirectory(_tmp);
        var gen = new KrosteTranslationGenerator();
        var config = new TranslationConfig(
            TargetLanguages: [TargetLanguage.German, TargetLanguage.French],
            TranslatedStrings: new Dictionary<TargetLanguage, IReadOnlyDictionary<string, string>>
            {
                [TargetLanguage.German] = new Dictionary<string, string> { ["Hello"] = "Hallo" },
                [TargetLanguage.French] = new Dictionary<string, string> { ["Hello"] = "Bonjour" },
            });
        var written = gen.Generate(_tmp, config);
        written.Should().HaveCount(2);
        File.Exists(Path.Combine(_tmp, "tl", "german", "krostemod_translations.rpy"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_tmp, "tl", "french", "krostemod_translations.rpy"))
            .Should().BeTrue();
    }

    [Fact]
    public void Emits_valid_renpy_translate_strings_block()
    {
        Directory.CreateDirectory(_tmp);
        var gen = new KrosteTranslationGenerator();
        var config = new TranslationConfig(
            TargetLanguages: [TargetLanguage.German],
            TranslatedStrings: new Dictionary<TargetLanguage, IReadOnlyDictionary<string, string>>
            {
                [TargetLanguage.German] = new Dictionary<string, string>
                {
                    ["Hello world"] = "Hallo Welt",
                    ["Good morning"] = "Guten Morgen",
                },
            });
        gen.Generate(_tmp, config);
        var content = File.ReadAllText(Path.Combine(_tmp, "tl", "german", "krostemod_translations.rpy"));
        content.Should().Contain("translate german strings:");
        content.Should().Contain("old \"Hello world\"");
        content.Should().Contain("new \"Hallo Welt\"");
        content.Should().Contain("old \"Good morning\"");
        content.Should().Contain("new \"Guten Morgen\"");
    }

    [Fact]
    public void Skips_language_without_translations()
    {
        Directory.CreateDirectory(_tmp);
        var gen = new KrosteTranslationGenerator();
        var config = new TranslationConfig(
            TargetLanguages: [TargetLanguage.German, TargetLanguage.Russian],
            TranslatedStrings: new Dictionary<TargetLanguage, IReadOnlyDictionary<string, string>>
            {
                [TargetLanguage.German] = new Dictionary<string, string> { ["Hi"] = "Hallo" },
                // Russian fehlt komplett
            });
        var written = gen.Generate(_tmp, config);
        written.Should().HaveCount(1);
        Directory.Exists(Path.Combine(_tmp, "tl", "russian")).Should().BeFalse();
    }

    [Fact]
    public void Escapes_quotes_and_newlines_in_strings()
    {
        Directory.CreateDirectory(_tmp);
        var gen = new KrosteTranslationGenerator();
        var config = new TranslationConfig(
            TargetLanguages: [TargetLanguage.German],
            TranslatedStrings: new Dictionary<TargetLanguage, IReadOnlyDictionary<string, string>>
            {
                [TargetLanguage.German] = new Dictionary<string, string>
                {
                    ["He said \"hi\""] = "Er sagte \"hallo\"",
                    ["Line1\nLine2"] = "Zeile1\nZeile2",
                },
            });
        gen.Generate(_tmp, config);
        var content = File.ReadAllText(Path.Combine(_tmp, "tl", "german", "krostemod_translations.rpy"));
        // Escaped quotes: \" muss im Output stehen
        content.Should().Contain("old \"He said \\\"hi\\\"\"");
        content.Should().Contain("new \"Er sagte \\\"hallo\\\"\"");
        // Escaped newlines: \\n literal
        content.Should().Contain("old \"Line1\\nLine2\"");
    }

    [Fact]
    public void Skips_entries_where_translation_equals_original()
    {
        Directory.CreateDirectory(_tmp);
        var gen = new KrosteTranslationGenerator();
        var config = new TranslationConfig(
            TargetLanguages: [TargetLanguage.German],
            TranslatedStrings: new Dictionary<TargetLanguage, IReadOnlyDictionary<string, string>>
            {
                [TargetLanguage.German] = new Dictionary<string, string>
                {
                    ["OK"] = "OK",             // identisch → skip
                    ["Hello"] = "Hallo",       // sollte drin sein
                },
            });
        gen.Generate(_tmp, config);
        var content = File.ReadAllText(Path.Combine(_tmp, "tl", "german", "krostemod_translations.rpy"));
        content.Should().Contain("old \"Hello\"");
        content.Should().NotContain("old \"OK\"");
    }
}
