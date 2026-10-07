using System.Globalization;

namespace ClaudeHomeServer.Services.Media;

// Единственная арифметика формата (ADR-025 §1, инвариант «своей арифметики нет»): ближайшая
// пропорция и ступень, округление «Точно…», серые ступени с причиной, фразы Note. Драйверы, ручка
// format, котировка и фронт берут готовое отсюда; фронт кратность и «ближайшее» не считает.
public static class MediaFormatResolver
{
    private static readonly FormatTier[] TierOrder = [FormatTier.Standard, FormatTier.High, FormatTier.Max];

    public static string TierLabel(FormatTier tier) => tier switch
    {
        FormatTier.Standard => "Стандарт",
        FormatTier.High => "Высокое",
        _ => "Макс",
    };

    // Действующий формат под модель. caps == null — модель размеров не сообщает: «Как у модели»;
    // wish == null — формат не задан (то же). Желание не меняется — подмену показывает только результат
    public static ResolvedFormat Resolve(MediaFormatCaps? caps, MediaFormat? wish)
    {
        if (wish is null || wish.Mode == FormatModes.Model || caps is null)
            return Plain(FormatModes.Model);
        if (wish.Mode == FormatModes.Source)
            return Plain(FormatModes.Source);

        return wish.Mode == FormatModes.Exact && wish.Width is > 0 && wish.Height is > 0
            ? ResolveExact(caps, wish)
            : ResolveAspect(caps, wish.Aspect, wish.Tier ?? FormatTier.Standard);
    }

    // Состояние для ручки format без цен (цены кладёт вызывающий: он знает котировку драйвера)
    public static MediaFormatState State(MediaFormatCaps? caps, MediaFormat? wish)
    {
        var resolved = Resolve(caps, wish);
        return new MediaFormatState(
            caps,
            wish,
            resolved,
            caps is null ? [] : TierOptions(caps, resolved.Aspect),
            caps?.Exact,
            caps is { Exact: null } ? ExactUnavailable(caps) : null);
    }

    // Три строки ступеней: у модели нет ступени — серая с причиной. Size — под пропорцию, если драйвер его знает
    public static IReadOnlyList<FormatTierOption> TierOptions(MediaFormatCaps caps, string? aspect)
    {
        var absent = TierOrder.Where(t => caps.Tiers.All(s => s.Tier != t)).ToList();
        var result = new List<FormatTierOption>();
        foreach (var tier in TierOrder)
        {
            var spec = caps.Tiers.FirstOrDefault(s => s.Tier == tier);
            if (spec is null)
            {
                result.Add(new FormatTierOption(tier, "нет у модели", null, null, null, null, AbsentReason(caps, tier, absent)));
                continue;
            }
            FormatSize? size = aspect is not null && spec.Sizes.TryGetValue(aspect, out var s) ? s : null;
            result.Add(new FormatTierOption(tier, spec.Label, size, spec.WireValue, spec.PostStep, null, null));
        }
        return result;
    }

    private static string AbsentReason(MediaFormatCaps caps, FormatTier tier, List<FormatTier> absent)
    {
        if (caps.NativeNote is null) return $"«{TierLabel(tier)}» нет у модели";
        var quoted = Join(absent.Select(t => $"«{TierLabel(t)}»"));
        return $"Модель рисует {caps.NativeNote} — {quoted} у неё {(absent.Count == 1 ? "недоступна" : "недоступны")}";
    }

    private static string ExactUnavailable(MediaFormatCaps caps)
    {
        var sizes = caps.Tiers.SelectMany(t => t.Sizes.Values).Distinct().ToList();
        if (sizes.Count == 0) return $"«Точно…» недоступно: {caps.ModelLabel} не принимает произвольный размер.";
        var count = sizes.Count switch { 1 => "один размер", 2 => "два размера", 3 => "три размера", 4 => "четыре размера", _ => $"{sizes.Count} размеров" };
        return $"«Точно…» недоступно: {caps.ModelLabel} рисует только {count} — {Join(sizes.Select(s => $"{s.W}×{s.H}"))}.";
    }

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list[..^1]) + " и " + list[^1];
    }

    private static ResolvedFormat Plain(string mode) => new(mode, null, null, null, null, null, null, false, false, null);

    private static ResolvedFormat ResolveAspect(MediaFormatCaps caps, string? wishAspect, FormatTier wishTier)
    {
        var substituted = false;
        var notes = new List<string>();

        var aspect = wishAspect;
        if (caps.Aspects.Count > 0)
        {
            if (aspect is null || !caps.Aspects.Contains(aspect))
            {
                var near = Nearest(caps.Aspects, aspect);
                if (aspect is not null && near is not null)
                {
                    notes.Add($"{aspect} нет у {caps.ModelLabel} — взяли {near}.");
                    substituted = true;
                }
                aspect = near;
            }
        }

        var spec = PickTier(caps, wishTier);
        FormatTier? tier = spec?.Tier;
        if (spec is not null && spec.Tier != wishTier)
        {
            notes.Add($"У {caps.ModelLabel} другие ступени: «{TierLabel(wishTier)}» — {spec.Label}.");
            substituted = true;
        }

        FormatSize? size = null;
        if (spec is not null && aspect is not null) spec.Sizes.TryGetValue(aspect, out size);

        return new ResolvedFormat(FormatModes.Aspect, aspect, tier, size?.W, size?.H, spec?.WireValue, spec?.PostStep,
            substituted, false, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    // Нужная ступень, иначе ближайшая ниже, иначе ближайшая выше; у модели без ступеней — null
    private static FormatTierSpec? PickTier(MediaFormatCaps caps, FormatTier wanted)
    {
        var exact = caps.Tiers.FirstOrDefault(s => s.Tier == wanted);
        if (exact is not null) return exact;
        return caps.Tiers.Where(s => s.Tier < wanted).OrderByDescending(s => s.Tier).FirstOrDefault()
               ?? caps.Tiers.Where(s => s.Tier > wanted).OrderBy(s => s.Tier).FirstOrDefault();
    }

    private static ResolvedFormat ResolveExact(MediaFormatCaps caps, MediaFormat wish)
    {
        int w = wish.Width!.Value, h = wish.Height!.Value;
        if (caps.Exact is not { } rules)
        {
            // Модель точный размер не умеет: ближайшая пропорция и та же ступень
            var near = Nearest(caps.Aspects, Reduce(w, h));
            var res = ResolveAspect(caps, near, wish.Tier ?? FormatTier.Standard);
            var phrase = $"Точный размер {w}×{h} {caps.ModelLabel} не умеет — взяли {near ?? "размер модели"}.";
            return res with { Substituted = true, Note = res.Note is null ? phrase : phrase + " " + res.Note };
        }

        var notes = new List<string>();
        double fw = w, fh = h;

        if (rules.MaxSide is { } maxSide && Math.Max(fw, fh) > maxSide)
        {
            var k = maxSide / Math.Max(fw, fh);
            fw *= k;
            fh *= k;
            notes.Add($"Модель рисует до {maxSide} px по длинной стороне");
        }
        if (Math.Min(fw, fh) < rules.MinSide)
        {
            var k = rules.MinSide / Math.Min(fw, fh);
            fw *= k;
            fh *= k;
            notes.Add($"Модель рисует от {rules.MinSide} px по короткой стороне");
        }
        if (rules.MaxMegapixels is { } mp && fw * fh > mp * 1_000_000)
        {
            var k = Math.Sqrt(mp * 1_000_000 / (fw * fh));
            fw *= k;
            fh *= k;
            notes.Add($"Модель рисует до {mp.ToString("0.#", CultureInfo.InvariantCulture)} Мп");
        }

        var rw = RoundToStep(fw, rules.Step, rules.MinSide);
        var rh = RoundToStep(fh, rules.Step, rules.MinSide);
        // Округление вверх не должно пробить потолок: опускаем до кратного вниз
        if (rules.MaxSide is { } ms)
        {
            if (rw > ms) rw = FloorToStep(ms, rules.Step);
            if (rh > ms) rh = FloorToStep(ms, rules.Step);
        }
        if (Math.Abs(fw - rw) > 0.5 || Math.Abs(fh - rh) > 0.5)
            notes.Add($"Модель рисует кратно {rules.Step}: {rw}×{rh}");

        var rounded = rw != w || rh != h;
        var tierSpec = PickTier(caps, wish.Tier ?? FormatTier.Standard);
        return new ResolvedFormat(FormatModes.Exact, null, tierSpec?.Tier, rw, rh, tierSpec?.WireValue, tierSpec?.PostStep,
            false, rounded, notes.Count == 0 ? null : string.Join("; ", notes) + ".");
    }

    private static int RoundToStep(double v, int step, int min)
    {
        var s = Math.Max(1, step);
        var r = (int)(Math.Round(v / s, MidpointRounding.AwayFromZero) * s);
        return Math.Max(r, Math.Max(s, CeilToStep(min, s)));
    }

    private static int FloorToStep(int v, int step) => Math.Max(step, v / step * step);

    private static int CeilToStep(int v, int step) => (v + step - 1) / step * step;

    // Ближайшая пропорция по |log(w/h) − log(w'/h')|; без желания — первая из списка
    private static string? Nearest(IReadOnlyList<string> aspects, string? wanted)
    {
        if (aspects.Count == 0) return null;
        if (!AspectCrop.TryParse(wanted, out var target)) return aspects[0];
        string? best = null;
        var bestD = double.MaxValue;
        foreach (var a in aspects)
        {
            if (!AspectCrop.TryParse(a, out var r)) continue;
            var d = Math.Abs(Math.Log(r) - Math.Log(target));
            if (d < bestD)
            {
                bestD = d;
                best = a;
            }
        }
        return best ?? aspects[0];
    }

    // 1920×1080 → «16:9»; не из известных малых пропорций — «W:H» по НОД
    private static string Reduce(int w, int h)
    {
        var g = Gcd(w, h);
        return $"{w / g}:{h / g}";
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
