using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm;

// Расход одного вызова: токены по видам, стоимость и модель, которой он реально
// посчитан. Вход разбит на виды — они тарифицируются по-разному (cache read дешевле).
public sealed record OneShotUsage(
    long InputTokens, long CacheCreationTokens, long CacheReadTokens, long OutputTokens,
    double? CostUsd, string? Model)
{
    public long TotalInputTokens => InputTokens + CacheCreationTokens + CacheReadTokens;
}

// Ответ вызова вместе с расходом. Usage = null, если CLI метрик не дал
// (нераспознанный формат ответа) — потребитель должен это пережить.
public sealed record OneShotResult(string Text, OneShotUsage? Usage, long DurationMs);

// Абстракция one-shot вызова LLM — для мокирования в тестах.
// В DI интерфейс указывает на тот же singleton OneShotClaudeRunner.
public interface IOneShotRunner
{
    // Модель ненастроенного провайдера тихо заменяется дефолтом claude
    string? NormalizeModel(string? model);

    // ownerId — владелец вызова: его среда исполнения определяет, где запустится claude
    // (локально или в песочнице). null — системный вызов, всегда локально.
    // effort — усилие рассуждения (--effort), для моделей с его поддержкой.
    Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null);

    // То же, но с расходом вызова (просит у CLI json-формат вместо text).
    // Для мест, которые показывают пользователю цену генерации.
    Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null);
}

// Общий раннер одноразовых вызовов claude --print (без сессии): промпт через stdin,
// ответ — stdout целиком. Модель стороннего провайдера подключается env-оверрайдами
// (LlmProviderRegistry.BuildCliEnv). Рабочая папка — пустая temp (claude не получает
// доступ к файлам). Используется сводками «Что нового» (ChangelogService),
// генерациями задач и заметок, персонами (ask/характер).
public sealed class OneShotClaudeRunner(LlmProviderRegistry llmProviders, ILauncherFactory launchers) : IOneShotRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // Модель ненастроенного провайдера тихо заменяется дефолтом claude —
    // генерация не должна падать из-за отсутствующего ключа
    public string? NormalizeModel(string? model) =>
        llmProviders.ResolveByModel(model) is { Enabled: false } ? null : model;

    public async Task<string> RunAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null) =>
        (await RunCliAsync(prompt, model, timeout, ct, ownerId, effort, withUsage: false)).Text;

    public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
        TimeSpan? timeout = null, CancellationToken ct = default,
        string? ownerId = null, string? effort = null) =>
        RunCliAsync(prompt, model, timeout, ct, ownerId, effort, withUsage: true);

    // withUsage: json-формат вместо text — тот же ответ плюс расход вызова.
    // Разделение сознательное: текстовый путь используют полтора десятка сервисов,
    // и лишний слой разбора json им ни к чему.
    private async Task<OneShotResult> RunCliAsync(string prompt, string? model,
        TimeSpan? timeout, CancellationToken ct, string? ownerId, string? effort, bool withUsage)
    {
        var launcher = launchers.ForOwner(ownerId);
        var workDir = Path.Combine(launcher.HostTempDir, "claude-oneshot");
        Directory.CreateDirectory(workDir);

        var args = new List<string> { "--print", "--output-format", withUsage ? "json" : "text" };
        // Хуки плагинов не нужны и плодят окна консоли на хосте — отключаем (скиллы one-shot не зовёт).
        // Нужно и при --safe-mode: в песочнице флага нет, а хуки отключить всё равно надо.
        args.AddRange(Claude.ClaudeRuntimeSettings.HooksOffArgs(launcher));
        // --safe-mode: CLI не тянет пользовательские кастомизации (~/.claude/CLAUDE.md
        // с правилами, скиллы, плагины, хуки, MCP) в системный промпт. One-shot — чистая
        // генерация текста, юзерский контекст ей не нужен, а стоил он ~половину входа
        // (замер на 2.1.207: 31.6 тыс. → 15.3 тыс. токенов обвязки), и личные правила
        // пользователя протекали в тон продуктовых текстов. CLAUDE_CONFIG_DIR так не
        // умеет (память CLI грузит мимо него), --bare ломает OAuth-авторизацию
        // (пропускает чтение кредов). Только локально: флаг появился в CLI 2.1.169,
        // песочница может нести версию старее — там не рискуем.
        if (!launcher.IsSandboxed) args.Add("--safe-mode");
        // Инструменты жёстко выключены. Это контракт (пустая temp-cwd и раньше
        // подразумевала «без файлов», но не мешала Read по абсолютному пути) и защита
        // от инъекции в промпт — в т.ч. когда вызов сделан от имени изолированного
        // пользователя, а процесс работает на хосте. Skill дополнительно отключает
        // инжекцию каталога скиллов в системный промпт (~3 тыс. токенов), когда
        // safe-mode недоступен (песочница).
        args.Add("--disallowedTools");
        args.Add("Bash,Read,Write,Edit,MultiEdit,NotebookEdit,Glob,Grep,WebFetch,WebSearch,Task,Agent,KillShell,BashOutput,Skill");
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(LlmProviderRegistry.StripClaudeWindowAlias(model)!);
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            args.Add("--effort");
            args.Add(effort);
        }

        var env = llmProviders.BuildCliEnv(model);

        var turnId = Guid.NewGuid().ToString("N")[..12];
        using var process = launcher.Start(new ProcessSpec
        {
            FileName = launcher.ClaudeCliCommand,
            Args = args,
            WorkingDirectory = workDir,
            Env = env,
            StdioEncoding = new UTF8Encoding(false),
            TurnId = turnId,
        });

        // Чтение вывода запускаем ДО записи промпта — иначе на большом промпте
        // возможен deadlock на заполненных пайпах
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.StandardInput.WriteAsync(prompt.AsMemory(), cts.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            started.Stop();

            if (process.ExitCode != 0)
            {
                // Причину CLI пишет не только в stderr: «Not logged in · Please run /login»
                // уходит в stdout при пустом stderr. Раньше её тут теряли, и в логах всех
                // сервисов оставалось «завершился с кодом 1:» без объяснения.
                var detail = stderr.Trim();
                if (detail.Length == 0) detail = ErrorDetail(stdout.Trim(), withUsage);
                if (detail.Length > 500) detail = detail[..500] + "…";
                throw new InvalidOperationException(
                    $"claude завершился с кодом {process.ExitCode}: {detail}");
            }

            // Время меряем по своим часам, а не по duration_ms от CLI: пользователь ждёт
            // весь вызов вместе со стартом процесса (~5-15 с), а не только запрос к API
            return withUsage
                ? ParseJsonResult(stdout, model, started.ElapsedMilliseconds)
                : new OneShotResult(stdout.Trim(), null, started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            launcher.Kill(process, turnId);
            throw new InvalidOperationException("Claude не ответил за отведённое время");
        }
    }

    // В json-режиме причина ошибки лежит в поле result, а не голым текстом —
    // достаём её, чтобы в логи и degraded-подпись не уезжала простыня JSON
    private static string ErrorDetail(string stdout, bool withUsage)
    {
        if (!withUsage || stdout.Length == 0) return stdout;
        try
        {
            var root = JsonDocument.Parse(stdout).RootElement;
            var text = root.TryGetProperty("result", out var r) ? r.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? stdout : text!;
        }
        catch { return stdout; }
    }

    // Ответ CLI в json: { result, total_cost_usd, modelUsage: { "<model>": {…} }, usage: {…} }.
    // Метрики берём из modelUsage — это агрегат по всем итерациям ответа, тогда как usage
    // описывает только последнюю (на длинных ответах расходятся в разы).
    private OneShotResult ParseJsonResult(string stdout, string? model, long durationMs)
    {
        try
        {
            var root = JsonDocument.Parse(stdout).RootElement;
            var text = (root.TryGetProperty("result", out var r) ? r.GetString() : null) ?? "";

            long input = 0, cacheCreate = 0, cacheRead = 0, output = 0;
            double? cliCost = null;
            string? usedModel = null;

            if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
            {
                foreach (var m in mu.EnumerateObject())
                {
                    usedModel ??= m.Name;
                    input += Num(m.Value, "inputTokens");
                    cacheCreate += Num(m.Value, "cacheCreationInputTokens");
                    cacheRead += Num(m.Value, "cacheReadInputTokens");
                    output += Num(m.Value, "outputTokens");
                }
            }
            else if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                input = Num(u, "input_tokens");
                cacheCreate = Num(u, "cache_creation_input_tokens");
                cacheRead = Num(u, "cache_read_input_tokens");
                output = Num(u, "output_tokens");
            }

            if (root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cost))
                cliCost = cost;

            // На стороннем эндпоинте CLI считает стоимость по ценам Anthropic — пересчитываем
            // по ценам конфига (та же логика, что у ходов сессии). Для родного Claude
            // ComputeCost возвращает null, и остаётся оценка CLI.
            var usage = new Protocol.UsageInfo((int)input, (int)output, (int)cacheRead, (int)cacheCreate);
            var finalCost = llmProviders.ComputeCost(model, usage) ?? cliCost;

            return new OneShotResult(text.Trim(),
                new OneShotUsage(input, cacheCreate, cacheRead, output, finalCost, usedModel ?? model),
                durationMs);
        }
        catch
        {
            // Формат ответа не распознан — отдаём как есть, без метрик: генерация важнее цифр
            return new OneShotResult(stdout.Trim(), null, durationMs);
        }
    }

    private static long Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
