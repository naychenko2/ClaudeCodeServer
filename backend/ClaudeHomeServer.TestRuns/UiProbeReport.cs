using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.TestRuns;

// Итог замера интерфейса (ui_probe): только числа и селекторы по пяти классам находок, без
// оценок и скриншотов. Скрипт отдаёт СЫРЫЕ измерения всех интерактивных элементов, а порог
// применяется здесь — параметром вызова, а не константой скрипта или проекта.
public static class UiProbeReport
{
    public const int DefaultMinTargetPx = 40;
    public static readonly int[] DefaultViewports = [360, 1280];

    // Строка с JSON-итогом скрипта среди прочего вывода процесса; null — скрипт ничего не напечатал
    public static string? FindResultLine(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith(UiProbeScript.ResultMarker, StringComparison.Ordinal))
            ?[UiProbeScript.ResultMarker.Length..];

    // Сырой вывод скрипта → итог для модели. Битый JSON — FormatException с понятным текстом
    public static JsonObject Build(string scriptJson, string url, int minTargetPx)
    {
        JsonNode? raw;
        try { raw = JsonNode.Parse(scriptJson); }
        catch (JsonException e) { throw new FormatException("Скрипт замера вернул не JSON: " + e.Message, e); }
        if (raw is not JsonObject root || root["viewports"] is not JsonArray viewports)
            throw new FormatException("Вывод скрипта замера без поля viewports.");

        var result = new JsonObject
        {
            ["url"] = url,
            ["minTargetPx"] = minTargetPx,
            ["auth"] = root["auth"]?.GetValue<string>() ?? "skipped",
        };
        var perViewport = new JsonArray();
        var totals = new JsonObject
        {
            ["smallTargets"] = 0, ["horizontalOverflow"] = 0, ["clippedText"] = 0,
            ["consoleErrors"] = 0, ["failedRequests"] = 0, ["unnamedInteractive"] = 0,
        };
        foreach (var node in viewports)
        {
            if (node is not JsonObject vp) continue;
            var item = BuildViewport(vp, minTargetPx);
            perViewport.Add(item);
            foreach (var key in new[] { "smallTargets", "horizontalOverflow", "clippedText", "consoleErrors",
                         "failedRequests", "unnamedInteractive" })
                totals[key] = totals[key]!.GetValue<int>() + item["counts"]![key]!.GetValue<int>();
        }
        result["viewports"] = perViewport;
        result["totals"] = totals;
        return result;
    }

    private static JsonObject BuildViewport(JsonObject vp, int min)
    {
        var width = Int(vp["width"]);
        var interactive = Objects(vp["interactive"]).ToList();
        // «Любая сторона меньше порога»: узкая, но длинная кнопка тоже мишень, в которую не попасть
        var small = new JsonArray(interactive
            .Where(e => Num(e["w"]) < min || Num(e["h"]) < min)
            .Select(e => (JsonNode)new JsonObject
            {
                ["selector"] = Str(e["s"]), ["width"] = Num(e["w"]), ["height"] = Num(e["h"]), ["screenWidth"] = width,
            }).ToArray());
        var unnamed = new JsonArray(interactive
            .Where(e => e["named"] is JsonValue v && v.TryGetValue<bool>(out var named) && !named)
            .Select(e => (JsonNode)new JsonObject { ["selector"] = Str(e["s"]), ["width"] = Num(e["w"]), ["height"] = Num(e["h"]) })
            .ToArray());

        var doc = vp["doc"] as JsonObject;
        var docOverflows = doc is not null && Int(doc["scrollWidth"]) > Int(doc["clientWidth"]);
        var containers = new JsonArray(Objects(vp["containers"])
            .Where(c => Int(c["scrollWidth"]) > Int(c["clientWidth"]))
            .Select(c => (JsonNode)new JsonObject
            {
                ["selector"] = Str(c["s"]), ["scrollWidth"] = Int(c["scrollWidth"]), ["clientWidth"] = Int(c["clientWidth"]),
                ["overflowX"] = Str(c["overflowX"]),
            }).ToArray());
        var overflow = new JsonObject
        {
            ["document"] = docOverflows
                ? new JsonObject { ["scrollWidth"] = Int(doc!["scrollWidth"]), ["clientWidth"] = Int(doc["clientWidth"]) }
                : null,
            ["containers"] = containers,
        };
        var clipped = new JsonArray(Objects(vp["clipped"]).Select(c => (JsonNode)new JsonObject
        {
            ["selector"] = Str(c["s"]),
            ["scrollWidth"] = Int(c["scrollWidth"]), ["clientWidth"] = Int(c["clientWidth"]),
            ["scrollHeight"] = Int(c["scrollHeight"]), ["clientHeight"] = Int(c["clientHeight"]),
        }).ToArray());
        var console = new JsonArray(Objects(vp["console"]).Select(c => (JsonNode)new JsonObject
            { ["type"] = Str(c["type"]), ["text"] = Str(c["text"]) }).ToArray());
        var failed = new JsonArray(Objects(vp["failed"]).Select(f =>
        {
            var o = new JsonObject { ["url"] = Str(f["url"]) };
            if (f["status"] is not null) o["status"] = Int(f["status"]);
            if (f["failure"] is not null) o["failure"] = Str(f["failure"]);
            return (JsonNode)o;
        }).ToArray());

        var counts = new JsonObject
        {
            ["smallTargets"] = small.Count,
            ["horizontalOverflow"] = containers.Count + (docOverflows ? 1 : 0),
            ["clippedText"] = clipped.Count,
            ["consoleErrors"] = console.Count,
            ["failedRequests"] = failed.Count,
            ["unnamedInteractive"] = unnamed.Count,
        };
        var item = new JsonObject
        {
            ["width"] = width,
            ["status"] = vp["status"] is { } st ? Int(st) : null,
            ["interactiveTotal"] = interactive.Count,
            ["counts"] = counts,
            ["smallTargets"] = small,
            ["horizontalOverflow"] = overflow,
            ["clippedText"] = clipped,
            ["consoleErrors"] = console,
            ["failedRequests"] = failed,
            ["unnamedInteractive"] = unnamed,
        };
        if (vp["error"] is { } error) item["error"] = Str(error);
        return item;
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    private static double Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static int Int(JsonNode? node) => (int)Math.Round(Num(node));

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
