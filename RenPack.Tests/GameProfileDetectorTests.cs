using FluentAssertions;
using RenPack.Services.Modding;
using Xunit;

namespace RenPack.Tests;

/// <summary>Tests fuer den GameProfileDetector — Heuristik-Klassifikation
/// dekompilierter Ren'Py-Spiele. Wir stellen jeweils minimale .rpy-
/// Snippets zusammen die genau ein Merkmal isolieren.</summary>
public sealed class GameProfileDetectorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(),
        $"renpack-profile-tests-{Guid.NewGuid():N}");
    private readonly GameProfileDetector _detector = new();

    public GameProfileDetectorTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private void Write(string relPath, string content)
    {
        var full = Path.Combine(_tmp, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Default_profile_has_choice_screen_and_no_special_flags()
    {
        Write("game/script.rpy", """
            label start:
                "narration"
                return
            """);
        var p = _detector.Detect(_tmp);
        p.MenuScreenCandidates.Should().Contain("choice");
        p.HasCharacterContainers.Should().BeFalse();
        p.DominantChoiceStyle.Should().Be(ChoiceStyle.Mixed); // zu wenig Choices fuer Klassifikation
        p.TranslationLanguages.Should().BeEmpty();
    }

    [Fact]
    public void Detects_custom_menu_screen_via_items_param()
    {
        // Boundaries-of-Morality-Pattern: eigenes Menu-Screen statt
        // Ren'Py-Default „choice".
        Write("game/screens.rpy", """
            screen sandbox_choice(items):
                vbox:
                    for i in items:
                        textbutton i.caption action i.action
            """);
        var p = _detector.Detect(_tmp);
        p.MenuScreenCandidates.Should().Contain("choice");
        p.MenuScreenCandidates.Should().Contain("sandbox_choice");
    }

    [Fact]
    public void Detects_menu_screen_with_init_prefix()
    {
        // Boundaries of Morality overrides den Ren'Py-Default-Screen mit
        // `init -500 screen choice(items):` — der Detector-Regex muss den
        // init-N-Praefix optional erlauben.
        Write("game/screens.rpy", """
            init -500 screen choice(items):
                pass

            init -500 screen universal_shop(title, items):
                pass
            """);
        var p = _detector.Detect(_tmp);
        p.MenuScreenCandidates.Should().Contain("choice");
        p.MenuScreenCandidates.Should().Contain("universal_shop");
    }

    [Fact]
    public void Detects_menu_screen_override_via_config()
    {
        Write("game/options.rpy", """
            init python:
                config.menu_screen = "my_custom_menu"
            """);
        var p = _detector.Detect(_tmp);
        p.MenuScreenCandidates.Should().Contain("my_custom_menu");
    }

    [Fact]
    public void Detects_character_container_style_from_update_ratio()
    {
        // Ueber 15% aller Assigns via .update() → Container-Style.
        var lines = new List<string> { "label start:" };
        for (int i = 0; i < 20; i++) lines.Add($"    $ fcs.update(\"stat{i}\", 1)");
        for (int i = 0; i < 5; i++) lines.Add($"    $ flat{i} = 0");
        lines.Add("    return");
        Write("game/script.rpy", string.Join("\n", lines));

        var p = _detector.Detect(_tmp);
        p.HasCharacterContainers.Should().BeTrue();
    }

    [Fact]
    public void Detects_flat_store_style_when_no_containers()
    {
        var lines = new List<string> { "label start:" };
        for (int i = 0; i < 30; i++) lines.Add($"    $ var{i} += 1");
        lines.Add("    return");
        Write("game/script.rpy", string.Join("\n", lines));

        var p = _detector.Detect(_tmp);
        p.HasCharacterContainers.Should().BeFalse();
    }

    [Fact]
    public void Classifies_jump_based_choice_style()
    {
        // >70% aller Choices haben nur einen jump im Body → JumpBased.
        var lines = new List<string>();
        for (int i = 0; i < 15; i++)
        {
            lines.Add($"label chapter{i}:");
            lines.Add("    menu:");
            lines.Add("        \"choice A\":");
            lines.Add("            jump next_scene");
            lines.Add("        \"choice B\":");
            lines.Add("            jump other_scene");
        }
        Write("game/script.rpy", string.Join("\n", lines));

        var p = _detector.Detect(_tmp);
        p.DominantChoiceStyle.Should().Be(ChoiceStyle.JumpBased);
    }

    [Fact]
    public void Classifies_inline_choice_style()
    {
        var lines = new List<string>();
        for (int i = 0; i < 15; i++)
        {
            lines.Add($"label chapter{i}:");
            lines.Add("    menu:");
            lines.Add("        \"nice\":");
            lines.Add("            $ love += 1");
            lines.Add("        \"rude\":");
            lines.Add("            $ love -= 1");
        }
        Write("game/script.rpy", string.Join("\n", lines));

        var p = _detector.Detect(_tmp);
        p.DominantChoiceStyle.Should().Be(ChoiceStyle.Inline);
    }

    [Fact]
    public void Detects_translation_languages_from_tl_subdirs()
    {
        Write("game/script.rpy", "label start:\n    return\n");
        Write("game/tl/de/common.rpy", "# de\n");
        Write("game/tl/fr/common.rpy", "# fr\n");
        Write("game/tl/english/common.rpy", "# en\n");
        var p = _detector.Detect(_tmp);
        p.TranslationLanguages.Should().Contain(["de", "fr", "english"]);
    }

    [Fact]
    public void Detects_game_title_from_config_name()
    {
        Write("game/options.rpy", """
            define config.name = _("Boundaries of Morality")
            """);
        var p = _detector.Detect(_tmp);
        p.DetectedTitle.Should().Be("Boundaries of Morality");
    }

    [Fact]
    public void Ignores_tl_folder_when_scanning_for_screens_and_stats()
    {
        // tl/ Kopien wuerden Zaehler sonst verdoppeln.
        Write("game/screens.rpy", """
            screen main_menu:
                pass
            """);
        Write("game/tl/de/screens.rpy", """
            screen fake_menu(items):
                pass
            """);
        var p = _detector.Detect(_tmp);
        p.MenuScreenCandidates.Should().NotContain("fake_menu");
    }
}
