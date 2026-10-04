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
import { audioApi, type AudioCatalog, type AudioPrefs, type AudioThread, type AudioThreadsState } from '../api';
import {
  __applyThreads, __resetAudioStore, ensureAudioThreads, getCatalog, getFocusedThread, getJobsOf, getPrefs,
  getThreadsState, handleEvent,
} from './threadStore';

const thread = (id: string): AudioThread => ({
  id, file: `${id}.mp3`, lineage: [], draftFolder: null, createdAt: '', versions: [], currentVersionId: null, launches: [], settings: null,
});
const st = (revision: number, focus: string | null = null, threads: AudioThread[] = []): AudioThreadsState => ({ focus, revision, threads });
const CATALOG: AudioCatalog = { providers: [], autoModelId: 'auto', maxCount: 4 };
const PREFS: AudioPrefs = { voice: null, music: null, process: null };

const base = { scopeKey: 'p1', sessionId: 's1' };
const progress = (jobId: string, extra: Partial<{ stage: 'queued' | 'running'; queuePosition: number | null; threadId: string }> = {}) => ({
  ...base, type: 'audio_edit_progress' as const, jobId, stage: extra.stage ?? 'queued', queuePosition: extra.queuePosition ?? 2,
  etaSeconds: 180, variant: 1, count: 1, chatSessionId: 's1', threadId: extra.threadId ?? 't1', initiator: 'Human' as const,
});

beforeEach(() => {
  localStorage.clear();
  __resetAudioStore();
  vi.restoreAllMocks();
});

describe('стор нитей звука: события → состояние', () => {
  it('загрузка берёт нити, каталог и префы одним запросом', async () => {
    vi.spyOn(audioApi, 'state').mockResolvedValue({ threads: st(3, 't1', [thread('t1')]), catalog: CATALOG, prefs: PREFS });
    await ensureAudioThreads('p1', 's1');
    expect(getFocusedThread('s1')?.id).toBe('t1');
    expect(getCatalog('p1')).toEqual(CATALOG);
  });

  it('audio_thread_changed применяется к показанному чату, старая ревизия пропускается', () => {
    __applyThreads('s1', 'p1', st(1));
    handleEvent({ ...base, type: 'audio_thread_changed', revision: 5, state: st(5, 't2', [thread('t2')]) });
    expect(getThreadsState('s1').revision).toBe(5);
    expect(getFocusedThread('s1')?.id).toBe('t2');
    handleEvent({ ...base, type: 'audio_thread_changed', revision: 4, state: st(4) });
    expect(getThreadsState('s1').revision).toBe(5);
  });

  it('событие чужого, ещё не показанного чата не заводит его в сторе', () => {
    handleEvent({ ...base, sessionId: 'other', type: 'audio_thread_changed', revision: 2, state: st(2, 't1', [thread('t1')]) });
    expect(getThreadsState('other').revision).toBe(0);
  });

  it('снятие фокуса событием убирает нить в работе, сами нити остаются', () => {
    __applyThreads('s1', 'p1', st(1, 't1', [thread('t1')]));
    handleEvent({ ...base, type: 'audio_thread_changed', revision: 2, state: st(2, null, [thread('t1')]) });
    expect(getFocusedThread('s1')).toBeNull();
    expect(getThreadsState('s1').threads).toHaveLength(1);
  });

  it('audio_prefs_changed меняет префы только своего режима области', () => {
    const music = { operation: 'song' as const, provider: 'local', model: null, count: 2, fields: null };
    handleEvent({ ...base, type: 'audio_prefs_changed', mode: 'music', prefs: music });
    expect(getPrefs('p1').music).toEqual(music);
    expect(getPrefs('p1').voice).toBeNull();
    expect(getPrefs('personal').music).toBeNull();
  });

  it('progress заводит ход задачи нити, completed и failed его убирают', () => {
    handleEvent(progress('j1'));
    handleEvent(progress('j2', { threadId: 't2' }));
    expect(getJobsOf('s1', 't1').map(j => j.jobId)).toEqual(['j1']);
    expect(getJobsOf('s1', null)).toHaveLength(2);
    handleEvent(progress('j1', { stage: 'running', queuePosition: null }));
    expect(getJobsOf('s1', 't1')[0].stage).toBe('running');
    handleEvent({ ...base, type: 'audio_edit_completed', jobId: 'j1', variants: [1], cost: null, error: null, chatSessionId: 's1', threadId: 't1', initiator: 'Human' });
    expect(getJobsOf('s1', 't1')).toEqual([]);
    handleEvent({
      ...base, type: 'audio_edit_failed', jobId: 'j2', outcome: 'failed', charged: false, error: 'x', retryQuote: null,
      chatSessionId: 's1', threadId: 't2', initiator: 'Human',
    });
    expect(getJobsOf('s1', null)).toEqual([]);
  });
});
