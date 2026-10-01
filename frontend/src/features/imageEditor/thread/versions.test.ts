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
// Выбор картинки человеком открывает панель «Картинки» событием на window
(globalThis as unknown as { window: EventTarget }).window = new EventTarget();
import { imageEditorApi } from '../api';
import { activeStepOf, applyStep, continueFrom, saveAsInThread, saveToProject, versionSaved } from './actions';
import { downloadName, stackSaveState, versionPrimary } from './model';
import { __applyThreads, __resetThreadStore } from './threadStore';
import { threadsApi, type ImageThread, type ImageThreadsState, type ImageThreadVersion } from './threadsApi';

// Карточки версий в ленте (изменение 27.09 к ADR-019): какая кнопка у карточки, откуда
// следующая правка, куда ложится правка без ИИ — у новой нити и у старой со стопкой

const ver = (id: string, patch: Partial<ImageThreadVersion> = {}): ImageThreadVersion => ({
  id, number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null,
  steps: [], currentStepId: null, createdAt: '2026-09-27T00:00:00Z', ...patch,
});

const withVersions = (patch: Partial<ImageThread> = {}): ImageThread => ({
  id: 't1', file: 'img/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-09-27T00:00:00Z',
  versions: [
    ver('origin'),
    ver('v1', { number: 1, jobId: 'j1', variant: 0, baseVersionId: 'origin', steps: ['s1'], currentStepId: 's1' }),
    ver('v2', { number: 2, jobId: 'j1', variant: 1, baseVersionId: 'origin', steps: ['s2', 's2r'], currentStepId: 's2r' }),
  ],
  currentVersionId: 'v2',
  launches: [],
  ...patch,
});

const legacy = (): ImageThread => ({
  id: 't1', file: 'img/hero.png', lineage: [], draftFolder: null,
  stacks: [{ stackId: 'k1', steps: ['a', 'b'], forkedFromStepId: null, old: false }],
  currentStackId: 'k1', currentStepId: 'b', settings: null, pendingJobId: null, createdAt: '2026-09-27T00:00:00Z',
});

const state = (t: ImageThread, revision = 3): ImageThreadsState => ({ focus: t.id, revision, threads: [t] });

beforeEach(() => {
  store.clear();
  __resetThreadStore();
  vi.restoreAllMocks();
});

describe('главная кнопка карточки версии', () => {
  const t = withVersions();
  it('у версии в работе — «Сохранить в проект», пока она черновик', () => {
    expect(versionPrimary(t, t.versions![2], true, false)).toBe('save');
    expect(versionPrimary(t, t.versions![2], true, true)).toBeNull();
  });
  it('у прочих версий выбранной картинки — «Продолжить от неё»', () => {
    expect(versionPrimary(t, t.versions![1], true, false)).toBe('continue');
    expect(versionPrimary(t, t.versions![0], true, true)).toBe('continue');
  });
  it('картинка не в работе — «Работать с этой» на любой версии', () => {
    expect(versionPrimary(t, t.versions![2], false, false)).toBe('work');
  });
  it('личный чат вне проекта: у версии в работе — «Скачать» вместо «Сохранить в проект»', () => {
    const draft = withVersions({ file: null, draftFolder: '' });
    expect(versionPrimary(draft, draft.versions![2], true, false, true)).toBe('download');
    expect(versionPrimary(draft, draft.versions![2], true, false, false)).toBe('save');
    // Прочие кнопки — как у проекта; у пустой версии скачивать нечего
    expect(versionPrimary(draft, draft.versions![1], true, false, true)).toBe('continue');
    expect(versionPrimary(draft, draft.versions![2], false, false, true)).toBe('work');
    const empty = withVersions({ file: null, draftFolder: '', versions: [ver('origin')], currentVersionId: 'origin' });
    expect(versionPrimary(empty, empty.versions![0], true, false, true)).toBeNull();
  });
  it('имя скачанного черновика — image-v{N} с расширением по типу, у файла проекта — его имя', () => {
    const draft = withVersions({ file: null, draftFolder: '' });
    expect(downloadName(draft, draft.versions![2], 'image/png')).toBe('image-v2.png');
    expect(downloadName(draft, draft.versions![1], 'image/jpeg')).toBe('image-v1.jpg');
    expect(downloadName(draft, draft.versions![1], 'image/webp')).toBe('image-v1.webp');
    expect(downloadName(draft, draft.versions![1], '')).toBe('image-v1.png');
    expect(downloadName(t, t.versions![2], 'image/png')).toBe('hero.png');
  });
  it('исходник без правок лежит в проекте файлом, версия ИИ — черновик до сохранения', () => {
    expect(versionSaved(t, t.versions![0])).toBe(true);
    expect(versionSaved(t, t.versions![1])).toBe(false);
  });
});

describe('откуда пойдёт следующая правка', () => {
  it('у нити с версиями — текущий шаг версии в работе', () => {
    expect(activeStepOf(withVersions())).toBe('s2r');
    expect(activeStepOf(withVersions({ currentVersionId: 'v1' }))).toBe('s1');
  });
  it('у старой нити со стопкой — её текущий шаг', () => {
    expect(activeStepOf(legacy())).toBe('b');
  });
});

describe('правка без ИИ и «Продолжить от неё»', () => {
  it('у нити с версиями шаг ложится в текущую версию, «Взять» не зовётся', async () => {
    const t = withVersions();
    __applyThreads('c1', 'p1', state(t));
    const add = vi.spyOn(threadsApi, 'addStep').mockResolvedValue(state(t, 4));
    const take = vi.spyOn(threadsApi, 'take').mockResolvedValue(state(t, 4));
    expect(await applyStep('p1', 'c1', t, 'rot1')).toBe(true);
    expect(add).toHaveBeenCalledWith('p1', 'c1', 't1', 'rot1', 3);
    expect(take).not.toHaveBeenCalled();
  });
  it('у старой нити со стопкой шаг берётся в стопку, как раньше', async () => {
    const t = legacy();
    __applyThreads('c1', 'p1', state(t));
    const add = vi.spyOn(threadsApi, 'addStep').mockResolvedValue(state(t, 4));
    const take = vi.spyOn(threadsApi, 'take').mockResolvedValue(state(t, 4));
    await applyStep('p1', 'c1', t, 'rot1');
    expect(take).toHaveBeenCalledWith('p1', 'c1', 't1', { stepId: 'rot1' }, 3);
    expect(add).not.toHaveBeenCalled();
  });
  it('«Продолжить от неё» делает версию текущей, ничего не удаляя', async () => {
    const t = withVersions();
    __applyThreads('c1', 'p1', state(t));
    const cur = vi.spyOn(threadsApi, 'current').mockResolvedValue(state({ ...t, currentVersionId: 'origin' }, 4));
    const remove = vi.spyOn(threadsApi, 'remove');
    expect(await continueFrom('p1', 'c1', t, 'origin')).toBe(true);
    expect(cur).toHaveBeenCalledWith('p1', 'c1', 't1', 'origin', 3);
    expect(remove).not.toHaveBeenCalled();
  });
});

// Личный чат вне проекта (ревью ИЛ-3): «Сохранить в проект» нет ни в карточке-стопке, ни в
// действиях — иначе клик уходит в проектную ручку и падает тостом «У личного чата нет проекта»
describe('сохранение в личном чате', () => {
  it('карточка-стопка личного чата не предлагает «Сохранить в проект» даже у несохранённого шага', () => {
    expect(stackSaveState(legacy(), true, true, true)).toBeNull();
    expect(stackSaveState(legacy(), true, false, true)).toBeNull();
  });

  it('у чата проекта карточка-стопка сохраняет несохранённый шаг и помечает сохранённый', () => {
    expect(stackSaveState(legacy(), true, true, false)).toBe('save');
    expect(stackSaveState(legacy(), true, false, false)).toBe('in-project');
    expect(stackSaveState(legacy(), false, true, false)).toBeNull();
  });

  it('saveToProject и saveAsInThread в личной области не зовут проектную ручку', async () => {
    const save = vi.spyOn(imageEditorApi(), 'save').mockResolvedValue({ path: 'img/hero-2.png' });
    expect(await saveToProject('personal', 's1', withVersions(), 's2r')).toBeNull();
    await saveAsInThread('personal', 's1', withVersions(), { folder: '', fileName: 'x' }, undefined, 's2r');
    expect(save).not.toHaveBeenCalled();
  });

  it('в проекте saveToProject сохраняет следующей версией', async () => {
    // Тост успеха шлёт событие в window, а окружение тестов — node
    vi.stubGlobal('window', { dispatchEvent: () => true });
    const save = vi.spyOn(imageEditorApi(), 'save').mockResolvedValue({ path: 'img/hero-2.png' });
    expect(await saveToProject('p1', 's1', withVersions(), 's2r')).toBe('img/hero-2.png');
    expect(save).toHaveBeenCalledWith('p1', expect.objectContaining({ stepId: 's2r', mode: 'next-version' }));
    vi.unstubAllGlobals();
  });
});
