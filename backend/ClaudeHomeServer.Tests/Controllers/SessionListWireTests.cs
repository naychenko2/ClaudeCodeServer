using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Список чатов проекта (GET /api/projects/{id}/sessions) фронт перечитывает опросом, поэтому
// его вес и число реально передаваемых байт — предмет отдельного сторожа:
//  • серверная кухня сессии (lastCostState, очередь ожидания устройства, …) на wire не уходит;
//  • условный GET: ETag считается по готовому телу, совпавший If-None-Match даёт 304, а любое
//    изменение поля, включая updatedAt, даёт новый тег — устаревший список клиент не получит
//    (на updatedAt держатся непрочитанность и пропуск повторной загрузки истории).
[Trait("Category", "Host")]
public class SessionListWireTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<(string ProjectId, string SessionId)> CreateProjectSessionAsync()
    {
        var dir = Path.Combine(factory.TempDir, "sesslist_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "SessionList", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var sessionResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        sessionResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await sessionResp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    private Task<HttpResponseMessage> GetListAsync(string projectId, string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{projectId}/sessions");
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return _client.SendAsync(req);
    }

    [Fact]
    public async Task СерверныеПоля_НаWireНеУходят_ПоляДляФронтаОстаются()
    {
        var (projectId, sessionId) = await CreateProjectSessionAsync();
        var s = factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!;
        s.CommittedFilePaths = ["src/a.ts"];
        s.LastCostState = new SessionCostState { TotalCostUsd = 1.5 };
        s.DeviceWaitQueue = [new DeviceWaitMessage("m1", "текст", DeviceWaitMessage.RouteAuto, DateTime.UtcNow)];
        s.Context = [new SessionContextEntry { Type = SessionContextTypes.File, Id = "a.md" }];
        s.ArchiveBatchId = "batch-1";

        var resp = await GetListAsync(projectId);
        resp.EnsureSuccessStatusCode();
        var item = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())[0];

        // Имена явные, а не из SessionJsonConverter.ServerOnlyFields: иначе удаление поля из
        // массива не краснило бы тест (он сверял бы массив с самим собой)
        foreach (var field in new[] { "committedFilePaths", "lastCostState", "deviceWaitQueue", "context", "archiveBatchId" })
            item.TryGetProperty(field, out _).Should().BeFalse($"«{field}» — серверная кухня, фронту не нужна");
        foreach (var field in SessionJsonConverter.ServerOnlyFields)
            item.TryGetProperty(field, out _).Should().BeFalse($"«{field}» объявлен серверным");
        // То, на чём держатся дерево, шапка, непрочитанность и сортировка
        foreach (var field in new[] { "id", "status", "updatedAt", "createdAt", "name", "parentSessionId", "taskDone", "origin", "tags" })
            item.TryGetProperty(field, out _).Should().BeTrue($"«{field}» нужен фронту");
    }

    [Fact]
    public async Task СписокОтдаётETag_СовпавшийТег_Даёт304()
    {
        var (projectId, _) = await CreateProjectSessionAsync();

        var first = await GetListAsync(projectId);
        first.EnsureSuccessStatusCode();
        var etag = first.Headers.ETag?.ToString();
        etag.Should().NotBeNullOrEmpty();
        first.Headers.CacheControl!.NoCache.Should().BeTrue("браузер обязан ревалидировать, а не отдавать без спроса");

        var second = await GetListAsync(projectId, etag);
        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsStringAsync()).Should().BeEmpty();

        // Слабая форма тега — так его присылает браузер после сжатия ответа
        var weak = await GetListAsync(projectId, "W/" + etag);
        weak.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task ИзменилосьПолеСессии_ТегНовый_ТелоСнова200()
    {
        var (projectId, sessionId) = await CreateProjectSessionAsync();
        var etag = (await GetListAsync(projectId)).Headers.ETag!.ToString();

        // Полный цикл: правка → тот же запрос с прежним тегом
        var rename = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}", new { name = "Новое имя" });
        rename.EnsureSuccessStatusCode();

        var after = await GetListAsync(projectId, etag);
        after.StatusCode.Should().Be(HttpStatusCode.OK, "список изменился — 304 дал бы клиенту устаревшие данные");
        after.Headers.ETag!.ToString().Should().NotBe(etag);
        var item = JsonSerializer.Deserialize<JsonElement>(await after.Content.ReadAsStringAsync())[0];
        item.GetProperty("name").GetString().Should().Be("Новое имя");
    }

    [Fact]
    public async Task ИзменилсяUpdatedAt_ТегНовый()
    {
        var (projectId, sessionId) = await CreateProjectSessionAsync();
        var etag = (await GetListAsync(projectId)).Headers.ETag!.ToString();

        // Движение updatedAt (новая активность в чате) обязано инвалидировать тег
        factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!.UpdatedAt =
            DateTime.UtcNow.AddMinutes(5);

        var after = await GetListAsync(projectId, etag);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        after.Headers.ETag!.ToString().Should().NotBe(etag);
    }
}
