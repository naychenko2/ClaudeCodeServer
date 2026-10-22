// Тесты на РЕАЛЬНОМ сторе раскладки (R-Финал-2): panelStackState НЕ мокнут, в отличие
// от useInstalledPlugins.test.ts (там wsPanels — шпион). Это важно: оба дефекта итерации 2
// проявляются только когда wsPanels.use() — настоящий хук (useSyncExternalStore).
//
//  • CRITICAL: refresh() идёт ПОСЛЕ await, т.е. вне рендера. На сломанном коде он звал
//    wsPanels.use() (хук) → «Invalid hook call» → error, plugins пуст. На починенном —
//    не-хуковый путь (ops/getZones) → error null, plugins заполнен.
//  • MAJOR (orphan-скан): плагин удалён вне сессии (никогда не был в lastList) — его
//    ключ plugin:* в раскладке сносится. Мутация: вырезать блок orphan-скана → ключ остаётся.
//
// Окружение node: fakeDom + настоящий react (паттерн useInstalledPlugins.test.ts).
// signalr/api/pluginStore — моки; panelStackState — настоящий wsPanels.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { PluginInfo } from '../../lib/api';

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

const m = vi.hoisted(() => ({ list: vi.fn<() => Promise<PluginInfo[]>>() }));
vi.mock('../../lib/api', () => ({
  api: { plugins: { list: () => m.list() } },
}));

// pluginStore — шпион (side-effect setInstalledPlugins не важен для проверки error/plugins/раскладки)
vi.mock('./pluginStore', () => ({ setInstalledPlugins: vi.fn() }));

const { installFakeDom, mount, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { useInstalledPlugins } = await import('./useInstalledPlugins');
const { _resetInstalledPluginsStoreForTest } = await import('./installedPluginsStore');
// Настоящий стор раскладки: wsPanels.use() — реальный хук, ops/getZones — не-хуковый путь.
const { wsPanels } = await import('../../pages/workspace/panelStackState');

const P1: PluginInfo = {
  id: 'acme/one', version: '1.0.0', sha256: 'a',
  requested: [], granted: [], enabled: true, source: 'upload:one', secrets: [],
  contributes: { panels: [{ id: 'main', title: 'One', entry: 'panel/index.html' }] },
};

let probe: ReturnType<typeof useInstalledPlugins> | null = null;
function Probe({ enabled }: { enabled: boolean }) {
  probe = useInstalledPlugins('proj-1', enabled);
  return null;
}

const flush = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

async function renderProbe(enabled = true) {
  installFakeDom();
  const { root, container } = mount(createElement(Probe, { enabled }));
  await flush();
  return { root, container };
}

// Ключ плагина в раскладке: plugin:{publisher}/{slug}:{panelId}.
const ORPHAN_KEY = 'plugin:acme/orphan:main' as const;
const ORPHAN_ID = 'acme/orphan';

function layoutHas(key: string): boolean {
  const z = wsPanels.getZones();
  return (
    z.left.layout.flat().includes(key as never) ||
    z.right.layout.flat().includes(key as never) ||
    z.left.stash.flat().includes(key as never) ||
    z.right.stash.flat().includes(key as never)
  );
}

beforeEach(() => {
  _resetInstalledPluginsStoreForTest();
  m.list.mockReset();
  subs.message.length = 0;
  subs.reconnected.length = 0;
  probe = null;
  // Стор раскладки между тестами: закрываем панели, чтобы orphan-ключ не «унаследовался».
  act(() => {
    for (const k of [...wsPanels.getZones().left.layout.flat(), ...wsPanels.getZones().right.layout.flat()])
      wsPanels.ops.close(k as never);
  });
});

afterAll(() => { uninstallFakeDom(); });

describe('refresh() вне рендера (R-Финал-2, CRITICAL)', () => {
  it('первичная загрузка: без «Invalid hook call», plugins заполнен', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe();
    // refresh() пошёл в микрозадаче (вне рендера): на сломанном коде wsPanels.use()
    // (useSyncExternalStore) дал бы «Invalid hook call» → error, и plugins остался бы пуст.
    expect(probe?.error).toBeNull();
    expect(probe?.plugins).toEqual([P1]);
  });

  it('plugins_changed → перечитывание без «Invalid hook call»', async () => {
    m.list.mockResolvedValue([P1]);
    await renderProbe();
    expect(probe?.error).toBeNull();

    act(() => { subs.message.forEach(h => h({ type: 'plugins_changed' })); });
    await flush();
    // Второй refresh (по событию) тоже вне рендера: error не появляется, список актуален.
    expect(probe?.error).toBeNull();
    expect(probe?.plugins).toEqual([P1]);
  });
});

describe('orphan-скан (R-Финал-2, MAJOR)', () => {
  it('плагин удалён вне сессии (никогда не был в lastList) → его ключ в раскладке снесён', async () => {
    // Ключ плагина уже в раскладке (панель открывали), но плагин удалён в другом окне:
    // в живом списке его нет, и он никогда не был в lastList → обычный дифф его не видит.
    act(() => { wsPanels.ops.openIn('right', ORPHAN_KEY as never); });
    expect(layoutHas(ORPHAN_KEY)).toBe(true, 'ключ плагина до refresh в раскладке');

    // Живой список: плагина нет (удалён вне сессии)
    m.list.mockResolvedValue([P1]);
    await renderProbe();
    await flush();

    // Orphan-скан (итерация 2): ключ plugin:acme/orphan:main чьё id не в списке — снесён.
    expect(layoutHas(ORPHAN_KEY)).toBe(false, 'orphan-ключ удалённого плагина снесён из раскладки');
    // Сам список при этом корректен: плагин P1 на месте, ошибки нет.
    expect(probe?.error).toBeNull();
    expect(probe?.plugins).toEqual([P1]);
  });
});
