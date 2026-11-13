// Методы REST «Задач» и доски агентов (диспетчерская). lib/api.ts подмешивает их
// в объект api spread'ом (адреса api.tasks.* и api.board.* остаются прежними для
// потребителей); Task/Board* и DTO правки — в ./types (шаг P13), FeatureSummaryResult —
// там же (type-only импорт, цикла рантайма нет).
import { request } from '../../lib/offline';
import type { BoardItem, CreateTaskDto, FeatureSummaryResult, Task, UpdateTaskDto } from './types';
import type { FeatureRun, MergeTailDecision } from '../../types/featureRun';
import type { FeaturesIndex, FeaturePage } from '../../types/featureIndex';
import type { StageScreen, StageCommitDiff } from '../../types/featureStage';

export const tasksApi = {
  // Доска агентов (диспетчерская)
  board: {
    // live: true — ответ крупный (замер часа работы: ~0,86 МБ), а доска читается
    // на каждый чих статуса. В офлайн-кэш IndexedDB такая частота писала 258 МБ за
    // час, и подставленный прошлый ответ выдавал бы доску «кто сейчас работает» за
    // текущую — ровно тот класс вранья, из-за которого live: живёт у статуса
    // выкатки и питания.
    agents: () => request<{ items: BoardItem[] }>('/board/agents', { live: true }),
    interrupt: (sessionId: string) =>
      request<void>(`/board/agents/${sessionId}/interrupt`, { method: 'POST' }),
    allowPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/allow`, { method: 'POST' }),
    denyPermission: (sessionId: string, requestId: string) =>
      request<void>(`/board/agents/${sessionId}/permission/${requestId}/deny`, { method: 'POST' }),
  },

  tasks: {
    // Экран этапа (D1a): GET /api/tasks/{id}/stage; signal — отмена устаревшего ответа при смене задачи.
    // live: данные этапа меняются на ходу, пассивный GET-кэш прошлым ответом их бы подменил
    stage: (taskId: string, signal?: AbortSignal) =>
      request<StageScreen>(`/tasks/${encodeURIComponent(taskId)}/stage`, { live: true, signal }),
    // Дифф одного коммита этапа (ленивая загрузка по клику)
    stageCommitDiff: (taskId: string, sha: string, signal?: AbortSignal) =>
      request<StageCommitDiff>(`/tasks/${encodeURIComponent(taskId)}/stage/commits/${encodeURIComponent(sha)}/diff`, { live: true, signal }),
    // Все задачи пользователя (для календаря)
    listAll: (from?: string, to?: string) => {
      const qs = new URLSearchParams();
      if (from) qs.set('from', from);
      if (to) qs.set('to', to);
      const q = qs.toString();
      // live: список живёт одним экземпляром в сторе (tasks:all), пассивный GET-кэш его не дублирует
      return request<Task[]>(`/tasks${q ? `?${q}` : ''}`, { live: true });
    },
    listByProject: (projectId: string) => request<Task[]>(`/projects/${projectId}/tasks`, { live: true }),
    // Поиск по названию/описанию/меткам на сервере (по ПОЛНОМУ описанию, которого нет
    // в облегчённом списке); элементы ответа — облегчённые, нужны id совпавших
    // live: ключ кэша зависит от текста запроса — без live каждый ввод оставлял бы новую запись в IndexedDB
    search: (q: string) => request<Task[]>(`/tasks?q=${encodeURIComponent(q)}`, { live: true }),
    // Задачи, порученные персоне-исполнителю (assignee=claude + personaId)
    listByPersona: (personaId: string) =>
      request<Task[]>(`/tasks?personaId=${encodeURIComponent(personaId)}`, { live: true }),
    // Агрегаты фич чата (режим «Исполнение фичи»): готовые `FeatureRun` из бэка.
    // sessionId — id сессии-чата; без него — все фичи владельца.
    getFeatures: (sessionId?: string) =>
      request<FeatureRun[]>(`/tasks/features${sessionId ? `?sessionId=${encodeURIComponent(sessionId)}` : ''}`, { live: true }),
    // Индекс фич (фасет «Фичи» и плашка в чате): строки фич + связи «чат × фича».
    // projectId — область ответа; без него — все проекты владельца и чаты вне проектов.
    // 503 при выключенной подсистеме Tasks (как getFeatures).
    featuresIndex: (projectId?: string) =>
      request<FeaturesIndex>(`/tasks/features/index${projectId ? `?projectId=${encodeURIComponent(projectId)}` : ''}`),
    // Страница фичи (волна 2 «Обзор фич»): агрегат `FeatureRun` одной фичи + лента событий.
    // fid — id эпика; eventsLimit — «all»/«0» → все, N (целое > 0) → N, иначе 100 (бэк).
    // 404 — фичи нет/чужая/удалена (один 404); 503 — подсистема Tasks выключена.
    // signal — отмена в-полёте запроса (быстрая смена fid в useFeaturePage): не плодим
    // параллельных GET, устаревший отменяется (AbortError игнорируется в featurePage).
    getFeature: (fid: string, eventsLimit?: string, signal?: AbortSignal) =>
      request<FeaturePage>(
        `/tasks/features/${encodeURIComponent(fid)}${eventsLimit ? `?eventsLimit=${encodeURIComponent(eventsLimit)}` : ''}`,
        signal ? { signal, live: true } : { live: true },
      ),
    // Резюме фичи моделью (W5-F2, «Точка 6 · резюме»): POST summary[?force=true].
    // force=false — берётся кэш бэка, если есть; force=true — новый прогон. 200 —
    // FeatureSummaryResult; 502 — модель не ответила (текст причины в error); 503 —
    // подсистема выключена; 404 — фичи нет. signal — «Остановить» в шапке резюме.
    // Клиентский таймаут 240 с — по конвенции модельные вызовы задают свой лимит (см. analyze-
    // запросы выше). Серверный бюджет цепочки выше: локаль 45 с + облако 180 с (LocalActionCatalog),
    // а при таймаоте облачного шага он повторяется один раз (CheapTextRunner.cs:381-386) — до 405 с;
    // на маршруте Model — ещё больше. Клиентский таймаут обрывает только ожидание ответа: сервер
    // продолжает считать и кладёт результат в свой кэш (FeatureSummaryService.cs:76), когда
    // доработает (до ~405 с). Повтор запроса до этой записи встаёт в single-flight и ждёт тот же
    // расчёт (FeatureSummaryService.cs:51-59), а не даёт мгновенный кэш-хит; хит — только после
    // попадания в кэш. Это не задуманное поведение, а известная проблема: на HTTP/1.1 отмена
    // клиента не доходит до модельного вызова, работа продолжается (открытый дефект 1f065f3c).
    featureSummary: (fid: string, force?: boolean, signal?: AbortSignal) =>
      request<FeatureSummaryResult>(
        `/tasks/features/${encodeURIComponent(fid)}/summary${force ? '?force=true' : ''}`,
        { method: 'POST', ...(signal ? { signal } : {}), timeoutMs: 240_000 },
      ),
    // Мерж фичи (волна 2 «Обзор фич», POST /api/tasks/features/{fid}/merge):
    // создаёт задачу role=merge с персоной DevopsExecutor и запускает её. Fail-closed:
    // 503 (подсистема выключена), 404 (нет/чужой эпик), 409 (финал не принят / 0 или >1
    // исполнителей DevOps) — причина в err.body.error (и err.message). Идемпотентна:
    // открытая merge-задача → вернуть её (повторный вызов — та же задача, вторую не заводим).
    // FS-XT1 (§8.3): при непустом закрытом хвосте решение о нём обязательно — тело
    // { tail: 'withdraw' | 'keep' }; без него 409 { code: 'tail_decision_required',
    // tailSteps } (TasksController.cs:1645). «withdraw», часть шагов к моменту замка
    // перестала быть хвостом → 409 { code: 'tail_withdraw_not_applied', tailSteps }
    // (:1670-1678): мерж не начат, решение — по свежему состоянию. При пустом хвосте
    // тело не нужно вовсе: вызов без tail работает как раньше (бэк игнорирует тело).
    mergeFeature: (fid: string, tail?: MergeTailDecision) =>
      request<Task>(`/tasks/features/${encodeURIComponent(fid)}/merge`, {
        method: 'POST',
        ...(tail ? { body: JSON.stringify({ tail }) } : {}),
      }),
    // Пауза/черновик фичи (W4-HOLD-F2, ручки W4-HOLD-B2): POST hold {reason} —
    // удержание (reason: 'manual' — пауза человека, 'draft' — черновик), DELETE — снять.
    // Fail-closed: 403 (сервисный токен — удержание ставит только человек), 404, 409
    // (merged/closed/merging; draft после запуска шага; manual поверх draft; снятие без
    // удержания) — причина в body.error. 200 — ходы, прерванные этой паузой:
    // { interrupted: [taskId] } (контракт §1, :364).
    setFeatureHold: (fid: string, reason: string) =>
      request<{ interrupted: string[] }>(`/tasks/features/${encodeURIComponent(fid)}/hold`, { method: 'POST', body: JSON.stringify({ reason }) }),
    // Снятие паузы (W4-HOLD-F2, хвост R-HOLD-H): 200 — { resumed: [taskId], expired: [taskId] }:
    // resumed — задачи, которым пауза мешала запуститься и теперь разрешены (не очередь
    // автостарта), expired — те, чьё ожидание истекло (фронтом не показывается).
    clearFeatureHold: (fid: string) =>
      request<{ resumed: string[]; expired: string[] }>(`/tasks/features/${encodeURIComponent(fid)}/hold`, { method: 'DELETE' }),
    // projectId === null → личная задача (вне проекта)
    create: (projectId: string | null, dto: CreateTaskDto) =>
      request<Task>(projectId ? `/projects/${projectId}/tasks` : '/tasks', { method: 'POST', body: JSON.stringify(dto) }),
    // live: true — полная карточка (description + resultMarkdown, ~90 КБ) в пассивный GET-кэш не
    // пишется: он без вытеснения. Открытые полные задачи держит стор (LRU FULL_TASKS_CAP, снимок
    // tasks:all); офлайн ensureFullTask получает OfflineError → состояние «error» с «Повторить».
    get: (taskId: string) => request<Task>(`/tasks/${taskId}`, { live: true }),
    update: (taskId: string, dto: UpdateTaskDto) =>
      request<Task>(`/tasks/${taskId}`, { method: 'PUT', body: JSON.stringify(dto) }),
    delete: (taskId: string) => request<void>(`/tasks/${taskId}`, { method: 'DELETE' }),
    // Запустить выполнение задачи Claude-ом (отдельная сессия)
    execute: (taskId: string) => request<Task>(`/tasks/${taskId}/execute`, { method: 'POST' }),
    // Генерация Claude: описание по названию (+контекст проекта), подзадачи по описанию
    aiDescription: (title: string, projectId?: string | null) =>
      request<{ description: string }>('/tasks/ai/description', {
        method: 'POST', body: JSON.stringify({ title, projectId: projectId ?? null }),
      }),
    aiSubtasks: (title: string, description: string, projectId?: string | null) =>
      request<{ subtasks: string[] }>('/tasks/ai/subtasks', {
        method: 'POST', body: JSON.stringify({ title, description, projectId: projectId ?? null }),
      }),
    // Локальная модель (если настроена, иначе Claude): приоритет+метки, нормализация заголовка, дедуп
    aiClassify: (title: string, description?: string | null, projectId?: string | null) =>
      request<{ priority: string | null; labels: string[] }>('/tasks/ai/classify', {
        method: 'POST', body: JSON.stringify({ title, description: description ?? null, projectId: projectId ?? null }),
      }),
    aiNormalizeTitle: (title: string) =>
      request<{ title: string; dueHint: string | null }>('/tasks/ai/normalize-title', {
        method: 'POST', body: JSON.stringify({ title }),
      }),
    aiFindDuplicate: (title: string, description?: string | null, projectId?: string | null) =>
      request<{ duplicateId: string | null; reason: string | null }>('/tasks/ai/find-duplicate', {
        method: 'POST', body: JSON.stringify({ title, description: description ?? null, projectId: projectId ?? null }),
      }),
  },
};
