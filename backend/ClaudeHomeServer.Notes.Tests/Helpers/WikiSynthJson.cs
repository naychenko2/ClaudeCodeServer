using System.Text.Json;

namespace ClaudeHomeServer.Tests.Helpers;

// Ответы фейковой модели в формате синтеза (WikiKeeperPrompts.Synth, S2-8b): один общий
// помощник для всех тестов разбора вместо прежних claims/operations[claim:int]. JSON строится
// сериализатором — кавычки и спецсимволы в утверждении и цитатах экранируются сами.
internal static class WikiSynthJson
{
    /// <summary>Одна операция ответа. kind/target/asOf == null — ключ не пишется (как у модели,
    /// которая его не прислала).</summary>
    public static Dictionary<string, object> Op(string op, string page, string claim,
        string[] quotes, string? kind = null, string? target = null, string? asOf = null)
    {
        var d = new Dictionary<string, object>
        {
            ["op"] = op, ["page"] = page, ["claim"] = claim, ["quotes"] = quotes,
        };
        if (kind is not null) d["kind"] = kind;
        if (target is not null) d["target"] = target;
        if (asOf is not null) d["as_of"] = asOf;
        return d;
    }

    /// <summary>Ответ «ok» с перечисленными операциями.</summary>
    public static string Ok(params Dictionary<string, object>[] ops) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["outcome"] = "ok", ["operations"] = ops });

    /// <summary>append на существующую страницу: утверждение своими словами, цитата — дословная.</summary>
    public static string Append(string page, string claim, string quote) =>
        Ok(Op("append", page, claim, [quote]));

    /// <summary>create новой страницы вида decision.</summary>
    public static string Create(string page, string claim, string quote, string kind = "decision",
        string? asOf = null) =>
        Ok(Op("create", page, claim, [quote], kind: kind, asOf: asOf));

    /// <summary>Штатный исход «нечего добавить».</summary>
    public static string Nothing(string reason = "не о чём писать") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
            { ["outcome"] = "nothing to add", ["reason"] = reason });
}
