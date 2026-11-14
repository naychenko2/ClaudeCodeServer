// Состояние строки ленты при перестановке стрелкой (F20a): выбор и фокус ручки.
// Реальный рендер лент и строк через react-dom/client поверх linkedom (jsdom в проекте нет).
// Ограничение окружения: linkedom не моделирует сброс фокуса при insertBefore и не ведёт
// activeElement, поэтому «браузер сбросил фокус» тут не воспроизвести. Проверяем то, что
// зависит от нашего кода: узел строки/ручки тот же (React его двигает, а не пересоздаёт),
// выбор жив, и после перестановки ручке вызван focus() — именно он спасает фокус в браузере.
import { describe, expect, it, vi } from 'vitest';
import { act, createElement, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { WorkflowStage } from './types';
import { moveStage } from './workflowGraphModel';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
  requestAnimationFrame: (cb: () => void) => setTimeout(cb, 0),
  cancelAnimationFrame: (id: ReturnType<typeof setTimeout>) => clearTimeout(id),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { WorkflowStageStrip } = await import('./WorkflowStageStrip');

const initial = (): WorkflowStage[] => [
  { role: 'cut', presence: 'on' },
  { role: 'plan', presence: 'on', after: ['cut'] },
  { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
];

function Host() {
  const [stages, setStages] = useState(initial());
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  return createElement(WorkflowStageStrip, {
    stages, readOnly: false, specialties: [], personas: [], errorsFor: () => [],
    onStagesChange: setStages, selectedKey, onSelectStage: setSelectedKey,
  });
}

const rowOf = (i: number) => doc.querySelector(`[data-stage-index="${i}"]`) as HTMLElement;
const gripOf = (i: number) => rowOf(i).querySelector('[data-stage-grip]') as HTMLElement;
const headOf = (i: number) => rowOf(i).querySelector('[aria-pressed]') as HTMLElement;
const labelOf = (i: number) => headOf(i).getAttribute('aria-label');

async function setup() {
  const root = createRoot(doc.getElementById('root')!);
  await act(async () => { root.render(createElement(Host)); });
  return root;
}

async function arrow(el: HTMLElement, key: 'ArrowDown' | 'ArrowUp') {
  const ev = new win.Event('keydown', { bubbles: true, cancelable: true }) as Event & { key: string };
  ev.key = key;
  await act(async () => { el.dispatchEvent(ev); });
}

describe('лента этапов — состояние строки при перестановке стрелкой', () => {
  it('ArrowDown: выбранная строка остаётся выбранной, узлы те же, ручке возвращается фокус', async () => {
    const root = await setup();
    const cutRow = rowOf(0);
    const cutGrip = gripOf(0);
    await act(async () => { headOf(0).dispatchEvent(new win.Event('click', { bubbles: true })); });
    expect(headOf(0).getAttribute('aria-pressed')).toBe('true');

    let focused = 0;
    cutGrip.addEventListener('focus', () => { focused++; });
    await arrow(cutGrip, 'ArrowDown');

    // «Разрез» теперь вторым; тот же узел строки и ручки, выбор не потерян.
    expect(labelOf(1)).toContain('Разрез');
    expect(rowOf(1)).toBe(cutRow);
    expect(gripOf(1)).toBe(cutGrip);
    expect(headOf(1).getAttribute('aria-pressed')).toBe('true');
    // Соседняя строка не выбрана по наследству позиции.
    expect(headOf(0).getAttribute('aria-pressed')).toBe('false');
    // Браузер сбросит фокус при insertBefore — код обязан вернуть его на ту же ручку.
    expect(focused).toBe(1);
    await act(async () => { root.unmount(); });
  });

  it('ArrowUp: то же для движения вверх, ключ — id этапа, а не позиция', async () => {
    const root = await setup();
    const row = rowOf(2);
    const grip = gripOf(2);
    await act(async () => { headOf(2).dispatchEvent(new win.Event('click', { bubbles: true })); });
    let focused = 0;
    grip.addEventListener('focus', () => { focused++; });
    await arrow(grip, 'ArrowUp');
    expect(rowOf(1)).toBe(row);
    expect(gripOf(1)).toBe(grip);
    expect(headOf(1).getAttribute('aria-pressed')).toBe('true');
    expect(focused).toBe(1);
    expect(moveStage(initial(), 2, 1)[1].id ?? moveStage(initial(), 2, 1)[1].role).toBe('step-back');
    await act(async () => { root.unmount(); });
  });
});
