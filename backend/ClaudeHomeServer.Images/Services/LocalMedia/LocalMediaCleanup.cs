using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Чистка того, что задачи оставляют в ComfyUI: входы в input, копии результатов и латенты в
// output. Результат уже лежит в проекте, а латент видео нужен только апскейлу, поэтому:
// - завершилась задача — убираем её входы и файлы результата (первый кадр i2v остаётся:
//   по нему апскейл пересобирает conditioning), у апскейла — и его копии латентов в input;
// - стор забыл задачу (JobRetentionDays) — убираем латенты и всё, что осталось.
//
// Удаляются ТОЛЬКО имена из задачи стора, и каждое обязано иметь вид
// {наш префикс}{jobId этой задачи}{разделитель}{хвост без «/»}: масок и каталогов нет,
// путь — через SafePath.Join от заданного корня, символическую ссылку не трогаем.
// Работает только из LocalMediaCollector (фон); сбой удаления — Warning, сбор не роняет.
public sealed class LocalMediaCleanup(LocalMediaJobStore store, ILogger<LocalMediaCleanup> log)
{
    private bool _reportedDisabled;

    public void Run(LocalMediaOptions options, DateTime now)
    {
        var expired = store.Prune(TimeSpan.FromDays(Math.Max(1, options.JobRetentionDays)), now);
        if (!options.CleanupEnabled)
        {
            if (!_reportedDisabled)
            {
                _reportedDisabled = true;
                log.LogInformation("Чистка файлов ComfyUI выключена: не заданы LocalMedia:ComfyInputDir и "
                    + "LocalMedia:ComfyOutputDir — латенты и промежуточные файлы локальной генерации копятся");
            }
            return;
        }

        var inputDir = options.ComfyInputDir.Trim();
        var outputDir = options.ComfyOutputDir.Trim();
        foreach (var job in store.TerminalUncleaned())
        {
            DeleteTransient(job, inputDir, outputDir, keepForUpscale: true);
            store.Update(job.Id, job.OwnerId, j => j.TransientCleaned = true);
        }
        foreach (var job in expired)
        {
            DeleteTransient(job, inputDir, outputDir, keepForUpscale: false);
            foreach (var latent in new[] { job.LatentVideo, job.LatentAudio })
                if (latent is not null) DeleteOwn(outputDir, latent, job.Id, OutputPrefixes, '_');
        }
    }

    // Префиксы имён в input: подпапка наших загрузок и корень (копии латентов для LoadLatent)
    private static readonly string[] InputPrefixes = [$"{ComfyClient.InputFolder}/", $"{ComfyClient.InputFolder}-"];
    private static readonly string[] OutputPrefixes = [$"{ComfyWorkflows.OutputFolder}/", $"{ComfyWorkflows.OutputFolder}/latents/"];

    private void DeleteTransient(LocalMediaJob job, string inputDir, string outputDir, bool keepForUpscale)
    {
        var inputs = job.ComfyInputs.ToList();
        // Копии латентов апскейла строятся из его jobId — так их находим и у задач, поставленных
        // до появления списка ComfyInputs
        if (job.Op == LocalMediaOps.VideoUpscale)
            inputs.AddRange([$"{ComfyClient.InputFolder}-{job.Id}-video.latent", $"{ComfyClient.InputFolder}-{job.Id}-audio.latent"]);
        var keep = keepForUpscale && job.Status == LocalMediaStatuses.Completed && LocalMediaOps.KeepsLatent(job.Op)
            ? job.ComfyFirstFrame
            : null;
        foreach (var name in inputs.Distinct(StringComparer.Ordinal))
            if (name != keep) DeleteOwn(inputDir, name, job.Id, InputPrefixes, '-');
        foreach (var name in job.ComfyOutputs)
            DeleteOwn(outputDir, name, job.Id, OutputPrefixes, '_');
    }

    // Имя принадлежит задаче: {префикс}{jobId}{разделитель}{хвост}, хвост без каталогов
    public static bool IsOwnName(string name, string jobId, IReadOnlyList<string> prefixes, char separator)
    {
        if (!LocalMediaService.LooksLikeJobId(jobId)) return false;
        foreach (var prefix in prefixes)
        {
            var head = prefix + jobId + separator;
            if (!name.StartsWith(head, StringComparison.Ordinal)) continue;
            var tail = name[head.Length..];
            return tail.Length > 0 && tail.IndexOfAny(['/', '\\']) < 0 && tail != "." && tail != "..";
        }
        return false;
    }

    private void DeleteOwn(string root, string name, string jobId, IReadOnlyList<string> prefixes, char separator)
    {
        if (!IsOwnName(name, jobId, prefixes, separator))
        {
            log.LogWarning("Чистка ComfyUI: имя {Name} не принадлежит задаче {JobId} — не трогаю", name, jobId);
            return;
        }
        try
        {
            var full = SafePath.Join(root, name);
            if (ProjectLinkGuard.HasLink(root, full))
            {
                log.LogWarning("Чистка ComfyUI: {Name} идёт через символическую ссылку — не трогаю", name);
                return;
            }
            // Каталог не трогаем явно: File.Exists/File.Delete ведут себя с ним по-разному на Windows и Linux
            if (Directory.Exists(full))
            {
                log.LogWarning("Чистка ComfyUI: {Name} — каталог, не трогаю", name);
                return;
            }
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Чистка ComfyUI: не удалось удалить {Name}: {Error}", name, ex.Message);
        }
    }
}
