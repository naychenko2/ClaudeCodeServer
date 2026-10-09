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
    expect(ctl.getSnapshot().pending).toBeNull();
  });

  it('отказ подключения (lora_missing) даёт типизированную причину и не трогает список', async () => {
    const api = makeApi({ items: [v('a')], own: null, limit: 5, used: 1 });
    api.connectVersion.mockRejectedValueOnce(Object.assign(new Error('x'), { status: 409, body: { code: 'lora_missing' } }));
    const ctl = await load(api);
    expect(await ctl.connect('a')).toBe(false);
    expect(ctl.getSnapshot().error?.reason).toBe('lora_missing');
    expect(ctl.getSnapshot().data!.items[0].connected).toBe(false);
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
});
