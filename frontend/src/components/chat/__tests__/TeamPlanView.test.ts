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
      maxWavesAfter: 0, maxTasksAfter: 0,
    };
    const html = renderWithCtx(budget);
    expect(html).not.toContain('осталось');
    expect(html).not.toContain('поднимется');
  });

  it('план сверх остатка по волнам — строка про остаток и поднятие потолка волн', () => {
    // used=5/5 волн (потолок исчерпан), осталось 0; план на 3 волны — выход за остаток.
    // Бэк посчитал новый потолок = maxWaves(5) + delta(3) = 8 — фронт только показывает.
    const budget: TeamImplementBudget = {
      tasksUsed: 5, wavesUsed: 5, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
      maxWavesAfter: 8, maxTasksAfter: 0,
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
    // и обещание про поднятие потолков соответствует тому, что делает «Запустить».
    expect(html).toContain('осталось');
    expect(html).toContain('поднимутся');
    // Конкретика: «осталось 0» (W) и новый потолок «до 8» (X = Max + delta)
    expect(html).toContain('осталось 0');
    expect(html).toContain('до 8');
    // Главная находка ревью c156193b: старый текст называл новым потолком размер плана (3).
    // Под запретом и старое «потолок будет поднят», и «бюджет итерации — N» (потолок за остаток).
    expect(html).not.toContain('до 3');
    expect(html).not.toContain('потолок будет поднят');
    expect(html).not.toMatch(/бюджет итерации — \d+/);
  });

  it('план сверх остатка по задачам — строка про остаток и поднятие потолка задач', () => {
    // used=10/10 задач (потолок исчерпан), осталось 0; план на 2 задачи — выход.
    // Новый потолок задач = maxTasks(10) + delta(2) = 12.
    const budget: TeamImplementBudget = {
      tasksUsed: 10, wavesUsed: 2, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
      maxWavesAfter: 0, maxTasksAfter: 12,
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
    expect(html).toContain('поднимутся');
    expect(html).toContain('осталось 0');
    expect(html).toContain('до 12');
    expect(html).not.toContain('до 2');
    expect(html).not.toContain('потолок будет поднят');
    expect(html).not.toMatch(/бюджет итерации — \d+/);
  });

  it('план сверх остатка по обоим измерениям — ОДНА фраза, без склейки через « и »', () => {
    // План на 3 волны и 2 задачи; новые потолки 8 и 12.
    const budget: TeamImplementBudget = {
      tasksUsed: 10, wavesUsed: 5, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
      maxWavesAfter: 8, maxTasksAfter: 12,
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
    expect(html).toContain('до 8');
    expect(html).toContain('до 12');
    // ОДНА фраза вместо склейки: «План на N. В бюджете осталось W — ... поднимутся до X».
    // Соединение « и » внутри одной части (план/остаток/потолок) допустимо, но между
    // частями — только «. » и « — ».
    expect(html).toContain('План на 3 волны и 2 под-задачи.');
    expect(html).toContain('осталось 0 волн и 0 под-задач');
    // Старая склейка двух независимых кусков через « и » между остатком и «потолок
    // поднимется до» — под запретом.
    expect(html).not.toContain('потолок поднимется до');
  });

  it('остаток частично израсходован (used > 0, но не max) — плашка показывает остаток и ЧЕСТНЫЙ новый потолок', () => {
    // used=2/5 волн → осталось 3; план на 5 волн — выход за остаток (delta=2).
    // Новый потолок = maxWaves(5) + delta(2) = 7. Старый текст говорил бы «до 5» — это
    // был размер потолка, а не «Max + delta». Главная находка ревью c156193b.
    const budget: TeamImplementBudget = {
      tasksUsed: 5, wavesUsed: 2, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
      maxWavesAfter: 7, maxTasksAfter: 0,
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
    // Остаток честный: 3 волны (родительный множественного)
    expect(html).toContain('осталось 3 волны');
    // Новый потолок — Max + delta = 7, а не размер плана (5)
    expect(html).toContain('до 7');
    expect(html).not.toContain('до 5');
    // Старая лексика «бюджет итерации — N» под запретом
    expect(html).not.toContain('бюджет итерации — 5');
  });

  it('maxWavesAfter=0 — плашка скрыта (плана нет, бэк не заполнил)', () => {
    // Доказательство мутацией: если бэк перестал считать maxWavesAfter (например, забыли
    // про новый код в BroadcastTeamImplementAsync), плашка должна пропасть, а не врать
    // числом maxWaves как «новым потолком». used=5/5, план на 3 волны — старый код бы
    // показал «до 3» (размер плана); новый — молчит.
    const budget: TeamImplementBudget = {
      tasksUsed: 5, wavesUsed: 5, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
      maxTasks: 10, maxWaves: 5, maxRuns: 10, maxRetries: 5, maxWakeups: 5,
      maxWavesAfter: 0, maxTasksAfter: 0,
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
    expect(html).not.toContain('осталось');
    expect(html).not.toContain('поднимется');
    expect(html).not.toContain('до 3');
  });
});
