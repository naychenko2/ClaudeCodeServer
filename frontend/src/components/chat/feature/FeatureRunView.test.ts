// Карточка фичи (режим «Исполнение фичи»). Рендер — renderToStaticMarkup
// (паттерн соседних тестов, @testing-library нет; окружение node — window undefined,
// useIsMobile() = false, effects не идут). Проверяем:
//   • шапка (имя + ветка + прогресс);
//   • плашку-тревогу: «ждёт вас» (мерж/явное) и «исполнитель упал» (с/без чата);
//   • блок «Сейчас» (по карточке на now[]; у закрытой нет);
//   • итог смерженной фичи (из summary);
//   • пустое состояние (шагов 0) и легенду (когда шаги есть);
//   • что УШЛО: «Принять», «Изменить ход», «Финал зелёный», «Ревью не было».
//   • гейт фич-флага (M2) и мобилу (M7).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { FeatureRun, FeatureTask, FeatureNow, FeatureAttention, FeatureSummary } from '../../../types/featureRun';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import { FeatureRunView } from './FeatureRunView';
import { TurnPlanPill } from '../TurnPlanPill';
import { __test } from '../../../lib/featureRuns';

// Задача-заглушка (подмножество TaskItem, как несёт GET /api/tasks/features).
function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

// Базовый агрегат фичи: эпик + шаг + ревью + финал; готовность/прогресс/вердикты.
// Новые поля (now/attention/closed/summary) — по умолчанию «тихие», тесты их покрывают.
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator-console' }),
    steps: [task({ id: 's1', title: 'Шаг 1', stepRef: 'Ш1', lane: 'фронт', status: 'done' })],
    reviews: [task({ id: 'r1', title: 'Ревью', status: 'done', verification: { verdict: 'accepted' } })],
    qa: [],
    final: task({ id: 'f1', title: 'Финал', status: 'done', verification: { verdict: 'accepted' } }),
    merge: null,
    awaitingHumanSince: null,
    awaitingHumanReason: null,
    verdicts: { accepted: 2, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 1,
    ready: true,
    now: [],
    attention: null,
    closed: false,
    summary: null,
    ...over,
  };
}

const render = (r: FeatureRun) => renderToStaticMarkup(createElement(FeatureRunView, { run: r }));

// now[]-элемент для блока «Сейчас».
function nowItem(over: Partial<FeatureNow> = {}): FeatureNow {
  return { taskId: 'n', stage: 'prod', title: 'Задача', ...over };
}

describe('FeatureRunView — шапка', () => {
  it('имя, ветка и прогресс на месте', () => {
    const html = render(run());
    expect(html).toContain('Пульт куратора');
    expect(html).toContain('feat/curator-console');
    expect(html).toContain('100%');
  });
});

describe('FeatureRunView — плашка «ждёт вас»', () => {
  it('мерж: «все шаги приняты, мержа нет» + действия [Попросить Марка] [Итог финала]', () => {
    const att: FeatureAttention = { kind: 'wait', reason: 'мерж', since: '2026-10-17T10:00:00Z' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Ждёт вас');
    expect(html).toContain('Все шаги приняты, мержа ещё нет');
    expect(html).toContain('Отдать фичу Марку на мерж?');
    expect(html).toContain('Попросить Марка');
    expect(html).toContain('Итог финала');
  });

  it('явное ожидание: причина из attention, без текста про мерж', () => {
    const att: FeatureAttention = { kind: 'wait', reason: 'нужно решение о мерже', since: '2026-10-17T10:00:00Z' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Ждёт вас');
    expect(html).toContain('нужно решение о мерже');
    expect(html).not.toContain('Отдать фичу Марку на мерж?');
  });
});

describe('FeatureRunView — плашка «исполнитель упал»', () => {
  it('с linkedSessionId: [Перезапустить] и [Чат исполнителя]', () => {
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', since: '2026-10-17T10:00:00Z', linkedSessionId: 'sess-1' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Исполнитель упал');
    expect(html).toContain('Перезапустить');
    expect(html).toContain('Чат исполнителя');
  });

  it('без linkedSessionId: [Чат исполнителя] не рендерится', () => {
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', since: '2026-10-17T10:00:00Z' };
    const html = render(run({ ready: false, attention: att }));
    expect(html).toContain('Исполнитель упал');
    expect(html).toContain('Перезапустить');
    expect(html).not.toContain('Чат исполнителя');
  });
});

describe('FeatureRunView — блок «Сейчас»', () => {
  it('карточка на каждый элемент now[] (заголовок задачи + шаг)', () => {
    const now: FeatureNow[] = [
      nowItem({ taskId: 'n1', stepRef: 'Ш7', title: 'Блокеры финала', startedAt: '2026-10-17T10:00:00Z' }),
      nowItem({ taskId: 'n2', stepRef: 'Ш8', title: 'Доработка экрана', startedAt: '2026-10-17T10:05:00Z' }),
    ];
    const html = render(run({ ready: false, now }));
    expect(html).toContain('Сейчас');
    expect(html).toContain('Блокеры финала');
    expect(html).toContain('Доработка экрана');
    expect(html).toContain('Ш7');
    expect(html).toContain('Ш8');
  });

  it('у закрытой фичи «Сейчас» нет (даже если now[] не пуст)', () => {
    const now: FeatureNow[] = [nowItem({ taskId: 'n1', title: 'не должно быть' })];
    const html = render(run({ closed: true, now, summary: { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 370, tasks: 14, steps: 9, loops: 3 } }));
    expect(html).not.toContain('>Сейчас<');
  });
});

describe('FeatureRunView — итог смерженной фичи', () => {
  it('«Фича смержена» + строка из summary (дата · длительность · задачи · шаги · ↺)', () => {
    const summary: FeatureSummary = { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 370, tasks: 14, steps: 9, loops: 3 };
    const html = render(run({ closed: true, summary }));
    expect(html).toContain('Фича смержена');
    expect(html).toContain('15.10 21:09');
    expect(html).toContain('6 ч 10 мин');
    expect(html).toContain('14');
    expect(html).toContain('9');
    expect(html).toContain('↺3');
  });
});

describe('FeatureRunView — пустое состояние и легенда', () => {
  it('шагов 0: пустое состояние, легенды нет', () => {
    const html = render(run({ steps: [] }));
    expect(html).toContain('Шагов пока нет');
    expect(html).toContain('План появится');
    // Легенда (кольца + «нет в трекере») при пустом состоянии не рисуется.
    expect(html).not.toContain('нет в трекере');
  });

  it('когда шаги есть: легенда (вернул / принял с правками / принял / нет в трекере)', () => {
    const html = render(run()); // run() несёт один шаг
    expect(html).toContain('вернул');
    expect(html).toContain('принял с правками');
    expect(html).toContain('принял');
    expect(html).toContain('нет в трекере');
  });
});

describe('FeatureRunView — что ушло (заглушки и лишние плашки)', () => {
  it('нет «Принять», «Изменить ход», «Финал зелёный», «Ревью не было»', () => {
    const html = render(run());
    expect(html).not.toContain('Принять');
    expect(html).not.toContain('Изменить ход');
    expect(html).not.toContain('Финал зелёный');
    expect(html).not.toContain('Ревью не было');
  });
});

describe('FeatureRunView — конвейер (PipelineFlow) на месте', () => {
  it('одна секция «Конвейер», стадии рендерятся', () => {
    const html = render(run());
    expect(html).toContain('Конвейер');
    expect(html).toContain('Производство');
    expect(html).toContain('Ревью');
  });
});

describe('FeatureRunView — мобила (M7)', () => {
  it('useIsMobile не рвёт SSR-рендер', () => {
    expect(() => render(run())).not.toThrow();
  });
});

describe('Гейт фич-флага (M2)', () => {
  const todo: TodoItem = { content: 'Шаг плана', status: 'pending' };
  const sid = 's1';

  it('runs=[] → пилюля в режиме плана (нет «исполнение фичи»)', () => {
    __test.reset();
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [todo], runs: [], sessionId: sid }));
    expect(html).toContain('Показать план хода');
    expect(html).not.toContain('Показать исполнение фичи');
  });

  it('runs=[run] → пилюля в режиме фичи', () => {
    __test.reset();
    __test.setRuns(sid, [run()]);
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [run()], sessionId: sid }));
    expect(html).toContain('Показать исполнение фичи');
  });
});
