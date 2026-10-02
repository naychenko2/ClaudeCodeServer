using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Срабатывание нормализатора: какой инструмент и какие поля развёрнуты — для лога шлюза.
public sealed record ToolInputFix(string ToolName, IReadOnlyList<string> Paths);

// Нормализация tool_use.input в ответе Messages API провайдера с флагом
// NormalizeToolInputArrays (см. ToolInputNormalizer). Два режима ответа:
//
// - SSE (stream=true): поток режется на события по пустой строке. Копятся ТОЛЬКО события
//   content_block_delta/input_json_delta блоков, открытых content_block_start с type=tool_use,
//   до их content_block_stop: частичный JSON нельзя чинить по кускам — обёртка
//   {"item":[ разрезается между дельтами. На stop склеенный input разбирается, нормализуется
//   и уходит одной дельтой перед stop. Нечего править — уходят исходные байты дельт, как
//   пришли. Текст, thinking и служебные события идут клиенту сразу, без задержки (сторож
//   Sse_БезБуферизации в тестах шлюза);
// - JSON (stream=false): content[].input правится в разобранном теле целиком.
//
// Сбой разбора — всегда исходные байты: фильтр не имеет права сломать ответ хуже провайдера.
public sealed class ToolInputResponseFilter(IReadOnlyDictionary<string, JsonNode?> schemas, Action<ToolInputFix> onFix)
{
    private sealed class Block(string name)
    {
        public string Name { get; } = name;
        public StringBuilder Json { get; } = new();
        public List<byte[]> Raw { get; } = [];
    }


    private readonly Dictionary<int, Block> _open = [];
    private byte[] _pending = [];

    // Кусок потока upstream. В output — всё, что уже можно отдать клиенту.
    public void Push(ReadOnlySpan<byte> chunk, IBufferWriter<byte> output)
    {
        ReadOnlySpan<byte> data = _pending.Length == 0 ? chunk : [.. _pending, .. chunk.ToArray()];
        var consumed = 0;
        while (EventEnd(data[consumed..]) is var end and > 0)
        {
            HandleEvent(data.Slice(consumed, end), output);
            consumed += end;
        }
        _pending = data[consumed..].ToArray();
    }

    // Конец потока: недописанное событие и незакрытые блоки уходят как пришли.
    public void Complete(IBufferWriter<byte> output)
    {
        foreach (var (_, block) in _open.OrderBy(b => b.Key))
            foreach (var raw in block.Raw) output.Write(raw);
        _open.Clear();
        output.Write(_pending);
        _pending = [];
    }

    // Длина первого полного события с завершающей пустой строкой; 0 — событие не дописано.
    // Разделитель строк — \n или \r\n (формат Anthropic — \n, \r\n терпим).
    private static int EventEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            var next = i + 1;
            if (next < data.Length && data[next] == (byte)'\n') return next + 1;
            if (next + 1 < data.Length && data[next] == (byte)'\r' && data[next + 1] == (byte)'\n') return next + 2;
        }
        return 0;
    }

    private void HandleEvent(ReadOnlySpan<byte> evt, IBufferWriter<byte> output)
    {
        // Дешёвый отсев: текстовые и служебные события не разбираем вовсе
        if (evt.IndexOf("content_block_start"u8) < 0 && evt.IndexOf("input_json_delta"u8) < 0 && evt.IndexOf("content_block_stop"u8) < 0)
        {
            output.Write(evt);
            return;
        }
        var root = ParseData(evt);
        var type = Str(root?["type"]);
        var index = root?["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : -1;

        if (type == "content_block_start" && root!["content_block"] is JsonObject cb
            && Str(cb["type"]) == "tool_use")
        {
            _open[index] = new Block(Str(cb["name"]) ?? "");
        }
        else if (type == "content_block_delta" && _open.TryGetValue(index, out var block)
            && root!["delta"] is JsonObject delta && Str(delta["type"]) == "input_json_delta")
        {
            block.Json.Append(Str(delta["partial_json"]));
            block.Raw.Add(evt.ToArray());
            return;
        }
        else if (type == "content_block_stop" && _open.Remove(index, out var done))
        {
            Finish(done, index, output);
        }
        output.Write(evt);
    }

    private void Finish(Block block, int index, IBufferWriter<byte> output)
    {
        var fixedJson = NormalizeInput(block.Name, block.Json.ToString());
        if (fixedJson is null)
        {
            foreach (var raw in block.Raw) output.Write(raw);
            return;
        }
        var payload = JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index,
            delta = new { type = "input_json_delta", partial_json = fixedJson },
        });
        output.Write(Encoding.UTF8.GetBytes($"event: content_block_delta\ndata: {payload}\n\n"));
    }

    // null — править нечего (или не разобралось): отдаём исходное.
    private string? NormalizeInput(string toolName, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonNode? input;
        try { input = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        var changed = new List<string>();
        var result = ToolInputNormalizer.Normalize(input, schemas.GetValueOrDefault(toolName), changed);
        if (changed.Count == 0) return null;
        onFix(new ToolInputFix(toolName, changed));
        return result?.ToJsonString() ?? "null";
    }


    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // JSON полей data: события (многострочное data склеивается через \n, как в спецификации SSE).
    private static JsonNode? ParseData(ReadOnlySpan<byte> evt)
    {
        var sb = new StringBuilder();
        foreach (var line in Encoding.UTF8.GetString(evt).Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (!l.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l.AsSpan(l.Length > 5 && l[5] == ' ' ? 6 : 5));
        }
        try { return sb.Length == 0 ? null : JsonNode.Parse(sb.ToString()); }
        catch (JsonException) { return null; }
    }

    // Не-потоковый ответ: правит content[].input у tool_use. null — ничего не менялось.
    public static byte[]? NormalizeMessage(byte[] body, IReadOnlyDictionary<string, JsonNode?> schemas, Action<ToolInputFix> onFix)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return null; }
        if (root?["content"] is not JsonArray content) return null;
        var any = false;
        foreach (var item in content)
        {
            if (item is not JsonObject block || Str(block["type"]) != "tool_use") continue;
            var name = Str(block["name"]) ?? "";
            var changed = new List<string>();
            var result = ToolInputNormalizer.Normalize(block["input"], schemas.GetValueOrDefault(name), changed);
            if (changed.Count == 0) continue;
            if (!ReferenceEquals(result, block["input"])) block["input"] = result;
            onFix(new ToolInputFix(name, changed));
            any = true;
        }
        return any ? JsonSerializer.SerializeToUtf8Bytes(root) : null;
    }

    // Схемы инструментов из тела запроса Messages API: tools[].name → input_schema.
    // Не JSON или инструментов нет — пусто (нормализация идёт по эвристике).
    public static IReadOnlyDictionary<string, JsonNode?> ToolSchemas(byte[]? requestBody)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (requestBody is not { Length: > 0 }) return result;
        try
        {
            if (JsonNode.Parse(requestBody)?["tools"] is not JsonArray tools) return result;
            foreach (var t in tools)
                if (t?["name"] is JsonValue n && n.TryGetValue<string>(out var name))
                    result[name] = t["input_schema"]?.DeepClone();
        }
        catch (JsonException) { }
        return result;
    }
}
