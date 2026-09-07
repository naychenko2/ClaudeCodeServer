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
    // иногда получает spec без TurnId). Тогда docker exec всё равно стартует — Start
    // генерирует Guid.Estimate должен давать согласованную длину (12-символьный
    // плейсхолдер — worst case реального Guid[..12]).
    [Fact]
    public void EstimateCommandLineLength_БезTurnId_НеПадает()
    {
        var (runner, _) = CreateRunner();
        var spec = SpecForEstimate(["--print"], turnId: null);

        var act = () => runner.EstimateCommandLineLength(spec);
        act.Should().NotThrow("отсутствие TurnId не повод ронять оценку; "
            + "Docker runner использует 12-символьный плейсхолдер");
    }
}
