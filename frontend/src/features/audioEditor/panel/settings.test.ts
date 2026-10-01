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
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { audioApi, type AudioPrefs, type AudioThread, type AudioThreadsState } from '../api';
import { __applyThreads, __resetAudioStore, getPrefs, getShortcutMode, getThreadsState } from '../thread/threadStore';
import { DEFAULT_INPUTS, inputsKey, readInputs, saveSettings, writeInputs } from './inputs';
import { ParamField } from './ParamField';
import { dialogueText, jobInput, trimSteps } from './run';
import { resolvePanel } from './model';

const thread = (id: string): AudioThread => ({
  id, file: `${id}.mp3`, lineage: [], draftFolder: null, createdAt: '', versions: [], currentVersionId: 'v1', launches: [], settings: null,
});
const NO_PREFS: AudioPrefs = { voice: null, music: null, process: null };
const SETTINGS = { mode: 'voice' as const, operation: 'speak' as const, provider: 'local', model: 'qwen', fields: { speaker: 'Eric' }, count: 2 };

beforeEach(() => {
  localStorage.clear();
  __resetAudioStore();
  vi.restoreAllMocks();
});

describe('настройки помнятся на нить', () => {
  it('выбранная нить — настройки уходят в неё с текущей ревизией', async () => {
    const t = thread('t1');
    __applyThreads('s1', 'p1', { focus: 't1', revision: 7, threads: [t] });
    const after: AudioThreadsState = { focus: 't1', revision: 8, threads: [{ ...t, settings: SETTINGS }] };
    const put = vi.spyOn(audioApi, 'settings').mockResolvedValue(after);
    const prefs = vi.spyOn(audioApi, 'putPrefs');
    expect(await saveSettings('p1', 's1', t, SETTINGS)).toBe(true);
    expect(put).toHaveBeenCalledWith('p1', 's1', 't1', SETTINGS, 7);
    expect(prefs).not.toHaveBeenCalled();
    expect(getThreadsState('s1').threads[0].settings).toEqual(SETTINGS);
  });

  it('без нити — в префы режима области, режим запоминается за чатом', async () => {
    const saved: AudioPrefs = { ...NO_PREFS, voice: { operation: 'speak', provider: 'local', model: 'qwen', count: 2, fields: { speaker: 'Eric' }, inputs: null } };
    const prefs = vi.spyOn(audioApi, 'putPrefs').mockResolvedValue(saved);
    const put = vi.spyOn(audioApi, 'settings');
    expect(await saveSettings('p1', 's1', null, SETTINGS)).toBe(true);
    expect(prefs).toHaveBeenCalledWith('p1', 's1', 'voice', saved.voice);
    expect(put).not.toHaveBeenCalled();
    expect(getPrefs('p1')).toEqual(saved);
    expect(getShortcutMode('s1')).toBe('voice');
  });

  it('у двух нитей — свои настройки', () => {
    const a = resolvePanel({ ...thread('a'), settings: SETTINGS }, NO_PREFS, null, 'voice');
    const b = resolvePanel({ ...thread('b'), settings: { ...SETTINGS, mode: 'process', operation: 'denoise', fields: null } }, NO_PREFS, null, 'voice');
    expect([a.op, a.fields]).toEqual(['speak', { speaker: 'Eric' }]);
    expect([b.mode, b.op, b.fields]).toEqual(['process', 'denoise', {}]);
  });

  it('входы вне белого списка сервера — в браузере на свою нить, входы из списка — нет', () => {
    const ka = inputsKey('p1', 's1', 'a');
    const kb = inputsKey('p1', 's1', 'b');
    writeInputs(ka, { ...DEFAULT_INPUTS, language: 'ru', voiceModelPath: 'v.pth', trim: { ...DEFAULT_INPUTS.trim, start: 3 } });
    expect(readInputs(ka).language).toBe('');
    expect(readInputs(ka).voiceModelPath).toBe('v.pth');
    expect(readInputs(ka).trim).toEqual({ ...DEFAULT_INPUTS.trim, start: 3 });
    expect(readInputs(kb)).toEqual(DEFAULT_INPUTS);
    expect(inputsKey('p1', 's1', null)).not.toBe(inputsKey('p1', 's2', null));
  });
});

describe('запуск из панели', () => {
  it('обрезка — правки по порядку: кусок, громкость, нормализация', () => {
    expect(trimSteps({ start: 1, end: null, gainDb: -3, fadeIn: 0, fadeOut: 2, normalize: true, format: 'mp3' })).toEqual([
      { op: 'trim', startSec: 1, endSec: null, format: 'mp3' },
      { op: 'gainFade', gainDb: -3, fadeInSec: 0, fadeOutSec: 2, format: 'mp3' },
      { op: 'normalize', format: 'mp3' },
    ]);
    expect(trimSteps(DEFAULT_INPUTS.trim)).toEqual([]);
  });

  it('входы задачи: текст, язык, образец и реплики диалога', () => {
    const state = resolvePanel({ ...thread('t1'), settings: { ...SETTINGS, operation: 'cloneVoice' } }, NO_PREFS, null, 'voice');
    const inputs = { ...DEFAULT_INPUTS, language: 'ru', referencePath: 'voices/a.wav' };
    const input = jobInput({ scope: 'p1', sessionId: 's1', thread: thread('t1'), state, fields: {}, inputs, reference: null, text: ' Привет ' }, 'q1');
    expect(input).toMatchObject({ quoteId: 'q1', threadId: 't1', text: 'Привет', language: 'ru', referencePath: 'voices/a.wav', prompt: null });
    expect(dialogueText({ ...DEFAULT_INPUTS, replicas: [{ voice: 'Аня', text: 'Привет' }, { voice: '', text: 'Пока' }, { voice: 'Б', text: ' ' }] }))
      .toBe('Аня: Привет\nПока');
  });
});

describe('поле автоформы', () => {
  const html = (field: Parameters<typeof ParamField>[0]['field'], value?: unknown) =>
    renderToStaticMarkup(createElement(ParamField, { field, value, onChange: () => {} }));

  it('число с границами — ползунок, enum — список, bool — флажок', () => {
    expect(html({ key: 'expressiveness', type: 'number', min: 0.25, max: 2, default: 0.5 })).toContain('type="range"');
    expect(html({ key: 'speaker', type: 'string', enum: ['Vivian', 'Eric'] })).toContain('<select');
    expect(html({ key: 'loop', type: 'boolean' })).toContain('role="checkbox"');
  });

  it('непередаваемое поле — подпись «Пока не передаётся» и заблокированный контрол', () => {
    const out = html({ key: 'cfg_weight', type: 'number', passed: false, notPassed: 'шов не принимает' });
    expect(out).toContain('Пока не передаётся: шов не принимает');
    expect(out).toContain('disabled');
    expect(html({ key: 'cfg', type: 'number' })).not.toContain('Пока не передаётся');
  });
});
