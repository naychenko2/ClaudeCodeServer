using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
public class WikiSettingsControllerTests : IDisposable
{
    private const string User = "u1";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FlagGate _flags = new();
    private readonly Notifier _notifier = new();
    private readonly WikiClassHealth _health;

    public WikiSettingsControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_settings_controller_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _health = new(_config, _flags, _notifier);
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
    public void PUTЧастичногоНочногоБлокаМержитсяСУмолчаниямиИЗаписывается()
    {
        var put = Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true, WindowStartHour = 21 } });

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
    public void PUTПриВыключенномФлагеLlmWikiЭто409_ФайлНеСоздан()
    {
        _flags.Enabled = false;

        var put = Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        AssertCode(put.Result!, 409, "wiki_llm_wiki_off");
        File.Exists(SettingsPath()).Should().BeFalse("отказ до любой записи: ни байта на диске");
    }

    // ─── валидация merged-блока и «менять нечего» ───────────────────────────────

    [Fact]
    public void PUTНочногоБлокаСКривымMergedРезультатомЭто400_ФайлНеТронут()
    {
        var ok = Controller().Put(new WikiSettingsRequest { Nightly = new() { WindowStartHour = 3, WindowEndHour = 6 } });
        ((OkObjectResult)ok.Result!).StatusCode.Should().Be(200);

        // Начало уже в файле (3); request ставит конец 3 — кривым становится merged-целое,
        // хотя само по себе поле корректно.
        var bad = Controller().Put(new WikiSettingsRequest { Nightly = new() { WindowEndHour = 3 } });
        AssertCode(bad.Result!, 400, "wiki_nightly");

        var bad2 = Controller().Put(new WikiSettingsRequest { Nightly = new() { WindowStartHour = 24 } });
        AssertCode(bad2.Result!, 400, "wiki_nightly");

        var (settings, _) = WikiSettingsStore.Read(Root());
        settings.Nightly!.WindowStartHour.Should().Be(3);
        settings.Nightly!.WindowEndHour.Should().Be(6); // отказанные PUT не тронули файл
    }

    [Fact]
    public void PUTПустогоНочногоБлокаЭтоНечегоМенять_ФайлНеСоздан()
    {
        var put = Controller().Put(new WikiSettingsRequest { Nightly = new() });

        AssertCode(put.Result!, 400, "wiki_settings_nothing_to_change");
        File.Exists(SettingsPath()).Should().BeFalse();
    }

    // ─── отказ записи: код в ответе, temp-файл убран ────────────────────────────

    [Fact]
    public void ОтказЗаписиФайлаЭтоКодВОтвете_БезTempФайловНаДиске()
    {
        // settings.json — каталог: атомарный перенос (temp→Move) не может его заменить,
        // запись отказана. Так же ведёт себя диск, отказавший запись.
        Directory.CreateDirectory(SettingsPath());

        var put = Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        AssertCode(put.Result!, 500, "wiki_settings_write_failed");
        Directory.GetFiles(Path.Combine(Root(), "keeper")).Should()
            .NotContain(f => f.EndsWith(".tmp"), "WriteAtomic убирает свой temp перед тем, как бросать дальше");
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
    public void PUTИтогаНочиНеНесёт_ДажеКогдаФайлЕсть()
    {
        // PUT меняет настройки, а не читает прошлые проходы: стор фронта хранит lastNight
        // от последнего GET (wiki.ts:983-985).
        WriteLastNight(LastNightJson);

        var put = Controller().Put(new WikiSettingsRequest { Nightly = new() { Enabled = true } });

        var dto = (WikiSettingsDto)((OkObjectResult)put.Result!).Value!;
        dto.LastNight.Should().BeNull();
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

    private WikiSettingsController Controller(string user = User)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "test"));
        return new WikiSettingsController(_health, _flags, _config)
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
