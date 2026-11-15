// Тесты блока подтверждения маршрута (C2, доработка по вердикту R-C2): строка «шаблон
// изменился» — только про правку ПОДТВЁЖДЁННОГО шаблона: выбор другого шаблона в Select
// строки не даёт (это смена выбора, не правка маршрута), а правка подтверждённого шаблона
// при выбранном другом — строка есть, с версиями подтверждённого (v3 → v4). Плюс показ
// имён этапов (дефект 56a684c0): имя из шаблона — имя строки и основа aria-label, фолбэк
// без имени — прежний (подпись роли + машинный id). Окружение node: mount (fakeDom),
// каталог — заглушка workflowTemplatesApi; «Подобрать» в этих тестах не нажимается
// (его гонка — в модели, applyRecommend).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureWorkflowConfirm } from './FeatureWorkflowConfirm';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';
import type { ConfirmedRoute } from './FeatureWorkflowConfirmModel';
import type { WorkflowProposal, WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from '../../features/workflowTemplates/types';

// Каталог подменена: тест выставляет его перед рендером (vi.mock хойстится выше констант).
const catalog = vi.hoisted(() => ({
  entries: [] as WorkflowTemplateEntry[],
  // Задержка каталога: не null — listTemplates ждёт её (тест держит загрузку «в полёте»).
  listGate: null as Promise<void> | null,
  // Ответ «Подобрать»: по умолчанию не завершается; тест подставляет управляемый промис.
  recommend: (() => new Promise<never>(() => {})) as () => Promise<unknown>,
}));
vi.mock('../../features/workflowTemplates/api', () => ({
  workflowTemplatesApi: {
    listTemplates: () => (catalog.listGate ?? Promise.resolve()).then(() => catalog.entries),
    confirmWorkflow: () => Promise.resolve(),
    recommendWorkflow: () => catalog.recommend(),
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

// «Параллельный цикл» — спорный случай дефекта 56a684c0: два qa-этапа, различимых только
// именем (с именами — как шаблон с 14777aa1f; без — легаси-состав для проверки фолбэка).
const parallelCycle = (withTitles: boolean): WorkflowTemplate =>
  tpl('parallel-cycle', 2, [
    st({ role: 'plan' }),
    st({ id: 'qa-plan', role: 'qa', presence: 'optional', ...(withTitles ? { title: 'Проверка плана' } : {}) }),
    st({ id: 'qa', role: 'qa', presence: 'optional', ...(withTitles ? { title: 'Проверка' } : {}) }),
    st({ role: 'final' }),
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

async function renderBlock(props: { proposal?: WorkflowProposal | null; confirmed?: ConfirmedRoute | null; routeRequired?: boolean; quiet?: boolean }) {
  const { container } = mount(createElement(FeatureWorkflowConfirm, {
    featureId: 'f1',
    proposal: props.proposal,
    confirmed: props.confirmed ?? null,
    isMobile: false,
    routeRequired: props.routeRequired,
    quiet: props.quiet,
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
    expect(text(c)).toContain('full-cycle v3 · снято: qa · подтвердил человек');
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
    expect(t).toContain('шаблон изменился');
    expect(t).toContain('v3 → v4');
    // Версии подтверждённого шаблона, а не выбранного B (v5): ложного «v3 → v5» нет.
    expect(t).not.toContain('v3 → v5');
  });
});

describe('FeatureWorkflowConfirm: имена этапов в списке (дефект 56a684c0)', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  it('шаблон с именами: имя — имя строки и основа aria-label, параллельные этапы одной роли различимы', async () => {
    catalog.entries = [entry(parallelCycle(true))];
    const c = await renderBlock({ proposal: { templateId: 'parallel-cycle', scope: 'global', skipped: [] } });
    // Имя из шаблона видно в строке (роль и id — серой подписью рядом, как в редакторе).
    expect(text(c)).toContain('Проверка плана');
    // Оба съёмных qa-этапа опознаются именем, а не машинным id.
    expect(findNodeByAttr(c, 'aria-label', 'Этап «Проверка плана» включён в маршрут')).not.toBeNull();
    expect(findNodeByAttr(c, 'aria-label', 'Этап «Проверка» включён в маршрут')).not.toBeNull();
  });

  it('этап без имени — фолбэк прежний: подпись роли и машинный id', async () => {
    catalog.entries = [entry(parallelCycle(false))];
    const c = await renderBlock({ proposal: { templateId: 'parallel-cycle', scope: 'global', skipped: [] } });
    // Имени нет → чип называется подписью роли «Проверка».
    expect(findNodeByAttr(c, 'aria-label', 'Этап «Проверка» включён в маршрут')).not.toBeNull();
  });
});

describe('FeatureWorkflowConfirm: состояние «предложения нет» и строка подтверждённого (M5)', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  it('none в тихом режиме: блок виден, текст, без предвыбора, подтверждение выключено, кнопка «Подобрать ещё раз»', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ routeRequired: true, quiet: true });
    expect(text(c)).toContain('Модель не предложила маршрут');
    expect(reactProps(selectOf(c))?.value).toBe('');
    expect(reactProps(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-submit')!)?.disabled).toBe(true);
    expect(text(c)).toContain('Подобрать ещё раз');
  });

  it('none: выбор шаблона включает «Подтвердить маршрут»', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ routeRequired: true });
    await selectTemplate(c, 'global:full-cycle');
    expect(reactProps(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-submit')!)?.disabled).toBeFalsy();
  });

  it('легаси без маркера и предложения — «предложения нет» не показывается', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({});
    expect(text(c)).not.toContain('Модель не предложила маршрут');
    expect(text(c)).toContain('Подобрать маршрут');
  });

  it('тихий режим + подтверждённый: только строка и «Сменить»; клик разворачивает полный блок', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ confirmed: CONFIRMED, quiet: true });
    expect(text(c)).toContain('full-cycle v3 · снято: qa · подтвердил человек');
    expect(selectOf(c)).toBeNull();
    const change = findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-change')!;
    await act(async () => { (reactProps(change)!.onClick as () => void)(); });
    expect(selectOf(c)).not.toBeNull();
  });

  it('автоподтверждение моделью — строка «выбрано моделью — можно сменить»', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ confirmed: { ...CONFIRMED, autoConfirmed: true }, quiet: true });
    expect(text(c)).toContain('выбрано моделью автоматически — выбор был очевиден');
    expect(text(c)).toContain('авто');
  });

  it('перерисовка с none на confirmed: строка подтверждённого, Select скрыт (тихий режим)', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const mk = (confirmed: ConfirmedRoute | null) => createElement(FeatureWorkflowConfirm, {
      featureId: 'f1', confirmed, routeRequired: true, quiet: true, isMobile: false,
    });
    const { container, root } = mount(mk(null));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(selectOf(container)).not.toBeNull();
    await act(async () => { root.render(mk(CONFIRMED)); });
    expect(selectOf(container)).toBeNull();
    expect(text(container)).toContain('подтвердил человек');
  });
});

describe('FeatureWorkflowConfirm: гонки и загрузка каталога (хвосты R-M5)', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); catalog.listGate = null; catalog.recommend = () => new Promise<never>(() => {}); });

  it('смена фичи во время подбора сбрасывает развёрнутый блок, поздний ответ не применяется', async () => {
    catalog.entries = [entry(fullCycle(3)), entry(fastTrack())];
    let resolveRecommend!: (p: WorkflowProposal) => void;
    catalog.recommend = () => new Promise<WorkflowProposal>(r => { resolveRecommend = r; });
    const mk = (featureId: string) => createElement(FeatureWorkflowConfirm, {
      featureId, confirmed: CONFIRMED, quiet: true, isMobile: false,
    });
    const { container, root } = mount(mk('f1'));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    // Разворачиваем блок и запускаем подбор для f1.
    await act(async () => { (reactProps(findNodeByAttr(container, 'data-testid', 'feature-workflow-confirm-change')!)!.onClick as () => void)(); });
    expect(selectOf(container)).not.toBeNull();
    await act(async () => { (reactProps(findNodeByAttr(container, 'data-testid', 'feature-workflow-confirm-recommend')!)!.onClick as () => void)(); });
    // Человек перешёл на другую фичу; пока подбор f1 в полёте, busy держит блок на экране.
    await act(async () => { root.render(mk('f2')); });
    // Поздний ответ по f1 приходит уже в f2: предложение не применяется, а развёрнутость
    // сброшена сменой фичи — после конца запроса блок снова свёрнут в строку.
    await act(async () => { resolveRecommend(PROPOSED_FAST); await new Promise(r => setTimeout(r, 0)); });
    expect(text(container)).not.toContain('Предложение модели');
    expect(selectOf(container)).toBeNull();
  });

  it('пока каталог грузится, строка подтверждённого не рисуется (без мигания id → имя)', async () => {
    catalog.entries = [entry(fullCycle(3))];
    let release!: () => void;
    catalog.listGate = new Promise<void>(r => { release = r; });
    const { container } = mount(createElement(FeatureWorkflowConfirm, {
      featureId: 'f1', confirmed: CONFIRMED, quiet: true, isMobile: false,
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(text(container)).not.toContain('full-cycle');
    expect(findNodeByAttr(container, 'data-testid', 'feature-workflow-confirm-line')).toBeNull();
    await act(async () => { release(); await new Promise(r => setTimeout(r, 0)); });
    expect(text(container)).toContain('full-cycle v3 · снято: qa · подтвердил человек');
  });
});

describe('FeatureWorkflowConfirm: состояния блока (F03)', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); catalog.recommend = () => new Promise<never>(() => {}); });

  const proposed = (over: Partial<WorkflowProposal> = {}): WorkflowProposal => ({
    templateId: 'full-cycle', scope: 'global', skipped: [], state: 'proposed',
    rationale: 'Фича меняет бэкенд и экраны', model: 'Flash', proposedAt: '2026-11-12T18:02:00', ...over,
  });
  const byTestId = (c: ReturnType<typeof mount>['container'], id: string) => findNodeByAttr(c, 'data-testid', id);

  it('needsHuman=true: тег «ждёт вас» и причина сомнения; список этапов — лестница', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ proposal: proposed({ needsHuman: true, doubtReason: 'ambiguous' }) });
    expect(text(c)).toContain('ждёт вас');
    expect(text(c)).toContain('Маршрут предлагает модель');
    expect(byTestId(c, 'feature-workflow-confirm-doubt')).not.toBeNull();
    expect(text(c)).toContain('несколько шаблонов подходят одинаково');
    // Этапы рисует WorkflowLadder: колонки и чипы, а не строки с Checkbox справа.
    expect(byTestId(c, 'workflow-ladder')).not.toBeNull();
    expect(byTestId(c, 'workflow-ladder-column')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-ladder-chip', 'plan')).not.toBeNull();
  });

  it('needsHuman=false или не задан: тега «ждёт вас» нет', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c1 = await renderBlock({ proposal: proposed({ needsHuman: false }) });
    expect(text(c1)).not.toContain('ждёт вас');
    expect(byTestId(c1, 'feature-workflow-confirm-doubt')).toBeNull();
    const c2 = await renderBlock({ proposal: proposed() });
    expect(text(c2)).not.toContain('ждёт вас');
  });

  it('предложение: обоснование цитатой с подписью «Flash · дата · обоснование модели»; «Шаблон» и опции «Имя · область · vN»', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ proposal: proposed() });
    expect(text(c)).toContain('Фича меняет бэкенд и экраны');
    expect(text(c)).toContain('Flash · 12.11 18:02 · обоснование модели');
    expect(text(c)).toContain('Шаблон');
    expect(text(c)).toContain('full-cycle · глобальный · v3');
    expect(text(c)).toContain('Подобрать заново');
    expect(text(c)).toContain('Пока не подтвердите, исполнители фичи не стартуют.');
  });

  it('подтверждено человеком: «✓ маршрут», «подтвердил человек», «Почему так» раскрывает confirmed.rationale', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ confirmed: { ...CONFIRMED, rationale: 'Бэк и экраны — подходит этот шаблон' }, quiet: true });
    expect(text(c)).toContain('✓ маршрут');
    expect(text(c)).toContain('подтвердил человек');
    expect(text(c)).not.toContain('авто');
    expect(text(c)).not.toContain('Бэк и экраны — подходит этот шаблон');
    await act(async () => { (reactProps(byTestId(c, 'feature-workflow-confirm-why')!)!.onClick as () => void)(); });
    expect(text(c)).toContain('Бэк и экраны — подходит этот шаблон');
  });

  it('подтверждено моделью: тег «авто» и «выбрано моделью автоматически — выбор был очевиден»; без снятых — «все этапы»', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ confirmed: { ...CONFIRMED, skipped: [], autoConfirmed: true }, quiet: true });
    expect(text(c)).toContain('авто');
    expect(text(c)).toContain('full-cycle v3 · все этапы');
    expect(text(c)).toContain('выбрано моделью автоматически — выбор был очевиден');
  });

  it('смена шаблона человеком: тег «сменили вы», обоснования модели в DOM нет', async () => {
    catalog.entries = [entry(fullCycle(3)), entry(fastTrack())];
    const c = await renderBlock({ proposal: proposed() });
    expect(text(c)).toContain('Фича меняет бэкенд и экраны');
    expect(text(c)).not.toContain('сменили вы');
    await selectTemplate(c, 'global:fast-track');
    expect(text(c)).toContain('сменили вы');
    expect(text(c)).not.toContain('Фича меняет бэкенд и экраны');
    expect(text(c)).toContain('обоснование к нему не относится');
  });

  it('proposal.state=none: «Модель не предложила маршрут», без шаблона «Подтвердить» выключена и рядом «Сначала шаблон.»', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ proposal: { state: 'none', templateId: '', scope: 'global', skipped: [] }, routeRequired: true });
    expect(text(c)).toContain('Модель не предложила маршрут');
    expect(text(c)).toContain('Сначала шаблон.');
    expect(reactProps(byTestId(c, 'feature-workflow-confirm-submit')!)?.disabled).toBe(true);
    expect(text(c)).toContain('Этапы появятся здесь, когда вы выберете шаблон');
    await selectTemplate(c, 'global:full-cycle');
    expect(text(c)).not.toContain('Сначала шаблон.');
    expect(reactProps(byTestId(c, 'feature-workflow-confirm-submit')!)?.disabled).toBeFalsy();
  });

  it('идущий подбор: «Подбираем», «Выбрать вручную» открывает выбор шаблона', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ routeRequired: true });
    await act(async () => { (reactProps(byTestId(c, 'feature-workflow-confirm-recommend')!)!.onClick as () => void)(); });
    expect(text(c)).toContain('Подбираем маршрут');
    expect(selectOf(c)).toBeNull();
    await act(async () => { (reactProps(byTestId(c, 'feature-workflow-confirm-manual')!)!.onClick as () => void)(); });
    expect(selectOf(c)).not.toBeNull();
  });
});

describe('FeatureWorkflowConfirm: пояснение смысла предложения (Ф-2.42)', () => {
  beforeEach(() => { installFakeDom(); });
  afterEach(() => { uninstallFakeDom(); });

  const hint = (c: ReturnType<typeof mount>['container']) =>
    findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm-plan-hint');

  it('предложение модели: строка только про смысл цвета; фразы про невыполнение этапов в карточке нет (дубль убран)', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ proposal: { templateId: 'full-cycle', scope: 'global', skipped: [], state: 'proposed' } });
    expect(hint(c)).not.toBeNull();
    const t = text(c);
    expect(t).toContain('Фиолетовый цвет означает: это предложение модели.');
    // Про невыполнение этапов в карточке больше не говорится — это подпись под кнопкой, она осталась.
    expect(t).not.toContain('Пока маршрут не подтверждён, предложенные этапы не выполняются.');
    expect(t).toContain('Пока не подтвердите, исполнители фичи не стартуют.');
  });

  it('человек сменил шаблон: строка есть, но про выбор человека, а не про модель', async () => {
    catalog.entries = [entry(fullCycle(3)), entry(fastTrack())];
    const c = await renderBlock({ proposal: { templateId: 'full-cycle', scope: 'global', skipped: [], state: 'proposed' } });
    await selectTemplate(c, 'global:fast-track');
    expect(hint(c)).not.toBeNull();
    const t = text(c);
    expect(t).toContain('Фиолетовый цвет означает: ваш выбор шаблона.');
    expect(t).not.toContain('это предложение модели');
  });

  it('подтверждено: строки нет — ни в полном блоке, ни в развёрнутом тихом', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c1 = await renderBlock({ confirmed: CONFIRMED });
    expect(hint(c1)).toBeNull();
    const c2 = await renderBlock({ confirmed: CONFIRMED, quiet: true });
    await act(async () => { (reactProps(findNodeByAttr(c2, 'data-testid', 'feature-workflow-confirm-change')!)!.onClick as () => void)(); });
    expect(hint(c2)).toBeNull();
  });

  it('«предложения нет» и «подбираем»: строки нет', async () => {
    catalog.entries = [entry(fullCycle(3))];
    const c = await renderBlock({ proposal: { state: 'none', templateId: '', scope: 'global', skipped: [] }, routeRequired: true });
    expect(hint(c)).toBeNull();
    const c2 = await renderBlock({ routeRequired: true });
    await act(async () => { (reactProps(findNodeByAttr(c2, 'data-testid', 'feature-workflow-confirm-recommend')!)!.onClick as () => void)(); });
    expect(text(c2)).toContain('Подбираем маршрут');
    expect(hint(c2)).toBeNull();
  });
});
