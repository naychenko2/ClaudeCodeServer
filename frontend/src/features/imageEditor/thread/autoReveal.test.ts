import { beforeEach, describe, expect, it, vi } from 'vitest';

// Автооткрытие панели «Картинки» (решения Андрея по v4, 2): выбор картинки человеком
// открывает панель, пока её не закрыли в этом чате; выбор агента (image_focus) — нет.
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
const { FLAGS, setFlagLocal } = await import('../../../lib/featureFlags');
const { markGenPanelDismissed } = await import('../../../lib/genPanelDismissed');
const { __applyThreads, __resetThreadStore } = await import('./threadStore');
const { threadsApi } = await import('./threadsApi');
const { continueFrom, createDraft, workWith, workWithFile } = await import('./actions');

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
  setFlagLocal(FLAGS.imageEditorPanel, true);
  for (const m of ['focus', 'current', 'create'] as const) vi.spyOn(threadsApi, m).mockResolvedValue(focused(2));
});

describe('автооткрытие панели «Картинки»', () => {
  it('выбор человеком: «Работать с этой», версия в ленте, «Редактировать» из дерева, «Нарисовать новую»', async () => {
    await workWith('p1', 's1', 't1');
    await continueFrom('p1', 's1', thread, 'v1');
    await workWithFile('p1', 's1', 'images/hero.png');
    await createDraft('p1', 's1', '');
    expect(reveals).toEqual(['images', 'images', 'images', 'images']);
  });

  it('выбор агента (image_focus приходит с сервера в стор) панель не открывает', () => {
    __applyThreads('s1', 'p1', focused(2));
    expect(reveals).toEqual([]);
  });

  it('человек закрыл панель в этом чате — выбор её больше не открывает, в соседнем чате открывает', async () => {
    markGenPanelDismissed('s1', 'images');
    await workWith('p1', 's1', 't1');
    expect(reveals).toEqual([]);
    __applyThreads('s2', 'p1', { focus: null, revision: 1, threads: [thread] });
    await workWith('p1', 's2', 't1');
    expect(reveals).toEqual(['images']);
  });

  it('снятие выбора и неудачная мутация панель не открывают', async () => {
    await workWith('p1', 's1', null);
    vi.spyOn(threadsApi, 'focus').mockRejectedValue(new Error('сеть'));
    await workWith('p1', 's1', 't1');
    expect(reveals).toEqual([]);
  });

  it('без флага image-editor-panel — как раньше: панель не открывается', async () => {
    setFlagLocal(FLAGS.imageEditorPanel, false);
    await workWith('p1', 's1', 't1');
    await createDraft('p1', 's1', '');
    expect(reveals).toEqual([]);
  });
});
