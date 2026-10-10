import { describe, expect, it, vi } from 'vitest';
import type { ImageEditCharacter, LoraTrainApi, LoraVersionDto, LoraVersionsDto } from '../api';
import { createLoraVersionsController } from './useLoraVersions';

const v = (runId: string, over: Partial<LoraVersionDto> = {}): LoraVersionDto =>
  ({ runId, status: 'ready', photosCount: 30, connected: false, ...over });

function makeApi(initial: LoraVersionsDto) {
  let data = initial;
  const api = {
    versions: vi.fn(async () => data),
    connectVersion: vi.fn(async (_p: string, _s: string, runId: string) => {
      data = {
        ...data, own: null,
        items: data.items.map(x => ({ ...x, connected: x.runId === runId })),
      };
      return {} as ImageEditCharacter;
    }),
    renameVersion: vi.fn(async (_p: string, _s: string, runId: string, title: string) =>
      ({ ...data.items.find(x => x.runId === runId)!, title })),
    deleteVersion: vi.fn(async (_p: string, _s: string, runId: string) => {
      const disconnected = data.items.find(x => x.runId === runId)?.connected ?? false;
      return { status: 'deleting' as const, disconnected };
    }),
  };
  return api as typeof api & LoraTrainApi;
}

const load = async (api: LoraTrainApi) => {
  const ctl = createLoraVersionsController('p', 's', api);
  ctl.subscribe(() => {});
  await ctl.refresh();
  return ctl;
};

describe('контроллер версий LoRA', () => {
  it('«своя LoRA» приходит из own', async () => {
    const api = makeApi({ items: [v('a')], own: { lora: 'anna.safetensors' }, limit: 5, used: 1 });
    const ctl = await load(api);
    expect(ctl.getSnapshot().data?.own).toEqual({ lora: 'anna.safetensors' });
  });

  it('подключение переключает connected и гасит own', async () => {
    const api = makeApi({
      items: [v('a', { connected: true }), v('b')], own: null, limit: 5, used: 2,
    });
    const ctl = await load(api);
    expect(await ctl.connect('b')).toBe(true);
    const items = ctl.getSnapshot().data!.items;
    expect(items.map(x => [x.runId, x.connected])).toEqual([['a', false], ['b', true]]);
    expect(ctl.getSnapshot().pending).toEqual([]);
  });

  it('отказ подключения (lora_missing) даёт типизированную причину и не трогает список', async () => {
    const api = makeApi({ items: [v('a')], own: null, limit: 5, used: 1 });
    api.connectVersion.mockRejectedValueOnce(Object.assign(new Error('x'), { status: 409, body: { code: 'lora_missing' } }));
    const ctl = await load(api);
    expect(await ctl.connect('a')).toBe(false);
    expect(ctl.getSnapshot().error?.reason).toBe('lora_missing');
    expect(ctl.getSnapshot().data!.items[0].connected).toBe(false);
  });

  it('подключение отдаёт персонажа колбэку, удаление подключённой — null', async () => {
    const api = makeApi({ items: [v('a', { connected: true }), v('b')], own: null, limit: 5, used: 2 });
    const onChanged = vi.fn();
    const ctl = createLoraVersionsController('p', 's', api, onChanged);
    ctl.subscribe(() => {});
    await ctl.refresh();
    await ctl.connect('b');
    expect(onChanged).toHaveBeenCalledTimes(1);
    expect(onChanged).toHaveBeenLastCalledWith({});
    await ctl.remove('b');
    expect(onChanged).toHaveBeenCalledTimes(2);
    expect(onChanged).toHaveBeenLastCalledWith(null);
    await ctl.remove('a');
    expect(onChanged).toHaveBeenCalledTimes(2);
  });

  it('отказ подключения не зовёт колбэк', async () => {
    const api = makeApi({ items: [v('a')], own: null, limit: 5, used: 1 });
    api.connectVersion.mockRejectedValueOnce(Object.assign(new Error('x'), { status: 409, body: { code: 'lora_missing' } }));
    const onChanged = vi.fn();
    const ctl = createLoraVersionsController('p', 's', api, onChanged);
    ctl.subscribe(() => {});
    await ctl.refresh();
    await ctl.connect('a');
    expect(onChanged).not.toHaveBeenCalled();
  });

  it('действия над разными строками не затирают pending друг друга', async () => {
    const api = makeApi({ items: [v('a'), v('b')], own: null, limit: 5, used: 2 });
    let release!: () => void;
    api.renameVersion.mockImplementationOnce(() => new Promise(r => { release = () => r({ ...v('a'), title: 'x' }); }));
    const ctl = await load(api);
    const slow = ctl.rename('a', 'x');
    await ctl.remove('b');
    expect(ctl.getSnapshot().pending).toEqual(['a']);
    release();
    await slow;
    expect(ctl.getSnapshot().pending).toEqual([]);
  });

  it('ошибка refresh даёт причину и снимает loading; падение versions() при подключении — ошибка действия', async () => {
    const api = makeApi({ items: [v('a')], own: null, limit: 5, used: 1 });
    api.versions.mockRejectedValueOnce(new TypeError('fetch failed'));
    const ctl = createLoraVersionsController('p', 's', api);
    ctl.subscribe(() => {});
    await new Promise(r => setTimeout(r, 0));
    expect(ctl.getSnapshot()).toMatchObject({ loading: false, data: null, error: { reason: 'network' } });

    const api2 = makeApi({ items: [v('a')], own: null, limit: 5, used: 1 });
    const ctl2 = await load(api2);
    api2.versions.mockRejectedValueOnce(new TypeError('fetch failed'));
    expect(await ctl2.connect('a')).toBe(true);
    expect(ctl2.getSnapshot().error?.reason).toBe('network');
    expect(ctl2.getSnapshot().pending).toEqual([]);
  });

  it('переименование меняет заголовок на месте', async () => {
    const api = makeApi({ items: [v('a')], own: null, limit: 5, used: 1 });
    const ctl = await load(api);
    await ctl.rename('a', 'До бороды');
    expect(ctl.getSnapshot().data!.items[0].title).toBe('До бороды');
  });

  it('удаление убирает строку и уменьшает used; подключённая снимает connected у остальных', async () => {
    const api = makeApi({
      items: [v('a', { connected: true }), v('b'), v('c', { status: 'failed' })], own: null, limit: 5, used: 2,
    });
    const ctl = await load(api);
    await ctl.remove('a');
    const d = ctl.getSnapshot().data!;
    expect(d.items.map(x => x.runId)).toEqual(['b', 'c']);
    expect(d.used).toBe(1);
    await ctl.remove('c');
    expect(ctl.getSnapshot().data!.used).toBe(1);
  });

  it('удаление обучающейся версии отвергнуто: строка остаётся', async () => {
    const api = makeApi({ items: [v('a', { status: 'training' })], own: null, limit: 5, used: 1 });
    api.deleteVersion.mockRejectedValueOnce(Object.assign(new Error('x'), { status: 409, body: { code: 'version_training' } }));
    const ctl = await load(api);
    expect(await ctl.remove('a')).toBe(false);
    expect(ctl.getSnapshot().data!.items).toHaveLength(1);
    expect(ctl.getSnapshot().error?.reason).toBe('version_training');
  });

  it('подключение через общий контроллер видят все подписчики (секции обучения и версий)', async () => {
    const api = makeApi({ items: [v('a', { connected: true }), v('b')], own: null, limit: 5, used: 2 });
    const ctl = createLoraVersionsController('p', 's', api);
    const seenByVersions = vi.fn();
    ctl.subscribe(() => {});
    ctl.subscribe(seenByVersions);
    await ctl.refresh();
    seenByVersions.mockClear();
    expect(await ctl.connect('b')).toBe(true);
    expect(seenByVersions).toHaveBeenCalled();
    expect(ctl.getSnapshot().data!.items.find(x => x.connected)?.runId).toBe('b');
  });
});
