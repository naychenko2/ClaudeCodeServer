using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Chats;

// Чат идёт за файлом (ADR-018 §1, шаг 11 плана v2): сохранение с chatSessionId переводит чат
// на новый файл и пишет image_file_moved (запись двигает UpdatedAt); переименование файла или
// папки через файловый API переписывает пути чатов картинок, не двигая UpdatedAt; чужой чат в
// save не трогается и не выдаётся ответом.
public class ImageChatFollowsFileTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly OneVariantJobs _jobs = new();
    private readonly HttpClient _client;
    private readonly string _projectId;
    private readonly string _root;

    public ImageChatFollowsFileTests()
    {
        _factory.ExtraServices = services => services.AddSingleton<IImageEditJobs>(_jobs);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.TestUsername);
        (_projectId, _root) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        _client = _factory.CreateAuthenticatedClient();
        WriteImage("images/hero.png");
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    // Одна задача «job-1» с единственным вариантом 1 — у любого владельца
    private sealed class OneVariantJobs : IImageEditJobs
    {
        public const string JobId = "job-1";
        public readonly byte[] Bytes = Png();

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct) =>
            throw new NotSupportedException();

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) => null;

        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct) =>
            Task.FromResult<ImageEditJobDto?>(null);

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) =>
            jobId == JobId && variant == 1 ? new EditedImage(Bytes, "image/png") : null;
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string Editor(string? projectId = null) => $"/api/projects/{projectId ?? _projectId}/image-editor";

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 6, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void WriteImage(string rel, string? root = null)
    {
        var full = Path.Combine(root ?? _root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Png());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    // Чат картинки с ключом истории, как после первого сообщения: иначе записи в ленту некуда лечь
    private async Task<Session> CreateChat(HttpClient? client = null, string? projectId = null, string sourcePath = "images/hero.png")
    {
        var resp = await (client ?? _client).PostAsJsonAsync($"{Editor(projectId)}/chats", new { sourcePath });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var session = Sessions.GetById((await Json(resp)).GetProperty("id").GetString()!)!;
        session.ClaudeSessionId = Guid.NewGuid().ToString();
        return session;
    }

    private Task<HttpResponseMessage> SaveNextVersion(string? chatSessionId) =>
        _client.PostAsJsonAsync($"{Editor()}/save",
            new { jobId = OneVariantJobs.JobId, variant = 1, sourcePath = "images/hero.png", chatSessionId });

    private async Task Rename(string oldPath, string newPath)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/files/rename", new { oldPath, newPath });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    private async Task<List<string>> Continued(string path)
    {
        var resp = await _client.GetAsync($"{Editor()}/chats?path={Uri.EscapeDataString(path)}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(resp)).GetProperty("continued").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()!).ToList();
    }

    // ── Перенос при сохранении ──────────────────────────────────────────────────

    [Fact]
    public async Task Сохранение_v2_с_чатом_переводит_чат_и_пишет_image_file_moved()
    {
        var chat = await CreateChat();
        var stamp = DateTime.UtcNow.AddDays(-1);
        chat.UpdatedAt = stamp;

        var resp = await SaveNextVersion(chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = (await Json(resp)).GetProperty("path").GetString();
        saved.Should().Be("images/hero.v2.png");
        chat.ImageChat!.CurrentPath.Should().Be("images/hero.v2.png");
        chat.ImageChat.Lineage.Should().Equal("images/hero.png");
        chat.UpdatedAt.Should().BeAfter(stamp, "запись в ленту — активность, она двигает UpdatedAt");

        var moved = (await Sessions.GetHistoryAsync(chat.Id)).OfType<StoredImageFileMovedMessage>()
            .Should().ContainSingle().Subject;
        moved.From.Should().Be("images/hero.png");
        moved.To.Should().Be("images/hero.v2.png");

        (await Continued("images/hero.png")).Should().Equal([chat.Id], "по старому пути чат находится через Lineage");
    }

    [Fact]
    public async Task Чужой_и_обычный_чат_в_save_файл_сохранён_чат_не_тронут_ответ_тот_же()
    {
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);
        var (foreignProject, foreignRoot) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.SecondUsername);
        WriteImage("images/hero.png", foreignRoot);
        var foreign = await CreateChat(second, foreignProject);
        var foreignStamp = foreign.UpdatedAt;
        var plainResp = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/sessions", new { mode = "auto" });
        var plainId = (await Json(plainResp)).GetProperty("id").GetString()!;

        var bodies = new List<string>();
        foreach (var chatId in new[] { "no-such-session", foreign.Id, plainId })
        {
            var resp = await SaveNextVersion(chatId);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, chatId);
            var body = await Json(resp);
            body.EnumerateObject().Select(p => p.Name).Should().Equal(["path"], "ответ не выдаёт, есть ли такой чат");
            bodies.Add(body.GetProperty("path").GetString()!);
        }

        bodies.Should().Equal("images/hero.v2.png", "images/hero.v3.png", "images/hero.v4.png");
        foreign.ImageChat!.CurrentPath.Should().Be("images/hero.png", "чужой чат не тронут");
        foreign.ImageChat.Lineage.Should().BeEmpty();
        foreign.UpdatedAt.Should().Be(foreignStamp);
        (await Sessions.GetHistoryAsync(foreign.Id)).Should().BeEmpty();
        (await Sessions.GetHistoryAsync(plainId)).Should().BeEmpty();
    }

    // ── Трекер переименований ───────────────────────────────────────────────────

    [Fact]
    public async Task Переименование_файла_переписывает_путь_не_двигая_UpdatedAt()
    {
        var chat = await CreateChat();
        var stamp = DateTime.UtcNow.AddDays(-1);
        chat.UpdatedAt = stamp;
        Sessions.SetArchived(chat.Id, true, "user");

        await Rename("images/hero.png", "images/hero-evening.png");

        chat.ImageChat!.CurrentPath.Should().Be("images/hero-evening.png");
        chat.ImageChat.Lineage.Should().BeEmpty("переименование — тот же файл, а не новая версия");
        chat.UpdatedAt.Should().Be(stamp, "переименование не поднимает чат");
        chat.IsArchived.Should().BeTrue("и не выводит его из архива");
        (await Sessions.GetHistoryAsync(chat.Id)).Should().BeEmpty("в ленту трекер не пишет");
    }

    [Fact]
    public async Task Переименование_папки_переписывает_текущий_путь_и_Lineage_префиксом_только_в_своём_проекте()
    {
        WriteImage("images/blog/hero.v2.png");
        WriteImage("images2/hero.png");
        var chat = await CreateChat();
        Sessions.SetImageChatPath(chat.Id, "images/blog/hero.v2.png");
        var neighbour = await CreateChat(sourcePath: "images2/hero.png");
        var stamp = DateTime.UtcNow.AddDays(-1);
        chat.UpdatedAt = stamp;

        // Тот же относительный путь у второго владельца в его папке не трогается
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        CharacterEndpointsTests.EnableFlag(_factory, TestWebApplicationFactory.SecondUsername);
        var (foreignProject, foreignRoot) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.SecondUsername);
        WriteImage("images/hero.png", foreignRoot);
        var foreign = await CreateChat(second, foreignProject);

        await Rename("images", "art");

        chat.ImageChat!.CurrentPath.Should().Be("art/blog/hero.v2.png");
        chat.ImageChat.Lineage.Should().Equal("art/hero.png");
        chat.UpdatedAt.Should().Be(stamp);
        neighbour.ImageChat!.CurrentPath.Should().Be("images2/hero.png", "images2 — не внутри images");
        foreign.ImageChat!.CurrentPath.Should().Be("images/hero.png", "чужой проект не тронут");

        (await Continued("art/hero.png")).Should().Equal(chat.Id);
    }

    [Fact]
    public async Task Удаление_файла_привязку_оставляет()
    {
        var chat = await CreateChat();

        var resp = await _client.DeleteAsync($"/api/projects/{_projectId}/files?path={Uri.EscapeDataString("images/hero.png")}");

        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());
        File.Exists(Path.Combine(_root, "images", "hero.png")).Should().BeFalse();
        chat.ImageChat!.CurrentPath.Should().Be("images/hero.png");
    }
}
