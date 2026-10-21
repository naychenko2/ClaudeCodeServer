// Панель плагина (S5): PluginFrame принимает КОНКРЕТНЫЙ (plugin, panel) — одна панель
// = одна карточка. Выбор, ЧЬЮ панелью пользоваться, и переключатель — выше (WorkspacePage
// строит pluginPanelTargets, мобила — PluginPanelsSwitcher). Здесь проверяем сам фрейм:
//  • panelHtml вызывается с id/версией плагина и entry ПАНЕЛИ из манифеста (не хардкод);
//  • 404 → «Панель не найдена»;
//  • html получен → iframe смонтирован.
//
// Окружение node: минимальный фейк-DOM (createRoot + act, паттерн fakeDom).
// useHasPanelHeader не мокаем: без PanelShell (тест монтирует фрейм голо) → false →
// фрейм рисует СВОЙ Island (мобильный режим), что и проверяем.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project } from '../../types';
import type { PluginInfo, PluginPanelView } from '../../lib/api';

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
const { installFakeDom, mount, textNodes, findNodeByAttr, reactProps, uninstallFakeDom } = await import(
  '../../components/chat/__tests__/fakeDom'
);
const { PluginFrame, pluginFrameKey } = await import('./PluginFrame');

// === Фиксированные данные ===
const PROJECT: Project = {
  id: 'proj-1',
  name: 'Тест',
  rootPath: '/test',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

const P_WITH_PANEL: PluginInfo = {
  id: 'local-1/with-panel', version: '1.0.0', sha256: 'b',
  requested: [], granted: [], enabled: true, source: 'upload:with-panel', secrets: [],
  contributes: { panels: [{ id: 'p1', title: 'Panel', entry: 'panel/index.html' }] },
};
const PANEL_1: PluginPanelView = { id: 'p1', title: 'Panel', entry: 'panel/index.html' };
// Панель с НЕ-стандартным entry: panelHtml обязан запросить именно panel/main.html
const P_WITH_PANEL_MAIN: PluginInfo = {
  id: 'local-1/with-panel-main', version: '1.0.0', sha256: 'd',
  requested: [], granted: [], enabled: true, source: 'upload:with-panel-main', secrets: [],
  contributes: { panels: [{ id: 'p3', title: 'Main', entry: 'panel/main.html' }] },
};
const PANEL_MAIN: PluginPanelView = { id: 'p3', title: 'Main', entry: 'panel/main.html' };

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

// Монтирует фрейм с конкретным (plugin, panel) и даёт асинхронной заборе панели дойти
async function renderFrame(plugin: PluginInfo, panel: PluginPanelView) {
  installFakeDom();
  const { container } = mount(createElement(PluginFrame, { project: PROJECT, plugin, panel }));
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

describe('PluginFrame: фрейм конкретной панели', () => {
  it('panelHtml вызывается с id/версией плагина и entry панели из манифеста', async () => {
    await renderFrame(P_WITH_PANEL, PANEL_1);
    const call = vi.mocked(api.plugins.panelHtml).mock.calls[0];
    expect(call, 'panelHtml не вызван').toBeDefined();
    // [id, version, entry]
    expect(call?.[0]).toBe('local-1/with-panel');
    expect(call?.[1]).toBe('1.0.0');
    expect(call?.[2]).toBe('panel/index.html');
  });

  // entry из манифеста (не panel/index.html) → panelHtml получает именно её (3-й аргумент).
  // Мутация «вернуть хардкод panel/index.html» → 3-й аргумент снова panel/index.html → красный.
  it('entry=panel/main.html → panelHtml вызывается с entry=panel/main.html', async () => {
    await renderFrame(P_WITH_PANEL_MAIN, PANEL_MAIN);
    const call = vi.mocked(api.plugins.panelHtml).mock.calls[0];
    expect(call, 'panelHtml не вызван').toBeDefined();
    expect(call?.[0]).toBe('local-1/with-panel-main');
    expect(call?.[1]).toBe('1.0.0');
    expect(call?.[2]).toBe('panel/main.html');
  });

  it('404 → «Панель не найдена» (iframe не смонтирован)', async () => {
    vi.mocked(api.plugins.panelHtml).mockRejectedValue(Object.assign(new Error('not found'), { status: 404 }));
    const { text } = await renderFrame(P_WITH_PANEL, PANEL_1);
    expect(text()).toContain('Панель не найдена');
  });
});

// === S7: версия плагина и набор прав в ключе фрейма ===
// Обновился плагин (новая версия) или выдано/снято право → ключ меняется →
// фрейм пересоздаётся, иначе панель продолжала бы показывать старое.
// Мутации: убрать version из pluginFrameKey → «новая версия → другой ключ»
// краснеет; убрать granted → «выданное/снятое право → другой ключ» краснеет.
describe('pluginFrameKey: версия и права в ключе', () => {
  const base: PluginInfo = { ...P_WITH_PANEL, granted: [] };

  it('ключ несёт id/панель, версию и отсортированный набор прав', () => {
    const k = pluginFrameKey({ ...base, granted: ['b:write', 'a:read'] }, PANEL_1, 0);
    expect(k).toBe('local-1/with-panel::p1#1.0.0#a:read,b:write#0');
  });

  it('обновление плагина (новая версия) → другой ключ', () => {
    const a = pluginFrameKey(base, PANEL_1, 0);
    const b = pluginFrameKey({ ...base, version: '2.0.0' }, PANEL_1, 0);
    expect(a).not.toBe(b);
  });

  it('выданное/снятое право → другой ключ', () => {
    const a = pluginFrameKey(base, PANEL_1, 0);
    const b = pluginFrameKey({ ...base, granted: ['notes:read'] }, PANEL_1, 0);
    expect(a).not.toBe(b);
  });

  it('один и тот же набор прав в другом порядке — тот же ключ (без лишнего перемонтирования)', () => {
    const a = pluginFrameKey({ ...base, granted: ['a:read', 'b:write'] }, PANEL_1, 0);
    const b = pluginFrameKey({ ...base, granted: ['b:write', 'a:read'] }, PANEL_1, 0);
    expect(a).toBe(b);
  });

  it('«Перезагрузить» (reloadKey) тоже в ключе', () => {
    expect(pluginFrameKey(base, PANEL_1, 0)).not.toBe(pluginFrameKey(base, PANEL_1, 1));
  });
});

// Поведенческий: смена версии пересоздаёт iframe (React remount по key) —
// панель в новом фрейме получит init с новой версией, а не продолжит жить в старом.
describe('PluginFrame: смена версии пересоздаёт iframe', () => {
  // Первый iframe в дереве контейнера (обход childNodes)
  function findIframe(n: FNode): FNode | null {
    if (n.nodeType === 1 && n.nodeName === 'IFRAME') return n;
    for (const c of n.childNodes) {
      const hit = findIframe(c);
      if (hit) return hit;
    }
    return null;
  }

  it('обновление плагина (v1 → v2) → iframe-узел заменён', async () => {
    installFakeDom();
    const { root, container } = mount(createElement(PluginFrame, {
      project: PROJECT, plugin: P_WITH_PANEL, panel: PANEL_1,
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const iframe1 = findIframe(container);
    expect(iframe1, 'iframe смонтирован после загрузки html').not.toBeNull();

    // Сервер обновил плагин: хост перечитал список, фрейм получил новую версию
    act(() => {
      root.render(createElement(PluginFrame, {
        project: PROJECT, plugin: { ...P_WITH_PANEL, version: '2.0.0' }, panel: PANEL_1,
      }));
    });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const iframe2 = findIframe(container);
    expect(iframe2, 'iframe смонтирован после обновления').not.toBeNull();
    expect(iframe2, 'фрейм пересоздан: прежний узел заменён новым').not.toBe(iframe1);
  });
});

// R-Финал: в разметке меню «⋯» не должно быть мусорного текста. Раньше комментарий
// «// …» стоял ВНУТРИ JSX <Menu>, и React выводил его как обычный текст (сборка и lint
// это не ловили). Теперь — {/* … */}. Открываем меню и проверяем, что в его тексте нет
// слов комментария, а есть только ожидаемый пункт.
describe('PluginFrame: меню «⋯» без мусорного текста (R-Финал)', () => {
  it('открытое меню не содержит текста комментария (только «Права…»)', async () => {
    const { container, text } = await renderFrame(P_WITH_PANEL, PANEL_1);

    // Открываем меню: кнопка «Меню панели»
    const btn = findNodeByAttr(container, 'aria-label', 'Меню панели');
    expect(btn, 'кнопка «Меню панели» найдена').not.toBeNull();
    act(() => { (reactProps(btn!) as { onClick?: () => void }).onClick?.(); });

    // В меню только «Права…» (onOpenPlugins/onClose в этом тесте не заданы)
    expect(text()).toContain('Права…');
    // Слова комментария-мусора в тексте меню не осталось
    expect(text()).not.toContain('сепаратор');
    expect(text()).not.toContain('Разделитель');
  });
});
