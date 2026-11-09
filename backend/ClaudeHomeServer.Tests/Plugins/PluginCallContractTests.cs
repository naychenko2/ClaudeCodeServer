using System.Text.Json;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Plugins;

// Контрактный тест стыка бэк↔фронт (QA-фикс L1, шаг 2): ТОЧНЫЙ JSON-литерал тела
// POST /api/plugins/{id}/call (контракт в задаче) десериализуется через НАСТОЯЩИЕ
// JsonSerializerOptions приложения в PluginCallRequest с нужными значениями.
//
// Почему это страховка: дефект 64191abe — каждая сторона проверяла свою половину
// (фронт шлёт {op,args,context}, бэк ждёт {capability,args}), а стык не проверял никто.
// Фронтовый тест frontend/src/lib/__tests__/pluginCallContract.test.ts проверяет ту же
// форму тела; этот тест — зеркало на стороне бэка.
//
// «Настоящие» опции приложения: ASP.NET Core AddControllers (Program.cs) по умолчанию
// использует JsonSerializerDefaults.Web (camelCase + case-insensitive) + JsonStringEnumConverter
// (no-op для PluginCallRequest — там нет enum). Дефолтные new JsonSerializerOptions()
// (PascalCase, case-sensitive) НЕ подойдут: фронт шлёт camelCase.
public class PluginCallContractTests
{
    // Опции, которыми приложение реально десериализует тело запроса (см. комментарий выше).
    private static readonly JsonSerializerOptions AppJson = new(JsonSerializerDefaults.Web);

    // Точный литерал из контракта (строка как есть, кроме <uuid> → конкретный Guid).
    // Фронт: frontend/src/lib/__tests__/pluginCallContract.test.ts проверяет ту же форму.
    private const string ContractBody =
        "{\"capability\": \"notes.search\", \"args\": { \"query\": \"a\", \"limit\": 3 }, \"projectId\": \"11111111-2222-3333-4444-555555555555\"}";

    // --- 1. Точный литерал контракта → PluginCallRequest с нужными значениями ---

    [Fact]
    public void КонтрактныйЛитерал_Десериализуется_СНужнымиЗначениями()
    {
        var req = JsonSerializer.Deserialize<PluginCallRequest>(ContractBody, AppJson);

        req.Should().NotBeNull();
        req!.Capability.Should().Be("notes.search", "capability — строка из реестра операций");
        req.ProjectId.Should().Be("11111111-2222-3333-4444-555555555555", "projectId доезжает до DTO");
        req.Args.Should().NotBeNull("args — объект");
        req.Args!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        req.Args.Value.GetProperty("query").GetString().Should().Be("a");
        req.Args.Value.GetProperty("limit").GetInt32().Should().Be(3);
    }

    // --- 2. Старый формат {op, args, context} (без capability) → Capability пустое → 400 ---
    // Контракт строгий: двойной поддержки нет. Если capability не задан, контроллер
    // отвечает 400 «Не задана capability».

    [Fact]
    public void СтарыйФорматOp_БезCapability_ДаетПустоеCapability()
    {
        // Тело в старом формате: op вместо capability, context вместо projectId.
        const string oldBody =
            "{\"op\": \"notes.search\", \"args\": { \"query\": \"a\", \"limit\": 3 }, \"context\": { \"projectId\": \"11111111-2222-3333-4444-555555555555\" }}";

        var req = JsonSerializer.Deserialize<PluginCallRequest>(oldBody, AppJson);

        req.Should().NotBeNull();
        // capability не задан (фронт шлёт op) → Capability остаётся дефолтным "" → 400.
        req!.Capability.Should().BeEmpty("старый op не читается → capability пусто → 400");
        // context.projectId не читается (нет такого поля) → ProjectId null.
        req.ProjectId.Should().BeNull("вложенный context не читается → projectId null");
    }

    // --- 3. projectId = null (явно) → ProjectId null (личные данные владельца) ---

    [Fact]
    public void ProjectIdNull_ДаетNull()
    {
        const string body = "{\"capability\": \"tasks.list\", \"args\": {}, \"projectId\": null}";
        var req = JsonSerializer.Deserialize<PluginCallRequest>(body, AppJson);

        req.Should().NotBeNull();
        req!.Capability.Should().Be("tasks.list");
        req.ProjectId.Should().BeNull("projectId null → личные данные владельца");
    }
}
