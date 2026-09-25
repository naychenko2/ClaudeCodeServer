using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.Editing;

// Пометки → модель (ADR-017, раздел 3). Растровую работу делает фронт: он присылает чистый
// исходник, маску и копию с нарисованными стрелками, рамками и подписями (annotated). Здесь
// из этого собирается запрос к драйверу:
// - исходник уходит ЧИСТЫМ, как пришёл, — пометки запечены только в копию;
// - образцы идут в фиксированном порядке: исходник, размеченная копия, маска (если у модели
//   нет своего канала маски), затем образцы человека; роли картинок названы в запросе;
// - текст пометок из marks.json с координатами дописывается к запросу человека;
// - просьбу стереть отмеченное чистый инпейнтер не получает (EditIntent) — отказ до запуска.
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

        // Чистый инпейнтер (FLUX Fill): место задаёт маска попиксельно, а запрос — что нарисовать
        // в ней, поэтому текст пометок ему только мешает. Без маски он не работает вовсе —
        // отказ до поставщика, а не падение драйвера: кисть на холсте есть, а PNG маски не доехал
        var pureInpaint = model.Caps.Mask == MaskSupport.Native && !model.Caps.Ops.Contains(ImageEditOp.Edit);
        if (mask is null && pureInpaint)
            return Invalid($"Маска не дошла до сервера, а модели {model.Label} без неё нечего править. " +
                           "Обведите место кистью ещё раз или возьмите модель «Авто»");
        // Чистый инпейнт рисует В маске то, что написано в запросе, и стереть не умеет
        if (pureInpaint && EditIntent.IsRemoval(input.Prompt))
            return Invalid($"Модель {model.Label} дорисовывает, а не стирает: для удаления отмеченного возьмите модель «Авто»");

        // Порядок образцов фиксирован: исходник, размеченная копия, маска, затем образцы
        // человека и персонажа — роли каждой картинки названы в запросе по номерам
        var references = new List<ReferenceImage>();
        var roles = new List<string>();
        if (source is not null) roles.Add("исходная картинка, её и нужно править");
        var notes = new List<string>();
        ImageBytes? maskChannel = null;

        if (annotated is not null && model.Caps.MaxReferences > 0)
        {
            references.Add(new ReferenceImage(annotated.Bytes, annotated.ContentType, ReferenceRole.Object, AnnotatedLabel));
            roles.Add("та же картинка с пометками поверх (стрелки, рамки, подписи) — это указания, " +
                      "куда смотреть; в результат пометки не переносить");
        }

        if (mask is not null)
        {
            if (model.Caps.Mask == MaskSupport.Native)
            {
                maskChannel = mask;
            }
            else
            {
                references.Add(new ReferenceImage(mask.Bytes, mask.ContentType, ReferenceRole.Object, MaskLabel));
                roles.Add("маска: белое — область, которую менять, чёрное — не трогать");
            }
        }

        var userRefs = input.References ?? [];
        references.AddRange(userRefs);
        if (userRefs.Count > 0)
            roles.Add(userRefs.Count == 1 ? "образец для правки" : $"ещё {userRefs.Count} — образцы для правки");

        // Нумерация ролей имеет смысл, только если картинок больше одной
        if (roles.Count > 1)
        {
            var sb = new System.Text.StringBuilder("Картинки в запросе:");
            for (var i = 0; i < roles.Count; i++) sb.Append($"\n{i + 1}) {roles[i]}");
            if (references.Any(r => r.Label is AnnotatedLabel or MaskLabel))
                sb.Append("\nМенять только отмеченные места первой картинки, всё остальное оставить как есть.");
            notes.Add(sb.ToString());
        }

        if (input.Character is { } character)
        {
            var who = string.IsNullOrWhiteSpace(character.Description)
                ? character.Name
                : $"{character.Name}: {character.Description.Trim()}";
            notes.Insert(0, $"Образцы «{character.Name}» — один и тот же человек ({who}): сохранить лицо и черты.");
        }

        var marks = pureInpaint ? "" : EditMarksPrompt.Describe(input.MarksJson);
        var prompt = string.Join("\n\n", new[] { input.Prompt?.Trim(), marks }
            .Concat(notes)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        return ImageEditCallResult<ImageEditRequest>.Ok(new ImageEditRequest(
            op, prompt, source, maskChannel, references, count, aspectRatio, null, model.Id, input.Character));
    }

    private static ImageEditCallResult<ImageEditRequest> Invalid(string error) =>
        ImageEditCallResult<ImageEditRequest>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}
