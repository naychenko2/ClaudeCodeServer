using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Notes.Controllers;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Tests.Services;

// S4c (ADR-035, решение 12г, меры 7–8): автовыключение класса `origin × op`, доля откатов
// и настройки применения. Состояние строится дисковым, а не гонкой: применённые и откатанные
// пакеты пишутся на диск с явными временами, окна проверяются по ним (граница 7-дневного окна
// — ятивным «сейчас» оценки, internal-перегрузка). Часы процесса в тестах не участвуют.
//
// Окна: доля считается по окну последних 50 применённых операций класса; «3 отката за 7 дней»
// — по откатам, случившимся в [now−7д, now]. Метрики 7/30 дней — доля откатанных среди
// применённых в этом окне. Пустой класс (нет применённых операций) — пустая оценка: ни записи
// метрик, ни настроек, ни уведомления.
public class WikiClassHealthTests : IDisposable
{
    private const string User = "u1";
    private const string Other = "u2";
    private const string Section = "common";
    private const string ClassKey = "session×append";
    private const string SourceRef = "session:chat-A";

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly FlagGate _flags = new();
    private readonly Notifier _notifier = new();
    private readonly WikiClassHealth _sut;

    public WikiClassHealthTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_class_health_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _sut = new(_config, _flags, _notifier);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── c1: пороги меры 7 ────────────────────────────────────────────────────

    [Fact]
    public async Task ПятьОткатовИз50ВыключаютКлассПоДоле_УведомлениеСПричинойИДолей()
    {
        var t = DateTimeOffset.UtcNow - TimeSpan.FromDays(10); // вне 7-дневного окна: срабатывает только доля
        for (var i = 0; i < 50; i++)
            WritePackage(t, retractedAt: i < 5 ? t : null);

        var outcome = await _sut.EvaluateAsync(User);

        outcome.State.Should().Be(WikiClassHealth.RunState.Ran);
        ReadSettings().IsClassDisabled(ClassKey).Should().BeTrue("5 откатов из 50 операций окна — 10 %, порог 10 %");
        _notifier.Sent.Should().HaveCount(1);
        _notifier.Sent[0].Body.Should().Contain("5 откатов из 50").And.Contain("10 %");
        // Метрики меры 8: тот же счёт в строке метрик (30-дневное окно включает все операции).
        File.ReadAllText(MetricsPath()).Should().Contain("\"retracted30\":5");
    }

    [Fact]
    public async Task ЧетыреОткатаИз50ИДваЗаСемьДнейКлассНеВыключают()
    {
        var now = DateTimeOffset.UtcNow;
        var t = now - TimeSpan.FromDays(1);
        for (var i = 0; i < 46; i++) WritePackage(t);
        for (var i = 0; i < 2; i++) WritePackage(t, retractedAt: t); // откаты внутри 7 дней
        for (var i = 0; i < 2; i++) WritePackage(t, retractedAt: now - TimeSpan.FromDays(10)); // старше

        await _sut.EvaluateAsync(User);

        // 4 из 50 = 8 % < 10 %; за 7 дней — 2 < 3. Класс остаётся включённым, сигнала нет.
        ReadSettings().IsClassDisabled(ClassKey).Should().BeFalse();
        _notifier.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ТриОткатаЗаСемьДнейВыключаютКлассПриНизкойДоле()
    {
        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        for (var i = 0; i < 47; i++) WritePackage(old);
        for (var i = 0; i < 3; i++) WritePackage(old, retractedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(1));

        await _sut.EvaluateAsync(User);

        // Доля 3/50 = 6 % < 10 %, но приток откатов за 7 дней — 3: класс выключается по нему.
        ReadSettings().IsClassDisabled(ClassKey).Should().BeTrue();
        _notifier.Sent.Should().HaveCount(1);
        _notifier.Sent[0].Body.Should().Contain("3 откатов за 7 дней");
    }

    // ─── B4a: класс замены человека «…@human» — отдельный класс автовыключения ──

    private const string HumanClassKey = "session×supersede@human";

    [Fact]
    public async Task ТриОткатаЗаСемьДнейВыключаютКлассСХвостомHuman_ДругойКлассНеТронут()
    {
        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        for (var i = 0; i < 47; i++) WritePackage(old, classKey: HumanClassKey);
        for (var i = 0; i < 3; i++) WritePackage(old, retractedAt: recent, classKey: HumanClassKey);
        // Класс замены ИИ в тех же условиях без откатов: окна у классов свои.
        for (var i = 0; i < 50; i++) WritePackage(old, classKey: "session×supersede");

        await _sut.EvaluateAsync(User);

        var settings = ReadSettings();
        settings.IsClassDisabled(HumanClassKey).Should().BeTrue("ключ с хвостом @human разбирается и оценивается как класс");
        settings.IsClassDisabled("session×supersede").Should().BeFalse("откаты @human не размываются в классе замен ИИ");
    }

    private const string SharedPage = "Общее/Моя-страница.md";

    private void WriteRestoreLine(string page, string actor = "user") =>
        WikiManifest.AppendLog(Root(), "restore", Section, [page], "human:x", actor);

    [Fact]
    public async Task ТриВернутьМоюЗаСемьДнейВыключаютКлассСХвостомHuman_КлассЗаменИИНе()
    {
        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        for (var i = 0; i < 3; i++) WritePackage(old, classKey: HumanClassKey, page: SharedPage);
        for (var i = 0; i < 3; i++) WritePackage(old, classKey: "session×supersede", page: SharedPage);
        for (var i = 0; i < 3; i++) WriteRestoreLine(SharedPage);

        await _sut.EvaluateAsync(User);

        var settings = ReadSettings();
        settings.IsClassDisabled(HumanClassKey).Should().BeTrue("«Вернуть мою» — откат для класса с хвостом @human");
        settings.IsClassDisabled("session×supersede").Should().BeFalse("замены ИИ сигналом не задеваются");
    }

    [Fact]
    public async Task ВернутьМоюНаСтраницеБезКлассаСХвостом_НеВыключаетНиОдинКласс()
    {
        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        for (var i = 0; i < 3; i++) WritePackage(old, classKey: "session×supersede", page: SharedPage);
        for (var i = 0; i < 3; i++) WriteRestoreLine(SharedPage);

        await _sut.EvaluateAsync(User);

        ReadSettings().IsClassDisabled("session×supersede").Should().BeFalse();
        ReadSettings().Classes.Should().BeEmpty();
    }

    [Fact]
    public async Task ДваВернутьМою_НеДостигаютПорогаИСтрокаНеОтПользователяНеСчитается()
    {
        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        for (var i = 0; i < 60; i++) WritePackage(old, classKey: HumanClassKey, page: SharedPage);
        WriteRestoreLine(SharedPage);
        WriteRestoreLine(SharedPage);
        WriteRestoreLine(SharedPage, actor: "keeper");

        await _sut.EvaluateAsync(User);

        ReadSettings().IsClassDisabled(HumanClassKey).Should().BeFalse("два откатов из 60 < порога, чужой actor не считается");
    }

    [Theory]
    [InlineData("session×supersede@human", true)]
    [InlineData("note×conflict@human", true)]
    [InlineData("session×append@human", false)]
    [InlineData("session×supersede@ai", false)]
    [InlineData("×supersede@human", false)]
    [InlineData("session×@human", false)]
    [InlineData("session×supersede@human@human", false)]
    public void IsValidClassKey_ХвостHuman_ТолькоУSupersedeИConflict(string key, bool valid) =>
        WikiSettingsStore.IsValidClassKey(key).Should().Be(valid);

    [Fact]
    public async Task ОткатРовноНаГраницеСемидневногоОкнаУчитывается()
    {
        // Явное «сейчас» оценки: событие ровно на границе [now−7д, now) входит в счётчик.
        var now = DateTimeOffset.UtcNow;
        var old = now - TimeSpan.FromDays(10);
        var boundary = now - TimeSpan.FromDays(7);
        for (var i = 0; i < 47; i++) WritePackage(old);
        for (var i = 0; i < 3; i++) WritePackage(old, retractedAt: boundary);

        await _sut.EvaluateAsync(User, now);

        ReadSettings().IsClassDisabled(ClassKey).Should().BeTrue("откат ровно на границе окна — внутри окна");
    }

    [Fact]
    public async Task НетПрименённыхОпераций_ОценкаПустаяИНичегоНеПишет()
    {
        var outcome = await _sut.EvaluateAsync(User);

        outcome.State.Should().Be(WikiClassHealth.RunState.Ran);
        outcome.Stats.Should().BeEmpty();
        File.Exists(SettingsPath()).Should().BeFalse();
        File.Exists(MetricsPath()).Should().BeFalse();
        _notifier.Sent.Should().BeEmpty();
    }

    // ─── c2: откат по источнику засчитывается каждой затронутой операции ──────

    [Fact]
    public async Task ОткатИсточникаЗадетриОперацииДаётТриОткатаВСчетчикеКласса()
    {
        var t = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        var ids = new List<string>();
        var pages = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var id = WritePackage(t); // статус остаётся «applied»: это снятие сносок, не побайтный откат
            ids.Add(id);
            pages.Add(PagePath(id));
        }
        WriteRetractLine(ids, pages);

        await _sut.EvaluateAsync(User);

        // Одна строка retract, задетая три операции класса, — три отката: порог «3 за 7 дней» пройден.
        ReadSettings().IsClassDisabled(ClassKey).Should().BeTrue();
        File.ReadAllText(MetricsPath()).Should().Contain("\"retracted7\":3");
    }

    // ─── c3: выключенный класс читается политикой; возврат обнуляет окно ──────

    [Fact]
    public async Task ВыключенныйКлассПолитикаОтправляетВОходящие_ВозвратОбнуляетОкно()
    {
        var t = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        for (var i = 0; i < 3; i++) WritePackage(t, retractedAt: t);
        (await _sut.EvaluateAsync(User)).State.Should().Be(WikiClassHealth.RunState.Ran);

        var settings = ReadSettings();
        settings.IsClassDisabled(ClassKey).Should().BeTrue();

        var verdict = WikiApplyPolicy.Decide(
            new WikiPackage.Operation
            {
                Op = WikiPackage.OpAppend, Page = "Общее/Сущность.md", Text = "утверждение",
                Class = ClassKey, Decision = WikiPackage.DecisionAuto,
            },
            new WikiApplyPolicy.ApplyContext { DisabledClasses = settings.DisabledClasses });
        verdict.IsAuto.Should().BeFalse();
        verdict.Reason.Should().Contain("выключен");

        // «Снова применять само» — окно начинается заново: старые откаты в него не входят.
        var put = Controller(User).Put(new WikiSettingsRequest { Class = ClassKey, Enable = true });
        ((OkObjectResult)put.Result!).StatusCode.Should().Be(200);

        await _sut.EvaluateAsync(User);

        ReadSettings().IsClassDisabled(ClassKey).Should().BeFalse("окно после возврата пусто — прежние откаты в нём не считаются");
        _notifier.Sent.Should().HaveCount(1); // повторного уведомления нет: переход был один
    }

    [Fact]
    public async Task ОперацияРовноВМоментВозвратаВНовоеОкноНеВходит()
    {
        var resume = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        WikiSettingsStore.Mutate(_dir, User, _ => new WikiSettingsStore.Settings(
            WikiSettingsStore.ModelVersion, true,
            [new WikiSettingsStore.ClassState(ClassKey, false, null, null, resume)]));

        WritePackage(resume, retractedAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(1)); // ровно в момент возврата

        var outcome = await _sut.EvaluateAsync(User);

        outcome.Stats.Should().BeEmpty("окно [resumedAt, ∞) не включает операцию, созданную ровно в resumedAt");
        ReadSettings().IsClassDisabled(ClassKey).Should().BeFalse();
    }

    // ─── c4: невалидный settings.json и чужая вики ────────────────────────────

    [Fact]
    public async Task НевалидныйSettingsJson_GETДаётУмолчания_ЧужаяВики403()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllText(SettingsPath(), "{ это не json", new UTF8Encoding(false));

        // GET не падает и отвечает умолчания: «Применять без ревью» включён, классов нет.
        var res = await Controller(User).Get();
        var dto = (WikiSettingsDto)((OkObjectResult)res.Result!).Value!;
        dto.AutoApplyEnabled.Should().BeTrue();
        dto.Classes.Should().BeEmpty();
        File.ReadAllText(SettingsPath()).Should().Be("{ это не json"); // чтение ничего не «чинит» молча

        AssertCode(Controller(User).Put(new WikiSettingsRequest { OwnerId = Other, AutoApplyEnabled = false }).Result!, 403, "wiki_foreign");
    }

    // ─── c5: уведомление — один раз на переход ─────────────────────────────────

    [Fact]
    public async Task УведомлениеОбАвтовыключенииПриходитОдинРазНаПереход()
    {
        var t = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        for (var i = 0; i < 3; i++) WritePackage(t, retractedAt: t);

        await _sut.EvaluateAsync(User);
        _notifier.Sent.Should().HaveCount(1);

        await _sut.EvaluateAsync(User); // класс уже выключен: это не переход, тика нет
        _notifier.Sent.Should().HaveCount(1);

        // Возврат с окном в прошлом + новые откатанные операции после него — новый переход, новое уведомление.
        var resume = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        WikiSettingsStore.Mutate(_dir, User, cur => cur with
        {
            Classes = cur.Classes
                .Select(c => c.Class == ClassKey ? c with { Disabled = false, Reason = null, DisabledAt = null, ResumedAt = resume } : c)
                .ToList(),
        });
        for (var i = 0; i < 3; i++)
            WritePackage(DateTimeOffset.UtcNow - TimeSpan.FromHours(1), retractedAt: DateTimeOffset.UtcNow - TimeSpan.FromHours(1));

        await _sut.EvaluateAsync(User);
        _notifier.Sent.Should().HaveCount(2);
    }

    // ─── конкурентность: два PUT и оценка не рвут settings.json ───────────────

    [Fact]
    public async Task ОдновременныеPUTIOценкаНеРвутНастройкиИНеЗатираютДругДруга()
    {
        var t = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        for (var i = 0; i < 3; i++) WritePackage(t, retractedAt: t); // оценка по этому состоянию выключает класс

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var putOff = Task.Run(() => { gate.Task.GetAwaiter().GetResult(); return Controller(User).Put(new WikiSettingsRequest { AutoApplyEnabled = false }); });
        var putResume = Task.Run(() => { gate.Task.GetAwaiter().GetResult(); return Controller(User).Put(new WikiSettingsRequest { Class = ClassKey, Enable = true }); });
        var evaluate = Task.Run(() => { gate.Task.GetAwaiter().GetResult(); return _sut.EvaluateAsync(User); });
        gate.SetResult();

        // Ждём все три: взаимной блокировки здесь нет по построению (замок стора держится только
        // на время файловой записи, цикл замков невозможен), поэтому await, а не блокирующий Wait.
        await Task.WhenAll(putOff, putResume, evaluate);

        // Файл цел (read-modify-write под per-owner замком стора). Итог детерминирован при любом
        // порядке: выключатель поменялся (оценка его не трогает), класс остался возвращённым —
        // выключение опоздало к возврату и merge его пропустил (окно сдвинулось).
        var (settings, invalid) = WikiSettingsStore.Read(Root());
        invalid.Should().BeFalse();
        settings.AutoApplyEnabled.Should().BeFalse();
        settings.IsClassDisabled(ClassKey).Should().BeFalse();
    }

    // ─── построение состояния ─────────────────────────────────────────────────

    private static readonly JsonSerializerOptions PkgJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Пакет на диске: состояние, которое читает оценка (стор проверяет форму файла).
    /// retractedAt != null — статус «reverted» с временем отката в UpdatedAt (переход статуса
    /// ставит его в момент отката — та же форма, что в бою).</summary>
    private string WritePackage(DateTimeOffset createdAt, DateTimeOffset? retractedAt = null, string classKey = ClassKey, string? page = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var parts = classKey.Split('×');
        var pkg = new WikiPackage
        {
            V = WikiPackage.ModelVersion,
            Id = id,
            Section = Section,
            Sources = [new WikiPackage.SourceRef(parts[0], SourceRef)],
            Operations = [new WikiPackage.Operation
            {
                Op = parts[1].Replace(WikiPackage.HumanTargetSuffix, ""),
                Page = page ?? PagePath(id),
                Text = "утверждение из чата",
                Target = parts[1].StartsWith("supersede") || parts[1].StartsWith("conflict") ? "заменяемое утверждение" : null,
                Class = classKey,
                Decision = WikiPackage.DecisionAuto,
            }],
            Status = retractedAt is null ? WikiPackage.StatusApplied : WikiPackage.StatusReverted,
            CreatedAt = createdAt,
            UpdatedAt = retractedAt ?? createdAt,
        };
        var dir = Path.Combine(WikiPaths.Keeper(_dir, User), "packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(pkg, PkgJsonOpts), new UTF8Encoding(false));
        return id;
    }

    private static string PagePath(string id) => "Общее/P-" + id + ".md";

    /// <summary>Строка op=retract — та же, что пишет откат по источнику (S4f): пакеты и
    /// затронутые страницы. Время — UtcNow строки.</summary>
    private void WriteRetractLine(IReadOnlyList<string> packageIds, IReadOnlyList<string> pages) =>
        WikiManifest.AppendLog(Root(), "retract", Section, pages, SourceRef, "user", null, packageIds);

    private string Root() => WikiPaths.Root(_dir, User);
    private string SettingsPath() => Path.Combine(Root(), "keeper/settings.json");
    private string MetricsPath() => Path.Combine(Root(), "keeper/metrics.jsonl");
    private WikiSettingsStore.Settings ReadSettings() => WikiSettingsStore.Read(Root()).Settings;

    private WikiSettingsController Controller(string user = User)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "test"));
        return new WikiSettingsController(_sut, _flags, _config)
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
        public bool IsEnabled(string userId, string key) => true;
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
