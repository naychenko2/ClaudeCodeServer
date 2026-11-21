using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Загрузка вложения в урок (ADR-041 «Контракт», ручка №8; решение Андрея №5, вариант «б»;
/// с решения 89 — запись в папку предмета). Маршрут и коды те же; меняется то, что внутри:
/// файл пишет общий писатель ChildTextCopyService в папку предмета этого урока по пути
/// «Вложения/{имя}», а его текстовую копию — по форме «.ccs-text/{путь}.txt» (решение 90).
/// Ответ — путь оригинала: его фронт передаёт в attachedPaths, а копию до хода подставляет
/// фильтр хаба (ChildHubFilter, Б10e) — без него текст PDF до модели не дошёл бы.
/// `.cc-attachments` и учёта в Session.Attachments у урока больше нет.
///
/// Фикстура создаёт урок в проекте-предмете штатным POST (образец ChildChatReadTests:
/// предмет с согласием на риск маршрутов и назначение модели репетитора — без него живой
/// маршрут не развёрнут и урок 409 subject_not_ready). Старый урок без проекта (решение 51)
/// вне ручки: 404 одним телом с несуществующим.
///
/// Конвертер — «печать файла как есть» (Markitdown:Command = cat/type, приём
/// FilesControllerEditorDenialsTests): тест меряет мою цепочку (сигнатура → писатель →
/// оригинал+копия → путь наружу → фильтр хаба), а не markitdown; настоящий markitdown в CI
/// нет. «Скан/битый» — те же байты с нулевыми: результат конвертера с нулевыми байтами не
/// текст (IsUsableText) → pdf_no_text, и оригинала на диске нет — его снимает писатель под
/// своим замком (решение 89). Лимит размера опущен конфигурацией до 300 КБ: «на байт больше»
/// дешёвый. Профиль ребёнка — боевая админская ручка, урок — боевой детский POST.
/// </summary>
[Trait("Category", "Host")]
public class ChildAttachmentsControllerTests :
    IClassFixture<ChildAttachmentsControllerTests.Hosts>,
    IClassFixture<ChildAttachmentsControllerTests.UnreadableConfigHosts>,
    IClassFixture<ChildAttachmentsControllerTests.ThrowingSaverHosts>
{
    // Не sealed: фикстуры отказа писателя ниже наследуют эту конфигурацию, меняя только ExtraServices.
    public class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            ExtraConfig["ChildMode:MaxAttachmentBytes"] = (300 * 1024).ToString(CultureInfo.InvariantCulture);
            ExtraConfig["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c type" : "cat";
            // Ребёнок — container-владелец: папка предмета лежит в песочнице (UserHomeResolver.SubjectHome)
            ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-child-attachments-" + Guid.NewGuid().ToString("N"));
            ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
            ExtraServices = services => services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
        }
    }

    // Хост с отказом писателя (Гейт-4·Г): тестам нужен свой хост — подмена ChildTextCopyService это
    // DI-регистрация, боевой писатель общего хоста класса нужен остальным тестам, а две разные
    // подмены в одном контейнере не живут. Наследники Hosts: та же конфигурация, свой ExtraServices.
    public sealed class UnreadableConfigHosts : Hosts
    {
        public UnreadableConfigHosts() => ExtraServices = services =>
        {
            services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
            services.AddSingleton<ChildTextCopyService>(sp => new ChildTextCopyService(
                sp.GetRequiredService<MarkitdownService>(), new UnreadableConfig()));
        };
    }

    public sealed class ThrowingSaverHosts : Hosts
    {
        public ThrowingSaverHosts() => ExtraServices = services =>
        {
            services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
            services.AddSingleton<ChildTextCopyService>(sp => new ThrowingSaver(
                sp.GetRequiredService<MarkitdownService>(), sp.GetRequiredService<IConfiguration>()));
        };
    }

    private sealed class FixedSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0.1m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0.5m;
    }

    // Конфиг, который не читается: метр папки в писателе — первое обращение к конфигурации
    // в его SaveAsync. Его исключение — та ветка отказа писателя, которой нет места в кодах
    // ручки (storage_error — это «не записал»; «не прочитал» — за пределы контракта):
    // 503 без исключения наружу и без полуприложения.
    private sealed class UnreadableConfig : IConfiguration
    {
        public string? this[string key]
        {
            get => throw new IOException("конфиг не читается");
            set => throw new IOException("конфиг не читается");
        }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
        public IConfigurationSection GetSection(string key) => throw new IOException("конфиг не читается");
    }

    private const string KidsKey = "kids-api";
    private const string ChildPassword = "kids-test-password";
    private const string TutorModel = "sonnet";
    private const int MaxBytes = 300 * 1024;
    private const int InlineBudget = 256 * 1024; // AttachmentInliner.MaxInlineBytes
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    // Минимальная PNG-сигнатура: 8 байт шапки + IHDR-начало — уже PNG по сигнатуре
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private readonly Hosts _hosts;
    private readonly UnreadableConfigHosts _unreadableHosts;
    private readonly ThrowingSaverHosts _throwingHosts;
    private readonly HttpClient _admin;
    private SessionManager Sessions => _hosts.Services.GetRequiredService<SessionManager>();

    public ChildAttachmentsControllerTests(Hosts hosts, UnreadableConfigHosts unreadableHosts, ThrowingSaverHosts throwingHosts)
    {
        _hosts = hosts;
        _unreadableHosts = unreadableHosts;
        _throwingHosts = throwingHosts;
        _admin = _hosts.CreateAuthenticatedClient();
    }

    private async Task<string> NewChild(string name) => await NewChildOn(_hosts, name);

    // Ребёнок с предметом, как в проде (образец ChildChatReadTests.NewSubjectChildAsync):
    // container-пользователь, настоящий детский профиль, согласие правил, фикстура предмета
    // и назначение модели репетитора (И10) — без него живой маршрут не развёрнут и урок 409.
    private static async Task<string> NewChildOn(TestWebApplicationFactory host, string name)
    {
        var admin = host.CreateAuthenticatedClient();
        var resp = await admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        var put = await admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, acknowledgeRouteRisk = true, providerKey = KidsKey, model = TutorModel,
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
        ChildTestSubjects.Seed(host.Services, id);
        var p = host.Services.GetRequiredService<UserStore>().GetById(id)!.ChildMode!;
        p.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        p.RulesSummary = "правила";
        (host.Services.GetRequiredService<LocalActionOverridesStore>().Set(LocalActionCatalog.ChatPersona, TutorModel)).Should().BeTrue();
        return id;
    }

    private HttpClient ChildClient(string name) => _hosts.CreateAuthenticatedClient(name, ChildPassword);

    // Штатный POST урока (Б9b): чат проекта-предмета от лица его руководителя
    // (тот же фикстур, что CreateLessonAsync в ChildChatReadTests).
    private static async Task<string> CreateLessonOn(TestWebApplicationFactory host, HttpClient client)
    {
        var resp = await ChildTestSubjects.CreateChat(client);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "штатный POST создаёт урок");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;
    }

    // Ребёнок с согласованными правилами и своим уроком: (id, chatId, абсолютная папка предмета)
    private async Task<(string ChildId, string ChatId, string Root)> NewChildWithLesson(string name)
    {
        var id = await NewChild(name);
        var chatId = await CreateLessonOn(_hosts, ChildClient(name));
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

    // Путь вложения урока — «Вложения/{имя}» папки предмета (решение 89): без guid-подпапки,
    // без .cc-attachments.
    private static string SubjectPathRegex(string name) => $@"^Вложения/{Regex.Escape(name)}$";

    private static byte[] PdfWithText(string marker, string body = "Урок: дроби.") =>
        Encoding.UTF8.GetBytes($"%PDF-1.7\nBT /F1 24 Tf 72 700 Td ({marker}) Tj ET\n{body} %%EOF\n");

    // ─── allow-list по сигнатуре (расширение и Content-Type не смотрятся) ───

    [Fact]
    public async Task Upload_Png_200_Image_ВПапкеПредмета_БайтыЦелы()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-png");
        var client = ChildClient("kid-att-png");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("схема.png", Png)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("image");
        b.GetProperty("name").GetString().Should().Be("схема.png");
        var path = b.GetProperty("path").GetString()!;
        path.Should().MatchRegex(SubjectPathRegex("схема.png"));
        SameBytes(root, path, Png);
        // учёт детских вложений урока в Session.Attachments снят (решение 89): после успешной
        // загрузки список чата остаётся пустым — путь судит папка предмета, не список чата
        Sessions.GetById(chat)!.Attachments.Should().BeNullOrEmpty("урок не ведёт Session.Attachments");
    }

    // .exe с переименованием в .png: сигнатура остаётся .exe — 415, файл не сохранён
    [Fact]
    public async Task Upload_ExeПереименованныйВ_Png_415_UnsupportedType_ФайлаНет()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-exe");
        var client = ChildClient("kid-att-exe");
        var exe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 }; // "MZ" — DOS/PE заголовок

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("driver.png", exe)));

        s.Should().Be(HttpStatusCode.UnsupportedMediaType);
        CodeOf(b).Should().Be("unsupported_type");
        Directory.Exists(Path.Combine(root, "Вложения")).Should().BeFalse("отказ по типу не оставляет файла");
        Sessions.GetById(chat)!.Attachments.Should().BeNullOrEmpty("и учёта несостоявшегося вложения нет");
    }

    [Fact]
    public async Task Upload_НаБайтБольшеЛимита_413_TooLarge()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-large");
        var client = ChildClient("kid-att-large");
        var over = new byte[MaxBytes + 1];
        Png.CopyTo(over, 0);

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("большой.png", over)));

        s.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        CodeOf(b).Should().Be("too_large");
        Directory.Exists(Path.Combine(root, "Вложения")).Should().BeFalse();
    }

    [Fact]
    public async Task Upload_ПустойФайл_422_EmptyFile()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-empty");
        var client = ChildClient("kid-att-empty");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("пусто.txt", [])));

        s.Should().Be(HttpStatusCode.UnprocessableEntity);
        CodeOf(b).Should().Be("empty_file");
        Directory.Exists(Path.Combine(root, "Вложения")).Should().BeFalse();
    }

    // PDF без текстового слоя (скан): конвертер вернул то, что в файле, — с нулевыми байтами.
    // Результат с нулевыми байтами не текст (тот же критерий, что text/plain) → pdf_no_text;
    // исключения наружу нет. С решения 89 (requireText) и самого оригинала на диске нет:
    // его снимает писатель под тем же замком — полуприложения в «Вложения/» не остаётся.
    [Fact]
    public async Task Upload_PdfСканБезТекста_422_PdfNoText_ОригиналСнятПисателем()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-scan");
        var client = ChildClient("kid-att-scan");
        var head = Encoding.UTF8.GetBytes("%PDF-1.7\n");
        var tail = new byte[] { 0x00, 0x01, 0x00, 0xFF, 0xFE, 0x00, 0x80 };
        var scan = new byte[head.Length + tail.Length];
        head.CopyTo(scan, 0);
        tail.CopyTo(scan, head.Length);

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("скан.pdf", scan)));

        s.Should().Be(HttpStatusCode.UnprocessableEntity);
        CodeOf(b).Should().Be("pdf_no_text");
        File.Exists(Abs(root, "Вложения/скан.pdf")).Should().BeFalse("оригинал, записанный этим же вызовом, снят");
        File.Exists(Abs(root, ChildPathPolicy.TextCopyOf("Вложения/скан.pdf"))).Should().BeFalse("копии несостоявшегося оригинала нет");
    }

    // ─── текст и PDF → текст ───

    [Fact]
    public async Task Upload_Txt_200_Text_ФайлКакЕсть()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-txt");
        var client = ChildClient("kid-att-txt");
        var bytes = Encoding.UTF8.GetBytes("задача: собрать пазл\nшаг 1 — найти углы");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("задача.txt", bytes)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("text");
        var path = b.GetProperty("path").GetString()!;
        path.Should().MatchRegex(SubjectPathRegex("задача.txt"));
        SameBytes(root, path, bytes);
    }

    // c2: PDF с текстовым слоем и случайным маркером → kind=text, path — оригинал в папке
    // предмета (решение 89), а текст — в копии «.ccs-text/{путь}.txt» (решение 90).
    // Инлайн сообщения (тот же AttachmentInliner) несёт маркер и НЕ несёт пометку бинарного
    // файла («открой инструментом чтения» — ребёнку Read закрыт).
    [Fact]
    public async Task Upload_PdfСТекстовымСлоем_200_ОтветПутьОригинала_КопияВCcsText_ИнлайнНесётМаркер()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-pdf");
        var client = ChildClient("kid-att-pdf");
        var marker = "МАРКЕР-" + Guid.NewGuid().ToString("N");
        var pdf = PdfWithText(marker);

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("урок.pdf", pdf)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("text");
        b.GetProperty("name").GetString().Should().Be("урок.pdf");
        var originalRel = b.GetProperty("path").GetString()!;
        originalRel.Should().MatchRegex(SubjectPathRegex("урок.pdf"));
        SameBytes(root, originalRel, pdf);

        // копия — форма решения 90: «.ccs-text/» с сохранённым расширением оригинала
        var copyRel = ChildPathPolicy.TextCopyOf(originalRel);
        copyRel.Should().Be(".ccs-text/Вложения/урок.pdf.txt");
        var copy = File.ReadAllText(Abs(root, copyRel));
        copy.Should().Contain(marker);

        var inlined = AttachmentInliner.BuildMessageText(root, "объясни задачу", [copyRel]);
        inlined.Should().Contain(marker);
        inlined.Should().NotContain("открой инструментом чтения");
    }

    // R-KM-B6e (major): текст под именем .png — тип по сигнатуре (текст), расширение
    // нормализовано в .txt: kind и расширение path не расходятся, и inliner несёт такой файл
    // текстом, а не картинкой (SplitImagePaths выбирает по расширению).
    [Fact]
    public async Task Upload_ТекстПодИменемPng_200_Text_РасширениеНормализованоВtxt()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-text-as-png");
        var client = ChildClient("kid-att-text-as-png");
        var bytes = Encoding.UTF8.GetBytes("домашнее задание: три урока");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("задание.png", bytes)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("text");
        var path = b.GetProperty("path").GetString()!;
        path.Should().MatchRegex(SubjectPathRegex("задание.txt"));
        SameBytes(root, path, bytes);
        AttachmentInliner.SplitImagePaths([path]).images.Should().BeEmpty("текст под .txt не считается картинкой");
    }

    // R-KM-B6e: PDF под именем .png — оригинал сохраняется как .pdf (конвертер выбирается по
    // расширению), наружу — тот же путь оригинала, kind=text; копия текста — по форме решения 90.
    [Fact]
    public async Task Upload_PdfПодИменемPng_200_ОригиналPdf_КопияВCcsText()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-pdf-as-png");
        var client = ChildClient("kid-att-pdf-as-png");
        var marker = "МАРКЕР-" + Guid.NewGuid().ToString("N");
        var pdf = PdfWithText(marker, body: "");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("урок.png", pdf)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("text");
        var originalRel = b.GetProperty("path").GetString()!;
        originalRel.Should().MatchRegex(SubjectPathRegex("урок.pdf"), "расширение приведено к типу");
        SameBytes(root, originalRel, pdf);
        File.Exists(Abs(root, ChildPathPolicy.TextCopyOf(originalRel))).Should().BeTrue("текстовая копия рядом, по форме решения 90");
    }

    // R-KM-B6e: PNG под именем .txt — тип по сигнатуре (картинка), расширение нормализовано
    // в .png: kind=image и расширение path не расходятся, inliner относит файл к картинкам.
    [Fact]
    public async Task Upload_PngПодИменемTxt_200_Image_РасширениеНормализованоВpng()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-png-as-txt");
        var client = ChildClient("kid-att-png-as-txt");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("схема.txt", Png)));

        s.Should().Be(HttpStatusCode.OK);
        b.GetProperty("kind").GetString().Should().Be("image");
        var path = b.GetProperty("path").GetString()!;
        path.Should().MatchRegex(SubjectPathRegex("схема.png"));
        SameBytes(root, path, Png);
        AttachmentInliner.SplitImagePaths([path]).images.Should().ContainSingle().Which.Should().Be(path);
    }

    // Решение 5 (граница в новом месте): текста больше бюджета инлайна (256 КБ) — копия
    // сохраняется целиком (писатель не режет), а в ход первые 256 КБ несёт инлайнер со своей
    // пометкой на месте обрыва. Раньше резала ручка — теперь это точка инлайнера.
    [Fact]
    public async Task Upload_PdfСТекстомБольше256КБ_КопияЦеликом_ВХодРежетИнлайнерСПометкой()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-trunc");
        var client = ChildClient("kid-att-trunc");
        var body = new string('а', 150_000); // «а» — 2 байта UTF-8: 300 КБ (> 256 КБ бюджета, < 300 КБ лимита)
        var marker = "МАРКЕР-" + Guid.NewGuid().ToString("N");
        var pdf = Encoding.UTF8.GetBytes($"%PDF-1.7\n{marker}\n{body}\n%%EOF\n");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("длинный.pdf", pdf)));

        s.Should().Be(HttpStatusCode.OK);
        var originalRel = b.GetProperty("path").GetString()!;
        var copyRel = ChildPathPolicy.TextCopyOf(originalRel);
        var copy = File.ReadAllText(Abs(root, copyRel));
        Encoding.UTF8.GetByteCount(copy).Should().BeGreaterThan(InlineBudget, "копия на диске — полный текст, не обрезанный");

        var inlined = AttachmentInliner.BuildMessageText(root, "объясни задачу", [copyRel]);
        inlined.Should().Contain(marker);
        inlined.Should().Contain("…(файл обрезан по размеру)", "в ход идёт обрезанный инлайнером текст с явной пометкой");
    }

    // Решение 89 + Б10e: путь в ответе — оригинал, его фронт и передаёт в attachedPaths;
    // фильтр хаба подменяет его копией (TextCopyIfExists) — по оставленному пути текст
    // входит в сообщение. Без подмены (мутация в ChildHubFilter) текст до модели не дошёл бы.
    [Fact]
    public async Task Upload_ОригиналПроходитФильтрХаба_ПодменёнКопией_ТекстДоМодели()
    {
        var (childId, chat, root) = await NewChildWithLesson("kid-att-pdf-hub");
        var client = ChildClient("kid-att-pdf-hub");
        var marker = "МАРКЕР-" + Guid.NewGuid().ToString("N");
        var pdf = PdfWithText(marker, body: "");

        var (s, b, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("урок.pdf", pdf)));
        s.Should().Be(HttpStatusCode.OK);
        var originalRel = b.GetProperty("path").GetString()!;

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chat, "объясни задачу", new List<string> { originalRel }, "bypassPermissions", true], Sessions, childId);
        args[2].Should().BeEquivalentTo(new List<string> { ChildPathPolicy.TextCopyOf(originalRel) }, o => o.WithStrictOrdering());

        var inlined = AttachmentInliner.BuildMessageText(root, "объясни задачу", (List<string>)args[2]!);
        inlined.Should().Contain(marker, "путь прошёл фильтр и подменён копией — текст вошёл в сообщение");
    }

    // ─── доступ к чату ───

    [Fact]
    public async Task Upload_ЧужойДетскийИлиНесуществующий_404_chat_not_found_ОдноТело()
    {
        var (_, chatB, rootB) = await NewChildWithLesson("kid-att-404-b");
        await NewChild("kid-att-404-a");
        var clientA = ChildClient("kid-att-404-a");

        var (s1, b1, raw1) = await ReadAsync(await clientA.PostAsync($"/api/child/chats/{chatB}/attachments", Multipart("схема.png", Png)));
        var (s2, b2, raw2) = await ReadAsync(await clientA.PostAsync($"/api/child/chats/{Guid.NewGuid():N}/attachments", Multipart("схема.png", Png)));

        s1.Should().Be(HttpStatusCode.NotFound);
        CodeOf(b1).Should().Be("chat_not_found");
        // существование чужого чата не раскрывается: тело неотличимо от несуществующего
        s2.Should().Be(s1);
        raw2.Should().Be(raw1);
        Directory.Exists(Path.Combine(rootB, "Вложения")).Should().BeFalse("чужой чат не получил файла");
    }

    // Старый урок без проекта (решение 51): с решения 89 его вложения принимает только рельса
    // — эта ручка отвечает ему тем же 404, одним телом с несуществующим (существование не
    // раскрываем). Создание — с снятым профилем (тот же приём, что LegacyLessonAsync в
    // ChildChatsControllerTests): детская проверка хода не должна мешать создать мерку).
    [Fact]
    public async Task Upload_СтарыйУрокБезПроекта_404_chat_not_found_ОдноТело()
    {
        var childId = await NewChild("kid-att-legacy");
        var user = _hosts.Services.GetRequiredService<UserStore>().GetById(childId)!;
        var profile = user.ChildMode!;
        user.ChildMode = null;
        string legacyId;
        try
        {
            legacyId = (await Sessions.CreateChatAsync(childId, ClaudeMode.Auto, name: "Старый урок",
                childSpace: ChildSpaces.Lessons, childSubject: ChildTestSubjects.Key)).Id;
        }
        finally { user.ChildMode = profile; }

        var client = ChildClient("kid-att-legacy");
        var (s1, b1, raw1) = await ReadAsync(await client.PostAsync($"/api/child/chats/{legacyId}/attachments", Multipart("схема.png", Png)));
        var (s2, _, raw2) = await ReadAsync(await client.PostAsync($"/api/child/chats/{Guid.NewGuid():N}/attachments", Multipart("схема.png", Png)));

        s1.Should().Be(HttpStatusCode.NotFound);
        CodeOf(b1).Should().Be("chat_not_found");
        s2.Should().Be(s1);
        raw2.Should().Be(raw1);
    }

    [Fact]
    public async Task Upload_Взрослый_403_not_child()
    {
        var adultChat = (await Sessions.CreateChatAsync(
            _hosts.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id,
            ClaudeMode.Auto, name: "взрослый")).Id;

        var (s, b, _) = await ReadAsync(await _admin.PostAsync($"/api/child/chats/{adultChat}/attachments", Multipart("схема.png", Png)));

        s.Should().Be(HttpStatusCode.Forbidden);
        CodeOf(b).Should().Be("not_child");
    }

    // ─── совпадение имён: правило писателя, как у `upload` рельсы ───

    // Файл на имени — перезапись (второй записан, одного файла в «Вложения/» ровно один);
    // каталог на имени — 409 «exists» (тот же код, что у записи взрослого). Своей блокировки
    // здесь нет: одновременные загрузки держит замок писателя (его тест — в Б11b).
    [Fact]
    public async Task Upload_ТоЖеИмя_Перезапись_КаталогНаИмени_409Exists()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-samename");
        var client = ChildClient("kid-att-samename");
        var first = Encoding.UTF8.GetBytes("первое содержимое");
        var second = Encoding.UTF8.GetBytes("второе содержимое");

        var (s1, b1, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("заметка.txt", first)));
        var (s2, b2, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("заметка.txt", second)));

        s1.Should().Be(HttpStatusCode.OK);
        s2.Should().Be(HttpStatusCode.OK);
        b2.GetProperty("path").GetString().Should().Be(b1.GetProperty("path").GetString(), "тот же путь: второй файл заменил первый");
        SameBytes(root, b2.GetProperty("path").GetString()!, second);
        Directory.GetFiles(Path.Combine(root, "Вложения")).Should().HaveCount(1, "второго файла с тем же именем нет");

        Directory.CreateDirectory(Path.Combine(root, "Вложения", "папка.txt"));
        var (s3, b3, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("папка.txt", first)));
        s3.Should().Be(HttpStatusCode.Conflict);
        CodeOf(b3).Should().Be("exists");
    }

    // Б11b2 (ревью ac7abe2d п.2): перезапись существующего PDF содержимым без текста не
    // стирает прежнее. Писатель держит новое содержимое стейнингом рядом и подменяет
    // оригинал только после успеха проверки; при отказе стейнинг снят, а прежние оригинал
    // и его копия — ровно как были. Мутация «писать сразу в оригинал» краснеет здесь:
    // второй заход затрёт первый файл, а при отказе он же и удалится.
    [Fact]
    public async Task ПерезаписьPdfФайломБезТекста_422_PdfNoText_ПрежниеОригиналИКопияНаМесте()
    {
        var (_, chat, root) = await NewChildWithLesson("kid-att-replace-scan");
        var client = ChildClient("kid-att-replace-scan");
        var marker = "МАРКЕР-" + Guid.NewGuid().ToString("N");
        var good = PdfWithText(marker);

        var (s1, b1, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("урок.pdf", good)));
        s1.Should().Be(HttpStatusCode.OK, "первый заход — обычный путь: оригинал и копия на диске");
        var originalRel = b1.GetProperty("path").GetString()!;
        var copyRel = ChildPathPolicy.TextCopyOf(originalRel);

        var head = Encoding.UTF8.GetBytes("%PDF-1.7\n");
        var tail = new byte[] { 0x00, 0x01, 0x00, 0xFF, 0xFE, 0x00, 0x80 };
        var scan = new byte[head.Length + tail.Length];
        head.CopyTo(scan, 0);
        tail.CopyTo(scan, head.Length);

        var (s2, b2, _) = await ReadAsync(await client.PostAsync($"/api/child/chats/{chat}/attachments", Multipart("урок.pdf", scan)));
        s2.Should().Be(HttpStatusCode.UnprocessableEntity);
        CodeOf(b2).Should().Be("pdf_no_text");

        // Конечное состояние полного цикла: прежние байты и их копия — нетронуты,
        // временного файла нет, второго файла в «Вложения/» нет.
        SameBytes(root, originalRel, good);
        File.ReadAllText(Abs(root, copyRel)).Should().Contain(marker);
        Directory.GetFiles(Path.Combine(root, "Вложения")).Should().HaveCount(1, "стейнинг снят отказом");
        Directory.GetFiles(root, "*.ccs-staging", SearchOption.AllDirectories).Should().BeEmpty("временный файл не остался");
    }

    // Отказ писателя вне его кодов (конфиг не читается → метр папки бросает): 503
    // policy_unavailable, исключения наружу нет, полуприложения в «Вложения/» нет —
    // метр проверяется до любой записи на диске. Хост подмены — фикстура класса
    // UnreadableConfigHosts (Гейт-4·Г: хост на класс, не на тест).
    [Fact]
    public async Task Upload_ПисательБросает_503_policy_unavailable_БезПолуприложения()
    {
        var host = _unreadableHosts;
        var childId = await NewChildOn(host, "kid-att-io");
        var chatId = await CreateLessonOn(host, host.CreateAuthenticatedClient("kid-att-io", ChildPassword));
        var root = host.Services.GetRequiredService<SessionManager>().GetChatRoot(chatId, childId)!.Replace('\\', '/');

        var (s, b, _) = await ReadAsync(await host.CreateAuthenticatedClient("kid-att-io", ChildPassword)
            .PostAsync($"/api/child/chats/{chatId}/attachments", Multipart("заметка.txt", Encoding.UTF8.GetBytes("проверка"))));

        s.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(b).Should().Be("policy_unavailable");
        Directory.Exists(Path.Combine(root, "Вложения")).Should().BeFalse("полуприложения не осталось");
    }

    // Писатель бросает исключение вызова (не свой код) — ветка внешнего catch контроллера,
    // которой нет в кодах ручки: 503 одним телом policy_unavailable, исключения наружу нет
    // (тело не несёт причины), полуприложения нет. Прежний файл на имени — ровно как был:
    // сбой — до любого эффекта. Мутация «снять catch в ChildAttachmentsController»
    // краснеет здесь: исключение стало бы 500 с трассировкой. Хост подмены — фикстура
    // класса ThrowingSaverHosts (Гейт-4·Г: хост на класс, не на тест).
    [Fact]
    public async Task Upload_ПисательБросаетИсключение_503_policy_unavailable_ПрежнийФайлТронутНеБыл()
    {
        var host = _throwingHosts;
        var childId = await NewChildOn(host, "kid-att-throw");
        var chatId = await CreateLessonOn(host, host.CreateAuthenticatedClient("kid-att-throw", ChildPassword));
        var root = host.Services.GetRequiredService<SessionManager>().GetChatRoot(chatId, childId)!.Replace('\\', '/');

        // Прежнее вложение — руками: состояние «на имени уже есть файл» до отказа.
        var first = Encoding.UTF8.GetBytes("прежнее вложение");
        Directory.CreateDirectory(Path.Combine(root, "Вложения"));
        File.WriteAllBytes(Path.Combine(root, "Вложения", "заметка.txt"), first);

        var (s, b, raw) = await ReadAsync(await host.CreateAuthenticatedClient("kid-att-throw", ChildPassword)
            .PostAsync($"/api/child/chats/{chatId}/attachments", Multipart("заметка.txt", Encoding.UTF8.GetBytes("проверка"))));

        s.Should().Be(HttpStatusCode.ServiceUnavailable);
        CodeOf(b).Should().Be("policy_unavailable");
        raw.Should().NotContain("писатель бросает", "исключение не выходит наружу");
        SameBytes(root, "Вложения/заметка.txt", first);
        Directory.GetFiles(Path.Combine(root, "Вложения")).Should().HaveCount(1, "полуприложения нет");
    }

    // Писатель, который бросает на самом SaveAsync: подмена регистрации DI (тот же приём,
    // что UnreadableConfig выше).
    private sealed class ThrowingSaver : ChildTextCopyService
    {
        public ThrowingSaver(MarkitdownService markitdown, IConfiguration config)
            : base(markitdown, config) { }
        public override Task<ChildSaveResult> SaveAsync(
            string subjectRoot, string rel, byte[] bytes, long maxBytes, bool requireText,
            CancellationToken ct = default)
            => throw new IOException("писатель бросает");
    }
}
