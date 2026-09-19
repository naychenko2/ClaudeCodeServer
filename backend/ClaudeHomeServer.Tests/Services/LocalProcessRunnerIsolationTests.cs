using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Изоляция процессов local-среды по памяти: обёртка systemd-run уводит процесс в scope вне
// cgroup прода (инцидент 2026-09-19 — systemd-oomd дважды убил ccs.service целиком).
// Решение «оборачивать или нет» — чистая функция от параметров (ОС, путь к systemd-run),
// поэтому юниты идут на любой ОС. Живой запуск — только Linux с user-шиной, иначе пропуск.
// Коллекция процесс-глобального состояния: тесты правят XDG_RUNTIME_DIR/DBUS_SESSION_BUS_ADDRESS
// и IsolationOptions.Instance.
[Collection(TestCollections.ProcessGlobalState)]
public class LocalProcessRunnerIsolationTests
{
    private const string SystemdRun = "/usr/bin/systemd-run";
    private const string Unit = "ccs-run-test.scope";

    private static ProcessSpec Spec(
        IReadOnlyList<string>? args = null,
        IReadOnlyList<string>? clear = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? raw = null) => new()
        {
            FileName = "dummy",
            Args = args ?? [],
            ClearEnv = clear,
            Env = env,
            RawArguments = raw,
        };

    private static IsolationOptions On(string? memHigh = null, string? memMax = null, bool reuse = true) => new()
    {
        Enabled = true,
        BuildNodeReuse = reuse,
        Slice = "ccs-agents.slice",
        MemoryHigh = memHigh,
        MemoryMax = memMax,
    };

    private static (System.Diagnostics.ProcessStartInfo psi, string? reason) Build(
        ProcessSpec spec, IsolationOptions options, bool windows = false, string? systemdRun = SystemdRun,
        SystemdRunProbeResult? probeResult = null) =>
        LocalProcessRunner.BuildStartInfo(spec, options, windows, systemdRun, probeResult, Unit);

    // Юнитам user-шина нужна «на бумаге»: обёртка проверяет только наличие переменных
    private static IDisposable Bus(bool present)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var dbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", present ? "/run/user/1000" : null);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", present ? "unix:path=/run/user/1000/bus" : null);
        return new Restore(() =>
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", xdg);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", dbus);
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // psi.Environment — КОПИЯ окружения хоста, поэтому «переменной там нет» проверяемо только
    // при её отсутствии снаружи. А снаружи она как раз бывает: изолированным процессам её
    // ставит этот самый механизм, и тестраннер, запущенный под изоляцией, её наследует —
    // без снятия ассерт проверял бы среду прогона, а не код.
    private static IDisposable NoAmbient(string name)
    {
        var prev = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, null);
        return new Restore(() => Environment.SetEnvironmentVariable(name, prev));
    }

    [Fact]
    public void Включена_НеWindows_ЗапускИдётЧерезSystemdRun()
    {
        using var _ = Bus(present: true);

        var (psi, reason) = Build(Spec(args: ["--print", "a b"]), On(memHigh: "12G", memMax: "16G"));

        reason.Should().BeNull();
        psi.FileName.Should().Be(SystemdRun);
        psi.ArgumentList.Should().Equal(
            "--user", "--scope", "--quiet", "--collect",
            $"--unit={Unit}",
            "--slice=ccs-agents.slice",
            "--property=MemoryHigh=12G",
            "--property=MemoryMax=16G",
            "--",
            "dummy", "--print", "a b");
    }

    [Fact]
    public void ПределыПамятиНеЗаданы_СвойстваНеСтавятся()
    {
        using var _ = Bus(present: true);

        var (psi, _) = Build(Spec(), On());

        psi.ArgumentList.Should().NotContain(a => a.StartsWith("--property="));
        psi.ArgumentList.Should().ContainInOrder("--slice=ccs-agents.slice", "--", "dummy");
    }

    [Fact]
    public void Выключена_ЗапускКакРаньше()
    {
        using var _ = Bus(present: true);
        using var noAmbient = NoAmbient("MSBUILDDISABLENODEREUSE");

        var (psi, reason) = Build(Spec(args: ["--print"]), new IsolationOptions { Enabled = false });

        reason.Should().Be("no-isolation");
        psi.FileName.Should().Be("dummy");
        psi.ArgumentList.Should().Equal("--print");
        psi.Environment.Should().NotContainKey("MSBUILDDISABLENODEREUSE");
        psi.Environment.Should().NotContainKey("MSBUILDNODEHANDSHAKESALT");
    }

    [Fact]
    public void Windows_ЗапускКакРаньше()
    {
        using var _ = Bus(present: true);

        var (psi, reason) = Build(Spec(args: ["--print"]), On(), windows: true);

        reason.Should().Be("no-isolation");
        psi.FileName.Should().Be("dummy");
        psi.ArgumentList.Should().Equal("--print");
    }

    [Fact]
    public void SystemdRunНеНайден_FailOpen()
    {
        using var _ = Bus(present: true);

        var (psi, reason) = Build(Spec(args: ["--print"]), On(), systemdRun: null);

        reason.Should().Contain("systemd-run");
        psi.FileName.Should().Be("dummy");
        psi.ArgumentList.Should().Equal("--print");
    }

    [Fact]
    public void НетUserШины_FailOpen()
    {
        using var _ = Bus(present: false);

        var (psi, reason) = Build(Spec(args: ["--print"]), On());

        reason.Should().Contain("user-шины");
        psi.FileName.Should().Be("dummy");
        psi.ArgumentList.Should().Equal("--print");
    }

    [Fact]
    public void RawArguments_FailOpen()
    {
        using var _ = Bus(present: true);

        var (psi, reason) = Build(Spec(raw: "/s /c \"echo hi\""), On());

        reason.Should().Contain("RawArguments");
        psi.FileName.Should().Be("dummy");
        psi.Arguments.Should().Be("/s /c \"echo hi\"");
        psi.ArgumentList.Should().BeEmpty();
    }

    // Проба systemd-run: systemd-run в PATH и user-шина на месте, но обёртка разрешена —
    // её применяем (проба прошла → reason null, FileName = systemd-run).
    [Fact]
    public void ПробаOk_ОбёрткаПрименена()
    {
        using var _ = Bus(present: true);

        var (psi, reason) = Build(Spec(args: ["--print", "a"]), On(),
            probeResult: new SystemdRunProbeResult(true));

        reason.Should().BeNull();
        psi.FileName.Should().Be(SystemdRun);
        psi.ArgumentList.Should().ContainInOrder("--", "dummy", "--print", "a");
    }

    // Проба systemd-run не прошла (ненулевой код/таймаут/исключение) — fail-open:
    // обёртка НЕ применяется, процесс запускается напрямую, и есть причина для warning.
    [Fact]
    public void ПробаУпала_ОбёрткиНет_ЕстьПричина()
    {
        using var _ = Bus(present: true);

        const string failReason = "проба systemd-run завершилась с кодом 1";
        var (psi, reason) = Build(Spec(args: ["--print"]), On(),
            probeResult: new SystemdRunProbeResult(false, failReason));

        reason.Should().Be(failReason);
        psi.FileName.Should().Be("dummy");
        psi.ArgumentList.Should().Equal("--print");
    }

    // Проба выполняется ОДИН раз за процесс (лениво, результат кэшируется, потокобезопасно):
    // N вызовов ProbeOnce при N запусках — ровно 1 обращение к шву, остальные — из кэша.
    [Fact]
    public void ПробаOnce_ОдинВызовНаНесколькоЗапусков()
    {
        var prevProbe = LocalProcessRunner.Probe;
        var calls = 0;
        LocalProcessRunner.Probe = (path, args, cmd, ms) =>
        {
            calls++;
            return new SystemdRunProbeResult(true);
        };
        try
        {
            LocalProcessRunner.ResetProbeForTests();
            var options = On();
            for (var i = 0; i < 5; i++)
            {
                var r = LocalProcessRunner.ProbeOnce(options, SystemdRun);
                r!.Ok.Should().BeTrue("кэшированный результат пробы стабилен");
            }
            calls.Should().Be(1, "проба повторяется только при пустом кэше");
        }
        finally
        {
            LocalProcessRunner.Probe = prevProbe;
            LocalProcessRunner.ResetProbeForTests();
        }
    }

    // Проба не нужна, когда обёртки всё равно не будет: изоляция выключена или systemd-run
    // не найден — ProbeOnce не ходит в шов (null), чтобы тесты не зависели от настоящего systemd.
    [Fact]
    public void ПробаOnce_НеНужна_КогдаОбёрткиВсёРавноНеБудет()
    {
        var prevProbe = LocalProcessRunner.Probe;
        var calls = 0;
        LocalProcessRunner.Probe = (path, args, cmd, ms) => { calls++; return new SystemdRunProbeResult(true); };
        try
        {
            LocalProcessRunner.ResetProbeForTests();
            LocalProcessRunner.ProbeOnce(new IsolationOptions { Enabled = false }, SystemdRun).Should().BeNull();
            LocalProcessRunner.ProbeOnce(On(), systemdRunPath: null).Should().BeNull();
            calls.Should().Be(0, "проба не выполняется, если обёртки всё равно не будет");
        }
        finally
        {
            LocalProcessRunner.Probe = prevProbe;
            LocalProcessRunner.ResetProbeForTests();
        }
    }

    [Fact]
    public void РеюзВыключен_ЗапретыMsbuildДобавляютсяИНеПеребиваютЯвные()
    {
        using var _ = Bus(present: true);

        var (psi, _) = Build(
            Spec(env: new Dictionary<string, string> { ["UseSharedCompilation"] = "true" }),
            On(reuse: false));

        psi.Environment["MSBUILDDISABLENODEREUSE"].Should().Be("1");
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"].Should().Be("0");
        psi.Environment["UseSharedCompilation"].Should().Be("true", "явный spec.Env сильнее дефолта изоляции");
        psi.Environment.Should().NotContainKey("MSBUILDNODEHANDSHAKESALT");
    }

    [Fact]
    public void Реюз_УзлыСборкиСолятсяИменемScope()
    {
        using var _ = Bus(present: true);

        var (psi, _) = Build(Spec(), On());

        // Соль рукопожатия и труба компилятора — имя юнита: узлы приватны scope, и остановка
        // чужого scope не роняет идущую сборку соседнего хода
        psi.Environment["MSBUILDNODEHANDSHAKESALT"].Should().Be(Unit);
        psi.Environment["SharedCompilationId"].Should().Be(Unit);
        psi.Environment.Should().NotContainKey("MSBUILDDISABLENODEREUSE");
        psi.Environment.Should().NotContainKey("DOTNET_CLI_USE_MSBUILD_SERVER");
        psi.Environment.Should().NotContainKey("UseSharedCompilation");
    }

    [Fact]
    public void Реюз_УнаследованныеЗапретыСнимаются_ЯвныеОстаются()
    {
        using var _ = Bus(present: true);
        // Бэкенд, запущенный из хода агента, наследует запреты прежнего режима
        var prevReuse = Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE");
        var prevShared = Environment.GetEnvironmentVariable("UseSharedCompilation");
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("UseSharedCompilation", "false");
        try
        {
            var (psi, _) = Build(
                Spec(env: new Dictionary<string, string> { ["UseSharedCompilation"] = "false" }),
                On());

            psi.Environment.Should().NotContainKey("MSBUILDDISABLENODEREUSE", "унаследованный запрет молча выключил бы реюз");
            psi.Environment["UseSharedCompilation"].Should().Be("false", "явный spec.Env сильнее режима");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", prevReuse);
            Environment.SetEnvironmentVariable("UseSharedCompilation", prevShared);
        }
    }

    [Fact]
    public void ИмяScope_УникальноНаЗапуск()
    {
        using var _ = Bus(present: true);

        var (a, _) = LocalProcessRunner.BuildStartInfo(Spec(), On(), false, SystemdRun);
        var (b, _) = LocalProcessRunner.BuildStartInfo(Spec(), On(), false, SystemdRun);

        var unitA = a.ArgumentList.Single(x => x.StartsWith("--unit="));
        var unitB = b.ArgumentList.Single(x => x.StartsWith("--unit="));
        unitA.Should().MatchRegex("^--unit=ccs-run-[0-9a-f]{32}\\.scope$");
        unitA.Should().NotBe(unitB);
        unitA.Length.Should().Be(("--unit=" + LocalProcessRunner.ScopeUnitPlaceholder).Length,
            "оценка длины командной строки считает имя заглушкой той же длины");
    }

    [Fact]
    public void ClearEnvИEnv_ПрименяютсяКОкружениюОбёртки()
    {
        using var _ = Bus(present: true);
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://чужой");
        try
        {
            var (psi, reason) = Build(
                Spec(clear: ["ANTHROPIC_BASE_URL"],
                     env: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "/профиль" }),
                On());

            reason.Should().BeNull();
            psi.Environment.Should().NotContainKey("ANTHROPIC_BASE_URL");
            psi.Environment["CLAUDE_CONFIG_DIR"].Should().Be("/профиль");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null);
        }
    }

    [Fact]
    public void Оценка_РастётРовноНаОбвязкуSystemdRun()
    {
        var spec = Spec(args: ["--print", "длинный промпт"]);
        var options = On(memHigh: "12G", memMax: "16G");

        var plain = LocalProcessRunner.EstimateCommandLineLength(spec, options, systemdRun: null);
        var wrapped = LocalProcessRunner.EstimateCommandLineLength(spec, options, SystemdRun);

        // FileName становится systemd-run, exe уезжает в аргументы, добавляются флаги обёртки
        var expectedDelta = SystemdRun.Length
            + LocalProcessRunner.WrapperArgs(options).Sum(CmdlineEstimate.ArgCost)
            + CmdlineEstimate.ArgCost("dummy") - "dummy".Length;
        (wrapped - plain).Should().Be(expectedDelta);
        wrapped.Should().BeGreaterThan(plain);
    }

    [Fact]
    public void Оценка_НеМеньшеРеальнойКоманднойСтроки()
    {
        // Верхняя граница: сравниваем с реальной командной строкой ПОСЛЕ экранирования .NET
        // (PasteArguments): FileName — с кавычками, если в нём есть пробел/таб/" (а при
        // экранировании \ перед " удваиваются — и в конце аргумента тоже); каждый аргумент
        // — с кавычками, если содержит пробел/таб/", и с \→\\" перед каждой ".
        // Нижний счёт (без кавычек и удвоений) тест проходил тривиально.
        using var _ = Bus(present: true);
        var spec = Spec(args: ["--print", "с пробелом", "кавычка\"внутри", "\\x\\"]);
        var options = On(memHigh: "12G", memMax: "16G");

        var (psi, _) = Build(spec, options);
        var actual = Escape(psi.FileName).Length;
        foreach (var a in psi.ArgumentList) actual += Escape(a).Length;

        LocalProcessRunner.EstimateCommandLineLength(spec, options, SystemdRun)
            .Should().BeGreaterThanOrEqualTo(actual);

        // Имитация PasteArguments: " → \", затем \ → \\ (два прохода, порядок удвоений),
        // обрамляющие кавычки, если в аргументе есть пробел/таб/".
        static string Escape(string s)
        {
            if (!s.Any(c => c is ' ' or '\t' or '"')) return s;
            var sb = new System.Text.StringBuilder();
            foreach (var c in s) sb.Append(c == '"' ? "\\\"" : c);
            foreach (var c in s) if (c == '\\') sb.Append('\\');
            return $"\"{sb}\"";
        }
    }

    [Fact]
    public void Оценка_ВыключенаИзоляция_БезОбвязки()
    {
        var spec = Spec(args: ["--print"]);

        LocalProcessRunner.EstimateCommandLineLength(spec, new IsolationOptions { Enabled = false }, SystemdRun)
            .Should().Be("dummy".Length + CmdlineEstimate.ArgCost("--print"));
    }

    [Fact]
    public void FindSystemdRun_ИщетПоPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "lpr_iso_" + Guid.NewGuid().ToString("N")[..8]);
        var empty = Path.Combine(root, "empty");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(bin);
        try
        {
            var expected = Path.Combine(bin, "systemd-run");
            File.WriteAllText(expected, "");

            LocalProcessRunner.FindSystemdRun(empty + Path.PathSeparator + bin).Should().Be(expected);
            LocalProcessRunner.FindSystemdRun(empty).Should().BeNull();
            LocalProcessRunner.FindSystemdRun(null).Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ResolveSystemdRunPath_ЯвныйПутьИзКонфигаПобеждает()
    {
        LocalProcessRunner.ResolveSystemdRunPath(new IsolationOptions { SystemdRunPath = "/opt/systemd-run" })
            .Should().Be("/opt/systemd-run");
    }

    // Реестр процессов: при Track=true Register запоминает имя ИМЕННО того процесса,
    // что .NET видит после Start — под обёрткой это «systemd-run» (кэш .NET до её exec).
    // Matches теперь сверяет время старта (exec не меняет start_time), и запись переживает
    // смену имени на «sleep»: PruneDead её не вычёркивает, PID-файл полон, KillAll
    // graceful-shutdown доходит до процесса.
    // Живой реестр правит IsolationOptions.Instance и XDG/DBUS-переменные — класс уже
    // сериализован коллекцией ProcessGlobalState, здесь только свои переменные.
    [Fact]
    public async Task Start_Linux_РеестрУдерживаетОбёрнутыйПроцесс()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return;
        if (LocalProcessRunner.FindSystemdRun(Environment.GetEnvironmentVariable("PATH")) is null) return;

        var prev = IsolationOptions.Instance;
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, Slice = "ccs-isotest.slice", MemoryMax = "256M" };
        var process = new System.Diagnostics.Process { StartInfo = new()
        {
            FileName = "sh",
            ArgumentList = { "-c", "exec sleep 30" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }};
        try
        {
            process.Start();
            var pid = process.Id;
            // Запись с именем обёртки: в реальном Register это «systemd-run» (кэш .NET до exec)
            ProcessRegistry.TrackForTests(new ProcessRegistry.TrackedProcess(pid, "systemd-run", process.StartTime));
            try
            {
                // Ждём exec опросом, а не фиксированной паузой: под нагрузкой он запаздывает
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (LiveName(pid) != "sleep" && DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                LiveName(pid).Should().Be("sleep", "к моменту PruneDead обёртка уже exec-нулась");
                ProcessRegistry.PruneDead();
                ProcessRegistry.IsTracked(pid).Should().BeTrue(
                    "реестр не должен вычёркивать живой обёрнутый процесс (Matches — по времени старта, не имени)");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                ProcessRegistry.Unregister(process);
                process.Dispose();
            }
        }
        finally
        {
            IsolationOptions.Instance = prev;
        }

        static string? LiveName(int pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return p.ProcessName;
            }
            catch (ArgumentException) { return null; }
        }
    }

    // Живой запуск: процесс в заданном slice, stdout читается, Kill его завершает.
    // Только Linux с user-шиной и systemd-run — иначе пропуск, а не падение (CI, контейнер).
    [Fact]
    public async Task Start_Linux_ПроцессВSliceВнеСервиса()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return;
        if (LocalProcessRunner.FindSystemdRun(Environment.GetEnvironmentVariable("PATH")) is null) return;

        const string slice = "ccs-isotest.slice";
        var prev = IsolationOptions.Instance;
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, Slice = slice, MemoryMax = "256M" };
        try
        {
            var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = "sh",
                Args = ["-c", "echo изоляция-ок; exec sleep 30"],
                RedirectStdin = false,
                Track = false,
            });
            try
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                line.Should().Be("изоляция-ок");

                // Строка вывода пришла от команды — значит, systemd-run уже переложил процесс в scope
                var cgroup = await File.ReadAllTextAsync($"/proc/{process.Id}/cgroup");
                cgroup.Should().Contain(slice).And.Contain(".scope");

                LocalProcessRunner.Instance.Kill(process);
                await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
                process.HasExited.Should().BeTrue("Kill обязан завершить процесс в scope");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
        finally
        {
            IsolationOptions.Instance = prev;
        }
    }
    // Гашение scope по выходу процесса — через шов StopScope и поддельный systemd-run
    // (скрипт отбрасывает флаги обёртки до «--» и exec-ает команду): реальный systemd не
    // нужен, поэтому тест идёт на любом Linux, включая CI. Проверяем: стоп ровно один раз
    // и ровно с тем юнитом, что ушёл обёртке в --unit.
    [Fact]
    public async Task Start_ПоВыходуПроцесса_ScopeГаситсяРовноОдинРаз()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), "lpr_stop_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var fake = Path.Combine(dir, "systemd-run");
        // Файл аргументов — по PID: exec сохраняет PID, так что это PID нашего процесса. Раннер
        // глобальный, и процессы параллельных тестов других коллекций тоже проходят через фейк
        File.WriteAllText(fake, $$"""
            #!/bin/sh
            printf '%s\n' "$@" > '{{dir}}/args-'$$'.txt'
            while [ "$1" != "--" ]; do shift; done
            shift
            exec "$@"
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var _ = Bus(present: true);
        var prevOptions = IsolationOptions.Instance;
        var prevStop = LocalProcessRunner.StopScope;
        var calls = new List<(string Systemctl, string Unit)>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? ourUnit = null;
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, SystemdRunPath = fake };
        LocalProcessRunner.StopScope = (systemctl, unit) =>
        {
            lock (calls)
            {
                calls.Add((systemctl, unit));
                if (unit == ourUnit) stopped.TrySetResult();
            }
        };
        try
        {
            using var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = "sh",
                Args = ["-c", "echo готово"],
                RedirectStdin = false,
                Track = false,
            });
            (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))
                .Should().Be("готово");
            var unitArg = File.ReadAllLines(Path.Combine(dir, $"args-{process.Id}.txt"))
                .Single(a => a.StartsWith("--unit="));
            lock (calls)
            {
                ourUnit = unitArg["--unit=".Length..];
                if (calls.Any(c => c.Unit == ourUnit)) stopped.TrySetResult();
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            (await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(10))))
                .Should().Be(stopped.Task, "по выходу обёрнутого процесса scope обязан гаситься");
            // Второго вызова быть не должно: даём фону шанс ошибиться
            await Task.Delay(300);

            lock (calls)
            {
                var ours = calls.Where(c => c.Unit == ourUnit).ToList();
                ours.Should().ContainSingle("scope гасится ровно один раз и ровно тем именем, что ушло в --unit");
                ours[0].Systemctl.Should().Be("systemctl", "рядом с поддельным systemd-run systemctl нет — берётся из PATH");
            }
        }
        finally
        {
            LocalProcessRunner.StopScope = prevStop;
            IsolationOptions.Instance = prevOptions;
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Start_ИзоляцияВыключена_ScopeНеГасится()
    {
        if (OperatingSystem.IsWindows()) return;

        var prevOptions = IsolationOptions.Instance;
        var prevStop = LocalProcessRunner.StopScope;
        var calls = 0;
        IsolationOptions.Instance = new IsolationOptions { Enabled = false };
        LocalProcessRunner.StopScope = (_, _) => Interlocked.Increment(ref calls);
        try
        {
            using var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = "sh",
                Args = ["-c", "true"],
                RedirectStdin = false,
                Track = false,
            });
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            calls.Should().Be(0);
        }
        finally
        {
            LocalProcessRunner.StopScope = prevStop;
            IsolationOptions.Instance = prevOptions;
        }
    }

    // Живой systemd: процесс оставляет в своём scope фоновый хвост (как узлы MSBuild после
    // сборки) и выходит. Остановка scope обязана добить хвост и выгрузить юнит.
    // Только Linux с user-шиной и systemd-run — иначе пропуск.
    [Fact]
    public async Task Start_Linux_ХвостВScopeГибнетПослеВыхода()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return;
        if (LocalProcessRunner.FindSystemdRun(Environment.GetEnvironmentVariable("PATH")) is null) return;

        const string slice = "ccs-isotest.slice";
        var prev = IsolationOptions.Instance;
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, Slice = slice, MemoryMax = "256M" };
        try
        {
            using var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = "sh",
                // Хвост отвязан от наших труб, иначе чтение stdout ждало бы и его
                Args = ["-c", "sleep 300 </dev/null >/dev/null 2>&1 & echo $!"],
                RedirectStdin = false,
                Track = false,
            });
            var tailPid = int.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            var cgroup = await File.ReadAllTextAsync($"/proc/{tailPid}/cgroup");
            var unit = cgroup.Trim().Split('/').Last();
            unit.Should().StartWith("ccs-run-").And.EndWith(".scope");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            // Остановка асинхронная (stop --no-block): у каждого ожидания свой потолок, под
            // нагрузкой SIGTERM и выгрузка юнита занимают секунды
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Directory.Exists($"/proc/{tailPid}") && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Directory.Exists($"/proc/{tailPid}").Should().BeFalse("остановка scope добивает хвост");

            // Хвост умер раньше, чем systemd обработал опустевшую cgroup: юнит ещё бывает
            // в «deactivating» — ждём терминального состояния, а не снимаем мгновенный кадр
            deadline = DateTime.UtcNow.AddSeconds(30);
            var state = await UnitStateAsync(unit);
            while (!IsTerminal(state) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                state = await UnitStateAsync(unit);
            }
            IsTerminal(state).Should().BeTrue(
                $"юнит остановлен и выгружен (--collect), в slice от процесса ничего не осталось; состояние: {state}");
        }
        finally
        {
            IsolationOptions.Instance = prev;
        }

        // Выгружен (not-found) либо уже остановлен — --collect соберёт и failed
        static bool IsTerminal(string state) =>
            state.Contains("LoadState=not-found")
            || state.Contains("ActiveState=inactive")
            || state.Contains("ActiveState=failed");

        static async Task<string> UnitStateAsync(string unit)
        {
            using var show = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("systemctl")
            {
                ArgumentList = { "--user", "show", unit, "-p", "LoadState,ActiveState,SubState" },
                RedirectStandardOutput = true,
            })!;
            var text = await show.StandardOutput.ReadToEndAsync();
            await show.WaitForExitAsync();
            return text.Replace('\n', ' ').Trim();
        }
    }
}
