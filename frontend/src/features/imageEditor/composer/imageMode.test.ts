import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — localStorage нет; стору полос хватает заглушки
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {}, key: () => null, length: 0,
} as Storage;
// Выбор картинки человеком открывает панель «Картинки» событием на window
(globalThis as unknown as { window: EventTarget }).window = new EventTarget();
import { nextComposerMode, type ComposerModeSeen } from '../../../lib/composerModes';
import { __resetComposerStrips, getComposerStripsVersion } from '../../../lib/composerStrips';
import { createDraft } from '../thread/actions';
import { threadsApi, type ImageThread, type ImageThreadsState } from '../thread/threadsApi';
import { __applyThreads, __resetThreadStore, requestImageMode } from '../thread/threadStore';
import { imageMode } from './imageMode';

// Дефект QA v3-polish #2/#3: фокус картинки пришёл с сервера (агент image_new, перезагрузка,
// другая вкладка), а поле ввода оставалось в «Чате» — без подсказки «Промпт модели» и
// кнопки «Сгенерировать». Правило записки v3: есть выбранная картинка — есть переключатель.
// Жалоба Андрея 29.09: черновик, заведённый агентом посреди разговора, уводил поле в
// «Картинку» — режим включается сам только по явной просьбе человека

const CTX = { projectId: 'p1', sessionId: 's1' };
const MODES = [{ name: 'image', action: imageMode }];

const thread = (patch: Partial<ImageThread>): ImageThread => ({
  id: 't1', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-09-27T00:00:00Z', ...patch,
});
const state = (focus: string | null, threads: ImageThread[], revision = 1): ImageThreadsState =>
  ({ focus, revision, threads });

// Что видит поле ввода: какие режимы в переключателе и какой из них включится
function composer(seen: ComposerModeSeen, modeId: string | null) {
  const available = MODES.filter(m => m.action.isAvailable(CTX));
  return { switcher: available.length > 0, ...nextComposerMode(available, CTX, seen, modeId) };
}

beforeEach(() => {
  __resetThreadStore();
  __resetComposerStrips();
});
afterEach(() => vi.restoreAllMocks());

describe('режим «Картинка» при фокусе с сервера', () => {
  it('черновик, выбранный извне (агент): переключатель есть, режим остаётся «Чат»', () => {
    __applyThreads('s1', 'p1', state('d1', [thread({ id: 'd1', file: null, draftFolder: '' })]));
    const c = composer({}, null);
    expect(c.switcher).toBe(true);
    expect(c.modeId).toBeNull();
  });

  it('«Нарисовать новую» от человека включает режим у черновика', async () => {
    vi.spyOn(threadsApi, 'create').mockResolvedValue(state('d1', [thread({ id: 'd1', file: null, draftFolder: '' })], 2));
    const before = composer({}, null);
    expect(await createDraft('p1', 's1', '')).toBe(true);
    expect(composer(before.seen, null).modeId).toBe('image');
  });

  it('картинка-файл, выбранная извне: переключатель есть, режим остаётся «Чат»', () => {
    __applyThreads('s1', 'p1', state('t1', [thread({})]));
    const c = composer({}, null);
    expect(c.switcher).toBe(true);
    expect(c.modeId).toBeNull();
  });

  it('ручной уход в «Чат» у черновика держится, пока повод тот же', () => {
    __applyThreads('s1', 'p1', state('d1', [thread({ id: 'd1', file: null, draftFolder: '' })]));
    requestImageMode('s1');
    const first = composer({}, null);
    expect(first.modeId).toBe('image');
    // Человек нажал «Чат» — перерисовка с тем же ключом режим не навязывает
    expect(composer(first.seen, null).modeId).toBeNull();
  });

  it('снятый и заново выбранный фокус не навязывает режим по прежнему поводу', () => {
    const d1 = thread({ id: 'd1', file: null, draftFolder: '' });
    __applyThreads('s1', 'p1', state('d1', [thread({}), d1]));
    requestImageMode('s1');
    const first = composer({}, null);
    expect(first.modeId).toBe('image');
    // Человек нажал «Чат», потом ✕ на чипе — режим пропал из переключателя
    __applyThreads('s1', 'p1', state(null, [thread({}), d1], 2));
    const gone = composer(first.seen, null);
    expect(gone.switcher).toBe(false);
    // «Работать с этой» у картинки в ленте — фокус вернулся, повода нового нет
    __applyThreads('s1', 'p1', state('t1', [thread({}), d1], 3));
    const back = composer(gone.seen, null);
    expect(back.switcher).toBe(true);
    expect(back.modeId).toBeNull();
  });

  it('«Редактировать» из дерева включает режим у картинки-файла', () => {
    __applyThreads('s1', 'p1', state('t1', [thread({})]));
    const before = composer({}, null);
    requestImageMode('s1');
    expect(composer(before.seen, null).modeId).toBe('image');
  });

  it('смена фокуса с сервера будит поле ввода, даже если полоса уже запрошена', () => {
    __applyThreads('s1', 'p1', state('t1', [thread({}), thread({ id: 'd1', file: null, draftFolder: '' })]));
    const v = getComposerStripsVersion();
    __applyThreads('s1', 'p1', state('d1', [thread({}), thread({ id: 'd1', file: null, draftFolder: '' })], 2));
    expect(getComposerStripsVersion()).toBeGreaterThan(v);
  });
});

describe('затравка поля промптом последнего запуска', () => {
  it('у нити с запусками — промпт с ключом по нити', () => {
    __applyThreads('s1', 'p1', state('t1', [thread({ launches: [
      { jobId: 'j1', baseVersionId: null, baseStepId: null, at: '2026-09-29T10:00:00Z', status: 'done', initiator: 'agent', prompt: 'закат над морем' },
    ] } as Partial<ImageThread>)]));
    expect(imageMode.prefill!(CTX)).toEqual({ key: 't1', text: 'закат над морем' });
  });

  it('у пустого черновика — ключ нити без текста', () => {
    __applyThreads('s1', 'p1', state('d1', [thread({ id: 'd1', file: null, draftFolder: '' })]));
    expect(imageMode.prefill!(CTX)).toEqual({ key: 'd1', text: null });
  });
});
