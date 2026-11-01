using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.AudioEditor;

// Ручка схемы «Дополнительно» модуля «Звук» на живом хосте: под флагом audio-editor (выключен — 404),
// только модели каталога. Схема local не требует ни сети, ни GPU — ею и проверяем
[Trait("Category", "Host")]
public sealed class AudioSchemaEndpointTests : IClassFixture<AudioSchemaEndpointTests.Hosts>
{
    private const string ChatterboxSchema = "/api/audio-editor/schema?provider=local&model=chatterbox-multilingual&op=Speak";

    private readonly Hosts _hosts;

    public AudioSchemaEndpointTests(Hosts hosts) => _hosts = hosts;

    // Хост на КЛАСС, один на каждую конфигурацию: поднимается лениво при первом обращении
    // и гасится вместе с фикстурой. Тесты состояния хоста не меняют — только читают
    public sealed class Hosts : IDisposable
    {
        public PlainHost Plain { get; } = new();

        public void Dispose() => Plain.Dispose();
    }

    public sealed class PlainHost : TestWebApplicationFactory;

    [Fact]
    public async Task Anonymous_401()
    {
        var response = await _hosts.Plain.CreateClient().GetAsync(ChatterboxSchema);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LocalSchema_WithNotPassedParams_ForeignModel404()
    {
        var client = _hosts.Plain.CreateAuthenticatedClient();

        var response = await client.GetAsync(ChatterboxSchema);
        var foreign = await client.GetAsync("/api/audio-editor/schema?provider=local&model=fal-ai/demucs&op=Separate");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        schema.GetProperty("source").GetString().Should().Be("local-catalog");
        var fields = schema.GetProperty("fields").EnumerateArray().ToList();
        fields.Select(f => f.GetProperty("key").GetString()).Should().Equal("expressiveness", "cfg_weight");
        fields[1].GetProperty("passed").GetBoolean().Should().BeFalse();
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
