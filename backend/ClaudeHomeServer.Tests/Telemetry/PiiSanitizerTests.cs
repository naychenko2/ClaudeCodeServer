using System.Diagnostics;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Тесты санитайзера PII перед экспортом в OTLP. Процессор сидит в начале pipeline
/// (CompositeProcessor), так что ОБА бэкенда (Aspire + SigNoz) получают очищенные данные.
///
/// Стратегия: allowlist + drop-by-default. См. <see cref="PiiSanitizingProcessor"/>.
/// </summary>
public class PiiSanitizerTests
{
    private static Activity CreateActivity(params (string Key, object? Value)[] tags)
    {
        var activity = new Activity("test");
        foreach (var (key, value) in tags)
            activity.SetTag(key, value);
        return activity;
    }

    [Fact]
    public void FilePath_IsHashed()
    {
        using var activity = CreateActivity(
            ("file_path", @"C:\Users\grisha\data\projects\test\file.txt"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        var tagValue = activity.GetTagItem("file_path")?.ToString();
        tagValue.Should().NotBe(@"C:\Users\grisha\data\projects\test\file.txt");
        tagValue.Should().HaveLength(8);
    }

    [Fact]
    public void FilePathSuffix_IsHashed()
    {
        // Правило таблицы: *.path → hash (любой тег, заканчивающийся на .path)
        using var activity = CreateActivity(
            ("project.path", @"D:\repo\myapp"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        var tagValue = activity.GetTagItem("project.path")?.ToString();
        tagValue.Should().NotBe(@"D:\repo\myapp");
        tagValue.Should().HaveLength(8);
    }

    [Fact]
    public void PersonaName_IsDropped()
    {
        using var activity = CreateActivity(
            ("persona_name", "Mark"),
            ("persona.id", "user-123"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("persona_name").Should().BeNull();
        activity.GetTagItem("persona.id").Should().BeNull();
    }

    [Fact]
    public void SessionId_IsKept()
    {
        var guid = Guid.NewGuid().ToString();
        using var activity = CreateActivity(("session_id", guid));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("session_id").Should().Be(guid);
    }

    [Fact]
    public void UserId_IsDropped()
    {
        using var activity = CreateActivity(("user_id", "grisha@example.com"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("user_id").Should().BeNull();
    }

    [Fact]
    public void OwnerId_IsDropped()
    {
        using var activity = CreateActivity(("owner_id", "usr-abc-123"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("owner_id").Should().BeNull();
    }

    [Fact]
    public void UserContent_IsDropped()
    {
        // prompt / text / content / body / message — PII (пользовательский контент)
        using var activity = CreateActivity(
            ("prompt", "Write me a poem"),
            ("content", "secret data"),
            ("text", "raw text"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("prompt").Should().BeNull();
        activity.GetTagItem("content").Should().BeNull();
        activity.GetTagItem("text").Should().BeNull();
    }

    [Fact]
    public void Provider_IsKept()
    {
        using var activity = CreateActivity(("provider", "claude"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("provider").Should().Be("claude");
    }

    [Fact]
    public void OperationalMetadata_IsKept()
    {
        // provider / model / direction / tool_name / outcome / error_type / reason
        using var activity = CreateActivity(
            ("model", "claude-sonnet-4"),
            ("direction", "outbound"),
            ("tool_name", "bash"),
            ("outcome", "success"),
            ("error_type", "timeout"),
            ("reason", "retry"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("model").Should().Be("claude-sonnet-4");
        activity.GetTagItem("direction").Should().Be("outbound");
        activity.GetTagItem("tool_name").Should().Be("bash");
        activity.GetTagItem("outcome").Should().Be("success");
        activity.GetTagItem("error_type").Should().Be("timeout");
        activity.GetTagItem("reason").Should().Be("retry");
    }

    [Fact]
    public void UnknownTag_IsDroppedByDefault()
    {
        using var activity = CreateActivity(("custom_field", "value"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("custom_field").Should().BeNull();
    }

    [Fact]
    public void HashedPath_IsDeterministic()
    {
        // Same path → same hash (для корреляции в дашбордах)
        var path = @"C:\test\file.txt";
        using var a1 = CreateActivity(("file_path", path));
        using var a2 = CreateActivity(("file_path", path));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(a1);
        processor.OnEnd(a2);

        a1.GetTagItem("file_path").Should().Be(a2.GetTagItem("file_path"));
    }

    [Fact]
    public void StableSemconvHttpAttributes_AreKept()
    {
        // Регрессия: allowlist был собран на именах semconv доOTel-1.0 (http.method,
        // http.status_code), а инструментации 1.17.0 пишут стабильные имена. Они не
        // находились в списке и дропались — спаны приезжали в SigNoz вообще пустыми.
        using var activity = CreateActivity(
            ("http.request.method", "GET"),
            ("http.response.status_code", 200),
            ("http.route", "api/projects/{projectId}/files/tree"),
            ("url.scheme", "https"),
            ("server.address", "api.z.ai"),
            ("server.port", 443),
            ("network.protocol.version", "1.1"),
            ("error.type", "timeout"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("http.request.method").Should().Be("GET");
        activity.GetTagItem("http.response.status_code").Should().Be(200);
        activity.GetTagItem("http.route").Should().Be("api/projects/{projectId}/files/tree");
        activity.GetTagItem("url.scheme").Should().Be("https");
        activity.GetTagItem("server.address").Should().Be("api.z.ai", "иначе непонятно, к кому ходили");
        activity.GetTagItem("server.port").Should().Be(443);
        activity.GetTagItem("network.protocol.version").Should().Be("1.1");
        activity.GetTagItem("error.type").Should().Be("timeout");
    }

    [Fact]
    public void FullUrlWithQuery_IsNotKept()
    {
        // В query-строке уезжают API-ключи (Dify/OpenRouter) — url.full и url.query
        // намеренно НЕ в allowlist. Путь виден через http.route.
        using var activity = CreateActivity(
            ("url.full", "https://api.example.com/v1/datasets?api_key=dataset-SECRET123"),
            ("url.query", "api_key=dataset-SECRET123"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("url.full").Should().BeNull();
        activity.GetTagItem("url.query").Should().BeNull();
    }

    [Fact]
    public void UrlPath_IsHashed_NotExposed()
    {
        // Конкретный путь может нести имена файлов — хэшируем (корреляция остаётся)
        using var activity = CreateActivity(("url.path", "/api/projects/p1/files/Договор.docx"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        var value = activity.GetTagItem("url.path")?.ToString();
        value.Should().NotContain("Договор");
        value.Should().HaveLength(8);
    }

    [Fact]
    public void StatusDescription_IsCleared()
    {
        // Инструментация кладёт сюда текст исключения: URL с query (там ключи)
        // и абсолютные пути сборки. Код ошибки остаётся в Status.
        using var activity = new Activity("test");
        activity.SetStatus(ActivityStatusCode.Error,
            "HttpRequestException: GET https://api.example.com?api_key=SECRET failed at C:\\build\\src\\Foo.cs");

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.StatusDescription.Should().BeNull();
        activity.Status.Should().Be(ActivityStatusCode.Error, "сам факт ошибки теряться не должен");
    }

    [Fact]
    public void PascalCaseKeys_FollowSameRules()
    {
        // Логи присылают {SessionId}/{UserId}, спаны — session_id/user_id.
        // Правило должно быть одно, иначе стиль записи решает, утечёт PII или нет.
        using var activity = CreateActivity(
            ("SessionId", "ses-1"),
            ("UserId", "usr-1"),
            ("PersonaName", "Марк"));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("SessionId").Should().Be("ses-1");
        activity.GetTagItem("UserId").Should().BeNull();
        activity.GetTagItem("PersonaName").Should().BeNull();
    }

    [Fact]
    public void TokensMetadata_IsKept()
    {
        // tokens_input / tokens_output — operational (не PII), содержит подстроку "token"
        // из DropExactText, но KeepTags проверяется раньше
        using var activity = CreateActivity(
            ("tokens_input", 1500),
            ("tokens_output", 300));

        var processor = new PiiSanitizingProcessor();
        processor.OnEnd(activity);

        activity.GetTagItem("tokens_input").Should().Be(1500);
        activity.GetTagItem("tokens_output").Should().Be(300);
    }
}
