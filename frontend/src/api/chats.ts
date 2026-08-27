// API-методы фичи «Архив чатов» (план v4): отдельный модуль — все архивные
// запросы рядом, чтобы их держать и поддерживать как одну группу. Остальные
// эндпоинты чатов живут в lib/api.ts (chat.*), и трогать их отсюда не нужно:
// разные волны правили.

import { request } from '../lib/offline';
import { api } from '../lib/api';
import type { Session, NoteDetail } from '../types';

// Архив ПРЯЧЕТ чат, а не удаляет (история и claudeSessionId целы). Архив/возврат
// идут одним эндпоинтом (PUT /api/chats/{id}/archived с булевым archived) —
// отдельного мутатора «снять архив» в API нет, и заводить его здесь нельзя
// (план v4, шаг 2). 409 на живом ходе/фоновых агентах: человек должен либо
// дождаться конца хода, либо прервать его, и только потом убирать чат в архив.
export const archiveApi = {
  // Снять/установить признак архива. archived=true — «Убрать в архив»,
  // archived=false — «Вернуть из архива» (ручной возврат из плашки открытого
  // чата и из карточки архива). Сервер может бросить 409 с человеческим
  // текстом «в чате идёт ход» — фронт ловит его на ошибке и показывает тостом.
  setArchived: (id: string, archived: boolean) =>
    request<Session>(`/chats/${encodeURIComponent(id)}/archived`, {
      method: 'PUT',
      body: JSON.stringify({ archived }),
    }),

  // Сводка карточки архива: 2–3 предложения о чём был разговор (место chat-digest).
  // Первая сборка зовёт модель, последующие (пока UpdatedAt <= ArchiveSummaryAt)
  // отдаются из кэша без обращения к LLM. 409 на повторном клике в полёте —
  // сводка уже собирается. 502 на ошибке модели. 400 для десктопного чата —
  // описания экрана не покидают грань.
  buildDigest: (id: string) =>
    request<Session>(`/chats/${encodeURIComponent(id)}/digest`, { method: 'POST' }),
} as const;

// «Сохранить в заметки»: существующий POST /api/sessions/{id}/summary через
// SessionSummaryService — здесь только проброс для удобства вызова из карточки
// архива. Контракт не меняется: ответ — созданная/обновлённая заметка,
// session.SummaryNoteId проставляется сервером (см. SessionManager.SetSummaryNoteId).
// Не кладём в archiveApi — это не архивный эндпоинт, а общий «Итог сессии»,
// которым карточка архива просто пользуется.
export async function saveArchiveSessionAsNote(sessionId: string): Promise<NoteDetail> {
  return api.sessions.summary(sessionId);
}

// API-методы автоправила архивации (флаг chat-auto-archive). За флагом — сами
// запросы отдают 400 «Автоправило архива выключено», фронт гасит настройку
// не показывая блок.
export const archiveRuleApi = {
  // Первоначальное состояние экрана настройки автоправила: личный порог
  // (User.ArchiveAfterDays; он же дефолт для проектов без своего и правило
  // чатов вне проекта) и признак первого прохода (производный от
  // User.ArchiveRuleFirstRunAt — гейта фонового тика). Без гейта по флагу:
  // чтение ничего не меняет, и раздел «Архив» работает и без тумблера.
  getSettings: () =>
    request<{ archiveAfterDays: number | null; hasFirstRun: boolean }>('/chats/archive-settings'),

  // Счётчик превью под полем порога: «Под правило подпадёт N чатов». days —
  // текущее значение поля; projectId — проект (null = чаты вне проекта).
  preview: (days: number, projectId: string | null) => {
    const qs = new URLSearchParams({ days: String(days) });
    if (projectId) qs.set('projectId', projectId);
    return request<{ count: number }>(`/chats/archive-preview?${qs}`);
  },

  // Сохранить личный порог правила. days=null — сброс (правило выключено
  // для своей сферы). PUT — идемпотентная запись в User.ArchiveAfterDays.
  setDays: (days: number | null) =>
    request<{ archiveAfterDays: number | null }>(`/chats/archive-days`, {
      method: 'PUT',
      body: JSON.stringify({ days }),
    }),

  // Кнопка «Применить сейчас»: один проход правила по всем сферам владельца
  // (включая накопившиеся старые чаты). Устанавливает User.ArchiveRuleFirstRunAt —
  // гейт фонового тика.
  runNow: () =>
    request<{ archived: number; batchId: string | null }>(`/chats/archive-run`, { method: 'POST' }),

  // Откат одной пачки автоправила из уведомления/раздела «Архив». Работает
  // без флага — это возврат, как ручной «Вернуть из архива».
  restoreBatch: (batchId: string) =>
    request<{ restored: number }>(`/chats/archive-batch/${encodeURIComponent(batchId)}/restore`, {
      method: 'POST',
    }),
} as const;