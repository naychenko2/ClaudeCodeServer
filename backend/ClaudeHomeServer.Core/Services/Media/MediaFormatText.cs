using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Services.Media;

// Тексты формата для агента (строка «Формат:» хвоста хода, context_state, тихая строка ленты): одни
// формулировки на картинки и видео. Для человека подписи собирает интерфейс из ResolvedFormat.
public static class MediaFormatText
{
    // Желание: «16:9 · Высокое», «1920×1080 точно», «как у модели»
    public static string Wish(MediaFormat? wish) => wish switch
    {
        null => "как у модели",
        { Mode: FormatModes.Source } => "как у исходника",
        { Mode: FormatModes.Exact, Width: { } w, Height: { } h } => $"{w}×{h} точно",
        { Mode: FormatModes.Aspect, Aspect: { } a, Tier: { } t } => $"{a} · {MediaFormatResolver.TierLabel(t)}",
        { Mode: FormatModes.Aspect, Aspect: { } a } => a,
        // Желание «пропорции» без пропорций валидация отклоняет на входе; здесь оно не маскируется под «как у модели»
        { Mode: FormatModes.Aspect } => "пропорции не заданы",
        _ => "как у модели",
    };

    // Действующий формат: то, что уйдёт в модель, «16:9 · Высокое (1792×1024)»
    public static string Resolved(ResolvedFormat resolved)
    {
        var size = resolved is { Width: { } w, Height: { } h } ? $" ({w}×{h})" : "";
        return resolved.Mode switch
        {
            FormatModes.Source => "как у исходника",
            FormatModes.Exact => $"{resolved.Width}×{resolved.Height} точно",
            FormatModes.Aspect => (resolved.Aspect, resolved.Tier) switch
            {
                ({ } a, { } t) => $"{a} · {MediaFormatResolver.TierLabel(t)}{size}",
                ({ } a, null) => a + size,
                _ => "как у модели",
            },
            _ => "как у модели",
        };
    }

    // Строка хвоста после «Формат: »: итог, чей выбор и подмена под модель.
    // modelChosen: модель известна (выбрана или подобрана); без неё в генерацию не уходит ничего, а у модели без размеров
    // драйвер шлёт только пропорции (fal и Higgsfield — aspect_ratio)
    public static string Line(MediaFormat? wish, ResolvedFormat resolved, bool modelChosen = true)
    {
        if (wish is null || wish.Mode == FormatModes.Model)
            return "как у модели — не задан; не передавай aspect/quality/width/height без просьбы человека";
        var who = wish.By == ContextActor.Agent ? "✦ поставил Claude" : "выбор человека";
        // Resolve вернул «как у модели», но желание задано — его и называем, с честной оговоркой по случаю
        var shown = resolved.Mode != FormatModes.Model ? Resolved(resolved)
            : Wish(wish) + (modelChosen
                ? " (модель сообщает только пропорции — ступень и точный размер не учтутся)"
                : " (модель не выбрана — формат применится, когда выберете модель)");
        var text = $"{shown} — {who}";
        if (resolved.Substituted)
            text += $"; желание {Wish(wish)}, модель его не умеет: {resolved.Note}";
        return text;
    }
}
