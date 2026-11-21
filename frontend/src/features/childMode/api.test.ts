import { beforeEach, describe, expect, it, vi } from 'vitest';
import { childApi, ChildAttachmentRejectedError, tutorAvatarUrl } from './api';
import type { ChildSettingsProfile, ChildSettingsResponse, ChildSubjectView, ChildTutorPatch } from './types';

// Клиент проверяем по его запросам: моким request (lib/offline) утверждаем URL, метод
// и тело по таблице «Ручки» ADR-041 («Контракт /api/child/*»). Путь request() строит
// '/api' + url — здесь ожидаем путь после префикса, как его передаёт клиент.
vi.mock('../../lib/offline', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../lib/offline')>();
  return { ...actual, request: vi.fn() };
});

async function req() {
  const { request } = await import('../../lib/offline');
  return request as ReturnType<typeof vi.fn>;
}

// Последний вызов: [url, options] — на нём проверяются URL/метод/тела вызовов.
async function lastCall(): Promise<{ url: string; opts: Record<string, unknown> }> {
  const calls = (await req()).mock.calls;
  const last = calls[calls.length - 1];
  return { url: last[0] as string, opts: (last[1] ?? {}) as Record<string, unknown> };
}

async function rejection(p: Promise<unknown>): Promise<unknown> {
  try {
    await p;
  } catch (e) {
    return e;
  }
  throw new Error('ожидался отказ');
}

// Ошибка как её выставляет request на !res.ok: status + тело целиком (ADR-041, «Общие правила»).
function httpErr(status: number, code: string, message: string): Error {
  const e = new Error(message) as Error & { status?: number; body?: unknown };
  e.status = status;
  e.body = { code, message };
  return e;
}

beforeEach(async () => {
  const r = await req();
  r.mockReset();
  r.mockResolvedValue({});
});

describe('GET /api/child/status', () => {
  it('без space: URL без параметра, GET, live (снимок per-owner, кэш общий по URL)', async () => {
    await childApi.status();
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/status');
    expect((opts.method ?? 'GET').toUpperCase()).toBe('GET');
    expect(opts.live).toBe(true);
  });

  it('space уходит в query-параметр', async () => {
    await childApi.status('lessons');
    const { url } = await lastCall();
    expect(url).toBe('/child/status?space=lessons');
  });

  it('signal вызывающего доходит до request (и fetch)', async () => {
    const ac = new AbortController();
    await childApi.status('lessons', { signal: ac.signal });
    const { opts } = await lastCall();
    expect(opts.signal).toBe(ac.signal);
  });
});

describe('POST /api/child/heartbeat', () => {
  it('тело { space }', async () => {
    await childApi.heartbeat('lessons');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/heartbeat');
    expect(opts.method).toBe('POST');
    expect(JSON.parse(opts.body as string)).toEqual({ space: 'lessons' });
  });
});

describe('POST /api/child/spaces/{space}/chat', () => {
  it('тело { subject } на путь пространства', async () => {
    await childApi.spaceChat('lessons', 'russian');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/spaces/lessons/chat');
    expect(opts.method).toBe('POST');
    expect(JSON.parse(opts.body as string)).toEqual({ subject: 'russian' });
  });

  it('400 unknown_subject и 403 subject_disabled — как прочие отказы: status + body.code', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(400, 'unknown_subject', 'Такого предмета нет.'));
    const e400 = (await rejection(childApi.spaceChat('lessons', 'nope'))) as { status?: number; body?: { code?: string } };
    expect(e400.status).toBe(400);
    expect(e400.body?.code).toBe('unknown_subject');

    r.mockRejectedValueOnce(httpErr(403, 'subject_disabled', 'Этот предмет закрыт.'));
    const e403 = (await rejection(childApi.spaceChat('lessons', 'sport'))) as { status?: number; body?: { code?: string } };
    expect(e403.status).toBe(403);
    expect(e403.body?.code).toBe('subject_disabled');
  });
});

describe('GET /api/child/spaces/{space}/chats', () => {
  it('live, GET; signal доходит до request', async () => {
    const ac = new AbortController();
    await childApi.spaceChats('lessons', { signal: ac.signal });
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/spaces/lessons/chats');
    expect((opts.method ?? 'GET').toUpperCase()).toBe('GET');
    expect(opts.live).toBe(true);
    expect(opts.signal).toBe(ac.signal);
  });

  it('чаты проходят как есть: subject с сервера, subjectClosed сохранён', async () => {
    const chats = [
      { sessionId: 's1', name: 'Дроби', updatedAt: '2026-11-17T10:00:00Z', subject: 'math', subjectClosed: false },
      { sessionId: 's2', name: 'Старый урок', updatedAt: '2026-11-16T10:00:00Z', subject: null, subjectClosed: true },
    ];
    const r = await req();
    r.mockResolvedValueOnce({ chats });
    const got = await childApi.spaceChats('lessons');
    expect(got.chats).toEqual(chats);
  });
});

describe('GET /api/child/subjects', () => {
  const subjects: ChildSubjectView[] = [
    { id: 'russian', label: 'Русский язык', glyph: 'Аа', color: 'info', tutor: { personaId: 'p1', name: 'Репетитор русский', hasAvatar: true } },
    // висящий ключ персоны — бэк отдаёт tutor: null (ChildSubjectsController)
    { id: 'math', label: 'Математика', glyph: '×÷', color: 'accent', tutor: null },
  ];

  it('live, GET; { subjects } разворачивается, порядок — как пришёл с сервера', async () => {
    const r = await req();
    r.mockResolvedValueOnce({ subjects });
    const got = await childApi.getSubjects();
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/subjects');
    expect((opts.method ?? 'GET').toUpperCase()).toBe('GET');
    expect(opts.live).toBe(true);
    expect(got).toEqual(subjects);
  });

  it('signal вызывающего доходит до request', async () => {
    const ac = new AbortController();
    await childApi.getSubjects({ signal: ac.signal });
    const { opts } = await lastCall();
    expect(opts.signal).toBe(ac.signal);
  });
});

describe('tutorAvatarUrl', () => {
  it('одна точка /api/personas/{id}/avatar; id и токен — экранированы', () => {
    expect(tutorAvatarUrl('p 1')).toBe('/api/personas/p%201/avatar');
    expect(tutorAvatarUrl('p1', 'tok&1')).toBe('/api/personas/p1/avatar?access_token=tok%261');
  });
});

describe('PUT /api/child/chats/{id}/voice', () => {
  it('style отправляется, только когда задан', async () => {
    await childApi.voice('c1', true, 'talk');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/chats/c1/voice');
    expect(opts.method).toBe('PUT');
    expect(JSON.parse(opts.body as string)).toEqual({ enabled: true, style: 'talk' });

    await childApi.voice('c1', false);
    const second = await lastCall();
    expect(JSON.parse(second.opts.body as string)).toEqual({ enabled: false });
  });
});

describe('POST /api/child/rules/*', () => {
  it('start — пустое тело', async () => {
    await childApi.rulesStart();
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/rules/start');
    expect(opts.method).toBe('POST');
    expect(opts.body).toBeUndefined();
  });

  it('agree — { sessionId, summary }', async () => {
    await childApi.rulesAgree('c1', 'Правила: сначала уроки, потом игры');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/rules/agree');
    expect(opts.method).toBe('POST');
    expect(JSON.parse(opts.body as string)).toEqual({ sessionId: 'c1', summary: 'Правила: сначала уроки, потом игры' });
  });
});

describe('POST /api/child/chats/{id}/attachments', () => {
  it('multipart, поле file', async () => {
    const file = new Blob(['\x89PNG-данные'], { type: 'image/png' });
    await childApi.uploadAttachment('c1', file);
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/chats/c1/attachments');
    expect(opts.method).toBe('POST');
    const form = opts.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    expect((form.get('file') as Blob).size).toBe(file.size);
  });

  it('413 too_large → типизированная ошибка с code и message сервера', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(413, 'too_large', 'Файл слишком большой'));
    const e = (await rejection(childApi.uploadAttachment('c1', new Blob(['x'])))) as ChildAttachmentRejectedError;
    expect(e).toBeInstanceOf(ChildAttachmentRejectedError);
    expect(e.status).toBe(413);
    expect(e.code).toBe('too_large');
    expect(e.message).toBe('Файл слишком большой');
  });

  it('415 unsupported_type и 422 pdf_no_text — те же поля', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(415, 'unsupported_type', 'Такие файлы нельзя'));
    const e415 = (await rejection(childApi.uploadAttachment('c1', new Blob(['x'])))) as ChildAttachmentRejectedError;
    expect(e415.code).toBe('unsupported_type');
    r.mockRejectedValueOnce(httpErr(422, 'pdf_no_text', 'В этом PDF нет текста'));
    const e422 = (await rejection(childApi.uploadAttachment('c1', new Blob(['x'])))) as ChildAttachmentRejectedError;
    expect(e422.code).toBe('pdf_no_text');
    expect(e422.message).toBe('В этом PDF нет текста');
  });

  it('прочий отказ (500) — как есть, не в обёртке', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(500, 'oops', 'Сбой сервера'));
    const e = await rejection(childApi.uploadAttachment('c1', new Blob(['x'])));
    expect(e).not.toBeInstanceOf(ChildAttachmentRejectedError);
    expect((e as { status?: number }).status).toBe(500);
  });
});

describe('POST /api/child/stop-signal', () => {
  it('без sessionId — пустой объект; с sessionId — { sessionId }', async () => {
    await childApi.stopSignal();
    const first = await lastCall();
    expect(first.url).toBe('/child/stop-signal');
    expect(first.opts.method).toBe('POST');
    expect(JSON.parse(first.opts.body as string)).toEqual({});

    await childApi.stopSignal('c1');
    const second = await lastCall();
    expect(JSON.parse(second.opts.body as string)).toEqual({ sessionId: 'c1' });
  });
});

// ── Окно настроек ребёнка (Ф2a) ────────────────────────────────────────────────
// Форма ручек после Б4a (docs/architecture/api.md, «Детский режим: окно настроек ребёнка»;
// бэк — ChildProfileDto/ChildSubjectDto/ChildGuardianDto, маппинг — ToChildProfileDto).
// Клиент проверяем по запросам: что уходит в тело и что проходит из ответа как есть.

// Профиль «не настроен» (маршрута профиля нет — пустые строки, законно по валидатору) с
// предметом-плиткой: так окно чернит профиль сразу после галки.
const windowProfile = (): ChildSettingsProfile => ({
  enabled: true,
  guardianUserIds: ['u2', 'u3'],
  providerKey: '',
  model: '',
  spaces: { lessons: { windows: [{ from: '16:00', to: '19:00', weekdays: [1, 2, 3] }], dailyMinutes: 30 } },
  subjects: [{ id: 'math', label: 'Математика', glyph: '×÷', color: 'accent', enabled: true }],
  dailyBudgetUsd: 2,
  monthlyBudgetUsd: 40,
  warnMinutes: 5,
});

describe('окно: PUT /api/users/{id}/child-mode — узкая ручка галки', () => {
  it('тело — только { enabled: true }, id экранирован; ответ проходит как пришёл', async () => {
    const r = await req();
    const res: ChildSettingsResponse = { profile: windowProfile(), tutorPersonaId: '' };
    r.mockResolvedValueOnce(res);
    const got = await childApi.setChildMode('u 1', true);
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u%201/child-mode');
    expect(opts.method).toBe('PUT');
    expect(JSON.parse(opts.body as string)).toEqual({ enabled: true });
    expect(got).toEqual(res);
  });

  it('enabled: false — то же тело с false (выключение с сохранением настроек)', async () => {
    await childApi.setChildMode('u1', false);
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u1/child-mode');
    expect(JSON.parse(opts.body as string)).toEqual({ enabled: false });
  });

  it('400 not_container и 404 user_not_found — status + body.code (текст — с сервера)', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(400, 'not_container', 'Детский режим включается только пользователям со средой исполнения container.'));
    const e400 = (await rejection(childApi.setChildMode('u1', true))) as { status?: number; body?: { code?: string } };
    expect(e400.status).toBe(400);
    expect(e400.body?.code).toBe('not_container');

    r.mockRejectedValueOnce(httpErr(404, 'user_not_found', 'Пользователь не найден.'));
    const e404 = (await rejection(childApi.setChildMode('u1', true))) as { status?: number; body?: { code?: string } };
    expect(e404.status).toBe(404);
    expect(e404.body?.code).toBe('user_not_found');
  });
});

// Одна ручка пути: чтение и запись профиля окна в одном описании.
describe('окно: GET/PUT /api/users/{id}/child-profile', () => {
  it('GET, live (окно чернит текущий профиль сервера), id экранирован', async () => {
    await childApi.getChildProfile('u 1');
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u%201/child-profile');
    expect((opts.method ?? 'GET').toUpperCase()).toBe('GET');
    expect(opts.live).toBe(true);
  });

  it('ответ окна проходит как есть: согласие с автором, guardians по опекуну, рубильник инстанса, knowledgeMoved', async () => {
    const r = await req();
    const res: ChildSettingsResponse = {
      profile: {
        ...windowProfile(),
        acknowledgeRouteRisk: true,
        routeRiskAcknowledgedAt: '2026-11-20T10:00:00Z',
        routeRiskAcknowledgedBy: 'admin',
        guardians: [{ userId: 'u2', sharedProjectsFlag: true }, { userId: 'u3', sharedProjectsFlag: false }],
        sharedProjectsInstanceEnabled: true,
        subjects: [{ id: 'math', label: 'Математика', glyph: '×÷', color: 'accent', enabled: true, knowledgeMoved: true }],
      },
      tutorPersonaId: 'p-1',
    };
    r.mockResolvedValueOnce(res);
    const got = await childApi.getChildProfile('u1');
    expect(got).toEqual(res);
    // Порядок guardians — как guardianUserIds; флаг опекуна и рубильник — разные вещи окна.
    expect(got.profile?.guardians).toEqual([{ userId: 'u2', sharedProjectsFlag: true }, { userId: 'u3', sharedProjectsFlag: false }]);
    expect(got.profile?.sharedProjectsInstanceEnabled).toBe(true);
    expect(got.profile?.routeRiskAcknowledgedBy).toBe('admin');
    expect(got.profile?.subjects?.[0].knowledgeMoved).toBe(true);
  });

  it('профиля нет — profile: null проходит как есть (выключение пользователя, который им и не был)', async () => {
    const r = await req();
    r.mockResolvedValueOnce({ profile: null, tutorPersonaId: '' });
    expect(await childApi.getChildProfile('u2')).toEqual({ profile: null, tutorPersonaId: '' });
  });

  it('404 user_not_found — status + body.code', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(404, 'user_not_found', 'Пользователь не найден.'));
    const e = (await rejection(childApi.getChildProfile('u1'))) as { status?: number; body?: { code?: string } };
    expect(e.status).toBe(404);
    expect(e.body?.code).toBe('user_not_found');
  });

  it('тело — профиль окна целиком, id экранирован', async () => {
    const profile = windowProfile();
    await childApi.putChildProfile('u 1', profile);
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u%201/child-profile');
    expect(opts.method).toBe('PUT');
    expect(JSON.parse(opts.body as string)).toEqual(profile);
  });

  it('тело предмета — только плитка: ни providerKey, ни model, ни tutorPersonaId (маршрут и репетитор — у проекта-предмета)', async () => {
    await childApi.putChildProfile('u1', windowProfile());
    const { opts } = await lastCall();
    const body = JSON.parse(opts.body as string) as { subjects: Record<string, unknown>[] };
    const subject = body.subjects[0];
    expect(Object.keys(subject).sort()).toEqual(['color', 'enabled', 'glyph', 'id', 'label']);
    for (const removed of ['providerKey', 'model', 'tutorPersonaId']) {
      expect(subject).not.toHaveProperty(removed);
    }
  });

  it('отсутствие enabled в предмете — поля в теле нет (сервер берёт «включён»), клиент не подставляет', async () => {
    const profile: ChildSettingsProfile = {
      ...windowProfile(),
      subjects: [{ id: 'lit', label: 'Литература', glyph: 'Л', color: 'rose' }],
    };
    await childApi.putChildProfile('u1', profile);
    const { opts } = await lastCall();
    const body = JSON.parse(opts.body as string) as { subjects: Record<string, unknown>[] };
    expect(body.subjects[0]).not.toHaveProperty('enabled');
  });

  // Согласие профиля в теле трёхзначно: «не трогать» не должно превращаться в «отозвать» —
  // false вместо отсутствующего поля снял бы живое согласие (решение 26).
  it('acknowledgeRouteRisk: null или отсутствие поля — в теле не false; false и true проходят как даны', async () => {
    const withNull: ChildSettingsProfile = { ...windowProfile(), acknowledgeRouteRisk: null };
    await childApi.putChildProfile('u1', withNull);
    let body = JSON.parse((await lastCall()).opts.body as string) as Record<string, unknown>;
    expect(body.acknowledgeRouteRisk ?? null).toBeNull();

    await childApi.putChildProfile('u1', windowProfile());
    body = JSON.parse((await lastCall()).opts.body as string) as Record<string, unknown>;
    expect(body).not.toHaveProperty('acknowledgeRouteRisk');

    await childApi.putChildProfile('u1', { ...windowProfile(), acknowledgeRouteRisk: false });
    body = JSON.parse((await lastCall()).opts.body as string) as Record<string, unknown>;
    expect(body.acknowledgeRouteRisk).toBe(false);

    await childApi.putChildProfile('u1', { ...windowProfile(), acknowledgeRouteRisk: true });
    body = JSON.parse((await lastCall()).opts.body as string) as Record<string, unknown>;
    expect(body.acknowledgeRouteRisk).toBe(true);
  });

  it('выключение — { enabled: false } без тела профиля', async () => {
    await childApi.putChildProfile('u1', { enabled: false });
    const { opts } = await lastCall();
    expect(JSON.parse(opts.body as string)).toEqual({ enabled: false });
  });

  it('400 route_risk_unacknowledged — status + body.code (текст отказа показывает окно как есть)', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(400, 'route_risk_unacknowledged', 'Маршрут профиля не Anthropic: нужно согласие на профиле ребёнка (acknowledgeRouteRisk).'));
    const e = (await rejection(childApi.putChildProfile('u1', windowProfile()))) as { status?: number; body?: { code?: string } };
    expect(e.status).toBe(400);
    expect(e.body?.code).toBe('route_risk_unacknowledged');
  });
});

describe('GET /api/admin/child-routes', () => {
  const routes = [
    { providerKey: 'pk-org', kind: 'pool' as const, label: 'Орг', priced: true, requiresAck: false, models: [{ id: 'claude-sonnet-4-5', label: 'Sonnet 4.5' }] },
    { providerKey: 'local-qwen', kind: 'local' as const, label: 'Qwen локально', priced: false, requiresAck: true, models: [{ id: 'qwen3.8-27b', label: 'Qwen3.8' }] },
  ];

  it('GET без параметров; { routes } разворачивается, порядок — как пришёл с сервера', async () => {
    const r = await req();
    r.mockResolvedValueOnce({ routes });
    const got = await childApi.getRoutes();
    const { url, opts } = await lastCall();
    expect(url).toBe('/admin/child-routes');
    expect((opts.method ?? 'GET').toUpperCase()).toBe('GET');
    expect(got).toEqual(routes);
  });
});

describe('POST /api/users/{id}/child-subjects/{subjectId}/tutor', () => {
  it('id и subject экранированы, тела нет; ответ — { personaId }', async () => {
    const r = await req();
    r.mockResolvedValueOnce({ personaId: 'p1' });
    const got = await childApi.provisionSubjectTutor('u 1', 'lit');
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u%201/child-subjects/lit/tutor');
    expect(opts.method).toBe('POST');
    expect(opts.body).toBeUndefined();
    expect(got.personaId).toBe('p1');
  });

  it('404 subject_not_found — как прочие отказы: status + body.code', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(404, 'subject_not_found', 'Предмет не найден в профиле ребёнка.'));
    const e = (await rejection(childApi.provisionSubjectTutor('u1', 'nope'))) as { status?: number; body?: { code?: string } };
    expect(e.status).toBe(404);
    expect(e.body?.code).toBe('subject_not_found');
  });
});

describe('PUT /api/users/{id}/child-tutors/{personaId}', () => {
  it('полный патч уходит как есть: null — «не трогать», [] — очистка', async () => {
    const patch: ChildTutorPatch = {
      name: 'Репетитор литературы',
      character: 'Ты ведёшь уроки литературы.',
      greeting: 'Привет! Давай читать.',
      voice: { voice: 'alena', role: 'good' },
      gender: 'female',
      mustDo: ['Сразу давай ответ'],
      mustNot: [],
    };
    await childApi.putTutor('u1', 'p 1', patch);
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u1/child-tutors/p%201');
    expect(opts.method).toBe('PUT');
    expect(JSON.parse(opts.body as string)).toEqual(patch);
  });

  it('незаданные поля в тело не попадают (частичный патч)', async () => {
    await childApi.putTutor('u1', 'p1', { character: 'Новый характер', mustNot: null });
    const { opts } = await lastCall();
    expect(JSON.parse(opts.body as string)).toEqual({ character: 'Новый характер', mustNot: null });
  });

  it('400 rules_too_long — status + body.code (текст сообщения — с сервера)', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(400, 'rules_too_long', 'Правил «Обязательно / Никогда» — не больше 12.'));
    const e = (await rejection(childApi.putTutor('u1', 'p1', { mustDo: ['x'] }))) as { status?: number; body?: { code?: string } };
    expect(e.status).toBe(400);
    expect(e.body?.code).toBe('rules_too_long');
  });
});
