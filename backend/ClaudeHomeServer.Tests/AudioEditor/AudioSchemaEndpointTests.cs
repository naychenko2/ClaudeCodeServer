using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.AudioEditor;

// Ручка схемы «Дополнительно» модуля «Звук» на живом хосте: под флагом audio-editor (выключен — 404),
// только модели каталога. Схема local не требует ни сети, ни GPU — ею и проверяем
public sealed class AudioSchemaEndpointTests : IDisposable
{
    private const string ChatterboxSchema = "/api/audio-editor/schema?provider=local&model=chatterbox-multilingual&op=Speak";

    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private void Flag(bool on)
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, FeatureFlagKeys.AudioEditor, on);
    }

    [Fact]
    public async Task FlagOff_404()
    {
        Flag(false);
        var response = await _factory.CreateAuthenticatedClient().GetAsync(ChatterboxSchema);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_401()
    {
        var response = await _factory.CreateClient().GetAsync(ChatterboxSchema);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FlagOn_LocalSchema_WithNotPassedParams_ForeignModel404()
    {
        Flag(true);
        var client = _factory.CreateAuthenticatedClient();

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
