using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ImageEditor;

// Доводчик обучения LoRA (ADR-026-contracts §6.4): раз в круг переносит итог задач шва в записи версий
// персонажей и убирает версии «deleting». Тот же SettleAsync зовёт ручка статуса перед ответом.
// Манифест трогаем одним способом: автоподключение первой готовой версии, пока у персонажа нет LoRA
// (ReadLora() == null); при любой уже подключённой — своей версии или ручной — он остаётся прежним.
public sealed class LoraTrainingFinisher(
    IProjectManager projects,
    CharacterLoraRuns runs,
    ILogger<LoraTrainingFinisher> log,
    ILocalLoraTrainer? trainer = null,
    ILocalImageMedia? localMedia = null,
    IProjectFiles? files = null,
    TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (trainer is null) return;
        using var timer = new PeriodicTimer(Interval, time ?? TimeProvider.System);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Доводчик обучения LoRA: круг не удался");
            }
        }
        while (await WaitNextAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    // Один круг: итоги обучений и уборка версий «deleting» (в том числе осиротевших падением процесса
    // между Update и Erase — они не привязаны к задаче, поэтому берутся обходом персонажей проектов)
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (trainer is null) return;
        foreach (var group in trainer.ListAll().GroupBy(s => (s.OwnerId, s.ProjectId, s.Slug)))
            await SettleAsync(group.Key.OwnerId, group.Key.ProjectId, group.Key.Slug, ct);

        foreach (var project in projects.GetAll())
        {
            // Файлы локального проекта лежат на устройстве, сервер к ним не ходит
            if (project.DeviceId is not null || !Directory.Exists(project.RootPath)) continue;
            foreach (var character in CharacterStore.List(project.RootPath))
                await DeleteMarkedAsync(project, character.Slug, ct);
        }
    }

    // Подводит итог обучений одного персонажа; ошибки одной записи не мешают остальным
    public async Task SettleAsync(string ownerId, string projectId, string slug, CancellationToken ct)
    {
        if (trainer is null) return;
        if (projects.GetById(projectId) is not { } project || project.OwnerId != ownerId || project.DeviceId is not null) return;
        if (CharacterStore.Get(project.RootPath, slug) is null) return;

        foreach (var state in trainer.ListAll().Where(s => s.OwnerId == ownerId && s.ProjectId == projectId && s.Slug == slug))
        {
            if (runs.Get(project.RootPath, slug, state.RunId) is not { Status: CharacterLoraRuns.Status.Training } run) continue;
            if (run.JobId is not null && run.JobId != state.JobId) continue;
            try { await FinishAsync(project, slug, run, state, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Доводчик обучения LoRA: версия {Run} персонажа {Slug} не обновлена", state.RunId, slug);
            }
        }
        await DeleteMarkedAsync(project, slug, ct);
    }

    private async Task FinishAsync(Project project, string slug, CharacterLoraRun run, LoraTrainState state, CancellationToken ct)
    {
        switch (state.Status)
        {
            case LoraTrainStatus.Completed when state.LoraFile is { Length: > 0 } loraFile:
                var rejected = state.Rejected?.Select(r => new LoraRunRejection(r.Name, r.Reason)).ToList();
                var ready = runs.Update(project.RootPath, slug, run.RunId, r => r with
                {
                    Status = CharacterLoraRuns.Status.Ready,
                    LoraFile = loraFile,
                    TrainedAt = state.FinishedAt is { } f ? new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Utc)) : DateTimeOffset.UtcNow,
                    PhotosKept = state.PhotosKept,
                    Rejected = rejected,
                    Error = null,
                });
                if (ready is not null) await ConnectIfFirstAsync(project, slug, ready, ct);
                break;
            case LoraTrainStatus.Failed or LoraTrainStatus.Completed:
                var failure = state.Error ?? new LoraTrainFailure("worker_error", "Обучение не дало файла LoRA");
                runs.Update(project.RootPath, slug, run.RunId, r => r with
                {
                    Status = CharacterLoraRuns.Status.Failed,
                    Error = new LoraRunError(failure.Code, failure.Message),
                });
                break;
            case LoraTrainStatus.Cancelled:
                runs.Update(project.RootPath, slug, run.RunId, r => r with { Status = CharacterLoraRuns.Status.Cancelled });
                break;
        }
    }

    // Подключаем только когда у персонажа нет никакой LoRA: свою версию или ручную не вытесняем
    private async Task ConnectIfFirstAsync(Project project, string slug, CharacterLoraRun run, CancellationToken ct)
    {
        if (CharacterStore.Get(project.RootPath, slug) is not { } manifest || manifest.ReadLora() is not null) return;
        var known = localMedia is null ? null : await localMedia.ListLorasAsync(ct);
        var patch = new CharacterPatch(null, null, [], [], null,
            CharacterLoraPatch.Set(run.LoraFile, run.Trigger, null, known));
        var result = CharacterStore.Update(project.RootPath, project.OwnerId, slug, patch);
        if (result?.Value is not { } change)
        {
            log.LogWarning("Доводчик обучения LoRA: версия {Run} не подключена автоматически: {Error}", run.RunId, result?.Error);
            return;
        }
        if (files is null) return;
        foreach (var rel in change.Written) files.NotifyMutated(project.RootPath, rel, FileMutationKind.Write);
    }

    // Стираем запись, когда воркер подтвердил удаление каталога прогона; «занято» и отказ — на следующий круг
    private async Task DeleteMarkedAsync(Project project, string slug, CancellationToken ct)
    {
        if (trainer is null) return;
        foreach (var run in runs.ListDeleting(project.RootPath, slug))
        {
            try
            {
                var state = await trainer.DeleteRunAsync(project.OwnerId, project.Id, slug, run.RunId, ct);
                if (state == LoraDeleteState.Deleted) runs.Erase(project.RootPath, slug, run.RunId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Доводчик обучения LoRA: удаление версии {Run} персонажа {Slug} не удалось", run.RunId, slug);
            }
        }
    }
}
