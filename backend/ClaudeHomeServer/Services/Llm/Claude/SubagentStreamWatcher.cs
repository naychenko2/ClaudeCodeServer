using System.Text.Json;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Полный поток inline-сабагентов (Task/Agent). CLI транслирует в stdout только их
// tool_use-блоки — text/thinking сабагента в стриме нет вовсе. Зато сабагент пишет
// собственный транскрипт <claude-projects>/<flat-cwd>/<sessionId>/subagents/agent-*.jsonl,
// а agent-*.meta.json рядом несёт toolUseId родительского вызова. Ватчер поллит эти файлы
// на протяжении хода и эмитит AgentTextMessage/AgentThinkingMessage(parentToolUseId, text).
// Файлы, существовавшие на старте хода, пропускаются целиком — их содержимое уже в истории.
internal sealed class SubagentStreamWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    private readonly string _cwd;
    private readonly string _claudeSessionId;
    private readonly Func<ServerMessage, Task> _onMessage;
    private readonly CancellationTokenSource _cts = new();
    // Смещение прочитанного по каждому файлу (продвигается только по целым строкам)
    private readonly Dictionary<string, long> _offsets = [];
    // toolUseId родителя по файлу транскрипта (из agent-*.meta.json; null — меты ещё нет)
    private readonly Dictionary<string, string?> _toolIdByFile = [];
    // Скан зовётся из цикла поллинга и из DrainAsync (перед tool_result) — не параллелим
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private string? _dir;               // папка subagents появляется при первом сабагенте
    private Task? _loop;

    public bool IsDisposed { get; private set; }

    public SubagentStreamWatcher(string cwd, string claudeSessionId, Func<ServerMessage, Task> onMessage)
    {
        _cwd = cwd;
        _claudeSessionId = claudeSessionId;
        _onMessage = onMessage;
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
                    _offsets[f] = new FileInfo(f).Length;
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
                    await ScanAsync();
                    await Task.Delay(PollInterval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { /* штатная остановка */ }
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

    private async Task ScanAsync()
    {
        await _scanLock.WaitAsync();
        try
        {
            _dir ??= ResolveDir();
            if (_dir is null) return;

            foreach (var file in Directory.GetFiles(_dir, "agent-*.jsonl", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                await ScanFileAsync(file);
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

        foreach (var line in chunk[..lastNewline].Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { await EmitBlocksAsync(line, toolId); }
            catch (JsonException) { /* битая строка — норма для дописываемого файла */ }
        }
    }

    // Блоки assistant-строки транскрипта: text/thinking эмитим, tool_use пропускаем —
    // их CLI уже транслирует в основной стрим с parent_tool_use_id
    private async Task EmitBlocksAsync(string line, string toolId)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
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
            return toolId;
        }
        catch (Exception)
        {
            return null; // мета дописывается — попробуем следующим тиком
        }
    }

    // Папка транскриптов сабагентов текущей сессии. Сначала — по соглашению CLI об
    // уплощении cwd (не-алфавитно-цифровые символы → '-'), затем фолбэк-скан по id сессии.
    private string? ResolveDir()
    {
        var root = WorkflowAgentParser.AllowedRoot;
        if (!Directory.Exists(root)) return null;

        var flat = string.Concat(_cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
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
        IsDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
