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

    private readonly string _cwd;
    private readonly string _claudeSessionId;
    private readonly Func<ServerMessage, Task> _onMessage;
    private readonly string? _sessionId;
    private readonly Action<SubagentRunPassport>? _runSink;
    private readonly CancellationTokenSource _cts = new();
    // Смещение прочитанного по каждому файлу (продвигается только по целым строкам)
    private readonly Dictionary<string, long> _offsets = [];
    // toolUseId родителя по файлу транскрипта (из agent-*.meta.json; null — меты ещё нет)
    private readonly Dictionary<string, string?> _toolIdByFile = [];
    // Паспорт прогона по файлу транскрипта (копится по мере чтения)
    private readonly Dictionary<string, SubagentRunTally> _tallies = [];
    // Момент последней активности, с которым паспорт уже отдан наружу: агент, которого добили,
    // дописывает ТОТ ЖЕ транскрипт и завершается повторно — второй одинаковый паспорт не нужен
    private readonly Dictionary<string, DateTime> _reported = [];
    // Скан зовётся из цикла поллинга и из DrainAsync (перед tool_result) — не параллелим
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private string? _dir;               // папка subagents появляется при первом сабагенте
    private Task? _loop;

    public bool IsDisposed { get; private set; }

    // Тот же контекст наблюдения? Same-process ход (init повторяется в том же процессе)
    // переиспользует живой ватчер — пересоздание без дренажа теряло бы хвост транскриптов
    public bool Matches(string cwd, string claudeSessionId) =>
        !IsDisposed && _cwd == cwd && _claudeSessionId == claudeSessionId;

    public SubagentStreamWatcher(string cwd, string claudeSessionId, Func<ServerMessage, Task> onMessage,
        string? sessionId = null, Action<SubagentRunPassport>? runSink = null)
    {
        _cwd = cwd;
        _claudeSessionId = claudeSessionId;
        _onMessage = onMessage;
        _sessionId = sessionId;
        _runSink = runSink;
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
                    // Начало транскрипта не прочитано — паспорт такого агента считается неполным
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
                if (toolId is not null && toolUseIds.Contains(toolId)) Emit(file, finishedBy);
        }
        finally { _scanLock.Release(); }
    }

    // Паспорт наружу. Повторный вызов без новой активности агента ничего не шлёт: у фонового
    // агента завершение приходит и структурным событием, и текстовым, а добитый агент
    // завершается ещё раз тем же транскриптом.
    private void Emit(string file, string finishedBy)
    {
        if (_runSink is null || !_tallies.TryGetValue(file, out var tally)) return;
        if (_reported.TryGetValue(file, out var at) && at == tally.LastActivityAt) return;
        _reported[file] = tally.LastActivityAt;

        long size = 0;
        try { size = new FileInfo(file).Length; } catch (Exception) { /* файл мог исчезнуть */ }
        try { _runSink(tally.Build(_sessionId, finishedBy, size)); }
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
            _dir ??= ResolveDir();
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

    // Папка транскриптов сабагентов текущей сессии. Корни — как у workflow-транскриптов
    // (~/.claude/projects + профили сторонних провайдеров). Сначала — по соглашению CLI
    // об уплощении cwd (не-алфавитно-цифровые символы → '-'), затем фолбэк-скан по id сессии.
    private string? ResolveDir()
    {
        var flat = string.Concat(_cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        foreach (var root in WorkflowAgentParser.AllowedRoots)
        {
            if (!Directory.Exists(root)) continue;

            var byConvention = Path.Combine(root, flat, _claudeSessionId, "subagents");
            if (Directory.Exists(byConvention)) return byConvention;

            foreach (var projDir in Directory.GetDirectories(root))
            {
                var candidate = Path.Combine(projDir, _claudeSessionId, "subagents");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _cts.Cancel();
        // Паспорта агентов, чьё завершение продукт так и не объявил (прогон умер, ход
        // закончился раньше агента): без них самый интересный класс обрывов был бы не виден.
        // Хвост транскрипта тут уже дочитан — вызывающий дренирует ватчер перед Dispose.
        // Ждём конца возможного скана: те же словари пишет поток поллинга (лок берём ПОСЛЕ
        // отмены — иначе ждали бы полный тик). Не дождались — паспорта пропускаем, ронять
        // остановку ватчера из-за диагностики нельзя.
        if (_scanLock.Wait(TimeSpan.FromSeconds(2)))
            try { foreach (var file in _tallies.Keys.ToList()) Emit(file, "run_end"); }
            finally { _scanLock.Release(); }
        _cts.Dispose();
    }
}
