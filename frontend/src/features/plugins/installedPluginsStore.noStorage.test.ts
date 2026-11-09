// F-д2: при отключённом хранилище (любое обращение к localStorage бросает
// SecurityError — Firefox dom.storage.enabled=false, приватный режим) список
// плагинов обязан открываться: сборка мусора в refresh() — тихий no-op, а не
// ошибка всего списка. Здесь НАСТОЯЩИЙ pluginStorageLifecycle (не мокнут), а
// globalThis.localStorage имитирует отключённое хранилище.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { PluginInfo } from '../../lib/api';
import { withThrowingLocalStorage } from './testing/withThrowingLocalStorage';

vi.mock('../../lib/defaultPersona', () => ({ getMeSnapshot: () => ({ userId: 'u1' }) }));
vi.mock('../../lib/signalr', () => ({ onMessage: () => () => {}, onReconnected: () => () => {} }));
const m = vi.hoisted(() => ({ list: vi.fn<() => Promise<PluginInfo[]>>() }));
vi.mock('../../lib/api', () => ({ api: { plugins: { list: () => m.list() } } }));
vi.mock('./pluginStore', () => ({ setInstalledPlugins: vi.fn() }));

const { installFakeDom, mount, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { useInstalledPlugins } = await import('./useInstalledPlugins');
const { _resetInstalledPluginsStoreForTest } = await import('./installedPluginsStore');

const P1: PluginInfo = {
  id: 'acme/one', version: '1.0.0', sha256: 'a',
  requested: [], granted: [], enabled: true, source: 'upload:one', secrets: [],
  contributes: { panels: [{ id: 'main', title: 'One', entry: 'panel/index.html' }] },
};

// Значение хука достаётся из рендера (тот же приём, что в realStore-тесте).
let probe: ReturnType<typeof useInstalledPlugins> | null = null;
function Probe() {
  // eslint-disable-next-line react-hooks/globals -- тест достаёт значение хука из рендера
  probe = useInstalledPlugins('proj-1', true);
  return null;
}

const flush = () => act(async () => { await new Promise(r => setTimeout(r, 0)); });

beforeEach(() => {
  _resetInstalledPluginsStoreForTest();
  m.list.mockReset();
  probe = null;
});

afterAll(() => { uninstallFakeDom(); });

describe('список при отключённом хранилище (F-д2)', () => {
  // Мутация: вернуть в pluginStorageLifecycle «typeof localStorage === 'undefined' ?
  // null : localStorage» без try/catch — refresh() словит SecurityError, error
  // станет текстом, plugins пустым → этот тест красный.
  it('настоящий collectOnListLoaded встречает SecurityError: список открыт, error null', async () => {
    await withThrowingLocalStorage(() => { throw Object.assign(new Error('sandboxed'), { name: 'SecurityError' }); }, async () => {
      m.list.mockResolvedValue([P1]);
      installFakeDom();
      mount(createElement(Probe));
      await flush();
      expect(probe?.error).toBeNull();
      expect(probe?.plugins).toEqual([P1]);
    });
  });
});
