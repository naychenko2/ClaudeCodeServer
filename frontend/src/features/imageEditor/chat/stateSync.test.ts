import { describe, expect, it, vi } from 'vitest';
import type { ImageChatState, ImageEditCatalog } from '../api';
import {
  applyAgentChanges, changedLine, describeChanges, NO_AGENT_MARKS, settingsToState, type EditorSettings,
} from './stateSync';
import { writeState } from './useChatStateSync';

const STATE: ImageChatState = {
  prompt: '', promptAuthor: 'human', provider: null, model: 'auto', mode: 'auto', count: 3,
  references: [], characterSlug: null, marks: null, canvasRevision: null, lastSentRevision: null,
  currentStepId: null, matchSourceSize: true, events: [], revision: 4,
};

const SETTINGS: EditorSettings = {
  prompt: 'закат', promptAuthor: 'human', provider: 'settings', model: 'auto', count: 3, references: [],
  characterSlug: null, matchSourceSize: true, marks: null, canvasRevision: null, currentStepId: null,
};

const CATALOG: ImageEditCatalog = {
  default: { provider: 'fal', model: 'auto' },
  providers: [{ key: 'fal', label: 'fal.ai', priceUnit: 'usd', models: [
    { id: 'auto', label: 'Авто' }, { id: 'fal-ai/flux-pro/v1/fill', label: 'FLUX Fill' },
  ] }],
  limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 },
  reason: null,
};

describe('применение image_chat_state агента', () => {
  it('ставит промпт, модель и число, помечает всё, что агент поменял', () => {
    const state = { ...STATE, prompt: 'вечер, без лампы', promptAuthor: 'agent' as const, provider: 'fal', model: 'fal-ai/flux-pro/v1/fill', count: 2 };
    const r = applyAgentChanges({ prompt: '', promptAuthor: 'human' }, NO_AGENT_MARKS, state, [
      { field: 'prompt' }, { field: 'provider' }, { field: 'model' }, { field: 'count' },
    ]);
    expect(r.patch).toMatchObject({ prompt: 'вечер, без лампы', promptAuthor: 'agent', provider: 'fal', model: 'fal-ai/flux-pro/v1/fill', count: 2 });
    expect(r.marks).toEqual({ prompt: true, model: true, count: true });
    expect(r.draft).toBeNull();
  });

  it('метка появляется и без смены промпта — агент поменял только число вариантов', () => {
    const r = applyAgentChanges({ prompt: 'мой', promptAuthor: 'human' }, NO_AGENT_MARKS, { ...STATE, count: 1 }, [{ field: 'count' }]);
    expect(r.patch).toEqual({ count: 1 });
    expect(r.marks).toEqual({ prompt: false, model: false, count: true });
  });

  it('текст человека, заменённый агентом, уходит в черновик «Вернуть мой текст»', () => {
    const r = applyAgentChanges({ prompt: 'мой набросок', promptAuthor: 'human' }, NO_AGENT_MARKS,
      { ...STATE, prompt: 'промпт агента' }, [{ field: 'prompt' }]);
    expect(r.draft).toBe('мой набросок');
  });

  it('промпт агента поверх промпта агента черновика не даёт', () => {
    const r = applyAgentChanges({ prompt: 'старый от агента', promptAuthor: 'agent' }, NO_AGENT_MARKS,
      { ...STATE, prompt: 'новый от агента' }, [{ field: 'prompt' }]);
    expect(r.draft).toBeNull();
  });
});

describe('строка «Изменил: …»', () => {
  it('называет модель по каталогу и число вариантов', () => {
    const parts = describeChanges([
      { field: 'prompt', from: '', to: 'вечер' },
      { field: 'model', from: 'auto', to: 'fal-ai/flux-pro/v1/fill' },
      { field: 'count', from: 3, to: 2 },
      { field: 'promptAuthor', from: 'human', to: 'agent' },
    ], CATALOG);
    expect(changedLine(parts, false)).toBe('Изменил: промпт, модель → FLUX Fill, вариантов: 2');
  });

  it('«как в настройках» → тот же поставщик изменением не считается', () => {
    expect(describeChanges([{ field: 'provider', from: null, to: 'fal' }], CATALOG)).toEqual([]);
  });

  it('у персоны — безличная форма; без изменений строки нет', () => {
    expect(changedLine(['вариантов: 2'], true)).toBe('Изменено: вариантов: 2');
    expect(changedLine([], false)).toBeNull();
  });
});

describe('запись состояния на сервер', () => {
  it('на 409 перечитывает и пишет своё поверх свежей ревизии, не затирая правку агента', async () => {
    let base: ImageChatState | null = STATE;
    const agentWrote = { ...STATE, count: 1, revision: 5 };
    const put = vi.fn()
      .mockRejectedValueOnce(Object.assign(new Error('Состояние уже поменялось'), { status: 409, body: { state: agentWrote } }))
      .mockImplementation(async (_p: string, _s: string, st: ImageChatState) => ({ ...st, revision: st.revision + 1 }));
    const onRemote = vi.fn();
    await writeState({
      api: { putChatState: put, getChatState: vi.fn() }, projectId: 'p', sessionId: 's', settings: SETTINGS,
      maskFor: async () => null, base: () => base, setBase: st => { base = st; },
      adopt: st => { base = st; }, onRemote,
    });
    expect(put).toHaveBeenCalledTimes(2);
    const second = put.mock.calls[1][2] as ImageChatState;
    expect(second.revision).toBe(5);
    expect(second.prompt).toBe('закат');
    expect(second.count).toBe(1);
    expect(onRemote).toHaveBeenCalledWith(agentWrote, [{ field: 'count', from: 3, to: 1 }]);
    expect(base!.revision).toBe(6);
  });

  it('без расхождения с сервером ничего не пишет', async () => {
    const put = vi.fn();
    const same = settingsToState(STATE, SETTINGS);
    await writeState({
      api: { putChatState: put, getChatState: vi.fn() }, projectId: 'p', sessionId: 's', settings: SETTINGS,
      maskFor: async () => null, base: () => same, setBase: () => {}, adopt: () => {}, onRemote: () => {},
    });
    expect(put).not.toHaveBeenCalled();
  });

  it('маска едет только при смене ревизии холста', async () => {
    const put = vi.fn(async (_p: string, _s: string, st: ImageChatState) => st);
    const mask = new Blob(['m']);
    await writeState({
      api: { putChatState: put, getChatState: vi.fn() }, projectId: 'p', sessionId: 's',
      settings: { ...SETTINGS, canvasRevision: 'r2' }, maskFor: async () => mask,
      base: () => ({ ...STATE, canvasRevision: 'r1' }), setBase: () => {}, adopt: () => {}, onRemote: () => {},
    });
    expect(put.mock.calls[0][3]).toBe(mask);
    put.mockClear();
    await writeState({
      api: { putChatState: put, getChatState: vi.fn() }, projectId: 'p', sessionId: 's',
      settings: { ...SETTINGS, canvasRevision: 'r1', prompt: 'другое' }, maskFor: async () => mask,
      base: () => ({ ...STATE, canvasRevision: 'r1' }), setBase: () => {}, adopt: () => {}, onRemote: () => {},
    });
    expect(put.mock.calls[0][3]).toBeNull();
  });
});
