using System.Text.Json;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа claude CLI на control_request initialize. Главное здесь — дубли: CLI отдаёт
// одну и ту же модель и версионным id, и алиасом с одинаковой ПОДПИСЬЮ («Fable»), а в списке
// это две неразличимые строки. Пары с разными подписями (Opus и Opus (1M context)) обязаны
// пережить схлопывание: окно там резолвится в рантайме, и обе позиции нужны.
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

    [Fact]
    public void Одноимённые_записи_схлопываются_в_одну()
    {
        var models = ModelCatalogService.TryParseModels(Line(
            ("claude-fable-5-1", "Fable", "Fable 5.1 · Most capable"),
            ("fable[1m]", "Fable", "Fable 5 · Most capable")), RequestId);

        models.Should().ContainSingle(m => m.DisplayName == "Fable");
    }

    [Fact]
    public void Из_одноимённых_остаётся_алиас_без_номера_версии()
    {
        var models = ModelCatalogService.TryParseModels(Line(
            ("claude-fable-5-1", "Fable", null),
            ("fable[1m]", "Fable", null)), RequestId);

        models!.Single(m => m.DisplayName == "Fable").Value.Should().Be("fable[1m]");
    }

    [Fact]
    public void Алиас_и_алиас_с_окном_остаются_обе_позиции()
    {
        var models = ModelCatalogService.TryParseModels(Line(
            ("opus", "Opus", null),
            ("opus[1m]", "Opus (1M context)", null)), RequestId);

        models!.Select(m => m.Value).Should().Equal("opus", "opus[1m]");
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
