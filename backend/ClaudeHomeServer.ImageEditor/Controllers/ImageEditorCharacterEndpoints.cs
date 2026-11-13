using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Общее тело ручек персонажей (ADR-017, раздел 10): папка проекта characters/<slug>/. Наследники —
// проектный ImageEditorController и личный PersonalImageEditorController (папка «Личного», ADR-040) —
// проходят свой гейт и зовут методы отсюда: две копии разошлись бы в проверках LoRA и фото.
// Невалидный slug неотличим от отсутствующего.
public abstract class ImageEditorCharacterEndpoints(
    IEnumerable<IImageEditor> editors,
    ImageEditLaunchAssembler launcher,
    IImagePlaceSettings? placeSettings,
    IImageEditJobs? jobs,
    ImageEditSteps? steps,
    IConfiguration? config,
    IImageRaster? raster,
    ImageContextLaunch? context,
    IProjectFiles? files,
    ILocalImageMedia? localMedia,
    CharacterTrainingPhotos? trainingPhotos,
    CharacterPhotoLabeling? labeling,
    CharacterLoraRuns? loraRuns)
    : ImageEditorEndpoints(editors, launcher, placeSettings, jobs, steps, config, raster, context)
{
    // Потолок тела формы персонажа: 10 фото по 8 МБ плюс поля
    protected const long MaxCharacterBodyBytes = 100L * 1024 * 1024;

    protected IProjectFiles? Files => files;

    protected IActionResult CharactersIn(Project project)
    {
        var list = CharacterStore.List(project.RootPath);
        foreach (var m in list) labeling?.Enqueue(ImageEditScope.TenantOf(project), project.RootPath, m.Slug);
        return Ok(list.Select(m => CharacterDto.From(m, withLabels: true)));
    }

    // Список LoRA ComfyUI для формы персонажа. Нет шва или ComfyUI не ответил — не ошибка:
    // available: false, форма прячет выбор и оставляет текущую LoRA как есть.
    // Проекта может не быть (личная область до первого персонажа): тогда дат обучения нет
    protected async Task<IActionResult> LorasIn(string tenant, Project? project, CancellationToken ct)
    {
        var items = localMedia is null ? null : await localMedia.ListLorasAsync(ct);
        var own = items?.Where(n => LoraNamespace.VisibleTo(tenant, n)).ToList();
        // Даты обучения — только для имён, прошедших фильтр видимости: чужие не утекают
        var trained = own is null || project is null ? [] : loraRuns?.TrainedDates(project.RootPath)
            .Where(kv => own.Contains(kv.Key, StringComparer.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
        return Ok(new { available = own is not null, items = own ?? [], trained });
    }

    protected IActionResult CharacterIn(Project project, string slug)
    {
        var manifest = CharacterStore.Get(project.RootPath, slug);
        if (manifest is not null) labeling?.Enqueue(ImageEditScope.TenantOf(project), project.RootPath, manifest.Slug);
        return manifest is null ? CharacterNotFound() : Ok(CharacterDto.From(manifest, withLabels: true));
    }

    protected async Task<IActionResult> CreateCharacterIn(Project project, CharacterForm form, CancellationToken ct)
    {
        var (lora, loraDenied) = await LoraPatchAsync(ImageEditScope.TenantOf(project), form, ct);
        // «Убрать» при создании нечего: LoRA ещё нет
        if (lora is { Remove: true }) lora = null;
        if (loraDenied is not null) return loraDenied;
        // Имя проверяем до создания: отказ не должен оставить персонажа без LoRA на диске
        if (lora is not null && CharacterStore.CheckLora(ImageEditScope.TenantOf(project), lora) is { } loraError) return MapCharacter(loraError, _ => Ok());
        var photos = await PhotosAsync(form.Photos, form.Angles, ct);
        var draft = new CharacterDraft(form.Name ?? "", form.Description, photos);
        var created = CharacterStore.Create(project.RootPath, draft, DateTime.UtcNow);
        if (created.Value is { } change) Notify(project.RootPath, change, FileMutationKind.Create);
        if (created.Value is { } made && lora is not null)
        {
            var withLora = CharacterStore.Update(project.RootPath, ImageEditScope.TenantOf(project), made.Manifest.Slug,
                new CharacterPatch(null, null, [], [], null, lora));
            if (withLora?.Value is { } loraChange) Notify(project.RootPath, loraChange, FileMutationKind.Write);
            created = withLora ?? created;
        }
        if (created.Value is { } born) labeling?.Enqueue(ImageEditScope.TenantOf(project), project.RootPath, born.Manifest.Slug);
        return MapCharacter(created, c => StatusCode(StatusCodes.Status201Created, CharacterDto.From(c.Manifest, withLabels: true)));
    }

    protected async Task<IActionResult> UpdateCharacterIn(Project project, string slug, CharacterForm form, CancellationToken ct)
    {
        var (lora, loraDenied) = await LoraPatchAsync(ImageEditScope.TenantOf(project), form, ct);
        if (loraDenied is not null) return loraDenied;
        var (labels, labelsDenied) = PhotoLabelsOf(form);
        if (labelsDenied is not null) return labelsDenied;
        var photos = await PhotosAsync(form.Photos, form.Angles, ct);
        var patch = new CharacterPatch(form.Name, form.Description, photos, form.RemovePhotos ?? [], form.PrimaryPhoto, lora, labels);
        var updated = CharacterStore.Update(project.RootPath, ImageEditScope.TenantOf(project), slug, patch);
        if (updated is null) return CharacterNotFound();
        if (updated.Value is { } change) Notify(project.RootPath, change, FileMutationKind.Write);
        if (photos.Count > 0) labeling?.Enqueue(ImageEditScope.TenantOf(project), project.RootPath, slug);
        return MapCharacter(updated, c => Ok(CharacterDto.From(c.Manifest, withLabels: true)));
    }

    protected IActionResult DeleteCharacterIn(Project project, string slug)
    {
        if (!CharacterStore.Delete(project.RootPath, slug)) return CharacterNotFound();
        // Обучающие фото персонажа живут вне проекта (data/lora-training) — без уборки остаются сиротами
        trainingPhotos?.Purge(ImageEditScope.TenantOf(project), project.Id, slug);
        files?.NotifyMutated(project.RootPath, $"{CharacterStore.Folder}/{slug}", FileMutationKind.Delete);
        return NoContent();
    }

    protected IActionResult CharacterPhotoIn(Project project, string slug, string file)
    {
        var photo = CharacterStore.OpenPhoto(project.RootPath, slug, file);
        return photo is null ? CharacterNotFound() : File(photo.Bytes, photo.ContentType);
    }

    // Поля формы персонажа. Angles — параллельный список к Photos (front, three-quarter…).
    // В PUT пустое поле — не менять; RemovePhotos — имена файлов из манифеста.
    public sealed class CharacterForm
    {
        public string? Name { get; set; }
        // Пустая строка = «стереть», поэтому в null её не превращаем
        [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
        public string? Description { get; set; }
        public List<IFormFile>? Photos { get; set; }
        public List<string>? Angles { get; set; }
        public List<string>? RemovePhotos { get; set; }
        public string? PrimaryPhoto { get; set; }
        public string? LoraName { get; set; }
        public string? LoraTrigger { get; set; }
        public string? LoraStrength { get; set; }
        public bool? RemoveLora { get; set; }
        // JSON {"<файл>": {"angle","pitch","emotion"}}
        public string? PhotoLabels { get; set; }
    }

    private static readonly System.Text.Json.JsonSerializerOptions LabelsJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    // Ручные метки фото (ADR-027 §1). Значения и имена файлов проверяет CharacterStore.Update
    private (IReadOnlyDictionary<string, CharacterPhotoLabels>? Labels, IActionResult? Denied) PhotoLabelsOf(CharacterForm form)
    {
        if (string.IsNullOrWhiteSpace(form.PhotoLabels)) return (null, null);
        try
        {
            var labels = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CharacterPhotoLabels>>(form.PhotoLabels, LabelsJson);
            if (labels is not null && labels.Values.All(l => l is not null)) return (labels, null);
        }
        catch (System.Text.Json.JsonException) { }
        return (null, Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Метки фото: ожидается объект {файл: {angle, pitch, emotion}}"));
    }

    // Патч LoRA из формы: RemoveLora — убрать, LoraName — задать, иначе null (не трогать).
    // Список ComfyUI запрашиваем только когда LoRA задают
    private async Task<(CharacterLoraPatch? Patch, IActionResult? Denied)> LoraPatchAsync(
        string tenantId, CharacterForm form, CancellationToken ct)
    {
        if (form.RemoveLora == true) return (CharacterLoraPatch.Removal(), null);
        if (string.IsNullOrWhiteSpace(form.LoraName)) return (null, null);
        double? strength = null;
        if (!string.IsNullOrWhiteSpace(form.LoraStrength))
        {
            if (!double.TryParse(form.LoraStrength.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return (null, Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "Сила должна быть числом от 0 до 2."));
            strength = parsed;
        }
        if (!LoraNamespace.VisibleTo(tenantId, form.LoraName.Trim()))
            return (null, Error(StatusCodes.Status403Forbidden, ImageEditErrorCodes.LoraNotVisible, "Эта LoRA вам недоступна."));
        var known = localMedia is null ? null : await localMedia.ListLorasAsync(ct);
        return (CharacterLoraPatch.Set(form.LoraName, form.LoraTrigger, strength, known), null);
    }

    // Недоступный ComfyUI при проверке LoRA — 503, а не 409 из общего Map
    private IActionResult MapCharacter<T>(ImageEditCallResult<T> result, Func<T, IActionResult> ok) =>
        result.ErrorCode == ImageEditErrorCodes.ProviderUnavailable
            ? Error(StatusCodes.Status503ServiceUnavailable, result.ErrorCode, result.Error ?? "Запрос не выполнен")
            : result.ErrorCode == ImageEditErrorCodes.LoraNotVisible
                ? Error(StatusCodes.Status403Forbidden, result.ErrorCode, result.Error ?? "Запрос не выполнен")
                : Map(result, ok);

    protected IActionResult CharacterNotFound() =>
        Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.CharacterNotFound, "Персонаж не найден");

    private void Notify(string root, CharacterChange change, FileMutationKind kind)
    {
        if (files is null) return;
        foreach (var rel in change.Written) files.NotifyMutated(root, rel, kind);
        foreach (var rel in change.Deleted) files.NotifyMutated(root, rel, FileMutationKind.Delete);
    }

    private static async Task<List<CharacterPhotoUpload>> PhotosAsync(
        List<IFormFile>? photos, List<string>? angles, CancellationToken ct)
    {
        var result = new List<CharacterPhotoUpload>();
        var list = photos ?? [];
        for (var i = 0; i < list.Count; i++)
            result.Add(new CharacterPhotoUpload(await ReadAsync(list[i], ct),
                angles is not null && i < angles.Count ? angles[i] : null));
        return result;
    }
}
