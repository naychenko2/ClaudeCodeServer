using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.Editing;

// Пометки → модель (ADR-017, раздел 3). Растровую работу делает фронт: он присылает чистый
// исходник, маску и копию с нарисованными стрелками, рамками и подписями (annotated). Здесь
// из этого собирается запрос к драйверу:
// - исходник уходит ЧИСТЫМ, как пришёл, — пометки запечены только в копию;
// - маска идёт отдельным каналом, если модель его умеет (Native), иначе дополнительным
//   образцом с фразой «менять только отмеченное»;
// - размеченная копия — дополнительным образцом, если модель принимает образцы;
// - текст пометок из marks.json дописывается к запросу человека.
// Ничего на диск не пишется: файл оригинала в проекте этот слой не видит вовсе.
public static class EditRequestComposer
{
    public const string AnnotatedLabel = "annotated";
    public const string MaskLabel = "mask";

    public static ImageEditCallResult<ImageEditRequest> Compose(
        ImageEditJobInput input, ImageEditOp op, ImageEditModelInfo model, int count, string? aspectRatio = null)
    {
        var source = input.Source is { Bytes.Length: > 0 } ? input.Source : null;
        var mask = input.Mask is { Bytes.Length: > 0 } ? input.Mask : null;
        var annotated = input.Annotated is { Bytes.Length: > 0 } ? input.Annotated : null;

        if (op != ImageEditOp.Generate && source is null)
            return Invalid("Нет исходной картинки");

        if (mask is not null)
        {
            if (source is null) return Invalid("Маска без исходной картинки");
            if (!ImageDimensions.IsPng(mask.Bytes)) return Invalid("Маска должна быть PNG");
            var sourceSize = ImageDimensions.Read(source.Bytes);
            if (sourceSize is null) return Invalid("Не удалось прочитать размер исходной картинки");
            if (ImageDimensions.Read(mask.Bytes) != sourceSize)
                return Invalid("Размер маски не совпадает с исходной картинкой");
            if (model.Caps.Mask == MaskSupport.None)
                return Invalid($"Модель {model.Label} не принимает маску");
            if (op == ImageEditOp.Edit) op = ImageEditOp.Inpaint;
        }

        var references = new List<ReferenceImage>(input.References ?? []);
        var notes = new List<string>();
        ImageBytes? maskChannel = null;

        if (mask is not null)
        {
            if (model.Caps.Mask == MaskSupport.Native)
            {
                maskChannel = mask;
            }
            else
            {
                references.Add(new ReferenceImage(mask.Bytes, mask.ContentType, ReferenceRole.Object, MaskLabel));
                notes.Add($"Менять только область, отмеченную белым на вспомогательной картинке «{MaskLabel}»; " +
                          "всё остальное оставить без изменений.");
            }
        }

        if (annotated is not null && model.Caps.MaxReferences > 0)
        {
            references.Add(new ReferenceImage(annotated.Bytes, annotated.ContentType, ReferenceRole.Object, AnnotatedLabel));
            notes.Add($"Стрелки, рамки и подписи нарисованы только на вспомогательной копии «{AnnotatedLabel}» — " +
                      "это указания, в результат их не переносить.");
        }

        if (input.Character is { } character)
        {
            var who = string.IsNullOrWhiteSpace(character.Description)
                ? character.Name
                : $"{character.Name}: {character.Description.Trim()}";
            notes.Insert(0, $"Образцы «{character.Name}» — один и тот же человек ({who}): сохранить лицо и черты.");
        }

        var marks = EditMarksPrompt.Describe(input.MarksJson);
        var prompt = string.Join("\n\n", new[] { input.Prompt?.Trim(), marks }
            .Concat(notes)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        return ImageEditCallResult<ImageEditRequest>.Ok(new ImageEditRequest(
            op, prompt, source, maskChannel, references, count, aspectRatio, null, model.Id, input.Character));
    }

    private static ImageEditCallResult<ImageEditRequest> Invalid(string error) =>
        ImageEditCallResult<ImageEditRequest>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}
