import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — localStorage и окна нет; мокаем минимально
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
(globalThis as unknown as { window: unknown }).window = {
  dispatchEvent: () => true, addEventListener: () => {}, removeEventListener: () => {},
};
(globalThis as unknown as { CustomEvent: unknown }).CustomEvent = class { constructor(public type: string, public init: { detail: unknown }) {} };

import { __resetComposerStrips } from '../../../lib/composerStrips';
import {
  audioApi, type AudioCatalog, type AudioModelInfo, type AudioPrefs, type AudioThread, type AudioThreadSettings, type AudioThreadsState,
} from '../api';
import { stripModel } from '../strip/SoundStrip';
import { __resetSoundSettings, changeSoundSettings, flushSoundSettings, releaseFocus, setSoundMode, soundPanelState } from './actions';
import { effectiveMode, getSoundMode } from './modeState';
import {
  __applyThreads, __resetAudioStore, __setScopeData, focusThread, getChosenMode, getFocusedThread, getPrefs, getThreadsState,
  handleEvent,
} from './threadStore';

const model = (id: string, ops: AudioModelInfo['caps']['ops']): AudioModelInfo => ({
  id, label: id.toUpperCase(),
  caps: { ops, languages: ['ru'], voiceKinds: [], producesFiles: ['main'], license: { label: 'MIT', kind: 'permissive' }, priceUnit: 'free' },
});
const CATALOG: AudioCatalog = {
  autoModelId: 'auto', maxCount: 4,
  providers: [
    { key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
      models: [model('qwen', ['speak']), model('ace', ['song']), model('demucs', ['separate'])] },
    { key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null,
      models: [model('mm', ['speak']), model('cassette', ['song'])] },
  ],
};
const NO_PREFS: AudioPrefs = { voice: null, music: null, process: null };
const withSound = (id: string, settings: AudioThreadSettings | null = null): AudioThread => ({
  id, file: `${id}.mp3`, lineage: [], draftFolder: null, createdAt: '', launches: [], settings,
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, files: [], license: null, createdAt: '' }],
  currentVersionId: 'origin',
});
const draft = (id: string, settings: AudioThreadSettings | null = null): AudioThread => ({
  ...withSound(id, settings), file: null, draftFolder: '', versions: [], currentVersionId: null,
});
const S = (mode: AudioThreadSettings['mode'], operation: AudioThreadSettings['operation']): AudioThreadSettings =>
  ({ mode, operation, provider: null, model: null, fields: null });

// Сервер: настройки нити и префы режима принимаются как есть, фокус — тоже
function fakeServer() {
  const settings = vi.spyOn(audioApi, 'settings').mockImplementation(async (_scope, sid, threadId, next, rev) => {
    const st = getThreadsState(sid);
    return { ...st, revision: rev + 1, threads: st.threads.map(t => (t.id === threadId ? { ...t, settings: next } : t)) };
  });
  // Префы сервер держит у себя: ответ — его состояние после записи, а не снимок клиента
  let saved: AudioPrefs = { ...getPrefs('p1') };
  const putPrefs = vi.spyOn(audioApi, 'putPrefs').mockImplementation(async (_scope, _sid, mode, prefs) => (saved = { ...saved, [mode]: prefs }));
  vi.spyOn(audioApi, 'focus').mockImplementation(async (_scope, sid, threadId, rev) => ({ ...getThreadsState(sid), focus: threadId, revision: rev + 1 }));
  return { settings, putPrefs };
}

const tick = () => new Promise(r => setTimeout(r, 0));

beforeEach(() => {
  localStorage.clear();
  __resetAudioStore();
  __resetSoundSettings();
  __resetComposerStrips();
  __setScopeData('p1', CATALOG, NO_PREFS);
});
afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers(); });

describe('effectiveMode', () => {
  it('нить со звуком — её режим, в том числе «Обработка»', () => {
    expect(effectiveMode({ thread: withSound('a', S('process', 'denoise')), chosen: null, lastCreate: 'music' })).toBe('process');
  });

  it('черновик без звука «Обработку» не включает — последний режим создания', () => {
    expect(effectiveMode({ thread: draft('d', S('process', 'separate')), chosen: { mode: 'process', withSound: false }, lastCreate: 'music' })).toBe('music');
    expect(effectiveMode({ thread: draft('d', S('music', 'song')), chosen: null, lastCreate: 'voice' })).toBe('music');
  });

  it('«Обработка», выбранная при звуке, без звука не держится; выбранная без звука (склейка) — держится', () => {
    expect(effectiveMode({ thread: null, chosen: { mode: 'process', withSound: true }, lastCreate: 'music' })).toBe('music');
    expect(effectiveMode({ thread: null, chosen: { mode: 'process', withSound: false }, lastCreate: 'music' })).toBe('process');
  });

  it('ничего не выбрано — «Голос»', () => {
    expect(effectiveMode({ thread: null, chosen: null, lastCreate: null })).toBe('voice');
  });
});

describe('режим в сторе: панель и полоса читают одно', () => {
  it('туда-обратно без нити: каждый режим поднимает свой выбор, полоса видит то же', async () => {
    const { putPrefs } = fakeServer();
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [] });
    changeSoundSettings('p1', 's1', { provider: 'local' });
    changeSoundSettings('p1', 's1', { model: 'qwen' });
    setSoundMode('p1', 's1', 'music');
    changeSoundSettings('p1', 's1', { provider: 'fal' });
    changeSoundSettings('p1', 's1', { model: 'cassette' });
    await tick();
    setSoundMode('p1', 's1', 'voice');
    expect(soundPanelState('p1', 's1')).toMatchObject({ mode: 'voice', providerKey: 'local', modelId: 'qwen' });
    expect(stripModel('p1', 's1', null).launch).toMatchObject({ mode: 'voice', op: 'speak' });
    setSoundMode('p1', 's1', 'music');
    await tick();
    expect(soundPanelState('p1', 's1')).toMatchObject({ mode: 'music', providerKey: 'fal', modelId: 'cassette' });
    expect(getPrefs('p1').voice).toMatchObject({ provider: 'local', model: 'qwen' });
    expect(putPrefs).not.toHaveBeenCalledWith('p1', 's1', 'voice', expect.objectContaining({ provider: null }));
  });

  it('туда-обратно с нитью: уходящий режим — в его префы, нить держит текущий', async () => {
    const { settings } = fakeServer();
    __applyThreads('s1', 'p1', { focus: 'a', revision: 1, threads: [withSound('a', S('voice', 'speak'))] });
    changeSoundSettings('p1', 's1', { provider: 'local' });
    await tick();
    changeSoundSettings('p1', 's1', { model: 'qwen' });
    await tick();
    setSoundMode('p1', 's1', 'music');
    await tick();
    changeSoundSettings('p1', 's1', { provider: 'fal' });
    await tick();
    setSoundMode('p1', 's1', 'voice');
    await tick();
    expect(getFocusedThread('s1')!.settings).toMatchObject({ mode: 'voice', provider: 'local', model: 'qwen' });
    setSoundMode('p1', 's1', 'music');
    await tick();
    expect(soundPanelState('p1', 's1')).toMatchObject({ mode: 'music', providerKey: 'fal' });
    expect(settings).toHaveBeenCalled();
  });

  it('выбор виден полосе сразу, до ответа сервера', () => {
    vi.spyOn(audioApi, 'putPrefs').mockReturnValue(new Promise(() => {}));
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [] });
    setSoundMode('p1', 's1', 'music');
    expect(stripModel('p1', 's1', null).launch.mode).toBe('music');
    changeSoundSettings('p1', 's1', { provider: 'fal' });
    expect(stripModel('p1', 's1', null).launch.provider?.key).toBe('fal');
  });

  it('черновик без звука: «Обработка» не включается', async () => {
    const { settings } = fakeServer();
    __applyThreads('s1', 'p1', { focus: 'd', revision: 1, threads: [draft('d', S('voice', 'speak'))] });
    expect(setSoundMode('p1', 's1', 'process')).toBe(false);
    await tick();
    expect(getSoundMode('p1', 's1')).toBe('voice');
    expect(settings).not.toHaveBeenCalled();
  });

  it.each(['человек', 'агент'])('снятие выбора в «Обработке» (%s) → последний режим создания', async who => {
    fakeServer();
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [withSound('a', S('music', 'song'))] });
    setSoundMode('p1', 's1', 'music');
    await tick();
    await focusThread('p1', 's1', 'a');
    setSoundMode('p1', 's1', 'process');
    await tick();
    expect(getSoundMode('p1', 's1')).toBe('process');
    const chosen = getChosenMode('s1');
    if (who === 'человек') await releaseFocus('p1', 's1', getFocusedThread('s1'));
    else {
      const st = getThreadsState('s1');
      handleEvent({ type: 'audio_thread_changed', scopeKey: 'p1', sessionId: 's1', revision: st.revision + 1, state: { ...st, focus: null, revision: st.revision + 1 } });
    }
    expect(getSoundMode('p1', 's1')).toBe('music');
    expect(stripModel('p1', 's1', null).launch.mode).toBe('music');
    // Выбор человека снятие не трогает: режим выведен, а не переписан
    expect(getChosenMode('s1')).toEqual(chosen);
  });

  it('агент, выбрав нить, выбор человека не меняет', () => {
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [withSound('a', S('process', 'denoise'))] });
    vi.spyOn(audioApi, 'putPrefs').mockReturnValue(new Promise(() => {}));
    setSoundMode('p1', 's1', 'music');
    const st: AudioThreadsState = { ...getThreadsState('s1'), focus: 'a', revision: 2 };
    handleEvent({ type: 'audio_thread_changed', scopeKey: 'p1', sessionId: 's1', revision: 2, state: st });
    expect(getSoundMode('p1', 's1')).toBe('process');
    expect(getChosenMode('s1')).toEqual({ mode: 'music', withSound: false });
  });

  it('отложенная правка уходит в свою нить при смене звука', async () => {
    vi.useFakeTimers();
    const { settings } = fakeServer();
    __applyThreads('s1', 'p1', { focus: 'a', revision: 1, threads: [withSound('a', S('voice', 'speak')), withSound('b', S('voice', 'speak'))] });
    changeSoundSettings('p1', 's1', { fields: { speaker: 'Eric' } }, true);
    expect(settings).not.toHaveBeenCalled();
    __applyThreads('s1', 'p1', { ...getThreadsState('s1'), focus: 'b', revision: 2 });
    changeSoundSettings('p1', 's1', { provider: 'local' });
    expect(settings.mock.calls.map(c => [c[2], c[3].fields, c[3].provider])).toEqual([['a', { speaker: 'Eric' }, null], ['b', {}, 'local']]);
    flushSoundSettings('s1');
    expect(settings).toHaveBeenCalledTimes(2);
  });
});
