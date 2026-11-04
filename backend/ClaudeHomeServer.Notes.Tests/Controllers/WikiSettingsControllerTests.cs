using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaudeHomeServer.Tests.Controllers;

// NA-2 (Ф2): блок «Ночной разбор» в ручке GET/PUT /api/wiki/settings. Контроллер — напрямую
// (образец WikiClassHealthTests): тестируется контракт действия, не транспорт. Оценка здоровья
// настоящая (на пустом дереве она пуста и ничего не пишет), флаг — подменный.
//
// Контракт: PUT принимает nightly частично (мердж с состоянием на момент записи); кривой
// merged-блок — 400 wiki_nightly, файл не тронут; пустой блок — «менять нечего»; GET отдаёт
// nightly с подставленными умолчаниями; выключенный флаг llm-wiki — 409 без записи файла;
// отказ записи — 500 с кодом, без temp-файла.
//
// N7c: GET несёт lastNight — итог последней ночи из keeper/nightly-last.json (пишет его
// ночная служба; здесь файл подменяется, прогон службы — в WikiNightlyNotifyTests). Нет
// файла или он нечитаем — null без 500. PUT итога не несёт (стор фронта хранит его от GET).
//
// LI-2: PUT, включивший ночной разбор, заводит вику (pages/_schema.md): человек поставил
// тумблер, а ночная служба вики не заводит. PUT класса или автоприменения вики не заводит;
// svc (403) и выключенный флаг (409) до заведения не доходят; GET остаётся читающим.
public class WikiSettingsControllerTests : IDisposable
{
    private const string User = "u1";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FlagGate _flags = new();
    private readonly Notifier _notifier = new();
    private readonly WikiClassHealth _health;
    // LI-2: контроллер держит Хранителя — с включённой ночью он заводит вику. Хранитель здесь
    // настоящий (тот же, что в бою): заведение — предмет этих тестов, а не заглушка.
    private readonly WikiKeeper _keeper;

    public WikiSettingsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_settings_controller_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _health = new(_config, _flags, _notifier);
        var users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(_config, users, new AppSettingsService(_config));
        _keeper = new WikiKeeper(new NotesService(projects, _config, NullLogger<NotesService>.Instance), _config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── GET: ночный блок целый всегда ──────────────────────────────────────────

    [Fact]
    public async Task GETОтдаётНочнойБлокСУмолчаниямиКогдаФайлаНет()
    {
        var res = await Controller().Get();

        var dto = (WikiSettingsDto)((OkObjectResult)res.Result!).Value!;
        dto.Nightly.Enabled.Should().BeFalse();
        dto.Nightly.WindowStartHour.Should().Be(3);
        dto.Nightly.WindowEndHour.Should().Be(6);
        dto.Nightly.MaxPackagesPerNight.Should().Be(20);
        dto.Nightly.MaxPackagesPerSection.Should().Be(6);
        dto.Nightly.MaxChatsPerSection.Should().Be(10);
        dto.Nightly.InputTokenBudget.Should().Be(700_000);
    }

    [Fact]
    public async Task НевалидныйФайл_GETОтдаётУмолчанияНочиБезСигналаНаружу()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllText(SettingsPath(), "{ это не json", new UTF8Encoding(false));

        var res = await Controller().Get();

        var dto = (WikiSettingsDto)((OkObjectResult)res.Result!).Value!;
        dto.Nightly.Enabled.Should().BeFalse();
        dto.Nightly.WindowStartHour.Should().Be(3);
        dto.Nightly.InputTokenBudget.Should().Be(700_000);
    }

    // ─── PUT: частичный блок мержется с состоянием (здесь — с умолчаниями) ──────

    [Fact]
    public async Task PUTЧастичногоНочногоБлокаМержитсяСУмолчаниямиИЗаписывается()
    {
        var put = await Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true, WindowStartHour = 21 } });

        var dto = (WikiSettingsDto)((OkObjectResult)put.Result!).Value!;
        dto.Nightly.Enabled.Should().BeTrue();
        dto.Nightly.WindowStartHour.Should().Be(21);
        dto.Nightly.WindowEndHour.Should().Be(6); // не названных полей — не менять: остались умолчания
        dto.Nightly.MaxPackagesPerNight.Should().Be(20);
        dto.Nightly.InputTokenBudget.Should().Be(700_000);

        var (settings, invalid) = WikiSettingsStore.Read(Root());
        invalid.Should().BeFalse();
        settings.Nightly!.Enabled.Should().BeTrue();
        settings.Nightly!.WindowStartHour.Should().Be(21);
        settings.Nightly!.InputTokenBudget.Should().Be(700_000);
    }

    // ─── c3: гонка PUT ночи и выключения классов — оба изменения в файле ────────

    [Fact]
    public async Task ГонкаPUTНочиИВыключенияКлассовСохраняетВсеИзменения()
    {
        // Старт по общему gate: без per-owner замка стора read-modify-write переплетается и
        // последние записи затирают чужие. Мутация «Mutate без семафора» — этот тест краснеет.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new[]
        {
            Task.Run(() => { gate.Task.GetAwaiter().GetResult(); return Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } }); }),
            Task.Run(() => { gate.Task.GetAwaiter().GetResult(); return Controller().Put(new WikiSettingsRequest { Class = "session×append", Enable = false }); }),
            Task.Run(() => { gate.Task.GetAwaiter().GetResult(); return Controller().Put(new WikiSettingsRequest { Class = "note×append", Enable = false }); }),
            Task.Run(() => { gate.Task.GetAwaiter().GetResult(); return Controller().Put(new WikiSettingsRequest { Class = "file×append", Enable = false }); }),
        };
        gate.SetResult();
        await Task.WhenAll(tasks);

        var (settings, invalid) = WikiSettingsStore.Read(Root());
        invalid.Should().BeFalse();
        settings.NightlyOrDefaults().Enabled.Should().BeTrue("ночное изменение не затёрто классовыми");
        settings.IsClassDisabled("session×append").Should().BeTrue();
        settings.IsClassDisabled("note×append").Should().BeTrue();
        settings.IsClassDisabled("file×append").Should().BeTrue();
    }

    // ─── c4: выключенный флаг — 409 до любой записи ────────────────────────────

    [Fact]
    public async Task PUTПриВыключенномФлагеLlmWikiЭто409_НиНастроекНиВики()
    {
        _flags.Enabled = false;

        var put = await Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        AssertCode(put.Result!, 409, "wiki_llm_wiki_off");
        File.Exists(SettingsPath()).Should().BeFalse("отказ до любой записи: ни байта на диске");
        // Мутация (LI-2, критерий c2): перенести заведение до проверки флага — pages/_schema.md
        // появился бы при выключенной функции: тест красный.
        WikiPaths.IsInitialized(_dir, User).Should().BeFalse("выключенная функция не заводит вику");
    }

    // ─── валидация merged-блока и «менять нечего» ───────────────────────────────

    [Fact]
    public async Task PUTНочногоБлокаСКривымMergedРезультатомЭто400_ФайлНеТронут()
    {
        var ok = await Controller().Put(new WikiSettingsRequest { Nightly = new() { WindowStartHour = 3, WindowEndHour = 6 } });
        ((OkObjectResult)ok.Result!).StatusCode.Should().Be(200);

        // Начало уже в файле (3); request ставит конец 3 — кривым становится merged-целое,
        // хотя само по себе поле корректно.
        var bad = await Controller().Put(new WikiSettingsRequest { Nightly = new() { WindowEndHour = 3 } });
        AssertCode(bad.Result!, 400, "wiki_nightly");

        var bad2 = await Controller().Put(new WikiSettingsRequest { Nightly = new() { WindowStartHour = 24 } });
        AssertCode(bad2.Result!, 400, "wiki_nightly");

        var (settings, _) = WikiSettingsStore.Read(Root());
        settings.Nightly!.WindowStartHour.Should().Be(3);
        settings.Nightly!.WindowEndHour.Should().Be(6); // отказанные PUT не тронули файл
    }

    [Fact]
    public async Task PUTПустогоНочногоБлокаЭтоНечегоМенять_ФайлНеСоздан()
    {
        var put = await Controller().Put(new WikiSettingsRequest { Nightly = new() });

        AssertCode(put.Result!, 400, "wiki_settings_nothing_to_change");
        File.Exists(SettingsPath()).Should().BeFalse();
    }

    // ─── отказ записи: код в ответе, temp-файл убран ────────────────────────────

    [Fact]
    public async Task ОтказЗаписиФайлаЭтоКодВОтвете_БезTempФайловНаДиске()
    {
        // settings.json — каталог: атомарный перенос (temp→Move) не может его заменить,
        // запись отказана. Так же ведёт себя диск, отказавший запись.
        Directory.CreateDirectory(SettingsPath());

        var put = await Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        AssertCode(put.Result!, 500, "wiki_settings_write_failed");
        Directory.GetFiles(Path.Combine(Root(), "keeper")).Should()
            .NotContain(f => f.EndsWith(".tmp"), "WriteAtomic убирает свой temp перед тем, как бросать дальше");
        // Заведение — после успешной записи настроек: отказанная запись вики не заводит.
        WikiPaths.IsInitialized(_dir, User).Should().BeFalse();
    }

    // ─── N7c: lastNight в GET ────────────────────────────────────────────────────

    private const string LastNightJson =
        """{"date":"2026-11-02","selected":4,"analyzed":3,"applied":3,"toInbox":1,"estTokens":1200,"outcome":"completed"}""";

    [Fact]
    public async Task GETПослеПроходаНесётИтогНочи()
    {
        WriteLastNight(LastNightJson);

        var res = await Controller().Get();

        var dto = (WikiSettingsDto)((OkObjectResult)res.Result!).Value!;
        dto.LastNight.Should().NotBeNull();
        dto.LastNight!.Date.Should().Be("2026-11-02");
        dto.LastNight!.Selected.Should().Be(4);
        dto.LastNight!.Analyzed.Should().Be(3);
        dto.LastNight!.Applied.Should().Be(3);
        dto.LastNight!.ToInbox.Should().Be(1);
        dto.LastNight!.EstTokens.Should().Be(1200);
        dto.LastNight!.Outcome.Should().Be("completed");
    }

    [Fact]
    public async Task GETБезФайлаИтогаОтдаётNull_Без500()
    {
        var res = await Controller().Get();

        var dto = (WikiSettingsDto)((OkObjectResult)res.Result!).Value!;
        dto.LastNight.Should().BeNull("нет файла — «итога не было»");
    }

    [Fact]
    public async Task GETБитогоФайлаИтогаОтдаётNull_Без500()
    {
        // Нечитаемый итог — то же «итога не было» для читающего и тот же reading, что у службы:
        // 500 из-за него быть не может, настройки при этом отвечаются как обычно.
        WriteLastNight("{ это не json");

        var res = await Controller().Get();

        var dto = (WikiSettingsDto)((OkObjectResult)res.Result!).Value!;
        dto.LastNight.Should().BeNull();
        dto.Nightly.Enabled.Should().BeFalse();
        dto.Nightly.InputTokenBudget.Should().Be(700_000);
    }

    [Fact]
    public async Task PUTИтогаНочиНеНесёт_ДажеКогдаФайлЕсть()
    {
        // PUT меняет настройки, а не читает прошлые проходы: стор фронта хранит lastNight
        // от последнего GET (wiki.ts:983-985).
        WriteLastNight(LastNightJson);

        var put = await Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        var dto = (WikiSettingsDto)((OkObjectResult)put.Result!).Value!;
        dto.LastNight.Should().BeNull();
    }

    // ─── B2b: сервисный токен (typ=svc) — PUT 403, GET остаётся 200 ────────────

    // Мутация (критерий c2): убрать проверку IsServiceToken в Put — этот тест падает
    // (PUT дошёл бы до записи и вернул 200 с файлом).
    [Fact]
    public async Task PUT_СервисныйТокен_403_НиНастроекНиВики()
    {
        var put = await Controller(svc: true).Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        ((IStatusCodeActionResult)put.Result!).StatusCode.Should().Be(403);
        File.Exists(SettingsPath()).Should().BeFalse("отказ до любой записи: ни байта на диске");
        // LI-2: svc не доходит до заведения — агент не заводит вику (инвариант ADR-035).
        WikiPaths.IsInitialized(_dir, User).Should().BeFalse();
    }

    // Отказ по типу токена не зависит от состояния: он раньше флага и раньше валидации
    // тела — при выключенной функции и при пустом теле ответ тот же 403.
    [Fact]
    public async Task PUT_СервисныйТокенПриВыключенномФлаге_403ПоТокену_АНе409()
    {
        _flags.Enabled = false;

        var put = await Controller(svc: true).Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        ((IStatusCodeActionResult)put.Result!).StatusCode.Should().Be(403,
            "отказ по типу токена раньше состояния: по ответу нельзя узнать, включена ли функция");
        File.Exists(SettingsPath()).Should().BeFalse();
        WikiPaths.IsInitialized(_dir, User).Should().BeFalse("ни типа токена, ни флага — до заведения не дошло");
    }

    // ─── LI-2: включённая ночь заводит вику, класс и автоприменение — нет ──────

    [Fact]
    public async Task PUTНочногоТумблераЗаводитВики_БезКнопкиЗавести()
    {
        // Мутация (критерий c2): убрать EnsureInitializedAsync в Put — этот тест красный
        // (pages/_schema.md не появляется), а ночная служба дальше молчит без владельца.
        var put = await Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        ((OkObjectResult)put.Result!).StatusCode.Should().Be(200);
        var schema = Path.Combine(WikiPaths.Pages(_dir, User), WikiPaths.SchemaFileName);
        File.Exists(schema).Should().BeTrue("человек включил ночной разбор — вики нужна ей сразу");
        File.ReadAllText(schema).Should().StartWith("# Схема вики", "шаблон пишет только Хранитель");
        // Разделы при этом не заведены: их заводит первая применённая операция (решение 1).
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты")).Should().BeFalse();
    }

    [Fact]
    public async Task PUTКлассаИАвтопримененияВикиНеЗаводит()
    {
        // Ни одно из этих действий не включает ночь: в итоговых настройках она остаётся
        // выключенной — вики не появляется. Мутация «заводить при любом PUT» — красный.
        ((OkObjectResult)(await Controller().Put(new WikiSettingsRequest { AutoApplyEnabled = true })).Result!)
            .StatusCode.Should().Be(200);
        ((OkObjectResult)(await Controller().Put(new WikiSettingsRequest { Class = "session×append", Enable = false })).Result!)
            .StatusCode.Should().Be(200);

        WikiPaths.IsInitialized(_dir, User).Should().BeFalse();
        Directory.Exists(WikiPaths.Pages(_dir, User)).Should().BeFalse("ни байта в pages/");
    }

    [Fact]
    public async Task GETНастроекВикиНеЗаводит_ЧтениеНеИмеетПобочныхЭффектов()
    {
        var res = await Controller().Get();

        ((OkObjectResult)res.Result!).StatusCode.Should().Be(200);
        // Мутация «заводить и на GET»: этот тест красный. Пустая вики показывается пустым
        // состоянием, заведение — на первом действии.
        WikiPaths.IsInitialized(_dir, User).Should().BeFalse();
        Directory.Exists(WikiPaths.Pages(_dir, User)).Should().BeFalse();
    }

    // Чтение под svc остаётся 200: отказ — только мутирующих ручек (контракт B2b).
    [Fact]
    public async Task GET_СервисныйТокен_200_НастройкиЧитаются()
    {
        var res = await Controller(svc: true).Get();

        var dto = (WikiSettingsDto)((OkObjectResult)res.Result!).Value!;
        dto.Nightly.InputTokenBudget.Should().Be(700_000);
    }

    // ─── построение состояния ───────────────────────────────────────────────────

    private string Root() => WikiPaths.Root(_dir, User);
    private string SettingsPath() => Path.Combine(Root(), "keeper/settings.json");
    private string LastNightPath() => Path.Combine(Root(), "keeper", "nightly-last.json");

    private void WriteLastNight(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastNightPath())!);
        File.WriteAllText(LastNightPath(), json, new UTF8Encoding(false));
    }

    private WikiSettingsController Controller(string user = User, bool svc = false)
    {
        var http = new DefaultHttpContext();
        var identity = new ClaimsIdentity([new Claim("sub", user)], "test");
        // Сервисный токен хода (typ=svc) — тот же claim-литерал, что выдаёт JwtService;
        // MapInboundClaims=false оставляет raw-имена, как в бою.
        if (svc) identity.AddClaim(new Claim("typ", "svc"));
        http.User = new ClaimsPrincipal(identity);
        return new WikiSettingsController(_health, _flags, _keeper, _config)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static void AssertCode(IActionResult result, int status, string code)
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    private sealed class FlagGate : IFeatureFlagGate
    {
        public bool Enabled = true;
        public bool IsEnabled(string userId, string key) => Enabled;
    }

    private sealed class Notifier : INotificationSender
    {
        public List<CreateNotificationRequest> Sent { get; } = [];

        public Task<string> SendAsync(string userId, CreateNotificationRequest req, bool sendPush = false)
        {
            Sent.Add(req);
            return Task.FromResult("n-" + Sent.Count);
        }
    }
}
