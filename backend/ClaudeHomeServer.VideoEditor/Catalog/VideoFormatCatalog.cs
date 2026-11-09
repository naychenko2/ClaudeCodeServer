using System.Globalization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.VideoEditor.Catalog;

// Ступени «Формата» (ADR-025) для видео: таблицы по драйверам и разбор enum `resolution` живой схемы.
// Арифметика «ближайшее/серая» — только MediaFormatResolver; здесь лишь данные каталога.
public static class VideoFormatCatalog
{
    // Пропорции, у которых размер ступени считается из числа в подписи («1080p» → 1920×1080)
    private static readonly Regex ShortSide = new(@"^(\d{3,4})p$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Размеры «Np» по пропорциям: короткая сторона N, длинная — по пропорции, чётная
    public static IReadOnlyDictionary<string, FormatSize> SizesForShortSide(IEnumerable<string> aspects, int shortSide)
    {
        var sizes = new Dictionary<string, FormatSize>(StringComparer.Ordinal);
        foreach (var aspect in aspects)
        {
            if (!AspectCrop.TryParse(aspect, out var ratio)) continue;
            var longSide = (int)Math.Round(shortSide * Math.Max(ratio, 1 / ratio) / 2) * 2;
            sizes[aspect] = ratio >= 1 ? new FormatSize(longSide, shortSide) : new FormatSize(shortSide, longSide);
        }
        return sizes;
    }

    // Ступени из enum resolution схемы модели: по возрастанию числа в подписи; до трёх значений — по порядку
    // (Стандарт, Высокое, Макс), больше — низшее, среднее и высшее. Значения без числа в начале («auto») пропускаются.
    // WireValue — значение enum как есть («1080p», «768P»)
    public static IReadOnlyList<FormatTierSpec> TiersFromOptions(IReadOnlyList<string> aspects, IEnumerable<string> options)
    {
        var parsed = options
            .Select(o => (Wire: o, Weight: WeightOf(o)))
            .Where(p => p.Weight is not null)
            .OrderBy(p => p.Weight)
            .ToList();
        if (parsed.Count == 0) return [];
        var picked = parsed.Count <= 3
            ? parsed
            : [parsed[0], parsed[parsed.Count / 2], parsed[^1]];
        FormatTier[] order = [FormatTier.Standard, FormatTier.High, FormatTier.Max];
        return [.. picked.Select((p, i) =>
        {
            var m = ShortSide.Match(p.Wire.Trim());
            var sizes = m.Success && aspects.Count > 0
                ? SizesForShortSide(aspects, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                : new Dictionary<string, FormatSize>();
            return new FormatTierSpec(order[i], p.Wire.Trim(), sizes, p.Wire.Trim());
        })];
    }

    // «480P» → 480, «2K» → 2000, «4K» → 4000: порядок по возрастанию
    private static double? WeightOf(string option)
    {
        var text = option.Trim();
        if (text.Length == 0) return null;
        var m = Regex.Match(text, @"^(\d+(?:\.\d+)?)\s*([pPkK])$");
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return char.ToLowerInvariant(m.Groups[2].Value[0]) == 'k' ? n * 1000 : n;
    }

    // Ступень, когда желание её не называет (aspect без tier — старый клиент, агент): прежнее умолчание драйвера.
    // У local H3 это full (768p) — раньше съёмка всегда шла full; у остальных — «Стандарт» (родное умолчание fal и Higgsfield)
    // У fal H3 «Стандарт» — родные 480P, а прежнее умолчание — 768P («Высокое»)
    public static FormatTier DefaultTier(string provider, string modelId) =>
        (provider == "local" && modelId == "minimax-h3") || (provider == "fal" && modelId == FalVideoCatalog.MiniMaxH3)
            ? FormatTier.High : FormatTier.Standard;

    // Пропорции для формата: без «auto» (авто-режим модели не пропорция формата)
    public static IReadOnlyList<string> RealAspects(IEnumerable<string> aspects) =>
        [.. aspects.Where(a => AspectCrop.TryParse(a, out _))];

    // ── local MiniMax H3 ─────────────────────────────────────────────────────────

    // 480p — half, 768p — full, 1440p — съёмка full и латентный апскейл на своей GPU (ADR-025, решение 1).
    // У портрета стороны меняются местами; «Макс» не серая благодаря PostStep
    public static MediaFormatCaps LocalH3(IReadOnlyList<string> aspects) => new(
        "MiniMax H3",
        aspects,
        [
            new FormatTierSpec(FormatTier.Standard, "480p", Sizes(aspects, 864, 480), "half"),
            new FormatTierSpec(FormatTier.High, "768p", Sizes(aspects, 1344, 768), "full"),
            new FormatTierSpec(FormatTier.Max, "1440p", Sizes(aspects, 2528, 1440), "full", PostStep: FormatPostSteps.Upscale),
        ],
        Exact: null,
        WireParam: "size");

    private static IReadOnlyDictionary<string, FormatSize> Sizes(IReadOnlyList<string> aspects, int w, int h)
    {
        var sizes = new Dictionary<string, FormatSize>(StringComparer.Ordinal);
        foreach (var aspect in aspects)
            sizes[aspect] = aspect switch
            {
                "16:9" => new FormatSize(w, h),
                "9:16" => new FormatSize(h, w),
                _ => new FormatSize(w, h),
            };
        return sizes;
    }
}
