using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-B6e: загрузка вложения в детский чат — POST /api/child/chats/{id}/attachments
/// (ADR-041 «Контракт», ручка №8; решение Андрея №5, вариант «б»). Тип — по сигнатуре байтов
/// (переименованный .exe в .png остаётся .exe → 415), PDF конвертируется на сервере
/// (тот же путь, что files_document_read), текст отдаётся рядом .txt, скан/битый PDF — 422.
///
/// Конвертер — «печать файла как есть» (Markitdown:Command = cat/type, приём
/// FilesControllerEditorDenialsTests): тест меряет мою цепочку (сигнатура → SaveChatAttachment →
/// конвертер → .txt → путь наружу), а не markitdown; настоящий markitdown в CI нет.
/// «Скан/битый» — те же байты с нулевыми: результат конвертера с нулевыми байтами не текст
/// (IsUsableText) → pdf_no_text. Лимит размера опущен конфигурацией до 300 КБ: «на байт больше»
/// дешёвый, а тест отсечения (текст > 256 КБ) проходит под этим же лимитом.
/// Профиль ребёнка — боевая админская ручка, чат — боевая детская (образец ChildChatsControllerTests).
/// </summary>
[Trait("Category", "Host")]
public class ChildAttachmentsControllerTests : IClassFixture<ChildAttachmentsControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            ExtraConfig["ChildMode:MaxAttachmentBytes"] = (300 * 1024).ToString(CultureInfo.InvariantCulture);
            ExtraConfig["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat";
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
    private const int MaxBytes = 300 * 1024;
    private const int TextBudget = 256 * 1024;
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    // Минимальная PNG-сигнатура: 8 байт шапки + IHDR-начало — уже PNG по сигнатуре
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();

    public ChildAttachmentsControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        // Ребёнок — container-владелец: корень его чатов лежит в песочнице (CliRoute.ResolveChatRoot)
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-child-attachments-" + Guid.NewGuid().ToString("N"));
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
    }

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
        var p = _users.GetById(id)!.ChildMode!;
        p.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        p.RulesSummary = "правила";
        return id;
    }

    private HttpClient ChildClient(string name) => _hosts.CreateAuthenticatedClient(name, ChildPassword);

    private async Task<string> CreateChatAs(HttpClient client)
    {
        var (status, body, _) = await ReadAsync(await client.PostAsync("/api/child/spaces/lessons/chat", null));
        status.Should().Be(HttpStatusCode.Created);
        return body.GetProperty("sessionId").GetString()!;
    }

    // Ребёнок с согласованными правилами и своим детским чатом: (id, chatId, абсолютный корень чата)
    private async Task<(string ChildId, string ChatId, string Root)> NewChildWithChat(string name)
    {
        var id = await NewChild(name);
        var chatId = await CreateChatAs(ChildClient(name));
        var root = Sessions.GetChatRoot(chatId, id)!.Replace('\\', '/');
        return (id, chatId, root);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default, text);
    }

    private static string CodeOf(JsonElement b) => b.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    private static MultipartFormDataContent Multipart(string name, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent();
        form.Add(content, "file", name);
        return form;
    }

    private static string Abs(string root, string rel) =>
        Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    // Байты на диске равны отправленным (содержимое не переписано по дороге)
    private static void SameBytes(string root, string rel, byte[] sent) =>
        File.ReadAllBytes(Abs(root, rel)).Should().Equal(sent);

    // Путь в общей раскладке: {AttachmentsDir}/{guid}/{имя} — та же, что у обычной загрузки
    private static string PathRegex(string name) => $@"^\.cc-attachments/[0-9a-f]{{32}}/{name}$";

    // ─── allow-list по сигнатуре (расширение и Content-Type не смотрятся) ───

    [Fact]
    public async Task Upload_Png_200_Image_ПутьВОбщейРаскладке_БайтыЦелы()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-png");
        var client = ChildClient("kid-att-png");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("схема.png", Png)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("image");
        b.GetProperty("name").GetString().Should().Be("схема.png");
        var path = b.GetProperty("path").GetString()!;
        path.Should().MatchRegex(PathRegex("схема.png"));
        SameBytes(root, path, Png);
    }

    // .exe с переименованием в .png: сигнатура остаётся .exe — 415, файл не сохранён
    [Fact]
    public async Task Upload_ExeПереименованныйВ_Png_415_UnsupportedType_ФайлаНет()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-exe");
        var client = ChildClient("kid-att-exe");
        var exe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 }; // "MZ" — DOS/PE заголовок

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("driver.png", exe)));

        s.Should().Be(HttpStatusCode.UnsupportedMediaType);
        CodeOf(b).Should().Be("unsupported_type");
        Directory.Exists(Path.Combine(root, ".cc-attachments")).Should().BeFalse("отказ по типу не оставляет файла");
        Sessions.GetById(chat)!.Attachments.Should().BeNullOrEmpty("и учёта несостоявшегося вложения нет");
    }

    [Fact]
    public async Task Upload_НаБайтБольшеЛимита_413_TooLarge()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-large");
        var client = ChildClient("kid-att-large");
        var over = new byte[MaxBytes + 1];
        Png.CopyTo(over, 0);

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("большой.png", over)));

        s.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        CodeOf(b).Should().Be("too_large");
        Directory.Exists(Path.Combine(root, ".cc-attachments")).Should().BeFalse();
    }

    [Fact]
    public async Task Upload_ПустойФайл_422_EmptyFile()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-empty");
        var client = ChildClient("kid-att-empty");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("пусто.txt", [])));

        s.Should().Be(HttpStatusCode.UnprocessableEntity);
        CodeOf(b).Should().Be("empty_file");
        Directory.Exists(Path.Combine(root, ".cc-attachments")).Should().BeFalse();
    }

    // PDF без текстового слоя (скан): конвертер вернул то, что в файле, — с нулевыми байтами.
    // Результат с нулевыми байтами не текст (тот же критерий, что text/plain) → pdf_no_text;
    // исключения наружу нет, в ход PDF не уходит (путь не отдан).
    [Fact]
    public async Task Upload_PdfСканБезТекста_422_PdfNoText()
    {
        var (_, chat, _) = await NewChildWithChat("kid-att-scan");
        var client = ChildClient("kid-att-scan");
        var head = Encoding.UTF8.GetBytes("%PDF-1.7\n");
        var tail = new byte[] { 0x00, 0x01, 0x00, 0xFF, 0xFE, 0x00, 0x80 };
        var scan = new byte[head.Length + tail.Length];
        head.CopyTo(scan, 0);
        tail.CopyTo(scan, head.Length);

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("скан.pdf", scan)));

        s.Should().Be(HttpStatusCode.UnprocessableEntity);
        CodeOf(b).Should().Be("pdf_no_text");
    }

    // ─── текст и PDF → текст ───

    [Fact]
    public async Task Upload_Txt_200_Text_ФайлКакЕсть()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-txt");
        var client = ChildClient("kid-att-txt");
        var bytes = Encoding.UTF8.GetBytes("задача: собрать пазл\nшаг 1 — найти углы");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("задача.txt", bytes)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("text");
        var path = b.GetProperty("path").GetString()!;
        path.Should().MatchRegex(PathRegex("задача.txt"));
        SameBytes(root, path, bytes);
    }

    // c2: PDF с текстовым слоем и случайным маркером → kind=text, path на .txt рядом с PDF;
    // инлайн сообщения (тот же AttachmentInliner, что у обычных вложений) несёт маркер и
    // НЕ несёт пометку бинарного файла («открой инструментом чтения» — ребёнку Read закрыт).
    [Fact]
    public async Task Upload_PdfСТекстовымСлоем_200_Text_TxtРядомСPdf_ИнлайнНесётМаркер()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-pdf");
        var client = ChildClient("kid-att-pdf");
        var marker = "МАРКЕР-" + Guid.NewGuid().ToString("N");
        var pdf = Encoding.UTF8.GetBytes($"%PDF-1.7\nBT /F1 24 Tf 72 700 Td ({marker}) Tj ET\nУрок: дроби. %%EOF\n");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("урок.pdf", pdf)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("text");
        b.GetProperty("name").GetString().Should().Be("урок.pdf");
        var txtRel = b.GetProperty("path").GetString()!;
        txtRel.Should().MatchRegex(PathRegex("урок.txt"));

        // .txt рядом с PDF: тот же {guid}-каталог, что у учтённого оригинала
        var pdfRel = Sessions.GetById(chat)!.Attachments.Should().ContainSingle().Which;
        Path.GetDirectoryName(txtRel).Should().Be(Path.GetDirectoryName(pdfRel));
        File.Exists(Abs(root, pdfRel)).Should().BeTrue("оригинал остаётся");
        SameBytes(root, pdfRel, pdf);

        var inlined = AttachmentInliner.BuildMessageText(root, "объясни задачу", [txtRel]);
        inlined.Should().Contain(marker);
        inlined.Should().NotContain("открой инструментом чтения");
    }

    // Текст больше бюджета инлайна (256 КБ) — обрезан с явной пометкой на месте обрыва
    [Fact]
    public async Task Upload_PdfСТекстомБольше256КБ_ОбрезанСПометкой_НеБольшеБюджета()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-trunc");
        var client = ChildClient("kid-att-trunc");
        var body = new string('а', 150_000); // «а» — 2 байта UTF-8: 300 КБ (> 256 КБ бюджета, < 300 КБ лимита)
        var marker = "МАРКЕР-" + Guid.NewGuid().ToString("N");
        var pdf = Encoding.UTF8.GetBytes($"%PDF-1.7\n{marker}\n{body}\n%%EOF\n");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("длинный.pdf", pdf)));

        s.Should().Be(HttpStatusCode.OK);
        var txtRel = b.GetProperty("path").GetString()!;
        var txt = File.ReadAllText(Abs(root, txtRel));
        txt.Should().Contain(marker);
        txt.Should().EndWith("…(текст обрезан по размеру)");
        var len = Encoding.UTF8.GetByteCount(txt);
        len.Should().BeLessThanOrEqualTo(TextBudget, "итог не больше бюджета вместе с пометкой");
        // обрезан у самой границы, а не произвольно короче: не хватает не больше размера одного символа
        len.Should().BeGreaterThan(TextBudget - 4);
    }

    // ─── доступ к чату ───

    [Fact]
    public async Task Upload_ЧужойДетскийИлиНесуществующий_404_chat_not_found_ОдноТело()
    {
        var (_, chatB, rootB) = await NewChildWithChat("kid-att-404-b");
        await NewChild("kid-att-404-a");
        var clientA = ChildClient("kid-att-404-a");

        var (s1, b1, raw1) = await ReadAsync(await clientA.PostAsync($"/api/child/chats/{chatB}/attachments", Multipart("схема.png", Png)));
        var (s2, b2, raw2) = await ReadAsync(await clientA.PostAsync($"/api/child/chats/{Guid.NewGuid():N}/attachments", Multipart("схема.png", Png)));

        s1.Should().Be(HttpStatusCode.NotFound);
        CodeOf(b1).Should().Be("chat_not_found");
        // существование чужого чата не раскрывается: тело неотличимо от несуществующего
        s2.Should().Be(s1);
        raw2.Should().Be(raw1);
        Directory.Exists(Path.Combine(rootB, ".cc-attachments")).Should().BeFalse("чужой чат не получил файла");
    }

    [Fact]
    public async Task Upload_Взрослый_403_not_child()
    {
        var adultChat = (await Sessions.CreateChatAsync(
            _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id, ClaudeMode.Auto, name: "взрослый")).Id;

        var (s, b, _) = await ReadAsync(await _admin.PostAsync($"/api/child/chats/{adultChat}/attachments", Multipart("схема.png", Png)));

        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("not_child");
    }

    // ─── конкурентность ───

    [Fact]
    public async Task Upload_ParallelSameName_BothSaved()
    {
        var (_, chat, root) = await NewChildWithChat("kid-att-race");
        var client = ChildClient("kid-att-race");
        var first = Encoding.UTF8.GetBytes("первое содержимое");
        var second = Encoding.UTF8.GetBytes("второе содержимое");

        var responses = await Task.WhenAll(
            client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("заметка.txt", first)),
            client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("заметка.txt", second)));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        var paths = new List<string>();
        foreach (var r in responses)
        {
            var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            paths.Add(body.GetProperty("path").GetString()!);
        }
        paths.Distinct().Should().HaveCount(2, "своя {guid}-подпапка на каждый файл: одно имя не затирает другое");
        SameBytes(root, paths[0], first);
        SameBytes(root, paths[1], second);
        Sessions.GetById(chat)!.Attachments.Should().HaveCount(2);
    }
}
