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
    // Запись training моложе этого срока «потерянной» не считаем: задача шва заводится до записи, но запас дешевле гонки
    public static readonly TimeSpan LostGrace = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // Полный обход проектов нужен один раз за жизнь процесса: наполнить индексы «training» и «deleting»; дальше — по ним
    private bool _fullScanDone;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (trainer is null) return;
        using var timer = new PeriodicTimer(Interval, _time);
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
    // между Update и Erase — они не привязаны к задаче; их находит единственный полный обход при старте,
    // а дальше персонажей с «deleting» держит индекс в CharacterLoraRuns)
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (trainer is null) return;
        var settled = new HashSet<(string Root, string Slug)>();
        foreach (var group in trainer.ListAll().GroupBy(s => (s.OwnerId, s.ProjectId, s.Slug)))
        {
            ct.ThrowIfCancellationRequested();
            if (projects.GetById(group.Key.ProjectId) is { } p) settled.Add((p.RootPath, group.Key.Slug));
            await SettleAsync(group.Key.OwnerId, group.Key.ProjectId, group.Key.Slug, ct);
        }

        if (!_fullScanDone)
        {
            // Записи training без задачи в сторе (откат кода, потеря jobs.json) группой из ListAll не находятся:
            // первичное наполнение индекса, дальше его ведёт CharacterLoraRuns
            foreach (var project in projects.GetAll())
            {
                ct.ThrowIfCancellationRequested();
                if (project.OwnerId is null || !ProjectCapabilities.FilesOnServer(project) || !Directory.Exists(project.RootPath)) continue;
                foreach (var character in CharacterStore.List(project.RootPath))
                    if (runs.List(project.RootPath, character.Slug).Any(r => r.Status == CharacterLoraRuns.Status.Training))
                        runs.RegisterTraining(project.RootPath, character.Slug);
            }

            foreach (var project in projects.GetAll())
            {
                ct.ThrowIfCancellationRequested();
                // Файлы локального проекта лежат на устройстве, сервер к ним не ходит
                if (!ProjectCapabilities.FilesOnServer(project) || !Directory.Exists(project.RootPath)) continue;
                foreach (var character in CharacterStore.List(project.RootPath))
                    if (runs.ListDeleting(project.RootPath, character.Slug).Count > 0)
                        runs.RegisterDeleting(project.RootPath, character.Slug);
            }
            _fullScanDone = true;
        }

        // Каждый круг: персонажи с идущим обучением, которых не покрыли группы шва (задача могла пропасть из стора)
        var trainingPending = runs.CharactersWithTraining().Where(k => !settled.Contains(k)).ToList();
        if (trainingPending.Count > 0)
        {
            var roots = projects.GetAll().Where(ProjectCapabilities.FilesOnServer)
                .GroupBy(p => p.RootPath).ToDictionary(g => g.Key, g => g.First());
            foreach (var (root, slug) in trainingPending)
            {
                ct.ThrowIfCancellationRequested();
                if (roots.TryGetValue(root, out var tp) && tp.OwnerId is { } owner) await SettleAsync(owner, tp.Id, slug, ct);
                runs.UnregisterTrainingIfEmpty(root, slug);
            }
        }

        var pending = runs.CharactersWithDeleting().Where(k => !settled.Contains(k)).ToList();
        if (pending.Count == 0) return;
        var byRoot = projects.GetAll().Where(ProjectCapabilities.FilesOnServer)
            .GroupBy(p => p.RootPath).ToDictionary(g => g.Key, g => g.First());
        foreach (var (root, slug) in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (byRoot.TryGetValue(root, out var project)) await DeleteMarkedAsync(project, slug, ct);
            else runs.UnregisterDeletingIfEmpty(root, slug);
        }
    }

    // Подводит итог обучений одного персонажа; ошибки одной записи не мешают остальным
    public async Task SettleAsync(string ownerId, string projectId, string slug, CancellationToken ct)
    {
        if (trainer is null) return;
        if (projects.GetById(projectId) is not { } project || project.OwnerId != ownerId || !ProjectCapabilities.FilesOnServer(project)) return;
        if (CharacterStore.Get(project.RootPath, slug) is null) return;

        // Записи читаем ДО снимка задач: задача шва заводится раньше записи, обратный порядок дал бы ложный «lost»
        var training = runs.List(project.RootPath, slug).Where(r => r.Status == CharacterLoraRuns.Status.Training).ToList();
        var states = trainer.ListAll().Where(s => s.OwnerId == ownerId && s.ProjectId == projectId && s.Slug == slug).ToList();
        foreach (var state in states)
        {
            ct.ThrowIfCancellationRequested();
            if (runs.Get(project.RootPath, slug, state.RunId) is not { Status: CharacterLoraRuns.Status.Training } run) continue;
            if (run.JobId is not null && run.JobId != state.JobId) continue;
            try { await FinishAsync(project, slug, run, state, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Доводчик обучения LoRA: версия {Run} персонажа {Slug} не обновлена", state.RunId, slug);
            }
        }
        MarkLost(project, slug, training, states);
        await DeleteMarkedAsync(project, slug, ct);
    }

    // Запись training, для которой шов отвечает «задачи нет», навсегда заблокировала бы старт, удаление и отмену.
    // Шов выключен — ответ ничего не значит, записи не трогаем
    private void MarkLost(Project project, string slug, List<CharacterLoraRun> training, List<LoraTrainState> states)
    {
        if (trainer is null || !trainer.Enabled) return;
        var now = _time.GetUtcNow();
        foreach (var run in training)
        {
            if (now - run.CreatedAt < LostGrace) continue;
            if (states.Any(s => s.RunId == run.RunId && (run.JobId is null || run.JobId == s.JobId))) continue;
            runs.Update(project.RootPath, slug, run.RunId, r => r.Status != CharacterLoraRuns.Status.Training ? r : r with
            {
                Status = CharacterLoraRuns.Status.Failed,
                Error = new LoraRunError("lost", "Задача обучения пропала"),
            });
            log.LogWarning("Доводчик обучения LoRA: задача версии {Run} персонажа {Slug} не найдена, версия помечена failed lost", run.RunId, slug);
        }
    }

    private async Task FinishAsync(Project project, string slug, CharacterLoraRun run, LoraTrainState state, CancellationToken ct)
    {
        if (project.OwnerId is null) return;
        switch (state.Status)
        {
            case LoraTrainStatus.Completed when state.LoraFile is { Length: > 0 } loraFile:
                // Без ответа ComfyUI автоподключить нечем: запись остаётся training, следующий круг доведёт.
                // Список нужен, только если подключать есть что: при уже выбранной LoRA ComfyUI не спрашиваем.
                IReadOnlyList<string>? known = null;
                if (localMedia is not null && CharacterStore.Get(project.RootPath, slug)?.ReadLora() is null)
                {
                    known = await localMedia.ListLorasAsync(ct);
                    if (known is null) return;
                }
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
                if (ready is not null) await ConnectIfFirstAsync(project, slug, ready, known, ct);
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

    // Подключаем только когда у персонажа нет никакой LoRA: свою версию или ручную не вытесняем.
    // Перепроверка ReadLora() — внутри блокировки персонажа, общей с ручками версий: между первым чтением
    // и записью человек мог подключить свою
    private async Task ConnectIfFirstAsync(Project project, string slug, CharacterLoraRun run, IReadOnlyList<string>? known, CancellationToken ct)
    {
        if (project.OwnerId is not { } ownerId) return;
        using var _ = await runs.LockCharacterAsync(project.RootPath, slug, ct);
        if (CharacterStore.Get(project.RootPath, slug) is not { } manifest || manifest.ReadLora() is not null) return;
        var patch = new CharacterPatch(null, null, [], [], null,
            CharacterLoraPatch.Set(run.LoraFile, run.Trigger, null, known));
        var result = CharacterStore.Update(project.RootPath, ownerId, slug, patch);
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
        if (trainer is null || project.OwnerId is null) return;
        foreach (var run in runs.ListDeleting(project.RootPath, slug))
        {
            ct.ThrowIfCancellationRequested();
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
        runs.UnregisterDeletingIfEmpty(project.RootPath, slug);
    }
}
