// ПОТОЛОК РОСТА (frontend-api-split-2026-11.md): файл только худеет. Лимит строк — в
// lib/__tests__/apiFileBudget.test.ts; он снижается после каждого выноса домена в
// features/<домен>/api.ts, поднимать его нельзя. Новые методы — сразу в features/<домен>/.
import type { Session, WorkflowAgentInfo, WorkflowAgentBlock, NoteDetail, ExtractTasksResponse, PromptSnapshot, PromptSection, LocalHandsChatStatus, SessionContextEntry } from '../types';
import { localModelApi } from '../features/localModel/api';
import { providerAccountsApi } from '../features/providerAccounts/api';
import { accountApi } from '../features/account/api';
import { deployApi } from '../features/deploy/api';
import { hostControlApi } from '../features/hostControl/api';
import { modelsApi } from '../features/models/api';
import { spendApi } from '../features/spend/api';
import { modulesApi } from '../features/modules/api';
import { homeApi } from '../features/home/api';
import { chatAuxApi } from '../features/chat/api';
import { videoApi } from '../features/video/api';
import { devicesApi } from '../features/desktop/api';
import { spheresApi } from '../features/spheres/api';
import { specialtiesApi } from '../features/specialties/api';
import { knowledgeApi } from '../features/knowledge/api';
import { dossiersApi } from '../features/dossiers/api';
import { telemetryApi } from '../features/telemetry/api';
import { skillsApi } from '../features/skills/api';
import { plansApi } from '../features/plan/api';
import { imageGenerationApi } from '../features/imageGeneration/api';
import { mcpApi } from '../features/mcp/api';
import { readerApi } from '../features/reader/api';
import { pluginsApi } from '../features/plugins/api';
// eslint-disable-next-line module/no-cross-feature-import -- lib/api собирает объект api из api-модулей фич spread'ом; публичного index.ts у notes нет (баррел заведётся отдельно)
import { notesApi } from '../features/notes/api';
import { docsApi } from '../features/docs/api';
import { tasksApi } from '../features/tasks/api';
import { personasApi } from '../features/personas/api';
import { gitApi } from '../features/git/api';
import { filesApi } from '../features/files/api';
import { projectsApi } from '../features/projects/api';
import { readStoredToken, request, writeStoredToken } from './offline';
import { forgetChatContextSession } from './chatContext/forget';
import { projectRouteOf, uploadAgentAttachment } from './deviceAgent';
import type { HistoryCursorQuery, HistoryReply } from './historyTail';

// Query-строка курсора докачки истории: after обязателен, fp — только для настоящего курсора
const historyCursorParams = (q: HistoryCursorQuery) =>
  `after=${q.after}${q.fp ? `&fp=${encodeURIComponent(q.fp)}` : ''}`;

// Типы «Проектов» (кандидат значка, «Архитектура») живут в features/projects/types.ts
export type * from '../features/projects/types';
// Типы «Локальных моделей» живут в features/localModel/types.ts, методы — в features/localModel/api.ts
export type * from '../features/localModel/types';
// Типы «Аккаунтов провайдеров» (Higgsfield) живут в features/providerAccounts/types.ts
export type * from '../features/providerAccounts/types';
// Типы «Аккаунта» (слоты моделей) живут в features/account/types.ts
export type * from '../features/account/types';
// Тип внешнего модуля (ModuleInfo) живёт в features/modules/types.ts
export type * from '../features/modules/types';
// Типы питания и пульта удалённых команд живут в features/hostControl/types.ts
export type * from '../features/hostControl/types';
// Типы выкатки (трей-раннер и журнал из чата) живут в features/deploy/types.ts
export type * from '../features/deploy/types';

// Потолок ожидания «Продолжить в новом чате»: ход записки-передачи — полноценный ход
// агента, бэкенд ждёт его до HandoffTurnTimeout = 15 мин (ChatContinueService.cs). Берём
// чуть больше (16 мин): обрыв по этому таймауту — RequestTimeoutError («Сервер не ответил
// за N с»), а не OfflineError, и приложение не уводится в офлайн (offline.ts:423–447).
const CONTINUE_TIMEOUT_MS = 16 * 60 * 1000;

// Потолок ожидания «Перенести в новый чат» (фича prompt-cache, шаг F-4): тёплый путь —
// видимый ход самого чата, бэкенд ждёт его до WarmTurnTimeout = 5 мин (ChatHandoffService.cs);
// холодный — быстрый раннер по транскрипту. Берём 6 мин: обрыв по этому таймауту —
// RequestTimeoutError, а не OfflineError (тот же приём, что у CONTINUE_TIMEOUT_MS).
const HANDOFF_TIMEOUT_MS = 6 * 60 * 1000;

export type { WorkflowAgentInfo, WorkflowAgentBlock };

// Типы «Знаний» (DifyDocument) живут в features/knowledge/types.ts
export type * from '../features/knowledge/types';
// Типы ридера (ReaderReadResult) живут в features/reader/types.ts
export type * from '../features/reader/types';
// Типы «Плагинов владельца» живут в features/plugins/types.ts; panelEntryPath — публичный
// экспорт и раньше (снаружи сейчас не импортируется), набор экспортов не меняем
export type * from '../features/plugins/types';
export { panelEntryPath } from '../features/plugins/api';
// Контекст worktree-чата для git-запросов: состояние живёт в features/git/api.ts,
// здесь только переэкспорт — адреса потребителей (ChatPanel, lib/git.ts) не меняются
export { setGitSessionContext, getGitSessionContext } from '../features/git/api';

// Перезапись cc_token после смены пароля: старый токен сервер отозвал, а хранилище
// выбирает не вызывающий — токен пишется туда, откуда его читает readStoredToken.
export function setStoredToken(token: string) {
  if (typeof window === 'undefined') return;
  writeStoredToken(token);
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

// Projects
export const api = {
  // Методы «Аккаунта» (auth/push/users/settings/слоты моделей) — в features/account/api.ts
  ...accountApi,

  // Расход токенов и лимиты подписок — в features/spend/api.ts
  ...spendApi,

  // Провайдеры, модели и исполнитель фоновых действий — в features/models/api.ts
  ...modelsApi,

  ...localModelApi,


  // Питание, пульт команд, бэкапы и файлы хоста — в features/hostControl/api.ts
  ...hostControlApi,

  // Выкатка трей-раннером и журнал выкатки из чата — в features/deploy/api.ts
  ...deployApi,

  ...providerAccountsApi,

  // Генератор картинок по местам — в features/imageGeneration/api.ts
  ...imageGenerationApi,

  // Модули, MF-remote подсистем и фича-флаги — в features/modules/api.ts
  ...modulesApi,

  // Личный реестр MCP-серверов — в features/mcp/api.ts
  ...mcpApi,

  // Устройства владельца и сопряжение — в features/desktop/api.ts
  ...devicesApi,

  // Плагины владельца (ADR-031 §2.3, §4) — в features/plugins/api.ts
  ...pluginsApi,

  // Раздел «Видео» — в features/video/api.ts
  ...videoApi,

  // Проекты и ссылки внешнего доступа — в features/projects/api.ts
  ...projectsApi,

  // Специальности персон и настройки к ним — в features/specialties/api.ts
  ...specialtiesApi,


  // Мелкие домены «Дома» (сводка, бриф, поиск, онбординг, «стена», быстрые фразы,
  // продуктовая история) — в features/home/api.ts
  ...homeApi,

  // Группы проектов и сферы — в features/spheres/api.ts
  ...spheresApi,

  // Задачи и доска агентов (диспетчерская) — в features/tasks/api.ts
  ...tasksApi,

  // Заметки (Obsidian-совместимая база знаний) — в features/notes/api.ts
  ...notesApi,

  // Персоны (олицетворённые ИИ-собеседники) — в features/personas/api.ts
  ...personasApi,

  sessions: {
    list: (projectId: string) => request<Session[]>(`/projects/${projectId}/sessions`),
    // Начальное состояние бейджа рук локального проекта; дальше — событие hands_status
    handsStatus: (sessionId: string) =>
      request<LocalHandsChatStatus>(`/sessions/${encodeURIComponent(sessionId)}/hands-status`),
    // Подобрать значки-иконки чатам проекта без них (действие AI-палитры «Проставить значки чатов»)
    iconBatch: (projectId: string) =>
      request<{ processed: number; skipped: number }>(`/projects/${encodeURIComponent(projectId)}/sessions/icon-batch`, { method: 'POST' }),
    create: (projectId: string, mode = 'acceptEdits', resumeSessionId?: string, name?: string, model?: string, agentName?: string, effort?: string) =>
      request<Session>(`/projects/${projectId}/sessions`, {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, agentName, effort }),
      }),
    update: (projectId: string, sessionId: string, data: { name?: string | null; model?: string | null; effort?: string | null; expiresAfterMinutes?: number | null; tags?: string[]; excludeFromDossiers?: boolean | null; notificationsMuted?: boolean; voiceMode?: boolean; voiceStyle?: string }) =>
      request<Session>(`/projects/${projectId}/sessions/${sessionId}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    delete: (projectId: string, sessionId: string) =>
      request<void>(`/projects/${projectId}/sessions/${sessionId}`, { method: 'DELETE' })
        .then(r => { forgetChatContextSession(sessionId); return r; }),
    // Состав контекста чата (фича chat-context) с признаками missing. live: true —
    // обязательный: офлайн-кэш после переключения чата подставил бы чужой/старый состав
    getContext: (projectId: string, sessionId: string) =>
      request<SessionContextEntry[]>(`/projects/${projectId}/sessions/${sessionId}/context`, { live: true }),
    // Идемпотентная замена состава целиком. Ответ PUT — записи БЕЗ missing (бэкенд его
    // считает только в GET), поэтому после записи дёргаем GET — он же обновит кеш
    putContext: async (projectId: string, sessionId: string, entries: Pick<SessionContextEntry, 'type' | 'id' | 'title'>[]) =>
      { await request<void>(`/projects/${projectId}/sessions/${sessionId}/context`, {
        method: 'PUT',
        body: JSON.stringify(entries),
      }); return api.sessions.getContext(projectId, sessionId); },
    getHistory: (projectId: string, sessionId: string) =>
      request<unknown[]>(`/projects/${projectId}/sessions/${sessionId}/history`, { chatScope: sessionId }),
    // Докачка хвоста истории по курсору сервера (см. lib/historyTail). after=-1 — первая
    // загрузка (полная история + якорь), её можно кэшировать офлайн; настоящий курсор — live:
    // хвост в офлайн-кэше был бы мусором (подстановка куска вместо ленты).
    // Полная история — по-чатовой класс кэша: она тяжёлая (до 2,5 МБ на чат), и без своего
    // потолка по-чатовые записи съедали бы общий бюджет кэша и росли вместе с числом чатов.
    // onBytes — необязательный колбэк веса ответа (мера та же, что у офлайн-кэша)
    getHistoryTail: (projectId: string, sessionId: string, q: HistoryCursorQuery, onBytes?: (n: number) => void) =>
      request<HistoryReply>(
        `/projects/${projectId}/sessions/${sessionId}/history?${historyCursorParams(q)}`,
        { live: q.after >= 0, chatScope: q.after < 0 ? sessionId : undefined, onBytes }),
    // «Итог сессии»: конспект сессии заметкой (флаг notes-session-summary).
    // Маршрут по id сессии — работает и для проектных сессий, и для чатов
    summary: (sessionId: string) =>
      request<NoteDetail>(`/sessions/${sessionId}/summary`, { method: 'POST' }),
    // «Задачи из чата» (флаг chat-extract-tasks): извлечь кандидатов (не создаёт)
    extractTasks: (sessionId: string) =>
      request<ExtractTasksResponse>(`/sessions/${sessionId}/extract-tasks`, { method: 'POST' }),
    // Снять постоянное разрешение инструмента в чате («Всегда разрешать …»).
    // Маршрут по id сессии — работает и для проектных чатов, и для чатов вне проекта.
    // Отдаёт обновлённую сессию (как смена режима) — доборный GET не нужен
    revokeAutoAllow: (sessionId: string, tool: string) =>
      request<Session>(
        `/sessions/${encodeURIComponent(sessionId)}/auto-allow?tool=${encodeURIComponent(tool)}`,
        { method: 'DELETE' }),
    // Снять сообщение из очереди занятого чата (крестик на карточке-призраке).
    // Очередь живёт в памяти сервера — актуальный состав приходит событием pending_messages
    cancelPending: (sessionId: string, messageId: string) =>
      request<void>(`/sessions/${encodeURIComponent(sessionId)}/pending/${encodeURIComponent(messageId)}`,
        { method: 'DELETE' }),
    // Прервать идущий ход и доставить ждущее сообщение сейчас. Обычная отправка ход не
    // прерывает (он доживает сам) — это явный перебой по кнопке на карточке очереди
    preemptForPending: (sessionId: string) =>
      request<void>(`/sessions/${encodeURIComponent(sessionId)}/pending/preempt`, { method: 'POST' }),
    // Снимок промпта хода — что ушло модели (кнопка под постом). 404 — снимок вытеснен
    // ретеншном последних 50 ходов чата
    promptSnapshot: (sessionId: string, snapshotId: string) =>
      request<PromptSnapshot>(
        `/sessions/${encodeURIComponent(sessionId)}/prompt/${encodeURIComponent(snapshotId)}`),
    // Текст одного файла слоя CLI (CLAUDE.md) — грузится, только когда строку раскрыли:
    // в основной выдаче у него лишь размер, иначе открытие шторки тянуло бы десятки КБ
    promptSnapshotFile: (sessionId: string, snapshotId: string, key: string) =>
      request<PromptSection>(
        `/sessions/${encodeURIComponent(sessionId)}/prompt/${encodeURIComponent(snapshotId)}/file?key=${encodeURIComponent(key)}`),
    // Разбор промпта моделью. includeText=true — человек разрешил приложить фрагменты
    // текста секций (по умолчанию уходят только метаданные)
    // timeoutMs — это ход модели, дефолтных 30 с ему мало: обрыв по таймауту
    // трактуется как сетевая ошибка и выглядит как «Действие недоступно офлайн»
    analyzePrompt: (sessionId: string, snapshotId: string, includeText: boolean) =>
      request<{ analysis: string }>(
        `/sessions/${encodeURIComponent(sessionId)}/prompt/${encodeURIComponent(snapshotId)}/analyze`,
        { method: 'POST', body: JSON.stringify({ includeText }), timeoutMs: 180_000 }),
  },


  // Чаты вне проекта (project-less)
  chats: {
    list: () => request<Session[]>('/chats'),
    // id чатов (в т.ч. проектных) с живой фоновой работой: отдельно агенты, отдельно фоновые
    // команды Bash. Снимок нужен потому, что событие bg_agents_presence приходит только на
    // смене состояния — открывший список позже старта иначе о нём не узнает
    agentsPresence: () => request<{ agents: string[]; commands: string[] }>('/chats/agents-presence'),
    get: (id: string) => request<Session>(`/chats/${id}`, { chatScope: id }),
    create: (mode?: string, resumeSessionId?: string, name?: string, model?: string, effort?: string) =>
      request<Session>('/chats', {
        method: 'POST',
        body: JSON.stringify({ mode, resumeSessionId, name, model, effort }),
      }),
    update: (id: string, data: { name?: string | null; model?: string | null; effort?: string | null; pinned?: boolean; expiresAfterMinutes?: number | null; notificationsMuted?: boolean; voiceMode?: boolean; voiceStyle?: string }) =>
      request<Session>(`/chats/${id}`, {
        method: 'PUT',
        body: JSON.stringify(data),
      }),
    // Ручная группировка чатов (drag-and-drop в списке): вложить в родительский чат
    // либо вынести в корень (parentId: null). Один эндпоинт на оба списка —
    // работает и для проектных сессий, и для чатов вне проектов
    setParent: (id: string, parentId: string | null) =>
      request<Session>(`/chats/${encodeURIComponent(id)}/parent`, {
        method: 'PUT',
        body: JSON.stringify({ parentId }),
      }),
    // Отметить чат прочитанным (синк непрочитанности между устройствами).
    // Работает и для проектных сессий, и для чатов вне проектов; не двигает updatedAt
    markRead: (id: string) =>
      request<void>(`/chats/${encodeURIComponent(id)}/read`, { method: 'PUT' }),
    // Обновить название чата по текущей переписке (AI-хаб, действие chat.retitle)
    retitle: (id: string) =>
      request<Session>(`/chats/${encodeURIComponent(id)}/retitle`, { method: 'POST' }),
    // Групповой чат персон (флаг persona-group-chats): 2-4 участника, первый — ведущая.
    // Зона — по ведущей: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    createGroup: (personaIds: string[], mode?: string, name?: string) =>
      request<Session>('/chats/group', {
        method: 'POST',
        body: JSON.stringify({ personaIds, mode, name }),
      }),
    // Обновить состав участников группового чата (спикер сохраняется, если остался)
    setParticipants: (id: string, personaIds: string[]) =>
      request<Session>(`/chats/${id}/participants`, {
        method: 'PUT',
        body: JSON.stringify({ personaIds }),
      }),
    // Цикл «до готово» (флаг work-loop): агент работает итерациями до отчёта о завершении.
    // Работает и для проектных сессий, и для чатов вне проекта
    setWorkLoop: (id: string, enabled: boolean) =>
      request<Session>(`/chats/${id}/loop`, {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
    // Режим «Командная реализация»: вкл/выкл режима чата-штаба.
    // При включении можно сразу задать авто-волны и состав (пустой список = вся команда)
    setTeamImplement: (id: string, enabled: boolean, opts?: { autoWaves?: boolean; coordinatorPersonaId?: string; plannerPersonaId?: string; executorPersonaIds?: string[] }) =>
      request<Session>(`/chats/${id}/team-implement`, {
        method: 'PUT',
        body: JSON.stringify({ enabled, ...opts }),
      }),
    // Переключение авто-волн на ходу (из бейджа режима): трогает только флаг, не режим
    setTeamImplementAuto: (id: string, autoWaves: boolean) =>
      request<Session>(`/chats/${id}/team-implement/auto`, {
        method: 'PUT',
        body: JSON.stringify({ autoWaves }),
      }),
    // «Остановить» (кнопка человека): новые волны не стартуют, текущие исполнители
    // дорабатывают начатое. В ленте появляется карточка остановки с «Продолжить»
    stopTeamImplement: (id: string) =>
      request<Session>(`/chats/${id}/team-implement/stop`, { method: 'PUT' }),
    // Полный снимок пульса волны (Э2 КР-наблюдаемости): пульс + список задач +
    // пороги liveness. Зовётся при КАЖДОМ открытии поповера бейджа — кэшировать
    // нельзя. live: true + cache: 'no-store' по той же причине, что и у статуса
    // выкатки: офлайн-слой кладёт GET в IndexedDB и при обрыве подставил бы
    // прошлый ответ — поповер показывал бы «в работе, 5 мин» при мёртвой волне.
    // Для индикатора живости это хуже, чем не показать ничего
    getTeamWaveSnapshot: (id: string) =>
      request<import('../types').TeamWaveSnapshot>(
        `/chats/${id}/team-wave-snapshot`,
        { cache: 'no-store', live: true },
      ),
    // Перезапуск одной под-задачи волны (КР-наблюдаемость, этап 3): та же перевыдача,
    // что у провала хода — потолок попыток общий. 409 несёт человеческий текст
    // (живая задача, завершена, повторный клик) — показываем его, а не пустую кнопку
    restartTeamWaveTask: (id: string, taskId: string) =>
      request<import('../types').TeamTaskRestartResponse>(
        `/chats/${id}/team-wave/tasks/${taskId}/restart`,
        { method: 'POST', live: true },
      ),
    // Перезапуск волны: без confirm — предупреждение о живых исполнениях (liveTasks),
    // повтор с confirm=true останавливает их и перевыдаёт незакрытое. Done не трогается
    restartTeamWave: (id: string, confirm: boolean) =>
      request<import('../types').TeamWaveRestartResponse>(
        `/chats/${id}/team-wave/restart`,
        { method: 'POST', body: JSON.stringify({ confirm }), live: true },
      ),
    // Перезапуск зависшего хода штаба: kill → валидация транскрипта → новый ход с
    // --resume, отложенные сообщения уходят. startFresh=true — «начать ход заново» при
    // повреждённом транскрипте (409 code=transcript_damaged). Долгий: kill и ожидание
    // смерти процесса могут занять десятки секунд
    restartTeamWaveTurn: (id: string, startFresh = false) =>
      request<import('../types').TeamTurnRestartResponse>(
        `/chats/${id}/team-wave/restart-turn`,
        { method: 'POST', body: JSON.stringify({ startFresh }), live: true, timeoutMs: 90_000 },
      ),
    // Отдельное git worktree чата: вкл — сессия переезжает в изолированное дерево на новой
    // ветке (начатый чат — с переносом контекста), выкл — возврат в корень проекта.
    // force подтверждает потерю несохранённых правок дерева. Только проектные чаты.
    // Создание дерева = checkout репы, на большой может быть небыстрым
    setWorktree: (id: string, enabled: boolean, branch?: string, force = false) =>
      request<Session>(`/chats/${id}/worktree`, {
        method: 'PUT',
        body: JSON.stringify({ enabled, branch: branch ?? null, force }),
        timeoutMs: 120_000,
      }),
    // Миграция чата на другого провайдера («Продолжить на …» при исчерпании лимита):
    // транскрипт переезжает в профиль провайдера, контекст сохраняется. Работает и для
    // проектных сессий. subscriptionKey — явный выбор аккаунта того же пула подписок
    // (кнопка kind='subscription' карточки лимита); для сторонних провайдеров не передаётся
    migrateProvider: (id: string, model: string, subscriptionKey?: string) =>
      request<Session>(`/chats/${id}/migrate-provider`, {
        method: 'POST',
        body: JSON.stringify(subscriptionKey ? { model, subscriptionKey } : { model }),
      }),
    // Ветвление чата (фича chat-branch): новый чат с копией истории до userMessageIndex
    // (включительно или нет — задаёт include). anchorText — сверка «тот ли это шаг»: на
    // расхождении текста сервер отдаёт 409, без молчаливого отреза не там. Имя опционально,
    // дефолт — «{имя оригинала} — ветка». Ответ — chatId+claudeSessionId новой ветки и draft
    // (null при include='turn', текст исходного промпта при include='beforePrompt' для
    // композера). Серверные 400 «ветвить нечего» / 409 «идёт ход» / 409 «границу не
    // сопоставить» несут человеческий текст — он доезжает до вызывающего кода через Error.message
    // (request() пробрасывает server body как Error & { body })
    branch: (
      id: string,
      body: {
        userMessageIndex: number;
        anchorText: string;
        include: 'turn' | 'beforePrompt';
        name?: string;
      },
    ) =>
      request<{ chatId: string; projectId: string | null; claudeSessionId: string; draft: string | null }>(
        `/chats/${id}/branch`,
        { method: 'POST', body: JSON.stringify(body) },
      ),
    // «Продолжить в новом чате»: агент пишет записку-передачу (один ход, десятки секунд),
    // платформа открывает новый чат той же персоны. 409 — чат занят, 400 — нет ходов агента;
    // оба несут человеческий текст в Error.message. timeoutMs чуть больше серверного лимита
    // хода (15 мин) — см. CONTINUE_TIMEOUT_MS: обрыв по таймауту не уводит в офлайн
    continue: (id: string, note?: string) =>
      request<{ newSessionId: string; handoffNoteId: string | null }>(
        `/chats/${id}/continue`,
        { method: 'POST', body: JSON.stringify(note ? { note } : {}), timeoutMs: CONTINUE_TIMEOUT_MS },
      ),
    // «Перенести в новый чат» (фича prompt-cache, шаг F-4): путь выбирает сервер — тёплый
    // (записку пишет сам чат, пока его кэш тёплый) или холодный (её собирает дешёвый раннер
    // по транскрипту). Ответ — id нового чата, его проект и черновик композера. 409 — чат
    // занят/повторный клик/не тот тип чата, 502 — модель не дала записку; текст ошибки
    // человеческий и показывается как есть. Тёплый путь — видимый ход чата до 5 мин,
    // таймаут фронта чуть больше — см. HANDOFF_TIMEOUT_MS.
    handoff: (id: string) =>
      request<{ chatId: string; projectId: string | null; draft: string | null; warm: boolean }>(
        `/chats/${id}/handoff`,
        { method: 'POST', timeoutMs: HANDOFF_TIMEOUT_MS },
      ),
    // Режим прав: сохраняем сразу при выборе в Composer, иначе он доехал бы до сессии
    // только вместе со следующим сообщением и терялся при уходе со страницы
    setMode: (id: string, mode: string) =>
      request<Session>(`/chats/${id}/mode`, {
        method: 'PUT',
        body: JSON.stringify({ mode }),
      }),
    // «Продолжить в стандартном окне 200K» под карточкой отказа Window1MUnavailable.
    // Единственный путь, которым суффикс [1m] снимается с чата: тихая деградация длинного
    // разговора в 200K — мина, решение принимает человек, сервер только исполняет.
    // Возвращает обновлённый чат: фронт по нему перерисовывает выбранную модель,
    // отдельного сигнала не приходит
    dropWindow1M: (id: string) =>
      request<Session>(`/chats/${id}/window-1m/drop`, { method: 'POST' }),
    delete: (id: string) => request<void>(`/chats/${id}`, { method: 'DELETE' })
      .then(r => { forgetChatContextSession(id); return r; }),
    getHistory: (id: string) => request<unknown[]>(`/chats/${id}/history`, { chatScope: id }),
    // То же, что api.sessions.getHistoryTail, для чата вне проекта
    getHistoryTail: (id: string, q: HistoryCursorQuery, onBytes?: (n: number) => void) =>
      request<HistoryReply>(`/chats/${id}/history?${historyCursorParams(q)}`,
        { live: q.after >= 0, chatScope: q.after < 0 ? id : undefined, onBytes }),
    // Вложение локального проекта ложится на машину проекта через агента: сервер на свой
    // маршрут для него отвечает отказом G1
    uploadFile: async (id: string, file: File, projectId?: string): Promise<{ path: string }> => {
      if (projectId && projectRouteOf(projectId) === 'agent') return uploadAgentAttachment(projectId, file);
      const token = readStoredToken();
      const form = new FormData();
      form.append('file', file);
      const res = await fetch(`/api/chats/${id}/files/upload`,
        { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: form });
      if (res.status === 401) {
        if (token && typeof window !== 'undefined') window.dispatchEvent(new Event('cc-unauthorized'));
        throw new Error('Нет доступа');
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        throw new Error(err.error ?? res.statusText);
      }
      return res.json();
    },
  },

  // Сторожа чатов, workflow-агенты и голоса TTS — в features/chat/api.ts
  ...chatAuxApi,

  // Документация проекта (README.md + docs/**) — в features/docs/api.ts
  ...docsApi,

  // «История решений» — в features/dossiers/api.ts; «Телеметрия» — в features/telemetry/api.ts
  ...dossiersApi,
  ...telemetryApi,

  // Файлы проекта (включая агент устройства) и «Синхронизация» — в features/files/api.ts
  ...filesApi,

  // Git проекта с контекстом worktree-чата — в features/git/api.ts
  ...gitApi,

  // «Знания»: проекльный индекс Dify и менеджер баз знаний — в features/knowledge/api.ts
  ...knowledgeApi,

  // «Навыки» и реестр skills.sh — в features/skills/api.ts
  ...skillsApi,


  // Карта плана — в features/plan/api.ts
  ...plansApi,

  // «Ридер ссылок» с хелперами — в features/reader/api.ts
  ...readerApi,
};
