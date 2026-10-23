// Общий хук сравнения черновика (useDraftDiff): сброс на смену версии с повторным запросом,
// отбрасывание ответа, ушедшего до смены, дедуп запроса в полёте.
// Окружение node: фейк-DOM (createRoot + act), паттерн остальных тестов плагинов.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { PluginDraftDiff } from '../../lib/api';

vi.mock('../../lib/api', () => ({ api: { plugins: { draftDiff: vi.fn() } } }));

const { api } = await import('../../lib/api');
const { useDraftDiff } = await import('./useDraftDiff');
const { installFakeDom, mount, uninstallFakeDom } = await import('../../components/chat/__tests__/fakeDom');

type Hook = ReturnType<typeof useDraftDiff>;
let latest: Hook;
const roots: { unmount: () => void }[] = [];

function Probe({ version }: { version: number }) {
  latest = useDraftDiff('weather', version);
  return null;
}

function diffOf(tag: string): PluginDraftDiff {
  return { tag } as unknown as PluginDraftDiff;
}

// Управляемый ответ: резолвим вручную, чтобы поймать «запрос в полёте»
function deferred() {
  let resolve!: (d: PluginDraftDiff) => void;
  const promise = new Promise<PluginDraftDiff>(r => { resolve = r; });
  return { promise, resolve };
}

beforeEach(() => { installFakeDom(); vi.mocked(api.plugins.draftDiff).mockReset(); });
afterEach(() => { for (const r of roots.splice(0)) act(() => r.unmount()); });
afterAll(() => { uninstallFakeDom(); });

function render(version: number) {
  const m = mount(createElement(Probe, { version }));
  roots.push(m.root);
  return m.root;
}

describe('useDraftDiff', () => {
  it('окно открыто → агент правит (смена версии) → diff свежий', async () => {
    vi.mocked(api.plugins.draftDiff)
      .mockResolvedValueOnce(diffOf('old'))
      .mockResolvedValueOnce(diffOf('new'));
    const root = render(1);
    await act(async () => { latest.loadDiff(); });
    expect(latest.diff).toEqual(diffOf('old'));

    await act(async () => { root.render(createElement(Probe, { version: 2 })); });
    expect(api.plugins.draftDiff).toHaveBeenCalledTimes(2);
    expect(latest.diff).toEqual(diffOf('new'));
  });

  it('сравнение не запрашивали → смена версии только сбрасывает, запроса нет', async () => {
    const root = render(1);
    await act(async () => { root.render(createElement(Probe, { version: 2 })); });
    expect(api.plugins.draftDiff).not.toHaveBeenCalled();
    expect(latest.diff).toBeNull();
  });

  it('ответ, ушедший до правки агента, отбрасывается', async () => {
    const stale = deferred();
    vi.mocked(api.plugins.draftDiff)
      .mockReturnValueOnce(stale.promise)
      .mockResolvedValueOnce(diffOf('fresh'));
    const root = render(1);
    await act(async () => { latest.loadDiff(); });
    await act(async () => { root.render(createElement(Probe, { version: 2 })); });
    await act(async () => { stale.resolve(diffOf('stale')); });
    expect(latest.diff).toEqual(diffOf('fresh'));
  });

  it('двойной вызов в полёте — один запрос; после загрузки повторный — тоже', async () => {
    const pending = deferred();
    vi.mocked(api.plugins.draftDiff).mockReturnValueOnce(pending.promise);
    render(1);
    await act(async () => { latest.loadDiff(); latest.loadDiff(); });
    expect(api.plugins.draftDiff).toHaveBeenCalledTimes(1);
    await act(async () => { pending.resolve(diffOf('x')); });
    await act(async () => { latest.loadDiff(); });
    expect(api.plugins.draftDiff).toHaveBeenCalledTimes(1);
  });

  it('была ошибка → агент правит (смена версии) → ошибка сброшена, запрос повторный', async () => {
    vi.mocked(api.plugins.draftDiff)
      .mockRejectedValueOnce(new Error('сбой'))
      .mockResolvedValueOnce(diffOf('fresh'));
    const root = render(1);
    await act(async () => { latest.loadDiff(); });
    expect(latest.diffError).toBe('сбой');

    await act(async () => { root.render(createElement(Probe, { version: 2 })); });
    expect(api.plugins.draftDiff).toHaveBeenCalledTimes(2);
    expect(latest.diffError).toBeNull();
    expect(latest.diff).toEqual(diffOf('fresh'));
  });
});
