// Типы домена «Задач»: «Колонка доски проекта» (переехали из types/index.ts, S5-P9), «Резюме фичи» (хвост S5-P, вместе с методом из api.ts) и весь контракт задач: Task и его метки, DTO создания/правки, дефекты, критерии приёмки, предгейт, доска агентов (шаг P13 линии разноса). types/index.ts ре-экспортирует их через `export type *`, lib/api.ts — FeatureSummaryResult именованным реэкспортом: внешние импорты не меняются.

// ModelTierValue (уровень модели исполнителя) остаётся в хребте: его читает не только Task
// (напр. features/personas/SpecialtyEditView.tsx), поэтому здесь только `import type`.
import type { ModelTierValue } from '../../types';

// Колонка Kanban-доски проекта. category — семантическая категория статуса
// (за ней recurrence/календарь/Claude/MCP); несколько колонок могут делить категорию.
export interface BoardColumn {
  id: string;
  name: string;
  category: TaskStatus;   // 'todo' | 'inProgress' | 'done'
  color?: string;
}

// Резюме фичи (W5-F2, «Точка 6 · резюме»): результат модельного анализа хронологии.
// text — markdown с разделами «Сделано», «Где застряло», «Дальше» (или голые пункты
// без заголовков — пока так просит промпт feature-summary); model=null — локальная
// модель; cached — результат взят из кэша бэка, без нового прогона. Ошибки: 502 —
// модель не ответила (причина в error), 503 — подсистема выключена, 404 — фичи нет.
export interface FeatureSummaryResult {
  text: string;
  generatedAt: string;
  model: string | null;
  cached: boolean;
  usage?: unknown;
  // Число событий хронологии, на которых построено резюме (W4-ANB): для шапки
  // «по N событиям» (бейдж устаревания — без числа). Бэк отдаёт всегда; optional —
  // защита от ответов старой версии сервера.
  eventCount?: number;
}

// Форма ChatRef (бэк Models/ChatRef.cs, шаг S2d-4b, К5): ссылка на чат, связанный с задачей.
// Видимый чат — id и название остаются в своих id-полях задачи (linkedSessionId и др.),
// этой формы нет. Чужой приватный (не виден зрителю) — id-поле обнулено (null), рядом
// стоит эта запись: sessionId нет, title — подпись вместо названия, private — признак.
export interface ChatRef {
  sessionId?: string | null;
  title?: string | null;
  private?: boolean;
  authorUserId?: string | null;   // чей чат: id автора, не название и не id чата
}

// --- Задачи, дефекты, критерии приёмки, предгейт (перенесены из types/index.ts, шаг P13) ---

// Значения enum-ов приходят с бэка в camelCase (JsonStringEnumConverter)
export type TaskStatus = 'todo' | 'inProgress' | 'done';
export type TaskPriority = 'urgent' | 'high' | 'medium' | 'low';
export type TaskAssignee = 'me' | 'claude';

// Повторение задачи. weekdays — ISO-дни (1=Пн … 7=Вс), только для weekly.
// type 'none' — wire-сентинел в UpdateTaskDto: убрать повторение
export type TaskRecurrenceType = 'none' | 'daily' | 'weekly' | 'monthly' | 'yearly';
export interface TaskRecurrence {
  type: TaskRecurrenceType;
  interval: number;          // каждые N периодов
  weekdays?: number[];
  until?: string;            // YYYY-MM-DD включительно
}

export interface TaskSubtask {
  id: string;
  title: string;
  isDone: boolean;
}

export interface Task {
  id: string;
  // Отсутствует у личной задачи (вне проекта)
  projectId?: string;
  ownerId?: string;
  title: string;
  description: string;      // markdown; у облегчённого элемента списка (listItem) всегда ''
  // Облегчённый элемент СПИСКА (GET /api/tasks, /projects/{id}/tasks): description='' и
  // resultMarkdown отсутствуют, вместо них — признаки и превью. Полный текст — через
  // ensureFullTask(id) (GET /api/tasks/{id}); у полной задачи (ответ create/update/get,
  // realtime task_changed) поля listItem нет.
  listItem?: boolean;
  hasDescription?: boolean;
  hasResult?: boolean;
  descriptionPreview?: string;   // начало описания (≤200 символов + «…» при усечении)
  status: TaskStatus;
  columnId?: string;         // колонка доски проекта; отсутствует = дефолтная колонка категории
  priority: TaskPriority;
  dueDate?: string;          // YYYY-MM-DD
  dueTime?: string;          // HH:MM
  reminderMinutes?: number;  // офсет напоминания до срока в минутах (0 = в момент срока)
  reminderSentAt?: string;   // UTC-отметка отправленного напоминания
  assignee?: TaskAssignee;
  // Общий проект: человек-исполнитель и кто поставил задачу (S2b-1). Нет у задач личного
  // проекта и у старого бэка — тогда вид карточки прежний
  assigneeUserId?: string | null;
  createdByUserId?: string | null;
  recurrence?: TaskRecurrence;
  seriesId?: string;         // общий id серии регулярной задачи
  // S2d-4b (К5): чат, невидимый зрителю, приходит обнулённым (null), форма — в linkedChat
  linkedSessionId?: string | null;
  // Режим «Исполнение фичи»: связь задачи с фичей (эпик) и кодом шага.
  // Аддитивно и nullable — старые задачи без разметки не несут этих полей.
  featureId?: string | null;   // id задачи-эпика
  stepRef?: string | null;    // код шага из плана («Ш1», «D1c»)
  lane?: string | null;       // линия шага («бэк», «фронт»); подпись берётся как есть
  // Роль задачи в фиче (бэк TaskItem.Role): feature · cut · plan · step · rework · review · qa · final · merge · consult.
  role?: string | null;
  // Чат, породивший задачу (бэк TaskItem.SourceSessionId). Для фичи — связь эпика с чатом:
  // GET /api/tasks/features?sessionId= выбирает эпики по этому полю (и ExecutorLaunchedBySessionId).
  sourceSessionId?: string | null;
  // Маска чужих приватных чатов (бэк TaskView.MaskForeignChats, S2d-4b): форма стоит рядом с
  // обнулённым id-полем и приходит ТОЛЬКО когда чат не виден зрителю; для видимого чата её нет.
  linkedChat?: ChatRef | null;
  sourceChat?: ChatRef | null;
  executorLaunchedByChat?: ChatRef | null;
  reportToChat?: ChatRef | null;
  personaId?: string;        // исполнение от лица персоны (assignee=claude)
  // Уровень модели исполнителя (слот strong/medium/weak); отсутствует — не задан:
  // модель берётся от персоны-исполнителя и назначения места «Исполнитель задач»
  modelTier?: ModelTierValue;
  // Время жизни чата исполнения (мин от последней активности); undefined/null — бессрочно
  executionExpiresAfterMinutes?: number | null;
  claudeStartedAt?: string;  // отметка запуска Claude-исполнителя
  claudeResult?: 'success' | 'error';  // итог последнего запуска (null — выполняется/не запускалась)
  resultMarkdown?: string;            // Markdown-итог выполнения (прикрепляет исполнитель через MCP)
  linkedFiles: string[];
  subtasks: TaskSubtask[];
  labels: string[];
  // Связь с чекбоксом заметки (флаг notes-task-sync)
  sourceNoteId?: string;
  sourceNoteLine?: number;
  order: number;             // порядок карточки на Kanban-доске (ручная сортировка)
  createdAt: string;
  updatedAt: string;
  completedAt?: string;      // дата+время завершения (статус стал done); null — не завершена
  // Тип карточки: обычная задача или дефект (правила DefectRules). У обычных задач — 'task'
  kind?: TaskKind;
  // Шаги воспроизведения дефекта (null у обычных задач; для дефекта — всегда присылается)
  repro?: DefectRepro | null;
  // Подтверждение проверки (null — не подтверждено). Сбрасывается при уходе из Done
  verification?: TaskVerification | null;
  // Критерии приёмки и свидетельства по ним (пусто/нет — задача без критериев)
  acceptance?: AcceptanceCriterion[];
  evidence?: AcceptanceEvidence[];
  // Последний результат предгейта (бэк TaskItem.Pregate): бывает только у шага/доработки;
  // null/нет — не прогонялся. Аддитивно: старые задачи читаются без поля.
  pregate?: PregateRecord | null;
  // Исход дефекта: 'closedWithoutCheck' — внутренний путь закрытия без проверки
  outcome?: DefectOutcome | null;
  // Фикс-волна 4 team-blocker-honest: метки от терминального отказа хода исполнителя
  // (см. ExecutorStopClassifier). executorStoppedAt != null — карточка ждёт человека.
  executorStoppedAt?: string;
  executorStopReason?: string;
  // Явное ожидание человека (Update AwaitingHumanSince): с какого момента и почему.
  // «Ход идёт» (спиннер «AI работает») — только без остановки и без ожидания,
  // тот же предикат, что бэк FeatureProjector.IsTurnRunning.
  awaitingHumanSince?: string;
  awaitingHumanReason?: string;
  /** Исполнитель ждёт устройство локального проекта (ADR-016): с какого момента и почему */
  deviceWaitSince?: string | null;
  deviceWaitReason?: string | null;
  // Метка снятия человеком по карточке блокера (DropSubtaskAsync): если стоит —
  // карточка закрыта человеком, правка статуса от агента отвергается. Человек может
  // снять метку, перетащив задачу обратно в Todo/InProgress.
  droppedByHumanAt?: string;
  // UI-проекция повторяющейся задачи в календаре (не приходит с бэка):
  // occurrenceOf — id реального экземпляра серии, который надо открыть по клику;
  // virtual — признак вычисленного будущего повтора (реально существует только один экземпляр)
  occurrenceOf?: string;
  virtual?: boolean;
}

// Элемент единой выдачи поиска (флаг unified-search)
export interface SearchHit {
  type: 'note' | 'task';
  id: string;
  title: string;
  context: string;   // источник заметки / контекст задачи
  snippet: string;
  score?: number | null;
  url: string;       // hash-диплинк
}

// Кандидат в задачу, извлечённый из чата (флаг chat-extract-tasks). Ещё не создан.
export interface ExtractedTaskCandidate {
  title: string;
  due?: string | null;
  priority?: TaskPriority | null;
}
export interface ExtractTasksResponse {
  projectId?: string | null;
  tasks: ExtractedTaskCandidate[];
}

// Строка-чекбокс заметки + связанная задача (флаг notes-task-sync)
export interface NoteTask {
  line: number;
  text: string;
  done: boolean;
  due?: string | null;
  taskId?: string | null;
  taskStatus?: string | null;
}

export interface CreateTaskDto {
  // Клиентский id для офлайн-создания (идемпотентный replay на сервере). Обычно не задаётся.
  id?: string;
  title: string;
  description?: string;
  status?: TaskStatus;
  columnId?: string;         // колонка доски проекта при создании (быстрое добавление)
  priority?: TaskPriority;
  dueDate?: string;
  dueTime?: string;
  reminderMinutes?: number;
  assignee?: TaskAssignee;
  recurrence?: TaskRecurrence;
  linkedSessionId?: string | null;   // null — не задано (то же, что поле отсутствует)
  personaId?: string;        // исполнение от лица персоны
  // Уровень модели исполнителя; не указано — не задан (модель по персоне и месту)
  modelTier?: ModelTierValue;
  // Не указано — дефолт 1440 (сутки); отрицательное — бессрочно; N>=0 — TTL в минутах.
  // Имеет смысл только при исполнителе Claude/персона.
  executionExpiresAfterMinutes?: number;
  resultMarkdown?: string;
  linkedFiles?: string[];
  subtasks?: { title: string }[];
  labels?: string[];
  // Тип карточки: обычная задача или дефект (правила DefectRules на бэке).
  // Не указано — сервер сохраняет прежний Kind. Repro/Verification имеют смысл
  // только у дефектов, но бэк их не трогает если они при присылке type=task —
  // переключение прячет блоки в UI, данные остаются в JSON
  kind?: TaskKind;
  // Шаги воспроизведения. Целиком заменяет объект на бэке — форма шлёт все
  // три поля, иначе пользователь потерял бы expected/actual при правке только steps
  repro?: DefectRepro | null;
  // Подтверждение проверки: на бэке PersonaId подставляется из сессии (X-Caller-Session-Id),
  // клиентский PersonaId не учитывается. null — очистить (снять вердикт)
  verification?: TaskVerification | null;
}

// Тип карточки. Defect подчиняется правилам DefectRules на бэке: нельзя закрыть
// без Verification (или Outcome=closedWithoutCheck), нельзя попасть в колонку Role=review
// без Repro.steps
export type TaskKind = 'task' | 'defect';

// Шаги воспроизведения дефекта: steps — обязательное описание, expected/actual —
// необязательные комментарии автора и наблюдателя
export interface DefectRepro {
  steps: string;
  expected?: string;
  actual?: string;
}

// Критерий приёмки задачи и свидетельство его выполнения (связь по criterionId)
export interface AcceptanceCriterion { id: string; text: string }
export interface AcceptanceEvidence { criterionId: string; how: string; result: string }

// Результат предгейта шага (бэк record PregateRecord): sha — коммит, по которому гнали проверки;
// green — вердикт; summary — вывод красных проверок; at — момент (UTC);
// redStreak — сколько красных подряд по одному redKey (зелёный обнуляет);
// nothingApplicable — «нечего проверять» (green при этом false, «зелёным» не считается).
export interface PregateRecord {
  sha: string;
  green: boolean;
  summary: string;
  at: string;
  redStreak: number;
  redKey?: string | null;
  nothingApplicable?: boolean;
  outcome?: 'green' | 'red' | 'neutral';
}

// Подтверждение проверки: personaId == null означает проверку человеком (UI-форма
// владельца задачи), не-null — конкретной персоной (чаще всего — исполнившей дефект).
// Поле бэк подставляет из сессии, клиент его НЕ шлёт
export interface TaskVerification {
  verifiedAt: string;
  personaId?: string | null;
  notes?: string | null;
}

// Исход дефекта — сейчас только внутренний путь «снято без проверки»
// (NoteTaskSyncService / TeamWaveService). Прочие исходы придут следующими волнами
export type DefectOutcome = 'closedWithoutCheck';

// Пустая строка в dueDate/dueTime/linkedSessionId = очистить поле, undefined = не менять
export interface UpdateTaskDto {
  title?: string;
  description?: string;
  status?: TaskStatus;
  priority?: TaskPriority;
  dueDate?: string;
  dueTime?: string;
  // Отрицательное значение = убрать напоминание, undefined = не менять
  reminderMinutes?: number;
  assignee?: TaskAssignee;
  // Человек-исполнитель общего проекта (S2b-1): undefined = не менять, null = снять,
  // guid участника = назначить
  assigneeUserId?: string | null;
  // type 'none' = убрать повторение, undefined = не менять
  recurrence?: TaskRecurrence;
  // '' = очистить, null = не менять (маска S2d-4b), undefined = не менять
  linkedSessionId?: string | null;
  // Персона-исполнитель: '' = убрать, undefined = не менять
  personaId?: string;
  // Уровень модели исполнителя: '' = сбросить, undefined = не менять
  modelTier?: ModelTierValue | '';
  // Время жизни чата исполнения: отрицательное = бессрочно, undefined = не менять, N>=0 = TTL
  executionExpiresAfterMinutes?: number;
  resultMarkdown?: string;    // '' = очистить, undefined = не менять
  linkedFiles?: string[];
  subtasks?: TaskSubtask[];
  labels?: string[];
  order?: number;            // порядок карточки на доске (drag); undefined = не менять
  columnId?: string;         // колонка доски проекта; undefined = не менять, '' = сброс на дефолт
  projectId?: string;        // смена проекта: guid = привязать, '' = сделать личной, undefined = не менять
  // Тип карточки: undefined = не менять, 'defect'/'task' = сменить (см. CreateTaskDto)
  kind?: TaskKind;
  // Шаги воспроизведения дефекта. Целиком заменяет объект (как Labels) — форма шлёт
  // все три поля разом. null — очистить
  repro?: DefectRepro | null;
  // Подтверждение проверки: null — очистить (например, при возврате из Done)
  verification?: TaskVerification | null;
  // Исход дефекта: только 'closedWithoutCheck' в этой волне. undefined = не менять
  outcome?: DefectOutcome | null;
}

// Элемент доски агентов (диспетчерская: GET /api/board/agents)
export interface BoardItem {
  taskId: string;
  title: string;
  projectId?: string;
  sessionId?: string;
  column: 'queue' | 'working' | 'waiting' | 'done';
  sessionStatus: string;
  personaId?: string;
  currentToolName?: string;
  startedAt?: string;
  permissionPending: boolean;
}
