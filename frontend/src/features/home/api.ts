// Мелкие домены «Дома» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// сводка дашборда, утренний бриф, единый поиск, онбординг, «стена», быстрые фразы
// композера, продуктовая история. Тонкая обёртка над живым REST; DTO-типы — types/index.ts
// (переедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type {
  ChangelogDay,
  ChangelogStatus,
  DaySummaryStub,
  HomeSummaryResponse,
  NoteDetail,
  Persona,
  QuickPhrase,
  SearchHit,
  Session,
} from '../../types';

// Методы этих доменов; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.home.* и т. д. остаются прежними для всех потребителей).
export const homeApi = {
  // Сводка дашборда «Домой»: активные + недавние сессии по всем проектам и чатам
  home: {
    summary: (recent = 10) => request<HomeSummaryResponse>(`/home/summary?recent=${recent}`),
  },

  // Утренний бриф (флаг daily-briefing): собрать план дня в дневник
  briefing: {
    today: (date?: string) =>
      request<NoteDetail>('/briefing/today', { method: 'POST', body: JSON.stringify({ date: date ?? null }) }),
  },

  // Единый поиск (флаг unified-search): заметки + задачи в одной выдаче
  search: (q: string, topK = 8) =>
    request<SearchHit[]>(`/search?q=${encodeURIComponent(q)}&topK=${topK}`),

  // Онбординги: старт/резюм чат-интервью знакомства.
  // Идемпотентны: живая сессия онбординга возвращается как есть, удалённая — заменяется новой
  onboarding: {
    startUser: () => request<Session>('/onboarding/user/start', { method: 'POST' }),
    startProject: (projectId: string) =>
      request<Session>(`/onboarding/project/${encodeURIComponent(projectId)}/start`, { method: 'POST' }),
    // Страховка «применить итоги разговора»: LLM-прогон + генерация аватара до ~90 с,
    // таймаут 150 с — запас на fal и холодный старт модели
    applyTranscript: () =>
      request<Persona>('/onboarding/user/apply-transcript', { method: 'POST', timeoutMs: 150_000 }),
  },

  // «Стена»: per-user набор чатов колонками. Ресурс бэка — /api/me/wall
  // (MyWallController, конвенция per-user настроек), короткое имя здесь — для читаемости вызовов.
  wall: {
    // Состав стены — полные Session в порядке набора (мёртвые уже отфильтрованы сервером)
    get: () => request<{ chats: Session[] }>('/me/wall'),
    // Полная замена состава; ответ — итог после серверной чистки (дедуп/чужие/потолок)
    put: (chatIds: string[]) =>
      request<{ chats: Session[] }>('/me/wall', { method: 'PUT', body: JSON.stringify({ chatIds }) }),
    // Кандидаты для пикера: все чаты владельца, свежие сверху
    candidates: () => request<Session[]>('/me/wall/candidates'),
  },

  // Быстрые фразы композера: готовые сообщения, уходящие одним нажатием.
  // Ресурс бэка — /api/me/quick-phrases (MyQuickPhrasesController)
  quickPhrases: {
    get: () => request<{ phrases: QuickPhrase[] }>('/me/quick-phrases'),
    // Полная замена набора; ответ — итог после серверной чистки (пустые/дубли/потолок)
    put: (phrases: QuickPhrase[]) =>
      request<{ phrases: QuickPhrase[] }>('/me/quick-phrases', { method: 'PUT', body: JSON.stringify({ phrases }) }),
  },

  // Продуктовая история (AI-сводка по всем проектам): дни, сводка дня, счетчик новых
  history: {
    days: (sinceDays = 0) =>
      request<DaySummaryStub[]>(`/history/days${sinceDays > 0 ? `?sinceDays=${sinceDays}` : ''}`),
    day: (date: string) =>
      request<ChangelogDay>(`/history/day/${date}`),
    newCount: (sinceIso: string) =>
      request<{ count: number }>(`/history/new-count?since=${encodeURIComponent(sinceIso)}`),
    status: () => request<ChangelogStatus>('/history/status'),
    // Сбросить кеш одного дня (перегенерация) / всей истории (очистка)
    invalidateDay: (date: string) =>
      request<void>(`/history/day/${date}`, { method: 'DELETE' }),
    clear: () =>
      request<void>('/history', { method: 'DELETE' }),
  },
};
