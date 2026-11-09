// Типы домена «Командная реализация» (переехали из types/index.ts, S5-P4). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

import type { Mode } from '../../lib/modes';

// === Режим «Командная реализация» ===
// Стадии непрерывного контура — совпадают с wire-токенами TeamImplementStage на бэке
export type TeamImplementStage =
  | 'interview'         // координатор спрашивает человека, прежде чем планировать (Э8)
  | 'planning'          // координатор готовит карточку плана
  | 'confirming'        // план ждёт единственного согласования
  | 'wave'              // волна исполнителей в работе
  | 'awaitingDecision'  // эскалация: ждёт решения человека
  | 'checking'          // финальная проверка (сборка/тесты)
  | 'idle';             // итерация закрыта, режим ждёт новой вводной

// Бюджет итерации: счётчики «израсходовано» + потолки (сбрасывается по новой вводной).
// wakeups — срочные вызовы координатора докладом-блокером снизу (свой потолок).
// maxWavesAfter / maxTasksAfter (волна 1 team-blocker-honest) — новые потолки после
// расширения, посчитанные на бэке (Max + max(0, plan - left)). 0 — плана нет, плашка
// бюджета скрывается. Раньше плашка показывала «потолок поднимется до N», где N был
// размером плана — это расходилось с реальностью при ненулевом расходе.
export interface TeamImplementBudget {
  tasksUsed: number;
  wavesUsed: number;
  runsUsed: number;
  retriesUsed: number;
  wakeupsUsed: number;
  maxTasks: number;
  maxWaves: number;
  maxRuns: number;
  maxRetries: number;
  maxWakeups: number;
  maxWavesAfter: number;
  maxTasksAfter: number;
}

// Состояние режима на сессии (Session.teamImplement); null — режим выключен.
// Состав исполнителей: пустой список = вся команда проекта.
export interface SessionTeamImplement {
  stage: TeamImplementStage;
  waveNumber: number;
  // Плановое число волн текущей итерации (из карточки плана); 0 — план ещё не запускался.
  // Бейдж «волна N из M» берёт M отсюда, а не из потолка бюджета — тот про расход
  plannedWaves: number;
  autoWaves: boolean;
  // Человек нажал «Остановить»: текущие исполнители дорабатывают, новые волны не стартуют.
  // Снимается решением «Продолжить» по карточке остановки либо новой вводной
  stopped: boolean;
  coordinatorPersonaId?: string | null;
  plannerPersonaId?: string | null;
  executorPersonaIds: string[];
  budget: TeamImplementBudget;
  // «Координатор не пишет код сам»: у чата-штаба отключены инструменты правки файлов
  coordinatorNoCode: boolean;
  planCardId?: string | null;
  // Э8: план-режим навязан чату (стадии интервью/планирования) — селектор режима в
  // композере показывает «план» и заблокирован. Присутствует у live-состояния (событие
  // team_implement присылает готовое значение); при REST-гидратации до первого события
  // считаем его сами из savedMode — см. teamImplementModeLocked()
  modeLocked?: boolean;
  // Э8: режим прав человека, сохранённый на входе в план-режим (raw-поле REST-гидратации,
  // SessionTeamImplement.SavedMode с бэка) — вернётся после согласования плана. У live-события
  // его нет, там сразу приходит посчитанный modeLocked
  savedMode?: Mode | null;
  // Э8: версия текущего плана итерации (0 — планов ещё не было). Имя совпадает и на REST,
  // и в live-событии — маппится напрямую
  planVersion: number;
}

// Live-состояние режима (из события team_implement)
export interface TeamImplementState extends SessionTeamImplement {
  active: boolean;
}

// === Пульс волны командной реализации (Э2 КР-наблюдаемости) ===
// Эфемерное состояние: приходит WS-событием раз в минуту в чат штаба, в ленту НЕ
// кладётся, в историю не пишется. Без пульса (старый бэкенд) бейдж показывает только
// стадию — обратная совместимость.
export type TeamWaveLiveness = 'alive' | 'quiet' | 'stalled' | 'dead';

// То, что шлёт бэкенд в WS-событии team_wave_pulse (краткая форма, без списка задач).
// stage ограничен двумя значениями: волна исполнителей и финальная проверка — обе стадии
// могут «дышать» (задачи ещё крутятся), остальные стадии пульса не шлют
export interface TeamWavePulse {
  stage: 'wave' | 'checking';
  waveNumber: number;
  plannedWaves: number;
  tasksActive: number;
  tasksTotal: number;
  lastActivityAt: string;       // ISO, момент последней активности штаба
  quietSeconds: number;
  liveness: TeamWaveLiveness;
}

// Задача волны (расширенный REST-снапшот)
// Совпадает с TaskItemStatus на бэке. Шире делать нельзя: бэк пришлёт незнакомый
// статус, фронт его не распарсит — задача пропадёт из списка молча
export type TeamWaveTaskStatus = 'todo' | 'inProgress' | 'done';

export interface TeamWaveTask {
  id: string;
  title: string;
  executorPersonaId: string | null;
  status: TeamWaveTaskStatus;
  updatedAt: string;
  startedAt: string | null;
}

// Полный снимок пульса + список задач + пороги (REST /api/chats/{id}/team-wave-snapshot)
// Refetch при КАЖДОМ открытии поповера — после реконнекта кэш протухнет, а человек
// должен видеть свежее состояние
export interface TeamWaveSnapshot extends TeamWavePulse {
  tasks: TeamWaveTask[];
  thresholds: { quietMinutes: number; stalledMinutes: number };
}

// === КР-наблюдаемость, этап 3: перезапуск без потери работы ===

// Ответ POST /api/chats/{id}/team-wave/tasks/{taskId}/restart.
// reissued — перевыдана тем же путём, что провал хода (потолок попыток общий);
// escalated — перевыдача не разрешена, карточка с решением легла в ленту штаба;
// failed — исполнитель не стартовал (причина — в message)
export type TeamTaskRestartOutcome = 'reissued' | 'escalated' | 'failed';

export interface TeamTaskRestartResponse {
  outcome: TeamTaskRestartOutcome;
  message: string;
}

// Ответ POST /api/chats/{id}/team-wave/restart. requiresConfirm=true — есть живые
// исполнения (liveTasks) либо волна выглядит живой: повторный вызов с confirm=true
// останавливает их и перевыдаёт незакрытое. Под-задачи в Done не трогаются
export interface TeamWaveRestartResponse {
  requiresConfirm: boolean;
  liveTasks?: string[];
  reissued: number;
  escalated: number;
  failed: number;
  message: string;
}

// Ответ POST /api/chats/{id}/team-wave/restart-turn. outcome=restarted — ход продолжится
// с сохранённым контекстом (--resume), fresh — контекст сброшен (startFresh при
// повреждённом транскрипте). Отказ 409 с code=transcript_damaged предлагает «начать заново»
export interface TeamTurnRestartResponse {
  outcome: 'restarted' | 'fresh';
  resumed: boolean;
  message: string;
}

// Код ошибки 409 restart-turn: файл разговора повреждён, resume запрещён
export type TeamTurnRestartErrorCode = 'transcript_damaged';

// Под-задача плана командной реализации: единица раздачи (в Э3 из неё создаётся задача).
// executorRationale — одна строка «почему именно он» от планировщика; в ней же приходят
// пометки бэка «проверьте выбор» / «не обосновал», когда подбор ненадёжен.
export interface TeamPlanSubtask {
  id: string;
  title: string;
  goal: string;
  executorPersonaId?: string | null;
  executorRationale: string;
  files: string[];
  wave: number;
  doneCriteria: string;
}

// Структурный план командной реализации (карточка в ленте штаба).
// waveCount/executorCount считает бэкенд — подзаголовок карточки собирается из них.
export interface TeamPlan {
  id: string;
  request: string;
  summary: string;
  plannerPersonaId?: string | null;
  approved?: boolean | null;
  createdAt: string;
  waveCount: number;
  executorCount: number;
  subtasks: TeamPlanSubtask[];
  // Э8: версия плана в итерации — 1 у первого, растёт с каждым перепланированием после
  // интервью. Карточка показывает «План v2 · обновлён после уточнений»
  version: number;
  // Э8: допущения планировщика — путь «вопросов нет» (утверждаются вместе с планом)
  assumptions: string[];
  // Э8: «что изменилось» у плана vN относительно предыдущей версии; пусто у v1
  changes: string[];
  // Замысел планировщика на 3–5 строк (решение 2026-08-02) — пусто/undefined у истории
  // до этой доработки и когда планировщик не заполнил поле: блок в карточке не рисуем
  intent?: string;
  // Путь к файлу полного плана относительно корня проекта — null у глобального чата
  // без проекта либо когда запись не удалась; undefined у истории до этой доработки
  planFilePath?: string | null;
}

// Решение человека по карточке плана (метод хаба RespondTeamPlan). edit — правка плана
// текстом (feedback): сервер сам гасит текущую карточку и пересобирает план версией vN+1
export type TeamPlanDecision = 'run' | 'reassign' | 'cancel' | 'edit';

// Триггер остановки практики — wire-токены TeamEscalationKind с бэка.
// waveGate — не проблема, а гейт «волна закрыта, запускать следующую?» при снятом авто;
// stopped — пауза по команде человека; waveAdded — вовсе не остановка (см. ниже)
export type TeamEscalationKind =
  | 'blocker'
  | 'taskFailed'
  | 'planDeviation'
  | 'checkFailed'
  | 'productDecision'
  | 'budgetExhausted'
  | 'waveStalled'
  | 'waveGate'
  | 'stopped'
  // Информация (Э5): по новой вводной развёрнута добавочная волна. Работа уже идёт,
  // клика карточка не ждёт — только показывает состав и даёт «Остановить»
  | 'waveAdded'
  // Тупик в волне (Э8): координатор не знает, как действовать дальше — требования неясны.
  // Волны на паузе, дальше — интервью (ASK-карточки); без кнопок, тон не тревожный
  | 'needsClarification';

// Кнопка карточки остановки: id — wire-токен решения, label — подпись для человека
export interface TeamEscalationAction {
  id: string;
  label: string;
}

// Карточка остановки режима «Командная реализация» (Э4). Форма — как в истории чата
// (StoredTeamEscalationMessage.escalation); live-событие приходит плоским и собирается
// в этот же объект редьюсером
export interface TeamEscalation {
  id: string;
  kind: TeamEscalationKind;
  title: string;
  details: string;
  actions: TeamEscalationAction[];
  taskId?: string | null;
  // Название задачи (волна 1 team-blocker-honest, дефект 1430b732): снимок из штаба на
  // момент публикации. Идёт в подпись диалога снятия — раньше там стоял заголовок
  // карточки («Исполнитель застрял: …»), и человек не видел, какую задачу закрывает.
  // null — штаб не смог подтянуть (задача удалена) или старая версия бэка: фолбэк на title.
  taskTitle?: string | null;
  wave: number;
  // Только в истории — у live-события времени нет
  createdAt?: string;
  resolved: boolean;
  chosenActionId?: string | null;
  // Автор карточки (Э8): координатор на момент публикации — карточка идёт от его лица.
  // null — персоны у штаба нет, шапка деградирует до обезличенного варианта
  personaId?: string | null;
  // Примечание от штаба при chosenActionId="resolvedByStaff": чем координатор закрыл
  // карточку. Поле опциональное — старый бэкенд и пустое примечание дают фронт без него;
  // рисуем «Снят штабом» без тела вместо «Снят штабом: undefined». chosenActionId="message"
  // означает «человек ответил сообщением» — это уже отдельная ветка без resolutionNote
  resolutionNote?: string | null;
}
