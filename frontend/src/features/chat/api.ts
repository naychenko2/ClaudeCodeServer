// Вспомогательные домены чатов (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// сторожа чатов, workflow-агенты, голоса TTS. Тонкая обёртка над живым REST;
// типы — types/index.ts (переедут в эту папку в S5-P3).
import { readStoredToken, request } from '../../lib/offline';
import { forgetChatContextSession } from '../../lib/chatContext/forget';
import { projectRouteOf, uploadAgentAttachment } from '../../lib/deviceAgent';
// eslint-disable-next-line module/no-cross-feature-import -- курсор докачки истории один для проектных сессий и чатов: две вертикали, одна строка формата (features/sessions/api.ts)
import { historyCursorParams } from '../sessions/api';
import type { HistoryCursorQuery, HistoryReply } from '../../lib/historyTail';
import type { Session, TtsVoicesResponse, WorkflowAgentBlock, WorkflowAgentInfo } from '../../types';

// Отказ тумблера видимости чата (S2d-F2): 409/403 с полем body.code. Тексты равны
// серверным (ChatsController.SetVisibility / ChatAuthorGate — русские), чтобы на клиенте
// не было второй формулировки того же отказа; человеку показывается текст, а не код.
// Незнакомый код — серверный error как есть, без кода — общий текст.
export const CHAT_VISIBILITY_TEXTS = {
  not_shared_project: 'Приватным можно сделать только чат общего проекта',
  chat_has_messages: 'В чате уже есть сообщения — приватным его не сделать',
  reader: 'Читатель проекта не меняет видимость чата',
  not_chat_author: 'Только автор чата',
  // S2d-6b-4: бэк не смог перенести вложения чата при смене видимости — видимость не изменена
  attachments_move_failed: 'Не удалось перенести вложения чата — видимость не изменена',
} as const;

export class ChatVisibilityError extends Error {
  readonly status: 403 | 409;
  readonly code: string | null;
  constructor(status: number, body: { error?: string; code?: string } | undefined) {
    const code = body?.code && body.code in CHAT_VISIBILITY_TEXTS ? body.code as keyof typeof CHAT_VISIBILITY_TEXTS : null;
    super(code ? CHAT_VISIBILITY_TEXTS[code] : (body?.error ?? 'Не удалось изменить видимость чата'));
    this.name = 'ChatVisibilityError';
    this.status = status as 403 | 409;
    this.code = body?.code ?? null;
  }
}

// Ответ тумблера видимости (S2d-6b-4): поля Session + карта переноса вложений
// {старый путь: новый путь} — бэк переносит файлы чата при смене видимости; карта
// есть только когда перенос был. Вызывающий применяет её к ленте (remapSessionAttachedPaths).
export type ChatVisibilityResponse = Session & { attachmentMoves?: Record<string, string> };

// Отказ обновления полей чата (S2d-6c-F): 409 с полем body.code от PUT sessions/{sid}
// (SessionsController.cs:126: пока чат приватный, «Не сохранять решения» нельзя
// выключить). Серверный текст — только отказ; клиентский добавляет действие, как
// требует постановка S2d-6c-F (человек читает причину и то, что сделать дальше); тон
// тот же, что у CHAT_VISIBILITY_TEXTS: коротко, без кода и статуса.
export const CHAT_FIELDS_ERROR_TEXTS = {
  private_chat_dossiers: 'Пока чат приватный, решения из него не сохраняются — это нельзя выключить. Сначала сделайте чат общим.',
} as const;

// Текст тоста об отказе изменить настройки чата: знакомый код — понятный текст,
// любая другая ошибка — тот же нейтральный, что был (отказ не меняет локальное состояние).
export function chatFieldsErrorText(e: unknown): string {
  const code = (e as { body?: { code?: string } })?.body?.code;
  return code && code in CHAT_FIELDS_ERROR_TEXTS
    ? CHAT_FIELDS_ERROR_TEXTS[code as keyof typeof CHAT_FIELDS_ERROR_TEXTS]
    : 'Не удалось изменить настройку чата';
}

// Методы этих доменов; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.watchdogs.* и т. д. остаются прежними для всех потребителей).
export const chatAuxApi = {
  // Сторожа чатов: чаты и проекты владельца с АКТИВНЫМИ сторожами. Снимок нужен потому,
  // что событие watchdogs_changed приходит только на смене состава — открывший список
  // позже постановки иначе о них не узнает
  watchdogs: {
    snapshot: () => request<{ sessions: string[]; projects: string[] }>('/watchdogs'),
  },

  workflow: {
    getAgents: (transcriptDir: string) =>
      request<{ agents: WorkflowAgentInfo[] }>(
        `/workflow-agents?transcriptDir=${encodeURIComponent(transcriptDir)}`
      ),
    // Полный поток одного агента (текст/thinking/инструменты) — лениво при раскрытии карточки
    getTimeline: (transcriptDir: string, agentId: string) =>
      request<{ blocks: WorkflowAgentBlock[] }>(
        `/workflow-agents/timeline?transcriptDir=${encodeURIComponent(transcriptDir)}&agentId=${encodeURIComponent(agentId)}`
      ),
  },

  tts: {
    // Каталог голосов для формы выбора: канонические имена, подписи, пол и амплуа.
    // live — мимо офлайн-кеша: сразу после того, как ключ SpeechKit пропишут в конфиг,
    // из IndexedDB прилетал бы устаревший configured: false и форма врала бы про синтез
    voices: () => request<TtsVoicesResponse>('/tts/voices', { live: true }),
  },
};

// «Чаты вне проекта» (вынесено из lib/api.ts, тем же порядком): тот же тонкий слой.

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом.
export const chatsApi = {
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
    // Видимость чата (S2d-F2): true — приватный (виден только автору; только чат общего
    // проекта, только пока в нём нет сообщений), false — снова общий (в любой момент;
    // после этого приватность уже не вернуть — бэк 409 chat_has_messages). Отказы 409/403
    // с body.code оборачиваются в ChatVisibilityError — с человеческим текстом, не кодом.
    // Успешный ответ может нести attachmentMoves (S2d-6b-4) — карта переноса вложений чата.
    setChatVisibility: async (id: string, isPrivate: boolean): Promise<ChatVisibilityResponse> => {
      try {
        return await request<ChatVisibilityResponse>(`/chats/${encodeURIComponent(id)}/visibility`, {
          method: 'PUT', body: JSON.stringify({ private: isPrivate }),
        });
      } catch (e) {
        const err = e as { status?: number; body?: { error?: string; code?: string } };
        if (err.status === 409 || err.status === 403) throw new ChatVisibilityError(err.status, err.body);
        throw e;
      }
    },
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
      request<import('../../types').TeamWaveSnapshot>(
        `/chats/${id}/team-wave-snapshot`,
        { cache: 'no-store', live: true },
      ),
    // Перезапуск одной под-задачи волны (КР-наблюдаемость, этап 3): та же перевыдача,
    // что у провала хода — потолок попыток общий. 409 несёт человеческий текст
    // (живая задача, завершена, повторный клик) — показываем его, а не пустую кнопку
    restartTeamWaveTask: (id: string, taskId: string) =>
      request<import('../../types').TeamTaskRestartResponse>(
        `/chats/${id}/team-wave/tasks/${taskId}/restart`,
        { method: 'POST', live: true },
      ),
    // Перезапуск волны: без confirm — предупреждение о живых исполнениях (liveTasks),
    // повтор с confirm=true останавливает их и перевыдаёт незакрытое. Done не трогается
    restartTeamWave: (id: string, confirm: boolean) =>
      request<import('../../types').TeamWaveRestartResponse>(
        `/chats/${id}/team-wave/restart`,
        { method: 'POST', body: JSON.stringify({ confirm }), live: true },
      ),
    // Перезапуск зависшего хода штаба: kill → валидация транскрипта → новый ход с
    // --resume, отложенные сообщения уходят. startFresh=true — «начать ход заново» при
    // повреждённом транскрипте (409 code=transcript_damaged). Долгий: kill и ожидание
    // смерти процесса могут занять десятки секунд
    restartTeamWaveTurn: (id: string, startFresh = false) =>
      request<import('../../types').TeamTurnRestartResponse>(
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
    // URL вложения чата для браузерного <img> (миниатюра, ADR-041, строка №12): токен через
    // ?access_token=, потому что тег не шлёт заголовки (тот же приём, что fileUrl в files/api).
    // path — относительный путь ровно в той форме, что вернула загрузка (.cc-attachments/…).
    // Единственная точка, где этот URL формируется; бэк отдаёт байты только для учтённых в
    // Session.Attachments файлов (404 file_not_found иначе — без различения причин).
    attachmentRawUrl: (sessionId: string, path: string): string => {
      const token = readStoredToken();
      const params = new URLSearchParams({ path });
      if (token) params.set('access_token', token);
      return `/api/chats/${encodeURIComponent(sessionId)}/attachments/raw?${params}`;
    },
  },
};
