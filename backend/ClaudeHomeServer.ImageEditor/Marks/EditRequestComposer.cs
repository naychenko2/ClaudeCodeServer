using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Пометки → модель (ADR-017, раздел 3). Растровую работу делает фронт: он присылает чистый
// исходник, маску и копию с нарисованными стрелками, рамками и подписями (annotated). Здесь
// из этого собирается запрос к драйверу:
// - исходник уходит ЧИСТЫМ, как пришёл, — пометки запечены только в копию;
// - образцы идут в фиксированном порядке: исходник, размеченная копия, маска (если у модели
//   нет своего канала маски), затем образцы человека; роли картинок названы в запросе;
// - у модели с SeparateMaskPass кисть вместе с пометками делится на два запроса: сначала по
//   маске, затем по пометкам поверх результата (MaskPass);
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

        // Кисть вместе с пометками такая модель в одном запросе не отрабатывает: рисует по
        // стрелке, а закрашенное оставляет (живой прогон 2026-09-26, 2 из 2; порядок картинок и
        // формулировка ролей не лечат). Тогда первый запрос правит по маске, второй — по
        // пометкам поверх его результата; просьба человека едет в оба, каждый выполняет свою часть
        var split = mask is not null && annotated is not null && model.Caps.SeparateMaskPass
                    && model.Caps.Mask == MaskSupport.AsReference && model.Caps.MaxReferences > 0;

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
            roles.Add(split
                ? "картинка с пометками поверх (стрелки, рамки, подписи) — это указания, куда смотреть; " +
                  "в результат пометки не переносить. Она сделана до первого шага правки, поэтому в остальном " +
                  "может отличаться от первой — брать с неё только пометки"
                : "та же картинка с пометками поверх (стрелки, рамки, подписи) — это указания, " +
                  "куда смотреть; в результат пометки не переносить");
        }

        if (mask is not null && !split)
        {
            if (model.Caps.Mask == MaskSupport.Native)
            {
                maskChannel = mask;
            }
            else
            {
                references.Add(new ReferenceImage(mask.Bytes, mask.ContentType, ReferenceRole.Object, MaskLabel));
                roles.Add(MaskRole);
            }
        }

        // Роль у каждой картинки своя и по её номеру: фото персонажа — одной строкой на диапазон,
        // иначе модель рисует по человеку на каждое фото (живой прогон 2026-09-26, Qwen-Image:
        // три фото — три одинаковых человека на скамейке)
        var userRefs = input.References ?? [];
        var character = input.Character;
        // Модель, которая рисует по человеку на фото, получает лишь первые (primary идёт первым)
        var facePhotos = character is null ? [] : userRefs.Where(r => r.Role == ReferenceRole.Character)
            .Take(model.Caps.MaxCharacterPhotos is > 0 and var max ? max : int.MaxValue).ToList();
        var otherRefs = userRefs.Where(r => character is null || r.Role != ReferenceRole.Character).ToList();
        var faces = facePhotos.Count;
        var others = otherRefs.Count;
        references.AddRange(facePhotos);
        references.AddRange(otherRefs);
        for (var i = 0; i < roles.Count; i++) roles[i] = $"{i + 1}) {roles[i]}";
        var next = roles.Count + 1;
        if (faces > 0)
        {
            var who = string.IsNullOrWhiteSpace(character!.Description)
                ? character.Name
                : $"{character.Name}: {character.Description.Trim()}";
            roles.Add($"{Range(next, faces)}) {(faces == 1 ? "фото лица" : $"{faces} фото лица одного и того же человека с разных ракурсов")} — " +
                      $"персонаж «{character.Name}» ({who}); сохранить его лицо и черты. " +
                      (faces == 1 ? "Это образец внешности, в результат его фон и позу не переносить"
                                  : "Это образцы внешности, а не разные люди: на результате он один, повторять его по числу фото нельзя"));
            next += faces;
        }
        if (others > 0)
            roles.Add($"{Range(next, others)}) {(others == 1 ? "образец для правки" : "образцы для правки")}");

        // Нумерация ролей имеет смысл, только если картинок больше одной
        if (roles.Count > 1 || faces > 0)
            notes.Add(Roles(roles, references.Any(r => r.Label is AnnotatedLabel or MaskLabel),
                split ? SecondPassNote : null));
        else if (character is not null)
            notes.Add($"Персонаж «{character.Name}»: сохранить лицо и черты.");

        var marks = pureInpaint ? "" : EditMarksPrompt.Describe(input.MarksJson, split ? MarksScope.WithoutBrush : MarksScope.All);
        var prompt = Join(input.Prompt, marks, notes);

        // Первый проход: исходник и маска, один вариант — все варианты второго прохода растут
        // из одного и того же стёртого или перерисованного места
        var maskPass = split
            ? new ImageEditRequest(ImageEditOp.Inpaint,
                Join(input.Prompt, EditMarksPrompt.Describe(input.MarksJson, MarksScope.BrushOnly),
                    [Roles(["1) исходная картинка, её и нужно править", $"2) {MaskRole}"], true, FirstPassNote)]),
                source, null, [new ReferenceImage(mask!.Bytes, mask.ContentType, ReferenceRole.Object, MaskLabel)],
                1, null, null, model.Id, null)
            : null;

        return ImageEditCallResult<ImageEditRequest>.Ok(new ImageEditRequest(
            op, prompt, source, maskChannel, references, count, aspectRatio, null, model.Id, input.Character, maskPass,
            Instruction: input.Prompt?.Trim()));
    }

    private const string MaskRole = "маска: белое — область, которую менять, чёрное — не трогать";

    private const string FirstPassNote =
        "Это первый шаг правки: выполнить только ту часть запроса, которая относится к закрашенному кистью " +
        "месту (белое на маске). Стрелки, рамки и подписи на этом шаге не выполнять и ничего не добавлять вне маски.";

    private const string SecondPassNote =
        "Это второй шаг правки: закрашенное кистью место уже обработано, его не трогать и не возвращать как было. " +
        "Выполнить только ту часть запроса, которая относится к стрелкам, рамкам и подписям.";

    private static string Range(int from, int count) => count == 1 ? $"{from}" : $"{from}–{from + count - 1}";

    private static string Roles(IReadOnlyList<string> roles, bool marked, string? pass)
    {
        var sb = new System.Text.StringBuilder("Картинки в запросе:");
        foreach (var role in roles) sb.Append('\n').Append(role);
        if (pass is not null) sb.Append('\n').Append(pass);
        if (marked) sb.Append("\nМенять только отмеченные места первой картинки, всё остальное оставить как есть.");
        return sb.ToString();
    }

    private static string Join(string? userPrompt, string marks, IEnumerable<string> notes) =>
        string.Join("\n\n", new[] { userPrompt?.Trim(), marks }
            .Concat(notes)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    private static ImageEditCallResult<ImageEditRequest> Invalid(string error) =>
        ImageEditCallResult<ImageEditRequest>.Fail(ImageEditErrorCodes.InvalidRequest, error);
}
