// Карточка плана «Командной реализации»: блок «Замысел», ссылка на файл полного плана
// (решение владельца 2026-08-02, docs/architecture/team-implement-mode.md) и переключатель
// «Текстом/Схемой» под флагом visual-plan. Рендерим статикой через react-dom/server —
// как соседние TeamEscalationView.test/ToolUseView.test.
import { describe, it, expect, afterEach } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem, TeamImplementBudget, TeamPlan } from '../../../types';
import { TeamPlanView } from '../TeamPlanView';
import { ChatOpenFileContext, TeamPlanContext } from '../contexts';
import { setAllFlags } from '../../../lib/featureFlags';

type PlanItem = Extract<ChatItem, { kind: 'team_plan' }>;

function plan(over: Partial<TeamPlan> = {}): TeamPlan {
  return {
    id: 'plan1', request: 'экспорт трат', summary: 'Экспорт трат в XLSX',
    createdAt: '2026-08-02T10:00:00Z', waveCount: 1, executorCount: 1,
    subtasks: [{
      id: 'st1', title: 'Эндпоинт экспорта', goal: '', executorPersonaId: 'p1',
      executorRationale: 'Бэкенд — его зона', files: ['Controllers/SpendController.cs'],
      wave: 1, doneCriteria: '',
    }],
    version: 1, assumptions: [], changes: [],
    ...over,
  };
}

function card(over: Partial<TeamPlan> = {}, extra: Partial<PlanItem> = {}): PlanItem {
  return { kind: 'team_plan', planId: 'plan1', plan: plan(over), resolved: false, approved: null, ...extra };
}

const render = (item: PlanItem, onOpenFile: ((path: string) => void) | null = () => {},
  props: { initialSchemeView?: 'text' | 'scheme' } = {}) =>
  renderToStaticMarkup(
    createElement(ChatOpenFileContext.Provider, { value: onOpenFile },
      createElement(TeamPlanView, { item, online: true, ...props })));

// Рендер карточки на подтверждении с подложенным TeamPlanContext — нужно для
// BudgetOverrunNote, который читает budget из контекста. ctx не-null триггерит
// canAct=true и выводит плашку на «на подтверждении» (та же ветка, что в проде).
function renderWithCtx(budget: TeamImplementBudget | null) {
  const ctx: import('../contexts').TeamPlanChatContext = {
    autoWaves: false, waveNumber: 0, planCardId: 'plan1',
    executorPersonaIds: [], budget,
    onRespond: () => {},
  };
  return renderToStaticMarkup(
    createElement(ChatOpenFileContext.Provider, { value: () => {} },
      createElement(TeamPlanContext.Provider, { value: ctx },
        createElement(TeamPlanView, { item: card(), online: true }))));
}

describe('TeamPlanView — блок «Замысел» и ссылка на полный план', () => {
  it('без intent и planFilePath — ни блока, ни ссылки нет', () => {
    const html = render(card());
    expect(html).not.toContain('Замысел');
    expect(html).not.toContain('Полный план');
  });

  it('intent непустой — блок «Замысел» с текстом рендерится над списком под-задач', () => {
    const html = render(card({ intent: 'Идём через SafeJoin, авторизацию не трогаем.' }));
    expect(html).toContain('Замысел');
    expect(html).toContain('Идём через SafeJoin, авторизацию не трогаем.');
  });

  it('planFilePath задан — строка-ссылка показывает хвост пути (полный путь — в title)', () => {
    const html = render(card({ planFilePath: 'docs/plans/team/abc123/plan-v1.md' }));
    expect(html).toContain('Полный план');
    expect(html).toContain('>plan-v1.md<');
    expect(html).toContain('title="Открыть docs/plans/team/abc123/plan-v1.md"');
  });

  it('planFilePath: null — строки нет, карточка не падает', () => {
    const html = render(card({ planFilePath: null }));
    expect(html).not.toContain('Полный план');
  });

  it('без обработчика открытия файла (onOpenFile отсутствует) — ссылка не рендерится', () => {
    const html = render(card({ planFilePath: 'docs/plans/team/abc123/plan-v1.md' }), null);
    expect(html).not.toContain('Полный план');
  });

  it('свёрнутая (resolved) карточка запущенного плана — ссылка всё равно работает', () => {
    const html = render(card(
      { planFilePath: 'docs/plans/team/abc123/plan-v1.md' },
      { resolved: true, approved: true },
    ));
    expect(html).toContain('Полный план');
    expect(html).toContain('plan-v1.md');
  });

  // Прод-баг (Вера, 2026-08-03): «Замысел» не показывался в развёрнутой карточке ПОСЛЕ
  // старта волны (resolved && approved) — прошлый тест этой ветки проверял только ссылку
  // на файл, без intent, и дефект прошёл мимо. Обе вещи должны рендериться вместе.
  it('запущенный план (resolved && approved) с intent — «Замысел» рендерится рядом со ссылкой на файл', () => {
    const html = render(card(
      { intent: 'Идём через SafeJoin, авторизацию не трогаем.', planFilePath: 'docs/plans/team/abc123/plan-v1.md' },
      { resolved: true, approved: true },
    ));
    expect(html).toContain('Замысел');
    expect(html).toContain('Идём через SafeJoin, авторизацию не трогаем.');
    expect(html).toContain('Полный план');
    expect(html).toContain('plan-v1.md');
  });

  it('отменённый план — ссылка тоже рендерится', () => {
    const html = render(card(
      { planFilePath: 'docs/plans/team/abc123/plan-v1.md' },
      { resolved: true, approved: false },
    ));
    expect(html).toContain('Полный план');
    expect(html).toContain('plan-v1.md');
  });

  // Правка плана человеком («Изменить план») гасит старую карточку не как отменённую,
  // а как заменённую версией vN — иначе выглядит будто план отменили, хотя правка принята
  it('карточка с supersededBy показывает «заменена версией vN», а не «план отменён»', () => {
    const html = render(card({}, { resolved: true, approved: false, supersededBy: 2 }));
    expect(html).toContain('заменена');
    expect(html).toContain('v2');
    expect(html).not.toContain('План отменён');
  });

  it('resolved/approved=false без supersededBy — по-прежнему «план отменён»', () => {
    const html = render(card({}, { resolved: true, approved: false }));
    expect(html).toContain('План отменён');
  });
});

// Переключатель «Текстом/Схемой» (флаг visual-plan): схема детерминированная,
// без модели и без кнопки «Собрать схему» — вид доступен сразу. Клик в статик-рендере
// не воспроизвести, поэтому стартовый вид задаётся initialSchemeView (тот же приём,
// что initialView у TeamPlanScheme).
describe('TeamPlanView — переключатель «Текстом/Схемой» (флаг visual-plan)', () => {
  afterEach(() => setAllFlags({}));

  it('флаг выключен — сегмента нет, тело плана рендерится текстом', () => {
    const html = render(card());
    expect(html).not.toContain('Текстом');
    expect(html).not.toContain('Схемой');
    expect(html).toContain('Волна 1');
  });

  it('флаг включён — сегмент есть, дефолтный вид «Текстом»', () => {
    setAllFlags({ 'visual-plan': true });
    const html = render(card());
    expect(html).toContain('Текстом');
    expect(html).toContain('Схемой');
    // Дефолт — текст: под-задачи списком по волнам, «суть» схемы не рендерится
    expect(html).toContain('Волна 1');
    expect(html).toContain('Эндпоинт экспорта');
    expect(html).not.toContain('командная реализация');
  });

  it('вид «Схемой» — детерминированная схема вместо текстового тела', () => {
    setAllFlags({ 'visual-plan': true });
    const html = render(card(), () => {}, { initialSchemeView: 'scheme' });
    // Крошка «Суть», жанр-пилюля и сводка планировщика — каркас схемы на месте…
    expect(html).toContain('Суть');
    expect(html).toContain('командная реализация');
    expect(html).toContain('Экспорт трат в XLSX');
    // …а текстовое тело (под-задачи списком по волнам) не рендерится
    expect(html).not.toContain('Волна 1');
  });

  it('resolved-карточка (план запущен) — сегмента нет даже с флагом', () => {
    setAllFlags({ 'visual-plan': true });
    const html = render(card({}, { resolved: true, approved: true }));
    expect(html).not.toContain('Текстом');
    expect(html).not.toContain('Схемой');
  });
});

// Предупреждение «план сверх остатка бюджета» (волна 4 team-blocker-honest): плашка
// показывает ОСТАТОК W/T в бюджете итерации (max - used, не ниже 0), а не потолок.
// Потолок как «остаток» врёт: часть волн/задач итерации уже потрачена, и плашка обещает
// предсказание, которого не происходит (находка ревью d3964f4a → фикс-волна).
// Выход за остаток бэкенд лечит расширением потолка на дельту в RespondTeamPlanAsync.
describe('TeamPlanView — BudgetOverrunNote: остаток бюджета и поднятие потолков', () => {
  it('budget=null — плашка молчит (read-only режим)', () => {
    const html = renderWithCtx(null);
    expect(html).not.toContain('осталось');
    expect(html).not.toContain('поднимется');
  });

  it('план в пределах остатка — плашки нет', () => {
    // used=2/5 волн и 5/10 задач потрачено, осталось 3 волны и 5 задач; план — 1 волна и 1 задача
    const budget: TeamImplementBudget = {
      tasksUsed: 5, wavesUsed: 2, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
    };
    const html = renderWithCtx(budget);
    expect(html).not.toContain('осталось');
    expect(html).not.toContain('поднимется');
  });

  it('план сверх остатка по волнам — строка про остаток и поднятие потолка волн', () => {
    // used=5/5 волн (потолок исчерпан), осталось 0; план на 3 волны — выход
    const budget: TeamImplementBudget = {
      tasksUsed: 5, wavesUsed: 5, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
    };
    const html = renderToStaticMarkup(
      createElement(ChatOpenFileContext.Provider, { value: () => {} },
        createElement(TeamPlanContext.Provider, { value: {
          autoWaves: false, waveNumber: 0, planCardId: 'plan1',
          executorPersonaIds: [], budget,
          onRespond: () => {},
        } },
          createElement(TeamPlanView, {
            item: card({ waveCount: 3, subtasks: [{
              id: 'st1', title: 'a', goal: '', executorPersonaId: 'p1',
              executorRationale: '', files: [], wave: 1, doneCriteria: '',
            }] }),
            online: true,
          }))));
    // Главное по условию задачи: слово «осталось» относится к остатку (W/T),
    // и обещание про поднятие потолков соответствует тому, что делает «Запустить»
    expect(html).toContain('осталось');
    expect(html).toContain('поднимется');
    // Конкретика: «в бюджете итерации осталось 0» (W) и «до 3» (N волн плана)
    expect(html).toContain('в бюджете итерации осталось 0');
    expect(html).toContain('до 3');
    // Запрещена старая лексика «потолок будет поднят» и «бюджет итерации — N»:
    // раньше плашка называла потолок остатком, теперь — нет
    expect(html).not.toContain('потолок будет поднят');
    expect(html).not.toMatch(/бюджет итерации — \d+/);
  });

  it('план сверх остатка по задачам — строка про остаток и поднятие потолка задач', () => {
    // used=10/10 задач (потолок исчерпан), осталось 0; план на 2 задачи — выход
    const budget: TeamImplementBudget = {
      tasksUsed: 10, wavesUsed: 2, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
    };
    const html = renderToStaticMarkup(
      createElement(ChatOpenFileContext.Provider, { value: () => {} },
        createElement(TeamPlanContext.Provider, { value: {
          autoWaves: false, waveNumber: 0, planCardId: 'plan1',
          executorPersonaIds: [], budget,
          onRespond: () => {},
        } },
          createElement(TeamPlanView, {
            item: card({ subtasks: [
              { id: 's1', title: 'a', goal: '', executorPersonaId: 'p1',
                executorRationale: '', files: [], wave: 1, doneCriteria: '' },
              { id: 's2', title: 'b', goal: '', executorPersonaId: 'p1',
                executorRationale: '', files: [], wave: 1, doneCriteria: '' },
            ] }),
            online: true,
          }))));
    expect(html).toContain('осталось');
    expect(html).toContain('поднимется');
    expect(html).toContain('в бюджете итерации осталось 0');
    expect(html).toContain('до 2');
    expect(html).not.toContain('потолок будет поднят');
    expect(html).not.toMatch(/бюджет итерации — \d+/);
  });

  it('план сверх остатка по обоим измерениям — две клаузы соединены через « и »', () => {
    const budget: TeamImplementBudget = {
      tasksUsed: 10, wavesUsed: 5, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
    };
    const html = renderToStaticMarkup(
      createElement(ChatOpenFileContext.Provider, { value: () => {} },
        createElement(TeamPlanContext.Provider, { value: {
          autoWaves: false, waveNumber: 0, planCardId: 'plan1',
          executorPersonaIds: [], budget,
          onRespond: () => {},
        } },
          createElement(TeamPlanView, {
            item: card({ waveCount: 3, subtasks: [
              { id: 's1', title: 'a', goal: '', executorPersonaId: 'p1',
                executorRationale: '', files: [], wave: 1, doneCriteria: '' },
              { id: 's2', title: 'b', goal: '', executorPersonaId: 'p1',
                executorRationale: '', files: [], wave: 2, doneCriteria: '' },
            ] }),
            online: true,
          }))));
    expect(html).toContain('осталось');
    // План на 3 волны и 2 задачи — обе клаузы в одной строке через « и »
    expect(html).toContain('до 3');
    expect(html).toContain('до 2');
    expect(html).toContain(' и ');
  });

  it('остаток частично израсходован (used > 0, но не max) — плашка называет ОСТАТОК, а не потолок', () => {
    // used=2/5 волн → осталось 3; план на 5 волн — выход за остаток 3 (потолок 5 не превышен)
    // Главная находка ревью: прежний код показал бы «бюджет итерации — 5» (потолок как остаток),
    // новый — «осталось 3» (честный остаток)
    const budget: TeamImplementBudget = {
      tasksUsed: 5, wavesUsed: 2, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
    };
    const html = renderToStaticMarkup(
      createElement(ChatOpenFileContext.Provider, { value: () => {} },
        createElement(TeamPlanContext.Provider, { value: {
          autoWaves: false, waveNumber: 0, planCardId: 'plan1',
          executorPersonaIds: [], budget,
          onRespond: () => {},
        } },
          createElement(TeamPlanView, {
            item: card({ waveCount: 5 }),
            online: true,
          }))));
    expect(html).toContain('осталось 3');
    // Старая лексика «бюджет итерации — N» под запретом (потолок нельзя выдавать за остаток)
    expect(html).not.toContain('бюджет итерации — 5');
  });
});
