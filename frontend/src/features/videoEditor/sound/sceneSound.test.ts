import { beforeEach, describe, expect, it, vi } from 'vitest';

const requestMock = vi.hoisted(() => vi.fn());
vi.mock('aihome_shell/kit', async orig => ({ ...(await orig<Record<string, unknown>>()), request: requestMock }));

import { etaLabel, quoteEtas, soundOptions, tooLong, voiceScene, type SoundModel } from './sceneSound';

const model = (id: string, over: Partial<SoundModel> = {}): SoundModel => ({ id, label: id, license: 'MIT', heavy: false, maxSec: 10, ...over });

const catalog = (available = true) => ({
  providers: [
    { key: 'fal', available: true, reason: null, models: [{ id: 'x', label: 'X', caps: { ops: ['sfx'], license: { label: 'c' } } }] },
    {
      key: 'local', available, reason: available ? null : 'Локальные модели работают только в чате проекта',
      models: [
        { id: 'moss-sfx-v2', label: 'MOSS', caps: { ops: ['sfx'], license: { label: 'Apache-2.0' } } },
        { id: 'hunyuanvideo-foley-xl', label: 'Foley XL', caps: { ops: ['videoToAudio'], license: { label: 'Tencent' }, inputMaxSec: 10 } },
        { id: 'hunyuanvideo-foley-xxl', label: 'Foley XXL', caps: { ops: ['videoToAudio'], license: { label: 'Tencent' }, heavyOps: ['videoToAudio'], inputMaxSec: 10 } },
        { id: 'mmaudio-large-44k', label: 'MMAudio', caps: { ops: ['sfx', 'videoToAudio'], license: { label: 'CC BY-NC' }, inputMaxSec: 8 } },
      ],
    },
  ],
});

beforeEach(() => { requestMock.mockReset(); });

describe('soundOptions', () => {
  it('берёт только локальные модели с videoToAudio в порядке каталога, тяжёлые помечены', () => {
    const o = soundOptions(catalog());
    expect(o.models.map(m => m.id)).toEqual(['hunyuanvideo-foley-xl', 'hunyuanvideo-foley-xxl', 'mmaudio-large-44k']);
    expect(o.models.map(m => m.heavy)).toEqual([false, true, false]);
    expect(o.reason).toBeNull();
  });
  it('недоступные локальные модели — причина серым, нет каталога — отдельная причина', () => {
    expect(soundOptions(catalog(false)).reason).toContain('только в чате проекта');
    expect(soundOptions(null).reason).toContain('не заведены');
    expect(soundOptions(null).models).toEqual([]);
  });
});

describe('etaLabel / tooLong', () => {
  it('секунды и минуты, нет оценки — честно', () => {
    expect(etaLabel(40)).toBe('≈ 40 с');
    expect(etaLabel(150)).toBe('≈ 3 мин');
    expect(etaLabel(null)).toBe('время не оценено');
  });
  it('клип длиннее предела модели — причина, в пределе — null', () => {
    expect(tooLong(model('m', { maxSec: 8 }), 9)).toContain('до 8 с');
    expect(tooLong(model('m', { maxSec: 8 }), 8.1)).toBeNull();
    expect(tooLong(model('m', { maxSec: null }), 100)).toBeNull();
  });
});

describe('quoteEtas', () => {
  it('котирует каждую модель; сбой одной не роняет остальные', async () => {
    requestMock.mockImplementation(async (_url: string, init: { body: string }) => {
      const b = JSON.parse(init.body) as { model: string; durationSec: number; operation: string };
      expect(b.operation).toBe('videoToAudio');
      expect(b.durationSec).toBe(5);
      if (b.model === 'b') throw new Error('нет');
      return { quoteId: 'q', price: { eta: b.model === 'a' ? 40 : 25 } };
    });
    const r = await quoteEtas('p1', 's1', [model('a'), model('b'), model('c')], 4.2);
    expect(r).toEqual({ a: 40, b: null, c: 25 });
  });
});

describe('voiceScene', () => {
  it('черновик нити → котировка → задача с версией сцены и вклейкой', async () => {
    requestMock.mockImplementation(async (url: string, init?: { method?: string; body?: unknown }) => {
      if (url.endsWith('/threads') && init?.method === 'POST') return { revision: 4, focus: 'th1', threads: [{ id: 'th1' }] };
      if (url.endsWith('/threads')) return { revision: 3, focus: null, threads: [] };
      if (url.endsWith('/quote')) return { quoteId: 'q9', price: { eta: 40 } };
      if (url.endsWith('/jobs')) return { jobId: 'j1' };
      throw new Error(url);
    });
    const r = await voiceScene({ scope: 'p1', sessionId: 's1', sceneId: 'sc1', versionId: 'v2', modelId: 'hunyuanvideo-foley-xl', durationSec: 4.2, prompt: ' шаги ' });
    expect(r).toEqual({ jobId: 'j1', threadId: 'th1' });
    const calls = requestMock.mock.calls;
    expect(String(calls[0][0])).toBe('/projects/p1/audio-editor/sessions/s1/threads');
    expect(JSON.parse(calls[1][1].body)).toMatchObject({ draftFolder: '', mode: 'music', revision: 3 });
    expect(JSON.parse(calls[2][1].body)).toMatchObject({ operation: 'videoToAudio', provider: 'local', model: 'hunyuanvideo-foley-xl', threadId: 'th1', prompt: 'шаги', durationSec: 5 });
    const form = calls[3][1].body as FormData;
    expect(Object.fromEntries(form.entries())).toMatchObject({
      quoteId: 'q9', sessionId: 's1', threadId: 'th1', videoSceneId: 'sc1', videoVersionId: 'v2', pasteIntoScene: 'true', prompt: 'шаги', durationSec: '5',
    });
  });
  it('нить не завелась — понятная ошибка, задача не стартует', async () => {
    requestMock.mockResolvedValue({ revision: 1, focus: null, threads: [] });
    await expect(voiceScene({ scope: 'p1', sessionId: 's1', sceneId: 'sc', versionId: null, modelId: 'm', durationSec: 3, prompt: '' })).rejects.toThrow('нить звука');
  });
});
