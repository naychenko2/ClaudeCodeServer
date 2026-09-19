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

    private static IsolationOptions On(string? memHigh = null, string? memMax = null) => new()
    {
        Enabled = true,
        Slice = "ccs-agents.slice",
        MemoryHigh = memHigh,
        MemoryMax = memMax,
    };

    private static (System.Diagnostics.ProcessStartInfo psi, string? reason) Build(
        ProcessSpec spec, IsolationOptions options, bool windows = false, string? systemdRun = SystemdRun,
        SystemdRunProbeResult? probeResult = null) =>
        LocalProcessRunner.BuildStartInfo(spec, options, windows, systemdRun, probeResult);

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
    public void MsbuildПеременные_ДобавляютсяИНеПеребиваютЯвные()
    {
        using var _ = Bus(present: true);

        var (psi, _) = Build(
            Spec(env: new Dictionary<string, string> { ["UseSharedCompilation"] = "true" }),
            On());

        psi.Environment["MSBUILDDISABLENODEREUSE"].Should().Be("1");
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"].Should().Be("0");
        psi.Environment["UseSharedCompilation"].Should().Be("true", "явный spec.Env сильнее дефолта изоляции");
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
                // Даём systemd-run успеть сделать exec: к этому моменту живое имя уже «sleep»
                await Task.Delay(TimeSpan.FromSeconds(2));
                ProcessRegistry.PruneDead();
                ProcessRegistry.IsTracked(pid).Should().BeTrue(
                    "реестр не должен вычёркивать живой обёрнутый процесс (Matches — по времени старта, не имени)");
                System.Diagnostics.Process.GetProcessById(pid).ProcessName
                    .Should().Be("sleep", "к моменту PruneDead обёртка уже exec-нулась");
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
}
