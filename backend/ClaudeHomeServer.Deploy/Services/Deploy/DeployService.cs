namespace ClaudeHomeServer.Services.Deploy;

/// <summary>Исход попытки заказать выкатку. Контроллер переводит его в HTTP-код.</summary>
public enum DeployStartStatus
{
    Accepted,
    Disabled,        // контура на этой машине нет (Deploy:Enabled=false)
    Misconfigured,   // включено, но секция неполна — до планировщика не доходим
    AlreadyRunning,  // 409: выкатка уже идёт
    InvalidRef,      // ref не прошёл белый список
    DirtyTree,       // 400: незакоммиченные правки без allowDirty
    NoRelease,       // откатывать не на что
    GitFailed,       // guard не смог отработать — ехать вслепую нельзя
    LaunchFailed,    // задача планировщика не запустилась
}

/// <summary>Заявка на выкатку — ровно поля контракта ADR-010.</summary>
public sealed record DeployStartRequest(
    string? Ref = null,
    bool SkipFrontend = false,
    bool SkipSandbox = false,
    bool AllowDirty = false);

public sealed record DeployStartResult(
    DeployStartStatus Status,
    string? DeployId = null,
    string? Error = null,
    IReadOnlyList<string>? DirtyFiles = null)
{
    public bool Ok => Status == DeployStartStatus.Accepted;
}

/// <summary>
/// Приём заявок на выкатку прода (ADR-010). Сервер НЕ деплоит себя: он проверяет guard'ы,
/// пишет заявку в журнал и будит задачу планировщика — всю работу делает внешний агент,
/// не состоящий с сервером в родстве (иначе трей убил бы его вместе с деревом процессов).
///
/// Журнал deploy-state.json — шов с агентом: сервер пишет в него только заявку (current),
/// фазы, шаги, итог и список релизов пишет агент. Отметка «доложено» живёт ОТДЕЛЬНЫМ файлом
/// (см. DeployOptions.ReportedMarkerPath) — агент переписывает журнал целиком из своей копии
/// в памяти, и общая запись двух процессов теряется. Единственный случай, когда сервер правит
/// журнал при возможном живом агенте — закрытие протухшей заявки, и оно идёт под мьютексом
/// Global\ccs-deploy, который агент держит всё время работы.
/// </summary>
public sealed class DeployService(
    IConfiguration config,
    IDeployHost host,
    ILogger<DeployService> log)
{
    public DeployOptions Options { get; } = DeployOptions.From(config);

    // Сериализация чтения-модификации-записи журнала внутри процесса. Межпроцессную
    // гонку с агентом это не решает и не должно: сервер пишет журнал ДО старта агента
    // (заявка) и ПОСЛЕ его смерти (reported).
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DeployState Load()
    {
        if (string.IsNullOrWhiteSpace(Options.ReleasesDir)) return new DeployState();
        return JsonFileStore.Load<DeployState>(Options.StatePath, DeployState.Json, log) ?? new DeployState();
    }

    private void Save(DeployState state) =>
        JsonFileStore.Save(Options.StatePath, state, DeployState.Json);

    public Task<DeployStartResult> StartAsync(
        DeployStartRequest req, string userId, string? sessionId, CancellationToken ct = default) =>
        EnqueueAsync(DeployKinds.Deploy, req, releaseId: null, userId, sessionId, ct);

    /// <summary>Ручной откат на снимок релиза; releaseId пуст — предыдущий (последний снимок).</summary>
    public Task<DeployStartResult> RollbackAsync(
        string? releaseId, string userId, string? sessionId, CancellationToken ct = default) =>
        EnqueueAsync(DeployKinds.Rollback, new DeployStartRequest(), releaseId, userId, sessionId, ct);

    private async Task<DeployStartResult> EnqueueAsync(
        string kind, DeployStartRequest req, string? releaseId,
        string userId, string? sessionId, CancellationToken ct)
    {
        if (!Options.Enabled)
            return new DeployStartResult(DeployStartStatus.Disabled,
                Error: "Выкатка на этой машине не настроена (Deploy:Enabled=false)");

        if (Options.Misconfiguration() is { } bad)
            return new DeployStartResult(DeployStartStatus.Misconfigured, Error: $"Секция Deploy неполна: {bad}");

        var gitRef = string.IsNullOrWhiteSpace(req.Ref) ? null : req.Ref.Trim();
        if (gitRef is not null && !DeployValidation.IsValidRef(gitRef))
            return new DeployStartResult(DeployStartStatus.InvalidRef,
                Error: "Недопустимая ссылка git: разрешены буквы, цифры, «.», «_», «/» и «-»");

        if (releaseId is not null && !DeployValidation.IsValidBuildId(releaseId))
            return new DeployStartResult(DeployStartStatus.InvalidRef, Error: "Недопустимый идентификатор релиза");

        await _gate.WaitAsync(ct);
        try
        {
            var state = Load();
            if (state.Current is { IsActive: true } running)
            {
                // Заявка могла остаться висеть в «queued» навсегда: агент не приступил
                // (не стартовала задача планировщика, сработал его собственный guard) и итог
                // не записал. Без TTL такой призрак отвечает 409 на всё следующее.
                if (CloseStaleQueued(running, DateTime.UtcNow) is not { } reloaded)
                    return new DeployStartResult(DeployStartStatus.AlreadyRunning, running.Id,
                        $"Выкатка {running.Id} уже идёт (фаза «{running.Phase}»)");
                state = reloaded;
            }

            var record = new DeployRecord
            {
                Id = NextId(state, DateTime.UtcNow),
                Kind = kind,
                Phase = DeployPhases.Queued,
                Ref = gitRef,
                StartedAt = DateTime.UtcNow,
                InitiatedBy = new DeployInitiator { UserId = userId, SessionId = sessionId },
                Request = new DeployRequest
                {
                    Ref = gitRef,
                    SkipFrontend = req.SkipFrontend,
                    SkipSandbox = req.SkipSandbox,
                    AllowDirty = req.AllowDirty,
                    ReleaseId = releaseId,
                },
            };

            if (kind == DeployKinds.Rollback)
            {
                // Откатываться некуда: снимков ещё нет (первая выкатка их и создаёт)
                if (state.Releases.Count == 0)
                    return new DeployStartResult(DeployStartStatus.NoRelease,
                        Error: "Снимков релизов нет — откатывать не на что");
                if (releaseId is not null && state.Releases.All(r => r.Id != releaseId))
                    return new DeployStartResult(DeployStartStatus.NoRelease,
                        Error: $"Снимок релиза {releaseId} не найден");
            }
            else
            {
                // Guard грязного дерева: агент соберёт ровно то, что лежит в рабочем дереве,
                // поэтому чужие незакоммиченные правки должны быть осознанным решением.
                var snapshot = await host.GitSnapshotAsync(Options.RepoDir, ct);
                if (snapshot.Error is { } gitError)
                    return new DeployStartResult(DeployStartStatus.GitFailed,
                        Error: $"Проверка рабочего дерева не удалась: {gitError}");

                if (snapshot.Dirty && !req.AllowDirty)
                    return new DeployStartResult(DeployStartStatus.DirtyTree,
                        Error: "В рабочем дереве есть незакоммиченные изменения",
                        DirtyFiles: snapshot.DirtyFiles);

                record.Sha = snapshot.Sha;
                record.Dirty = snapshot.Dirty;
                record.DirtyFiles = [.. snapshot.DirtyFiles];
            }

            // Прошлая завершённая выкатка уезжает в историю — current значит «текущая»
            Archive(state);
            state.Current = record;
            Save(state);

            if (await host.WakeAgentAsync(Options, ct) is { } launchError)
            {
                // Агент не стартовал: заявка не должна остаться висеть «в очереди» —
                // иначе следующая попытка получит 409 от призрака
                record.Phase = DeployPhases.Failed;
                record.Result = new DeployResult
                {
                    Ok = false,
                    Status = DeployPhases.Failed,
                    Message = $"Агент выкатки не запустился: {launchError}",
                    FinishedAt = DateTime.UtcNow,
                };
                // Докладывать нечего: заказчик жив и получил ошибку синхронно
                record.Reported = true;
                Save(state);
                log.LogError("Заявка на выкатку {Id}: {Error}", record.Id, launchError);
                return new DeployStartResult(DeployStartStatus.LaunchFailed, record.Id, launchError);
            }

            log.LogInformation("Заявка на выкатку {Id} ({Kind}) принята: ref={Ref}, sha={Sha}, dirty={Dirty}",
                record.Id, record.Kind, record.Ref ?? "по умолчанию", record.Sha ?? "?", record.Dirty);
            return new DeployStartResult(DeployStartStatus.Accepted, record.Id);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Закрыть протухшую заявку: агент не сдвинул её с «queued» дольше TTL и мьютекс выкатки
    /// свободен — значит агента нет и не будет. Возвращает перечитанный под мьютексом журнал
    /// (заявка в нём уже закрыта как failed) либо null — заявка живая, отказывать по-прежнему.
    ///
    /// Синхронный намеренно: владение мьютексом привязано к потоку, между взятием и Dispose
    /// не должно быть await.
    /// </summary>
    private DeployState? CloseStaleQueued(DeployRecord running, DateTime utcNow)
    {
        // Двигавшуюся заявку не трогаем: фазы дальше queued означают живого агента, а его
        // отказ на середине — его же забота (он закрывает заявку сам).
        if (running.Phase != DeployPhases.Queued) return null;
        var age = utcNow - (running.StartedAt ?? utcNow);
        if (age < TimeSpan.FromMinutes(Options.StaleQueuedMinutes)) return null;

        // Свободный мьютекс и есть доказательство, что агента больше нет: он берёт его первым
        // делом и держит до конца работы
        using var lease = host.TryLockAgent();
        if (lease is null) return null;

        // Под мьютексом перечитываем: агент мог дописать журнал, пока мы решали
        var state = Load();
        if (state.Current is not { IsActive: true } current
            || current.Id != running.Id || current.Phase != DeployPhases.Queued) return null;

        var minutes = (int)age.TotalMinutes;
        current.Phase = DeployPhases.Failed;
        current.Result = new DeployResult
        {
            Ok = false,
            Status = DeployPhases.Failed,
            Message = $"Агент выкатки так и не приступил к работе за {minutes} мин — заявка "
                + "закрыта как протухшая. Смотри задачу планировщика и её журнал: скорее всего "
                + "она не стартовала или агент отказался ехать на своём guard'е.",
            FinishedAt = utcNow,
        };
        // Докладывать нечего: заказчика тут никто не ждёт, а тот, кто пришёл с новой заявкой,
        // получит ответ по ней. Устаревший отчёт о призраке только вводил бы в заблуждение.
        current.Reported = true;
        Save(state);
        log.LogWarning("Заявка на выкатку {Id} висела в «queued» {Minutes} мин и закрыта как "
            + "протухшая — агент не приступил", current.Id, minutes);
        return state;
    }

    // Идентификатор — UTC-штамп (он же имя папки снимка). Две заявки в одну секунду
    // получили бы одно имя, поэтому при столкновении добавляем суффикс.
    private static string NextId(DeployState state, DateTime utcNow)
    {
        var baseId = DeployValidation.NewDeployId(utcNow);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.Current is { } c) taken.Add(c.Id);
        foreach (var h in state.History) taken.Add(h.Id);

        if (!taken.Contains(baseId)) return baseId;
        for (var i = 2; i < 100; i++)
            if (!taken.Contains($"{baseId}-{i}")) return $"{baseId}-{i}";
        return $"{baseId}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    private static void Archive(DeployState state)
    {
        if (state.Current is not { } done) return;
        state.History.Insert(0, done);
        if (state.History.Count > DeployState.HistoryLimit)
            state.History.RemoveRange(DeployState.HistoryLimit, state.History.Count - DeployState.HistoryLimit);
        state.Current = null;
    }

    /// <summary>
    /// Срок годности итога: доклад о выкатке старше суток бессмыслен и вреден — журнал живёт
    /// на диске месяцами, и без этого предела рестарт через месяц отчитался бы о позавчерашней
    /// выкатке как о свежей новости.
    /// </summary>
    public static readonly TimeSpan ReportMaxAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Незадоложенный итог: выкатка закончилась, но заказчик о ней не узнал — его чат умер
    /// вместе со старым инстансом. Возвращает запись, докладывать её — DeployReportService.
    /// </summary>
    public DeployRecord? PendingReport() => PendingReport(DateTime.UtcNow);

    internal DeployRecord? PendingReport(DateTime utcNow)
    {
        if (Load().Current is not { Result: not null } current) return null;
        // Reported в журнале — только для заявок, не доехавших до агента (их закрывает сам
        // сервер); доклад состоявшейся выкатки отмечается маркером-файлом
        if (current.Reported || IsReported(current.Id)) return null;

        // Момент итога знает только агент; нет отметки времени — считаем свежим (пропустить
        // настоящий доклад хуже, чем прислать один спорный)
        var finished = current.Result?.FinishedAt;
        return finished is { } at && utcNow - at > ReportMaxAge ? null : current;
    }

    /// <summary>Итог этой выкатки уже доложен (маркер рядом с журналом).</summary>
    private bool IsReported(string deployId) =>
        Options.ReportedMarkerPath(deployId) is { } path && File.Exists(path);

    /// <summary>
    /// Отметить итог доложенным. Пишем ОТДЕЛЬНЫЙ файл, а не поле журнала: журнал в этот момент
    /// ещё может переписывать живой агент (он ставит result до конца своей работы), и отметка
    /// в общем файле либо пропала бы — доклад повторился, либо затёрла бы его result.
    /// </summary>
    public async Task MarkReportedAsync(string deployId, CancellationToken ct = default)
    {
        if (Options.ReportedMarkerPath(deployId) is not { } path) return;
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Options.ReleasesDir);
            await File.WriteAllTextAsync(path,
                DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);
            PruneMarkers();
        }
        catch (Exception ex)
        {
            // Не записали отметку — доклад повторится на следующем рестарте. Неприятно,
            // но ронять фоновый сервис из-за файла нельзя
            log.LogWarning(ex, "Отметка о докладе выкатки {Id} не записана", deployId);
        }
        finally { _gate.Release(); }
    }

    // Маркеры доклада копятся по одному на выкатку — держим только свежие: журнал хранит
    // 30 записей истории, всё старше месяца доложить уже невозможно (см. ReportMaxAge).
    private void PruneMarkers()
    {
        try
        {
            var edge = DateTime.UtcNow - TimeSpan.FromDays(30);
            foreach (var file in Directory.EnumerateFiles(Options.ReleasesDir, "reported-*"))
                if (File.GetLastWriteTimeUtc(file) < edge) File.Delete(file);
        }
        catch (Exception ex) { log.LogDebug(ex, "Уборка маркеров доклада не удалась"); }
    }
}
