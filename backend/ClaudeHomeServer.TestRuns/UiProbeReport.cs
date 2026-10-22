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

    // Потолок элементов в каждом списке по ступеням: итог не длиннее maxBytes (как сводка run_tests),
    // остальное — счётчиком «omitted»; ступень 0 оставляет одни числа
    private static readonly int[] ListLimits = [int.MaxValue, 100, 40, 20, 10, 5, 2, 0];

    // Отказ по выводу скрипта, когда измерять было нечего или измеренное — не та страница: стенд
    // не виден из среды процесса, вход не удался (страница логина дала бы ложное «находок нет»),
    // страница перенаправила на вход. null — замер годен
    public static string? RefusalOf(string scriptJson, string requestedUrl, string origin)
    {
        JsonNode? raw;
        try { raw = JsonNode.Parse(scriptJson); }
        catch (JsonException) { return null; }
        if (raw is not JsonObject root) return null;
        if (Str(root["reach"]) is { Length: > 0 } reach && reach != "ok")
            return $"Стенд {origin} недостижим из среды проекта ({reach}) — ui_probe только читает уже поднятый "
                + "стенд. Для проекта в песочнице стенд хоста по loopback не виден: попроси поднять стенд, "
                + "доступный из среды проекта, и повтори вызов.";
        var auth = Str(root["auth"]);
        if (auth is not ("ok" or "skipped" or ""))
            return $"Не удалось войти на стенд (auth: {auth}) — передай user/password; без входа замерялась бы "
                + "страница логина.";
        var requested = Normalize(requestedUrl);
        foreach (var vp in Objects(root["viewports"]))
        {
            var final = Str(vp["finalUrl"]);
            if (final.Length == 0 || Normalize(final) == requested) continue;
            if (IsLoginUrl(final) && !IsLoginUrl(requestedUrl))
                return $"Страница {requestedUrl} перенаправила на вход ({final}) — вход не действует, "
                    + "проверь user/password.";
        }
        return null;
    }

    // Один формат сериализации и для замера размера, и для ответа: кириллица не раздувается в \uXXXX
    public static string Serialize(JsonObject report) => report.ToJsonString(Compact);

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Normalize(string url) => url.TrimEnd('/');

    private static bool IsLoginUrl(string url) =>
        url.Contains("login", StringComparison.OrdinalIgnoreCase) || url.Contains("signin", StringComparison.OrdinalIgnoreCase);

    // Сырой вывод скрипта → итог для модели под потолком maxBytes. Битый JSON — FormatException
    public static JsonObject Build(string scriptJson, string url, int minTargetPx, int maxBytes = int.MaxValue)
    {
        JsonObject result = null!;
        foreach (var limit in ListLimits)
        {
            result = BuildLimited(scriptJson, url, minTargetPx, limit);
            if (System.Text.Encoding.UTF8.GetByteCount(Serialize(result)) <= maxBytes) break;
        }
        return result;
    }

    private static JsonObject BuildLimited(string scriptJson, string url, int minTargetPx, int listLimit)
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
            var item = BuildViewport(vp, minTargetPx, listLimit);
            perViewport.Add(item);
            foreach (var key in new[] { "smallTargets", "horizontalOverflow", "clippedText", "consoleErrors",
                         "failedRequests", "unnamedInteractive" })
                totals[key] = totals[key]!.GetValue<int>() + item["counts"]![key]!.GetValue<int>();
        }
        result["viewports"] = perViewport;
        result["totals"] = totals;
        // Скрипт копит не больше MaxItems записей на список: счётчики выше нижние оценки
        result["truncated"] = perViewport.Any(v => v!["truncated"]!.GetValue<bool>());
        return result;
    }

    private static JsonObject BuildViewport(JsonObject vp, int min, int listLimit)
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
        // Потолок размера итога: списки режутся до listLimit, сколько не вошло — в omitted
        var omitted = new JsonObject();
        JsonArray Cap(string name, JsonArray list)
        {
            if (list.Count <= listLimit) return list;
            omitted[name] = list.Count - listLimit;
            return new JsonArray(list.Take(listLimit).Select(n => n!.DeepClone()).ToArray());
        }
        var scriptTruncated = Strings(vp["truncated"]).Concat(Strings(vp["pageTruncated"])).Distinct().ToList();
        var item = new JsonObject
        {
            ["width"] = width,
            ["status"] = vp["status"] is { } st ? Int(st) : null,
            ["finalUrl"] = Str(vp["finalUrl"]),
            ["interactiveTotal"] = interactive.Count,
            ["truncated"] = scriptTruncated.Count > 0,
            ["counts"] = counts,
            ["smallTargets"] = Cap("smallTargets", small),
            ["horizontalOverflow"] = overflow,
            ["clippedText"] = Cap("clippedText", clipped),
            ["consoleErrors"] = Cap("consoleErrors", console),
            ["failedRequests"] = Cap("failedRequests", failed),
            ["unnamedInteractive"] = Cap("unnamedInteractive", unnamed),
        };
        overflow["containers"] = Cap("overflowContainers", containers);
        if (omitted.Count > 0) item["omitted"] = omitted;
        if (vp["error"] is { } error) item["error"] = Str(error);
        return item;
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str) : [];

    private static IEnumerable<JsonObject> Objects(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    private static double Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static int Int(JsonNode? node) => (int)Math.Round(Num(node));

    private static string Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
