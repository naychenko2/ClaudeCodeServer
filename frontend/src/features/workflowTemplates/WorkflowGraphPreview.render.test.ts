import { describe, expect, it, vi } from 'vitest';
import { Children, createElement, isValidElement } from 'react';
import type { ReactElement, ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { WorkflowStage } from './types';

vi.mock('../../lib/breakpoints', async orig => ({ ...(await orig<object>()), useIsMobile: () => false }));

const { WorkflowGraphPreview } = await import('./WorkflowGraphPreview');

const parallelCycle = (): WorkflowStage[] => [
  { role: 'cut', presence: 'on' },
  { role: 'design', presence: 'on' },
  { role: 'plan', presence: 'on' },
  { role: 'qa', presence: 'optional', id: 'qa-plan', after: ['design'] },
  { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
  { role: 'step', presence: 'on', id: 'step-front', lane: 'front', after: ['plan'] },
  { role: 'review', presence: 'on', after: ['step-back', 'step-front'] },
  { role: 'qa', presence: 'on', id: 'qa', after: ['review', 'qa-plan'] },
  { role: 'final', presence: 'on' },
  { role: 'merge', presence: 'on' },
];

const html = (stages: WorkflowStage[], extra: Record<string, unknown> = {}) =>
  renderToStaticMarkup(createElement(WorkflowGraphPreview, { stages, ...extra })).replace(/<!-- -->/g, '');

// Обход дерева элементов: хук useIsMobile замокан, компонент вызывается как функция.
function findAll(node: ReactNode, pred: (e: ReactElement<Record<string, unknown>>) => boolean, out: ReactElement<Record<string, unknown>>[] = []) {
  Children.forEach(node, ch => {
    if (!isValidElement(ch)) return;
    const el = ch as ReactElement<Record<string, unknown>>;
    if (pred(el)) out.push(el);
    findAll(el.props.children as ReactNode, pred, out);
  });
  return out;
}

describe('граф маршрута — разметка (F25)', () => {
  it('«⇉ параллельно» ровно один раз, far-ребро с пунктиром, подпись «Маршрут, который увидит фича»', () => {
    // Ромб plan → {step-back, step-front} → review: одна параллельная колонка — одна рамка.
    const diamond: WorkflowStage[] = [
      { role: 'plan', presence: 'on' },
      { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
      { role: 'step', presence: 'on', id: 'step-front', lane: 'front', after: ['plan'] },
      { role: 'review', presence: 'on', after: ['step-back', 'step-front'] },
    ];
    expect(html(diamond).split('⇉ параллельно').length - 1).toBe(1);
    // В parallel-cycle две параллельные колонки ({plan, qa-plan} и {step-back, step-front}).
    expect(html(parallelCycle()).split('⇉ параллельно').length - 1).toBe(2);
    const h = html(parallelCycle());
    expect(h).toContain('data-far="true"');
    expect(h).toMatch(/data-far="true"[^>]*stroke-dasharray:4 3/);
    expect(h).toContain('aria-label="Маршрут, который увидит фича"');
  });

  it('этап с presence=off в DOM нет', () => {
    const stages = parallelCycle().map(s => (s.role === 'design' ? { ...s, presence: 'off' } : s));
    expect(html(stages)).not.toContain('data-node="design"');
    expect(html(parallelCycle())).toContain('data-node="design"');
  });

  it('несъёмный узел — замок, необязательный — «?» и пунктир, у qa — mono-метка «qa»', () => {
    const h = html(parallelCycle(), { selectedKey: null });
    const node = (k: string) => h.match(new RegExp(`<button[^>]*data-node="${k}"[\\s\\S]*?</button>`))?.[0] ?? '';
    expect(node('plan')).toContain('aria-label="нельзя снять"');
    expect(node('qa-plan')).not.toContain('нельзя снять');
    expect(node('qa-plan')).toContain('>?<');
    expect(node('qa-plan')).toContain('dashed');
    expect(node('qa')).toMatch(/JetBrains|monospace|font-family/);
    expect(node('qa')).toContain('>qa<');
  });

  it('клик по узлу зовёт onSelectStage(key), выбранный узел — рамка accent', () => {
    const onSelect = vi.fn();
    const tree = (WorkflowGraphPreview as unknown as (p: unknown) => ReactElement)({
      stages: parallelCycle(), selectedKey: 'plan', onSelectStage: onSelect,
    });
    const btn = (k: string) => findAll(tree, e => e.props['data-node'] === k)[0];
    (btn('step-back').props.onClick as () => void)();
    expect(onSelect).toHaveBeenCalledWith('step-back');
    expect(btn('plan').props['aria-pressed']).toBe(true);
    expect(btn('step-back').props['aria-pressed']).toBe(false);
    expect(JSON.stringify((btn('plan').props.style as { border: string }).border)).toContain('2px solid');
    expect(html(parallelCycle(), { selectedKey: 'plan' })).toMatch(/data-node="plan"[^>]*border:2px solid var\(--/);
  });
});
