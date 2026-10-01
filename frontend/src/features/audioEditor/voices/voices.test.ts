import { beforeEach, describe, expect, it, vi } from 'vitest';

const calls: { url: string; options?: RequestInit & { timeoutMs?: number } }[] = [];
vi.mock('aihome_shell/kit', () => ({
  request: vi.fn(async (url: string, options?: RequestInit) => { calls.push({ url, options }); return {}; }),
  readStoredToken: () => 'tok',
}));

const { voicesApi, MINIMAX_RECREATE_READY } = await import('./api');
const {
  EMPTY_TITLE, PERSONAL_TITLE, RECREATE_PENDING_HINT, isStale, newVoiceProblem, pickedSlug, recreateAction,
  sampleRemoval, samplesProblem, voicePickValue, voicesView, voiceSubtitle, whereWorks,
} = await import('./model');
type AudioVoice = import('./api').AudioVoice;

const NOW = new Date('2026-10-01T12:00:00Z');

function voice(over: Partial<AudioVoice> = {}): AudioVoice {
  return {
    slug: 'anya', name: 'Аня', kind: 'samples', path: 'voices/anya',
    samples: [{ file: 'sample-1.wav' }, { file: 'sample-2.wav' }], transcript: 'Привет',
    createdAt: '2026-09-20T00:00:00Z',
    providers: [
      { provider: 'higgsfield', state: 'none', createdAt: null, lastUsedAt: null },
      { provider: 'minimax', state: 'none', createdAt: null, lastUsedAt: null },
      { provider: 'falQwen', state: 'none', createdAt: null, lastUsedAt: null },
      { provider: 'rvc', state: 'none', createdAt: null, lastUsedAt: null },
    ],
    needsAttention: false,
    ...over,
  };
}

beforeEach(() => { calls.length = 0; });

describe('список голосов', () => {
  it('проект: список по имени, тип и число образцов в подписи', () => {
    const view = voicesView(false, { available: true, voices: [voice({ slug: 'ya', name: 'Яна' }), voice()] }, null);
    expect(view.kind).toBe('list');
    expect(view.kind === 'list' && view.voices.map(v => v.name)).toEqual(['Аня', 'Яна']);
    expect(voiceSubtitle(voice())).toBe('По записям · 2 образца');
    expect(voiceSubtitle(voice({ kind: 'rvc', samples: [] }))).toBe('Модель RVC · voice.pth · voice.index');
  });

  it('пустой проект, загрузка и ошибка различаются', () => {
    expect(voicesView(false, { available: true, voices: [] }, null).kind).toBe('empty');
    expect(voicesView(false, null, null).kind).toBe('loading');
    expect(voicesView(false, null, 'сеть')).toEqual({ kind: 'error', message: 'сеть' });
    expect(EMPTY_TITLE).toBe('Голосов пока нет');
  });

  it('запрос списка идёт по ручке проекта', async () => {
    await voicesApi.list('p1', 's1');
    expect(calls[0].url).toBe('/projects/p1/audio-editor/voices');
  });

  it('«Где работает»: локальные — да, Яндекс — не умеет, клон до запуска — не создан', () => {
    const rows = whereWorks(voice(), NOW);
    const by = Object.fromEntries(rows.map(r => [r.key, r.status]));
    expect(by).toEqual({ 'local-tts': 'ok', 'local-vc': 'ok', higgsfield: 'none', minimax: 'none', falQwen: 'none', yandex: 'no' });
    const rvc = whereWorks(voice({ kind: 'rvc', providers: [{ provider: 'rvc', state: 'ok', createdAt: '2026-09-30T00:00:00Z', lastUsedAt: null }] }), NOW);
    expect(rvc.map(r => r.status)).toEqual(['ok', 'no']);
  });

  it('«Выбрать» отдаёт значение поля voice:<slug>', () => {
    expect(voicePickValue('anya')).toBe('voice:anya');
    expect(pickedSlug('voice:anya')).toBe('anya');
    expect(pickedSlug('Eric')).toBeNull();
  });
});

describe('создание голоса', () => {
  it('нужны имя и 1–5 записей до 50 МБ', () => {
    const one = { files: [{ name: 'a.wav', size: 1000 }], projectFiles: [] };
    expect(newVoiceProblem('', one)).toBe('Укажите имя голоса');
    expect(newVoiceProblem('Аня', { files: [], projectFiles: [] })).toBe('Добавьте хотя бы одну запись');
    expect(newVoiceProblem('Аня', { files: [], projectFiles: ['1', '2', '3', '4', '5', '6'] })).toBe('Не больше 5 записей');
    expect(newVoiceProblem('Аня', { files: [{ name: 'big.wav', size: 51 * 1024 * 1024 }], projectFiles: [] })).toBe('big.wav больше 50 МБ');
    expect(newVoiceProblem('Аня', { files: [], projectFiles: ['audio/anya.wav'] })).toBeNull();
    expect(newVoiceProblem(' Аня ', one)).toBeNull();
  });

  it('добавить образец — с учётом уже имеющихся', () => {
    expect(samplesProblem({ files: [], projectFiles: ['a', 'b'] }, 4)).toBe('У голоса не больше 5 записей — можно добавить ещё 1');
    expect(samplesProblem({ files: [], projectFiles: ['a'] }, 4)).toBeNull();
  });

  it('форма уходит multipart: имя, расшифровка, загрузки и файлы проекта', async () => {
    const file = new File(['x'], 'a.wav', { type: 'audio/wav' });
    await voicesApi.create('p1', { name: ' Аня ', transcript: 'Привет', files: [file], projectFiles: ['audio/b.wav'] });
    const { url, options } = calls[0];
    expect(url).toBe('/projects/p1/audio-editor/voices');
    expect(options?.method).toBe('POST');
    const form = options?.body as FormData;
    expect(form.get('name')).toBe('Аня');
    expect(form.get('transcript')).toBe('Привет');
    expect((form.getAll('files')[0] as File).name).toBe('a.wav');
    expect(form.getAll('projectFiles')).toEqual(['audio/b.wav']);
  });
});

describe('образцы', () => {
  it('последний образец убрать нельзя', () => {
    expect(sampleRemoval(voice({ samples: [{ file: 'only.wav' }] })))
      .toEqual({ allowed: false, reason: 'Последнюю запись убрать нельзя — удалите голос целиком' });
    expect(sampleRemoval(voice()).allowed).toBe(true);
  });
});

describe('протухший клон MiniMax', () => {
  const stale = voice({
    needsAttention: true,
    providers: [{ provider: 'minimax', state: 'stale', createdAt: '2026-09-20T00:00:00Z', lastUsedAt: '2026-09-22T12:00:00Z' }],
  });

  it('строка «клон удалён» с давностью и значок на карточке', () => {
    const row = whereWorks(stale, NOW).find(r => r.key === 'minimax');
    expect(row).toEqual({ key: 'minimax', label: 'fal · MiniMax клон', status: 'stale', note: 'MiniMax удалил клон: не использовался 9 дней' });
    expect(isStale(stale)).toBe(true);
    expect(isStale(voice())).toBe(false);
  });

  it('«Пересоздать» неактивна, пока ручки нет; с ручкой и ценой — активна', () => {
    expect(MINIMAX_RECREATE_READY).toBe(false);
    expect(recreateAction('$1.5')).toEqual({ label: 'Пересоздать · $1.5', disabled: true, hint: RECREATE_PENDING_HINT });
    expect(recreateAction('$1.5', true)).toEqual({ label: 'Пересоздать · $1.5', disabled: false, hint: null });
    expect(recreateAction(null, true).disabled).toBe(true);
  });
});

describe('личный чат', () => {
  it('честный пустой экран и по области, и по ответу available:false', () => {
    expect(voicesView(true, null, null)).toEqual({ kind: 'personal' });
    expect(voicesView(false, { available: false, voices: [] }, null)).toEqual({ kind: 'personal' });
    expect(PERSONAL_TITLE).toBe('«Голоса» живут в проекте');
  });

  it('список — по ручке чата, мутации отказывают до запроса', async () => {
    await voicesApi.list('personal', 's1');
    expect(calls[0].url).toBe('/audio-editor/chats/s1/voices');
    expect(() => voicesApi.create('personal', { name: 'Аня' })).toThrow('Библиотека «Голоса» живёт в проекте');
    expect(() => voicesApi.remove('personal', 'anya')).toThrow();
    expect(calls).toHaveLength(1);
  });
});
