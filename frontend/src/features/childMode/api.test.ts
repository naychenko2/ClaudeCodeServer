import { beforeEach, describe, expect, it, vi } from 'vitest';
import { childApi, ChildAttachmentRejectedError, tutorAvatarUrl } from './api';
import type { ChildProfile, ChildSubjectView, ChildTutorPatch } from './types';

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

describe('PUT /api/users/{id}/child-profile', () => {
  const profile: ChildProfile = {
    enabled: true,
    guardianUserIds: ['u2', 'u3'],
    providerKey: 'pk-org',
    model: 'claude-sonnet-4-5',
    spaces: { lessons: { windows: [{ from: '16:00', to: '19:00', weekdays: [1, 2, 3] }], dailyMinutes: 30 } },
    dailyBudgetUsd: 2,
    monthlyBudgetUsd: 40,
    warnMinutes: 5,
  };

  it('тело — ChildProfile целиком', async () => {
    await childApi.putProfile('u1', profile);
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u1/child-profile');
    expect(opts.method).toBe('PUT');
    expect(JSON.parse(opts.body as string)).toEqual(profile);
  });

  it('выключение — { enabled: false }', async () => {
    await childApi.putProfile('u1', { enabled: false });
    const { opts } = await lastCall();
    expect(JSON.parse(opts.body as string)).toEqual({ enabled: false });
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

describe('childApi.tutorAvatar.* — /api/users/{id}/child-tutors/{personaId}/avatar/*', () => {
  const avatarBase = '/users/u1/child-tutors/p%201/avatar';

  it('generate: POST { } без опций, prompt trim-ится, count по заданному; таймаут — под бюджет генерации', async () => {
    const r = await req();
    r.mockResolvedValueOnce({ candidates: ['cand-1.png'] });
    const got = await childApi.tutorAvatar.generate('u1', 'p 1');
    const first = await lastCall();
    expect(first.url).toBe(`${avatarBase}/generate`);
    expect(first.opts.method).toBe('POST');
    expect(JSON.parse(first.opts.body as string)).toEqual({});
    expect(first.opts.timeoutMs).toBe(400_000);
    expect(got.candidates).toEqual(['cand-1.png']);

    await childApi.tutorAvatar.generate('u1', 'p 1', { prompt: '  портрет учительницы ', count: 2 });
    const second = await lastCall();
    expect(JSON.parse(second.opts.body as string)).toEqual({ prompt: 'портрет учительницы', count: 2 });
  });

  it('upload: multipart original+cropped+crop, таймаут как у загрузки аватара персоны', async () => {
    const original = new File(['\x89PNG-данные'], 'photo.png', { type: 'image/png' });
    const cropped = new Blob(['\x89PNG-crop'], { type: 'image/png' });
    await childApi.tutorAvatar.upload('u1', 'p 1', original, cropped, { scale: 1, offsetX: 0, offsetY: 0 });
    const { url, opts } = await lastCall();
    expect(url).toBe(`${avatarBase}/upload`);
    expect(opts.method).toBe('POST');
    expect(opts.timeoutMs).toBe(60_000);
    const form = opts.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    expect((form.get('original') as Blob).size).toBe(original.size);
    expect((form.get('cropped') as Blob).size).toBe(cropped.size);
    expect(JSON.parse(form.get('crop') as string)).toEqual({ scale: 1, offsetX: 0, offsetY: 0 });
  });

  it('select: POST { file }; 503 image_generation_off — на error.body.code', async () => {
    await childApi.tutorAvatar.select('u1', 'p 1', 'cand-1.png');
    const { url, opts } = await lastCall();
    expect(url).toBe(`${avatarBase}/select`);
    expect(opts.method).toBe('POST');
    expect(JSON.parse(opts.body as string)).toEqual({ file: 'cand-1.png' });

    const r = await req();
    r.mockRejectedValueOnce(httpErr(503, 'image_generation_off', 'Генерация изображений не настроена.'));
    const e = (await rejection(childApi.tutorAvatar.generate('u1', 'p 1'))) as { status?: number; body?: { code?: string } };
    expect(e.status).toBe(503);
    expect(e.body?.code).toBe('image_generation_off');
  });
});

describe('childApi.materials — /api/users/{id}/child-subjects/{subjectId}/materials (К5a)', () => {
  const base = '/users/u%201/child-subjects/l%20it/materials';
  const file = () => new File(['данные файла'], 'уравнения.pdf', { type: 'application/pdf' });

  // Таблица ручек (разрез §2): метод и URL каждого вызова, id экранированы.
  const table = [
    { name: 'list', method: 'GET', url: base, call: () => childApi.materials.list('u 1', 'l it') },
    { name: 'upload', method: 'POST', url: base, call: () => childApi.materials.upload('u 1', 'l it', file()) },
    { name: 'remove', method: 'DELETE', url: `${base}/m_1`, call: () => childApi.materials.remove('u 1', 'l it', 'm_1') },
  ];
  it.each(table)('$name: $method в свой URL', async (t) => {
    await t.call();
    const { url, opts } = await lastCall();
    expect(url).toBe(t.url);
    expect((opts.method ?? 'GET').toUpperCase()).toBe(t.method);
  });

  it('list: массив строк проходит как пришёл (обёртки нет), содержимого и preview в строке нет', async () => {
    const rows = [
      { id: 'm_1', name: 'уравнения.pdf', kind: 'pdf', bytes: 20480, uploadedAt: '2026-11-18T10:00:00Z' },
      { id: 'm_2', name: 'страница.png', kind: 'image', bytes: 1024, uploadedAt: '2026-11-18T10:05:00Z' },
    ];
    const r = await req();
    r.mockResolvedValueOnce(rows);
    const got = await childApi.materials.list('u1', 'lit');
    expect(got).toEqual(rows);
  });

  it('upload: multipart поле file (имя файла сохраняется), ответ — строка + preview', async () => {
    const uploaded = {
      id: 'm_1', name: 'уравнения.pdf', kind: 'pdf', bytes: 1024, uploadedAt: '2026-11-18T10:00:00Z',
      preview: 'Распознанный текст первых 600 символов…',
    };
    const r = await req();
    r.mockResolvedValueOnce(uploaded);
    const got = await childApi.materials.upload('u1', 'lit', file());
    const { opts } = await lastCall();
    const form = opts.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    expect((form.get('file') as File).name).toBe('уравнения.pdf');
    expect(got).toEqual(uploaded);
    expect(got.preview).toBe('Распознанный текст первых 600 символов…');
  });

  it('upload не резается таймаутом короче серверного распознавания (до 3 мин)', async () => {
    await childApi.materials.upload('u1', 'lit', file());
    const { opts } = await lastCall();
    // Дефолт request — 30 с (offline.ts:396): без явного перекрытия загрузка падала бы на середине.
    expect((opts.timeoutMs as number | undefined) ?? 30_000).toBeGreaterThanOrEqual(180_000);
  });

  it('415 unsupported_type и 503 ocr_unavailable — как у putProfile: status + body.code, без обёртки', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(415, 'unsupported_type', 'Такой файл нельзя добавить: подходит текстовый PDF, DOCX или TXT.'));
    const e415 = await rejection(childApi.materials.upload('u1', 'lit', file()));
    expect(e415).not.toBeInstanceOf(ChildAttachmentRejectedError);
    expect((e415 as { status?: number }).status).toBe(415);
    expect((e415 as { body?: { code?: string } }).body?.code).toBe('unsupported_type');

    r.mockRejectedValueOnce(httpErr(503, 'ocr_unavailable', 'Страницу не удалось распознать.'));
    const e503 = await rejection(childApi.materials.upload('u1', 'lit', file()));
    expect((e503 as { status?: number }).status).toBe(503);
    expect((e503 as { body?: { code?: string } }).body?.code).toBe('ocr_unavailable');
  });

  it('remove: 404 material_not_found — status + body.code (204 — обычный успех без тела)', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(404, 'material_not_found', 'Материал не найден.'));
    const e = (await rejection(childApi.materials.remove('u1', 'lit', 'm_x'))) as { status?: number; body?: { code?: string } };
    expect(e.status).toBe(404);
    expect(e.body?.code).toBe('material_not_found');
  });
});
