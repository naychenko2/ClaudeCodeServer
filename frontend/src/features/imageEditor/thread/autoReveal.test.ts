import { beforeEach, describe, expect, it, vi } from 'vitest';

// Автооткрытие панели «Контекст» (ключ images — упразднённый, переводится в chatContext): выбор картинки
// человеком открывает панель, пока её не закрыли в этом чате; выбор агента (image_focus) — нет.
// Окружение node: localStorage на Map, window — источник события revealWorkspacePanel
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
const storage = new Map<string, string>();
vi.stubGlobal('localStorage', fakeStorage(storage));
const win = Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
});
vi.stubGlobal('window', win);

const { REVEAL_PANEL_EVENT } = await import('../../../lib/subsystems/registryCore');
const { markGenPanelDismissed } = await import('../../../lib/genPanelDismissed');
const { __applyThreads, __resetThreadStore } = await import('./threadStore');
const { threadsApi } = await import('./threadsApi');
const { continueFrom, createDraft, workWithFile } = await import('./actions');

type Thread = Parameters<typeof continueFrom>[2];
const thread = {
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-01T00:00:00Z',
} as unknown as Thread;
const focused = (revision: number) => ({ focus: 't1', revision, threads: [thread] });

let reveals: string[] = [];
win.addEventListener(REVEAL_PANEL_EVENT, e => { reveals.push((e as CustomEvent<{ key: string }>).detail.key); });

beforeEach(() => {
  storage.clear();
  reveals = [];
  vi.restoreAllMocks();
  __resetThreadStore();
  __applyThreads('s1', 'p1', { focus: null, revision: 1, threads: [thread] });
  for (const m of ['focus', 'current', 'create'] as const) vi.spyOn(threadsApi, m).mockResolvedValue(focused(2));
});

describe('автооткрытие панели «Контекст»', () => {
  it('выбор человеком: версия в ленте, «Редактировать» из дерева, «Нарисовать новую»', async () => {
    await continueFrom('p1', 's1', thread, 'v1');
    await workWithFile('p1', 's1', 'images/hero.png');
    await createDraft('p1', 's1', '');
    expect(reveals).toEqual(['chatContext', 'chatContext', 'chatContext']);
  });

  it('выбор агента (image_focus приходит с сервера в стор) панель не открывает', () => {
    __applyThreads('s1', 'p1', focused(2));
    expect(reveals).toEqual([]);
  });

  it('человек закрыл панель в этом чате — «Нарисовать новую» её больше не открывает, «Продолжить от неё» открывает', async () => {
    markGenPanelDismissed('s1', 'images');
    await createDraft('p1', 's1', '');
    expect(reveals).toEqual([]);
    // Кнопка — просьба открыть, а не выбор («Панель следует за выбором», правило 4)
    await continueFrom('p1', 's1', thread, 'v1');
    expect(reveals).toEqual(['chatContext']);
  });

  it('неудачная мутация панель не открывает', async () => {
    vi.spyOn(threadsApi, 'current').mockRejectedValue(new Error('сеть'));
    await continueFrom('p1', 's1', thread, 'v1');
    expect(reveals).toEqual([]);
  });
});
