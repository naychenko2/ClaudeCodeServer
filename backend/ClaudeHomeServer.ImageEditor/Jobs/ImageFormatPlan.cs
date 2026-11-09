using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ImageEditor;

// Какой формат уходит в запуск (ADR-025): желание человека или агента превращается в действующий формат
// под модель. Единственное место, где операция решает, чем формат является: генерация — пропорции,
// ступень или «Точно…» модели; «Дорисовать» — пропорции холста; правка — «Как у исходника» (формат
// модель не меняет: пропорции — только явным «Дорисовать» или «Обрезать»); прочее формата не имеет.
public static class ImageFormatPlan
{
    public static ResolvedFormat? For(ImageEditOp op, MediaFormatCaps? caps, MediaFormat? wish) => op switch
    {
        ImageEditOp.Generate => MediaFormatResolver.Resolve(caps, wish),
        ImageEditOp.Outpaint when wish is { Mode: FormatModes.Aspect, Aspect: { } aspect } && AspectCrop.TryParse(aspect, out _) =>
            new ResolvedFormat(FormatModes.Aspect, aspect, null, null, null, null, null, false, false, null),
        ImageEditOp.Edit or ImageEditOp.Inpaint or ImageEditOp.RemoveBackground =>
            new ResolvedFormat(FormatModes.Source, null, null, null, null, null, null, false, false, null),
        _ => null,
    };

    // Размер результата для котировки по мегапикселям: у генерации целевой, у остальных — размер
    // исходника, и его знает вызывающий
    public static (int Width, int Height)? TargetSize(ResolvedFormat? format) =>
        format is { Width: > 0, Height: > 0 } ? (format.Width.Value, format.Height.Value) : null;
}
