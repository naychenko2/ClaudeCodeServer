using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-B7: GET /api/chats/{id}/attachments/raw?path= — чтение картинки-вложения для миниатюры
/// (ADR-041 «Контракт», ручка №12). Инвариант: отдаётся только учтённая картинка видимого чата.
/// Каждый путь отказа — отдельным тестом: путь вне Attachments, «..», чужой private-автор,
/// чужой чат, детский чат без ChildSpace, не-картинка, локальный проект. Тип — по сигнатуре байтов
/// (JPEG-байты под .png отдаются как image/jpeg).
/// </summary>
[Trait("Category", "Host")]
public class ChatAttachmentRawControllerTests : IClassFixture<ChatAttachmentRawControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-raw-att-" + Guid.NewGuid().ToString("N"));
            ExtraServices = services => services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
        }
    }

    private sealed class FixedSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0.1m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0.5m;
    }

    private const string KidsKey = "kids-api";
    private const string ChildPassword = "kids-test-password";
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Gif = "GIF89a\x01\0\x01\0"u8.ToArray();
    private static readonly byte[] Webp = "RIFF\x24\0\0\0WEBPVP8 "u8.ToArray();

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();

    public ChatAttachmentRawControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        _admin = _hosts.CreateAuthenticatedClient();
    }

    // ─── стенд ───

    private async Task<string> NewChild(string name)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, providerKey = KidsKey, model = "sonnet",
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new
                {
                    windows = new[] { new { from = local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture), to = local.AddHours(+3).ToString("HH:mm", CultureInfo.InvariantCulture) } },
                    dailyMinutes = 40,
                },
            },
            dailyBudgetUsd = 2m, monthlyBudgetUsd = 40m,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        ChildTestSubjects.Seed(_hosts.Services, id);
        var p = _hosts.Services.GetRequiredService<UserStore>().GetById(id)!.ChildMode!;
        p.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        p.RulesSummary = "правила";
        return id;
    }

    // Детский чат: (id ребёнка, id чата, корень чата)
    private async Task<(string ChildId, string ChatId, string Root)> ChildChat(string name)
    {
        var id = await NewChild(name);
        var resp = await ChildTestSubjects.CreateChat(_hosts.CreateAuthenticatedClient(name, ChildPassword));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var chatId = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("sessionId").GetString()!;
        return (id, chatId, Sessions.GetChatRoot(chatId, id)!);
    }

    // Взрослый чат вне проекта (владелец — админ тестового хоста): (id чата, корень)
    private async Task<(string ChatId, string Root, string OwnerId)> AdultChat()
    {
        var resp = await _admin.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        var chatId = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var chat = Sessions.GetById(chatId)!;
        return (chatId, Sessions.GetChatRoot(chatId, chat.OwnerId!)!, chat.OwnerId!);
    }

    // Файл на диске + запись в учёт — как после загрузки
    private string Seed(string chatId, string root, string rel, byte[] bytes, bool recorded = true)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, bytes);
        if (recorded) (Sessions.GetById(chatId)!.Attachments ??= []).Add(rel);
        return rel;
    }

    private static string Url(string chatId, string path) =>
        $"/api/chats/{chatId}/attachments/raw?path={Uri.EscapeDataString(path)}";

    private static async Task<string> CodeOf(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return text.Length == 0 ? "" : JsonSerializer.Deserialize<JsonElement>(text).GetProperty("code").GetString()!;
    }

    private async Task<HttpResponseMessage> UploadAdult(HttpClient c, string chatId, string name, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent { { content, "file", name } };
        return await c.PostAsync($"/api/chats/{chatId}/files/upload", form);
    }

    // ─── 200: заголовки, типы по сигнатуре ───

    [Fact]
    public async Task Взрослый_УчтённаяКартинка_200_БайтыИЗаголовки()
    {
        var (chat, root, _) = await AdultChat();
        var up = await UploadAdult(_admin, chat, "схема.png", Png);
        var rel = JsonSerializer.Deserialize<JsonElement>(await up.Content.ReadAsStringAsync()).GetProperty("path").GetString()!;

        var r = await _admin.GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        r.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        r.Headers.GetValues("Content-Security-Policy").Should().Equal("sandbox");
        r.Headers.CacheControl!.Private.Should().BeTrue();
        r.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(86400));
        (await r.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
    }

    // <img> не умеет слать заголовок: JWT приезжает в ?access_token= (обработчик OnMessageReceived)
    [Fact]
    public async Task ТокенТолькоВQuery_AccessToken_200()
    {
        var (chat, root, _) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png);
        var token = _admin.DefaultRequestHeaders.Authorization!.Parameter!;

        var anon = _hosts.CreateClient();
        (await anon.GetAsync(Url(chat, rel))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var r = await anon.GetAsync(Url(chat, rel) + "&access_token=" + Uri.EscapeDataString(token));

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }

    [Theory]
    [InlineData("a.png", "image/jpeg")] // JPEG-байты под .png: тип по сигнатуре, не по расширению
    [InlineData("b.dat", "image/gif")]
    [InlineData("c.gif", "image/webp")]
    public async Task Тип_ПоСигнатуре_НеПоРасширению(string name, string expectedMime)
    {
        var (chat, root, _) = await AdultChat();
        var bytes = expectedMime switch { "image/jpeg" => Jpeg, "image/gif" => Gif, _ => Webp };
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/{name}", bytes);

        var r = await _admin.GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be(expectedMime);
    }

    // И6 №3 (К3, разрез «Знания репетитора» §4): ручка миниатюр не отдаёт материалы предмета —
    // каталог data/child-subjects вне корня чата. Три формы пути (абсолютный, обход от корня, «data/…»),
    // каждая и без учёта, и с записью в Session.Attachments: ни одна не 2xx, содержимого в ответе нет.
    [Fact]
    public async Task ДетскийЧат_ПутьВКаталогМатериаловПредмета_НеОтдаётся()
    {
        var name = "kid-raw-materials";
        var (childId, chatId, root) = await ChildChat(name);
        var dataDir = Path.GetDirectoryName(ChildSubjectMaterialsStore.RootFromConfig(
            _hosts.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()))!;
        var materials = Path.Combine(dataDir, ChildSubjectMaterialsStore.DirName, childId, "russian", "text", "m_000000000001.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(materials)!);
        File.WriteAllBytes(materials, Png); // даже валидная картинка по сигнатуре не должна уйти ребёнку
        var traversal = Path.GetRelativePath(root, materials).Replace('\\', '/');
        var viaData = $"data/{ChildSubjectMaterialsStore.DirName}/{childId}/russian/text/m_000000000001.txt";
        var attachments = Sessions.GetById(chatId)!.Attachments ??= [];
        using var child = _hosts.CreateAuthenticatedClient(name, ChildPassword);

        foreach (var recorded in new[] { false, true })
        {
            if (recorded) { attachments.Add(materials); attachments.Add(traversal); attachments.Add(viaData); }
            foreach (var path in new[] { materials, traversal, viaData })
            {
                var r = await child.GetAsync(Url(chatId, path));
                ((int)r.StatusCode).Should().BeGreaterThanOrEqualTo(400, $"путь «{path}» (учтён: {recorded})");
                (await r.Content.ReadAsByteArrayAsync()).Should().NotEqual(Png, "байты материала ребёнку не уходят");
            }
        }
    }

    [Fact]
    public async Task Путь_ОбратныеСлешиИВедущийСлеш_НормализуютсяДоТойЖеЗаписи()
    {
        var (chat, root, _) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png);

        (await _admin.GetAsync(Url(chat, "/" + rel.Replace('/', '\\')))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ребёнок_СвойДетскийЧат_200()
    {
        var (_, chat, root) = await ChildChat("kid-raw-ok");
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/pic.png", Png);

        var r = await _hosts.CreateAuthenticatedClient("kid-raw-ok", ChildPassword).GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }

    // ─── 404: каждый путь отказа отдельным тестом ───

    [Fact]
    public async Task ПутьВнеAttachments_ДажеЕслиФайлЕсть_404_FileNotFound()
    {
        var (chat, root, _) = await AdultChat();
        Seed(chat, root, "secret.png", Png, recorded: false);

        var r = await _admin.GetAsync(Url(chat, "secret.png"));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    // Префикс не годится: файл лежит под .cc-attachments/, но в учёте его нет
    [Fact]
    public async Task ПодAttachments_НоНеУчтён_404_FileNotFound()
    {
        var (chat, root, _) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png, recorded: false);

        var r = await _admin.GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData(".cc-attachments/../../outside.png")]
    public async Task ТочкиТочки_404_FileNotFound(string path)
    {
        var (chat, root, _) = await AdultChat();
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(root.TrimEnd('/', '\\'))!, "outside.png"), Png);

        var r = await _admin.GetAsync(Url(chat, path));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    // Запись учёта с выходом за корень (испорченный sessions.json): SafePath.Join отказывает
    [Fact]
    public async Task УчтённыйПутьЗаКорень_404_FileNotFound()
    {
        var (chat, root, _) = await AdultChat();
        var outside = Path.Combine(Path.GetDirectoryName(root.TrimEnd('/', '\\'))!, "leak.png");
        File.WriteAllBytes(outside, Png);
        (Sessions.GetById(chat)!.Attachments ??= []).Add("../leak.png");

        var r = await _admin.GetAsync(Url(chat, "../leak.png"));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    [Fact]
    public async Task ЧужойPrivateАвтор_ДажеУчтённый_404_FileNotFound()
    {
        var (chat, root, _) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/private/other-author/{Guid.NewGuid():N}/x.png", Png);

        var r = await _admin.GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    [Fact]
    public async Task ЧужойЧат_404_ChatNotFound()
    {
        var (chat, root, _) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png);
        await NewChild("kid-raw-stranger");

        var r = await _hosts.CreateAuthenticatedClient("kid-raw-stranger", ChildPassword).GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("chat_not_found");
    }

    [Fact]
    public async Task ЧужойДетскийЧат_ДругимРебёнком_404_ChatNotFound()
    {
        var (_, chat, root) = await ChildChat("kid-raw-owner");
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png);
        await NewChild("kid-raw-other");

        var r = await _hosts.CreateAuthenticatedClient("kid-raw-other", ChildPassword).GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("chat_not_found");
    }

    [Fact]
    public async Task НесуществующийЧат_404_ChatNotFound()
    {
        var r = await _admin.GetAsync(Url("no-such-chat", ".cc-attachments/a/x.png"));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("chat_not_found");
    }

    // Свой чат ребёнка, но без ChildSpace (сброшен): для ребёнка неотличим от чужого
    [Fact]
    public async Task ДетскийЧатБезChildSpace_404_ChatNotFound()
    {
        var (_, chat, root) = await ChildChat("kid-raw-nospace");
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png);
        Sessions.GetById(chat)!.ChildSpace = null;

        var r = await _hosts.CreateAuthenticatedClient("kid-raw-nospace", ChildPassword).GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("chat_not_found");
    }

    [Fact]
    public async Task НеКартинка_УчтённыйТекст_404_FileNotFound_Не415()
    {
        var (chat, root, _) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/note.png", Encoding.UTF8.GetBytes("это текст, не картинка"));

        var r = await _admin.GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    [Fact]
    public async Task УчтённыйФайлУдалён_404_FileNotFound()
    {
        var (chat, root, _) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png);
        File.Delete(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

        var r = await _admin.GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    // Локальный проект: файлы на устройстве, диск сервера не читается — даже если файл там есть
    [Fact]
    public async Task ЧатЛокальногоПроекта_404_FileNotFound_БезДиска()
    {
        var (chat, root, ownerId) = await AdultChat();
        var rel = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/x.png", Png);
        var projects = _hosts.Services.GetRequiredService<ProjectManager>();
        var project = projects.Create("local-raw-" + Guid.NewGuid().ToString("N")[..6], Path.Combine(Path.GetTempPath(), "ccs-local-raw-" + Guid.NewGuid().ToString("N")), ownerId, "owner", createDirectory: true);
        project.DeviceId = "device-1";
        Sessions.GetById(chat)!.ProjectId = project.Id;

        var r = await _admin.GetAsync(Url(chat, rel));

        r.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CodeOf(r)).Should().Be("file_not_found");
    }

    // ─── конкурентность ───

    // Перенос приватности переписывает путь под VisibilityLock: старый путь — 404, новый — 200.
    // Перенос здесь воспроизведён перепиской учёта и файла (SetChatVisibility требует общий проект
    // с участниками — он покрыт ChatVisibilityEndpointTests); замок читателя проверяет гонка ниже.
    [Fact]
    public async Task ПослеПереписиПутей_СтарыйПуть404_НовыйПуть200()
    {
        var (chat, root, _) = await AdultChat();
        var guid = Guid.NewGuid().ToString("N");
        var oldRel = Seed(chat, root, $".cc-attachments/{guid}/x.png", Png);
        var newRel = $".cc-attachments/private/{Sessions.GetById(chat)!.OwnerId}/{guid}/x.png";
        var entry = Sessions.GetById(chat)!;
        {
            var from = Path.Combine(root, oldRel.Replace('/', Path.DirectorySeparatorChar));
            var to = Path.Combine(root, newRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            entry.Attachments![entry.Attachments.IndexOf(oldRel)] = newRel;
        }

        (await _admin.GetAsync(Url(chat, oldRel))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.GetAsync(Url(chat, newRel))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Raw_ParallelUploadAndRead_NoException()
    {
        var (chat, root, ownerId) = await AdultChat();
        var seeded = Seed(chat, root, $".cc-attachments/{Guid.NewGuid():N}/seed.png", Png);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                try { Sessions.SaveChatAttachment(chat, ownerId, $"p{i}.png", r => File.WriteAllBytes(EnsureDir(Path.Combine(root, r)), Png)); }
                catch (Exception e) { failures.Add("writer: " + e.Message); }
            }
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    var r = await _admin.GetAsync(Url(chat, seeded));
                    if (r.StatusCode != HttpStatusCode.OK) failures.Add("reader: " + r.StatusCode);
                }
                catch (Exception e) { failures.Add("reader: " + e.Message); }
            }
        });
        await Task.WhenAll(writer, reader);

        failures.Should().BeEmpty();
        Sessions.GetById(chat)!.Attachments!.Count.Should().Be(101);
    }

    private static string EnsureDir(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }
}
