// Тесты стыка «Подобрать маршрут» (дефект приёмки QA f55676aa, сценарий 4б): бэкенд отвечает
// на recommend обёрткой { proposal: … } (TaskWorkflowController.Recommend), api.ts разворачивает
// её на границе — компонент не знает про обёртку. Мок здесь на уровень ниже, чем в
// FeatureWorkflowConfirm.test.ts (тот подменяет весь модуль api): мокнут только
// lib/offline.request, живой код api.ts проходит, ответ — в реальной форме.
// Без разворота в api.ts оба теста краснеют: компонент получает обёртку и кладёт её в
// localProposal — «Предложение модели» без шаблона, пустой Select, «не найден в каталоге».

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureWorkflowConfirm } from './FeatureWorkflowConfirm';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import type { WorkflowProposal, WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from '../../features/workflowTemplates/types';

// Сеть подменена на уровне request: ответ — в фактической форме ручек (обёртка для
// recommend, список для listTemplates). Остальное — настоящее (та же схема, что
// features/workflowTemplates/workflowTemplateModel.test.ts).
const net = vi.hoisted(() => ({
  entries: [] as WorkflowTemplateEntry[],
  recommend: (() => Promise.resolve({ proposal: null })) as () => Promise<unknown>,
}));
vi.mock('../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/offline')>();
  return {
    ...orig,
    request: (url: string) =>
      url.includes('/workflow/recommend') ? net.recommend() : Promise.resolve(net.entries),
  };
});

const st = (over: Partial<WorkflowStage> & { role: string }): WorkflowStage => ({ presence: 'on', ...over });

const tpl = (id: string, version: number, stages: WorkflowStage[]): WorkflowTemplate => ({ id, name: id, version, stages });

const fullCycle = (): WorkflowTemplate =>
  tpl('full-cycle', 3, [
    st({ role: 'cut', presence: 'optional' }),
    st({ role: 'plan' }),
    st({ role: 'step', lane: 'back' }),
    st({ role: 'review', presence: 'optional' }),
    st({ role: 'qa', presence: 'optional' }),
    st({ role: 'final' }),
    st({ role: 'merge' }),
  ]);

const fastTrack = (): WorkflowTemplate =>
  tpl('fast-track', 5, [
    st({ role: 'cut', presence: 'optional' }),
    st({ role: 'plan' }),
    st({ role: 'step', lane: 'back' }),
    st({ role: 'review', presence: 'optional' }),
    st({ role: 'merge' }),
  ]);

const entry = (template: WorkflowTemplate): WorkflowTemplateEntry => ({ scope: 'global', projectId: null, template });

// Предложение с шаблоном из каталога: с обоснованием и моделью — их показ и проверяем.
const PROPOSED_FULL: WorkflowProposal = {
  templateId: 'full-cycle', scope: 'global', skipped: ['qa'],
  rationale: 'по описанию фичи', model: 'test-model',
};
const PROPOSED_FAST: WorkflowProposal = { templateId: 'fast-track', scope: 'global', skipped: [] };

async function renderBlock(props: { proposal?: WorkflowProposal | null; routeRequired?: boolean }) {
  const { container } = mount(createElement(FeatureWorkflowConfirm, {
    featureId: 'f1',
    proposal: props.proposal,
    isMobile: false,
    routeRequired: props.routeRequired,
  }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return container;
}

const text = (container: ReturnType<typeof mount>['container']): string => textNodes(container).join(' ');

const selectOf = (container: ReturnType<typeof mount>['container']) => findNodeByAttr(container, 'title', 'Шаблон маршрута');

async function clickRecommend(container: ReturnType<typeof mount>['container']) {
  const btn = findNodeByAttr(container, 'data-testid', 'feature-workflow-confirm-recommend');
  expect(btn).not.toBeNull();
  await act(async () => {
    (reactProps(btn!)!.onClick as () => void)();
    await new Promise(r => setTimeout(r, 0));
  });
}

describe('FeatureWorkflowConfirm: стык recommend с реальной формой ответа', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); net.entries = []; net.recommend = () => Promise.resolve({ proposal: null }); });

  it('ответ {proposal:null}: блок остаётся «предложения нет», кнопка «Подобрать ещё раз»', async () => {
    net.entries = [entry(fullCycle())];
    net.recommend = () => Promise.resolve({ proposal: null });
    const c = await renderBlock({ routeRequired: true });
    expect(text(c)).toContain('Модель не предложила маршрут');
    await clickRecommend(c);
    // Пустой ответ — то же состояние «нет предложения», а не «Предложение модели» без шаблона.
    expect(text(c)).toContain('Модель не предложила маршрут');
    expect(text(c)).not.toContain('Предложение модели');
    expect(text(c)).toContain('Подобрать ещё раз');
    expect(reactProps(selectOf(c))!.value).toBe('');
    expect(text(c)).not.toContain('не найден в каталоге');
  });

  it('ответ {proposal:P}: показаны шаблон и обоснование P, а не пустой Select', async () => {
    net.entries = [entry(fullCycle())];
    net.recommend = () => Promise.resolve({ proposal: PROPOSED_FULL });
    const c = await renderBlock({ routeRequired: true });
    await clickRecommend(c);
    // Предложение развёрнуто: его шаблон выбран, его обоснование видно.
    expect(reactProps(selectOf(c))!.value).toBe('global:full-cycle');
    expect(text(c)).toContain('по описанию фичи');
    expect(text(c)).not.toContain('Модель не предложила маршрут');
    expect(text(c)).not.toContain('не найден в каталоге');
  });

  it('новое предложение заменяет предложение эпика — как предложение с шаблоном, а не обёрткой', async () => {
    net.entries = [entry(fullCycle()), entry(fastTrack())];
    net.recommend = () => Promise.resolve({ proposal: PROPOSED_FULL });
    // До подбора показан эпик-пропosal: его шаблон выбран.
    const c = await renderBlock({ proposal: PROPOSED_FAST });
    expect(reactProps(selectOf(c))!.value).toBe('global:fast-track');
    await clickRecommend(c);
    // После подбора — новое предложение (full-cycle с обоснованием), а не обёртка без
    // templateId: без разворота в api она перекрывала бы всё (localProposal ?? proposal).
    expect(reactProps(selectOf(c))!.value).toBe('global:full-cycle');
    expect(text(c)).toContain('по описанию фичи');
    expect(text(c)).not.toContain('не найден в каталоге');
  });
});
