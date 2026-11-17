// Детский режим: тонкий клиент ручек /api/child/* и PUT /api/users/{id}/child-profile.
// Контракт — docs/adr/ADR-041-child-mode.md, раздел «Контракт /api/child/*» (бэк обязан
// совпасть с тем же разделом). В lib/api.ts НЕ подключается (у файла бюджет строк,
// apiFileBudget.test.ts): потребители импортируют childApi напрямую.
// Клиент без состояния: без подписок и без своего кэша; отмена — параметр signal
// пробрасывается в request (lib/offline).

import { request } from '../../lib/offline';
import type {
  ChildAttachmentRejectCode,
  ChildChatListItem,
  ChildProfile,
  ChildStatus,
  ChildSubjectView,
} from './types';

// Отказ вложения (413/415/422 с code) — типизированная ошибка вызывающему:
// code — для действия интерфейса, message — серверный текст для ребёнка по-русски
// (ADR-041, «Общие правила»: фронт показывает его как есть).
export class ChildAttachmentRejectedError extends Error {
  readonly status: number;
  readonly code: ChildAttachmentRejectCode;
  constructor(status: number, code: ChildAttachmentRejectCode, message: string) {
    super(message);
    this.name = 'ChildAttachmentRejectedError';
    this.status = status;
    this.code = code;
  }
}

const ATTACHMENT_REJECT_CODES: ChildAttachmentRejectCode[] = ['too_large', 'unsupported_type', 'pdf_no_text', 'empty_file'];

export const childApi = {
  // GET /api/child/status — снимок окон/лимитов (старшая причина по порядку И3).
  // live: снимок per-owner, а ключ IDB-кэша — по URL, общий для владельцев
  // (тот же повод, что у live у account.auth.me) — чужой снимок подставился бы этому.
  // signal — отмена вызывающего (опрос KM-F2), доходит до fetch через request.
  status: (space?: string, opts?: { signal?: AbortSignal }) =>
    request<ChildStatus>(space ? `/child/status?space=${encodeURIComponent(space)}` : '/child/status', {
      live: true,
      signal: opts?.signal,
    }),

  // POST /api/child/heartbeat — присутствие в пространстве (время считается присутствием,
  // ADR-041 решение 4); ответ — тот же ChildStatus, экономит второй запрос status.
  heartbeat: (space: string) =>
    request<ChildStatus>('/child/heartbeat', { method: 'POST', body: JSON.stringify({ space }) }),

  // POST /api/child/spaces/{space}/chat — новый чат с предметом: тьютор, провайдер и модель
  // берутся из записи предмета профиля (не из полей профиля), плюс ChildSpace, ChildSubject,
  // ProjectId = null. Отказы — на error.body.code (request): 400 unknown_subject (пустой или
  // нет в профиле), 403 subject_disabled, 409 с code = state (rules_pending/…/time_up).
  spaceChat: (space: string, subject: string) =>
    request<{ sessionId: string }>(`/child/spaces/${encodeURIComponent(space)}/chat`, {
      method: 'POST',
      body: JSON.stringify({ subject }),
    }),

  // GET /api/child/spaces/{space}/chats — детские чаты пространства, новые сверху.
  // live и signal — по тем же причинам, что у status.
  spaceChats: (space: string, opts?: { signal?: AbortSignal }) =>
    request<{ chats: ChildChatListItem[] }>(
      `/child/spaces/${encodeURIComponent(space)}/chats`,
      { live: true, signal: opts?.signal },
    ),

  // GET /api/child/subjects — проекция репетиторов ребёнка: только включённые предметы,
  // в порядке профиля (И5); без промпта, маршрута и голоса. Порядок не переставляем.
  // live — проекция per-owner, а ключ IDB-кэша — по URL, общий для владельцев (тот же
  // повод, что у live у status). Отказы 403 not_child / 503 policy_unavailable — на error.body.
  getSubjects: async (opts?: { signal?: AbortSignal }): Promise<ChildSubjectView[]> => {
    const r = await request<{ subjects: ChildSubjectView[] }>('/child/subjects', {
      live: true,
      signal: opts?.signal,
    });
    return r.subjects;
  },

  // PUT /api/child/chats/{id}/voice — озвучка ответов (TTS, расход — в бюджет; решение Андрея 4:
  // голосового ввода без микрофона нет). style отсутствует в теле — сервер берёт своё.
  voice: (id: string, enabled: boolean, style?: 'talk' | 'digest') =>
    request<{ voiceMode: boolean; voiceStyle: string }>(`/child/chats/${encodeURIComponent(id)}/voice`, {
      method: 'PUT',
      // JSON.stringify выбрасывает поле со значением undefined — style шлём только заданный
      body: JSON.stringify({ enabled, style }),
    }),

  // POST /api/child/rules/start — чат онбординга правил, идемпотентно (существующий
  // незавершённый или новый). Отказы: 409 rules_already_agreed, 503 policy_unavailable.
  rulesStart: () => request<{ sessionId: string }>('/child/rules/start', { method: 'POST' }),

  // POST /api/child/rules/agree — только факт согласия (RulesAgreedAt/RulesSummary),
  // лимиты не трогает никогда (ADR-041, «Отвергнуто»). summary — 1..2000 символов,
  // отказы 400 summary_empty/summary_too_long — на error.body.code (request).
  rulesAgree: (sessionId: string, summary: string) =>
    request<{ rulesAgreedAt: string }>('/child/rules/agree', {
      method: 'POST',
      body: JSON.stringify({ sessionId, summary }),
    }),

  // POST /api/child/chats/{id}/attachments — multipart, поле `file` (решение Андрея 5:
  // allow-list по сигнатуре файла, PDF → текст на сервере). Ответ: path относительный
  // (.cc-attachments/…), его вызывающий передаёт в SendMessage(attachedPaths).
  // 413/415/422 с code → ChildAttachmentRejectedError; прочие отказы — как есть.
  uploadAttachment: async (id: string, file: Blob) => {
    const form = new FormData();
    form.append('file', file);
    try {
      return await request<{ path: string; kind: 'image' | 'text'; name: string }>(
        `/child/chats/${encodeURIComponent(id)}/attachments`,
        { method: 'POST', body: form },
      );
    } catch (e) {
      const err = e as { status?: number; body?: { code?: string; message?: string } } | null;
      const code = err?.body?.code as ChildAttachmentRejectCode | undefined;
      if ((err?.status === 413 || err?.status === 415 || err?.status === 422) && code && ATTACHMENT_REJECT_CODES.includes(code)) {
        throw new ChildAttachmentRejectedError(err.status, code, err.body?.message ?? 'Не удалось загрузить вложение');
      }
      throw e;
    }
  },

  // POST /api/child/stop-signal — кнопка «Мне неприятно» (источник child; маркер тьютора
  // разбирает сервер тем же путём). 202 без текста переписки; 429 too_frequent — на error.body.
  stopSignal: (sessionId?: string) =>
    request<{ accepted: true }>('/child/stop-signal', {
      method: 'POST',
      // sessionId отсутствует — поле не отправляем (JSON.stringify выбрасывает undefined)
      body: JSON.stringify({ sessionId }),
    }),

  // PUT /api/users/{id}/child-profile — только админ ([Authorize(Roles="admin")] + AdminByStore,
  // без [ChildAllowed]); { enabled: false } — выключение режима. Отказы 400 not_container/
  // provider_not_api_key/invalid_window/invalid_space/guardian_not_found — на error.body.code.
  putProfile: (userId: string, profile: ChildProfile | { enabled: false }) =>
    request<{ profile: ChildProfile; tutorPersonaId: string }>(`/users/${encodeURIComponent(userId)}/child-profile`, {
      method: 'PUT',
      body: JSON.stringify(profile),
    }),
};

// URL аватара репетитора — одна точка построения (И5: GET /api/personas/{id}/avatar, [ChildAllowed],
// PersonasController.cs:654). hasAvatar=true ⟺ ручка отдаёт картинку (тот же признак, что у 404
// в ней, ChildSubjectsController.HasAvatar); пропал файл с диска — 404, вызывающий возвращается
// к инициалам. Тег <img> — без JWT-заголовка, токен в ?access_token= (тот же путь, что у raw-картинок).
export function tutorAvatarUrl(personaId: string, accessToken?: string): string {
  const base = `/api/personas/${encodeURIComponent(personaId)}/avatar`;
  return accessToken ? `${base}?access_token=${encodeURIComponent(accessToken)}` : base;
}
