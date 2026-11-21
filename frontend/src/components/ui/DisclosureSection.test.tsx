// Ф-1 (v1 «Сначала ферма»): управляемый примитив раскрытия. Проверяем контракт,
// а не косметику: закрытое начальное состояние, переключение по клику через onToggle
// потребителя, aria-expanded/aria-controls и связь региона, статичная строка без
// children (пустое облачное окно), высоты 40/44, приглушение движения по
// prefers-reduced-motion.
//
// Окружение — node + linkedom, как в соседних .test.tsx (SidebarSection.test.tsx):
// matchMedia здесь нужен только на запросе reduced motion — сам примитив ширину
// не меряет, isMobile приходит пропом.

import { describe, it, expect, afterEach, vi } from 'vitest';
import { act, createElement, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><head></head><body><div id="root"></div></body></html>');
let reducedMotion = false;
Object.assign(win, {
  matchMedia: (q: string) => ({
    matches: q === '(prefers-reduced-motion: reduce)' && reducedMotion,
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

const { DisclosureSection } = await import('./DisclosureSection');

const mounted: { root: ReturnType<typeof createRoot>; host: HTMLElement }[] = [];

// Состояние держит потребитель — ровно так, как его будет держать панель:
// open/onToggle пропом, внутри примитива своего состояния нет.
function Harness({ open: initialOpen = false, isMobile = false, withBody = true, meta }: {
  open?: boolean; isMobile?: boolean; withBody?: boolean; meta?: string;
}) {
  const [open, setOpen] = useState(initialOpen);
  return createElement(DisclosureSection, {
    title: 'Точные метрики локальной модели',
    meta,
    open,
    onToggle: () => setOpen(o => !o),
    isMobile,
    children: withBody ? createElement('div', null, 'содержимое метрик') : undefined,
  });
}

async function render(props: Parameters<typeof Harness>[0] = {}) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  mounted.push({ root, host });
  await act(async () => {
    root.render(createElement(Harness, props));
  });
  await act(async () => { await Promise.resolve(); });
  return host as HTMLElement;
}

afterEach(() => {
  for (const { root, host } of mounted.splice(0, mounted.length)) {
    act(() => { root.unmount(); });
    host.remove();
  }
});

const head = (host: HTMLElement) => host.querySelector('button[aria-expanded]') as HTMLElement;
const body = (host: HTMLElement) => host.querySelector('[role=region]') as HTMLElement;
const chevron = (host: HTMLElement) => head(host).querySelector('svg') as unknown as HTMLElement;

describe('DisclosureSection: контракт раскрытия', () => {
  it('закрытое начальное состояние: aria-expanded=false, тело смонтировано и скрыто display', async () => {
    const host = await render({ open: false });
    expect(head(host).getAttribute('aria-expanded'), 'aria-expanded закрытой секции').toBe('false');
    const b = body(host);
    expect(b, 'тело смонтировано при закрытой секции (опросы внутри не останавливаются)').toBeTruthy();
    expect(b.textContent).toContain('содержимое метрик');
    expect(b.style.display, 'скрытие — display:none, не размонтирование').toBe('none');
  });

  it('клик по заголовку переключает через onToggle потребителя в обе стороны', async () => {
    const host = await render({ open: false });
    await act(async () => { head(host).click(); });
    expect(head(host).getAttribute('aria-expanded'), 'после первого клика').toBe('true');
    expect(body(host).style.display, 'открытое тело видно').toBe('block');
    await act(async () => { head(host).click(); });
    expect(head(host).getAttribute('aria-expanded'), 'после второго клика').toBe('false');
    expect(body(host).style.display, 'снова скрыто').toBe('none');
  });

  it('заголовок — настоящая кнопка с доступным именем и aria-связями к региону', async () => {
    const host = await render({ open: true, meta: 'TTFT · медианы' });
    const b = head(host);
    expect(b.tagName).toBe('BUTTON');
    expect(b.textContent).toContain('Точные метрики локальной модели');
    expect(b.textContent).toContain('TTFT · медианы');
    const bodyId = b.getAttribute('aria-controls');
    expect(bodyId, 'aria-controls указывает на id тела').toBeTruthy();
    expect(body(host).getAttribute('id'), 'тело носит этот id').toBe(bodyId);
    expect(body(host).getAttribute('aria-labelledby'), 'регион подписан заголовком').toBe(b.getAttribute('id'));
  });

  it('без children — статичная закрытая строка: ни кнопки, ни chevron, ни пустого тела', async () => {
    const host = await render({ withBody: false, meta: 'нет ходов за сутки' });
    expect(host.querySelector('button'), 'статичная строка не кнопка').toBeFalsy();
    expect(host.querySelector('[aria-expanded]'), 'нет переключателя').toBeFalsy();
    expect(host.querySelector('svg'), 'нет chevron').toBeFalsy();
    expect(host.querySelector('[role=region]'), 'нет пустого тела').toBeFalsy();
    expect(host.textContent).toContain('Точные метрики локальной модели');
    expect(host.textContent).toContain('нет ходов за сутки');
  });

  it('цель заголовка: 40 px на десктопе, 44 px на мобиле — в интерактивной и статичной строке', async () => {
    const desktop = await render({});
    expect(Number.parseFloat(head(desktop).style.minHeight), 'десктоп').toBe(40);
    const mobile = await render({ isMobile: true });
    expect(Number.parseFloat(head(mobile).style.minHeight), 'мобильная шторка').toBe(44);
    const staticHost = await render({ withBody: false, isMobile: true });
    // строка — единственный div внутри оболочки-карточки
    const staticRow = (staticHost.querySelector('div') as HTMLElement).querySelector('div') as HTMLElement;
    expect(Number.parseFloat(staticRow.style.minHeight), 'статичная строка на мобиле').toBe(44);
  });

  it('chevron поворачивается на месте; при prefers-reduced-motion перехода нет', async () => {
    reducedMotion = false;
    const closed = await render({ open: false });
    expect(chevron(closed).style.transform || 'none', 'закрытый chevron не повёрнут').toBe('none');
    await act(async () => { head(closed).click(); });
    expect(chevron(closed).style.transform, 'открытый chevron повёрнут').toContain('rotate(180deg)');
    expect(chevron(closed).style.transition, 'обычный режим — анимированный поворот').toContain('transform');

    reducedMotion = true;
    const calm = await render({ open: true });
    expect(chevron(calm).style.transform, 'поворот остаётся').toContain('rotate(180deg)');
    expect(chevron(calm).style.transition, 'reduced-motion: движение выключено').toBe('none');
    reducedMotion = false;
  });
});
