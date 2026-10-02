using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Агент позвал local_generate_image / local_edit_image / local_face_detail напрямую, мимо image_generate:
// картинка лежит файлом в проекте, а ленте нечего показать, кроме голого вызова. Одна картинка — нить по
// файлу с якорем в ленте; несколько (count > 1) — одна нить с вариантами-версиями, как у кнопки.
// Выключенный модуль (флаг) и чужой чат — молчаливый отказ.
public sealed class LocalImageAdopter(
    ImageThreadService threads,
    ISessionDirectory directory,
    IProjectManager projects,
    IFeatureFlagGate flags) : ILocalMediaAdopter
{
    public const int MaxThreadsPerJob = 4;
    private const long MaxFileBytes = 100L * 1024 * 1024;

    public async Task AdoptAsync(LocalMediaAdoption adoption, CancellationToken ct)
    {
        var files = adoption.Files
            .Where(f => f.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .Take(MaxThreadsPerJob).ToList();
        if (files.Count == 0) return;
        if (!flags.IsEnabled(adoption.OwnerId, FeatureFlagKeys.ImageEditor)) return;
        if (directory.GetById(adoption.SessionId) is not { } session || session.ProjectId != adoption.ProjectId) return;
        if (directory.ResolveOwnerId(session) != adoption.OwnerId) return;
        if (projects.GetById(adoption.ProjectId) is not { } project || project.OwnerId != adoption.OwnerId) return;

        if (files.Count == 1)
        {
            await threads.AdoptFileAsync(adoption.OwnerId, adoption.ProjectId, session.Id, files[0].Path, ct);
            return;
        }

        // Несколько картинок одной задачи (count > 1) — одна нить с вариантами, как у кнопки; байты читаем
        // здесь, из-под ProjectLinkGuard, — сервису нитей корень проекта не нужен
        List<ProjectImage> images = [];
        foreach (var file in files)
        {
            if (ProjectLinkGuard.ResolveInside(project.RootPath, file.Path) is not { Length: > 0 } full
                || new FileInfo(full) is not { Exists: true, Length: <= MaxFileBytes }) continue;
            images.Add(new ProjectImage(file.Path, await File.ReadAllBytesAsync(full, ct)));
        }
        await threads.AdoptFilesAsync(adoption.OwnerId, adoption.ProjectId, session.Id, adoption.JobId, images, ct);
    }
}
