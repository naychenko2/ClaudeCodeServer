using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Полный поток inline-сабагентов (Task/Agent). CLI транслирует в stdout только их
// tool_use-блоки — text/thinking сабагента в стриме нет вовсе. Зато сабагент пишет
// собственный транскрипт <claude-projects>/<flat-cwd>/<sessionId>/subagents/agent-*.jsonl,
// а agent-*.meta.json рядом несёт toolUseId родительского вызова. Ватчер поллит эти файлы
// на протяжении хода и эмитит AgentTextMessage/AgentThinkingMessage(parentToolUseId, text).
// Файлы, существовавшие на старте хода, пропускаются целиком — их содержимое уже в истории.
//
// Попутно с чтением копится паспорт прогона каждого агента (SubagentRunTally): расследование
// «сабагент замолчал посреди работы» упиралось в отсутствие любых цифр — см. факты в
// SubagentRunLog. Паспорт уходит в runSink на завершении агента (Finalize) — там же, где
// продукт объявляет его результат готовым.
internal sealed class SubagentStreamWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    // Дебаунс детектора обрыва на bg_done: событие bg_agent_done приходит РАНЬШЕ, чем финальный
    // отчёт агента доезжает до транскрипта agent-*.jsonl (по журналу subagent-runs за 19–23.08:
    // медиана дозаписи финала 2.6 с, хвост до ~50 с) — без паузы хвост tool_use читался как
    // обрыв у штатно завершившихся агентов. Ждём и перечитываем хвост: доехал end_turn —
    // паспорт уходит штатным. 10 с покрывают медиану с запасом; редкий хвост в десятки секунд
    // доберёт финальный Emit при Dispose (там транскрипт дочитывается ещё раз).
    // internal-поле, а не константа: тесты сокращают ожидание.
    internal TimeSpan BgDoneRecheckDelay = TimeSpan.FromSeconds(10);

    private readonly string _cwd;
    private readonly string _claudeSessionId;
    private readonly Func<ServerMessage, Task> _onMessage;
    private readonly string? _sessionId;
    private readonly Action<SubagentRunPassport>? _runSink;
    // Окно контекста, объявленное CLI на запуске прогона (0 — не объявляли): env наследуют
    // и сабагенты, поэтому оно относится к ним так же, как к основному ходу
    private readonly int _cliContextWindow;
    private readonly CancellationTokenSource _cts = new();
    // Смещение прочитанного по каждому файлу (продвигается только по целым строкам)
    private readonly Dictionary<string, long> _offsets = [];
    // toolUseId родителя по файлу транскрипта (из agent-*.meta.json; null — меты ещё нет)
    private readonly Dictionary<string, string?> _toolIdByFile = [];
    // Паспорт прогона по файлу транскрипта (копится по мере чтения)
    private readonly Dictionary<string, SubagentRunTally> _tallies = [];
    // Что уже отдано наружу по файлу: момент последней активности (агент, которого добили,
    // дописывает ТОТ ЖЕ транскрипт и завершается повторно — второй одинаковый паспорт не нужен),
    // был ли паспорт оборванным и чем закрыт — чтобы опровергнуть обрыв, когда финал доехал позже
    private readonly Dictionary<string, (DateTime At, bool Truncated, string FinishedBy)> _reported = [];
    // Скан зовётся из цикла поллинга и из DrainAsync (перед tool_result) — не параллелим
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    // Файлы с назначенной перепроверкой обрыва (защита от второй эмиссии тем же bg_done);
    // читается/пишется только под _scanLock
    private readonly HashSet<string> _bgRechecks = [];
    // Снимок ProfilesRoot на создание ватчера (см. конструктор)
    private readonly string? _profilesRoot;
    // Корень профиля CLI (CLAUDE_CONFIG_DIR) текущего прогона — папку сессии ищем СНАЧАЛА в нём.
    // null — профиль не известен: «свой» корень тогда ~/.claude/projects (DefaultRoot)
    private readonly string? _preferredConfigRoot;
    private string? _dir;               // папка subagents появляется при первом сабагенте
    private Task? _loop;

    public bool IsDisposed { get; private set; }

    // Причина конца прогона для ветки НЕ-bg_done. По умолчанию "run_end" — обычная смерть
    // прогона по окончании хода. ClaudeSession ставит "interrupted" перед Dispose ватчера,
    // когда ход был убит пользовательским прерыванием: эта ветка раньше маскировалась под
    // обычную смерть, и координатор не мог отличить «агент замолчал сам» от «его убили» —
    // класс «убит прерыванием» отдельно выделяется в паспорте и в текстах директив.
    // Ветка bg_done (BgDoneRecheckDelay) этим полем НЕ управляется — она отдаётся как "bg_done".
    internal string RunEndReason { get; private set; } = "run_end";

    /// <summary>
    /// Пометить прогон как убитый пользовательским прерыванием хода. Dispose использует
    /// эту причину вместо дефолтного "run_end" — переход отражается и в дневном jsonl.
    /// </summary>
    internal void MarkRunInterrupted() => RunEndReason = "interrupted";

    // Тот же контекст наблюдения? Same-process ход (init повторяется в том же процессе)
    // переиспользует живой ватчер — пересоздание без дренажа теряло бы хвост транскриптов.
    // Профиль входит в контекст: прогон под другим CLAUDE_CONFIG_DIR пишет в другую папку,
    // а _dir у живого ватчера закеширован
    public bool Matches(string cwd, string claudeSessionId, string? preferredConfigRoot = null) =>
        !IsDisposed && _cwd == cwd && _claudeSessionId == claudeSessionId
        && string.Equals(_preferredConfigRoot, preferredConfigRoot, StringComparison.OrdinalIgnoreCase);

    public SubagentStreamWatcher(string cwd, string claudeSessionId, Func<ServerMessage, Task> onMessage,
        string? sessionId = null, Action<SubagentRunPassport>? runSink = null, int cliContextWindow = 0,
        string? profilesRoot = null, string? preferredConfigRoot = null)
    {
        _cwd = cwd;
        _claudeSessionId = claudeSessionId;
        _onMessage = onMessage;
        _sessionId = sessionId;
        _runSink = runSink;
        _cliContextWindow = cliContextWindow;
        _preferredConfigRoot = string.IsNullOrWhiteSpace(preferredConfigRoot) ? null : preferredConfigRoot;
        // Снимок на момент создания ватчера: статик WorkflowAgentParser.ProfilesRoot —
        // глобальный, и в тестах его перезаписывает Program.cs параллельных
        // WebApplicationFactory-хостов; живой ватчер не должен менять корень посреди работы
        _profilesRoot = profilesRoot ?? WorkflowAgentParser.ProfilesRoot;
    }

    public void Start()
    {
        // Транскрипты, существующие на старте хода, — от прошлых ходов, их содержимое уже
        // в истории: помечаем прочитанными. Появившаяся позже папка/файлы — текущий ход, с нуля.
        try
        {
            _dir = ResolveDir();
            if (_dir is not null)
                foreach (var f in Directory.GetFiles(_dir, "agent-*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    _offsets[f] = new FileInfo(f).Length;
                    // Начало транскрипта не прочитано — паспорт такого агента считается неполным.
                    // Содержимое прошлого хода НЕ парсим: журнал не должен получать паспорт
                    // за активность, которой в этом ходе не было.
                    TallyFor(f).Partial = true;
                }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SubagentWatcher] Инициализация не удалась: {ex.Message}");
        }

        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // Транзиентная ошибка ФС не должна убивать цикл насовсем — иначе поток
                    // сабагентов молчит до конца прогона (пустые карточки «Активности»)
                    try { await ScanAsync(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SubagentWatcher] Скан не удался, пропускаем тик: {ex.Message}");
                    }
                    await Task.Delay(PollInterval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { /* штатная остановка */ }
            catch (ObjectDisposedException) { /* Dispose между сканом и Delay — штатная остановка */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SubagentWatcher] Цикл поллинга упал: {ex.Message}");
            }
        });
    }

    // Немедленный скан: зовётся перед трансляцией tool_result сабагента, чтобы весь его
    // текст лёг в ленту ДО результата (и до продолжения текста основного агента)
    public async Task DrainAsync()
    {
        if (IsDisposed) return;
        try { await ScanAsync(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SubagentWatcher] Drain не удался: {ex.Message}");
        }
    }

    /// <summary>
    /// Прогон агента(ов) объявлен завершённым: дочитываем хвост транскрипта и отдаём паспорта
    /// наружу. Зовётся оттуда, где продукт считает результат агента готовым — bg_agent_done
    /// и tool_result синхронного Task. finishedBy идёт в паспорт как есть.
    /// </summary>
    public async Task FinalizeAsync(IReadOnlyList<string> toolUseIds, string finishedBy)
    {
        if (IsDisposed) return;
        // Дренаж — безусловный: текст сабагента обязан лечь в ленту раньше индикатора
        // завершения, даже когда паспорта не ведутся (тесты/сессии без стора)
        await DrainAsync();
        if (_runSink is null || toolUseIds.Count == 0) return;
        // Под тем же локом, что и скан: словари файлов пишет поток поллинга
        await _scanLock.WaitAsync();
        try
        {
            foreach (var (file, toolId) in _toolIdByFile)
            {
                if (toolId is null || !toolUseIds.Contains(toolId)) continue;
                // bg_done с хвостом tool_use — ещё не обрыв: финал агента часто дозаписывается
                // в транскрипт ПОСЛЕ сигнала завершения. Эмиссию откладываем до перепроверки
                // (см. BgDoneRecheckDelay) — паспорт по этому bg_done уходит один раз, оттуда.
                if (finishedBy == "bg_done" && _tallies.TryGetValue(file, out var tally) && tally.Truncated)
                {
                    if (_bgRechecks.Add(file)) ScheduleBgDoneRecheck(file);
                    continue;
                }
                Emit(file, finishedBy);
            }
        }
        finally { _scanLock.Release(); }
    }

    // Отложенная перепроверка обрыва по bg_done: выждать, перечитать хвост транскрипта и только
    // тогда эмитить паспорт (штатный, если доехал end_turn). Фоновая задача не блокирует ни цикл
    // поллинга, ни Dispose; остановка ватчера отменяет её через _cts — паспорт тогда уходит
    // финальным Emit из Dispose (с тем же bg_done) после его собственного дренажа, без дубля.
    private void ScheduleBgDoneRecheck(string file)
    {
        // Токен — до запуска задачи: Dispose освобождает _cts, и обращение к _cts.Token из
        // фоновой задачи бросало бы ObjectDisposedException вместо штатной отмены
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BgDoneRecheckDelay, ct);
                await _scanLock.WaitAsync(ct);
                try
                {
                    if (IsDisposed) return; // паспорт уже эмитнул Dispose
                    await ScanFileAsync(file);
                    Emit(file, "bg_done");
                }
                finally
                {
                    // Снимаем отметку и при сбое чтения — иначе файл навсегда остался бы
                    // «на перепроверке», и следующий bg_done того же агента глотался бы
                    _bgRechecks.Remove(file);
                    _scanLock.Release();
                }
            }
            catch (OperationCanceledException) { /* ватчер остановлен — паспорт отдал Dispose */ }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SubagentWatcher] Перепроверка обрыва не удалась: {ex.Message}");
            }
        });
    }

    // Паспорт наружу. Повторный вызов без новой активности агента ничего не шлёт: у фонового
    // агента завершение приходит и структурным событием, и текстовым, а добитый агент
    // завершается ещё раз тем же транскриптом.
    private void Emit(string file, string finishedBy)
    {
        if (_runSink is null || !_tallies.TryGetValue(file, out var tally)) return;
        if (_reported.TryGetValue(file, out var last) && last.At == tally.LastActivityAt) return;
        _reported[file] = (tally.LastActivityAt, tally.Truncated, finishedBy);

        long size = 0;
        try { size = new FileInfo(file).Length; } catch (Exception) { /* файл мог исчезнуть */ }
        try { _runSink(tally.Build(_sessionId, finishedBy, size, _cliContextWindow)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SubagentWatcher] Паспорт прогона не записан: {ex.Message}");
        }
    }

    private SubagentRunTally TallyFor(string file)
    {
        if (_tallies.TryGetValue(file, out var tally)) return tally;
        // id агента — в имени транскрипта (agent-<id>.jsonl), он же в строках файла
        var id = Path.GetFileNameWithoutExtension(file);
        if (id.StartsWith("agent-", StringComparison.Ordinal)) id = id["agent-".Length..];
        return _tallies[file] = new SubagentRunTally(id);
    }

    private static string? StringProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task ScanAsync()
    {
        await _scanLock.WaitAsync();
        try
        {
            // Папка subagents появляется при первом сабагенте — пробуем найти её при каждом скане,
            // пока не найдём (??= не подойдет: null от ResolveDir "запомнится" и новые попытки не пойдут)
            if (_dir is null)
                _dir = ResolveDir();
            if (_dir is null) return;

            foreach (var file in Directory.GetFiles(_dir, "agent-*.jsonl", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                // Ошибка одного файла (исчез/занят между GetFiles и чтением) не должна
                // блокировать чтение остальных сабагентов
                try { await ScanFileAsync(file); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SubagentWatcher] Файл {Path.GetFileName(file)} не прочитан: {ex.Message}");
                }
        }
        finally { _scanLock.Release(); }
    }

    private async Task ScanFileAsync(string file)
    {
        var toolId = ResolveToolId(file);
        if (toolId is null) return; // меты ещё нет — файл дочитаем следующим тиком

        var offset = _offsets.GetValueOrDefault(file, 0L);
        var length = new FileInfo(file).Length;
        if (length <= offset) return;

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var chunk = await reader.ReadToEndAsync();

        // Продвигаемся только по целым строкам — хвост без \n дописывается CLI прямо сейчас
        var lastNewline = chunk.LastIndexOf('\n');
        if (lastNewline < 0) return;
        _offsets[file] = offset + System.Text.Encoding.UTF8.GetByteCount(chunk[..(lastNewline + 1)]);

        var tally = TallyFor(file);
        foreach (var line in chunk[..lastNewline].Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                tally.Feed(doc.RootElement);
                await EmitBlocksAsync(doc.RootElement, toolId);
            }
            catch (JsonException) { /* битая строка — норма для дописываемого файла */ }
        }

        // Финал доехал позже перепроверки (хвост дозаписи бывает и в десятки секунд): паспорт
        // уже ушёл оборванным — отдаём опровержение тем же finishedBy, иначе пометка обрыва в
        // чате так и останется (Emit зовётся только из Finalize/перепроверки/Dispose)
        if (_reported.TryGetValue(file, out var last) && last.Truncated && !tally.Truncated)
            Emit(file, last.FinishedBy);
    }

    // Блоки assistant-строки транскрипта: text/thinking эмитим, tool_use пропускаем —
    // их CLI уже транслирует в основной стрим с parent_tool_use_id
    private async Task EmitBlocksAsync(JsonElement root, string toolId)
    {
        if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") return;
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            switch (bt.GetString())
            {
                case "text":
                    if (block.TryGetProperty("text", out var te) && te.GetString() is { } text
                        && !string.IsNullOrWhiteSpace(text))
                        await _onMessage(new AgentTextMessage(toolId, text));
                    break;
                case "thinking":
                    if (block.TryGetProperty("thinking", out var th) && th.GetString() is { } thinking
                        && !string.IsNullOrWhiteSpace(thinking))
                        await _onMessage(new AgentThinkingMessage(toolId, thinking));
                    break;
            }
        }
    }

    private string? ResolveToolId(string agentFile)
    {
        if (_toolIdByFile.TryGetValue(agentFile, out var cached)) return cached;
        var metaPath = Path.ChangeExtension(agentFile, ".meta.json");
        if (!File.Exists(metaPath)) return null; // не кэшируем — мета может появиться позже
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var toolId = doc.RootElement.TryGetProperty("toolUseId", out var tid)
                && tid.ValueKind == JsonValueKind.String ? tid.GetString() : null;
            _toolIdByFile[agentFile] = toolId;
            // Тип и описание агента есть ТОЛЬКО в мете — в самом транскрипте их нет
            var tally = TallyFor(agentFile);
            tally.ToolUseId = toolId;
            tally.AgentType = StringProp(doc.RootElement, "agentType");
            tally.Description = StringProp(doc.RootElement, "description");
            return toolId;
        }
        catch (Exception)
        {
            return null; // мета дописывается — попробуем следующим тиком
        }
    }

    private string? ResolveDir()
    {
        var flat = string.Concat(_cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));

        // «Свой» корень — первым. Папка сессии живёт сразу в нескольких профилях, когда у чата
        // были фолбэк-ходы через другие провайдеры (glm, kimi…): перебор ниже идёт в порядке ФС
        // и возвращал первую попавшуюся — старую копию без новых строк. Ватчер весь день
        // молчал: ни паспортов, ни добивания (чат 95926c09, 23.08: 9 сабагентов, 6 обрывов,
        // 0 паспортов). Общий перебор остаётся фолбэком.
        var ownRoot = _preferredConfigRoot is { } preferred
            ? Path.Combine(preferred, "projects")
            : WorkflowAgentParser.DefaultRoot;
        if (Directory.Exists(ownRoot) && FindSubagentsDir(ownRoot, flat) is { } own) return own;

        // Профили подписок (sub-*) и созданные после старта сервера не входят в AllowedRoots
        // (там только явно зарегистрированные провайдеры) — проходим их ключи сами:
        // {ProfilesRoot}/{key}/projects/{flat}/{sessionId}/subagents
        if (_profilesRoot is { } profilesRoot && Directory.Exists(profilesRoot))
            foreach (var keyDir in Directory.GetDirectories(profilesRoot))
            {
                var projectsDir = Path.Combine(keyDir, "projects");
                if (!Directory.Exists(projectsDir)) continue;
                var found = FindSubagentsDir(projectsDir, flat);
                if (found is not null) return found;
            }

        foreach (var root in WorkflowAgentParser.AllowedRoots)
        {
            // Свой корень уже просмотрен выше (DefaultRoot при неизвестном профиле)
            if (!Directory.Exists(root) || string.Equals(root, ownRoot, StringComparison.OrdinalIgnoreCase)) continue;
            var found = FindSubagentsDir(root, flat);
            if (found is not null) return found;
        }

        return null;
    }

    // Ищет папку subagents в заданном корне по соглашению {flat}/{sessionId}/subagents,
    // с фолбэк-перебором подпапок корня: правило уплощения cwd у CLI может отличаться
    // от нашего (кириллица, точки, подчёркивания) — без фолбэка такие проекты молча
    // теряли бы паспорта
    private string? FindSubagentsDir(string root, string flat)
    {
        var byConvention = Path.Combine(root, flat, _claudeSessionId, "subagents");
        if (Directory.Exists(byConvention)) return byConvention;

        foreach (var projDir in Directory.GetDirectories(root))
        {
            var candidate = Path.Combine(projDir, _claudeSessionId, "subagents");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    public void Dispose()
    {
        if (IsDisposed) return;

        // Финальное дренирование — дочитываем хвост транскрипта перед эмиссией паспортов
        try { DrainAsync().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SubagentWatcher] Финальный дренаж не удался: {ex.Message}");
        }

        IsDisposed = true;
        _cts.Cancel();

        // Паспорта агентов, чьё завершение продукт так и не объявил (прогон умер, ход
        // закончился раньше агента): без них самый интересный класс обрывов был бы не виден.
        // Хвост транскрипта тут уже дочитан — финальный дренаж выше.
        // Ждём конца возможного скана: те же словари пишет поток поллинга (лок берём ПОСЛЕ
        // отмены — иначе ждали бы полный тик). Не дождались — паспорта пропускаем, ронять
        // остановку ватчера из-за диагностики нельзя.
        // Только агенты с новыми строками при этом ватчере: старые файлы без новой
        // активности молчат — их содержимое уже в истории прошлого хода
        if (_scanLock.Wait(TimeSpan.FromSeconds(2)))
            try
            {
                foreach (var file in _tallies.Keys.ToList())
                {
                    // Только агенты с новыми строками при этом ватчере: старые файлы без новой
                    // активности молчат — их содержимое уже в истории прошлого хода
                    if (!_tallies[file].FeedCalled) continue;
                    // Агент, чью перепроверку оборвала остановка ватчера (ход кончился раньше
                    // BgDoneRecheckDelay), закрыт продуктом как bg_done — так и отдаём: run_end
                    // для SessionManager не фоновый, и реальный обрыв остался бы без добивания,
                    // а конец хода его уже не разберёт. Не-bg_done ветка берёт настраиваемую
                    // причину: "run_end" по умолчанию, "interrupted" если ClaudeSession пометил
                    // прогон как убитый пользовательским прерыванием хода.
                    Emit(file, _bgRechecks.Remove(file) ? "bg_done" : RunEndReason);
                }
            }
            finally { _scanLock.Release(); }
        _cts.Dispose();
    }
}
