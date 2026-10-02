using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Агент позвал local_generate_image / local_edit_image / local_face_detail напрямую, мимо image_generate:
// картинка лежит файлом в проекте, а ленте нечего показать, кроме голого вызова. Усыновитель заводит нить
// по каждому файлу результата и кладёт якорь в ленту — та же карточка, что у запуска через редактор.
// Выключенный модуль (флаг) и чужой чат — молчаливый отказ.
public sealed class LocalImageAdopter(
    ImageThreadService threads,
    ISessionDirectory directory,
    IProjectManager projects,
    IFeatureFlagGate flags) : ILocalMediaAdopter
{
    public const int MaxThreadsPerJob = 4;

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

        foreach (var file in files)
            await threads.AdoptFileAsync(adoption.OwnerId, adoption.ProjectId, session.Id, file.Path, ct);
    }
}
