using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Блокер волны 3 фикса dc641949: Estimate внутри ApplyBudget считал только cli+args,
// а реальный docker exec добавлял ~401 символ обвязки (`docker exec -i -w … -e K=V …
// cc-sandbox /app/run-turn.sh turnId claude`). Глеб замерил на живом процессе
// 20 347 оценки против 20 710 реальной cmdline — полоса (32 403; 32 767] проходила
// гейт и ловила Win32 206 уже после старта.
//
// Лечение: IProcessLauncher.EstimateCommandLineLength(spec) — единственная точка
// правды. Тесты ниже ЗАЩИЩАЮТ это: мутация «верни 0» или «не учитывай dockerArgs» должна
// ронять хотя бы один тест. Иначе мы просто переставим слагаемые и оставим ту же дыру
// (задача d2ebadda, ключевое к проверке).
//
// Процессов здесь не запускаем — DockerProcessRunner.BuildDockerExecArgs и
// EstimateCommandLineLength чистые функции от ProcessSpec. Тесты гоняются и на Linux
// CI без docker CLI, как и соседний DockerProcessRunnerEnvTests.
public class DockerProcessRunnerCmdlineEstimationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(),
        "ccs-docker-cmdline-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* тест-мусор */ }
    }

    private (DockerProcessRunner runner, string ownerId) CreateRunner(
        Dictionary<string, string?>? envOverrides = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(envOverrides ?? new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Sandbox должен быть включён, иначе DockerPathMapper.ToRuntime бросает
                // «Путь недоступен в песочнице» на любом WorkingDirectory. Тесты здесь
                // проверяют длину argv, а не реальный запуск.
                ["Sandbox:ProjectsRoot"] = Path.Combine(_tempDir, "projects"),
            })
            .Build();
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        return (new DockerProcessRunner(sandbox, "owner-1"), "owner-1");
    }

    private ProcessSpec SpecForEstimate(IReadOnlyList<string> args, string? turnId)
    {
        // WorkingDirectory кладём ВНУТРЬ Sandbox:ProjectsRoot — иначе DockerPathMapper
        // бросит исключение. ProjectsRoot живёт в temp-каталоге теста и подчищается в Dispose.
        var wd = Path.Combine(_tempDir, "projects", "demo");
        Directory.CreateDirectory(wd);
        return new ProcessSpec
        {
            FileName = "claude",
            Args = args,
            WorkingDirectory = wd,
            RedirectStdin = true,
            TurnId = turnId,
        };
    }

    // Главный гейт: Estimate должен учитывать обвязку docker exec (~401 символ без env).
    // Без неё — оценка 20 347, реальная cmdline 20 710: тот самый блокер.
    // Мутация `return DockerPath.Length` или `return 0` ломает тест.
    [Fact]
    public void EstimateCommandLineLength_УчитываетОбвязкуDockerExec()
    {
        var (runner, _) = CreateRunner();
        const int argLen = 50;
        var args = new[] { new string('a', argLen) };
        var turnId = "0123456789ab";

        var estimated = runner.EstimateCommandLineLength(SpecForEstimate(args, turnId));
        // «Внутренности» — только то, что уйдёт в CLI: claude + сам аргумент + ArgCost с обвязкой.
        // Проверяем, что estimated больше них на обвязку docker exec (~120 символов минимум:
        // docker + exec + -i + -w + ContainerName + /app/run-turn.sh + turnId).
        var inner = TurnPromptAssembler.ArgCost("claude") + TurnPromptAssembler.ArgCost(args[0]);

        estimated.Should().BeGreaterThan(inner + 100,
            "docker exec добавляет существенную обвязку поверх cli+args; без неё оценка "
            + "ниже реальной cmdline, что и было блокером dc641949");
    }

    // env-переменные уходят в обвязку как `-e K=V`: каждая пара добавляет ArgCost("-e")
    // + ArgCost("K=V") к итогу. Без них — занижение на длину переменных провайдера
    // (ANTHROPIC_AUTH_TOKEN от BuildCliEnv — длина нам неизвестна).
    [Fact]
    public void EstimateCommandLineLength_УчитываетEnvПарыВОбвязке()
    {
        var (runner, _) = CreateRunner();
        var turnId = "0123456789ab";
        var args = Array.Empty<string>();

        // Сравниваем две оценки: одна — с одной env-переменной, вторая — с двумя.
        // Разница должна быть ArgCost("-e") + ArgCost("K=V").
        var withOneEnv = runner.EstimateCommandLineLength(new ProcessSpec
        {
            FileName = "claude",
            Args = args,
            WorkingDirectory = SpecForEstimate(args, turnId).WorkingDirectory,
            Env = new Dictionary<string, string> { ["FOO"] = "bar" },
            RedirectStdin = true,
            TurnId = turnId,
        });
        var withTwoEnvs = runner.EstimateCommandLineLength(new ProcessSpec
        {
            FileName = "claude",
            Args = args,
            WorkingDirectory = SpecForEstimate(args, turnId).WorkingDirectory,
            Env = new Dictionary<string, string> { ["FOO"] = "bar", ["BAZ"] = "qux" },
            RedirectStdin = true,
            TurnId = turnId,
        });

        var expectedDelta = TurnPromptAssembler.ArgCost("-e") + TurnPromptAssembler.ArgCost("BAZ=qux");
        (withTwoEnvs - withOneEnv).Should().Be(expectedDelta,
            "каждая env добавляется парой -e K=V; без этого ANTHROPIC_AUTH_TOKEN стороннего "
            + "провайдера прошёл бы гейт без своей длины — то же падение, что и блокер");
    }

    // Симметрия Estimate и Start проверяется косвенно: обвязка docker exec ОБЯЗАНА
    // давать существенную надбавку к cli+args (тест «УчитываетОбвязкуDockerExec»), и каждая
    // env-переменная ОБЯЗАНА добавляться как -e K=V (тест «УчитываетEnvПарыВОбвязке»).
    // Точная сверка через BuildDockerExecArgs осознанно НЕ делается: BuildTurnEnv внутри
    // BuildDockerExecArgs тянет CLAUDE_CONFIG_DIR, и его длина зависит от профиля владельца
    // (см. SandboxManager.ProfilesMount) — это лучше покрывать интеграционным тестом
    // на живой SandboxManager, не на Estimate.

    // TurnId может быть null в редких сценариях (ApplyBudget через лямбду cmdlineLength
    // иногда получает spec без TurnId). Тогда Estimate использует 12-символьный плейсхолдер
    // (длина реального Guid[..12], который Start сгенерирует в DockerProcessRunner.Start),
    // и оценка остаётся согласованной с реальной обвязкой запуска.
    [Fact]
    public void EstimateCommandLineLength_БезTurnId_НеПадает()
    {
        var (runner, _) = CreateRunner();
        var spec = SpecForEstimate(["--print"], turnId: null);

        var act = () => runner.EstimateCommandLineLength(spec);
        act.Should().NotThrow("отсутствие TurnId не повод ронять оценку; "
            + "Docker runner использует 12-символьный плейсхолдер");
    }

    // M2 (финальная волна): уникальность turnId в Start. Раньше DockerProcessRunner.BuildDockerExecArgs
    // подставлял spec.TurnId ?? new string('0', 12), и при первом docker-запуске без TurnId все процессы
    // делили бы /tmp/turns/000000000000.pid (run-turn.sh удаляет pid-файл на выходе). Сейчас Start
    // генерирует реальный Guid[..12] и пробрасывает через spec with { TurnId = ... } — Estimate
    // остаётся детерминированным (TurnId=null → 12-символьный плейсхолдер), длина у реального и
    // плейсхолдера одинакова, и гейт ApplyBudget не разъезжается. Мутация «подменить NewTurnId()
    // на константу new string('0', 12)» — единственный способ вернуть дыру молча; мутация
    // «[..24]» разъезжает длину Estimate ↔ Start.
    //
    // Тест проверяет РЕАЛЬНЫЙ код-путь: DockerProcessRunner.NewTurnId() вызывается напрямую,
    // длина сверяется с плейсхолдером из BuildDockerExecArgs(null-TurnId), два вызова дают
    // разные значения. Прежняя редакция дублировала формулу инлайном и тестировала
    // Guid.NewGuid() вместо нашего кода — обе мутации проходили зелёными.
    [Fact]
    public void Start_БезTurnId_ГенерируетУникальныйGuidДлиной12()
    {
        var (runner, _) = CreateRunner();

        var baseSpec = new ProcessSpec
        {
            FileName = "claude",
            Args = ["--print"],
            WorkingDirectory = Path.Combine(_tempDir, "projects", "demo"),
            RedirectStdin = true,
            TurnId = null,
        };
        Directory.CreateDirectory(baseSpec.WorkingDirectory!);

        // Реальная генерация из прод-кода: NewTurnId — единственная точка, мутация «подменить
        // Guid на константу» или «растянуть Guid до 24» падает на проверке ниже.
        var first = DockerProcessRunner.NewTurnId();
        var second = DockerProcessRunner.NewTurnId();

        first.Length.Should().Be(12,
            "длина turnId в docker exec стабильна: либо плейсхолдер (Estimate), либо реальный Guid[..12] (Start); "
            + "мутация «[..24]» разъезжает длину Estimate ↔ Start — тест обязан её ловить");
        second.Length.Should().Be(12);
        first.Should().NotBe(second,
            "Guid.NewGuid() даёт уникальные значения; коллизия = дыра с общим /tmp/turns/{id}.pid вернулась — "
            + "мутация «вернуть new string('0', 12)» именно так её и возвращает");

        // Гейт на сам DockerProcessRunner: Estimate-путь даёт 12-символьный плейсхолдер turnId.
        // Длины NewTurnId и плейсхолдера ОБЯЗАНЫ совпадать, иначе Estimate и Start разойдутся,
        // и docker-владельцы снова поймают блокер dc641949.
        var estimateTurnIdInArgs = runner.BuildDockerExecArgs(baseSpec)
            .SkipWhile(a => a != "/app/run-turn.sh").Skip(1).First();
        estimateTurnIdInArgs.Length.Should().Be(first.Length,
            "Estimate-путь даёт 12-символьный плейсхолдер turnId той же длины, что и NewTurnId — "
            + "иначе оценка cmdline и реальная сборка .NET разъедутся");

        var act = () => runner.EstimateCommandLineLength(baseSpec);
        act.Should().NotThrow("отсутствие TurnId не повод ронять оценку; "
            + "Docker runner использует 12-символьный плейсхолдер той же длины, что NewTurnId");
    }
}
