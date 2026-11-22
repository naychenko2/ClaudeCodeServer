using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Hubs;

/// <summary>
/// Гейт И1 на хабах (KM-B2, Б11c): ребёнку — SendMessage, ответ на карточку вопроса (AnswerQuestion)
/// и подписки своего чата; terminal и devices закрыты на подключении; mode/auto из SendMessage
/// ребёнка выбрасываются. attachedPaths — пути папки предмета этого чата (И8, ADR-041 решение 57):
/// их судит ChildPathPolicy, Deny/Hidden отбрасываются без отказа вызова; офис и PDF подменяются
/// своей текстовой копией «.ccs-text/», если она есть.
/// </summary>
[Trait("Category", "Host")]
public class ChildHubFilterTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
    }

    private void SetChild(bool valid = true)
    {
        factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = valid ? "tutor" : "",
            ProviderKey = "key",
            Model = "sonnet",
        };
    }

    private HubConnection Build(string hub, string username, string password)
    {
        var token = factory.GetToken(username, password);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, hub), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private void NoChild() => factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode = null;

    // Готовит ребёнка (SetChild) и подключает его к session-хабу
    private async Task<HubConnection> ConnectChildSessionHubAsync()
    {
        SetChild();
        var c = Build("hubs/session", TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await c.StartAsync();
        return c;
    }

    // Проверка перед детским путём: пользователь действительно ребёнок, иначе тест молча проверил бы взрослый
    private void ShouldBeChild()
    {
        var child = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.ChildMode;
        child.Should().NotBeNull("пользователь должен быть ребёнком до проверки детского пути");
        child!.Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("SetMode", "no-such", "bypassPermissions")]
    [InlineData("CompactSession", "no-such", null)]
    [InlineData("JoinProject", "no-such", null)]
    public async Task Child_DeniedMethods_ThrowChildMode(string method, string arg1, string? arg2)
    {
        NoChild();
        var conn = await ConnectChildSessionHubAsync();
        ShouldBeChild();
        var act = () => arg2 is null ? conn.InvokeAsync(method, arg1) : conn.InvokeAsync(method, arg1, arg2);
        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("child_mode");
    }

    private SessionManager Sessions => factory.Services.GetRequiredService<SessionManager>();

    private string ChildId => factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private string AdminId => factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<string> ChatAsync(string ownerId, string? childSpace)
    {
        // Создание чата при включённом детском режиме запрещено — гасим профиль на время создания
        // и возвращаем в finally: после хелпера ребёнок остаётся детским
        var user = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        var previous = user.ChildMode;
        user.ChildMode = null;
        try
        {
            var chat = await Sessions.CreateChatAsync(ownerId, ClaudeMode.Auto, name: "чат");
            chat.ChildSpace = childSpace;
            return chat.Id;
        }
        finally
        {
            user.ChildMode = previous;
        }
    }

    // Файл в папке этого чата — тот же корень, который судит фильтр (GetChatRoot). Путь относительный,
    // как его присылает рельса «Файлы».
    private string WriteInChat(string chatId, string rel, string content = "x")
    {
        var full = SafePath.Join(Sessions.GetChatRoot(chatId, ChildId)!, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return rel;
    }

    [Fact]
    public async Task Child_OwnChildChat_PassesFilter()
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var conn = await ConnectChildSessionHubAsync();
        ShouldBeChild();
        // Фильтр пропустил: JoinSession своего детского чата отрабатывает штатно
        await conn.InvokeAsync("JoinSession", chatId);

        // SendMessage дошёл до хаба: отказ может прийти от самого хаба, но не child_mode
        var send = () => conn.InvokeAsync<string>("SendMessage", chatId, "привет", (List<string>?)null, "bypassPermissions", true);
        try { await send(); }
        catch (HubException e) { e.Message.Should().NotContain("child_mode"); }
    }

    [Theory]
    [InlineData("JoinSession")]
    [InlineData("SendMessage")]
    [InlineData("AnswerQuestion")]
    public async Task Child_ForeignOrNonChildOrMissingChat_ThrowsChildMode(string method)
    {
        NoChild();
        var foreignWithSpace = await ChatAsync(AdminId, ChildSpaces.Lessons);
        var ownWithoutSpace = await ChatAsync(ChildId, null);
        var conn = await ConnectChildSessionHubAsync();
        ShouldBeChild();

        foreach (var id in new[] { foreignWithSpace, ownWithoutSpace, "no-such" })
        {
            var act = () => method switch
            {
                "JoinSession" => conn.InvokeAsync(method, id),
                "AnswerQuestion" => conn.InvokeAsync(method, id, "toolu_1", "{\"answers\":{}}"),
                _ => conn.InvokeAsync<string>(method, id, "привет", (List<string>?)null, null, false),
            };
            (await act.Should().ThrowAsync<HubException>(id)).Which.Message.Should().Contain("child_mode");
        }
    }

    [Theory]
    [InlineData("hubs/terminal")]
    [InlineData("hubs/devices")]
    public async Task Child_CannotConnect_ToTerminalOrDevicesHub(string hub)
    {
        SetChild();
        ShouldBeChild();
        var c = Build(hub, TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var act = () => c.StartAsync();
        await act.Should().ThrowAsync<Exception>();
    }

    // Г14: DenyAll-ребёнок доходит до подключения к session-хабу — HTTP-гейт отвечает только за
    // allow-list, а /hubs/session помечен [ChildAllowed]. Сам метод-вызов остаётся закрытым:
    // фильтр хаба отказывает его по DenyAll (ChildHubFilter.cs:39). Подключиться может — сделать
    // в хабе ничего нельзя. (Раньше коннект глушил blanket-отказ HTTP-гейта, не сам фильтр.)
    [Fact]
    public async Task DenyAllChild_CanConnect_ButEveryMethodIsChildMode()
    {
        SetChild(valid: false);
        ShouldBeChild();
        var c = Build("hubs/session", TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await c.StartAsync(); // дошёл до подключения
        var act = () => c.InvokeAsync("JoinSession", "no-such");
        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("child_mode");
    }

    [Fact]
    public async Task Adult_CanUseDeniedMethods()
    {
        NoChild();
        var c = Build("hubs/session", TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        await c.StartAsync();
        var act = () => c.InvokeAsync("SetMode", "no-such", "plan");
        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().NotContain("child_mode");
    }

    // ─── attachedPaths: путь проекта этого чата (И8, решение 57) ───

    // К1 (Б11c): из трёх присланных путей до хода доходит ровно свой. Чужой «../» — вне папки
    // предмета, «Для репетитора/» — невидима ребёнку (Hidden). Вызов при этом не падает.
    [Fact]
    public async Task SendMessageArgs_AttachedPaths_ДоХодаДоходитРовноСвойПуть_ModeAndAutoDropped()
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var own = WriteInChat(chatId, "конспект.txt");

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string> { own, "../чужой/x.txt", "Для репетитора/key.pdf" }, "bypassPermissions", true],
            Sessions, ChildId);

        args[2].Should().BeEquivalentTo(new List<string> { own }, "свой путь — один, чужой и невидимый отброшены");
        args[3].Should().BeNull();
        args[4].Should().Be(false);
    }

    // Всё, что вне папки или скрыто: абсолютный путь, «..» наружу, настройки CLI, любой «.»-сегмент
    // и своя папка загрузки вложений ребёнка (.cc-attachments) — решение 57 закрыл её для
    // attachedPaths: вложения в урок — путём проекта. Пустое — не путь. Всё отвергнуто → null.
    [Fact]
    public async Task SendMessageArgs_AttachedPaths_ВнеПапкиИСкрытые_Отброшены()
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var uploaded = WriteInChat(chatId, ".cc-attachments/" + Guid.NewGuid().ToString("N") + "/a.txt");
        var foreignPrivate = ".cc-attachments/private/" + AdminId + "/b.txt";

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string>
            {
                "/etc/passwd", "Материалы/../../чужой/x", ".claude/settings.json", "CLAUDE.md",
                "Заметки/CLAUDE.md", foreignPrivate, uploaded, "", ".cc-attachments"
            }, null, false],
            Sessions, ChildId);

        args[2].Should().BeNull("ни один из этих путей не читается ребёнку в его папке предмета");
    }

    // «Материалы/» — на чтение проходит (решение 65); абсолютный путь каталога данных
    // (data/child-subjects — бывшего магазина материалов, стор удалён Х-W4-28) — вне папки этого
    // чата: та же граница, что у решения разрешения хода (I6 №4). Граница держится и для
    // оставшегося на диске каталога: она — про путь, а не про то, что его кто-то пишет.
    // Относительный «data/…» судится как путь под корнем чата: фильтр проверяет границу, а не то,
    // что файла нет — отсутствующий путь доезжает до AttachmentInliner, который его и пропустит.
    [Fact]
    public async Task SendMessageArgs_МатериалыНаЧтение_Проходят_АбсолютныйПутьКаталогаДанных_Нет()
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var material = WriteInChat(chatId, "Материалы/урок.txt");
        // Путь собран по резолверу хоста (TestWebApplicationFactory.cs:76: DataPath = TempDir/
        // projects.json): каталог данных — TempDir, его подкаталог — тот же literal, что был
        // константой DirName удалённого стора.
        var store = Path.Combine(factory.TempDir, "child-subjects", ChildId, "math", "text", "m_000000000001.txt");
        var traversal = Path.GetRelativePath(Sessions.GetChatRoot(chatId, ChildId)!, store).Replace('\\', '/');

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string> { material, store, traversal }, null, false],
            Sessions, ChildId);

        args[2].Should().BeEquivalentTo(new List<string> { material },
            "ни абсолютный путь магазина, ни обходной «../» к нему не проходят");
    }

    // Офис и PDF в урок встраиваются текстом: путь подменяется своей копией
    // «.ccs-text/…<расширение оригинала>.txt» (форма — ChildPathPolicy.TextCopyOf, решение 90,
    // та же, что подсказка модели в TextCopyPath). Копии нет — остаётся исходный путь:
    // AttachmentInliner отдаст о нём пометку, отказа нет. Сама копия проходит как есть.
    [Fact]
    public async Task SendMessageArgs_ОфисИPDF_ПодменяютсяСвоейТекстовойКопией()
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var pdf = WriteInChat(chatId, "Материалы/урок.pdf");
        var copy = WriteInChat(chatId, ".ccs-text/Материалы/урок.pdf.txt", "текст урока");
        var docxNoCopy = WriteInChat(chatId, "Заметки/план.docx");

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string> { pdf, docxNoCopy, copy }, null, false],
            Sessions, ChildId);

        args[2].Should().BeEquivalentTo(
            new List<string> { ".ccs-text/Материалы/урок.pdf.txt", docxNoCopy, copy },
            "PDF подменён копией; без копии — исходный путь; копия проходит как есть");
    }

    // Офис/PDF — расширения документов (состав загрузки решения 45); текст остаётся текстом,
    // картинка — картинкой (её несёт image-блок, не подмена). Копия сохраняет расширение
    // оригинала (решение 90): «урок.pdf» → «.ccs-text/урок.pdf.txt», регистр не меняется.
    [Theory]
    [InlineData("урок.pdf", ".ccs-text/урок.pdf.txt")]
    [InlineData("урок.DOCX", ".ccs-text/урок.DOCX.txt")]
    [InlineData("таблица.Xlsx", ".ccs-text/таблица.Xlsx.txt")]
    [InlineData("презентация.pptx", ".ccs-text/презентация.pptx.txt")]
    [InlineData("заметка.md", "заметка.md")]
    [InlineData("картинка.png", "картинка.png")]
    public async Task SendMessageArgs_ТолькоДокументыПодменяютсяКопией(string name, string expected)
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var rel = WriteInChat(chatId, name);
        WriteInChat(chatId, ChildPathPolicy.TextCopyOf(name));

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string> { rel }, null, false], Sessions, ChildId);

        args[2].Should().BeEquivalentTo(new List<string> { expected });
    }

    // Два оригинала, отличающиеся только расширением, имеют РАЗНЫЕ копии (решение 90):
    // прежний Path.ChangeExtension свёл бы «лист.pdf» и «лист.docx» в одну «лист.txt».
    [Fact]
    public async Task SendMessageArgs_ДвеКопииНеСклеиваются()
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var pdf = WriteInChat(chatId, "лист.pdf");
        var docx = WriteInChat(chatId, "лист.docx");
        var copyPdf = WriteInChat(chatId, ".ccs-text/лист.pdf.txt", "текст pdf");
        var copyDocx = WriteInChat(chatId, ".ccs-text/лист.docx.txt", "текст docx");

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string> { pdf, docx }, null, false], Sessions, ChildId);

        args[2].Should().BeEquivalentTo(new List<string> { copyPdf, copyDocx },
            "у каждого оригинала своя копия: расширение сохранено, пути не сливаются в один");
    }

    // Копия судится по своему оригиналу (решение 91): явный путь копии ключа репетитора
    // отбрасывается — original «Для репетитора/…» ребёнку Hidden; копия материалов остаётся.
    [Fact]
    public async Task SendMessageArgs_ЯвныйПутьКопииКлючаРепетитора_Отбрасывается()
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var copyTutor = WriteInChat(chatId, ".ccs-text/Для репетитора/k.pdf.txt", "текст ключа");
        var copyMaterials = WriteInChat(chatId, ".ccs-text/Материалы/a.pdf.txt", "текст материала");

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string> { copyTutor, copyMaterials }, null, false], Sessions, ChildId);

        args[2].Should().BeEquivalentTo(new List<string> { copyMaterials },
            "копия ключа не видна ребёнку ни в каком виде — ни оригиналом, ни явным путём копии");
    }

    // Кривая копия без оригинала: путь под «.ccs-text/», которым никто не создавал копию.
    // «x.txt» — OriginalOf находит форму «x», но файла-оригинала нет; «x.pdf» — не форма
    // копии (нет суффикса «.txt», OriginalOf null) и судится политикой по остатку после
    // «.ccs-text/». В обоих случаях TextCopyIfExists отдаёт null: второй копии не ищем,
    // путь проходит как есть — отсутствия файла не знает AttachmentInliner, отказа нет.
    [Theory]
    [InlineData(".ccs-text/x.txt")]
    [InlineData(".ccs-text/x.pdf")]
    public async Task SendMessageArgs_КриваяКопияБезОригинала_ПроходитКакЕсть(string malformed)
    {
        NoChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var rel = WriteInChat(chatId, malformed);

        var args = ChildHubFilter.SanitizeSendMessageArgs(
            [chatId, "t", new List<string> { rel }, null, false], Sessions, ChildId);

        args[2].Should().BeEquivalentTo(new List<string> { rel },
            "кривая копия — не подменяемая копия: второй копии не ищем, путь остаётся как есть");
    }

    // Чат, который ребёнку не принадлежит (чужой) или которого нет: корня нет — путей нет (fail-closed,
    // как в IsOwnChildChat). Здесь — только это; «не фильтровать вовсе» мутации красит
    // SendMessageArgs_AttachedPaths_ДоХодаДоходитРовноСвойПуть.
    [Fact]
    public async Task SendMessageArgs_ЧужойИлиНесуществующийЧат_СтановитсяNull()
    {
        NoChild();
        var foreign = await ChatAsync(AdminId, ChildSpaces.Lessons);
        ChildHubFilter.SanitizeSendMessageArgs([foreign, "t", new List<string> { "c.txt" }, null, false], Sessions, ChildId)[2]
            .Should().BeNull("папка чужого чата — не папка предмета этого ребёнка");
        ChildHubFilter.SanitizeSendMessageArgs(["no-such", "t", new List<string> { "c.txt" }, null, false], Sessions, ChildId)[2]
            .Should().BeNull();
    }

    // ─── AnswerQuestion: только свой чат (Б19) ───

    // Свой детский чат: фильтр пускает ответ, живого хода нет — хаб игнорирует (карточки нет).
    // Устаревший и повторный ответ на один toolUseId — тот же игнор без исключения: принят был бы
    // первый (ClaudeSession.AnswerQuestion снимает toolUseId при первом — ClaudeSession.cs:3081).
    [Fact]
    public async Task AnswerQuestion_СвойЧат_Проходит_УстаревшийИВторойОтвет_БезИсключения()
    {
        NoChild();
        var own = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var conn = await ConnectChildSessionHubAsync();
        ShouldBeChild();

        await conn.InvokeAsync("AnswerQuestion", own, "toolu_1", "{\"answers\":{\"а\":\"1\"}}");
        await conn.InvokeAsync("AnswerQuestion", own, "toolu_1", "{\"answers\":{\"а\":\"2\"}}");
        // Ход уже завершён (его никогда не было) — ответ после конца хода тоже игнор:
        await conn.InvokeAsync("AnswerQuestion", own, "toolu_2", "{\"answers\":{\"а\":\"3\"}}");
    }

    [Fact]
    public async Task Interrupt_OwnChildChat_PassesFilter_ForeignIsChildMode()
    {
        NoChild();
        var own = await ChatAsync(ChildId, ChildSpaces.Lessons);
        var foreign = await ChatAsync(AdminId, ChildSpaces.Lessons);
        var conn = await ConnectChildSessionHubAsync();
        ShouldBeChild();

        try { await conn.InvokeAsync("Interrupt", own); }
        catch (HubException e) { e.Message.Should().NotContain("child_mode"); }

        var act = () => conn.InvokeAsync("Interrupt", foreign);
        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("child_mode");
    }

    // ─── Путь через сам фильтр (без транспорта) ───

    private sealed class FakeCaller(System.Security.Claims.ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "c";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features =>
            new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class ChildPolicy : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => new(ownerId, new ChildProfile { Enabled = true }, DenyAll: false);
    }

    private HubInvocationContext Through(string chatId, string method, object?[] args)
    {
        var services = new ServiceCollection().AddSingleton<IChildModePolicy, ChildPolicy>()
            .AddSingleton(Sessions).BuildServiceProvider();
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, ChildId)], "test"));
        var hub = (SessionHub)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SessionHub));
        return new HubInvocationContext(new FakeCaller(user), services, hub, typeof(SessionHub).GetMethod(method)!, args);
    }

    [Fact]
    public async Task SendMessage_ThroughFilter_NextGetsSanitizedArgs()
    {
        SetChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        // ChatAsync гасит профиль только на время создания и возвращает его: ребёнок остаётся детским
        ShouldBeChild();
        var ctx = Through(chatId, nameof(SessionHub.SendMessage),
            new object?[] { chatId, "t", new List<string> { "/etc/passwd" }, "bypassPermissions", true });

        IReadOnlyList<object?>? seen = null;
        await new ChildHubFilter().InvokeMethodAsync(ctx, c => { seen = c.HubMethodArguments; return ValueTask.FromResult<object?>(null); });

        seen.Should().NotBeNull();
        seen!.Should().Equal(chatId, "t", null, null, false); // путь вне папки предмета — выброшен
    }

    // Фильтр не трогает аргументы ответа и не гасит второй вызов: очередь «принят первый» — за
    // сессией (ClaudeSession/TurnProcessHost), не за хабом. Здесь — что оба долетают как присланы.
    [Fact]
    public async Task AnswerQuestion_ThroughFilter_NextGetsArgsUnchanged_BothTimes()
    {
        SetChild();
        var chatId = await ChatAsync(ChildId, ChildSpaces.Lessons);
        ShouldBeChild();
        var filter = new ChildHubFilter();

        for (var i = 0; i < 2; i++)
        {
            IReadOnlyList<object?>? seen = null;
            var ctx = Through(chatId, nameof(SessionHub.AnswerQuestion),
                new object?[] { chatId, "toolu_1", "{\"answers\":{}}" });
            await filter.InvokeMethodAsync(ctx, c => { seen = c.HubMethodArguments; return ValueTask.FromResult<object?>(null); });
            // вызов i: фильтр обязан пропустить ответ своего чата, аргументы — как присланы
            seen.Should().NotBeNull();
            seen!.Should().Equal(chatId, "toolu_1", "{\"answers\":{}}");
        }
    }
}
