using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Замер интерфейса ui_probe: разбор вывода скрипта (пороги — параметр вызова), форма итога по
/// пяти классам находок, отказ без стенда и «Стоп» очереди/процесса. Процесс подменяет
/// лаунчер-заглушка тестов сборки: печатает фикстуру скрипта или спит.
/// </summary>
public class UiProbeTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-uiprobe-" + Guid.NewGuid().ToString("N"));
    private readonly List<System.Diagnostics.Process> _processes = [];

    public UiProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        lock (_processes)
            foreach (var p in _processes)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                p.Dispose();
            }
        try { Directory.Delete(_root, recursive: true); } catch { /* занят */ }
        GC.SuppressFinalize(this);
    }

    private static string Output() => File.ReadAllText(TestRunServiceTests.Fixture("ui-probe-output.txt"));

    private static JsonObject Build(int min) =>
        UiProbeReport.Build(UiProbeReport.FindResultLine(Output().Split('\n'))!, "http://127.0.0.1:5559/", min);

    private static JsonObject Viewport(JsonObject report, int width) =>
        report["viewports"]!.AsArray().Select(v => v!.AsObject()).Single(v => v["width"]!.GetValue<int>() == width);

    [Fact]
    public void FindResultLine_БерётСтрокуСМаркеромСреди_Шума()
    {
        UiProbeReport.FindResultLine(["шум", "UIPROBE-RESULT {\"a\":1}", "  ещё шум"]).Should().Be("{\"a\":1}");
        UiProbeReport.FindResultLine(["только шум"]).Should().BeNull();
        UiProbeReport.FindResultLine([]).Should().BeNull("пустой вывод — результата нет");
    }

    [Fact]
    public void Порог_ЛюбаяСторонаМеньшеПорога_ГраницаВходитВДопустимое()
    {
        var small = Viewport(Build(40), 360)["smallTargets"]!.AsArray().Select(s => s!["selector"]!.GetValue<string>()).ToList();

        // button.a: ширина 39 < 40; a.c: высота 24 < 40; button.b — ровно 40×40 — не находка (интервал [40, ∞))
        small.Should().BeEquivalentTo("button.a", "a.c");
        Viewport(Build(40), 360)["smallTargets"]!.AsArray().First()!["screenWidth"]!.GetValue<int>().Should().Be(360);
    }

    [Fact]
    public void Порог_ЭтоПараметр_БольшийПорогЛовитБольше_МеньшийНеЛовитНичего()
    {
        Viewport(Build(41), 360)["counts"]!["smallTargets"]!.GetValue<int>().Should().Be(3, "button.b 40×40 при пороге 41 — находка");
        Viewport(Build(20), 360)["counts"]!["smallTargets"]!.GetValue<int>().Should().Be(0, "при пороге 20 все стороны не меньше");
    }

    [Fact]
    public void ПустойСписокЭлементов_НичегоНеНаходит()
    {
        var vp = Viewport(Build(40), 1280);

        vp["counts"]!["smallTargets"]!.GetValue<int>().Should().Be(0);
        vp["interactiveTotal"]!.GetValue<int>().Should().Be(0);
        vp["horizontalOverflow"]!["document"].Should().BeNull("1280 = 1280 — документ не шире экрана");
        vp["horizontalOverflow"]!["containers"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void ПятьКлассовНаходок_ПоСырымИзмерениям()
    {
        var report = Build(40);
        var vp = Viewport(report, 360);

        vp["horizontalOverflow"]!["document"]!["scrollWidth"]!.GetValue<int>().Should().Be(500);
        vp["horizontalOverflow"]!["containers"]!.AsArray().Should().ContainSingle()
            .Which!["selector"]!.GetValue<string>().Should().Be("div.wide", "scrollWidth == clientWidth — не переполнение");
        vp["clippedText"]!.AsArray().Should().ContainSingle().Which!["scrollWidth"]!.GetValue<int>().Should().Be(300);
        vp["consoleErrors"]!.AsArray().Should().ContainSingle();
        vp["failedRequests"]!.AsArray().Select(f => f!["url"]!.GetValue<string>()).Should().HaveCount(2);
        vp["failedRequests"]!.AsArray()[0]!["status"]!.GetValue<int>().Should().Be(503);
        vp["failedRequests"]!.AsArray()[1]!["failure"]!.GetValue<string>().Should().Be("net::ERR_ABORTED");
        vp["unnamedInteractive"]!.AsArray().Should().ContainSingle().Which!["selector"]!.GetValue<string>().Should().Be("a.c");

        report["totals"]!["horizontalOverflow"]!.GetValue<int>().Should().Be(2, "документ плюс один контейнер");
        report["totals"]!["smallTargets"]!.GetValue<int>().Should().Be(2);
        report["auth"]!.GetValue<string>().Should().Be("ok");
        report.ToJsonString().Should().NotContain("screenshot", "выход — только числа и селекторы");
    }

    [Fact]
    public void БитыйВыводСкрипта_ПонятнаяОшибкаФормата()
    {
        FluentActions.Invoking(() => UiProbeReport.Build("{не json", "http://x/", 40)).Should().Throw<FormatException>();
        FluentActions.Invoking(() => UiProbeReport.Build("{}", "http://x/", 40)).Should().Throw<FormatException>()
            .WithMessage("*viewports*");
    }

    private UiProbeService Service(DotnetBuildServiceTests.FakeLauncher launcher, BuildConcurrencyGate gate,
        Func<Uri, CancellationToken, Task<bool>>? alive = null) =>
        new(launcher, new TestRunsOptions(), new PhasePipeline(gate, TimeSpan.FromSeconds(5)), alive ?? ((_, _) => Task.FromResult(true)));

    private UiProbeRequest Request(int min = 40) => new(
        new Project { Id = "p1", OwnerId = "u1", Name = "Замер", RootPath = _root }, _root, _root, "sess-1",
        "http://127.0.0.1:5559", "/#/ui-kit", [360, 1280], min, null, "admin", "12345");

    [Fact]
    public async Task ЖивойСтенд_ИтогИзВыводаСкрипта_СпецСлотаИТекущегоОкруженияБезПароляВАргументах()
    {
        var gate = new BuildConcurrencyGate(1);
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt");

        var result = await Service(launcher, gate).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().BeNull();
        result.Failure.Should().BeNull();
        var json = JsonNode.Parse(result.Json!)!;
        json["minTargetPx"]!.GetValue<int>().Should().Be(40);
        json["url"]!.GetValue<string>().Should().Be("http://127.0.0.1:5559/#/ui-kit");
        var spec = launcher.Specs.Should().ContainSingle().Subject;
        spec.Heavy.Should().BeTrue("замер встаёт в общую очередь сборок");
        spec.Track.Should().BeTrue();
        spec.TurnId!.Length.Should().Be(12);
        spec.FileName.Should().Be("node");
        spec.Args[0].Should().Be("--eval");
        string.Join(' ', spec.Args).Should().NotContain("12345", "пароль — в окружении процесса, а не в командной строке");
        spec.Env!["UIPROBE_PASS"].Should().Be("12345");
        gate.Available.Should().Be(1, "слот освобождается по концу замера");
    }

    [Fact]
    public async Task НетСтенда_ОтказДоЗапускаПроцесса()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt");

        var result = await Service(launcher, new BuildConcurrencyGate(1), (_, _) => Task.FromResult(false))
            .RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("не отвечает").And.Contain("start_stand");
        result.Json.Should().BeNull();
        launcher.Specs.Should().BeEmpty("нет стенда — отказ, а не таймаут браузера");
    }

    [Fact]
    public async Task СкриптБезРезультата_ХвостВыводаВОтказе()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "dotnet-build-error.txt", exit: 1);

        var result = await Service(launcher, new BuildConcurrencyGate(1)).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Json.Should().BeNull();
        result.Failure.Should().Contain("не вернул результат").And.Contain("код выхода 1");
    }

    [Fact]
    public async Task Стоп_ГаситПроцессЗамераПоTurnId()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, null, sleep: true);
        using var cts = new CancellationTokenSource();

        var running = Service(launcher, new BuildConcurrencyGate(1)).RunAsync(Request(), null, cts.Token);
        var spec = await launcher.SleepStarted.Task.WaitAsync(Wait);
        await cts.CancelAsync();
        var result = await running.WaitAsync(Wait);

        (await launcher.Killed.Task.WaitAsync(Wait)).Should().Be(spec.TurnId);
        result.Cancelled.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task Стоп_ВОчереди_ПроцессНеЗапускался()
    {
        var gate = new BuildConcurrencyGate(1);
        using var busy = gate.TryAcquire(new ProcessSpec { FileName = "x", Args = [], Heavy = true });
        busy.Should().NotBeNull("слот занят чужой сборкой");
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt");
        var progress = new List<TestRunProgress>();
        using var cts = new CancellationTokenSource();

        var running = Service(launcher, gate).RunAsync(Request(), progress.Add, cts.Token);
        await Task.Run(async () => { while (progress.Count == 0) await Task.Delay(10); }).WaitAsync(Wait);
        await cts.CancelAsync();
        var result = await running.WaitAsync(Wait);

        progress.Should().ContainSingle().Which.Stage.Should().Be("queued");
        result.Cancelled.Should().BeTrue();
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task ВходНеПрошёл_ОтказВместоЗамераСтраницыЛогина()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-auth-failed.txt");

        var result = await Service(launcher, new BuildConcurrencyGate(1)).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Json.Should().BeNull("страница логина — не замер");
        result.Refusal.Should().Contain("войти").And.Contain("failed:401").And.Contain("user/password");
    }

    [Fact]
    public async Task СтендНедостижимИзСреды_ОтказДляКаждойСреды()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-unreachable.txt");

        var result = await Service(launcher, new BuildConcurrencyGate(1)).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Json.Should().BeNull();
        result.Refusal.Should().Contain("недостижим").And.Contain("ECONNREFUSED");
    }

    [Fact]
    public async Task ЗамерЕсть_ВИтогеФактическийUrlКаждойШирины()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt");

        var result = await Service(launcher, new BuildConcurrencyGate(1)).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        var viewports = JsonNode.Parse(result.Json!)!["viewports"]!.AsArray();
        viewports.Select(v => v!["finalUrl"]!.GetValue<string>()).Should().OnlyContain(u => u == "http://127.0.0.1:5559/#/ui-kit");
    }

    [Fact]
    public void РедиректНаВход_Отказ_ЛогинПоЗапросу_НеОтказ()
    {
        const string redirected = "{\"reach\":\"ok\",\"auth\":\"ok\",\"viewports\":[{\"width\":360,\"finalUrl\":\"http://h:1/#/login\"}]}";

        UiProbeReport.RefusalOf(redirected, "http://h:1/#/ui-kit", "http://h:1").Should().Contain("перенаправила на вход");
        UiProbeReport.RefusalOf(redirected, "http://h:1/#/login", "http://h:1").Should().BeNull("логин запрошен сам");
        UiProbeReport.RefusalOf(redirected.Replace("#/login", "#/ui-kit"), "http://h:1/#/ui-kit", "http://h:1").Should().BeNull();
    }

    [Fact]
    public void ОбрезкаСкрипта_ВыходНеМолчаЗанижаетСчётчики()
    {
        Build(40)["truncated"]!.GetValue<bool>().Should().BeFalse();
        Viewport(Build(40), 360)["truncated"]!.GetValue<bool>().Should().BeFalse();

        var cut = UiProbeReport.Build(
            "{\"auth\":\"ok\",\"viewports\":[{\"width\":360,\"interactive\":[],\"truncated\":[\"interactive\"]},{\"width\":1280,\"pageTruncated\":[\"console\"]}]}",
            "http://h/", 40);

        cut["truncated"]!.GetValue<bool>().Should().BeTrue();
        Viewport(cut, 360)["truncated"]!.GetValue<bool>().Should().BeTrue();
        Viewport(cut, 1280)["truncated"]!.GetValue<bool>().Should().BeTrue("страничные списки console/failed режутся в обработчиках страницы");
    }

    [Fact]
    public void ПотолокРазмера_СписокРежется_СчётчикиИОстаткиСохраняются()
    {
        var items = string.Join(',', Enumerable.Range(0, 300).Select(i => $"{{\"s\":\"button.item-{i}\",\"w\":10,\"h\":10,\"named\":true}}"));
        var raw = "{\"auth\":\"ok\",\"viewports\":[{\"width\":360,\"interactive\":[" + items + "]}]}";

        var whole = UiProbeReport.Build(raw, "http://h/", 40);
        var capped = UiProbeReport.Build(raw, "http://h/", 40, maxBytes: 4096);

        System.Text.Encoding.UTF8.GetByteCount(UiProbeReport.Serialize(whole)).Should().BeGreaterThan(4096);
        System.Text.Encoding.UTF8.GetByteCount(UiProbeReport.Serialize(capped)).Should().BeLessThanOrEqualTo(4096);
        var vp = Viewport(capped, 360);
        vp["counts"]!["smallTargets"]!.GetValue<int>().Should().Be(300, "счётчик — по всем находкам");
        var shown = vp["smallTargets"]!.AsArray().Count;
        shown.Should().BeLessThan(300);
        vp["omitted"]!["smallTargets"]!.GetValue<int>().Should().Be(300 - shown, "«ещё N»");
        Viewport(whole, 360).AsObject().ContainsKey("omitted").Should().BeFalse("влезло целиком — ничего не опущено");
    }

    [Fact]
    public void DI_ПодсистемаРегистрируетДвижокЗамераНаОбщемКонвейере()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<ILauncherFactory>(new DotnetBuildServiceTests.FakeLauncher(_processes, null));
        new TestRunsSubsystem().Register(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var probe = provider.GetService<UiProbeService>();

        probe.Should().NotBeNull("без регистрации тулсет получает null и ui_probe всегда отказывает");
        probe!.Options.Should().BeSameAs(provider.GetRequiredService<TestRunsOptions>());
    }

    // node_modules worktree — ссылка на зависимости основного дерева (scripts/worktree-setup.sh)
    private (string Worktree, string Primary) WorktreeWithLinkedModules(bool packageJsonBesideModules = true,
        string modulesName = "node_modules", bool sameRepository = true)
    {
        var primary = Path.Combine(_root, sameRepository ? "primary" : "stranger");
        var modules = Path.Combine(primary, "frontend", modulesName);
        Directory.CreateDirectory(Path.Combine(modules, "playwright"));
        File.WriteAllText(Path.Combine(modules, "playwright", "package.json"), "{}");
        if (packageJsonBesideModules) File.WriteAllText(Path.Combine(primary, "frontend", "package.json"), "{}");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(Path.Combine(worktree, "frontend"));
        // Раскладка git: основной checkout с .git/worktrees/wt и worktree с файлом .git (как у `git worktree add`)
        var common = Path.Combine(_root, "primary", ".git");
        Directory.CreateDirectory(Path.Combine(common, "worktrees", "wt"));
        File.WriteAllText(Path.Combine(common, "worktrees", "wt", "gitdir"), Path.Combine(worktree, ".git") + "\n");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(common, "worktrees", "wt") + "\n");
        return (worktree, modules);
    }

    [Fact]
    public void FindPackageDir_NodeModulesWorktreeСсылкойНаОсновноеДерево_Находит()
    {
        var (worktree, modules) = WorktreeWithLinkedModules();
        if (!TreeLinks.TryLink(Path.Combine(worktree, "frontend", "node_modules"), modules, directory: true)) return;

        var found = TestRunService.FindPackageDir(worktree, "frontend", UiProbeService.RequiredPackages, out var error);

        found.Should().NotBeNull(error);
        TestRunService.FindNodeBin(worktree, "frontend", TestRunKind.Playwright, out _)
            .Should().BeNull("строгий резолв бинаря тест-раннера в таком worktree отказывает — ради него и мягче");
    }

    [Fact]
    public void FindPackageDir_СсылкаНеНаNodeModulesИлиБезPackageJson_Отказ()
    {
        var (worktree, modules) = WorktreeWithLinkedModules(packageJsonBesideModules: false);
        if (!TreeLinks.TryLink(Path.Combine(worktree, "frontend", "node_modules"), modules, directory: true)) return;
        TestRunService.FindPackageDir(worktree, "frontend", UiProbeService.RequiredPackages, out var noPackageJson).Should().BeNull();
        noPackageJson.Should().Contain("за пределы проекта");

        // Каталог с чужим именем: даже с package.json рядом ссылка не годится
        Directory.Delete(Path.Combine(worktree, "frontend", "node_modules"));
        var foreign = Path.Combine(_root, "foreign");
        Directory.CreateDirectory(Path.Combine(foreign, "playwright"));
        File.WriteAllText(Path.Combine(foreign, "playwright", "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        TreeLinks.TryLink(Path.Combine(worktree, "frontend", "node_modules"), foreign, directory: true).Should().BeTrue();
        TestRunService.FindPackageDir(worktree, "frontend", UiProbeService.RequiredPackages, out _).Should().BeNull();
    }

    [Fact]
    public void FindPackageDir_ОбычныйИПоднятыйВКореньNodeModules_Находит_ПустойДеревоОтказ()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(Path.Combine(plain, "node_modules", "playwright"));
        File.WriteAllText(Path.Combine(plain, "node_modules", "playwright", "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(plain, "web", "app"));

        TestRunService.FindPackageDir(plain, "web/app", UiProbeService.RequiredPackages, out _).Should().Be(Path.Combine(plain, "node_modules"),
            "монорепа поднимает пакет в корень");
        TestRunService.FindPackageDir(_root, "wt", UiProbeService.RequiredPackages, out var error).Should().BeNull();
        error.Should().Contain("нет пакета playwright");
    }

    [Fact]
    public void FindPackageDir_NodeModulesСсылкойВЧужойРепозиторий_Отказ()
    {
        var (worktree, modules) = WorktreeWithLinkedModules(sameRepository: false);
        if (!TreeLinks.TryLink(Path.Combine(worktree, "frontend", "node_modules"), modules, directory: true)) return;

        TestRunService.FindPackageDir(worktree, "frontend", UiProbeService.RequiredPackages, out var error).Should().BeNull(
            "имя node_modules и package.json рядом не делают каталог из чужого дерева своим");
        error.Should().Contain("основном дереве того же репозитория");
    }

    [Fact]
    public void FindPackageDir_ПакетВОбычномNodeModulesСсылкойНаружу_Отказ()
    {
        var plain = Path.Combine(_root, "plain-link");
        var outside = Path.Combine(_root, "outside-pkg");
        Directory.CreateDirectory(Path.Combine(plain, "node_modules"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "package.json"), "{}");
        if (!TreeLinks.TryLink(Path.Combine(plain, "node_modules", "playwright"), outside, directory: true)) return;

        TestRunService.FindPackageDir(plain, ".", UiProbeService.RequiredPackages, out var error).Should().BeNull(
            "пакет-ссылка наружу дерева исполнил бы чужой код");
        error.Should().Contain("node_modules/playwright").And.Contain("за пределы проекта");
    }

    [Fact]
    public void Build_ОпущенныйПотолкомХвост_ПомечаетTruncated_НеУложившийсяИтог_OverLimit()
    {
        var items = string.Join(',', Enumerable.Range(0, 300).Select(i => $"{{\"s\":\"button.item-{i}\",\"w\":10,\"h\":10,\"named\":true}}"));
        var raw = "{\"auth\":\"ok\",\"viewports\":[{\"width\":360,\"interactive\":[" + items + "]}]}";

        var whole = UiProbeReport.Build(raw, "http://h/", 40);
        var capped = UiProbeReport.Build(raw, "http://h/", 40, maxBytes: 4096);
        var impossible = UiProbeReport.Build(raw, "http://h/", 40, maxBytes: 10);

        whole["truncated"]!.GetValue<bool>().Should().BeFalse("ничего не опущено и скрипт не обрезал");
        capped["truncated"]!.GetValue<bool>().Should().BeTrue("серверная обрезка по потолку — тоже нижняя оценка");
        Viewport(capped, 360)["truncated"]!.GetValue<bool>().Should().BeTrue();
        capped.ContainsKey("overLimit").Should().BeFalse();
        impossible["overLimit"]!.GetValue<bool>().Should().BeTrue("даже ступень «одни числа» не влезла в 10 байт");
        Viewport(impossible, 360)["counts"]!["smallTargets"]!.GetValue<int>().Should().Be(300);
    }

    [Fact]
    public void Размеры_ОкруглённыДо01_ПорогСтрогий()
    {
        const string raw = "{\"auth\":\"ok\",\"viewports\":[{\"width\":360,\"interactive\":[{\"s\":\"a.x\",\"w\":39.64,\"h\":44,\"named\":true},{\"s\":\"a.y\",\"w\":40,\"h\":40,\"named\":true}]}]}";

        var small = Viewport(UiProbeReport.Build(raw, "http://h/", 40), 360)["smallTargets"]!.AsArray();

        small.Should().ContainSingle().Which!["width"]!.GetValue<double>().Should().Be(39.6);
    }

    [Fact]
    public void ПустаяСтраница_ЧисленныйРазбор_ОтказНеНули()
    {
        const string empty = "{\"reach\":\"ok\",\"auth\":\"ok\",\"viewports\":[{\"width\":360,\"finalUrl\":\"http://h:1/#/notes\",\"pageInteractive\":0}]}";

        UiProbeReport.RefusalOf(empty, "http://h:1/#/notes", "http://h:1").Should().Contain("не отрисовалась");
        UiProbeReport.RefusalOf(empty.Replace("\"pageInteractive\":0", "\"pageInteractive\":3"), "http://h:1/#/notes", "http://h:1").Should().BeNull();
        UiProbeReport.RefusalOf(empty.Replace("\"pageInteractive\":0", "\"error\":\"timeout\",\"pageInteractive\":0"), "http://h:1/#/notes", "http://h:1")
            .Should().BeNull("страница с ошибкой навигации — отдельный исход, его покажет поле error");
    }

    [Fact]
    public async Task СтраницаБезИнтерактивныхЭлементов_ОтказВместоНулевогоЗамера()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-empty-page.txt");

        var result = await Service(launcher, new BuildConcurrencyGate(1)).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Json.Should().BeNull("все нули при status=200 — ложное «находок нет»");
        result.Refusal.Should().Contain("не отрисовалась или вход не выполнен");
    }

    private static int Logins(DotnetBuildServiceTests.FakeLauncher launcher) =>
        launcher.Specs.Count(s => !s.Env!.ContainsKey("UIPROBE_TOKEN"));

    [Fact]
    public async Task ДвенадцатьВызововПодряд_ОдинВход_ТокенНеВИтогеИНеВАргументах()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt");
        var service = Service(launcher, new BuildConcurrencyGate(1));
        var results = new List<UiProbeResult>();

        for (var i = 0; i < 12; i++)
            results.Add(await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait));

        results.Should().OnlyContain(r => r.Json != null && r.Refusal == null);
        Logins(launcher).Should().Be(1, "лимит входа 10/мин: остальные 11 вызовов идут по кэшированному токену");
        launcher.Specs.Where(s => s.Env!.ContainsKey("UIPROBE_TOKEN")).Should().OnlyContain(s => s.Env!["UIPROBE_TOKEN"] == "tok-fresh");
        launcher.Specs.Select(s => string.Join(' ', s.Args)).Should().NotContain(a => a.Contains("tok-fresh"));
        results.Should().OnlyContain(r => !r.Json!.Contains("tok-fresh"), "токен в итог для модели не попадает");
    }

    [Fact]
    public async Task КэшТокена_НеПересекаетВладельцевOriginИПользователей()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt");
        var service = Service(launcher, new BuildConcurrencyGate(1));
        var mine = Request();
        var other = mine with { Project = new Project { Id = "p2", OwnerId = "u2", Name = "Чужой", RootPath = _root } };

        await service.RunAsync(mine, null, CancellationToken.None).WaitAsync(Wait);
        await service.RunAsync(other, null, CancellationToken.None).WaitAsync(Wait);
        await service.RunAsync(mine with { Origin = "http://127.0.0.1:5560" }, null, CancellationToken.None).WaitAsync(Wait);
        await service.RunAsync(mine with { User = "other" }, null, CancellationToken.None).WaitAsync(Wait);
        await service.RunAsync(mine with { Password = "changed" }, null, CancellationToken.None).WaitAsync(Wait);

        Logins(launcher).Should().Be(5, "владелец, origin, пользователь и пароль — части ключа: токен чужого входа не подставляется");
    }

    [Fact]
    public async Task КэшТокена_ИстёкТокен_ВходЗаново()
    {
        var launcher = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt");
        var time = new ManualTime();
        var service = new UiProbeService(launcher, new TestRunsOptions(),
            new PhasePipeline(new BuildConcurrencyGate(1), TimeSpan.FromSeconds(5)), (_, _) => Task.FromResult(true), time);

        await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);
        time.Advance(TimeSpan.FromMinutes(5));
        await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);
        time.Advance(TimeSpan.FromMinutes(6));
        await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        Logins(launcher).Should().Be(2, "токену без exp — 10 минут жизни: через 5 мин кэш жив, через 11 — нет");
    }

    [Fact]
    public async Task ТокенОтклонён401_ОдинПовторныйВход()
    {
        var factory = new SwitchFactory(new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-output.txt"));
        var service = new UiProbeService(factory, new TestRunsOptions(),
            new PhasePipeline(new BuildConcurrencyGate(1), TimeSpan.FromSeconds(5)), (_, _) => Task.FromResult(true));
        await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        var rejecting = new DotnetBuildServiceTests.FakeLauncher(_processes, "ui-probe-token-rejected.txt");
        factory.Current = rejecting;
        var result = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        rejecting.Specs.Should().HaveCount(2, "кэшированный токен получил 401 — ровно один повторный вход, не цикл");
        rejecting.Specs.First().Env!.Should().ContainKey("UIPROBE_TOKEN");
        rejecting.Specs.Last().Env!.Should().NotContainKey("UIPROBE_TOKEN");
        result.Json.Should().NotBeNull();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class SwitchFactory(DotnetBuildServiceTests.FakeLauncher current) : ILauncherFactory
    {
        public DotnetBuildServiceTests.FakeLauncher Current { get; set; } = current;
        public IProcessLauncher Local => Current;
        public IProcessLauncher ForOwner(string? ownerId) => Current;
        public IProcessLauncher ForProject(Project project) => Current;
    }
}
