using System.Text.Json;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа claude CLI на control_request initialize. Каталог CLI отдаёт смесь алиасов и
// версионных id, а мы храним только семейство: на выходе value ∈ {default, opus, fable,
// sonnet, haiku}, версия — отдельным полем ResolvedVersion для подписи.
public class ModelCatalogParseTests
{
    private const string RequestId = "model-catalog";

    private static string Line(params (string Value, string Label, string? Desc)[] models)
        => JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new
            {
                request_id = RequestId,
                subtype = "success",
                response = new
                {
                    models = models.Select(m => new { value = m.Value, displayName = m.Label, description = m.Desc })
                }
            }
        });

    // Каталог CLI 2.1.283 (снят живым initialize 2026-09-27): Fable — версионным id с окном
    private static readonly (string, string, string?)[] Cli283 =
    [
        ("default", "Default (recommended)", "Use the default model (currently Opus 5.5) · $4/$20 per Mtok"),
        ("opus", "Opus", "Opus 5.5 · Best for everyday, complex tasks · $4/$20 per Mtok"),
        ("claude-fable-5-1[1m]", "Fable", "Fable 5.1 · Most capable for your hardest and longest-running tasks"),
        ("sonnet", "Sonnet", "Sonnet 5 · Efficient for routine tasks · $2/$10 per Mtok"),
        ("haiku", "Haiku", "Haiku 4.5 · Fastest for quick answers · $1/$5 per Mtok"),
    ];

    // Каталог прежних версий CLI: Fable — алиасом с окном, Opus — двумя позициями
    private static readonly (string, string, string?)[] CliOld =
    [
        ("default", "Default (recommended)", "Use the default model (currently Opus 5.5) · $4/$20 per Mtok"),
        ("opus", "Opus", "Opus 5 · Best for everyday, complex tasks"),
        ("opus[1m]", "Opus (1M context)", "Opus 5 with 1M context · Best for everyday, complex tasks"),
        ("fable[1m]", "Fable", "Fable 5 · Most capable"),
        ("claude-fable-5-1", "Fable", "Fable 5.1 · Most capable"),
        ("sonnet", "Sonnet", "Sonnet 5 · Efficient for routine tasks"),
        ("opusplan", "Opus Plan Mode", "Use Opus in plan mode, Sonnet otherwise"),
    ];

    [Fact]
    public void Каталог_CLI_2_1_283_СводитсяКСемействам()
    {
        var models = ModelCatalogService.TryParseModels(Line(Cli283), RequestId)!;

        models.Select(m => m.Value).Should().Equal("default", "opus", "fable", "sonnet", "haiku");
        models.Select(m => m.DisplayName).Should().Equal("Default (recommended)", "Opus", "Fable", "Sonnet", "Haiku");
        models.Select(m => m.ResolvedVersion).Should().Equal("Opus 5.5", "Opus 5.5", "Fable 5.1", "Sonnet 5", "Haiku 4.5");
    }

    [Fact]
    public void Каталог_прежнего_CLI_СводитсяКСемействам()
    {
        var models = ModelCatalogService.TryParseModels(Line(CliOld), RequestId)!;

        models.Select(m => m.Value).Should().Equal("default", "opus", "fable", "sonnet");
        models.Single(m => m.Value == "opus").ResolvedVersion.Should().Be("Opus 5");
        models.Single(m => m.Value == "fable").ResolvedVersion.Should().Be("Fable 5");
        models.Single(m => m.Value == "default").ResolvedVersion.Should().Be("Opus 5.5");
    }

    [Theory]
    [InlineData("Opus 5.5 · Best for everyday", "Opus 5.5")]
    [InlineData("Opus 5 with 1M context · x", "Opus 5")]
    [InlineData("Use the default model (currently Opus 5.5) · $4/$20 per Mtok", "Opus 5.5")]
    [InlineData("Use Opus in plan mode, Sonnet otherwise", null)]
    [InlineData("Mythos 5 · preview", null)]
    [InlineData(null, null)]
    public void ResolvedVersion_ИзОписания(string? description, string? expected)
    {
        ModelCatalogService.ResolvedVersionOf(description).Should().Be(expected);
    }

    [Fact]
    public void Точный_дубль_value_отбрасывается()
    {
        var models = ModelCatalogService.TryParseModels(Line(
            ("sonnet", "Sonnet", null),
            ("sonnet", "Sonnet", null)), RequestId);

        models.Should().ContainSingle();
    }

    [Fact]
    public void Чужой_request_id_не_разбирается()
    {
        ModelCatalogService.TryParseModels(Line(("sonnet", "Sonnet", null)), "другой").Should().BeNull();
    }
}
