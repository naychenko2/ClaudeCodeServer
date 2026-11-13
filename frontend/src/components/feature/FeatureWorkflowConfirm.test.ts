// Тесты блока подтверждения маршрута (C2, доработка по вердикту R-C2): строка «шаблон
// изменился» — только про правку ПОДТВЁЖДЁННОГО шаблона: выбор другого шаблона в Select
// строки не даёт (это смена выбора, не правка маршрута), а правка подтверждённого шаблона
// при выбранном другом — строка есть, с версиями подтверждённого (v3 → v4). Окружение node:
// mount (fakeDom), каталог — заглушка workflowTemplatesApi; «Подобрать» в этих тестах не
// нажимается (его гонка — в модели, applyRecommend).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureWorkflowConfirm } from './FeatureWorkflowConfirm';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import type { ConfirmedRoute } from './FeatureWorkflowConfirmModel';
import type { WorkflowProposal, WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from '../../features/workflowTemplates/types';

// Каталог подменена: тест выставляет его перед рендером (vi.mock хойстится выше констант).
const catalog = vi.hoisted(() => ({ entries: [] as WorkflowTemplateEntry[] }));
vi.mock('../../features/workflowTemplates/api', () => ({
  workflowTemplatesApi: {
    listTemplates: () => Promise.resolve(catalog.entries),
    confirmWorkflow: () => Promise.resolve(),
    recommendWorkflow: () => new Promise<never>(() => {}),
  },
}));

const st = (over: Partial<WorkflowStage> & { role: string }): WorkflowStage => ({ presence: 'on', ...over });

const tpl = (id: string, version: number, stages: WorkflowStage[]): WorkflowTemplate => ({ id, name: id, version, stages });

// 'full-cycle' v3 — тот же состав, что подтверждённый маршрут ниже ('qa' optional, снят).
const fullCycle = (version: number, extra: WorkflowStage[] = []): WorkflowTemplate =>
  tpl('full-cycle', version, [
    st({ role: 'cut', presence: 'optional' }),
    st({ role: 'plan' }),
    st({ role: 'step', lane: 'back' }),
    st({ role: 'review', presence: 'optional' }),
    st({ role: 'qa', presence: 'optional' }),
    st({ role: 'final' }),
    st({ role: 'merge' }),
    ...extra,
  ]);

// Другой шаблон 'fast-track' v5: состав другой (нет qa/final).
const fastTrack = (): WorkflowTemplate =>
  tpl('fast-track', 5, [
    st({ role: 'cut', presence: 'optional' }),
    st({ role: 'plan' }),
    st({ role: 'step', lane: 'back' }),
    st({ role: 'review', presence: 'optional' }),
    st({ role: 'merge' }),
  ]);

const entry = (template: WorkflowTemplate): WorkflowTemplateEntry => ({ scope: 'global', projectId: null, template });

// Маршрут, подтверждённый по 'full-cycle' v3 (снял optional 'qa').
const CONFIRMED: ConfirmedRoute = {
  templateId: 'full-cycle',
  scope: 'global',
  stages: [
    { id: 'cut', role: 'cut', after: [] },
    { id: 'plan', role: 'plan', after: ['cut'] },
    { id: 'step', role: 'step', lane: 'back', after: ['plan'] },
    { id: 'review', role: 'review', after: ['step'] },
    { id: 'final', role: 'final', after: ['review'] },
    { id: 'merge', role: 'merge', after: ['final'] },
  ],
  skipped: ['qa'],
  confirmedVersion: 3,
};

const PROPOSED_FAST: WorkflowProposal = { templateId: 'fast-track', scope: 'global', skipped: [] };

async function renderBlock(props: { proposal?: WorkflowProposal | null; confirmed?: ConfirmedRoute | null }) {
  const { container } = mount(createElement(FeatureWorkflowConfirm, {
    featureId: 'f1',
    proposal: props.proposal,
    confirmed: props.confirmed ?? null,
    isMobile: false,
  }));
  // listTemplates — мгновенная заглушка: её then (setEntries) догорает на микрозадачах.
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return container;
}

const text = (container: ReturnType<typeof mount>['container']): string => textNodes(container).join(' ');

// Select блока — единственный <select> (title из компонента).
const selectOf = (container: ReturnType<typeof mount>['container']) => findNodeByAttr(container, 'title', 'Шаблон маршрута');

async function selectTemplate(container: ReturnType<typeof mount>['container'], key: string) {
  const sel = selectOf(container);
  expect(sel).not.toBeNull();
  await act(async () => {
    (reactProps(sel!)!.onChange as (e: { target: { value: string } }) => void)({ target: { value: key } });
  });
}

describe('FeatureWorkflowConfirm: строка «шаблон изменился»', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  it('подтверждённый шаблон в каталоге не отредактирован — строки нет', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ confirmed: CONFIRMED });
    expect(text(c)).toContain('маршрут подтверждён');
    expect(text(c)).not.toContain('Шаблон изменился');
  });

  it('выбран другой шаблон (B v5) — строки нет: смена выбора не правка подтверждённого', async () => {
    catalog.entries = [entry(fullCycle(3)), entry(fastTrack())];
    const c = await renderBlock({ confirmed: CONFIRMED });
    await selectTemplate(c, 'global:fast-track');
    // Выбор действительно применён (controlled value Select).
    expect(reactProps(selectOf(c))?.value).toBe('global:fast-track');
    expect(text(c)).not.toContain('Шаблон изменился');
  });

  it('модель предложила другой шаблон (B v5) — ложной строки без действий человека нет', async () => {
    catalog.entries = [entry(fullCycle(3)), entry(fastTrack())];
    const c = await renderBlock({ proposal: PROPOSED_FAST, confirmed: CONFIRMED });
    expect(text(c)).not.toContain('Шаблон изменился');
  });

  it('подтверждённый шаблон отредактирован (A v3 → v4, состав другой) при выбранном другом — строка есть, по версиям подтверждённого', async () => {
    catalog.entries = [entry(fullCycle(4, [st({ id: 'audit', role: 'review', presence: 'optional' })])), entry(fastTrack())];
    const c = await renderBlock({ proposal: PROPOSED_FAST, confirmed: CONFIRMED });
    const t = text(c);
    expect(t).toContain('Шаблон изменился: v3 → v4, обновить маршрут?');
    // Версии подтверждённого шаблона, а не выбранного B (v5): ложного «v3 → v5» нет.
    expect(t).not.toContain('v3 → v5');
  });
});
