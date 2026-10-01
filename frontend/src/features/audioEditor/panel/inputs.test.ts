import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
import { audioApi, type AudioPrefs, type AudioQuote, type AudioThread } from '../api';
import { TO_END } from '../player/selection';
import { __applyThreads, __resetAudioStore } from '../thread/threadStore';
import { jointsFor } from './ConcatFields';
import {
  DEFAULT_INPUTS, inputsKey, mergeInputs, migrateLocal, projectInputs, readInputs, saveSettings, serverPiece, toServerInputs,
  type PanelInputs,
} from './inputs';
import { nextSettings, resolvePanel, voicePick } from './model';
import { jobInput, runPanel } from './run';

const thread = (id: string, settings: AudioThread['settings'] = null): AudioThread => ({
  id, file: `${id}.mp3`, lineage: [], draftFolder: null, createdAt: '',
  versions: [{ id: 'v1', number: 1, jobId: null, variant: null, baseVersionId: null, files: [], license: null, createdAt: '' }],
  currentVersionId: 'v1', launches: [], settings,
});
const NO_PREFS: AudioPrefs = { voice: null, music: null, process: null };
const BASE = { provider: null, model: null, fields: null, count: 1 };

beforeEach(() => {
  localStorage.clear();
  __resetAudioStore();
  vi.restoreAllMocks();
});

describe('входы операции на сервере', () => {
  const full: PanelInputs = {
    ...DEFAULT_INPUTS, language: 'ru', referencePath: 'voices/a.wav', voice: 'voice:anya',
    replicas: [{ voice: 'Аня', text: 'Привет' }, { voice: '', text: 'Пока' }],
    concat: {
      ...DEFAULT_INPUTS.concat,
      pieces: [{ threadId: 't1', versionId: 'v1', label: 'x' }, { projectFile: 'a/j.mp3', label: 'a/j.mp3' }],
      joints: [{ kind: 'crossfade', seconds: 1 }],
    },
  };

  it('уходят только входы текущей операции, пустое не едет', () => {
    expect(toServerInputs('cloneVoice', full, null, false)).toEqual({ language: 'ru', referencePath: 'voices/a.wav', voice: 'voice:anya' });
    expect(toServerInputs('dialogue', full, null, false)).toEqual({ language: 'ru', dialogue: [{ text: 'Привет', voice: 'Аня' }, { text: 'Пока' }] });
    expect(toServerInputs('concat', full, null, false)).toEqual({
      pieces: [{ threadId: 't1', versionId: 'v1' }, { projectFile: 'a/j.mp3' }],
      joint: DEFAULT_INPUTS.concat.joint, joints: [{ kind: 'crossfade', seconds: 1 }],
    });
    expect(toServerInputs('repaint', full, { start: 2, end: TO_END }, false)).toEqual({ startSec: 2 });
    expect(toServerInputs('trim', full, { start: 2, end: 5 }, false)).toEqual({ startSec: 2, endSec: 5 });
    expect(toServerInputs('denoise', full, null, false)).toBeNull();
    expect(toServerInputs('speak', DEFAULT_INPUTS, null, false)).toBeNull();
  });

  it('у личного чата нет путей проекта и библиотеки', () => {
    expect(toServerInputs('cloneVoice', full, null, true)).toEqual({ language: 'ru' });
    expect(toServerInputs('concat', full, null, true)?.pieces).toEqual([{ threadId: 't1', versionId: 'v1' }]);
  });

  it('с сервера — назад в панель: подписи кусков по нитям, кусок «до конца»', () => {
    const merged = mergeInputs(DEFAULT_INPUTS, {
      voice: 'voice:anya', dialogue: [{ text: 'Привет' }],
      pieces: [{ threadId: 'gone' }, { projectFile: 'a/j.mp3' }],
    }, [thread('t1')]);
    expect(merged.voice).toBe('voice:anya');
    expect(merged.replicas).toEqual([{ text: 'Привет', voice: '' }]);
    expect(merged.concat.pieces.map(p => p.label)).toEqual(['Звук удалён из чата', 'a/j.mp3']);
    expect(serverPiece({ startSec: 2 })).toEqual({ start: 2, end: TO_END });
    expect(serverPiece({ endSec: 2 })).toBeNull();
  });

  it('смена режима или операции шлёт входы новой операции, а не старые', () => {
    const cur = resolvePanel(thread('t1', {
      mode: 'voice', operation: 'cloneVoice', ...BASE, inputs: { language: 'ru', referencePath: 'voices/a.wav', voice: 'voice:anya' },
    }), NO_PREFS, null, 'voice');
    expect(cur.inputs).toEqual({ language: 'ru', referencePath: 'voices/a.wav', voice: 'voice:anya' });
    expect(nextSettings(cur, { operation: 'dialogue' }).inputs).toEqual({ language: 'ru' });
    expect(nextSettings(cur, { mode: 'process' }).inputs).toBeNull();
    expect(nextSettings(cur, { mode: 'process', operation: 'master' }).inputs).toEqual({ referencePath: 'voices/a.wav' });
    // Прочие правки входы сохраняют — PUT иначе стёр бы их
    expect(nextSettings(cur, { provider: 'fal' }).inputs).toEqual(cur.inputs);
    expect(nextSettings(cur, { inputs: null }).inputs).toBeNull();
  });

  it('входы едут в настройки нити и в префы режима без нити', async () => {
    const t = thread('t1');
    __applyThreads('s1', 'p1', { focus: 't1', revision: 3, threads: [t] });
    const next = { mode: 'voice' as const, operation: 'speak' as const, ...BASE, inputs: { language: 'ru' } };
    const put = vi.spyOn(audioApi, 'settings').mockResolvedValue({ focus: 't1', revision: 4, threads: [{ ...t, settings: next }] });
    await saveSettings('p1', 's1', t, next);
    expect(put.mock.calls[0][3].inputs).toEqual({ language: 'ru' });

    const prefs = vi.spyOn(audioApi, 'putPrefs').mockResolvedValue(NO_PREFS);
    await saveSettings('p1', 's1', null, next);
    expect(prefs.mock.calls[0][3].inputs).toEqual({ language: 'ru' });
  });
});

describe('перенос из localStorage', () => {
  const key = inputsKey('p1', 's1', 't1');
  const legacy = { ...DEFAULT_INPUTS, language: 'ru', referencePath: 'voices/a.wav', voiceModelPath: 'v.pth' };

  it('на сервере пусто — переносим один раз и чистим локальное, своё браузерное оставляем', () => {
    localStorage.setItem(key, JSON.stringify(legacy));
    expect(migrateLocal(key, null, 'cloneVoice', false)).toEqual({ language: 'ru', referencePath: 'voices/a.wav' });
    const left = JSON.parse(localStorage.getItem(key)!);
    expect(left.language).toBeUndefined();
    expect(left.referencePath).toBeUndefined();
    expect(readInputs(key).voiceModelPath).toBe('v.pth');
    expect(migrateLocal(key, null, 'cloneVoice', false)).toBeNull();
  });

  it('на сервере уже есть — не переносим, но локальную копию стираем', () => {
    localStorage.setItem(key, JSON.stringify(legacy));
    expect(migrateLocal(key, { language: 'en' }, 'cloneVoice', false)).toBeNull();
    expect(JSON.parse(localStorage.getItem(key)!).language).toBeUndefined();
  });

  it('нечего переносить — ничего не трогаем', () => {
    expect(migrateLocal(key, null, 'speak', false)).toBeNull();
    expect(localStorage.getItem(key)).toBeNull();
  });
});

describe('голос из библиотеки', () => {
  it('«Выбрать» ставит голос в поле операции, которая его берёт', () => {
    const cur = resolvePanel(thread('t1', { mode: 'voice', operation: 'convertVoice', ...BASE }), NO_PREFS, null, 'voice');
    expect(voicePick(cur, DEFAULT_INPUTS, 'voice:anya', false)).toEqual({ inputs: { voice: 'voice:anya' } });
  });

  it('операция голос не берёт — переходим на «Образец» уже с голосом', () => {
    const cur = resolvePanel(thread('t1', { mode: 'process', operation: 'denoise', ...BASE }), NO_PREFS, null, 'voice');
    const pick = voicePick(cur, { ...DEFAULT_INPUTS, language: 'ru' }, 'voice:anya', false);
    expect(pick).toEqual({ patch: { mode: 'voice', operation: 'cloneVoice', inputs: { language: 'ru', voice: 'voice:anya' } } });
    expect('patch' in pick && nextSettings(cur, pick.patch)).toMatchObject({
      mode: 'voice', operation: 'cloneVoice', inputs: { language: 'ru', voice: 'voice:anya' },
    });
  });

  it('в задачу едет голос, а образец и модель RVC — нет', () => {
    const state = resolvePanel(thread('t1', { mode: 'voice', operation: 'convertVoice', ...BASE }), NO_PREFS, null, 'voice');
    const inputs = { ...DEFAULT_INPUTS, voice: 'voice:anya', referencePath: 'voices/a.wav', voiceModelPath: 'v.pth' };
    const input = jobInput({ scope: 'p1', sessionId: 's1', thread: thread('t1'), state, fields: {}, inputs, reference: null, text: '', piece: null }, 'q1');
    expect(input).toMatchObject({ voice: 'voice:anya', referencePath: null, voiceModelPath: null });
  });

  it('отказ «клон протух» уходит в панель с котировкой пересоздания', async () => {
    const state = resolvePanel(thread('t1', { mode: 'voice', operation: 'speak', ...BASE }), NO_PREFS, null, 'voice');
    const recreate = { quoteId: 'rq', price: { amount: 0.6, unit: 'usd', approx: false }, recreateVoice: 'anya' } as AudioQuote;
    vi.spyOn(audioApi, 'quote').mockResolvedValue({ quoteId: 'q1' } as AudioQuote);
    vi.spyOn(audioApi, 'startJob').mockRejectedValue(Object.assign(new Error('протух'), {
      status: 409, body: { code: 'voice_clone_stale', error: 'Клон MiniMax удалён', recreate },
    }));
    const got = vi.fn();
    const ok = await runPanel({
      scope: 'p1', sessionId: 's1', thread: thread('t1'), state, fields: {}, inputs: { ...DEFAULT_INPUTS, voice: 'voice:anya' },
      reference: null, text: 'Привет', piece: null,
    }, got);
    expect(ok).toBe(false);
    expect(got).toHaveBeenCalledWith({ message: 'Клон MiniMax удалён', slug: 'anya', quote: recreate });
  });
});

describe('склейка', () => {
  it('стыки — по новой длине: мест на одно меньше кусков, свои сохраняются', () => {
    const own = { kind: 'pause' as const, seconds: 1 };
    expect(jointsFor(4, [])).toEqual([null, null, null]);
    expect(jointsFor(3, [own])).toEqual([own, null]);
    expect(jointsFor(1, [own])).toEqual([]);
  });
});
