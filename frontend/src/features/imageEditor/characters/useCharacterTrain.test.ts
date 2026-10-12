// Хук обучения персонажа: node-окружение без рендерера, поэтому react подменён мини-прогонщиком
// useState/useEffect (зависимости эффектов сравниваются как в React). Три места, за которыми следим:
// зависимости эффекта isAdmin, умолчательный триггер и перезагрузка персонажа после первого done.
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { LoraTrainStateDto, LoraVersionDto } from '../api';
import type { LoraTrainView } from './useLoraTraining';

const h = vi.hoisted(() => {
  const rt = {
    slots: [] as unknown[], effects: [] as { deps: unknown[] | undefined; cleanup?: () => void }[],
    i: 0, e: 0, render: () => {}, rendering: false, dirty: false,
  };
  return {
    rt,
    training: { value: null as unknown },
    active: vi.fn(),
    reload: vi.fn(),
  };
});

vi.mock('react', () => ({
  useState: <T,>(init: T) => {
    const rt = h.rt; const i = rt.i++;
    if (!(i in rt.slots)) rt.slots[i] = init;
    const set = (v: T) => {
      if (Object.is(rt.slots[i], v)) return;
      rt.slots[i] = v;
      if (rt.rendering) rt.dirty = true; else rt.render();
    };
    return [rt.slots[i] as T, set];
  },
  useEffect: (fn: () => void | (() => void), deps?: unknown[]) => {
    const rt = h.rt; const e = rt.e++;
    const prev = rt.effects[e];
    if (prev && deps && prev.deps && deps.every((d, k) => Object.is(d, prev.deps![k]))) return;
    prev?.cleanup?.();
    const slot: { deps: unknown[] | undefined; cleanup?: () => void } = { deps };
    rt.effects[e] = slot;
    queueMicrotask(() => { slot.cleanup = fn() ?? undefined; });
  },
  // Кит (в графе через loraErrors) зовёт эти фабрики на уровне модуля;
  // сами компоненты в этом тесте не рендерятся
  createContext: (defaultValue: unknown) => ({ defaultValue }),
  lazy: (load: () => unknown) => load,
  memo: <T,>(c: T) => c,
  forwardRef: <T,>(c: T) => c,
}));
vi.mock('../api', () => ({ loraTrainApi: { active: h.active } }));
vi.mock('./useCharacters', () => ({ reloadCharacters: h.reload }));
vi.mock('./useLoraTraining', () => ({ useLoraTraining: () => h.training.value }));

const { useCharacterTrain } = await import('./useCharacterTrain');

const state = (over: Partial<LoraTrainStateDto> = {}): LoraTrainStateDto => ({
  enabled: true, available: true, gpu: { busy: false, mine: false }, current: null,
  defaultTrigger: 'ccsandrey', presets: [{ key: 'standard', etaSeconds: 6660 }], photosChanged: false, ...over,
});
const ver = (over: Partial<LoraVersionDto> = {}): LoraVersionDto =>
  ({ runId: 'r1', status: 'ready', photosCount: 30, connected: true, ...over });
const setTraining = (view: LoraTrainView, st: LoraTrainStateDto | null) => {
  h.training.value = { view, state: st, error: null };
};
const versions = { error: null } as never;

type Result = ReturnType<typeof useCharacterTrain>;
function mount(projectId = 'p', slug = 's') {
  const rt = h.rt;
  rt.slots = []; rt.effects = [];
  let props = { projectId, slug };
  let result!: Result;
  rt.render = () => {
    rt.rendering = true;
    do {
      rt.dirty = false; rt.i = 0; rt.e = 0;
      result = useCharacterTrain(props.projectId, props.slug, 30, versions);
    } while (rt.dirty);
    rt.rendering = false;
  };
  rt.render();
  return {
    get: () => result,
    rerender: (p: Partial<typeof props>) => { props = { ...props, ...p }; rt.render(); },
  };
}
const settle = () => new Promise<void>(r => setTimeout(r, 0));

beforeEach(() => {
  h.active.mockReset().mockResolvedValue({ jobId: 'j1' });
  h.reload.mockReset();
  setTraining({ kind: 'idle' }, state());
});

describe('useCharacterTrain: триггер', () => {
  it('без правки берётся умолчательный из состояния', () => {
    expect(mount().get().trigger).toBe('ccsandrey');
  });
  it('введённый вручную перекрывает умолчательный; пустая правка — тоже правка', () => {
    const m = mount();
    m.get().setTrigger('mytrig');
    expect(m.get().trigger).toBe('mytrig');
    m.get().setTrigger('');
    expect(m.get().trigger).toBe('');
  });
  it('состояние не загружено — пустой триггер', () => {
    setTraining({ kind: 'loading' }, null);
    expect(mount().get().trigger).toBe('');
  });
});

describe('useCharacterTrain: isAdmin', () => {
  const foreign = () => setTraining({ kind: 'idle' }, state({ gpu: { busy: true, mine: false } }));

  it('карта занята чужим: спрашивает активный прогон, jobId даёт isAdmin', async () => {
    foreign();
    const m = mount();
    await settle();
    expect(h.active).toHaveBeenCalledTimes(1);
    expect(m.get().isAdmin).toBe(true);
  });
  it('карта свободна: активный прогон не спрашивается', async () => {
    const m = mount();
    await settle();
    expect(h.active).not.toHaveBeenCalled();
    expect(m.get().isAdmin).toBe(false);
  });
  it('смена персонажа при том же foreignBusy: запрос повторяется, прежний isAdmin сбрасывается', async () => {
    foreign();
    const m = mount('p', 'a');
    await settle();
    expect(m.get().isAdmin).toBe(true);
    h.active.mockResolvedValue({ jobId: null });
    m.rerender({ slug: 'b' });
    await settle();
    expect(h.active).toHaveBeenCalledTimes(2);
    expect(m.get().isAdmin).toBe(false);
  });
  it('смена проекта тоже перезапрашивает', async () => {
    foreign();
    const m = mount('p1', 's');
    await settle();
    m.rerender({ projectId: 'p2' });
    await settle();
    expect(h.active).toHaveBeenCalledTimes(2);
  });
});

describe('useCharacterTrain: перезагрузка после первого done', () => {
  const done = (first: boolean) => ({ kind: 'done', version: ver({ connected: first }) }) as LoraTrainView;

  it('первое обучение (версия подключена сервером) — персонаж перезагружается один раз', async () => {
    setTraining(done(true), state());
    const m = mount('p');
    await settle();
    expect(h.reload).toHaveBeenCalledTimes(1);
    expect(h.reload).toHaveBeenCalledWith('p');
    m.rerender({});
    await settle();
    expect(h.reload).toHaveBeenCalledTimes(1);
  });
  it('новая версия не подключена (first=false) — перезагрузки нет', async () => {
    setTraining(done(false), state());
    mount();
    await settle();
    expect(h.reload).not.toHaveBeenCalled();
  });
  it('без done перезагрузки нет', async () => {
    mount();
    await settle();
    expect(h.reload).not.toHaveBeenCalled();
  });
});
