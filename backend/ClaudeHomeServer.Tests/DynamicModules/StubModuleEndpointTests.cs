using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.DynamicModules;

// Интеграционный PoC сценария Б: полный подъём хоста (WebApplicationFactory) — Program.cs
// сам читает секцию "DynamicModules" из конфига, грузит StubModule по
// Backend.AssemblyPath и подключает его контроллер через ApplicationPart.
// Доказывает критерий «эндпоинт отвечает»:
// GET /api/stub-module/ping → 200 + тело заглушки. (См. ModuleLoaderTests — юнит той же механики.)
// N3: в базовом appsettings.json заглушка Disabled; в test-среде её включает appsettings.Testing.json
// (тот же файл читает ModuleRegistry ДО builder.Build()), поэтому __stub здесь грузится и роутер его находит.
public class StubModuleEndpointTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public StubModuleEndpointTests()
    {
        // Авторизованный клиент — рабочий; эндпоинт заглушки анонимный, поэтому JWT не мешает.
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ПингЗагруженногоМодуля_Отвечает_200()
    {
        var response = await _client.GetAsync("/api/stub-module/ping");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "контроллер динамически загруженного модуля найден роутером и ответил");

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("module").GetString().Should().Be("__stub");
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
    }
}
