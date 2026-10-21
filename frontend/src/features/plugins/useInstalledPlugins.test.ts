// useInstalledPlugins (S7): живое обновление списка установленных плагинов.
//  • plugins_changed (S6, без полезной нагрузки) → перечитывание GET /api/plugins;
//  • reconnected → перечитывание (события за время обрыва потеряны);
//  • сборка мусора по диффу УСПЕШНОЙ загрузки: плагин исчез из списка — его
//    ключи снимаются из раскладки (forgetPlugin → forgetPluginKeys, S3);
//  • УПАВШИЙ GET (ошибка) ничего не выносит: раскладка цела, ошибка — в обычном
//    виде (error), прежний список не тронут.
//
// Окружение node: fakeDom + настоящий react (паттерн PluginFrame.test.ts).
// signalr/api/panelStore/panelStackState — моки (подписки и стор ловим шпионами).

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { PluginInfo } from '../../lib/api';

// Подписчики signalr: ловим, чтобы «послать» событие из теста
const subs = vi.hoisted(() => ({
  message: [] as ((msg: unknown) => void)[],
  reconnected: [] as (() => void)[],
}));
vi.mock('../../lib/signalr', () => ({
  onMessage: (h: (msg: unknown) => void) => {
    subs.message.push(h);
    return () => { const i = subs.message.indexOf(h); if (i >= 0) subs.message.splice(i, 1); };
  },
  onReconnected: (h: () => void) => {
    subs.reconnected.push(h);
    return () => { const i = subs.reconnected.indexOf(h); if (i >= 0) subs.reconnected.splice(i, 1); };
  },
}));

// GET /api/plugins — управляемый шпион
const m = vi.hoisted(() => ({ list: vi.fn<() => Promise<PluginInfo[]>>() }));
vi.mock('../../lib/api', () => ({
  api: { plugins: { list: () => m.list() } },
}));

// Стор раскладки: forgetPlugin (S7) — шпион
const store = vi.hoisted(() => ({ forgetPlugin: vi.fn() }));
vi.mock('../../pages/workspace/panelStackState', () => ({
  wsPanels: { use: () => ({ forgetPlugin: store.forgetPlugin }) },
}));

// Стор «установленные плагины» (pluginStore) — шпион
vi.mock('./pluginStore', () => ({ setInstalledPlugins: vi.fn() }));

const { installFakeDom, mount, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { useInstalledPlugins } = await import('./useInstalledPlugins');
const { _resetInstalledPluginsStoreForTest } = await import('./installedPluginsStore');

// === Фиксированные плагины ===
const P1: PluginInfo = {
  id: 'acme/one', version: '1.0.0', sha256: 'a',
  requested: [], granted: [], enabled: true, source: 'upload:one', secrets: [],
  contributes: { panels: [{ id: 'main', title: 'One', entry: 'panel/index.html' }] },
};
const P2: PluginInfo = {
  id: 'acme/two', version: '2.0.0', sha256: 'b',
  requested: [], granted: [], enabled: true, source: 'upload:two', secrets: [],
  contributes: { panels: [{ id: 'main', title: 'Two', entry: 'panel/index.html' }] },
};

// Хук-проба: результат хука доступен тесту после рендера
let probe: ReturnType<typeof useInstalledPlugins> | null = null;
function Probe({ enabled }: { enabled: boolean }) {
  probe = useInstalledPlugins('proj-1', enabled);
  return null;
}

// Отмыть асинхронность: дать микрозадачам/таймеру 0 дойти
const flush = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

async function renderProbe(enabled = true) {
  installFakeDom();
  const { root, container } = mount(createElement(Probe, { enabled }));
  await flush();
  return { root, container };
}

beforeEach(() => {
  _resetInstalledPluginsStoreForTest();
  m.list.mockReset();
  store.forgetPlugin.mockClear();
  subs.message.length = 0;
  subs.reconnected.length = 0;
  probe = null;
});

afterAll(() => { uninstallFakeDom(); });

describe('useInstalledPlugins: первичная загрузка', () => {
  it('enabled=true → один GET /api/plugins при монтировании', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe();
    expect(m.list).toHaveBeenCalledTimes(1);
    expect(probe?.plugins).toEqual([P1]);
    expect(probe?.error).toBeNull();
  });

  it('enabled=false → GET не идёт', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe(false);
    expect(m.list).not.toHaveBeenCalled();
    expect(probe?.plugins).toEqual([]);
  });
});

describe('useInstalledPlugins: живое обновление (S7)', () => {
  it('plugins_changed → перечитывание списка (без закрытия/открытия панели)', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe();
    expect(m.list).toHaveBeenCalledTimes(1);

    // Хранилище плагинов записало установку (install/grant/…) и испустило
    // событие владельцу. Полезной нагрузки в событии нет — клиент перечитывает.
    act(() => { subs.message.forEach(h => h({ type: 'plugins_changed' })); });
    await flush();
    expect(m.list).toHaveBeenCalledTimes(2);
  });

  it('чужие события (не plugins_changed) список не перечитывают', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe();
    act(() => { subs.message.forEach(h => h({ type: 'git_status_changed', projectId: 'p1' })); });
    await flush();
    expect(m.list).toHaveBeenCalledTimes(1);
  });

  it('reconnected → перечитывание (события за время обрыва потеряны)', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe();
    expect(m.list).toHaveBeenCalledTimes(1);

    act(() => { subs.reconnected.forEach(h => h()); });
    await flush();
    expect(m.list).toHaveBeenCalledTimes(2);
  });

  it('enabled=false → подписок нет: ни событие, ни реконнект список не трогают', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe(false);
    act(() => {
      subs.message.forEach(h => h({ type: 'plugins_changed' }));
      subs.reconnected.forEach(h => h());
    });
    await flush();
    expect(m.list).not.toHaveBeenCalled();
  });
});

describe('useInstalledPlugins: сборка мусора по диффу живого списка (S7)', () => {
  it('плагин исчез из УСПЕШНОЙ загрузки → forgetPlugin(id) (ключи снимаются из раскладки)', async () => {
    m.list.mockResolvedValueOnce([P1, P2]).mockResolvedValue([P1]);
    await renderProbe();
    expect(m.list).toHaveBeenCalledTimes(1);
    expect(store.forgetPlugin).not.toHaveBeenCalled();

    // plugins_changed: в свежем списке P2 нет
    act(() => { subs.message.forEach(h => h({ type: 'plugins_changed' })); });
    await flush();
    expect(m.list).toHaveBeenCalledTimes(2);
    expect(store.forgetPlugin).toHaveBeenCalledTimes(1);
    expect(store.forgetPlugin).toHaveBeenCalledWith('acme/two');
    // список и стор обновлены: P2 нет
    expect(probe?.plugins).toEqual([P1]);
  });

  it('плагин появился в списке → просто добавлен (forgetPlugin не зовётся)', async () => {
    m.list.mockResolvedValueOnce([P1]).mockResolvedValue([P1, P2]);
    await renderProbe();
    act(() => { subs.message.forEach(h => h({ type: 'plugins_changed' })); });
    await flush();
    expect(store.forgetPlugin).not.toHaveBeenCalled();
    expect(probe?.plugins).toEqual([P1, P2]);
  });

  it('УПАВШИЙ GET (ошибка) ничего не выносит: forgetPlugin не зван, список прежний, error в обычном виде', async () => {
    m.list.mockResolvedValueOnce([P1, P2]).mockRejectedValue(new Error('нет сети'));
    await renderProbe();
    expect(store.forgetPlugin).not.toHaveBeenCalled();

    act(() => { subs.message.forEach(h => h({ type: 'plugins_changed' })); });
    await flush();
    // ошибка показана, раскладка не тронута, прежний список на месте
    expect(probe?.error).toBe('нет сети');
    expect(store.forgetPlugin).not.toHaveBeenCalled();
    expect(probe?.plugins).toEqual([P1, P2]);
  });

  it('УПАВШИЙ GET (таймаут) по reconnected тоже ничего не выносит', async () => {
    // «Пустой ответ» по контракту бэка — это ошибка запроса (reject), а не
    // успешный GET с []: успешный пустой список — легитимное «всё выключено».
    m.list.mockResolvedValueOnce([P1, P2]).mockRejectedValue(new Error('timeout'));
    await renderProbe();
    act(() => { subs.reconnected.forEach(h => h()); });
    await flush();
    expect(store.forgetPlugin).not.toHaveBeenCalled();
    expect(probe?.plugins).toEqual([P1, P2]);
  });
});

describe('useInstalledPlugins: возврат в проект (R-Финал)', () => {
  it('ушёл из проекта → вернулся: список перечитывается (плагин, установленный, пока проект был закрыт, виден)', async () => {
    // Первый GET: один плагин. Пока проект закрыт, в чате вне проекта принимают
    // черновик — появляется P2. Возврат в проект обязан послать НОВЫЙ GET: иначе
    // список остался бы старым ([P1]) до F5.
    m.list.mockResolvedValueOnce([P1]).mockResolvedValue([P1, P2]);

    // Открыли проект: первичный GET
    const first = await renderProbe();
    expect(m.list).toHaveBeenCalledTimes(1);
    expect(probe?.plugins).toEqual([P1]);

    // Ушли из проекта: страница размонтировалась, проект «заснул» (active=0)
    act(() => { first.root.unmount(); });

    // Вернулись в проект: повторный mount шлёт новый GET
    await renderProbe();
    expect(m.list).toHaveBeenCalledTimes(2);
    // Список актуален: P2 на месте
    expect(probe?.plugins).toEqual([P1, P2]);
  });
});
