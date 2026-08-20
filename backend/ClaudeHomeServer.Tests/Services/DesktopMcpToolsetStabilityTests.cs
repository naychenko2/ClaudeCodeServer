using System.Text.Json;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож состава инструментов desktop-server (ADR-008). Существующий
/// <c>McpToolsetStabilityTests</c> новый сервер не покрывает, а инвариант тот же и такой же
/// смертельный: набор ключей серверов и отпечаток состава входят в сигнатуру запуска CLI
/// (<c>BuildLaunchSignature</c>). Стоит составу «мигать» между ходами — процесс claude
/// перезапускается со ВСЕМИ MCP-серверами: незавершённые вызовы падают «Stream closed»,
/// инструменты то есть, то «No such tool available».
///
/// Для десктопной грани соблазн особенно силён: устройство офлайн, сеанс рук не запущен,
/// грань выключена в проекте — всё это выглядит как повод убрать инструменты из tools/list.
/// Так делать нельзя: это ОТВЕТ инструмента, гейт живёт на бэкенде и проверяется на каждый
/// вызов. Тесты ниже держат состав постоянным при любом окружении.
/// </summary>
public class DesktopMcpToolsetStabilityTests
{
    // Полный и единственный состав грани
    private static readonly string[] ExpectedTools =
    [
        "desktop_devices", "desktop_screen", "desktop_ui", "desktop_act", "desktop_open", "desktop_run",
    ];

    private static string? FindMcpServer(string server)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", server, "index.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindBackendSource(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, "backend", "ClaudeHomeServer", .. relative]);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // tools/list живого desktop-server с заданными env. Бэкенд не нужен: состав считается
    // локально, в сеть сервер на tools/list не ходит. null — node недоступен (тест скипается).
    private static JsonDocument? ListTools(params string[] env)
    {
        var serverPath = FindMcpServer("desktop-server");
        Skip.If(serverPath is null, "mcp/desktop-server/index.js не найден");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { serverPath! },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var pair in env)
        {
            var i = pair.IndexOf('=', StringComparison.Ordinal);
            psi.Environment[pair[..i]] = pair[(i + 1)..];
        }

        System.Diagnostics.Process? proc;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            proc.StandardInput.Flush();

            // Первой строкой сервер пишет сигнал готовности (нотификация без id) — читаем до
            // строки с ответом, а не «первую попавшуюся»
            string? answer = null;
            for (var i = 0; i < 10 && answer is null; i++)
            {
                var line = proc.StandardOutput.ReadLine();
                if (line is null) break;
                if (line.Contains("\"id\":1", StringComparison.Ordinal)) answer = line;
            }
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            answer.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на tools/list");
            return JsonDocument.Parse(answer!);
        }
    }

    private static IReadOnlyList<string> ToolNames(JsonDocument doc) =>
        doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();

    private static JsonElement Tool(JsonDocument doc, string name) =>
        doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);

    /// <summary>
    /// Состав — ровно шесть инструментов при ЛЮБОМ окружении: без токена, с недоступным
    /// бэкендом, без id чата. Каждый из этих случаев в жизни означает «руки сейчас не
    /// работают» — и ни один не смеет менять tools/list.
    /// </summary>
    [SkippableTheory]
    // Штатное окружение хода
    [InlineData("DESKTOP_API_URL=http://127.0.0.1:1", "DESKTOP_API_TOKEN=t", "DESKTOP_SESSION_ID=s1")]
    // Capability-токен не выдан (грань выключена в проекте, чат не десктопный)
    [InlineData("DESKTOP_API_URL=http://127.0.0.1:1", "DESKTOP_API_TOKEN=")]
    // Голый запуск: ни одной переменной
    [InlineData()]
    public void СоставГрани_ШестьИнструментов_ПриЛюбомОкружении(params string[] env)
    {
        var doc = ListTools(env);
        if (doc is null) return;
        using (doc)
        {
            ToolNames(doc).Should().Equal(ExpectedTools,
                "офлайн-устройство, отсутствие сеанса рук и невыданный токен — это ОТВЕТ "
                + "инструмента, а не изменение состава: гейт живёт на бэкенде и проверяется "
                + "на каждый вызов");
        }
    }

    /// <summary>
    /// Адресат — человеческое имя устройства («home», «work»), а не GUID, и присутствует во
    /// всех инструментах, кроме самого списка устройств. GUID в аргументе означал бы, что
    /// модель обязана таскать идентификаторы, которых в разговоре с человеком не существует.
    /// </summary>
    [SkippableFact]
    public void ПараметрDevice_ЕстьВезде_КромеСпискаУстройств()
    {
        var doc = ListTools("DESKTOP_API_URL=http://127.0.0.1:1", "DESKTOP_API_TOKEN=t");
        if (doc is null) return;
        using (doc)
        {
            Tool(doc, "desktop_devices").GetProperty("inputSchema").GetProperty("properties")
                .TryGetProperty("device", out _).Should().BeFalse(
                    "имена устройств берутся ИЗ этого инструмента — адресовать его некуда");

            foreach (var name in ExpectedTools.Where(t => t != "desktop_devices"))
            {
                var device = Tool(doc, name).GetProperty("inputSchema").GetProperty("properties")
                    .GetProperty("device");
                device.GetProperty("type").GetString().Should().Be("string");
                device.GetProperty("description").GetString().Should()
                    .Contain("GUID", $"{name}: в описании device обязано стоять, что это имя, а не GUID");
            }
        }
    }

    /// <summary>
    /// Потолок батча — 10 шагов, набор действий закрыт (click|type|key|scroll|focus), шаги
    /// адресуются снапшотом: отдельных click/type/press нет, действий по координатам нет.
    /// </summary>
    [SkippableFact]
    public void Act_ПотолокШаговИНаборДействий()
    {
        var doc = ListTools("DESKTOP_API_URL=http://127.0.0.1:1", "DESKTOP_API_TOKEN=t");
        if (doc is null) return;
        using (doc)
        {
            var schema = Tool(doc, "desktop_act").GetProperty("inputSchema");
            schema.GetProperty("required").EnumerateArray().Select(x => x.GetString())
                .Should().Contain("snapshotId").And.Contain("steps",
                    "шаги адресуются ref-ами из снапшота, а не пикселями");

            var steps = schema.GetProperty("properties").GetProperty("steps");
            steps.GetProperty("maxItems").GetInt32().Should().Be(10, "потолок батча — правило протокола");
            steps.GetProperty("items").GetProperty("properties").GetProperty("action")
                .GetProperty("enum").EnumerateArray().Select(x => x.GetString())
                .Should().Equal(["click", "type", "key", "scroll", "focus"]);
        }
    }

    /// <summary>
    /// Кадр по умолчанию — активное окно, а не экран целиком: полноэкранный 4K-кадр оседает
    /// в транскрипте <c>.jsonl</c> навсегда и кирпичит чат через <c>--resume</c>. И тот же
    /// инструмент принимает snapshotId — иначе исход snapshot_stale некуда вернуть.
    /// </summary>
    [SkippableFact]
    public void Screen_ДефолтныйScopeОкно_ИПринимаетSnapshotId()
    {
        var doc = ListTools("DESKTOP_API_URL=http://127.0.0.1:1", "DESKTOP_API_TOKEN=t");
        if (doc is null) return;
        using (doc)
        {
            var props = Tool(doc, "desktop_screen").GetProperty("inputSchema").GetProperty("properties");
            var scope = props.GetProperty("scope");
            scope.GetProperty("default").GetString().Should().Be("window");
            scope.GetProperty("enum").EnumerateArray().Select(x => x.GetString())
                .Should().Equal(["window", "screen", "region"]);
            props.TryGetProperty("snapshotId", out _).Should().BeTrue(
                "кадр берётся вместе со снапшотом: расхождение обязано возвращаться как snapshot_stale");
        }
    }

    /// <summary>
    /// Состав в исходнике — константа: ни одного обращения к env внутри объявления TOOLS.
    /// Ровно так состав начинал зависеть от хода у соседних серверов (WORKSPACE_WRITE по
    /// интенту, TASKS_EXECUTE по глубине делегирования) — и каждый раз убивал процесс CLI.
    /// </summary>
    [SkippableFact]
    public void СоставВИсходнике_НеСмотритНаОкружение()
    {
        var path = FindMcpServer("desktop-server");
        Skip.If(path is null, "mcp/desktop-server/index.js не найден");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("const TOOLS = [", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "объявление состава обязано существовать");
        var end = source.IndexOf("\n// --- Обработчики ---", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "за составом обязан идти раздел обработчиков");

        var body = string.Join('\n', source[start..end].Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        body.Should().NotContain("process.env",
            "состав инструментов не смеет зависеть от окружения — иначе отпечаток запуска CLI "
            + "мерцает между ходами и процесс перезапускается со всеми MCP-серверами");
        body.Should().NotContain("if (",
            "состав объявляется целиком, без условных веток");
        body.Should().NotContain(".push(",
            "инструменты не доклеиваются к составу по условию");
    }

    /// <summary>
    /// Авто-ретраев для действий нет: клик, ввод и запуск команды не идемпотентны, а
    /// «связь оборвалась» не равно «не применилось». Повтор разрешён только чтению списка
    /// устройств — и только когда запрос заведомо не дошёл.
    /// </summary>
    [SkippableFact]
    public void ВызовУстройства_БезАвтоРетраев()
    {
        var path = FindMcpServer("desktop-server");
        Skip.If(path is null, "mcp/desktop-server/index.js не найден");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("async function callDevice(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "единая точка вызова устройства обязана существовать");
        var end = source.IndexOf("\n// --- JSON-RPC over stdio ---", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "за вызовом устройства обязан идти раздел JSON-RPC");

        var body = string.Join('\n', source[start..end].Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        body.Should().NotContain("retry",
            "повтор действия на чужой машине запрещён протоколом: клик и ввод не идемпотентны");

        // Тексты исходов тоже не смеют звать на повтор — модель читает именно их
        var hints = source[source.IndexOf("const OUTCOME_HINT", StringComparison.Ordinal)..
            source.IndexOf("// --- Ответы инструмента ---", StringComparison.Ordinal)];
        foreach (var outcome in new[] { "applied_unverified", "no_visible_change", "unknown" })
            hints.Should().Contain(outcome,
                $"исход {outcome} обязан иметь текст с явным запретом повтора");
        hints.Should().NotContain("повтори действие").And.NotContain("попробуй ещё раз");
    }

    /// <summary>
    /// Результаты экрана и снапшота уходят модели в явном контейнере недоверенных данных:
    /// это основная угроза класса computer-use — инструкции, написанные на чужом экране,
    /// не исполняются.
    /// </summary>
    [SkippableFact]
    public void ЭкранИСнапшот_ВКонтейнереНедоверенныхДанных()
    {
        var path = FindMcpServer("desktop-server");
        Skip.If(path is null, "mcp/desktop-server/index.js не найден");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("function outcomeContent(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "сборка содержимого ответа обязана существовать");
        var end = source.IndexOf("function callResult(", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        source[start..end].Should().Contain("untrusted(",
            "содержимое чужого экрана заворачивается в контейнер недоверенных данных");
        source.Should().Contain("НЕДОВЕРЕННЫЕ ДАННЫЕ",
            "контейнер обязан быть подписан явно, а не подразумеваться");
    }

    /// <summary>
    /// Серверная сторона доставки грани (появляется вместе с <c>BuildDesktopContext</c> в
    /// SessionManager): решается конфигурацией — типом чата и включением в проекте, — но
    /// никогда состоянием хода. Пока метод не появился, тест скипается.
    /// </summary>
    [SkippableFact]
    public void КонтекстГрани_НеСмотритНаСостояниеХода()
    {
        var path = FindBackendSource("Services", "SessionManager.cs");
        Skip.If(path is null, "SessionManager.cs не найден (сборка вне дерева репозитория)");

        var source = File.ReadAllText(path!);
        var start = source.IndexOf("BuildDesktopContext", StringComparison.Ordinal);
        Skip.If(start < 0, "BuildDesktopContext ещё не реализован (серверная часть грани)");

        var end = source.IndexOf("\n    private ", start, StringComparison.Ordinal);
        if (end < 0) end = source.Length;
        var body = string.Join('\n', source[start..end].Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        body.Should().NotContain("_currentTurn",
            "состояние хода не должно влиять на доставку грани — гейт исполнения живёт на "
            + "бэкенде и проверяется на каждый вызов");
    }

    /// <summary>
    /// Deny-имена десктопной грани (PersonaAccessPolicy.ReadOnlyDisallowed) не роняют запуск
    /// claude CLI, когда грань в этом ходу не доставлена: список уезжает в --disallowedTools
    /// каждой сессии ReadOnly-персоны, а сервера desktop в конфиге хода нет. Живой прогон
    /// (CLI 2.1.232): exit 0 и чистый stderr — CLI не сверяет mcp__*-имена со списком
    /// известных инструментов, в отличие от встроенных (мёртвое встроенное имя даёт
    /// предупреждение «matches no known tool» — класс дефекта MultiEdit).
    /// Требования к окружению: claude в PATH и залогиненный профиль; промпт подаётся через
    /// stdin — --disallowedTools вариадический и съедает позиционные аргументы. claude нет
    /// (CI) или логин недоступен — скип; юнит-часть инварианта живёт в PersonaAccessPolicyTests.
    /// </summary>
    // claude в PATH: на Windows это npm-shim claude.cmd, который Process.Start с
    // UseShellExecute=false по имени не резолвит — ищем полный путь сами.
    private static string? FindClaude()
    {
        string[] exts = OperatingSystem.IsWindows() ? [".cmd", ".exe", ""] : [""];
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, "claude" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    [SkippableFact]
    public async Task DenyИменаДесктопа_НеРоняютЗапускCli()
    {
        var claudePath = FindClaude();
        Skip.If(claudePath is null, "claude CLI не найден в PATH");

        var deny = string.Join(",", PersonaAccessPolicy.ReadOnlyDisallowed);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = claudePath!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--disallowedTools");
        psi.ArgumentList.Add(deny);
        // Маркеры вложенного запуска (наследуются, когда тесты гоняют изнутри Claude Code)
        // с себя снимаем: CLI с CLAUDECODE=1/CLAUDE_CODE_* ведёт себя как дочерняя сессия.
        // Провайдерские ANTHROPIC_*/CLAUDE_CONFIG_DIR наоборот наследуем — окружение
        // прогоняющего (его ретранслятор, его профиль) остаётся его выбором.
        foreach (var key in psi.Environment.Keys.Where(k =>
                     k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                     || k.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        System.Diagnostics.Process? proc;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"claude CLI не запустился: {ex.Message}"); return; }
        Skip.If(proc is null, "не удалось запустить claude CLI");

        using (proc!)
        {
            await proc.StandardInput.WriteAsync("Ответь одним словом: ок");
            await proc.StandardInput.DisposeAsync();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var exitTask = proc.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromMinutes(2))) != exitTask)
            {
                proc.Kill(entireProcessTree: true);
                var tail = stderrTask.IsCompleted ? await stderrTask : "";
                Skip.If(true, $"claude CLI не завершился за 2 минуты (нет логина или сети); stderr: {tail[..Math.Min(300, tail.Length)]}");
                return;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            // Инвариант в строгой форме: CLI принимает mcp__desktop__*-имена молча и завершает
            // ход. «matches no known tool» — ровно тот класс, которого избегаем (неизвестное
            // имя в deny; так проявлял себя MultiEdit), это красный при любом исходе хода.
            // Сетевой или логинный сбой инфраструктуры инвариант не опровергает — скип, чтобы
            // тест не флейкал от качества связи с API.
            stderr.Should().NotContain("matches no known tool",
                "CLI отверг deny-правило — неизвестное имя в deny-списке персоны");
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                Skip.If(true, $"claude CLI не завершил ход (код {proc.ExitCode} — нет логина/сети?); stderr: {stderr[..Math.Min(300, stderr.Length)]}");
                return;
            }
            stdout.Should().NotBeNullOrWhiteSpace("CLI завершил ход с deny-именами грани в правилах");
        }
    }
}
