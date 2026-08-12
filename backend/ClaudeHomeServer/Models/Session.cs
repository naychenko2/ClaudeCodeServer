namespace ClaudeHomeServer.Models;

public enum SessionStatus { Starting, Working, Active, Waiting, Finished, Error, Orphaned }

// Происхождение чата — вычисляется из TaskId/AutomationRuleId (см. Session.Origin),
// отдельно не хранится, чтобы не было второго источника истины.
public enum ChatOrigin { Manual, Task, Automation }

// Режимы прав — соответствуют значениям флага --permission-mode у claude CLI
public enum ClaudeMode { Default, AcceptEdits, Plan, Auto, DontAsk, Bypass }

// Значения Session.OnboardingKind (фича default-personas-onboarding):
// User — онбординг первого входа (ведёт системный «Мастер настройки», персоны у сессии нет);
// Project — онбординг проекта (ведёт личная дефолт-персона владельца).
public static class OnboardingKinds
{
    public const string User = "user";
    public const string Project = "project";
}

public static class ClaudeModeExtensions
{
    // Значение флага --permission-mode для claude CLI
    public static string ToCliFlag(this ClaudeMode mode) => mode switch
    {
        ClaudeMode.AcceptEdits => "acceptEdits",
        ClaudeMode.Plan => "plan",
        ClaudeMode.Auto => "auto",
        ClaudeMode.DontAsk => "dontAsk",
        ClaudeMode.Bypass => "bypassPermissions",
        _ => "default",
    };

    // Wire-токен для фронта (совпадает с именами режимов в frontend/src/lib/modes.ts)
    public static string ToWireToken(this ClaudeMode mode) => mode switch
    {
        ClaudeMode.AcceptEdits => "acceptEdits",
        ClaudeMode.Plan => "plan",
        ClaudeMode.Auto => "auto",
        ClaudeMode.DontAsk => "dontAsk",
        ClaudeMode.Bypass => "bypass",
        _ => "default",
    };
}

// Состояние цикла «до готово» (флаг work-loop, идея ralph/ulw-loop из oh-my-openagent):
// присутствие объекта у сессии = цикл активен; остановка обнуляет поле.
public class SessionWorkLoop
{
    // Маркер завершения: агент выводит <promise>{Promise}</promise>, когда всё сделано
    public string Promise { get; set; } = "ГОТОВО";
    // Номер текущей итерации (растёт с каждым автопродолжением)
    public int Iteration { get; set; }
    // Потолок итераций — защита от бесконечного цикла (дефолт из конфига Loop:MaxIterations)
    public int MaxIterations { get; set; } = 20;
    // working — рабочие итерации; verifying — финальный верификационный ход после маркера
    public string Phase { get; set; } = "working";
    // Сколько раз в цикле уже запущены задачи на исполнение: счёт ведёт бэкенд в точке
    // запуска (гейт DenyOnDelegatedTurn), а не модель — как у TeamImplementBudget. Квота
    // заменяет запрет на ходу доклада, иначе «доклад → запуск → доклад» — бесконечный цикл.
    public int ExecutionsStarted { get; set; }
    // Потолок запусков задач за цикл (дефолт из конфига Loop:MaxTaskExecutions)
    public int MaxExecutions { get; set; } = 20;
}

// Стадии режима «Командная реализация» — непрерывный контур.
// Wire-токены отдаёт ToWireToken (camelCase), совпадают с метками бейджа режима на фронте.
public enum TeamImplementStage
{
    // Координатор спрашивает человека по вводной, прежде чем планировать (Э8): вопросы идут
    // ASK-карточками, волны на паузе, чат работает в план-режиме. Первая вводная итерации
    // проходит интервью всегда; тупик в волне возвращает практику сюда же.
    Interview,
    // Координатор декомпозирует вводную и готовит карточку плана
    Planning,
    // Карточка плана ждёт единственного согласования человека («Запустить»)
    Confirming,
    // Волна исполнителей в работе
    Wave,
    // Эскалация: практика ждёт решения человека (блокер, провал, исчерпание бюджета)
    AwaitingDecision,
    // Финальная проверка (сборка/тесты) по итогам волн
    Checking,
    // Итерация завершена, режим ждёт новой вводной — жив, НЕ выключен
    Idle
}

public static class TeamImplementStageExtensions
{
    // Wire-токен стадии для фронта (camelCase)
    public static string ToWireToken(this TeamImplementStage stage) => stage switch
    {
        TeamImplementStage.Interview => "interview",
        TeamImplementStage.Planning => "planning",
        TeamImplementStage.Confirming => "confirming",
        TeamImplementStage.Wave => "wave",
        TeamImplementStage.AwaitingDecision => "awaitingDecision",
        TeamImplementStage.Checking => "checking",
        TeamImplementStage.Idle => "idle",
        _ => "planning",
    };

    // Стадии, на которых штаб думает, а не работает (Э8): интервью, планирование и ожидание
    // подтверждения плана. Чат в них держится в план-режиме — правки запрещены самой
    // permission-механикой CLI, а селектор режима у человека заблокирован до «Запустить».
    public static bool IsPlanPhase(this TeamImplementStage stage) =>
        stage is TeamImplementStage.Interview or TeamImplementStage.Planning
            or TeamImplementStage.Confirming;
}

// Бюджет итерации режима «Командная реализация»: считается на одну вводную человека и
// сбрасывается с каждой новой (см. docs/architecture/team-implement-mode.md, «Бюджет»).
// Счётчики «израсходовано» инкрементирует бэкенд в точке запуска (Э3–Э4), НЕ модель.
// Потолки — настраиваемые дефолты (TeamImplement:* в конфиге), выставляются при включении.
public class TeamImplementBudget
{
    // Израсходовано в текущей итерации
    public int TasksUsed { get; set; }
    public int WavesUsed { get; set; }
    public int RunsUsed { get; set; }     // запуски исполнителей
    public int RetriesUsed { get; set; }  // перевыдачи провалившихся под-задач
    // Пробуждения штаба докладом-блокером снизу: каждое — платный ход координатора,
    // инициированный АГЕНТОМ, поэтому у него свой потолок. Без него исполнитель мог бы
    // будить координатора в цикле, не тратя ни одной единицы прочих квот.
    public int WakeupsUsed { get; set; }

    // Потолки (дефолты из плана; override — TeamImplement:Max* в конфиге)
    public int MaxTasks { get; set; } = 12;
    public int MaxWaves { get; set; } = 4;
    public int MaxRuns { get; set; } = 20;
    public int MaxRetries { get; set; } = 3;
    public int MaxWakeups { get; set; } = 10;

    // Бюджет итерации исчерпан? Возвращает человекочитаемую причину для карточки остановки
    // и текста отказа гейта, либо null — можно работать дальше. Чтение потолков в модели
    // допустимо; инкремент счётчиков — только на бэкенде в точке запуска.
    public string? ExceededReason() =>
        TasksUsed >= MaxTasks ? $"израсходовано задач: {TasksUsed} из {MaxTasks}"
        : RunsUsed >= MaxRuns ? $"израсходовано запусков исполнителей: {RunsUsed} из {MaxRuns}"
        : WavesUsed >= MaxWaves ? $"израсходовано волн: {WavesUsed} из {MaxWaves}"
        : RetriesUsed >= MaxRetries ? $"израсходовано перевыдач: {RetriesUsed} из {MaxRetries}"
        : WakeupsUsed >= MaxWakeups ? $"израсходовано срочных вызовов координатора: {WakeupsUsed} из {MaxWakeups}"
        : null;

    // Помещается ли в остаток ЦЕЛАЯ волна из tasks под-задач (плюс сама волна). Волна либо
    // раздаётся целиком, либо не стартует вовсе: «остаток есть?» пропускал волну из 5 задач
    // при остатке в одну, и потолок 12 выбирался с перерасходом. Причина — для карточки.
    public string? ExceededReasonForWave(int tasks) =>
        TasksUsed + tasks > MaxTasks
            ? $"задач в итерации: {TasksUsed} из {MaxTasks}, а волне нужно ещё {tasks}"
        : RunsUsed + tasks > MaxRuns
            ? $"запусков исполнителей: {RunsUsed} из {MaxRuns}, а волне нужно ещё {tasks}"
        : WavesUsed + 1 > MaxWaves
            ? $"израсходовано волн: {WavesUsed} из {MaxWaves}"
        : ExceededReason();
}

// Состояние режима «Командная реализация»: чат работает как
// штаб фичи — план, задачи на персон-исполнителей, волны, проверка. Наличие объекта у
// сессии = режим активен; выключение обнуляет поле (как SessionWorkLoop).
// Здесь только состояние; планирование живёт в TeamPlanningService, волны и эскалации —
// в TeamWaveService.
public class SessionTeamImplement
{
    // Стадия непрерывного контура (см. TeamImplementStage)
    public TeamImplementStage Stage { get; set; } = TeamImplementStage.Planning;
    // Стадия, из которой практика ушла в «ждёт решения»: ответ человека до первой волны
    // возвращает её СЮДА (интервью/планирование), а не в Wave — иначе «волна-призрак»
    // (WaveNumber=0, плана нет, сторож не тикает; прод 2026-07-31). null — не в ожидании.
    public TeamImplementStage? StageBeforeDecision { get; set; }
    // Номер текущей волны (растёт с каждой стартованной; 0 — волн ещё не было)
    public int WaveNumber { get; set; }
    // Плановое число волн текущей итерации: проставляется при запуске плана (Э3) и
    // растёт при добавочной волне. 0 — план ещё не запускался. Бейдж «волна N из M»
    // берёт M отсюда, а НЕ из потолка бюджета (тот про расход, а не про план).
    public int PlannedWaves { get; set; }
    // Авто-волны: true — между волнами карточки согласования не появляются (дефолт включён).
    // Переключается на ходу отдельным эндпоинтом (PUT {id}/team-implement/auto).
    public bool AutoWaves { get; set; } = true;
    // Момент старта текущей волны (Э4): по нему сторож ловит зависшую волну. null — волн
    // ещё не было, волна закрыта либо практика уже ждёт решения человека.
    public DateTime? WaveStartedAt { get; set; }
    // Последняя активность волны: закрытие её задачи, перевыдача, старт. Сторож считает
    // таймаут ОТСЮДА, а не от старта волны — иначе длинная, но живая волна (задачи
    // закрываются одна за другой) эскалировалась бы просто за то, что идёт долго.
    // null — активности не было с момента старта, тогда сторож смотрит на WaveStartedAt.
    public DateTime? WaveActivityAt { get; set; }
    // Номер последней ЗАКРЫТОЙ волны (Э4): защита от повторного закрытия — колбэки
    // завершения задач приходят из разных потоков, а закрыть волну можно только раз.
    // Отдельно от WaveStartedAt: тот обнуляется ещё и эскалацией, и волна после ответа
    // человека всё равно обязана закрыться.
    public int ClosedWave { get; set; }
    // Человек нажал «Остановить» (Э4): текущие исполнители дорабатывают, новые волны не
    // стартуют. Снимается решением по карточке остановки («Продолжить») либо новой вводной.
    public bool Stopped { get; set; }
    // Координатор — собеседник чата (Session.PersonaId). null, пока не назначен (Э2).
    public string? CoordinatorPersonaId { get; set; }
    // Планировщик — LLM-персона для декомпозиции по компетенциям (Э2). null — координатор сам.
    public string? PlannerPersonaId { get; set; }
    // Состав исполнителей: пустой список = вся команда проекта; явный — сужает круг.
    public List<string> ExecutorPersonaIds { get; set; } = [];
    // Бюджет текущей итерации (сбрасывается по новой вводной). Не null, пока режим активен.
    public TeamImplementBudget Budget { get; set; } = new();
    // «Координатор не пишет код сам» (по умолчанию включено): у чата-штаба отключены
    // инструменты правки файлов — иначе координатор срежет углы и сделает работу вместо
    // команды. См. «Правило „любая работа — через задачу"» продуктового плана.
    public bool CoordinatorNoCode { get; set; } = true;
    // id карточки плана в ленте чата (Э2 — новый тип сообщения). null — план ещё не опубликован.
    public string? PlanCardId { get; set; }

    // --- Э8: интервью, план-режим и версии плана ---

    // Режим прав человека, сохранённый на входе в интервью: на стадиях интервью и
    // планирования чат работает в `Plan`, а на «Запустить» (Confirming → Wave) возвращается
    // сюда. Живёт на состоянии режима, поэтому переживает рестарт сервера посреди
    // планирования. null — план-режим сейчас не навязан (или выбор уже возвращён).
    public ClaudeMode? SavedMode { get; set; }
    // Версия последнего ОПУБЛИКОВАННОГО плана итерации: карточка «План v2 · обновлён после
    // уточнений». 0 — планов ещё не публиковали (и у состояний, сохранённых до Э8).
    public int PlanVersion { get; set; }
    // Версия плана, по которой человек разрешил работу («Запустить»; у добавочной волны при
    // авто — сама вводная). Волна стартует ТОЛЬКО по ней: иначе после перепланирования
    // авто-волна доигрывала бы отменённый vN-1, обходя обязательное подтверждение (Э8).
    public int ApprovedPlanVersion { get; set; }
    // Раундов вопросов интервью задано по текущей вводной: протокол разрешает максимум 2,
    // дальше «не развилка → допущение». Сбрасывается новой вводной человека.
    public int InterviewRounds { get; set; }
    // Интервью вызвано тупиком в волне (маркер `<escalate:clarify>`), а не входом в итерацию:
    // следующий план — новая версия, и подтверждение карточкой обязательно ДАЖЕ при
    // включённых авто-волнах (авто покрывает волны по неизменному плану, но не смену плана).
    public bool Replanning { get; set; }
    // Первая вводная итерации уже разобрана SessionManager.ResetTeamIterationOnUserInput
    // (волна 3). Отдельно от Stage: с волны 3 дефолтная стадия свежего режима — тоже Interview
    // (спека Э8, «первая стадия — интервью»), поэтому одного Stage мало, чтобы отличить
    // «ни одного сообщения ещё не было» (нужен полный сброс бюджета и вход в план-режим) от
    // «уже второй раунд интервью» (ответ человека, сбрасывать нечего) — оба со Stage=Interview
    // и PlanCardId=null.
    public bool FirstIterationOpened { get; set; }
    // Порядковый номер вводной чата-штаба (растёт на каждой НОВОЙ вводной — ResetTeamIterationOnUserInput
    // и ветка Idle в StartTeamWorkAsync; перепланирование Э8 внутри той же вводной его не трогает,
    // там растёт PlanVersion). Только для файловых путей плана (TeamPlanFileRenderer): слаг папки
    // строится из имени чата, а PlanVersion у каждой новой вводной снова стартует с 1 — без этого
    // счётчика вторая вводная того же чата ссылалась бы на файл первой (прод 2026-08-03, находка Веры).
    // 0 — легаси-состояния до этой правки; рендерер трактует 0 как первую вводную.
    public int IterationNumber { get; set; }
    // Вводная (текст маркера работы), по которой СЕЙЧАС строится план. Живёт на состоянии,
    // чтобы при отказе планировщика человек мог повторить планирование кнопкой карточки,
    // не проходя интервью заново (прод 2026-08-04). Ставится в StartTeamWorkAsync перед
    // вызовом планировщика, обнуляется при успешном плане.
    public string? LastPlanRequest { get; set; }
    // Правка человека к текущему плану («Изменить план»): причина перепланирования.
    // Повтор планирования после сбоя обязан пересобирать план С НЕЙ ЖЕ — без неё повтор
    // вернул бы прежний план, и правка потерялась бы. Живёт и обнуляется вместе с
    // LastPlanRequest. null — план строится без правки (обычная вводная или clarify).
    public string? LastPlanFeedback { get; set; }
}

public class Session
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    // null → чат вне проекта (project-less). set (не init) — задел под будущее «прикрепить проект»
    public string? ProjectId { get; set; }
    // Владелец project-less чата (JWT sub). Для проектных сессий null — владелец резолвится через проект
    public string? OwnerId { get; set; }
    // Закреплён в списке чатов («Закреплённые»)
    public bool IsPinned { get; set; }
    public string? ClaudeSessionId { get; set; }
    public ClaudeMode Mode { get; set; } = ClaudeMode.AcceptEdits;
    // Псевдоним или полный id модели для флага --model. null → дефолтная модель CLI
    public string? Model { get; set; }
    // Уровень reasoning effort для флага --effort (low/medium/high/xhigh/max). null → дефолт CLI
    public string? Effort { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Starting;
    public string? LastMessage { get; set; }
    public int MessageCount { get; set; }
    public string? Name { get; set; }
    // Имя задано явно (вручную/MCP/действие «Обновить название») — авто-заголовок по первому
    // сообщению его НЕ перезаписывает. Очистка имени снимает лок.
    public bool NameLocked { get; set; }
    // Тема чата — ИМЯ компонента lucide-react в PascalCase (Cat, Bug, User, MousePointerClick).
    // Фронт рисует иконку по нему: icons[Topic]. null — темы нет (значок не рисуется).
    public string? Topic { get; set; }
    // Имя агента (.claude/agents/<name>.md), чей промпт инжектируется в системный контекст
    public string? AgentName { get; set; }
    // Персона, от лица которой ведётся чат: задаёт характер,
    // модель и зону контекста (см. Persona). null — обычная сессия.
    // В групповом чате — АКТИВНЫЙ спикер (∈ Participants).
    public string? PersonaId { get; set; }
    // Участники группового чата (2-8 id персон; первый — ведущая). null — обычный чат.
    public List<string>? Participants { get; set; }
    // Собеседника меняли по ходу разговора: в персона-промпт добавляется оговорка,
    // что прошлые ответы в транскрипте могли быть от другого собеседника.
    public bool PersonaSwitched { get; set; }
    // Заметка-итог сессии (кнопка «Итог сессии»): повторная генерация обновляет её, а не плодит дубли
    public string? SummaryNoteId { get; set; }
    // Провайдер/подписка, обслуживающая сессию:
    // "claude" — основная подписка (дефолт, ~/.claude с OAuth);
    // для сторонних провайдеров — ключ из LlmProviders (deepseek, glm);
    // для дополнительных подписок Claude — ключ из ClaudeSubscriptions (sub-*).
    // Единственный источник истины, НЕ выводится из Model на лету.
    public string Provider { get; set; } = "claude";
    // Временный чат: авто-удаление через N минут после последней активности (UpdatedAt). null — обычный
    public int? ExpiresAfterMinutes { get; set; }
    // Opt-out «Истории решений» (ADR-004 §6): true — паспорта изменений из этого чата не снимаются.
    // Уважается только на захвате; уже снятые паспорта не удаляются (решение ADR «ничего не удаляем»).
    public bool ExcludeFromDossiers { get; set; }
    // Момент, когда срок задали. Отсчёт идёт от него, если он ПОЗЖЕ последней активности:
    // иначе «1 час» на чате, неактивном пятый день, снёс бы его на ближайшем тике уборки.
    // Отдельное поле, а не сдвиг UpdatedAt: тот двигает чат в списке и метит его
    // непрочитанным, а смена настройки хранения — не активность. null — срока нет либо
    // состояние от версии до этого поля (тогда считаем от UpdatedAt, как раньше).
    public DateTime? ExpiryAnchor { get; set; }
    // Момент последнего прочтения чата владельцем — синк непрочитанности между устройствами
    // (фронт держит и локальную отметку, непрочитанность = UpdatedAt > max из двух).
    // null — чат ни разу не отмечали прочитанным через API. Скаляр, а не per-user словарь:
    // у сессии ровно один читатель-владелец (GetOwned); при шаринге проектов поле придётся
    // мигрировать. Инвариант: отметка прочтения НЕ двигает UpdatedAt.
    public DateTime? LastReadAt { get; set; }
    // Чат заглушён: браузерные уведомления («нужно решение» / «ход завершён») по нему не
    // показываются. Настройка ЧАТА; общее разрешение браузера — глобальный тумблер в разделе «Уведомления»
    public bool NotificationsMuted { get; set; }
    // Цикл «до готово» (флаг work-loop): не null — ход автопродолжается до маркера завершения
    public SessionWorkLoop? WorkLoop { get; set; }
    // Режим «Командная реализация»: не null — чат работает как
    // штаб фичи (план, задачи на исполнителей, волны, проверка).
    public SessionTeamImplement? TeamImplement { get; set; }
    // Сессия-исполнитель задачи (создана TaskExecutionService): tasks-MCP форсируется включённым
    // независимо от Persona.Tools — исполнитель обязан управлять задачей через mcp__tasks__*.
    // Иначе персона с ограничением tools (без «tasks») теряет tasks-сервер и не может ни прочитать,
    // ни завершить задачу (fallback на встроенный Task-тул → «система задач недоступна»).
    public bool TaskExecution { get; set; }
    // Задача-владелец чата-исполнителя (TaskExecutionService): для отображения контекста
    // («в рамках какой задачи») на плашке чата, в шапке и в артефактах сессии.
    public string? TaskId { get; set; }
    // Онбординг-сессия (фича default-personas-onboarding): "user" | "project" (см. OnboardingKinds),
    // null — обычный чат. Задаёт врезку онбординг-промпта в BuildPersonaLayer и гейт
    // MCP-вызова make-default (назначить дефолт из чата может только онбординг-сессия).
    public string? OnboardingKind { get; set; }
    // Персона, созданная в ходе этой онбординг-сессии через personas_create (null — не создана
    // или выбрана существующая). Финализация досевает профиль дефолта (Coordinator+Full+manage)
    // ТОЛЬКО ей, а не выбранной существующей: молчаливая дозапись прав готовой персоне —
    // тихая эскалация (как и ручная смена дефолта из настроек). Персистится в sessions.json.
    public string? OnboardingCreatedPersonaId { get; set; }
    // Origin автоматизации: null — обычный чат; иначе — id правила PersonaAutomationRule,
    // чат которого создан движком проактивности. Для фильтрации авто-чатов и трассировки.
    public string? AutomationRuleId { get; set; }
    // Отдельное git worktree чата: рабочая папка сессии вместо project.RootPath.
    // Путь всегда ХОСТОВЫЙ (как Project.RootPath); в песочницу транслируется при запуске.
    // null — чат живёт в основном дереве проекта. Только для проектных сессий.
    public string? WorktreePath { get; set; }
    // Имя ветки worktree (для метки в git-баре); заполняется вместе с WorktreePath
    public string? WorktreeBranch { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    // Теги сессии (из реестра проекта) — per-owner изоляция
    public List<string> Tags { get; set; } = [];

    // Тип происхождения чата — производный от TaskId/AutomationRuleId, единая точка истины.
    public ChatOrigin Origin => TaskId != null ? ChatOrigin.Task
        : AutomationRuleId != null ? ChatOrigin.Automation
        : ChatOrigin.Manual;

    // Резолвер «задача → чат-источник»: назначает TaskManager при старте (DI в модель не
    // пробросить — Session сериализуется напрямую во всех точках отдачи). Истина живёт
    // в TaskItem.SourceSessionId, здесь только вычисление.
    public static Func<string, string?>? TaskSourceSessionResolver { get; set; }

    // Ручная группировка (drag-and-drop в списке чатов): родитель, назначенный пользователем.
    // Побеждает авто-связь по задаче. Пара с ParentDetached описывает три состояния, поэтому
    // менять их обоих можно ТОЛЬКО через SessionManager.SetParent — он держит инвариант
    // (взаимоисключающие поля, проверка цикла и скоупа).
    public string? ParentOverrideId { get; set; }
    // Чат явно вынесен в корень пользователем: гасит авто-связь по задаче. Отдельный флаг,
    // а не пустая строка в ParentOverrideId — «не задано» и «задано как корень» это разные
    // состояния, и sentinel-строка их бы склеила.
    public bool ParentDetached { get; set; }

    // Родительский чат: ручная группировка, иначе — чат, в котором была создана задача
    // сессии-исполнителя (TaskId → Task.SourceSessionId). Вычисляется, не хранится — как Origin.
    // null — корневой чат либо задача удалена (чат всплывает в корень — принято осознанно).
    // TaskId ручная группировка не трогает: связь чата с задачей (плашка, артефакты,
    // TaskDelegationDepth) живёт своей жизнью и перетаскиванием не рвётся.
    public string? ParentSessionId =>
        ParentOverrideId is not null ? ParentOverrideId
        : ParentDetached ? null
        : TaskId != null ? TaskSourceSessionResolver?.Invoke(TaskId) : null;

    // Резолвер «задача → глубина делегирования»: назначает TaskManager при старте (как
    // TaskSourceSessionResolver). Истина живёт в TaskItem.DelegationDepth.
    public static Func<string, int>? TaskDelegationDepthResolver { get; set; }

    // Глубина цепочки делегирования задачи-исполнителя этого чата (0 — обычный чат либо
    // задача без глубины). Вычисляется, не хранится. Используется гейтом TASKS_EXECUTE
    // (ClaudeSession.BuildTurnMcpConfig): чат-исполнитель глубины >= 3 не запускает нового.
    public int TaskDelegationDepth =>
        TaskId != null ? TaskDelegationDepthResolver?.Invoke(TaskId) ?? 0 : 0;

    // Резолвер «задача → выполнена?»: назначает TaskManager при старте (как
    // TaskSourceSessionResolver). Истина живёт в TaskItem.Status == Done.
    public static Func<string, bool>? TaskDoneResolver { get; set; }

    // Связанная задача чата-исполнителя выполнена (TaskId != null и задача в статусе Done).
    // true только для чатов выполненных задач (бывший «архив»); false — обычные чаты и живые
    // задачи. Вычисляется, не хранится — как Origin/ParentSessionId. Фронт объединяет его с
    // статусом Finished в чип «Готово» фильтра чатов (маппинг статусов, макет A).
    public bool TaskDone => TaskId is not null && (TaskDoneResolver?.Invoke(TaskId) ?? false);
}
