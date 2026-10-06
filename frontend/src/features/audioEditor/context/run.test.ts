import { beforeEach, describe, expect, it, vi } from 'vitest';

// Запуск, цена, «Чем» и параметры действий звука: тонкий слой над существующими котировкой, задачей и
// ручками без ИИ. Окружение node: хранилища и window подставляем сами
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(new Map()));
vi.stubGlobal('sessionStorage', fakeStorage(new Map()));
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { audioApi } = await import('../api');
const { __applyThreads, __resetAudioStore, __setScopeData, setSelection } = await import('../thread/threadStore');
const { __applyChatContext, __resetChatContextStore } = await import('../../../lib/chatContext/store');
const { __resetExecutorState, NO_AI_ROW } = await import('./executors');
const { audioKindApi } = await import('./kind');
import type { AudioCatalog, AudioThread, AudioThreadsState } from '../api';
import type { ChatContextDto } from '../../../lib/chatContext/types';

const P = 'p1';
const S = 's1';
const CTX = { projectId: P, sessionId: S, isMobile: false };

const thread = (patch: Partial<AudioThread> = {}): AudioThread => ({
  id: 't1', file: null, lineage: [], draftFolder: '', createdAt: '2026-10-03T00:00:00Z', versions: [],
  currentVersionId: null, launches: [], settings: null, ...patch,
});
const song = (): AudioThread => thread({
  file: 'music/a.mp3', currentVersionId: 'origin', settings: { mode: 'music', operation: null, provider: null, model: null, fields: null },
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, license: null, createdAt: '', files: [{ role: 'main', path: 'music/a.mp3' }] }],
});
const withStems = (): AudioThread => thread({
  file: 'music/a.mp3', currentVersionId: 'v1',
  versions: [{ id: 'v1', number: 1, jobId: 'j0', variant: 1, baseVersionId: null, license: null, createdAt: '', files: [
    { role: 'stem:vocals', path: 'a/vocals.mp3' }, { role: 'stem:drums', path: 'a/drums.mp3' },
  ] }],
});
const state = (t: AudioThread): AudioThreadsState => ({ focus: t.id, revision: 3, threads: [t] });
const context = (revision: number): ChatContextDto => ({
  revision, refs: [],
  primary: { id: 'a1', kind: 'audio', ref: { threadId: 't1' }, by: 'human', addedAt: '2026-10-03T00:00:00Z', label: 'a.mp3', version: null, thumb: null, missing: false, role: null },
});

const caps = (patch: object) => ({ languages: [], voiceKinds: [], producesFiles: [], license: { label: 'MIT', kind: 'permissive' }, priceUnit: 'free', ...patch });
const catalog: AudioCatalog = {
  autoModelId: 'auto', maxCount: 3,
  providers: [
    { key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null, models: [
      { id: 'fal-ai/stems4', label: 'Stems 4', caps: caps({ ops: ['separate'], stemSet: '4' }) },
    ] },
    { key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null, models: [
      { id: 'demucs6', label: 'Demucs 6', caps: caps({ ops: ['separate'], stemSet: '6' }) },
      { id: 'qwen', label: 'Qwen TTS', caps: caps({ ops: ['speak'] }) },
    ] },
  ],
} as unknown as AudioCatalog;

let quoted: Record<string, unknown>[];
let started: Record<string, unknown>[];
let mixed: Record<string, unknown>[];
let concatenated: Record<string, unknown>[];

beforeEach(() => {
  __resetAudioStore(); __resetChatContextStore(); __resetExecutorState();
  quoted = []; started = []; mixed = []; concatenated = [];
  vi.spyOn(audioApi, 'subscribe').mockImplementation(() => () => {});
  vi.spyOn(audioApi, 'quote').mockImplementation(async (_s, _c, req) => {
    quoted.push({ ...req });
    return { quoteId: 'q1', provider: 'local', model: 'm', price: { amount: null, unit: 'free', approx: false, source: 'x', eta: null, queueLength: null } } as never;
  });
  vi.spyOn(audioApi, 'startJob').mockImplementation(async (_s, _c, input) => { started.push({ ...input }); return { jobId: 'j1' }; });
  vi.spyOn(audioApi, 'mix').mockImplementation(async (_s, _c, _t, req) => {
    mixed.push({ ...req });
    return { threadId: 't1', versionId: 'v2', number: 2, jobId: 'jm', state: state(withStems()) };
  });
  vi.spyOn(audioApi, 'concat').mockImplementation(async (_s, _c, req) => {
    concatenated.push({ ...req });
    return { threadId: 't2', versionId: 'origin', jobId: 'jc', name: 'склейка' };
  });
  __setScopeData(P, catalog, { voice: null, music: null, process: null });
});

const apply = (t: AudioThread, revision = 7) => { __applyThreads(S, P, state(t)); __applyChatContext(S, context(revision)); };
const run = (op: string, text = '', params: Record<string, string | number> = {}, contextRevision = 7) =>
  audioKindApi.launch!(CTX, { op, text, params, contextRevision });

describe('запуск по ревизии контекста', () => {
  it('«Озвучить»: котировка и запуск несут ревизию и текст в нужном поле, нить из тела не едет', async () => {
    apply(thread({ settings: { mode: 'voice', operation: null, provider: null, model: null, fields: null } }));
    const h = await run('speak', ' Привет ', { variants: 2 });
    expect(h.id).toBe('j1');
    expect(quoted[0]).toMatchObject({ mode: 'voice', operation: 'speak', provider: 'auto', model: 'auto', count: 2, text: 'Привет', prompt: null, contextRevision: 7 });
    expect(started[0]).toMatchObject({ quoteId: 'q1', text: 'Привет', contextRevision: 7 });
    expect(started[0].threadId).toBeUndefined();
  });

  it('«Песня»: текст уходит в prompt, режим music', async () => {
    apply(thread());
    await run('song', 'рок, гитара');
    expect(quoted[0]).toMatchObject({ mode: 'music', operation: 'song', text: null, prompt: 'рок, гитара' });
    expect(started[0]).toMatchObject({ prompt: 'рок, гитара', text: null });
  });

  it('«Перегенерировать кусок»: границы — из выделения на волне', async () => {
    apply(song());
    setSelection(S, 't1', { start: 1.5, end: 4, versionId: 'origin' });
    await run('repaint', 'мягче');
    expect(started[0]).toMatchObject({ startSec: 1.5, endSec: 4, prompt: 'мягче' });
  });

  it('«Стемы»: набор выбирает модель по caps.stemSet, а не по id', async () => {
    apply(song());
    await run('separate', '', { stemSet: '6' });
    expect(quoted[0]).toMatchObject({ mode: 'process', operation: 'separate', provider: 'local', model: 'demucs6', text: null, prompt: null });
    await run('separate', '', { stemSet: '4' });
    expect(quoted[1]).toMatchObject({ provider: 'fal', model: 'fal-ai/stems4' });
  });

  it('«Стемы» с набором, которого нет ни у кого, отказывают без запроса', async () => {
    apply(song());
    await expect(run('separate', '', { stemSet: 'karaoke' })).rejects.toThrow(/набор/);
    expect(quoted).toHaveLength(0);
  });

  it('«Свести»: все стемы версии без ИИ, ревизия контекста в теле', async () => {
    apply(withStems());
    const h = await run('mixStems');
    expect(h.id).toBe('jm');
    expect(mixed[0]).toMatchObject({ stems: [{ role: 'stem:vocals', gainDb: 0 }, { role: 'stem:drums', gainDb: 0 }], contextRevision: 7 });
    expect(quoted).toHaveLength(0);
  });

  it('«Склеить»: куски — из контекста, в теле только ревизия', async () => {
    apply(song());
    const h = await run('concat');
    expect(h.id).toBe('jc');
    expect(concatenated[0]).toEqual({ contextRevision: 7 });
  });

  it('нить пропала (контекст сменился) — запуск отказывает', async () => {
    __applyChatContext(S, context(7));
    await expect(run('denoise')).rejects.toThrow(/недоступен/);
  });
});

describe('цена, «Чем» и параметры', () => {
  it('без ИИ цена — «бесплатно» и без запроса', async () => {
    apply(withStems());
    expect(await audioKindApi.quote!(CTX, { op: 'mixStems', text: '', params: {}, contextRevision: 7 })).toMatchObject({ price: 'бесплатно' });
    expect(quoted).toHaveLength(0);
  });

  it('ИИ-операция: цена из котировки по ревизии контекста', async () => {
    apply(song());
    const q = await audioKindApi.quote!(CTX, { op: 'denoise', text: '', params: {}, contextRevision: 7 });
    expect(q.price).toBe('бесплатно');
    expect(quoted[0]).toMatchObject({ operation: 'denoise', contextRevision: 7 });
  });

  it('«Чем» у «Свести» и «Склеить» — одна строка «Без ИИ · на сервере · бесплатно»', () => {
    apply(withStems());
    for (const id of ['mix', 'concat']) {
      const m = audioKindApi.executors!(CTX, id)!;
      expect(m.rows).toEqual([NO_AI_ROW]);
      expect(m.rows[0]).toMatchObject({ name: 'Без ИИ', sub: 'на сервере', price: 'бесплатно', free: true });
    }
  });

  it('«Чем» у ИИ-действия — строки каталога под операцию, выбор помнится на операцию', () => {
    apply(song());
    const m = audioKindApi.executors!(CTX, 'stems')!;
    expect(m.value).toBe('auto');
    expect(m.rows.map(r => r.id)).toEqual(['auto', 'local|demucs6', 'fal|fal-ai/stems4']);
    m.onChange('local|demucs6');
    expect(audioKindApi.executors!(CTX, 'stems')!.value).toBe('local|demucs6');
    expect(audioKindApi.executors!(CTX, 'denoise')?.value).toBe('auto');
  });

  it('выбранный исполнитель уходит в котировку явно', async () => {
    apply(song());
    audioKindApi.executors!(CTX, 'stems')!.onChange('fal|fal-ai/stems4');
    await run('separate', '', { stemSet: '4' });
    expect(quoted[0]).toMatchObject({ provider: 'fal', model: 'fal-ai/stems4' });
  });

  it('варианты — у создающих звук операций, потолок из каталога; у обработки и без ИИ параметров нет', () => {
    apply(thread({ settings: { mode: 'voice', operation: null, provider: null, model: null, fields: null } }));
    expect(audioKindApi.params!(CTX, 'speak')).toEqual([{ kind: 'variants', min: 1, max: 3, value: 1 }]);
    apply(song());
    expect(audioKindApi.params!(CTX, 'stems')).toEqual([]);
    apply(withStems());
    expect(audioKindApi.params!(CTX, 'mix')).toEqual([]);
  });
});
