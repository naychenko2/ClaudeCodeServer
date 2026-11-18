// ХВ2-Ф1: длинная подпись шляпки не выходит за капсулу рельса (дефект QA_KIDS_W3).
// Внутри капсулы 39px, а капс длинного имени (имя ребёнка в детской оболочке) шире —
// без обрезки строка с whiteSpace:nowrap уходила за край окна. Правка: span подписи
// режется многоточием, его родитель (div строки) ограничен шириной капсулы.
// При этом div строки сам НЕ режет: иконка сворачивания (9px) намеренно вылезает за
// 7-пиксельную строку на пиксель сверху и снизу — overflow на ней срезал бы иконку.
// Окружение — node + linkedom, как в SidebarSection.test.tsx; у RailHat нет тач-
// веток на mount, стабы — только общий минимум для рендера.

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import type { ComponentProps } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({
    matches: false,
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

const { RailHat } = await import('./RailHat');
type HatProps = ComponentProps<typeof RailHat>;

// Без title: плашка RailFlyout сама рисует span-ы, и поиск по span поймал бы её,
// а не строку подписи. Обрезка проверяется на самой шляпке — плашка ей не нужна.
async function render(props: HatProps) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(RailHat, props)); });
  await act(async () => { await Promise.resolve(); });
  return host as HTMLElement;
}

describe('RailHat: обрезание длинной подписи', () => {
  it('подпись рендерится в span с многоточием, её родитель ограничен шириной капсулы', async () => {
    const host = await render({ side: 'left', label: 'Александра-Мария' });
    const span = host.querySelector('span') as HTMLElement | null;
    expect(span, 'span подписи').not.toBeNull();
    expect(span!.textContent).toBe('Александра-Мария');
    const st = span!.style;
    expect(st.overflow, 'overflow span').toBe('hidden');
    expect(st.textOverflow, 'text-overflow span').toBe('ellipsis');
    expect(st.whiteSpace, 'nowrap внутри span').toBe('nowrap');
    // Родитель строки — с пределом по ширине капсулы, но сам не режет:
    // иконка сворачивания вылезает за строку и её бы срезало
    const line = span!.parentElement as HTMLElement;
    expect(line.style.maxWidth, 'max-width строки').toBe('100%');
    expect(line.style.overflow || '', 'overflow строки не ставится').toBe('');
  });

  it('при collapse и наведении вместо подписи — иконка, и её родитель не режет', async () => {
    const host = await render({
      side: 'left', label: 'Панели',
      collapse: { collapsed: false, onToggle: () => {} }, railHover: true,
    });
    const svg = host.querySelector('svg') as HTMLElement | null;
    expect(svg, 'иконка сворачивания').not.toBeNull();
    expect(host.querySelector('span'), 'подписи под курсором нет').toBeNull();
    const line = svg!.parentElement as HTMLElement;
    expect(line.style.overflow || '', 'overflow строки под иконкой').toBe('');
  });
});
