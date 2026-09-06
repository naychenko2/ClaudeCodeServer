using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Team;

// Включение и переключение режима «Командная реализация» (этап 4, шаг 2г-3и, волна Ж
// плана выноса штаба, docs/research/session-core-split-2026-09.md). Сюда переехали блоки
// тела SessionManager, отвечающие за вкл/выкл режима штаба и переключение авто-волн на
// лету (из бейджа режима):
//
//   • SetTeamImplementAsync — вкл/выкл режима: гард B4 против work-loop, гард B2 приёмки
//     против пустого состава/отсутствующего координатора (отказ ДО интервью), снимок
//     «оборванной волны» (Minor волна 3: задачи волны сиротели молча — теперь человек
//     узнаёт сразу), возврат режима человека при выключении, правка настроек поверх
//     активного режима (M4: повторное включение не рестарт — иначе стираются SavedMode/
//     бюджет/стадия/PlanCardId/счёт волн и волна сиротела), гард «координатор не пишет код»
//     (Default/Auto вместо acceptEdits/bypassPermissions), карточка-след «итерация
//     оборвана» в ленту. Это самая большая точка входа режима — с неё начинается любая
//     практика, и она же выключает её.
//
//   • SetTeamImplementAutoAsync — переключатель авто-волн из бейджа режима: только флаг
//     внутри, режим не активен → поля не трогает, возвращает сессию как есть.
//
// TeamImplementSetupError остался в TeamStateService (волна А): единая проверка готовности
// чата к включению режима, обёртка над `_teamState.TeamImplementSetupError(session, ...)`
// живёт в SessionManager и доступна вертикали через публичный API ядра.
//
// Все поля для включения режима (TeamImplement, Mode, AdapterStale, WorkLoop) живут в
// Session — вертикаль правит его через публичный API ядра (GetById) и швы данных
// (ITeamSessionDirectory.Persist для SaveSessions, ITeamHistoryStore.AppendAsync для
// следа в ленте, ITeamRunState.TrySetEntryModeLiveAndStaleAdapter для тройной
// синхронизации Mode/CLI/AdapterStale — это достройка шва волны Ж, потому что гард
// «координатор не пишет код» пересекается с --disallowedTools, и без AdapterStale правка
// долетала бы только до следующего пересоздания процесса). Включение режима — операция
// с долгим следом (карточка-след в ленту + рассылка + персист), но без транзакций: write-
// then-publish допустим, потому что это единичная кнопка человека, не цикл.
//
// Owning-паттерн (как TeamCoordinator/TeamStateService/TeamBudgetService): экземпляр
// создаётся в конструкторе SessionManager, а не через DI. Так разорван цикл
// «SessionManager хочет TeamEnableService, TeamEnableService хочет SessionManager» без
// Lazy<T> и без новых Func-каналов. Когда придёт шаг 2г-4, регистрация переедет в
// Program.cs, а owning-обёртки SetTeamImplementAsync/SetTeamImplementAutoAsync в
// SessionManager будут сняты.
internal sealed class TeamEnableService
{
    private readonly SessionManager _sessions;
    private readonly ITeamSessionDirectory _dir;
    private readonly ITeamHistoryStore _history;
    private readonly ITeamRunState _run;
    private readonly ILogger<TeamEnableService> _log;

    internal TeamEnableService(SessionManager sessions, ITeamSessionDirectory dir,
        ITeamHistoryStore history, ITeamRunState run, ILogger<TeamEnableService> log)
    {
        _sessions = sessions;
        _dir = dir;
        _history = history;
        _run = run;
        _log = log;
    }

    // Режим «Командная реализация»: вкл/выкл режима чата-штаба. При включении задаётся
    // начальный состав (пустой список исполнителей = вся команда проекта) и стартовый
    // бюджет итерации из дефолтов/конфига. Выкл обнуляет поле — как work-loop.
    public async Task<Session?> SetTeamImplementAsync(string sessionId, bool enabled,
        bool autoWaves = true, string? coordinatorPersonaId = null, string? plannerPersonaId = null,
        IReadOnlyCollection<string>? executorPersonaIds = null, string? userId = null,
        bool coordinatorNoCode = true)
    {
        var session = _sessions.GetById(sessionId);
        if (session is null) return null;
        if (userId is not null && _sessions.ResolveOwnerId(session) != userId) return null;

        // Гард B4 (симметрично SetWorkLoopAsync): автопилот и «Командная реализация» не
        // сочетаются в одном чате — см. SessionModeConflictException.
        if (enabled && session.WorkLoop is not null)
            throw new SessionModeConflictException(
                "Командная реализация недоступна, пока в чате активен Автопилот — сначала выключите цикл «до готово».");

        // Гард на входе (B2 приёмки): чат без координатора или без состава исполнителей режимом
        // не станет. Раньше те же проверки жили только в CreateTeamPlanAsync — отказ приходил
        // ПОСЛЕ полного интервью, и вся постановка (десятки минут хода и токены) уходила впустую.
        if (enabled && _sessions.TeamImplementSetupError(session, coordinatorPersonaId, executorPersonaIds)
            is { } setupError)
            throw new TeamImplementSetupException(setupError.Code, setupError.Message);

        // Minor (волна 3): выключение режима посреди незакрытой волны раньше не оставляло
        // следа — задачи волны сиротели молча (доисполняются, но никто не подводит итог).
        // Снимок ДО обнуления TeamImplement ниже.
        var interruptedWave = !enabled && session.TeamImplement is { WaveNumber: > 0 } wi
            && wi.WaveNumber > wi.ClosedWave ? wi.WaveNumber : (int?)null;
        var interruptedWaveAuthor = interruptedWave is not null
            ? session.TeamImplement!.CoordinatorPersonaId ?? session.PersonaId : null;

        // Выключение режима посреди интервью/планирования: сначала вернуть человеку его
        // режим прав, пока состояние с SavedMode ещё живо — иначе чат навсегда остался бы
        // в план-режиме, который ему навязал штаб (Э8).
        if (!enabled) _sessions.RestoreUserMode(sessionId);

        if (enabled && session.TeamImplement is { } active)
        {
            // M4: повторное включение поверх активного режима — правка настроек, а не рестарт.
            // Пересоздание объекта стирало SavedMode, бюджет, стадию, PlanCardId и счёт волн:
            // волна сиротела — задачи доисполнялись, а закрытия, сводки и проверки не было
            // никогда (план по пустому PlanCardId не находился). Меняем только настраиваемое.
            active.AutoWaves = autoWaves;
            active.CoordinatorPersonaId = coordinatorPersonaId;
            active.PlannerPersonaId = plannerPersonaId;
            active.ExecutorPersonaIds = executorPersonaIds?.ToList() ?? [];
            active.CoordinatorNoCode = coordinatorNoCode;
        }
        else
        {
            session.TeamImplement = enabled
                ? new SessionTeamImplement
                {
                    // Minor (волна 3): по спеке Э8 первая стадия итерации — интервью, а не
                    // планирование (дефолт модели). До этой правки бейдж окно между включением
                    // режима и первой вводной мог показать «планирование» — тексту спеки
                    // соответствует только по совпадению (первая вводная тут же переводит
                    // стадию через ResetTeamIterationOnUserInput).
                    Stage = TeamImplementStage.Interview,
                    AutoWaves = autoWaves,
                    CoordinatorPersonaId = coordinatorPersonaId,
                    PlannerPersonaId = plannerPersonaId,
                    ExecutorPersonaIds = executorPersonaIds?.ToList() ?? [],
                    Budget = _sessions.NewTeamImplementBudget(),
                    CoordinatorNoCode = coordinatorNoCode,
                }
                : null;
        }
        // Гард «координатор не пишет код» (CoordinatorWriteGuard) проверяет команду Bash/
        // PowerShell в момент permission-запроса — а CLI спрашивает разрешение не в любом
        // --permission-mode: в acceptEdits/bypassPermissions запись через shell проходит мимо
        // сервера целиком (проверено вживую той же командой из находки Веры). Default/Auto
        // спрашивают всегда — переводим координатора туда, не трогая уже совместимые режимы.
        // Тройная синхронизация Mode/CLI/AdapterStale через шов: гард режет на приёме
        // permission, --disallowedTools — на создании адаптера, и без AdapterStale правка
        // второго долетала бы только до следующего пересоздания (живой ход остался бы
        // с прежним набором инструментов). Вызов безусловный при включении: даже если
        // Mode не сменился, --disallowedTools зависит от CoordinatorNoCode и должен
        // быть пересобран со следующего хода (ленивая уборка, как у SwitchSpeaker).
        if (enabled)
        {
            var guarded = PermissionModeGuard.GuardCompatibleMode(session.Mode, coordinatorNoCode);
            _run.TrySetEntryModeLiveAndStaleAdapter(sessionId, guarded);
        }
        session.UpdatedAt = DateTime.UtcNow;
        _dir.Persist();
        await _sessions.BroadcastTeamImplementAsync(sessionId, session);

        // След «итерация оборвана» (Minor, волна 3): молчаливых пауз не бывает и у ручного
        // выключения — задачи незакрытой волны продолжат исполняться сами по себе, но человек
        // должен узнать об этом здесь и сейчас, а не догадываться по пропавшему бейджу режима.
        if (interruptedWave is { } wave && interruptedWaveAuthor is { } author)
        {
            var text = $"Режим «Командная реализация» выключен посреди волны {wave} — " +
                "задачи волны продолжат исполняться сами по себе, но закрытия волны, сводки и " +
                "итога итерации больше не будет. Проверьте их вручную.";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _history.AppendAsync(sessionId, new StoredTextMessage(text, personaId: author, timestamp: ts),
                new GuestTextMessage(text, author, ts));
        }
        return session;
    }

    // Переключение авто-волн на ходу (из бейджа режима): не включает/выключает режим,
    // только флаг внутри. Режим не активен → поля не трогает, возвращает сессию как есть.
    public async Task<Session?> SetTeamImplementAutoAsync(string sessionId, bool autoWaves, string? userId = null)
    {
        var session = _sessions.GetById(sessionId);
        if (session is null) return null;
        if (userId is not null && _sessions.ResolveOwnerId(session) != userId) return null;
        if (session.TeamImplement is not { } ti) return session;

        ti.AutoWaves = autoWaves;
        session.UpdatedAt = DateTime.UtcNow;
        _dir.Persist();
        await _sessions.BroadcastTeamImplementAsync(sessionId, session);
        return session;
    }
}