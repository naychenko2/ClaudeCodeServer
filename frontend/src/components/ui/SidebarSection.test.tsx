// ХВ9: тач-цель шапки SidebarSection. На сенсорном вводе строка-переключатель не меньше
// TB.iconHitMobile (40) — «тач-цель меньше 40 в чек-лист не проходит» (docs/design/guidelines.md:180);
// на мобиле через примитив открывается группа «Старые уроки» (детский режим, ХВ8). На десктопе
// с мышью вид не меняется: minHeight не ставится вовсе.
// Окружение — node + linkedom, как в соседних .test.tsx: useIsTouch читает window.matchMedia
// при mount, тач включается matches только на '(pointer: coarse)' (тот же приём, что в
// ChildHome.test.tsx, но с различением запроса: ширина окна здесь ни при чём).

import { describe, it, expect, afterEach, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import { TB } from '../../lib/design';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Тач включается только на запросе coarse pointer — как на настоящем устройстве с пальцем:
// широкая панель на десктопе (matches:false) сохраняет мышиный вид.
let coarse = false;
Object.assign(win, {
  matchMedia: (q: string) => ({
    matches: q === '(pointer: coarse)' && coarse,
    addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { SidebarSection } = await import('./SidebarSection');

const mounted: { root: ReturnType<typeof createRoot>; host: HTMLElement }[] = [];

async function render() {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  mounted.push({ root, host });
  await act(async () => {
    // children — обязательный проп примита, он проверяется в самом props-объекте
    root.render(createElement(SidebarSection, {
      title: 'Старые уроки', count: 3, children: createElement('div', null, 'содержимое секции'),
    }));
  });
  await act(async () => { await Promise.resolve(); });
  return host as HTMLElement;
}

// Размонтирование после каждого теста: иначе хосты копятся в #root между тестами
// (тот же порядок, что в ChatCard/PluginKitSection). В конце самого хелпера unmount
// невозможен: он очищает контейнер, а тесты читают его DOM после возврата хелпера.
afterEach(() => {
  for (const { root, host } of mounted.splice(0, mounted.length)) {
    act(() => { root.unmount(); });
    host.remove();
  }
});

// Кнопка-шапка — единственная с aria-expanded (слот actions в этом тесте не передаётся).
const head = (host: HTMLElement) => host.querySelector('button[aria-expanded]') as HTMLElement;

describe('SidebarSection: тач-цель шапки', () => {
  it('сенсорный ввод: minHeight шапки ≥ 40 (TB.iconHitMobile)', async () => {
    coarse = true;
    const host = await render();
    const st = head(host).style;
    expect(TB.iconHitMobile, 'само тач-значение в шкале').toBeGreaterThanOrEqual(40);
    expect(Number.parseFloat(st.minHeight), 'тач-цель шапки секции').toBeGreaterThanOrEqual(40);
  });

  it('десктоп с мышью: прежний вид — minHeight не ставится', async () => {
    coarse = false;
    const host = await render();
    const st = head(host).style;
    expect(st.minHeight || '', 'минHeight на десктопе').toBe('');
  });

  it('шапка остаётся одной строкой-переключателем в обоих режимах', async () => {
    for (const touch of [true, false]) {
      coarse = touch;
      const host = await render();
      const b = head(host);
      expect(b.textContent).toContain('Старые уроки');
      expect(b.getAttribute('aria-expanded'), `aria-expanded при touch=${touch}`).toBe('true');
      // Инвариант: шапка — переключатель, а не статичная плашка: клик по ней сворачивает
      // секцию (тот же приём клика, что в UserManagementModal.test: el.click() внутри act).
      await act(async () => { b.click(); });
      expect(b.getAttribute('aria-expanded'), `после клика при touch=${touch}`).toBe('false');
    }
  });
});
