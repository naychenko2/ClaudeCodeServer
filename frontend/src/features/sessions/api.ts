// «Сессии проекта» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// тонкая обёртка над живым REST; типы — types/index.ts и lib/historyTail.
// historyCursorParams — общий курсор докачки истории: его же зовёт api.chats.*.
import { request } from '../../lib/offline';
import { forgetChatContextSession } from '../../lib/chatContext/forget';
import type { HistoryCursorQuery, HistoryReply } from '../../lib/historyTail';
import type {
  ExtractTasksResponse, LocalHandsChatStatus, NoteDetail, PromptSection, PromptSnapshot,
  Session, SessionContextEntry,
} from '../../types';

// Query-строка курсора докачки истории: after обязателен, fp — только для настоящего курсора
export const historyCursorParams = (q: HistoryCursorQuery) =>
  `after=${q.after}${q.fp ? `&fp=${encodeURIComponent(q.fp)}` : ''}`;

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом.
export const sessionsApi = {
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
      }); return sessionsApi.sessions.getContext(projectId, sessionId); },
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
};
