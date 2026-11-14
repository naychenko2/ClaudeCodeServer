// Содержимое области этапа (F11): плитки KPI (время с разбивкой, ходы, токены, «ждёт вас», «≈» у легаси),
// тег «N возврат», строка задачи ветки (исполнитель · минуты · «принято»), заметка о параллельной ветке
// со ссылкой-выбором узла, пустое состояние.
//
// Мутации (в отчёте): (1) убрать `${approx}` из значения «Время» → тест «≈» красный; (2) не рисовать плитку
// «Ждёт вас» → тест «четыре плитки» красный; (3) не передавать note.stageId в onSelectStage → тест ссылки
// красный; (4) убрать returns > 0 в теге → тест «1 возврат» красный.

import { createElement } from 'react';
import { act } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStageAreaView, FeatureStageArea } from './FeatureStageArea';
import { kpiTiles } from './FeatureStageKpi';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureStage, FeatureTask } from '../../types/featureRun';
import type { StageSummary } from '../../types/featureStage';

const { stageMock } = vi.hoisted(() => ({ stageMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, tasks: { ...orig.api.tasks, stage: stageMock } } };
});

beforeEach(() => { installFakeDom(); stageMock.mockReset(); });
afterEach(() => uninstallFakeDom());

const stage = { id: 'step-b', key: 'prod', state: 'active', taskIds: ['t1'], title: 'Шаги', line: 'back', returns: 0 } as unknown as FeatureStage;
const task = { id: 't1', title: 'Модель', stepRef: 'B1', status: 'done' } as unknown as FeatureTask;

const summary = (over: Partial<StageSummary> = {}): StageSummary => ({
  executorSessionId: null,
  executor: { personaId: 'p', name: 'Денис', roleTitle: 'Backend', modelLabel: 'sonnet', modelIsOwnCell: false, silenceMinutes: 15, silenceAction: 'notify' },
  templateName: 'Стандарт', templateVersion: 3, returns: 1,
  kpi: { totalMinutes: 70, execMinutes: 40, reviewMinutes: 20, waitedHumanMinutes: 10, turns: 12, tokens: { input: 1000, output: 500, cache: 40000 }, approx: false },
  tasks: [{ taskId: 't1', stepRef: 'B1', title: 'Модель', state: 'done', executor: { name: 'Денис' }, minutes: 25, verdict: 'accepted', returns: 1 }],
  parallelNote: { stageId: 'step-f', title: 'Шаги · фронт', doneCount: 1, totalCount: 2, personaName: 'Полина' },
  ...over,
});

const view = (sum: StageSummary | null, extra: Record<string, unknown> = {}) =>
  mount(createElement(FeatureStageAreaView, { stage, tasks: [task], summary: sum, isMobile: false, ...extra }));

describe('область этапа: содержимое', () => {
  it('четыре плитки KPI, разбивка «исп · ревью · вы», тег «1 возврат»', () => {
    const { container } = view(summary());
    for (const k of ['time', 'turns', 'tokens', 'wait']) expect(findNodeByAttr(container, 'data-testid', `stage-kpi-${k}`), k).not.toBeNull();
    const all = textNodes(container).join('|');
    expect(all).toContain('исп 40 мин · ревью 20 мин · вы 10 мин');
    expect(all).toContain('кэш 40 тыс.');
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'feature-stage-area-returns')!).join('')).toBe('1 возврат');
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'feature-stage-area-caption')!).join(''))
      .toBe('Исполнители из плана · шаблон «Стандарт» v3 · этап: молчит 15 мин → только сообщить');
  });

  it('легаси (approx): «≈» перед временем; нет возвратов — тега нет', () => {
    const k = summary().kpi!;
    const tiles = kpiTiles({ ...k, approx: true });
    expect(tiles[0].value.startsWith('≈ ')).toBe(true);
    const { container } = view(summary({ returns: 0, kpi: { ...k, approx: true } }));
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'stage-kpi-time-value')!).join('')).toMatch(/^≈ /);
    expect(findNodeByAttr(container, 'data-testid', 'feature-stage-area-returns')).toBeNull();
    expect(kpiTiles({ ...k, approx: true, totalMinutes: 0, tokens: null })[2].value).toBe('нет замера');
  });

  it('строка задачи ветки: исполнитель, минуты, «принято», ↺1, ✓', () => {
    const { container } = view(summary());
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'stage-task-caption-t1')!).join('')).toBe('Денис · 25 мин · принято');
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'stage-task-returns-t1')!).join('')).toBe('↺1');
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'stage-task-mark-t1')!).join('')).toBe('✓');
  });

  it('заметка о параллельной ветке; клик по ссылке выбирает соседний узел', () => {
    const onSelectStage = vi.fn();
    const { container } = view(summary(), { onSelectStage });
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'feature-stage-area-parallel')!).join('')).toContain('Параллельно идёт Шаги · фронт');
    act(() => { (reactProps(findNodeByAttr(container, 'data-testid', 'feature-stage-area-parallel-link')!)!.onClick as () => void)(); });
    expect(onSelectStage).toHaveBeenCalledWith('step-f');
  });

  it('без сводки — только задачи из run, без KPI и заметки', () => {
    const { container } = view(null);
    expect(findNodeByAttr(container, 'data-testid', 'stage-kpi')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-stage-area-parallel')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-task-t1')).not.toBeNull();
  });

  it('пустое состояние без выбранного этапа', () => {
    const { container } = mount(createElement(FeatureStageArea, { run: { stages: [] } as unknown as FeatureRun, selectedId: null, isMobile: false }));
    expect(textNodes(container).join(' ')).toContain('Здесь будет выбранный этап');
  });

  it('сводка подгружается по первой задаче этапа и рисует KPI', async () => {
    stageMock.mockResolvedValue({ stageSummary: summary() });
    const run = { stages: [stage], steps: [task], reviews: [], qa: [], final: null, merge: null } as unknown as FeatureRun;
    const { container } = mount(createElement(FeatureStageArea, { run, selectedId: 'step-b', isMobile: false }));
    await act(async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); });
    expect(stageMock).toHaveBeenCalledWith('t1', expect.anything());
    expect(findNodeByAttr(container, 'data-testid', 'stage-kpi')).not.toBeNull();
  });
});
