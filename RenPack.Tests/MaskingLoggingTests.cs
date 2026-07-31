using FluentAssertions;
using NLog;
using NLog.Config;
using NLog.Targets;
using RenPack.Logging;
using Xunit;

namespace RenPack.Tests;

/// <summary>
/// Verifiziert die Logging-Kette: der <c>${masked:inner=${message}}</c>-Renderer wird
/// registriert, rendert die Message korrekt (kein Parsing-Artefakt) und maskiert Secrets.
/// </summary>
public sealed class MaskingLoggingTests
{
    [Fact]
    public void Masked_layout_renders_message_and_hides_secrets()
    {
        MaskingLayoutRenderer.Register();

        var config = new LoggingConfiguration();
        var mem = new MemoryTarget("mem") { Layout = "${masked:inner=${message}}" };
        config.AddTarget(mem);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, mem, "masktest");
        LogManager.Configuration = config;

        var log = LogManager.GetLogger("masktest");
        log.Info("Verbinde mit password=geheim123 und token=abc.def bei https://user:pass@host/x");
        LogManager.Flush();

        mem.Logs.Should().ContainSingle();
        string line = mem.Logs[0];
        line.Should().Contain("Verbinde mit", "die Message muss gerendert werden (kein Parsing-Artefakt)");
        line.Should().NotContain("geheim123");
        line.Should().NotContain("pass@host");
        line.Should().Contain("***");
    }
}
