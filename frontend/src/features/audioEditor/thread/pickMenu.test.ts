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
  audioApi, type AudioCatalog, type AudioModelInfo, type AudioPrefs, type AudioThread, type AudioThreadSettings,
} from '../api';
import {
  __resetSoundSettings, processThreadByHuman, releaseFocus, setSoundMode, soundPanelState, soundReleaseUndo, startConcat, undoSoundRelease,
} from './actions';
import { getSoundMode } from './modeState';
import { soundPickRows } from './pickMenu';
import {
  __applyThreads, __resetAudioStore, __setScopeData, focusThread, getFocusedThread, getPrefs, getThreadsState, handleEvent,
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


// Нить с версией, сделанной запуском: кто запускал и когда
const made = (id: string, at: string, initiator: 'human' | 'agent' | null): AudioThread => ({
  ...withSound(id),
  versions: [{ id: 'v1', number: 1, jobId: initiator ? `job-${id}` : null, variant: 1, baseVersionId: null, files: [], license: null, createdAt: at }],
  currentVersionId: 'v1',
  launches: initiator ? [{ jobId: `job-${id}`, baseVersionId: null, at, status: 'done', initiator, prompt: null, license: null }] : [],
});
const ago = (iso: string) => `в ${iso.slice(11, 16)}`;

describe('строки меню «Что обработать?»', () => {
  it('звуки с версией, свежие сверху, автор и время; черновик и звук в работе — мимо', () => {
    const rows = soundPickRows([
      made('old', '2026-10-02T08:00:00.000Z', 'human'),
      draft('d'),
      made('new', '2026-10-02T09:30:00.000Z', 'agent'),
      made('cur', '2026-10-02T10:00:00.000Z', 'human'),
    ], ago, 'cur');
    expect(rows.map(r => r.id)).toEqual(['new', 'old']);
    expect(rows[0]).toMatchObject({ name: 'new.mp3 · версия 1', sub: 'агент · в 09:30' });
    expect(rows[1].sub).toBe('вы · в 08:00');
  });

  it('прикреплённый файл без запуска — без автора, только время', () => {
    expect(soundPickRows([made('f', '2026-10-02T07:15:00.000Z', null)], ago)[0].sub).toBe('в 07:15');
  });

  it('пустое меню: звуков нет или одни черновики', () => {
    expect(soundPickRows([], ago)).toEqual([]);
    expect(soundPickRows([draft('d1'), draft('d2', S('music', 'song'))], ago)).toEqual([]);
  });
});

describe('выбор из меню, склейка и «Вернуть»', () => {
  it('строка меню: сначала звук в работу, потом «Обработка» — уже на нити', async () => {
    const { settings } = fakeServer();
    const order: string[] = [];
    vi.mocked(audioApi.focus).mockImplementation(async (_s, sid, threadId, rev) => {
      order.push(`focus:${threadId}`);
      return { ...getThreadsState(sid), focus: threadId, revision: rev + 1 };
    });
    settings.mockImplementation(async (_s, sid, threadId, next, rev) => {
      order.push(`settings:${threadId}:${next.mode}`);
      const st = getThreadsState(sid);
      return { ...st, revision: rev + 1, threads: st.threads.map(t => (t.id === threadId ? { ...t, settings: next } : t)) };
    });
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [withSound('a', S('music', 'song'))] });
    setSoundMode('p1', 's1', 'music');
    expect(await processThreadByHuman('p1', 's1', 'a')).toBe(true);
    await tick();
    expect(order).toEqual(['focus:a', 'settings:a:process']);
    expect(getSoundMode('p1', 's1')).toBe('process');
  });

  it('звук не взят в работу — режим не трогаем', async () => {
    fakeServer();
    vi.mocked(audioApi.focus).mockRejectedValue(new Error('нет'));
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [withSound('a', S('music', 'song'))] });
    setSoundMode('p1', 's1', 'music');
    expect(await processThreadByHuman('p1', 's1', 'a')).toBe(false);
    expect(getSoundMode('p1', 's1')).toBe('music');
  });

  it('«Склеить несколько…»: «Обработка» и «Склеить» без звука, черновик снимается', async () => {
    fakeServer();
    __applyThreads('s1', 'p1', { focus: 'd', revision: 1, threads: [draft('d', S('voice', 'speak'))] });
    expect(await startConcat('p1', 's1')).toBe(true);
    expect(getFocusedThread('s1')).toBeNull();
    expect(soundPanelState('p1', 's1')).toMatchObject({ mode: 'process', op: 'concat' });
  });

  it('человек снял звук в «Обработке» — плашка «Вернуть», и она возвращает звук и «Обработку»', async () => {
    fakeServer();
    __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [withSound('a', S('music', 'song'))] });
    setSoundMode('p1', 's1', 'music');
    await tick();
    await processThreadByHuman('p1', 's1', 'a');
    await tick();
    await releaseFocus('p1', 's1', getFocusedThread('s1'));
    expect(getSoundMode('p1', 's1')).toBe('music');
    expect(soundReleaseUndo.current()).toMatchObject({ text: 'Звук снят — вернулись к «Музыке»', snapshot: { sessionId: 's1', threadId: 'a' } });
    expect(await undoSoundRelease()).toBe(true);
    await tick();
    expect(soundReleaseUndo.current()).toBeNull();
    expect(getFocusedThread('s1')?.id).toBe('a');
    expect(getSoundMode('p1', 's1')).toBe('process');
  });

  it('снятие не в «Обработке» и снятие агентом плашки не дают', async () => {
    fakeServer();
    soundReleaseUndo.dismiss();
    __applyThreads('s1', 'p1', { focus: 'a', revision: 1, threads: [withSound('a', S('music', 'song')), withSound('b', S('process', 'denoise'))] });
    await releaseFocus('p1', 's1', getFocusedThread('s1'));
    expect(soundReleaseUndo.current()).toBeNull();
    await focusThread('p1', 's1', 'b');
    expect(getSoundMode('p1', 's1')).toBe('process');
    const st = getThreadsState('s1');
    handleEvent({ type: 'audio_thread_changed', scopeKey: 'p1', sessionId: 's1', revision: st.revision + 1, state: { ...st, focus: null, revision: st.revision + 1 } });
    expect(soundReleaseUndo.current()).toBeNull();
  });
});
