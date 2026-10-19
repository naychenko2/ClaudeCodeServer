// Слот панели плагина (макет экран 3, ADR-031): панель берётся у плагина, у которого
// панель ОБЪЯВЛЕНА (contributes.panels в данных списка), а не просто у первого включённого:
//  • включённый без панели первым + включённый с панелью вторым → показывается второй;
//  • два с панелями → переключатель в шапке острова;
//  • ни одного с панелью → «пусто» (панелей нет).
//
// Окружение node: минимальный фейк-DOM (createRoot + act, паттерн fakeDom).
// Бэковая зависимость: PluginResponse (список) пока не отдаёт contributes — тест строится
// на моке данных списка, где contributes.panels присутствует.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project } from '../../types';
import type { PluginInfo } from '../../lib/api';

// useIsMobile — false (десктоп); прочие экспорты модуля — оригинал
vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});

// Тема хоста: фиксированная, без перемонтирования; прочие экспорты — оригинал
vi.mock('../../lib/themeMode', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/themeMode')>();
  return { ...orig, getEffectiveTheme: () => 'light', subscribeThemeMode: () => () => {} };
});

// useProjectFeature — плагины доступны; прочие экспорты (RELAY_ROUTE_REASON и др.) — оригинал
vi.mock('../../lib/projectCapabilities', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/projectCapabilities')>();
  return { ...orig, useProjectFeature: () => true };
});

// onMessage — заглушка (realtime в этом тесте не сдвигаем); прочие экспорты — оригинал
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {} };
});

// api.plugins.* — фиксированные ответы; остальные экспорты api — оригинал
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return {
    ...orig,
    api: {
      ...orig.api,
      plugins: {
        ...orig.api.plugins,
        list: vi.fn(),
        panelHtml: vi.fn(),
        call: vi.fn(),
        grant: vi.fn(),
      },
    },
  };
});

const { api } = await import('../../lib/api');
const { installFakeDom, mount, textNodes, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { PluginFrame } = await import('./PluginFrame');

// === Фиксированные данные ===
const PROJECT: Project = {
  id: 'proj-1',
  name: 'Тест',
  rootPath: '/test',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

const P_NO_PANEL: PluginInfo = {
  id: 'local-1/no-panel', version: '1.0.0', sha256: 'a',
  requested: [], granted: [], enabled: true, source: 'upload:no-panel', secrets: [],
};
const P_WITH_PANEL: PluginInfo = {
  id: 'local-1/with-panel', version: '1.0.0', sha256: 'b',
  requested: [], granted: [], enabled: true, source: 'upload:with-panel', secrets: [],
  contributes: { panels: [{ id: 'p1', slot: 'sidebar', title: 'Panel' }] },
};
const P_WITH_PANEL_2: PluginInfo = {
  id: 'local-1/with-panel-2', version: '1.0.0', sha256: 'c',
  requested: [], granted: [], enabled: true, source: 'upload:with-panel-2', secrets: [],
  contributes: { panels: [{ id: 'p2', slot: 'sidebar', title: 'Panel 2' }] },
};

// === Хелперы (типы нод fakeDom не экспортируются — через сигнатуру textNodes) ===
type FNode = Parameters<typeof textNodes>[0];

function textOf(n: FNode): string {
  let out = '';
  const walk = (x: FNode) => {
    if (x.nodeType === 3) out += x.nodeValue;
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return out;
}

// Кнопка/элемент с onClick, чей ПОЛНЫЙ текст равен label
function findButton(container: FNode, label: string): { onClick: () => void } | null {
  let hit: { onClick: () => void } | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1) {
      const p = Object.keys(x).find(k => k.startsWith('__reactProps$'));
      if (p) {
        const props = (x as unknown as Record<string, unknown>)[p] as Record<string, unknown> | undefined;
        if (props?.onClick && textOf(x) === label) hit = { onClick: props.onClick as () => void };
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// Элемент по aria-label (для IconButton-триггеров без текстовой подписи)
function findByAriaLabel(container: FNode, label: string): FNode | null {
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.getAttribute('aria-label') === label) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// Монтирует остров и даёт асинхронной загрузке списка (и забору панели) дойти
async function renderFrame() {
  installFakeDom();
  const { container } = mount(createElement(PluginFrame, { project: PROJECT }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return { container, text: () => textNodes(container).join(' ') };
}

beforeEach(() => {
  vi.mocked(api.plugins.list).mockReset();
  vi.mocked(api.plugins.panelHtml).mockReset().mockResolvedValue('<html></html>');
  vi.mocked(api.plugins.call).mockReset();
  vi.mocked(api.plugins.grant).mockReset();
});

afterAll(() => { uninstallFakeDom(); });

describe('PluginFrame: выбор плагина с панелью', () => {
  it('включённый без панели первым + с панелью вторым → панель второго (panelHtml по id второго)', async () => {
    vi.mocked(api.plugins.list).mockResolvedValue([P_NO_PANEL, P_WITH_PANEL]);

    const { text } = await renderFrame();
    // панель забрана у плагина С панелью (второго), а не у первого без панели
    expect(vi.mocked(api.plugins.panelHtml).mock.calls[0]?.[0]).toBe('local-1/with-panel');
    // остров не в «пустом» состоянии
    expect(text()).not.toContain('У установленных плагинов панелей нет');
  });

  it('два включённых с панелями → переключатель в шапке', async () => {
    vi.mocked(api.plugins.list).mockResolvedValue([P_WITH_PANEL, P_WITH_PANEL_2]);

    const { container } = await renderFrame();
    // триггер переключателя (aria-label) на месте
    expect(findByAriaLabel(container, 'Выбрать плагин'), 'переключатель не найден').not.toBeNull();
  });

  it('ни одного включённого с панелью → «пусто» (панелей нет)', async () => {
    vi.mocked(api.plugins.list).mockResolvedValue([P_NO_PANEL]);

    const { text } = await renderFrame();
    expect(text()).toContain('У установленных плагинов панелей нет');
    // панель не забрана ни у кого
    expect(vi.mocked(api.plugins.panelHtml).mock.calls.length).toBe(0);
  });
});
