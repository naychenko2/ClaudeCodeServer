// Агент не двигает панель (ADR-023, 2к-2; «Панель следует за выбором», правило 2): фокус нити, пришедший
// событием с сервера (image_focus), только заводит подсказку «Claude взял в работу» — панель «Контекст» не
// показывается и не переключается. Ход человека (клик по карточке) идёт другой дорогой.
import { beforeEach, describe, expect, it, vi } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); }, clear: () => store.clear(), key: () => null, length: 0,
} as Storage;
const revealed: Event[] = [];
vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1440, innerHeight: 900 }));
window.addEventListener('cc-reveal-panel', e => revealed.push(e));

import { __resetAgentPicks, getAgentPick } from '../../../lib/genPanelFollow';
import { __applyThreads, __resetThreadStore, subscribeThreadStore } from '../thread/threadStore';
import { threadsApi, type ImageThread, type ImageThreadChangedEvent } from '../thread/threadsApi';

const thread = (id: string): ImageThread => ({
  id, file: `images/${id}.png`, lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: '2026-10-03T10:00:00Z',
} as unknown as ImageThread);

let emit: (e: ImageThreadChangedEvent) => void;

beforeEach(() => {
  revealed.length = 0;
  __resetThreadStore();
  __resetAgentPicks();
  vi.spyOn(threadsApi, 'subscribe').mockImplementation(handler => { emit = handler; return () => {}; });
  subscribeThreadStore(() => {});
  __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [thread('cat')] });
});

describe('фокус агента', () => {
  it('заводит подсказку и не показывает панель', () => {
    emit({ type: 'image_thread_changed', sessionId: 's1', projectId: 'p1', state: { focus: 'cat', revision: 2, threads: [thread('cat')] } } as ImageThreadChangedEvent);
    expect(getAgentPick('s1')?.target).toContain('cat');
    expect(revealed).toHaveLength(0);
  });

  it('снятие фокуса агентом убирает подсказку и тоже не двигает панель', () => {
    emit({ type: 'image_thread_changed', sessionId: 's1', projectId: 'p1', state: { focus: 'cat', revision: 2, threads: [thread('cat')] } } as ImageThreadChangedEvent);
    emit({ type: 'image_thread_changed', sessionId: 's1', projectId: 'p1', state: { focus: null, revision: 3, threads: [thread('cat')] } } as ImageThreadChangedEvent);
    expect(getAgentPick('s1')).toBeNull();
    expect(revealed).toHaveLength(0);
  });
});
