// Детский режим: тонкий клиент ручек /api/child/* и админских ручек профиля, предметов и
// репетиторов (PUT child-profile, child-subjects/{id}/tutor, PUT child-tutors/{id}, child-routes).
// Контракт — docs/adr/ADR-041-child-mode.md, раздел «Контракт /api/child/*» (бэк обязан
// совпасть с тем же разделом). В lib/api.ts НЕ подключается (у файла бюджет строк,
// apiFileBudget.test.ts): потребители импортируют childApi напрямую.
// Клиент без состояния: без подписок и без своего кэша; отмена — параметр signal
// пробрасывается в request (lib/offline).

import { request } from '../../lib/offline';
import type { Persona } from '../../types';
import type {
  ChildAttachmentRejectCode,
  ChildChatListItem,
  ChildRoute,
  ChildSettingsProfile,
  ChildSettingsResponse,
  ChildStatus,
  ChildSubjectView,
  ChildTutorPatch,
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

// Единственное пространство режима (ADR-041, «Общие правила»): «lessons». Держится здесь:
// «новый урок» не должно знать, какой подставить space — это свойство режима, а не вызывающего.
const LESSONS_SPACE = 'lessons';

// «Эту папку видят: …» (Б14а, решение 64): имена кураторов-опекунов живого профиля в его
// порядке, DisplayName иначе username (ChildSubjectsController: поле viewers ответа и его
// селектор имён). Список один на
// профиль — одинаковый для всех его предметов. Пустой — никого: строку не рисуем вовсе
// (записка Майи к макету kids-project-screen: пустого «Эту папку видят:» не бывает).
export type ChildSubjectViewers = string[];

// Предмет глазами клиента: плитка ChildSubjectView плюс то, что добавляет к ней ответ
// GET /api/child/subjects (viewers — поле ответа ChildSubjectsController).
// enabled в ответе детской ручки нет — там только включённые (тот же контроллер,
// фильтр .Where(s is { Enabled: true }));
// явный false приходит оттуда, где полный список предметов уже есть (окно настроек Ф2),
// и док проектов-предметов скрывает его сам: правило решения 84
// («выключенный предмет в доке не показывается») живёт в одном месте, а не в каждом
// вызывающем.
export type ChildSubjectWithViewers = ChildSubjectView & {
  viewers?: ChildSubjectViewers;
  enabled?: boolean;
};

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

  // «Новый урок» — ЕДИНСТВЕННАЯ точка создания детского чата на фронте (ADR-041 ред. 8,
  // решение 84: «новый урок — кнопка нового чата проекта, которая у ребёнка зовёт
  // POST /api/child/spaces/{space}/chat»). Space берётся отсюда: вызывающий называет
  // только предмет. Ответ и отказы — те же, что у spaceChat (400 unknown_subject,
  // 403 subject_disabled, 409 с code = state).
  createLesson: (subjectId: string) => childApi.spaceChat(LESSONS_SPACE, subjectId),

  // GET /api/child/spaces/{space}/chats — детские чаты пространства, новые сверху.
  // live и signal — по тем же причинам, что у status.
  spaceChats: (space: string, opts?: { signal?: AbortSignal }) =>
    request<{ chats: ChildChatListItem[] }>(
      `/child/spaces/${encodeURIComponent(space)}/chats`,
      { live: true, signal: opts?.signal },
    ),

  // GET /api/child/subjects — проекция репетиторов ребёнка: только включённые предметы,
  // в порядке профиля (И5); без промпта, маршрута и голоса, но с viewers — именами
  // кураторов для строки «Эту папку видят: …» (Б14а). Порядок не переставляем.
  // live — проекция per-owner, а ключ IDB-кэша — по URL, общий для владельцев (тот же
  // повод, что у live у status). Отказы 403 not_child / 503 policy_unavailable — на error.body.
  getSubjects: async (opts?: { signal?: AbortSignal }): Promise<ChildSubjectWithViewers[]> => {
    const r = await request<{ subjects: ChildSubjectWithViewers[] }>('/child/subjects', {
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

  // ── Окно настроек ребёнка (Ф2a, ручка Б4a) ──────────────────────────────────
  // Все три — admin + AdminByStore, без [ChildAllowed]: ребёнок свои настройки не правит
  // (сторож ChildModeEndpointGuardTests). Отказы — { code, message } на status и error.body.code,
  // как у putChildProfile ниже.

  // PUT /api/users/{id}/child-mode { enabled } — узкая ручка галки «детский режим» (ADR-041
  // ред. 4 решение 30): тела профиля нет. Включение заводит профиль «не настроен» (без предметов,
  // маршрутов и тьютора) и ничего не провит: повторный вызов включённого режима — идемпотентный
  // 200 без записи; выключенный возвращается с сохранёнными настройками. Выключение — профиль
  // остаётся с enabled: false. Ответ — та же форма, что у child-profile. Отказы: 404 user_not_found,
  // 400 not_container (среда исполнения — не песочница), 500 profile_not_saved.
  setChildMode: (userId: string, enabled: boolean) =>
    request<ChildSettingsResponse>(`/users/${encodeURIComponent(userId)}/child-mode`, {
      method: 'PUT',
      body: JSON.stringify({ enabled }),
    }),

  // GET /api/users/{id}/child-profile — живой профиль для окна настроек. live: окно чернит
  // текущий профиль сервера — офлайн-копия означала бы «сохранить поверх того, чего уже может
  // не быть». Отказ 404 user_not_found.
  getChildProfile: (userId: string) =>
    request<ChildSettingsResponse>(`/users/${encodeURIComponent(userId)}/child-profile`, {
      live: true,
    }),

  // PUT /api/users/{id}/child-profile — единственная ручка записи профиля (решение 12/30):
  // тело — ChildSettingsProfile (предмет — только плитка, без маршрута и репетитора);
  // { enabled: false } — выключение режима с сохранением настроек. Согласие профиля трёхзначно
  // в теле: отсутствие поля или null — «не трогать», false — явный отзыв; клиент ничего не
  // подставляет сам (false вместо отсутствующего поля отозвал бы живое согласие).
  // Отказы валидатора — на error.body.code: not_container, provider_not_api_key, model_not_concrete,
  // model_unpriced, model_not_on_plan, model_not_of_provider, route_risk_unacknowledged,
  // invalid_space, invalid_window, subject_id_invalid, subject_label_invalid, subject_glyph_invalid,
  // subject_color_invalid, guardian_not_found (ChildProfileValidator).
  putChildProfile: (userId: string, profile: ChildSettingsProfile | { enabled: false }) =>
    request<ChildSettingsResponse>(`/users/${encodeURIComponent(userId)}/child-profile`, {
      method: 'PUT',
      body: JSON.stringify(profile),
    }),

  // GET /api/admin/child-routes — допустимые пары «поставщик → модель» для пикера профиля (Т2f):
  // список отбирает сервер той же ChildRoute.Evaluate, что валидатор и сверка запуска, — порядок
  // уходит как пришёл (записи пула в порядке конфига, затем провайдеры). Кэшируемый GET: список
  // конфигурации, не per-owner состояние (тот же признак, что у взрослого GET /models).
  // signal — отмена вызывающего (ревью Т4a): редактор профиля отменяет загрузку при смене
  // пользователя или уходе с экрана, чужой список не доходит до состояния (тот же приём, что у status).
  getRoutes: async (opts?: { signal?: AbortSignal }): Promise<ChildRoute[]> => {
    const r = await request<{ routes: ChildRoute[] }>('/admin/child-routes', { signal: opts?.signal });
    return r.routes;
  },

  // POST /api/users/{id}/child-subjects/{subjectId}/tutor — провижн персоны-репетитора предмета
  // (Т2c): идемпотенен, живой ключ возвращает тот же personaId. Тела нет. Отказы 404
  // subject_not_found (ни пользователя, ни предмета в его профиле) / 500 tutor_provision_failed —
  // на error.body.code (ChildTutorsController.cs:58-81).
  provisionSubjectTutor: (userId: string, subjectId: string) =>
    request<{ personaId: string }>(
      `/users/${encodeURIComponent(userId)}/child-subjects/${encodeURIComponent(subjectId)}/tutor`,
      { method: 'POST' },
    ),

  // PUT /api/users/{id}/child-tutors/{personaId} — правка характера репетитора и списков
  // «Обязательно / Никогда» (редакция 2 §12, редакция 3 §22): частичный патч, null-поле не трогает.
  // Ответ — обновлённая персона. Отказы 404 tutor_not_found / 400 rules_too_long, voice_invalid /
  // 500 tutor_update_failed — на status и error.body.code (персона проверяется как
  // репетитор ЭТОГО ребёнка, включая репетитора выключенного предмета).
  putTutor: (userId: string, personaId: string, patch: ChildTutorPatch) =>
    request<Persona>(`/users/${encodeURIComponent(userId)}/child-tutors/${encodeURIComponent(personaId)}`, {
      method: 'PUT',
      body: JSON.stringify(patch),
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

