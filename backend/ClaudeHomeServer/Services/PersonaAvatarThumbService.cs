using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services;

// Миниатюра аватара персоны (GET /api/personas/{id}/avatar?size=…): уменьшенная копия
// оригинала на фиксированной ступени (64/128/256 px, ImageAssetHelper.AvatarThumbSteps).
// Генерируется ОДИН раз по первому запросу и лежит рядом с оригиналом
// (avatar-<guid>-<step>.<ext>); смена аватара меняет имя, старую миниатюру чистит
// PersonaManager.DeleteAvatarAssets.
//
// Генерация — через IImageRaster (SkiaSharp, ADR-018 §9): тот же растр, что у редактора
// картинок, с внутренним семафором на 2 одновременные операции. Без апскейла: если
// оригинал уже меньше ступени, отдаётся он (браузер сам растянет). При отказе
// (Busy/Unsupported/TooLarge — напр. SVG-аватар, который Skia не декодирует) вызывающий
// отдаёт оригинал: аватар не теряется, миниатюра попробуется повторным запросом.
public sealed class PersonaAvatarThumbService(IImageRaster raster, ILogger<PersonaAvatarThumbService> log)
{
    // Путь к существующей или только что сгенерированной миниатюре; null — миниатюра не
    // нужна/не получилась (вызывающий отдаёт оригинал).
    public async Task<string?> GetOrGenerateAsync(Persona persona, string originalPath, int step)
    {
        var name = ImageAssetHelper.AvatarThumbName(persona.Avatar.ImageFile, step);
        if (name is null) return null;
        var dir = Path.GetDirectoryName(originalPath)!;
        var path = SafePath.Join(dir, name);
        if (File.Exists(path)) return path;

        var original = await File.ReadAllBytesAsync(originalPath);
        // Заголовок без декодирования: формат/размер известны до пикселей
        var probe = raster.Probe(original);
        if (probe is null)
        {
            // Формат растр не распознал (напр. SVG-аватар, который Skia не декодирует):
            // вызывающий отдаёт оригинал. Логируем — иначе в проде не видно, почему
            // миниатюра не сгенерировалась (дефект: оригинал залипал в кэше браузера)
            log.LogInformation("Миниатюра {Step} аватара персоны {Persona} не сгенерирована: формат не распознан",
                step, persona.Id);
            return null;
        }
        // Без апскейла: оригинал меньше ступени — браузер растянет его сам, сервер
        // не плодит большую копию меньшей картинки
        if (Math.Max(probe.DisplayWidth, probe.DisplayHeight) <= step) return null;
        // Фиксируем БОЛЬШУЮ сторону в ступень: портрет/альбом не раздуваются по меньшей
        var op = probe.DisplayWidth >= probe.DisplayHeight
            ? new ResizeOp(Width: step)
            : new ResizeOp(Height: step);
        var result = raster.Apply(original, [op], null);
        if (!result.Ok)
        {
            log.LogInformation("Миниатюра {Step} аватара персоны {Persona} не сгенерирована: {Error}",
                step, persona.Id, result.Message);
            return null;
        }

        // Временный файл + переименование: параллельный второй запрос не увидит
        // наполовину записанную миниатюру; победил последний Move, обе копии валидны
        var tmp = SafePath.Join(dir, $".{name}.tmp-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(tmp, result.Image!.Bytes);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* не критично */ }
        }
        return path;
    }
}
