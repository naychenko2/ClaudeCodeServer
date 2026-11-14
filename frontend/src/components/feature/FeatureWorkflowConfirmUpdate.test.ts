// Тесты карточки «шаблон изменился» (F04): лестница v2 с новым этапом, «Обновить маршрут до vN»
// (confirm с прежним skipped из confirmed), «Оставить vN» (keep-version), закрытое предупреждение.
// Плюс применение записанной версии (Ф-2.15): ответ confirm (ConfirmedDto) становится видом
// блока — строка и граф сразу на новой версии, карточка больше не предлагается.
// Окружение node: mount (fakeDom), каталог и ручки — заглушки workflowTemplatesApi.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureWorkflowConfirm } from './FeatureWorkflowConfirm';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import { newStageIds, updateExplainText, type ConfirmedRoute } from './FeatureWorkflowConfirmModel';
import type { WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from '../../features/workflowTemplates/types';

const api = vi.hoisted(() => ({
  entries: [] as WorkflowTemplateEntry[],
  // Ответ confirm — неизвестный по форме: тест подставляет его значение (mockResolvedValue),
  // по умолчанию — «тела нет» (старый сервер / отказ): блок остаётся на прежнем виде.
  confirm: vi.fn((..._a: unknown[]): Promise<unknown> => Promise.resolve(null)),
  keep: vi.fn((..._a: unknown[]) => Promise.resolve()),
}));
vi.mock('../../features/workflowTemplates/api', () => ({
  workflowTemplatesApi: {
    listTemplates: () => Promise.resolve(api.entries),
    confirmWorkflow: (...a: unknown[]) => api.confirm(...a),
    keepVersion: (...a: unknown[]) => api.keep(...a),
    recommendWorkflow: () => new Promise<never>(() => {}),
  },
}));

const st = (over: Partial<WorkflowStage> & { role: string }): WorkflowStage => ({ presence: 'on', ...over });
const tpl = (version: number, extra: WorkflowStage[] = []): WorkflowTemplate => ({
  id: 'full-cycle', name: 'Полный цикл', version,
  stages: [
    st({ role: 'plan' }),
    st({ role: 'step', lane: 'back' }),
    st({ role: 'qa', presence: 'optional' }),
    ...extra,
    st({ role: 'final' }),
    st({ role: 'merge' }),
  ],
});
const entry = (t: WorkflowTemplate): WorkflowTemplateEntry => ({ scope: 'global', projectId: null, template: t });

// v1: снят optional 'qa'.
const CONFIRMED: ConfirmedRoute = {
  templateId: 'full-cycle', scope: 'global', confirmedVersion: 1, skipped: ['qa'],
  stages: [
    { id: 'plan', role: 'plan', after: [] },
    { id: 'step', role: 'step', lane: 'back', after: ['plan'] },
    { id: 'final', role: 'final', after: ['step'] },
    { id: 'merge', role: 'merge', after: ['final'] },
  ],
};
const NEW_REVIEW = st({ id: 'review', role: 'review', title: 'Общее ревью' });

async function render(confirmed: ConfirmedRoute) {
  const { container } = mount(createElement(FeatureWorkflowConfirm, { featureId: 'f1', confirmed, isMobile: false }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  return container;
}
const text = (c: ReturnType<typeof mount>['container']) => textNodes(c).join(' ');
const click = async (c: ReturnType<typeof mount>['container'], id: string) => {
  const n = findNodeByAttr(c, 'data-testid', id);
  expect(n).not.toBeNull();
  await act(async () => { (reactProps(n!)!.onClick as () => void)(); });
};

describe('FeatureWorkflowConfirm: карточка «шаблон изменился» (F04)', () => {
  // mockReset, а не mockClear: подставленный тестом ответ confirm не должен доживать в следующем.
  beforeEach(() => { installFakeDom(); api.confirm.mockReset(); api.confirm.mockResolvedValue(null); api.keep.mockClear(); });
  afterEach(() => { uninstallFakeDom(); });

  it('показывает заголовок «Имя»: v1 → v2, лестницу и пояснение про новый обязательный этап', async () => {
    api.entries = [entry(tpl(2, [NEW_REVIEW]))];
    const c = await render(CONFIRMED);
    expect(text(c)).toContain('«Полный цикл»: v1 → v2');
    expect(findNodeByAttr(c, 'aria-label', 'Этап «Общее ревью» включён в маршрут, новый этап')).not.toBeNull();
    expect(text(c)).toContain('В v2 «Общее ревью» стала обязательной.');
    expect(text(c)).toContain('Настройки этапов действуют со следующего шага без подтверждения.');
    expect(text(c)).toContain('Обновить маршрут до v2');
    expect(text(c)).toContain('Оставить v1');
    // Обычный редактор под карточкой молчит.
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-submit')).toBeNull();
  });

  it('«Обновить маршрут до v2» подтверждает с прежним skipped из confirmed', async () => {
    api.entries = [entry(tpl(2, [NEW_REVIEW]))];
    const c = await render(CONFIRMED);
    await click(c, 'feature-workflow-confirm-update-apply');
    expect(api.confirm).toHaveBeenCalledTimes(1);
    expect(api.confirm).toHaveBeenCalledWith('f1', { templateId: 'full-cycle', scope: 'global', skipped: ['qa'] });
  });

  it('«Обновить маршрут до v3» применяет записанную версию: строка на v3, карточка исчезает (Ф-2.15)', async () => {
    api.entries = [entry(tpl(3, [NEW_REVIEW]))];
    // Что отдаёт ручка после подтверждения — записанный сервером маршрут (ConfirmedDto на
    // бэке): v3, то же снятие, новый обязательный этап в графе.
    const written: ConfirmedRoute = {
      ...CONFIRMED,
      confirmedVersion: 3,
      stages: [
        { id: 'plan', role: 'plan', after: [] },
        { id: 'step', role: 'step', lane: 'back', after: ['plan'] },
        { id: 'review', role: 'review', after: ['step'] },
        { id: 'final', role: 'final', after: ['review'] },
        { id: 'merge', role: 'merge', after: ['final'] },
      ],
    };
    api.confirm.mockResolvedValue(written);
    const c = await render(CONFIRMED);
    expect(text(c)).toContain('v1 → v3');
    await click(c, 'feature-workflow-confirm-update-apply');
    expect(api.confirm).toHaveBeenCalledTimes(1);
    expect(api.confirm).toHaveBeenCalledWith('f1', { templateId: 'full-cycle', scope: 'global', skipped: ['qa'] });
    // Карточка больше не предлагается: подтверждённая версия догнала версию шаблона.
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-update')).toBeNull();
    // Строка подтверждённого маршрута — на новой версии, сразу, без ожидания task_changed.
    expect(text(c)).toContain('Полный цикл v3');
    expect(text(c)).not.toContain('v1 → v3');
  });

  it('без тела ответа (старый сервер/отказ) вид остаётся прежним: карточка и v1 на месте', async () => {
    api.entries = [entry(tpl(3, [NEW_REVIEW]))];
    const c = await render(CONFIRMED);
    await click(c, 'feature-workflow-confirm-update-apply');
    expect(api.confirm).toHaveBeenCalledTimes(1);
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-update')).not.toBeNull();
    expect(text(c)).toContain('Полный цикл v1');
  });

  it('«Оставить v1» шлёт keep-version с версией шаблона и не подтверждает', async () => {
    api.entries = [entry(tpl(2, [NEW_REVIEW]))];
    const c = await render(CONFIRMED);
    await click(c, 'feature-workflow-confirm-update-keep');
    expect(api.keep).toHaveBeenCalledTimes(1);
    expect(api.keep).toHaveBeenCalledWith('f1', 2);
    expect(api.confirm).not.toHaveBeenCalled();
  });

  it('закрытое предупреждение не возвращается до v3, а с v3 появляется снова', async () => {
    api.entries = [entry(tpl(2, [NEW_REVIEW]))];
    let c = await render({ ...CONFIRMED, dismissedUpdateVersion: 2 });
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-update')).toBeNull();
    uninstallFakeDom(); installFakeDom();
    api.entries = [entry(tpl(3, [NEW_REVIEW]))];
    c = await render({ ...CONFIRMED, dismissedUpdateVersion: 2 });
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-update')).not.toBeNull();
  });
});

describe('модель F04: newStageIds / updateExplainText', () => {
  it('новый этап — только то, чего не было в маршруте; снятый optional не считается', () => {
    expect(newStageIds(CONFIRMED, tpl(2, [NEW_REVIEW]))).toEqual(['review']);
  });
  it('новый optional-этап описан «по решению», а не «обязательной»', () => {
    const t = tpl(2, [st({ id: 'audit', role: 'review', presence: 'optional', title: 'Аудит' })]);
    const s = updateExplainText(CONFIRMED, t, r => r);
    expect(s).toContain('добавлено по решению: «Аудит»');
    expect(s).not.toContain('обязательной');
  });
});
