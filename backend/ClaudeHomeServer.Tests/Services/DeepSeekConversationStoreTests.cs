using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Llm.DeepSeek;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты истории messages[] DeepSeek: persist/восстановление и обрезка контекста.
/// </summary>
public class DeepSeekConversationStoreTests : IDisposable
{
    private readonly string _base;

    public DeepSeekConversationStoreTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "ds_store_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private static JsonObject Msg(string role, string content) =>
        new() { ["role"] = role, ["content"] = content };

    [Fact]
    public async Task SaveИBind_ВосстанавливаютИсториюПослеРестарта()
    {
        var store = new DeepSeekConversationStore(_base);
        store.Bind("engine-1");
        store.Append(Msg("system", "промпт"));
        store.Append(Msg("user", "привет"));
        await store.SaveAsync();

        var restored = new DeepSeekConversationStore(_base);
        restored.Bind("engine-1");

        restored.Messages.Should().HaveCount(2);
        restored.Messages[1]!["content"]!.GetValue<string>().Should().Be("привет");
    }

    [Fact]
    public void TrimToFit_УдаляетСтарыеСообщенияИСтавитЗаглушку()
    {
        var store = new DeepSeekConversationStore(_base);
        store.Append(Msg("system", "промпт"));
        for (var i = 0; i < 10; i++)
        {
            store.Append(Msg("user", new string('в', 4000) + i));
            store.Append(Msg("assistant", new string('о', 4000) + i));
        }

        // Бюджет заведомо меньше суммарного размера — часть истории должна уйти
        store.TrimToFit(contextWindow: 20_000, maxTokens: 1000);

        store.Messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        store.Messages[1]!["content"]!.GetValue<string>().Should().Contain("усечена");
        // Хвост диалога сохранён
        store.Messages[^1]!["content"]!.GetValue<string>().Should().EndWith("9");
    }

    [Fact]
    public void TrimToFit_УдаляетСвязкуAssistantToolCallsВместеСРезультатами()
    {
        var store = new DeepSeekConversationStore(_base);
        store.Append(Msg("system", "промпт"));
        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new string('х', 8000),
            ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "call_1" }),
        };
        store.Append(assistant);
        store.Append(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call_1", ["content"] = new string('р', 8000) });
        store.Append(Msg("user", "финал"));
        store.Append(Msg("assistant", "ответ"));

        store.TrimToFit(contextWindow: 3000, maxTokens: 500);

        // Осиротевших tool-сообщений не осталось
        store.Messages.Should().NotContain(m => m!["role"]!.GetValue<string>() == "tool");
        store.Messages.Select(m => m as JsonObject)
            .Should().NotContain(o => o != null && o.ContainsKey("tool_calls"));
    }

    [Fact]
    public void TrimToFit_ВлезающаяИстория_НеТрогается()
    {
        var store = new DeepSeekConversationStore(_base);
        store.Append(Msg("system", "промпт"));
        store.Append(Msg("user", "короткое"));

        store.TrimToFit(contextWindow: 1_000_000, maxTokens: 8192);

        store.Messages.Should().HaveCount(2);
    }
}
