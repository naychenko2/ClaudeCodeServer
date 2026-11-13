// Шторка «Маршрут фичи» (F05): заголовок и липкий подвал. Modal (портал) подменён заглушкой,
// которая отдаёт title и footer — проверяем то, что шторка передаёт ему.

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { WorkflowRouteSheet } from './WorkflowRouteSheet';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';

vi.mock('../../components/ui', async () => {
  const { createElement: h } = await import('react');
  return {
    Modal: ({ title, footer, children }: { title: string; footer: unknown; children: unknown }) =>
      h('div', null, h('h2', null, title), h('div', { 'data-testid': 'body' }, children as never), h('div', { 'data-testid': 'foot' }, footer as never)),
  };
});

describe('WorkflowRouteSheet', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  it('заголовок «Маршрут фичи», кнопки — в липком подвале (position: sticky), тело отдельно', () => {
    const { container } = mount(createElement(WorkflowRouteSheet, {
      onClose: () => {}, footer: createElement('button', null, 'Подтвердить маршрут'),
      children: createElement('span', null, 'лестница'),
    }));
    expect(textNodes(container)).toContain('Маршрут фичи');
    const footer = findNodeByAttr(container, 'data-testid', 'workflow-route-sheet-footer')!;
    expect((reactProps(footer)!.style as { position: string }).position).toBe('sticky');
    expect(textNodes(footer)).toContain('Подтвердить маршрут');
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'body')!)).not.toContain('Подтвердить маршрут');
  });
});
