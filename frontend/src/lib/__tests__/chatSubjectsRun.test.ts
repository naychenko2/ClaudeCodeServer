import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ProjectSubject, Session, SubjectRunState } from '../../types';

// Хуки стора без рендера: эффект выполняется сразу, снимок читается напрямую
vi.mock('react', async orig => ({
  ...(await orig<typeof import('react')>()),
  useEffect: (fn: () => void) => { fn(); },
  useSyncExternalStore: (_sub: unknown, get: () => unknown) => get(),
}));

const runState = vi.fn<(projectId: string) => Promise<SubjectRunState>>();
const run = vi.fn<(projectId: string, resetAi?: boolean) => Promise<SubjectRunState>>();
vi.mock('../api', () => ({ api: { projects: { subjects: { runState: (p: string) => runState(p), run: (p: string, r?: boolean) => run(p, r) } } } }));
const showToast = vi.fn();
vi.mock('../toast', () => ({ showToast: (...a: unknown[]) => showToast(...a) }));

const { useSubjectRun, startSubjectRun, onSubjectRunFinished, needsRegistryReload, overlayPendingMoves, prunePendingMoves, takeFreshPoll } = await import('../chatSubjects');
type PendingSubjectMove = import('../chatSubjects').PendingSubjectMove;

const st = (state: SubjectRunState['state'], finishedAt: string | null = null, done = 0): SubjectRunState =>
  ({ state, done, total: 3, lastError: null, finishedAt });

// Ручной промис: ответ сервера приходит тогда, когда скажет тест
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}
const flush = () => new Promise(r => setTimeout(r, 0));

let n = 0;
const freshProject = () => `p${++n}`;

beforeEach(() => {
  runState.mockReset();
  run.mockReset();
  showToast.mockReset();
});

describe('стор разбора тем', () => {
  it('конец прохода из опроса (running → idle) зовёт подписчика перечитывания ровно раз', async () => {
    const pid = freshProject();
    vi.useFakeTimers();
    try {
      runState.mockResolvedValueOnce(st('idle'));
      useSubjectRun(pid, true);
      await vi.advanceTimersByTimeAsync(0);
      const finished = vi.fn();
      const off = onSubjectRunFinished(finished);
      run.mockResolvedValueOnce(st('running'));
      await startSubjectRun(pid);
      runState.mockResolvedValueOnce(st('idle', '2026-10-10T15:00:00Z', 3));
      await vi.advanceTimersByTimeAsync(2000);
      expect(finished).toHaveBeenCalledTimes(1);
      expect(finished).toHaveBeenCalledWith(pid);
      expect(useSubjectRun(pid, false).running).toBe(false);
      off();
    } finally { vi.useRealTimers(); }
  });

  it('устаревший GET, ушедший до POST, не перетирает running: ни тоста, ни конца прохода', async () => {
    const pid = freshProject();
    const firstGet = deferred<SubjectRunState>();
    runState.mockReturnValueOnce(firstGet.promise);
    useSubjectRun(pid, true); // GET ушёл и висит
    const finished = vi.fn();
    const off = onSubjectRunFinished(finished);
    run.mockResolvedValueOnce(st('running'));
    await startSubjectRun(pid);
    expect(useSubjectRun(pid, false).running).toBe(true);
    // Ответ GET приходит позже POST и говорит idle прошлого прохода — это снимок до запуска.
    // До POST стор его не видел, отсечь его может только поколение запроса
    firstGet.resolve(st('idle', '2026-10-10T09:00:00Z'));
    await flush();
    expect(useSubjectRun(pid, false).running).toBe(true);
    expect(finished).not.toHaveBeenCalled();
    expect(showToast).not.toHaveBeenCalled();
    off();
  });

  it('GET, ушедший между POST и его ответом, с idle до запуска — не конец нашего прохода', async () => {
    const pid = freshProject();
    vi.useFakeTimers();
    try {
      const finished = vi.fn();
      const off = onSubjectRunFinished(finished);
      const post = deferred<SubjectRunState>();
      run.mockReturnValueOnce(post.promise);
      const started = startSubjectRun(pid);
      // Пока POST в полёте, монтируется первый вход и шлёт GET (поколение уже новое)
      const midGet = deferred<SubjectRunState>();
      runState.mockReturnValueOnce(midGet.promise);
      useSubjectRun(pid, true);
      post.resolve(st('running'));
      await started;
      // Ответ GET приходит после POST: idle без конца — снимок до запуска
      midGet.resolve(st('idle', null));
      await vi.advanceTimersByTimeAsync(0);
      expect(useSubjectRun(pid, false).running).toBe(true);
      expect(finished).not.toHaveBeenCalled();
      // Опрос продолжается и ловит настоящий конец
      runState.mockResolvedValueOnce(st('idle', '2026-10-10T10:05:00Z', 2));
      await vi.advanceTimersByTimeAsync(2000);
      expect(finished).toHaveBeenCalledTimes(1);
      off();
    } finally { vi.useRealTimers(); }
  });

  it('после рестарта бэка (idle без finishedAt) опрос снимает полосу, а не висит', async () => {
    const pid = freshProject();
    vi.useFakeTimers();
    try {
      runState.mockResolvedValueOnce(st('idle', '2026-10-10T10:00:00Z'));
      useSubjectRun(pid, true);
      await vi.advanceTimersByTimeAsync(0);
      const finished = vi.fn();
      const off = onSubjectRunFinished(finished);
      run.mockResolvedValueOnce(st('running'));
      await startSubjectRun(pid);
      expect(useSubjectRun(pid, false).running).toBe(true);
      // Бэк перезапустился: прохода нет, состояние в памяти пустое
      runState.mockResolvedValueOnce(st('idle', null));
      await vi.advanceTimersByTimeAsync(2000);
      expect(useSubjectRun(pid, false).running).toBe(false);
      expect(finished).toHaveBeenCalledTimes(1);
      // Проход умер посреди дела — «Разбор прерван», а не «Темы разложены · 0»
      expect(showToast).toHaveBeenCalledTimes(1);
      expect(showToast.mock.calls[0][0]).toBe('Разбор прерван');
      off();
    } finally { vi.useRealTimers(); }
  });

  it('штатный конец прохода — тост «Темы разложены» с числом чатов', async () => {
    const pid = freshProject();
    vi.useFakeTimers();
    try {
      runState.mockResolvedValueOnce(st('idle'));
      useSubjectRun(pid, true);
      await vi.advanceTimersByTimeAsync(0);
      run.mockResolvedValueOnce(st('running'));
      await startSubjectRun(pid);
      runState.mockResolvedValueOnce(st('idle', '2026-10-10T15:00:00Z', 3));
      await vi.advanceTimersByTimeAsync(2000);
      expect(showToast).toHaveBeenCalledTimes(1);
      expect(showToast.mock.calls[0][0]).toBe('Темы разложены');
    } finally { vi.useRealTimers(); }
  });
});

describe('needsRegistryReload', () => {
  const reg: ProjectSubject[] = [{ id: 't1', name: 'Деплой', order: 0, createdBy: 'ai', createdAt: '2026-10-01T00:00:00Z' }];

  it('неизвестный subjectId — перечитать; тот же висячий id второй раз — нет', () => {
    const tried = new Set<string>();
    const chats = [{ subjectId: 't1' }, { subjectId: 'new' }, { subjectId: null }];
    expect(needsRegistryReload(chats, reg, tried)).toBe(true);
    expect(needsRegistryReload(chats, reg, tried)).toBe(false);
    expect(needsRegistryReload([...chats, { subjectId: 'other' }], reg, tried)).toBe(true);
  });

  it('все темы известны — перечитывать нечего', () => {
    expect(needsRegistryReload([{ subjectId: 't1' }, {}], reg, new Set())).toBe(false);
  });
});

describe('takeFreshPoll', () => {
  it('опрос, ушедший раньше уже применённого, отбрасывается; тот же номер и новее — нет', () => {
    const applied = { current: 0 };
    expect(takeFreshPoll(applied, 2)).toBe(true);
    // Опрос №1 ушёл раньше №2, а вернулся позже — его список старее экрана
    expect(takeFreshPoll(applied, 1)).toBe(false);
    expect(applied.current).toBe(2);
    expect(takeFreshPoll(applied, 2)).toBe(true);
    expect(takeFreshPoll(applied, 3)).toBe(true);
    expect(applied.current).toBe(3);
  });
});

describe('overlayPendingMoves', () => {
  it('перенос в полёте поверх ответа опроса, остальные чаты — те же объекты', () => {
    const a = { id: 'a', subjectId: 'old', subjectSource: 'ai' } as Session;
    const b = { id: 'b', subjectId: 'x' } as Session;
    const out = overlayPendingMoves([a, b], new Map([['a', { patch: { subjectId: 'new', subjectSource: 'human' as const } }]]));
    expect(out[0]).toMatchObject({ id: 'a', subjectId: 'new', subjectSource: 'human' });
    expect(out[1]).toBe(b);
  });

  it('ответивший перенос держится до опроса, ушедшего после ответа PUT', () => {
    const a = { id: 'a', subjectId: 'old', subjectSource: 'ai' } as Session;
    const pending = new Map<string, PendingSubjectMove>([
      ['a', { patch: { subjectId: 'new', subjectSource: 'human' }, settledAt: 4 }],
      ['b', { patch: { subjectId: 'x', subjectSource: 'human' } }],
    ]);
    // Опрос №4 ушёл до ответа PUT и несёт старую тему — перенос остаётся поверх
    prunePendingMoves(pending, 4);
    expect(overlayPendingMoves([a], pending)[0].subjectId).toBe('new');
    // Опрос №5 ушёл после ответа — перенос снят; PUT в полёте (b) не трогается
    prunePendingMoves(pending, 5);
    expect(pending.has('a')).toBe(false);
    expect(pending.has('b')).toBe(true);
  });
});
