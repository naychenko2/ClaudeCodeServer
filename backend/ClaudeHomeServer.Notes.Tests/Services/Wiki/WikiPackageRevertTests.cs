using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// W3-3 (хвосты S4b-3): журнал отката различает пакеты, «уже в состоянии «до пакета»» — названный
// исход, а не молчание, запись состояния применения живёт тот же срок, что прообразы, и защищена
// у пакета, который откатывается. Существующее поведение (побайтный возврат, отказ по чужой правке,
// доводка сорванного Transition, REST) — в WikiRevertTests; здесь — то, что W3-3 меняет.
//
// Окна ставятся дисковым состоянием или держателем блокировки, а не гонкой: Task.Delay в этом
// файле нет.
public class WikiPackageRevertTests : IDisposable
{
    private const string User = "u1";
    private const string Section = "common";
    private const string CardTitle = "Карточка чата";
    private const string CardPath = "Общее/Карточка-чата.md";
    private const string PagePath = "Общее/Сущность.md";
    private const string NewPagePath = "Общее/Новое-знание.md";

    // Текст источника: по нему выводится цитата (мера 1). Утверждения тестов — его части.
    private static readonly string SourceText =
        "Решение: перенести сборку на Linux. Прежний план был остаться на Windows. Контенер собран на второй машине.";

    // Объявляется после SourceText: статические поля инициализируются по порядку объявления.
    private static readonly WikiApplyService.ApplyContext Ctx = new(CardTitle, SourceText);

    private static readonly string CardFile = $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{CardTitle}}
        """;

    private static readonly string PageBase = $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка
        Прежний план был остаться на Windows [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[{{CardTitle}}]] — «Прежний план был остаться на Windows»
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly RecordingEventLog _events = new();
    private readonly WikiApplyService _sut;
    private readonly WikiPackageRevert _revert;

    public WikiPackageRevertTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_pkg_revert_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        // Флаг llm-wiki включён: состояние читается моделью (тот же приём, что в WikiApplyTests).
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, _events, new WikiFlagOn());
        _keeper = new WikiKeeper(_notes, _config, _events);
        _sut = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, _events);
        _revert = new WikiPackageRevert(_notes, _keeper, _config);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class WikiFlagOn : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class RecordingEventLog : IProjectEventLogService
    {
        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null) => null;
    }

    private string Root => WikiPaths.Root(_dir, User);
    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);
    private string PreimageOf(string packageId, string pageKey) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "preimages", packageId, pageKey);
    private string AppliedRecordOf(string packageId) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "applied", packageId + ".json");

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private List<string> LogLines()
    {
        var p = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Length > 0).ToList() : [];
    }

    private List<string> RevertLines() => LogLines().Where(l => l.Contains("\"op\":\"revert\"")).ToList();

    // id пакетов, названных в строке журнала (поле packages, та же форма, что у op=retract).
    private static List<string?> LinePackageIds(string line) =>
        JsonDocument.Parse(line).RootElement.TryGetProperty("packages", out var ids) &&
        ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Select(x => x.GetString())]
            : [];

    private async Task WriteViaKeeper(string pageKey, string content)
    {
        var r = await _keeper.WritePageAsync(User, Section, pageKey, content);
        r.Status.Should().Be(WikiOpStatus.Applied, r.Reason);
    }

    private async Task WriteCardAndPage()
    {
        await WriteViaKeeper(CardPath, CardFile);
        await WriteViaKeeper(PagePath, PageBase);
    }

    private Dictionary<string, string> SnapshotPages()
    {
        var pagesDir = WikiPaths.Pages(_dir, User);
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(pagesDir, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(pagesDir, f).Replace('\\', '/')] = Sha256Of(File.ReadAllBytes(f));
        return snap;
    }

    private static WikiPackage.Operation Op(string op, string page, string? text) => new()
    {
        Op = op,
        Page = page,
        Text = text,
        Class = WikiPackage.ClassKey("session", op),
        Decision = WikiPackage.DecisionAuto,
        Reason = null,
        BaseHashes = new Dictionary<string, string>(),
        HumanDecision = null,
        HumanReason = null,
    };

    // Пакет с тем же источником («session:chat-1»), доведённый до «accepted»: идентичный источник
    // и идентичные страницы — это про него W3-3, пункт 1.
    private WikiPackage CreateAccepted(params WikiPackage.Operation[] ops)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(Root, pkg);
        created.Ok.Should().BeTrue(created.Reason);
        WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        var accepted = WikiPackageStore.Transition(Root, pkg.Id, WikiPackage.StatusAccepted);
        accepted.Ok.Should().BeTrue(accepted.Reason);
        return WikiPackageStore.TryRead(Root, pkg.Id).Package!;
    }

    // Пакет, доведённый переходом стора до «apply_failed» (то же, что делает сторож старта):
    // его файлы на диск не записаны — для чистки записи состояния применения этого достаточно.
    private string NewApplyFailedPackageInStore()
    {
        var folder = WikiSections.Folder(Section);
        folder.Should().NotBeNull($"«{Section}» — не ключ раздела вики");
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = Section,
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            // Folder() отдаёт папку с хвостовым «/» — страница склеивается без разделителя.
            Operations = [Op(WikiPackage.OpAppend, folder + "X.md", "Контенер собран на второй машине")],
            Status = WikiPackage.StatusChecking,
        };
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        foreach (var step in new[] { WikiPackage.StatusProposed, WikiPackage.StatusApplying, WikiPackage.StatusApplyFailed })
            WikiPackageStore.Transition(Root, pkg.Id, step).Ok.Should().BeTrue(step);
        return pkg.Id;
    }

    private (string Status, string? Reason) ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return (read.Package!.Status, read.Package!.StatusReason);
    }

    private void WriteAppliedRecord(string packageId, string body, DateTimeOffset written)
    {
        var full = AppliedRecordOf(packageId);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(full, written.UtcDateTime);
    }

    // ─── 1. Два пакета с идентичным источником и идентичными страницами: по строке op=revert ─
    // ─── на каждый фактический откат (пункт 1, «конкурентность» п.2) ───────────────────────────

    [Fact]
    public async Task Revert_ДваПакетаСОдинаковымИсточникомИСтраницами_ПоСтрокеRevertНаКаждый()
    {
        // Мутация «RevertAlreadyLogged возвращает true всегда»: ни одна строка не пишется —
        // HaveCount(2) краснеет (и цепочка не возвращается: Last().Should().Equal краснеет следом).
        await WriteCardAndPage();
        var beforeChain = File.ReadAllBytes(PageFull(PagePath));

        var a = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var appliedA = await _sut.ApplyAsync(User, a, Ctx);
        appliedA.Applied.Should().BeTrue(appliedA.Reason);

        // Тот же источник, та же страница: для человека обе строки журнала выглядят одинаково.
        var b = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Решение: перенести сборку на Linux"));
        var appliedB = await _sut.ApplyAsync(User, b, Ctx);
        appliedB.Applied.Should().BeTrue(appliedB.Reason);

        // Откат в обратном порядке применения: на диске состояние B, затем A.
        var rb = await _revert.RevertAsync(User, b.Id);
        rb.Reverted.Should().BeTrue(rb.Reason);
        var ra = await _revert.RevertAsync(User, a.Id);
        ra.Reverted.Should().BeTrue(ra.Reason);

        // Ровно одна строка на каждый фактический откат: идентичные источник и страницы не делают
        // второй откат «уже сделанным» — признак строки её id.
        var lines = RevertLines();
        lines.Should().HaveCount(2, "два фактических отката — две строки журнала");
        // Каждая строка называет свой пакет: идентичные источник и страницы не делают второй
        // откат уже сделанным.
        lines.SelectMany(LinePackageIds).OrderBy(x => x, StringComparer.Ordinal).ToList()
            .Should().Equal(a.Id, b.Id);

        // Цепочка возвращена целиком: страница — то, что было до раннего пакета.
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(beforeChain);
        ReadStatus(a.Id).Item1.Should().Be(WikiPackage.StatusReverted);
        ReadStatus(b.Id).Item1.Should().Be(WikiPackage.StatusReverted);
    }

    // ─── 2. «already» для create-страницы, снесённой вне Хранителя: пакет не закрывается молча ─

    [Fact]
    public async Task Revert_СозданнаяСтраницаУдаленаВнеХранителя_НеЗакрываетПакетМолча()
    {
        // Мутация «already не называть» (вернуть пустой список в alreadyPages): Should().Equal
        // краснеет — страница осталась бы описанной только как возвращённая этим откатом.
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpCreate, NewPagePath, "Решение: перенести сборку на Linux"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);
        File.Exists(PageFull(NewPagePath)).Should().BeTrue();

        // Снесли мимо Хранителя: файл удалён, запись манифеста осталась — так это и выглядит на диске.
        File.Delete(PageFull(NewPagePath));
        WikiManifest.TryRead(Root, Section).Manifest.Should().ContainKey(NewPagePath,
            "состояние после сноса вне Хранителя: запись манифеста цела");

        var r = await _revert.RevertAsync(User, pkg.Id);

        // Пакет закрывается: его состояние «до применения» уже наступило, и отказ закрыть его
        // держал бы раздел в дрейфе навсегда (запись манифеста без файла — отказ Prepare).
        r.Reverted.Should().BeTrue(r.Reason);
        // Но названо, что этот вызов не трогал: страница уже была снята не им.
        r.AlreadyPages.Should().Equal([NewPagePath],
            "снесённая вне Хранителя create-страница — названный исход, а не молчание");
        // RestoredPages — эффект операции над разделом: страница из раздела снята (в неё входит и already).
        r.RestoredPages.Should().Contain(NewPagePath);

        // Состояние раздела приведено к «до пакета»: ни файла, ни записи манифеста.
        File.Exists(PageFull(NewPagePath)).Should().BeFalse();
        WikiManifest.TryRead(Root, Section).Manifest.Should().NotContainKey(NewPagePath);

        // Ровно одна строка op=revert и статус доведён: откат назван, а не повторён.
        RevertLines().Should().HaveCount(1);
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);

        // Дрейфа не осталось: следующий Prepare того же раздела проходит.
        var next = CreateAccepted(Op(WikiPackage.OpCreate, NewPagePath, "Решение: перенести сборку на Linux"));
        var again = await _sut.PrepareAsync(User, next, Ctx);
        again.Ready.Should().BeTrue(again.Reason);
    }

    // ─── 3. Битая запись состояния применения: тот же честный отказ, что её отсутствие ──────────

    [Fact]
    public async Task Revert_БитыйAppliedJson_CodeNoApplied()
    {
        // Мутация «битую запись читать как пустую» (вернуть new Dictionary() вместо null):
        // CodeNoApplied краснеет (отказ стал бы CodeNoApplied-без-причины или откатом по пустой сверке).
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);

        // Запись есть, но не читается: состояние применения неизвестно — сверки нет.
        WriteAppliedRecord(pkg.Id, """{"Общее/Сущность.md": """, DateTimeOffset.UtcNow);
        var before = SnapshotPages();

        var r = await _revert.RevertAsync(User, pkg.Id);

        r.Reverted.Should().BeFalse();
        r.Code.Should().Be(WikiPackageRevert.CodeNoApplied, "битая запись — тот же исход, что её отсутствие (fail-closed)");
        r.Reason.Should().Contain("состояния применения");
        r.AlreadyPages.Should().BeEmpty("отказа «already» нет: до фазы возврата не дошло");

        // Отказ — это отказ: на диске не тронуто ничего, журнал пуст, статус прежний.
        SnapshotPages().Should().Equal(before);
        RevertLines().Should().BeEmpty();
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusApplied);
    }

    // ─── 4. Записи состояния применения упавших пакетов чистятся тем же сроком ──────────────────

    [Fact]
    public void Purge_УдаляетAppliedУПакетаВApplyFailed()
    {
        // Мутация «не чистить keeper/applied» (убрать PurgeOldApplied из суммы): HaveCount/Be(1)
        // и BeFalse краснеют — записи упавших пакетов остались бы навсегда.
        var now = DateTimeOffset.UtcNow;

        var fallen = NewApplyFailedPackageInStore();
        var fallenRecord = AppliedRecordOf(fallen);
        WriteAppliedRecord(fallen, """{"Общее/Сущность.md":"0123456789012345678901234567890123456789012345678901234567890123"}""",
            now.AddDays(-91));

        // Тот же срок, что у прообразов: [now−90d, now] — свежая запись упавшего пакета не мусор.
        var young = NewApplyFailedPackageInStore();
        var youngRecord = AppliedRecordOf(young);
        WriteAppliedRecord(young, """{"Общее/Сущность.md":"0123456789012345678901234567890123456789012345678901234567890123"}""",
            now.AddDays(-90));

        var deleted = WikiPackageRevert.PurgeOldPreimages(Root, now);

        deleted.Should().Be(1);
        File.Exists(fallenRecord).Should().BeFalse("сторож старта вернул файлы по прообразам: этой записью пакет не владеет");
        File.Exists(youngRecord).Should().BeTrue("ровно 90 суток — не «старше»: тот же интервал хранения, что у прообразов");
    }

    // ─── 5. Запись откатываемого пакета purge не трогает: она — то, чем меряется его откат ──────

    [Fact]
    public async Task Purge_НеУдаляетAppliedОткатываемогоПакета_ОткатПроходит()
    {
        // Мутация «удалять запись независимо от статуса» (снять проверку StatusApplyFailed):
        // Be(0) и BeTrue краснеют, а следом краснеет откат — он получил бы CodeNoApplied
        // на записи, которую чистка снесла посреди его работы.
        var now = DateTimeOffset.UtcNow;
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);

        // Запись старше срока хранения, но пакет в «applied»: он откатывается сейчас или позже.
        File.SetLastWriteTimeUtc(AppliedRecordOf(pkg.Id), now.AddDays(-91).UtcDateTime);

        var deleted = WikiPackageRevert.PurgeOldPreimages(Root, now);

        deleted.Should().Be(0, "пакет в «applied» защищён по статусу: его запись — то, чем меряется откат");
        File.Exists(AppliedRecordOf(pkg.Id)).Should().BeTrue();

        var r = await _revert.RevertAsync(User, pkg.Id);
        r.Reverted.Should().BeTrue(r.Reason);
        r.RestoredPages.Should().Equal([PagePath]);
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(File.ReadAllBytes(PreimageOf(pkg.Id, PagePath)));
    }

    // ─── 6. Два одновременных отката одного пакета: одна строка, второй — «уже откатан» ─────────

    [Fact]
    public async Task ДваПараллельныхОткатаОдногоПакета_ОднаСтрокаRevert_ВторойОтказанПоСтатусу()
    {
        // Мутация «состояние пакета не перечитывается под блоком» (в RevertBody не звать TryRead,
        // брать чтение до блока): оба вызова проходят гейт по «applied», второй доходит до
        // Transition(«reverted»→«reverted») и даёт CodeClaim «статус не записан» — краснеет
        // Single(...).Code.Should().Be(CodeStatus).
        await WriteCardAndPage();
        var pkg = CreateAccepted(Op(WikiPackage.OpAppend, PagePath, "Контенер собран на второй машине"));
        var applied = await _sut.ApplyAsync(User, pkg, Ctx);
        applied.Applied.Should().BeTrue(applied.Reason);

        // Окно — держатель блокировки раздела тем же механизмом, что сама запись: оба вызова
        // доходят до входа в неё и стоят там, пока тест её не откроет.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = _keeper.WithSectionsLockedAsync(User, [Section], async () =>
        {
            started.SetResult();
            await gate.Task;
            return true;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var first = Task.Run(() => _revert.RevertAsync(User, pkg.Id));
        var second = Task.Run(() => _revert.RevertAsync(User, pkg.Id));
        first.IsCompleted.Should().BeFalse("откат ждёт блокировку раздела, как запись");
        second.IsCompleted.Should().BeFalse("оба вызова стоят на входе в блок — состояние решается под ним");

        gate.SetResult();
        var r1 = await first.WaitAsync(TimeSpan.FromSeconds(30));
        var r2 = await second.WaitAsync(TimeSpan.FromSeconds(30));
        await holding;

        RevertLines().Should().HaveCount(1, "ровно одна строка op=revert на один фактический откат");
        var results = new[] { r1, r2 };
        results.Count(r => r.Reverted).Should().Be(1);
        results.Single(r => !r.Reverted).Code.Should().Be(WikiPackageRevert.CodeStatus,
            "второй видит под блоком уже записанный «reverted» — это «уже откатано», а не второй откат");
        ReadStatus(pkg.Id).Item1.Should().Be(WikiPackage.StatusReverted);
        File.ReadAllBytes(PageFull(PagePath)).Should().Equal(File.ReadAllBytes(PreimageOf(pkg.Id, PagePath)));
    }
}
