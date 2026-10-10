using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Обучение LoRA персонажа (ADR-026): один узел CcsAudioWorker. Через границу едут только
// белый список — пресет, триггер, runId, owner, slug и имена загруженных фото; каталог
// результата строит воркер. Путей, yaml, шагов, rank и lr на проводе нет никогда.
public static partial class ComfyWorkflows
{
    // inputs — имена, которые вернул UploadInputAsync: "ccs/<ownerKey>/<runId>/<sha16>.jpg"
    public static JsonObject LoraTrain(string ownerId, string slug, string runId, string trigger, string preset,
        IReadOnlyList<string> inputs, string jobId)
    {
        var owner = LoraNamespace.OwnerKey(ownerId);
        LoraNamespace.EnsureRunValid(slug, runId);
        if (!LoraTrigger.IsValid(trigger)) throw new ArgumentException("Недопустимый триггер", nameof(trigger));
        if (!LoraTrainPresets.All.Contains(preset)) throw new ArgumentException("Неизвестный пресет", nameof(preset));
        if (inputs.Count == 0) throw new ArgumentException("Нет фото для обучения", nameof(inputs));

        var folder = LoraNamespace.RunInputFolder(ownerId, runId) + "/";
        foreach (var input in inputs)
        {
            if (!input.StartsWith(folder, StringComparison.Ordinal)
                || !IsLoraPhotoName(input[folder.Length..]))
                throw new ArgumentException("Недопустимое имя фото", nameof(inputs));
        }

        return AudioWorker(LocalMediaOps.LoraTrain, new JsonObject
        {
            ["owner"] = owner,
            ["slug"] = slug,
            ["run_id"] = runId,
            ["trigger"] = trigger,
            ["preset"] = preset,
        }, inputs, jobId, 240);
    }

    public static JsonObject LoraDelete(string ownerId, string slug, string runId, string jobId)
    {
        LoraNamespace.EnsureRunValid(slug, runId);
        return AudioWorker(LocalMediaOps.LoraDelete, new JsonObject
        {
            ["owner"] = LoraNamespace.OwnerKey(ownerId),
            ["slug"] = slug,
            ["run_id"] = runId,
        }, [], jobId, 1);
    }

    internal static bool IsLoraPhotoName(string name) => LoraPhotoNameRegex.IsMatch(name);

    // Имя обучающего фото: хеш (16 hex) и расширение; без путей и «..»; конец строки — `\z`, не `$`
    private static readonly Regex LoraPhotoNameRegex = new(@"^[0-9a-f]{16}\.(jpg|png|webp)\z", RegexOptions.Compiled);
}
