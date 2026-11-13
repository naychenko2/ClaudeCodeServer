// Мобильный вид блока подтверждения маршрута (F05): компактная строка «ждёт вас» с счётом
// этапов и «Посмотреть», шторка «Маршрут фичи» с липким подвалом по состоянию.
// Шторка (Modal через портал) подменена обёрткой с отдельным подвалом: проверяем состав
// кнопок и их место (тело / подвал), а не сам Modal.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureWorkflowConfirm } from './FeatureWorkflowConfirm';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import type { ConfirmedRoute } from './FeatureWorkflowConfirmModel';
import type { WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from '../../features/workflowTemplates/types';

const catalog = vi.hoisted(() => ({ entries: [] as WorkflowTemplateEntry[] }));
vi.mock('../../features/workflowTemplates/api', () => ({
  workflowTemplatesApi: {
    listTemplates: () => Promise.resolve(catalog.entries),
    confirmWorkflow: () => Promise.resolve(),
    recommendWorkflow: () => new Promise<never>(() => {}),
    keepVersion: () => Promise.resolve(),
  },
}));
vi.mock('./WorkflowRouteSheet', async () => {
  const { createElement: h } = await import('react');
  return {
    WorkflowRouteSheet: ({ children, footer }: { children: unknown; footer: unknown }) =>
      h('div', { 'data-testid': 'sheet' },
        h('div', { 'data-testid': 'sheet-body' }, children as never),
        h('div', { 'data-testid': 'sheet-footer' }, footer as never)),
  };
});

const st = (over: Partial<WorkflowStage> & { role: string }): WorkflowStage => ({ presence: 'on', ...over });
const tpl = (version: number, extra: WorkflowStage[] = []): WorkflowTemplate => ({
  id: 'full-cycle', name: 'Полный цикл', version,
  stages: [
    st({ role: 'plan' }), st({ role: 'step', lane: 'back' }),
    st({ role: 'qa', presence: 'optional' }), st({ role: 'final' }), st({ role: 'merge' }), ...extra,
  ],
});
const entry = (t: WorkflowTemplate): WorkflowTemplateEntry => ({ scope: 'global', projectId: null, template: t });
const CONFIRMED: ConfirmedRoute = {
  templateId: 'full-cycle', scope: 'global', stages: [], skipped: ['qa'], confirmedVersion: 3,
};

type C = ReturnType<typeof mount>['container'];
const text = (c: C) => textNodes(c).join(' ');
const byId = (c: C, id: string) => findNodeByAttr(c, 'data-testid', id);

async function render(props: { proposal?: { templateId: string; scope: 'global'; skipped: string[] } | null; confirmed?: ConfirmedRoute | null; routeRequired?: boolean }) {
  const { container } = mount(createElement(FeatureWorkflowConfirm, {
    featureId: 'f1', proposal: props.proposal, confirmed: props.confirmed ?? null,
    routeRequired: props.routeRequired, isMobile: true, quiet: true,
  }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return container;
}
const open = async (c: C) => {
  await act(async () => { (reactProps(byId(c, 'feature-workflow-confirm-mobile-open')!)!.onClick as () => void)(); });
};

describe('FeatureWorkflowConfirm: мобильная строка и шторка (F05)', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  it('предложение: строка «ждёт вас», «Имя · N из M этапов», «Посмотреть»; шторка закрыта', async () => {
    catalog.entries = [entry(tpl(3))];
    const c = await render({ proposal: { templateId: 'full-cycle', scope: 'global', skipped: ['qa'] } });
    const row = text(c);
    expect(row).toContain('ждёт вас');
    expect(row).toContain('Полный цикл · 4 из 5 этапов');
    expect(row).toContain('Посмотреть');
    expect(byId(c, 'sheet')).toBeNull();
  });

  it('«Посмотреть» открывает шторку: подвал — «Подтвердить маршрут» и «Подобрать заново»', async () => {
    catalog.entries = [entry(tpl(3))];
    const c = await render({ proposal: { templateId: 'full-cycle', scope: 'global', skipped: [] } });
    await open(c);
    const footer = text(byId(c, "sheet-footer") as unknown as C);
    expect(footer).toContain('Подтвердить маршрут');
    expect(footer).toContain('Подобрать заново');
    expect(byId(c, 'feature-workflow-confirm-submit')).not.toBeNull();
  });

  it('«шаблон изменился»: строка с тегом, подвал — «Обновить маршрут до v4» и «Оставить v3»', async () => {
    catalog.entries = [entry(tpl(4, [st({ id: 'audit', role: 'review', presence: 'optional' })]))];
    const c = await render({ confirmed: CONFIRMED });
    expect(text(c)).toContain('шаблон изменился');
    await open(c);
    const footer = text(byId(c, "sheet-footer") as unknown as C);
    expect(footer).toContain('Обновить маршрут до v4');
    expect(footer).toContain('Оставить v3');
    expect(footer).not.toContain('Подтвердить маршрут');
  });

  it('нет предложения: подвал шторки — единая подпись «Подобрать заново» (не «Подобрать ещё раз»)', async () => {
    catalog.entries = [entry(tpl(3))];
    const c = await render({ routeRequired: true });
    await open(c);
    const t = text(c);
    expect(t).toContain('Подобрать заново');
    expect(t).not.toContain('Подобрать ещё раз');
  });
});
