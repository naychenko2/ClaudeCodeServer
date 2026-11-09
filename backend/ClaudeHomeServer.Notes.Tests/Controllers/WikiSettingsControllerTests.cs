using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
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

    // ─── WN-5: ручной запуск (POST nightly/run) ────────────────────────────────

    [Fact]
    public async Task POSTРучногоЗапускаЭто202_ИдущийПроходВиденВГете_ИВторойЗапуск409Running()
    {
        EnableNightly();
        AddNightlyChat();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sut = NightlyService(async (o, c, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            return new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []);
        });

        var res = Controller(sut).NightlyRun();
        var obj = res.Should().BeAssignableTo<ObjectResult>("202 с телом").Which;
        obj.StatusCode.Should().Be(202);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement.GetProperty("started").GetBoolean()
            .Should().BeTrue();

        // Таймаут вместо зависания: если проход не дошёл до разбора, тест должен покраснеть.
        var reached = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        reached.Should().BeSameAs(entered.Task, "ручной проход обязан дойти до подменённого разбора");
        AssertCode(Controller(sut).NightlyRun(), 409, "wiki_nightly_running",
            "тот же замок, что у тика: второй запуск того же владельца отвергнут");
        var running = (WikiSettingsDto)((OkObjectResult)(await Controller(sut).Get()).Result!).Value!;
        running.NightlyRunning.Should().BeTrue("кнопка обязана показывать: разбор идёт");

        gate.SetResult();
        await WaitForIdleAsync(Root());
        var after = (WikiSettingsDto)((OkObjectResult)(await Controller(sut).Get()).Result!).Value!;
        after.NightlyRunning.Should().BeFalse("после прохода замок снят");
        after.LastNight!.Manual.Should().BeTrue("ручной проход помечен в итоге — дату он не закрывает");
    }

    [Fact]
    public void POSTРучногоЗапускаПриВыключеннойНочиЭто409_NightlyDisabled_БезЗамка()
    {
        // Настроек ночи нет вовсе — блок читается умолчаниями (выключено): отказ до замка.
        var res = Controller(NightlyService(Immediate())).NightlyRun();

        AssertCode(res, 409, "wiki_nightly_disabled");
        WikiNightlyService.IsRunning(Root()).Should().BeFalse("отказ до замка");
    }

    [Fact]
    public void POSTРучногоЗапускаПриВыключенномФлагеЭто409_LlmWikiOff()
    {
        EnableNightly();
        _flags.Enabled = false;

        var res = Controller(NightlyService(Immediate())).NightlyRun();

        AssertCode(res, 409, "wiki_llm_wiki_off");
        WikiNightlyService.IsRunning(Root()).Should().BeFalse();
    }

    [Fact]
    public void POSTРучногоЗапускаПриНечитаемыхНастройкахЭто409_SettingsInvalid()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllText(SettingsPath(), "{ это не json");

        var res = Controller(NightlyService(Immediate())).NightlyRun();

        AssertCode(res, 409, "wiki_settings_invalid");
        WikiNightlyService.IsRunning(Root()).Should().BeFalse();
    }

    [Fact]
    public void POSTРучногоЗапускаБезСлужбыЭто503()
    {
        // Состав без ночной службы: необязательный параметр ctor → 503 с телом, а не 500 без тела.
        // Ответ — общей формой проекта { error = … } (TasksController.cs:760), своего кода не заводим.
        var res = Controller().NightlyRun();

        AssertError(res, 503, "Ночной разбор не зарегистрирован в этом составе");
    }

    // B2b: ручной проход запускает только человек — svc отвергается 403 до службы и до замка.
    // Мутация (критерий c2): убрать проверку IsServiceToken в NightlyRun — этот тест красный:
    // при включённой ночи и живом чате svc дошёл бы до TryStartManual и получил 202 с замком.
    [Fact]
    public void POST_СервисныйТокен_403_НиЗапускаНиЗамка()
    {
        EnableNightly();
        AddNightlyChat();

        var res = Controller(NightlyService(Immediate()), svc: true).NightlyRun();

        AssertError(res, 403, "Сервисный JWT: ночной разбор запускает только пользовательский токен");
        // Отказ до TryStartManual: фоновый проход не начат, замок свободного владельца не занят.
        WikiNightlyService.IsRunning(Root()).Should().BeFalse("отказ до замка: проход не начат");
    }

    // Отказ по типу токена раньше любого состояния: даже при отсутствующей в составе службе
    // ответ — 403 по токену, а не 503 (тот же смысл, что у PUT_СервисныйТокенПриВыключенномФлаге).
    [Fact]
    public void POST_СервисныйТокенБезСлужбы_403ПоТокену_АНе503()
    {
        var res = Controller(svc: true).NightlyRun();

        AssertError(res, 403, "Сервисный JWT: ночной разбор запускает только пользовательский токен");
    }

    // ─── построение состояния ───────────────────────────────────────────────────

    // Служба для тестов ручного запуска — настоящая (тот же internal-шов, что в
    // WikiNightlyServiceTests): подменены только разбор и часы, стор, Хранитель и замок —
    // настоящие. Хранитель обязателен: вики здесь не заведена, и проход ручного запуска
    // заводит её тем же путём, что ночной тик (TryEnsureInitializedAsync).
    private readonly SessionsStub _sessions = new();
    private readonly LlmStub _llm = new();
    private readonly DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private WikiNightlyService NightlyService(
        Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>> analyze) =>
        new(_sessions, new WikiChatRecalc(_sessions, _config), new WikiChatStage2(_llm, _sessions, _config),
            analyze, _llm, _config, NullLogger<WikiNightlyService>.Instance, _flags, clock: () => _now, keeper: _keeper);

    private static Func<string, string, CancellationToken, Task<WikiChatIngestService.ChatResult>> Immediate() =>
        (o, c, ct) => Task.FromResult(new WikiChatIngestService.ChatResult(WikiChatIngestService.OutcomeNothingNew, null, []));

    private void EnableNightly() => WikiSettingsStore.Mutate(_dir, User, s => s with
    {
        Nightly = new WikiSettingsStore.NightlyConfig(true, 3, 6, 20, 6, 10, 700_000),
    });

    // Важный чат с дельтой: чтобы тестовый проход реально дошёл до разбора (тестам замка нужен
    // живой проход, а не пустая очередь).
    private void AddNightlyChat()
    {
        _sessions.All.Add(new Session
        {
            Id = "c1", OwnerId = User, ProjectId = "p1", Name = "c1",
            WikiMark = WikiMarks.Important, UpdatedAt = DateTime.UtcNow,
        });
        var h = new List<StoredMessage>();
        for (var i = 0; i < 4; i++)
        {
            h.Add(new StoredUserMessage($"Реплика {i} " + new string('а', 100)));
            h.Add(new StoredTextMessage("Принято."));
        }
        _sessions.History["c1"] = h;
    }

    // Ожидание окончания прохода: за тестом не должна оставаться фоновая работа —
    // Dispose удалит каталог, а следующий тест класса не обязан попадать в занятого владельца.
    private static async Task WaitForIdleAsync(string root)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (WikiNightlyService.IsRunning(root) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        WikiNightlyService.IsRunning(root).Should().BeFalse("проход обязан завершиться");
    }

    private string Root() => WikiPaths.Root(_dir, User);
    private string SettingsPath() => Path.Combine(Root(), "keeper/settings.json");
    private string LastNightPath() => Path.Combine(Root(), "keeper", "nightly-last.json");

    private void WriteLastNight(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LastNightPath())!);
        File.WriteAllText(LastNightPath(), json, new UTF8Encoding(false));
    }

    private WikiSettingsController Controller(WikiNightlyService? nightly = null, string user = User, bool svc = false)
    {
        var http = new DefaultHttpContext();
        var identity = new ClaimsIdentity([new Claim("sub", user)], "test");
        // Сервисный токен хода (typ=svc) — тот же claim-литерал, что выдаёт JwtService;
        // MapInboundClaims=false оставляет raw-имена, как в бою.
        if (svc) identity.AddClaim(new Claim("typ", "svc"));
        http.User = new ClaimsPrincipal(identity);
        return new WikiSettingsController(_health, _flags, _keeper, _config, nightly)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static void AssertCode(IActionResult result, int status, string code, string because = "")
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status, because);
        JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement
            .GetProperty("code").GetString().Should().Be(code);
    }

    // Общая форма отказа проекта: { error = … } без своего кода (TasksController.cs:760) —
    // вызывающий не различает подпричины по коду, только сам отказ.
    private static void AssertError(IActionResult result, int status, string error, string because = "")
    {
        var obj = result.Should().BeAssignableTo<ObjectResult>($"{status} с телом отказа").Which;
        obj.StatusCode.Should().Be(status, because);
        var root = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value)).RootElement;
        root.GetProperty("error").GetString().Should().Be(error);
        root.TryGetProperty("code", out _).Should().BeFalse("своего кода в этой форме нет");
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

    // Пустая директория чатов с одной подставной историей: проходу службы нужен хоть один
    // чат, чтобы дойти до подменённого разбора (тот же фикстуры-класс, что в тестов службы).
    private sealed class SessionsStub : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(History.TryGetValue(sessionId, out var h) ? h : []);
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }

    // Модель тестов ручного запуска не зовётся вовсе (разбор подменён делегатом службы):
    // шов службы требует только HasRoute.
    private sealed class LlmStub : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => true;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "fake";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult("{}");

        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default) =>
            Task.FromResult<string?>("{}");

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null, object? jsonFormat = null,
            CancellationToken ct = default) =>
            Task.FromResult(new OneShotResult("{}", null, 0));
    }
}
