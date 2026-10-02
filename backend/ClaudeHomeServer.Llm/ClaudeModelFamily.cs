using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Llm;

// Семейство моделей родного Claude — единственный источник правды о том, какие модели Claude
// мы знаем. Храним ТОЛЬКО семейство (opus/fable/sonnet/haiku): версию CLI выбирает сам по
// алиасу (opus → последняя Opus), окно 1M дописывает сервер при сборке --model
// (ClaudeSubscriptionPool.LaunchModel). Номер версии или суффикс [1m] в сохранённом значении
// прибивает чат к устаревшей модели — каталог CLI отдаёт часть пунктов версионными id.
//
// Supports1M — у семейства есть окно 1M (актуальные opus-5-5, fable-5-1, sonnet-5 объявлены
// в реестре моделей CLI 2.1.283 с window 1e6; haiku-4-5 — 200k).
public sealed record ClaudeModelFamily(string Alias, string DisplayName, bool Supports1M)
{
    public const string WindowSuffix = "[1m]";

    public static readonly IReadOnlyList<ClaudeModelFamily> All =
    [
        new("opus", "Opus", Supports1M: true),
        new("fable", "Fable", Supports1M: true),
        new("sonnet", "Sonnet", Supports1M: true),
        new("haiku", "Haiku", Supports1M: false),
    ];

    // Алиас с окном: «opus[1m]».
    public string WindowAlias => Alias + WindowSuffix;

    // Полный id Anthropic: claude-opus-4-8, claude-3-5-sonnet-20241022, claude-fable-5-1[1m].
    private static readonly Regex FullId = new(
        @"^claude-(?:\d+-)*(?<family>[a-z]+)(?:-[\w.-]*)?(?:\[1m\])?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Голый алиас семейства («opus»), без суффикса окна.
    public static ClaudeModelFamily? FromAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return All.FirstOrDefault(f => f.Alias.Equals(v, StringComparison.OrdinalIgnoreCase));
    }

    // Алиас семейства с окном или без («opus», «opus[1m]»).
    public static ClaudeModelFamily? FromAliasOrWindowAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.EndsWith(WindowSuffix, StringComparison.OrdinalIgnoreCase)) v = v[..^WindowSuffix.Length];
        return FromAlias(v);
    }

    // Семейство по любой форме id: алиас, алиас с окном, полный id claude-* с версией и окном.
    // null — не модель Claude известного семейства (default, сторонний id, claude-mythos-*).
    public static ClaudeModelFamily? Resolve(string? value)
    {
        if (FromAliasOrWindowAlias(value) is { } byAlias) return byAlias;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = FullId.Match(value.Trim());
        return match.Success ? FromAlias(match.Groups["family"].Value) : null;
    }

    // Свести значение модели к семейству. Идемпотентна; пусто, default и всё, что не узнано
    // как семейство Claude, возвращается как есть. Сторонний провайдер, объявивший id вида
    // claude-*, этой функции не виден — на записи зови LlmProviderRegistry.CanonicalizeModel,
    // он сперва спрашивает ResolveByModel.
    public static string? Canonicalize(string? value) =>
        Resolve(value) is { } family ? family.Alias : value;
}
