// Конвейер фичи (PipelineFlow) — строго «фронт только показывает»: стадии/линии/возвраты/
// шаги приходят из бэка (run.stages / run.lanes / run.returnsBySource / run.stepChains),
// компонент раскладывает.
//
// Окружение node (без jsdom): чистые функции (stageByKey, stageState, stageWidth, laneInfo,
// laneTrackWidth, returnBadges, toggleExpanded, laneOfChain, stageMeta, stepChainByRef,
// stageHasDetails) + рендер renderToStaticMarkup (SSR) + интерактивные кейсы на fake-DOM
// (паттерн FeatureRunView.test.ts). useIsMobile() = false → десктоп-раскладка.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { act } from 'react';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  FeatureRun, FeatureTask, FeatureStage, FeatureStepChain, FeatureRound,
  FeatureRoundVerdict, StageKey,
} from '../../../types/featureRun';
import { SP } from '../../../lib/design';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from '../__tests__/fakeDom';
import { __test, focusFeatureStep, setTurnPlanMode } from '../../../lib/featureRuns';
import { ChatSessionContext } from '../contexts';
import {
  PipelineFlow, stageByKey, stageState, stageWidth,
  laneInfo, laneTrackWidth, returnBadges, toggleExpanded,
  laneOfChain, stageMeta, stepChainByRef, stageHasDetails,
} from './PipelineFlow';

// Стор персон (lib/personas) — мок: hoisted-карта id→имя. Исполнитель резолвится по имени;
// тесты подставляют нужных персон. Компоненту нужен только .name.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', () => ({
  getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
// Карта общая на весь файл — сбрасываю перед каждым тестом (изоляция).
beforeEach(() => { personaMap.clear(); });

function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора' }),
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    awaitingHumanSince: null,
    awaitingHumanReason: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

// 7 стадий в порядке жизненного цикла (cut → plan → prod → review → qa → final → merge).
// `over` — точечные переопределения по ключу стадии (rounds/personaIds/taskCount/verdict/longestStepRef).
function stages(over: Partial<Record<StageKey, Partial<FeatureStage>>> = {}): FeatureStage[] {
  const base: Record<StageKey, Partial<FeatureStage>> = {
    cut:    { key: 'cut',    state: 'done',    passes: 1, taskIds: ['epic-1'] },
    plan:   { key: 'plan',   state: 'done',    passes: 1, taskIds: ['epic-1'] },
    prod:   { key: 'prod',   state: 'done',    passes: 1, taskIds: [] },
    review: { key: 'review', state: 'done',    passes: 1, taskIds: [] },
    qa:     { key: 'qa',     state: 'done',    passes: 1, taskIds: [] },
    final:  { key: 'final',  state: 'done',    passes: 1, taskIds: [] },
    merge:  { key: 'merge',  state: 'pending', passes: 0, taskIds: [] },
  };
  const merged = { ...base, ...over };
  return (Object.keys(merged) as StageKey[]).map(k => merged[k] as FeatureStage);
}

// Цепочка шага (F8: run.stepChains).
function chain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'D1',
    lane: 'back',
    title: 'Шаг',
    personaId: null,
    taskIds: ['s1'],
    outcome: 'accepted',
    status: 'принят',
    durationMinutes: 30,
    source: null,
    loops: 0,
    rounds: [],
    ...over,
  };
}

// Виток (F8: rounds у стадий qa/final и у stepChains).
function round(over: Partial<FeatureRound> = {}): FeatureRound {
  return { n: 1, verdict: 'accepted', ...over };
}

const render = (r: FeatureRun) => renderToStaticMarkup(createElement(PipelineFlow, { run: r }));
// SSR с форсированным раскрытием «Производства»: строки шагов (data-step-ref) в разметке.
// После F8 «свёрнуто по умолчанию» (initialExpandedStage=null) тестам строк шагов нужен
// раскрытый prod — иначе SSR (читает только начальное состояние) их не отрендерит.
const renderOpen = (r: FeatureRun) => renderToStaticMarkup(createElement(PipelineFlow, { run: r, initialExpandedStage: 'prod' }));

// Ожидаемое «ЧЧ:ММ»: теми же локальными геттерами от той же Date, что и hhmm (featureTime) —
// тест не зависит от пояса машины (CI в UTC, разработка в МСК).
function localHHMM(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// Интерактив: смонтировать, кликнуть строку стадии, вернуть весь текст дерева (для проверок содержимого).
function clickStageText(r: FeatureRun, key: string): string {
  const { root, container } = mount(createElement(PipelineFlow, { run: r }));
  const btn = findNodeByAttr(container, 'data-testid', `stage-row-${key}`);
  if (btn) act(() => (reactProps(btn!)!.onClick as () => void)());
  const text = textNodes(container).join(' ');
  root.unmount();
  return text;
}

// === stageByKey / stageState: чтение стадии из бэка ===
describe('stageByKey / stageState', () => {
  it('нет stages (старый ответ/кэш) → null / pending (не падаем, не считаем сами)', () => {
    const r = run(); // без stages
    expect(stageByKey(r, 'merge')).toBeNull();
    expect(stageState(r, 'merge')).toBe('pending');
    expect(stageState(r, 'prod')).toBe('pending');
  });

  it('stages есть → состояние из бэка (done/active/pending/absent)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'run', passes: 3, taskIds: ['s1'] },
        merge: { key: 'merge', state: 'absent', passes: 0, taskIds: [] },
      }),
    });
    expect(stageState(r, 'prod')).toBe('active');
    expect(stageState(r, 'merge')).toBe('absent');
    expect(stageByKey(r, 'prod')?.passes).toBe(3);
  });
});

// === stageWidth: толщина сегмента (формула макета; pending/absent → 2) ===
describe('stageWidth', () => {
  it('pending/absent → 2 (пунктир)', () => {
    expect(stageWidth(3, 'pending')).toBe(2);
    expect(stageWidth(3, 'absent')).toBe(2);
  });
  it('passes=3, done → min(2 + (3−1)·1.5, 8) = 5', () => {
    expect(stageWidth(3, 'done')).toBe(5);
  });
  it('passes=1 → min(2 + 0, 8) = 2', () => {
    expect(stageWidth(1, 'done')).toBe(2);
  });
  it('потолок: passes=5 → min(2 + 4·1.5, 8) = 8', () => {
    expect(stageWidth(5, 'done')).toBe(8);
  });
  it('passes>5 капится: passes=9 → min(2 + 4·1.5, 8) = 8', () => {
    expect(stageWidth(9, 'done')).toBe(8);
  });
});

// === laneInfo: линии бэк/фронт из run.lanes ===
describe('laneInfo', () => {
  it('нет lanes → total=0 (строка не рисуется)', () => {
    expect(laneInfo(run())).toEqual({
      back: { label: 'Бэк', total: 0, accepted: 0, returns: 0, personaIds: [] },
      front: { label: 'Фронт', total: 0, accepted: 0, returns: 0, personaIds: [] },
    });
  });

  it('lanes: back total=3, accepted=2, returns=1 → строка «Бэк · 2 из 3»', () => {
    const r = run({
      lanes: [{ lane: 'back', personaIds: ['denis'], total: 3, accepted: 2, returns: 1 }],
    });
    expect(laneInfo(r).back).toEqual({ label: 'Бэк', total: 3, accepted: 2, returns: 1, personaIds: ['denis'] });
    expect(laneInfo(r).front.total).toBe(0);
  });

  it('lanes: back + front → обе строки', () => {
    const r = run({
      lanes: [
        { lane: 'back', personaIds: [], total: 2, accepted: 1, returns: 0 },
        { lane: 'front', personaIds: [], total: 1, accepted: 1, returns: 0 },
      ],
    });
    expect(laneInfo(r).back.total).toBe(2);
    expect(laneInfo(r).front.total).toBe(1);
  });
});

// === laneTrackWidth: толщина дорожки = min(2 + r·0.5, 8) ===
describe('laneTrackWidth', () => {
  it('r=0 → 2', () => expect(laneTrackWidth(0)).toBe(2));
  it('r=2 → 3', () => expect(laneTrackWidth(2)).toBe(3));
  it('r=12 → потолок 8', () => expect(laneTrackWidth(12)).toBe(8));
});

// === returnBadges: бейджи возврата из run.returnsBySource ===
describe('returnBadges', () => {
  it('нет returnsBySource → пусто', () => {
    expect(returnBadges(run())).toEqual([]);
  });

  it('count=0 → бейдж не строится (показывать только где count > 0)', () => {
    const r = run({
      returnsBySource: {
        review: { count: 0, open: 0 },
        qa: { count: 0, open: 0 },
        final: { count: 0, open: 0 },
      },
    });
    expect(returnBadges(r)).toEqual([]);
  });

  it('review/qa/final с count>0 → 3 бейджа в порядке ревью/проверка/финал', () => {
    const r = run({
      returnsBySource: {
        review: { count: 18, open: 0 },
        qa: { count: 2, open: 1 },
        final: { count: 1, open: 0 },
      },
    });
    const badges = returnBadges(r);
    expect(badges.map(b => [b.key, b.count, b.open])).toEqual([
      ['review', 18, 0],
      ['qa', 2, 1],
      ['final', 1, 0],
    ]);
    expect(badges.map(b => b.label)).toEqual(['с ревью', 'с проверки', 'с финала']);
  });
});

// === toggleExpanded: одиночное раскрытие ===
describe('toggleExpanded', () => {
  it('открыть одну стадию — закрыть предыдущую; повторный клик — сворачивает', () => {
    expect(toggleExpanded(null, 'prod')).toBe('prod');
    expect(toggleExpanded('prod', 'review')).toBe('review');
    expect(toggleExpanded('review', 'review')).toBeNull();
  });
});

// === F8: laneOfChain — нормализация линии шага ===
describe('laneOfChain', () => {
  it('back/бэк → back; front/фронт → front; прочее → other', () => {
    expect(laneOfChain('back')).toBe('back');
    expect(laneOfChain('бэк')).toBe('back');
    expect(laneOfChain('backend')).toBe('back');
    expect(laneOfChain('front')).toBe('front');
    expect(laneOfChain('фронт')).toBe('front');
    expect(laneOfChain('frontend')).toBe('front');
    expect(laneOfChain('test')).toBe('other');
    expect(laneOfChain(null)).toBe('other');
    expect(laneOfChain(undefined)).toBe('other');
  });
});

// === F8: stageMeta — подпись-мета этапа ===
describe('stageMeta', () => {
  it('pending/absent → null (подписи Ф6, не мета)', () => {
    expect(stageMeta({ key: 'cut', state: 'pending', passes: 0, taskIds: [], rounds: [] } as FeatureStage)).toBeNull();
    expect(stageMeta({ key: 'cut', state: 'absent', passes: 0, taskIds: [], rounds: [] } as FeatureStage)).toBeNull();
    expect(stageMeta(null)).toBeNull();
  });

  it('«Егор · 1 задача»: имя + 1 задача (склонение)', () => {
    personaMap.set('egor', 'Егор');
    const s = { key: 'plan', state: 'done', passes: 1, taskIds: ['p1'], rounds: [], personaIds: ['egor'], taskCount: 1 } as FeatureStage;
    expect(stageMeta(s)).toBe('Егор · 1 задача');
  });

  it('склонение: 2 задачи / 5 задач', () => {
    personaMap.set('egor', 'Егор');
    expect(stageMeta({ key: 'plan', state: 'done', passes: 1, taskIds: [], rounds: [], personaIds: ['egor'], taskCount: 2 } as FeatureStage)).toBe('Егор · 2 задачи');
    expect(stageMeta({ key: 'plan', state: 'done', passes: 1, taskIds: [], rounds: [], personaIds: ['egor'], taskCount: 5 } as FeatureStage)).toBe('Егор · 5 задач');
  });

  it('исход из verdict: «Александр · возврат» (безличная форма, Майя)', () => {
    personaMap.set('alex', 'Александр');
    const s = { key: 'review', state: 'done', passes: 1, taskIds: [], rounds: [], personaIds: ['alex'], verdict: 'returned' as FeatureRoundVerdict } as FeatureStage;
    expect(stageMeta(s)).toBe('Александр · возврат');
  });

  it('все слова verdict безличными: принято / с правками / снято / идёт', () => {
    personaMap.set('alex', 'Александр');
    const mk = (v: FeatureRoundVerdict) => stageMeta({ key: 'review', state: 'done', passes: 1, taskIds: [], rounds: [], personaIds: ['alex'], verdict: v } as FeatureStage);
    expect(mk('accepted')).toBe('Александр · принято');
    expect(mk('accepted_with_remarks')).toBe('Александр · принято с правками');
    expect(mk('withdrawn')).toBe('Александр · снято');
    expect(mk('running')).toBe('Александр · идёт');
  });

  it('≥3 исполнителей — первые два + «+N» (Майя: не плодить длинный список)', () => {
    personaMap.set('a', 'Анна');
    personaMap.set('b', 'Борис');
    personaMap.set('c', 'Вера');
    const s = { key: 'plan', state: 'done', passes: 1, taskIds: [], rounds: [], personaIds: ['a', 'b', 'c'], taskCount: 3 } as FeatureStage;
    expect(stageMeta(s)).toBe('Анна, Борис +1 · 3 задачи');
  });

  it('нет данных (нет personaIds/taskCount/verdict) → null', () => {
    expect(stageMeta({ key: 'prod', state: 'done', passes: 1, taskIds: [], rounds: [] } as FeatureStage)).toBeNull();
  });

  it('несколько исполнителей — через запятую; неизвестный id не показываем', () => {
    personaMap.set('a', 'Анна');
    personaMap.set('b', 'Борис');
    const s = { key: 'plan', state: 'done', passes: 1, taskIds: [], rounds: [], personaIds: ['a', 'ghost', 'b'], taskCount: 3 } as FeatureStage;
    expect(stageMeta(s)).toBe('Анна, Борис · 3 задачи');
  });
});

// === F8: stepChainByRef ===
describe('stepChainByRef', () => {
  it('находит цепочку по stepRef; null — нет', () => {
    const r = run({ stepChains: [chain({ stepRef: 'D1' }), chain({ stepRef: 'D2' })] });
    expect(stepChainByRef(r, 'D2')?.stepRef).toBe('D2');
    expect(stepChainByRef(r, 'D9')).toBeNull();
    expect(stepChainByRef(r, null)).toBeNull();
  });
});

// === F8: stageHasDetails — есть ли у стадии раскрытие ===
describe('stageHasDetails', () => {
  it('cut/plan — по наличию задач; prod/review — по stepChains; qa/final — по проходам (rounds); merge — всегда', () => {
    const empty = run();
    expect(stageHasDetails('cut', empty)).toBe(false);
    expect(stageHasDetails('plan', empty)).toBe(false);
    expect(stageHasDetails('prod', empty)).toBe(false);
    expect(stageHasDetails('review', empty)).toBe(false);
    expect(stageHasDetails('qa', empty)).toBe(false);
    expect(stageHasDetails('final', empty)).toBe(false);
    expect(stageHasDetails('merge', empty)).toBe(true);

    const full = run({
      cut: [task({ id: 'c1' })],
      plan: [task({ id: 'p1' })],
      stepChains: [chain({ stepRef: 'D1', rounds: [round()] })],
      stages: stages({
        qa: { key: 'qa', state: 'done', passes: 1, taskIds: ['q1'], rounds: [round({ n: 1, verdict: 'returned' })] },
        final: { key: 'final', state: 'done', passes: 1, taskIds: ['f1'], rounds: [round({ n: 1, verdict: 'accepted' })] },
      }),
    });
    expect(stageHasDetails('cut', full)).toBe(true);
    expect(stageHasDetails('plan', full)).toBe(true);
    expect(stageHasDetails('prod', full)).toBe(true);
    expect(stageHasDetails('review', full)).toBe(true);
    expect(stageHasDetails('qa', full)).toBe(true);
    expect(stageHasDetails('final', full)).toBe(true);
  });

  it('review без витков → нет деталей (шаги есть, но кругов нет)', () => {
    const r = run({ stepChains: [chain({ stepRef: 'D1', rounds: [] })] });
    expect(stageHasDetails('review', r)).toBe(false);
  });
});

// === Рендер (SSR) ===
describe('PipelineFlow: рендер', () => {
  it('7 строк в порядке: Разрез → План → Производство → Ревью → Проверка → Финальное ревью → Мерж', () => {
    const html = render(run({ stages: stages() }));
    const order = ['Разрез', 'План', 'Производство', 'Ревью', 'Проверка', 'Финальное ревью', 'Мерж'];
    let pos = -1;
    for (const label of order) {
      const at = html.indexOf(label);
      expect(at, `подпись «${label}» не найдена`).toBeGreaterThan(pos);
      pos = at;
    }
  });

  it('нет stages → 7 строк pending, без падения (не считаем сами)', () => {
    const r = run(); // без stages
    expect(() => render(r)).not.toThrow();
    const html = render(r);
    for (const label of ['Разрез', 'План', 'Производство', 'Ревью', 'Проверка', 'Финальное ревью', 'Мерж']) {
      expect(html).toContain(label);
    }
    const from = html.indexOf('data-stage="merge"');
    const seg = html.slice(from, from + 400);
    expect(seg).not.toContain('var(--c-success-bg)');
  });

  it('финал done + run.ready=true, но merge в stages=pending → «Мерж» не done (не зелёный)', () => {
    const r = run({
      ready: true,
      stages: stages({
        final: { key: 'final', state: 'done', passes: 1, taskIds: ['f1'] },
        merge: { key: 'merge', state: 'pending', passes: 0, taskIds: [] },
      }),
    });
    const html = render(r);
    const from = html.indexOf('data-stage="merge"');
    expect(from).toBeGreaterThan(-1);
    const seg = html.slice(from, from + 500);
    expect(seg, '«Мерж» pending → нет зелёной подсветки').not.toContain('var(--c-success-bg)');
    expect(seg, 'точка «Мерж» не зелёная').not.toContain('var(--c-success)');
  });

  it('pending/absent → пунктирный сегмент (2px dashed C.dashed)', () => {
    const r2 = run({
      stages: stages({
        qa: { key: 'qa', state: 'absent', passes: 0, taskIds: [] },
      }),
    });
    const html2 = render(r2);
    const from = html2.indexOf('data-stage="qa"');
    const seg = html2.slice(from, from + 600);
    expect(seg).toContain('2px dashed var(--c-dashed)');
  });

  it('толщина по passes: passes=3 → 5px, passes=5 → 8px (потолок)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'done', passes: 3, taskIds: [] },
        review: { key: 'review', state: 'done', passes: 5, taskIds: [] },
      }),
    });
    const html = render(r);
    const prodFrom = html.indexOf('data-stage="prod"');
    const prodSeg = html.slice(prodFrom).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    expect(prodSeg).toContain('width:5px');
    const reviewFrom = html.indexOf('data-stage="review"');
    const reviewSeg = html.slice(reviewFrom).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    expect(reviewSeg).toContain('width:8px');
  });

  it('бейджи из returnsBySource на десктопе (не только мобила), open>0 → тревожный тон', () => {
    const r = run({
      stages: stages(),
      returnsBySource: {
        review: { count: 18, open: 0 },
        qa: { count: 2, open: 1 },
        final: { count: 1, open: 0 },
      },
    });
    const html = render(r);
    expect(html).toContain('↺18 с ревью');
    expect(html).toContain('↺2 с проверки');
    expect(html).toContain('↺1 с финала');
    const at = html.indexOf('↺2 с проверки');
    const badge = html.slice(at - 200, at + 50);
    expect(badge, 'бейдж с open>0 — тревожный фон').toContain('var(--c-warning-bg)');
    expect(badge, 'бейдж с open>0 — тревожный текст').toContain('var(--c-warning-text)');
  });

  it('линия «Бэк · 2 из 3» + дорожка (3 back-шага, 2 done)', () => {
    const r = run({
      stages: stages(),
      lanes: [{ lane: 'back', personaIds: ['denis'], total: 3, accepted: 2, returns: 0 }],
    });
    expect(render(r)).toContain('Бэк · 2 из 3');
  });

  it('активная стадия помечена aria-current=step', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [] },
      }),
    });
    expect(render(r)).toContain('aria-current="step"');
  });

  it('runningSince → метка «сейчас · …» (tone=run, длительность от runningSince)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], runningSince: '2026-10-17T10:00:00Z' },
      }),
    });
    const html = render(r);
    expect(html).toContain('сейчас ·');
    expect(html).not.toContain('стоит');
  });

  it('tone=wait → wait-обводка (border C.info) + метка «ждёт вас · …»', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'wait', passes: 1, taskIds: [], runningSince: '2026-10-17T10:00:00Z' },
      }),
      awaitingHumanReason: 'нужно решение',
    });
    const html = render(r);
    expect(html).toContain('var(--c-info)');
    expect(html).toContain('ждёт вас ·');
  });

  it('tone=stopped → метка «стоит · …» (только упавший исполнитель)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'stopped', passes: 1, taskIds: [], runningSince: '2026-10-17T10:00:00Z' },
      }),
    });
    const html = render(r);
    expect(html).toContain('стоит ·');
    expect(html).not.toContain('стоит сейчас');
  });

  it('merge done + run.ready=true → «Мерж» done, но НЕ зелёный (зелёный только у итога, Майя M3)', () => {
    const r = run({
      ready: true,
      stages: stages({
        final: { key: 'final', state: 'done', passes: 1, taskIds: ['f1'] },
        merge: { key: 'merge', state: 'done', passes: 1, taskIds: ['m1'] },
      }),
    });
    const html = render(r);
    const from = html.indexOf('data-stage="merge"');
    expect(from).toBeGreaterThan(-1);
    const seg = html.slice(from, from + 1000);
    expect(seg, '«Мерж» done — без зелёного фона строки').not.toContain('var(--c-success-bg)');
    expect(seg, '«Мерж» done — точка не зелёная').not.toContain('var(--c-success)');
    expect(seg, '«Мерж» done — галка не successText').not.toContain('var(--c-success-text)');
  });
});

// === Ф6: различение pending/absent, метки, ×N, линии, рельса, minHeight, 360px ===
describe('PipelineFlow: Ф6 (строки и линии)', () => {
  const region = (html: string, key: string, next: string) =>
    html.slice(html.indexOf(`data-stage="${key}"`), html.indexOf(`data-stage="${next}"`));

  it('absent и pending различимы: подпись, aria-label, не-кнопка, без шеврона', () => {
    const r = run({
      stages: stages({
        cut: { key: 'cut', state: 'absent', passes: 0, taskIds: [] },
        plan: { key: 'plan', state: 'pending', passes: 0, taskIds: [] },
      }),
    });
    const html = render(r);
    const cut = region(html, 'cut', 'plan');
    expect(cut).toContain('задачи разреза в трекере нет');
    expect(cut).toContain('aria-label="Разрез — нет в трекере"');
    const plan = region(html, 'plan', 'prod');
    expect(plan).toContain('ещё не начат');
    expect(plan).toContain('aria-label="План — ещё не начат"');
    for (const seg of [cut, plan]) {
      expect(seg).toContain('role="group"');
      expect(seg).not.toContain('<button');
      expect(seg).not.toContain('M4 6l4 4 4-4');
    }
  });

  it('×N при passes>1, но НЕ у стадии «Ревью»', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'done', passes: 3, taskIds: [] },
        review: { key: 'review', state: 'done', passes: 4, taskIds: [] },
      }),
    });
    const html = render(r);
    const prod = region(html, 'prod', 'review');
    const review = region(html, 'review', 'qa');
    expect(prod).toContain('×3');
    expect(review).not.toContain('×4');
  });

  it('линия «Бэк · Денис, Кира · 3 из 4» (два исполнителя) и нет линии при total=0', () => {
    personaMap.set('denis', 'Денис');
    personaMap.set('kira', 'Кира');
    const r = run({
      stages: stages(),
      lanes: [{ lane: 'back', personaIds: ['denis', 'kira'], total: 4, accepted: 3, returns: 0 }],
    });
    const html = render(r);
    expect(html).toContain('Бэк · Денис, Кира · 3 из 4');
    expect(html).not.toContain('Фронт ·');
  });

  it('цвет рельсы — C.progressTrack (не C.textMuted)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'done', passes: 3, taskIds: [] },
      }),
    });
    const html = render(r);
    const prod = region(html, 'prod', 'review');
    expect(prod).toContain('var(--c-progress-track)');
  });

  it('minHeight строк: 32 (десктоп) — из TB.iconHitDesktop', () => {
    const r = run({ stages: stages() });
    const html = render(r);
    expect(html).toContain('min-height:32px');
  });

  it('360px: длинные имя/исполнители переносятся (overflow-wrap:anywhere), не горизонтальный скролл', () => {
    personaMap.set('denis', 'Денис');
    personaMap.set('kira', 'Кира');
    const r = run({
      stages: stages(),
      lanes: [{ lane: 'back', personaIds: ['denis', 'kira'], total: 4, accepted: 3, returns: 0 }],
    });
    const html = render(r);
    expect(html).toContain('overflow-wrap:anywhere');
  });
});

// === F6.1: доработка по ревью Ф4–Ф6 (Майя M1/M2 + миноры 1/2/5, Глеб) ===
describe('PipelineFlow: F6.1 (пилюли, тач-цель, глифы, защита)', () => {
  it('M1: дорожка и бейдж — R.max (999px), не R.full (50%)', () => {
    const r = run({
      stages: stages(),
      lanes: [{ lane: 'back', personaIds: [], total: 4, accepted: 3, returns: 2 }],
    });
    const html = render(r);
    const track = html.match(/role="progressbar"[^>]*style="([^"]*)"/)?.[1] ?? '';
    expect(track, 'дорожка — R.max').toContain('border-radius:999px');
    expect(track, 'дорожка — не R.full').not.toContain('border-radius:50%');
    const at = html.indexOf('↺2');
    expect(at).toBeGreaterThan(-1);
    const badge = html.slice(at - 200, at + 2);
    expect(badge, 'бейдж ↺ — R.max').toContain('border-radius:999px');
  });

  it('M2: minHeight на <button> (32 десктоп) и на строке pending/absent', () => {
    const r = run({
      stages: stages({
        merge: { key: 'merge', state: 'pending', passes: 0, taskIds: [] },
      }),
    });
    const html = render(r);
    const btn = html.match(/<button[^>]*data-testid="stage-row-prod"[^>]*style="([^"]*)"/)?.[1] ?? '';
    expect(btn, 'кнопка — minHeight 32').toContain('min-height:32px');
    const pending = html.match(/<div[^>]*data-testid="stage-row-merge"[^>]*style="([^"]*)"/)?.[1] ?? '';
    expect(pending, 'pending-строка — minHeight 32').toContain('min-height:32px');
  });

  it('минор 1: глиф absent — косая черта C.textMuted (круг C.dashed)', () => {
    const r = run({
      stages: stages({
        cut: { key: 'cut', state: 'absent', passes: 0, taskIds: [] },
      }),
    });
    const html = render(r);
    const cut = html.slice(html.indexOf('data-stage="cut"'), html.indexOf('data-stage="plan"'));
    expect(cut).toContain('stroke="var(--c-dashed)"');
    expect(cut).toContain('stroke="var(--c-text-muted)"');
  });

  it('минор 2: глиф done — у круга stroke C.textMuted (тёмная тема)', () => {
    const r = run({
      stages: stages({
        cut: { key: 'cut', state: 'done', passes: 1, taskIds: ['c1'] },
      }),
    });
    const html = render(r);
    const cut = html.slice(html.indexOf('data-stage="cut"'), html.indexOf('data-stage="plan"'));
    expect(cut).toContain('stroke="var(--c-text-muted)"');
  });

  it('минор 5: глиф loop — петля C.warning (дуга + стрелка), не круг', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'loop', passes: 1, taskIds: [] },
      }),
    });
    const html = render(r);
    const prod = html.slice(html.indexOf('data-stage="prod"'), html.indexOf('data-stage="review"'));
    expect(prod).toContain('M12.6 9.2A4.8 4.8 0 1 1 11.3 4.4');
    expect(prod).toContain('M9.2 2.6 12.6 3.4 11.8 6.8');
    expect(prod).toContain('stroke="var(--c-warning)"');
  });

  it('Глеб: run.steps=undefined → рендер не падает, раскрытия нет', () => {
    const r = run({
      stages: stages(),
      steps: undefined as unknown as FeatureTask[],
    });
    expect(() => render(r)).not.toThrow();
    const html = render(r);
    const prodBtn = html.match(/<button[^>]*data-testid="stage-row-prod"[^>]*>/)?.[0] ?? '';
    expect(prodBtn).toContain('aria-expanded="false"');
    expect(html).not.toContain('id="stage-details-prod"');
  });
});

// === Геометрия рельсы (SSR: из инлайн-стилей) ===
describe('PipelineFlow: геометрия рельсы', () => {
  it('РельсаНепрерывна: align-start, top=низ точки, bottom=верх следующей', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [] },
      }),
    });
    const html = render(r);
    const rowTag = html.match(/<button[^>]*data-testid="stage-row-prod"[^>]*>/)?.[0] ?? '';
    const rowStyle = rowTag.match(/style="([^"]*)"/)?.[1] ?? '';
    expect(rowStyle).toContain('align-items:start');
    const from = html.indexOf('data-stage="prod"');
    const segStyle = html.slice(from).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    const top = segStyle.match(/top:([^;]+)/)?.[1]?.trim();
    const bottom = segStyle.match(/bottom:([^;]+)/)?.[1]?.trim();
    const pad = SP.xs + 1;
    expect(top).toBe(`${pad + 16}px`);
    expect(bottom).toBe(`-${pad}px`);
  });

  it('ЦентрСовпадаетСКольцом: left сегмента = центр точки (рамка кнопки 1px + 16/2)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [] },
      }),
    });
    const html = render(r);
    const from = html.indexOf('data-stage="prod"');
    const segStyle = html.slice(from).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    const left = segStyle.match(/left:([^;]+)/)?.[1]?.trim();
    expect(left).toBeTruthy();
    const segmentLeft = parseFloat(left!);
    const dotCenter = 1 + 16 / 2;
    expect(Math.abs(segmentLeft - dotCenter), `left=${segmentLeft}, центр точки=${dotCenter}`).toBeLessThan(1);
  });
});

// === Покрытие: FONT.mono, null-guard (находки Глеба) ===
describe('Покрытие: null-guard', () => {
  it('null-эпик не роняет рендер (stages/lanes/returnsBySource опциональны)', () => {
    const r = run({ epic: null });
    expect(() => render(r)).not.toThrow();
  });
});

// === F8: раскрытие КАЖДОГО этапа + содержимое ===
describe('PipelineFlow: F8 (раскрытие этапов)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  // Агрегат, где у каждого этапа есть данные.
  function fullRun(): FeatureRun {
    return run({
      stages: stages({
        cut: { key: 'cut', state: 'done', passes: 1, taskIds: ['c1'], personaIds: ['alex'], taskCount: 1 },
        plan: { key: 'plan', state: 'done', passes: 1, taskIds: ['p1'], personaIds: ['egor'], taskCount: 1 },
        prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] },
        review: { key: 'review', state: 'done', passes: 1, taskIds: [], longestStepRef: 'D1' },
        qa: { key: 'qa', state: 'done', passes: 1, taskIds: ['q1'], rounds: [round({ n: 1, verdict: 'returned', reason: '3 дефекта', reviewTaskId: 'q1', personaId: 'vera' })] },
        final: { key: 'final', state: 'done', passes: 1, taskIds: ['f1'], rounds: [round({ n: 1, verdict: 'accepted', reviewTaskId: 'f1' })] },
        merge: { key: 'merge', state: 'done', passes: 1, taskIds: ['m1'] },
      }),
      cut: [task({ id: 'c1', title: 'Разрез фичи', personaId: 'alex', status: 'done' })],
      plan: [task({ id: 'p1', title: 'План шагов', personaId: 'egor', status: 'done' })],
      stepChains: [
        chain({ stepRef: 'D1', title: 'Бэк-шаг', lane: 'back', personaId: 'denis', outcome: 'accepted', rounds: [round({ n: 1, verdict: 'returned' }), round({ n: 2, verdict: 'accepted' })] }),
        chain({ stepRef: 'F1', title: 'Фронт-шаг', lane: 'front', personaId: 'kira', outcome: 'running', durationMinutes: 12 }),
      ],
      qa: [task({ id: 'q1', title: 'Проверка сценариев', personaId: 'vera', status: 'done' })],
      final: task({ id: 'f1', title: 'Финальное ревью', personaId: 'alex', status: 'done' }),
    });
  }

  it('«Разрез» раскрывается: «Задачи этапа» + заголовок задачи', () => {
    personaMap.set('alex', 'Александр');
    const text = clickStageText(fullRun(), 'cut');
    expect(text).toContain('Задачи этапа');
    expect(text).toContain('Разрез фичи');
  });

  it('«План» раскрывается: «Задачи этапа» + заголовок задачи', () => {
    personaMap.set('egor', 'Егор');
    const text = clickStageText(fullRun(), 'plan');
    expect(text).toContain('Задачи этапа');
    expect(text).toContain('План шагов');
  });

  it('«Производство» свёрнуто по умолчанию; по клику — строки шагов (stepRef, название, линия)', () => {
    personaMap.set('denis', 'Денис');
    personaMap.set('kira', 'Кира');
    // Без автораскрытия: в SSR-разметке контейнера деталей «Производства» (id=stage-details-prod)
    // нет. Проверяем id, а не подстроку: кнопка несёт aria-controls="stage-details-prod" всегда.
    const collapsed = render(fullRun());
    expect(collapsed).not.toContain('id="stage-details-prod"');
    // Клик раскрывает: строки шагов (stepRef, название, линия)
    const text = clickStageText(fullRun(), 'prod');
    expect(text).toContain('D1');
    expect(text).toContain('Бэк-шаг');
    expect(text).toContain('F1');
    expect(text).toContain('Фронт-шаг');
  });

  it('«Ревью» раскрывается: «Круги ревью по шагам» + ID + бусины вердиктов', () => {
    const text = clickStageText(fullRun(), 'review');
    expect(text).toContain('Круги ревью по шагам');
    expect(text).toContain('D1');
    // Бусины вердиктов: слова вердиктов (text-ноды «N»/«слово» склеиваются с пробелами —
    // проверяем сами слова, не точный формат).
    expect(text).toContain('вернул');
    expect(text).toContain('принял');
  });

  it('«Проверка» раскрывается: «Проходы проверки» + проход (исполнитель, вердикт, причина)', () => {
    personaMap.set('vera', 'Вера');
    const text = clickStageText(fullRun(), 'qa');
    expect(text).toContain('Проходы проверки');
    expect(text).toContain('Вера');
    expect(text).toContain('вернул');
    expect(text).toContain('3 дефекта');
  });

  it('«Финальное ревью» раскрывается: заголовок + проход с вердиктом', () => {
    const text = clickStageText(fullRun(), 'final');
    expect(text).toContain('Финальное ревью');
    expect(text).toContain('принял');
  });

  it('«Мерж»: три текста по состоянию (смержено / ждёт решения / начнётся после финала)', () => {
    // смержено: summary.mergedAt
    const merged = run({
      stages: stages({ merge: { key: 'merge', state: 'done', passes: 1, taskIds: ['m1'] } }),
      summary: { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 100, tasks: 5, steps: 3, loops: 1 },
    });
    expect(clickStageText(merged, 'merge')).toContain('Смержено');
    // ждёт решения: active + tone wait
    const waiting = run({
      stages: stages({ merge: { key: 'merge', state: 'active', tone: 'wait', passes: 1, taskIds: ['m1'] } }),
    });
    expect(clickStageText(waiting, 'merge')).toContain('Ждёт вашего решения — Марк получит запрос');
    // ещё не время: done (не wait)
    const notYet = run({
      stages: stages({ merge: { key: 'merge', state: 'done', passes: 1, taskIds: ['m1'] } }),
    });
    expect(clickStageText(notYet, 'merge')).toContain('Начнётся после финального ревью');
  });

  it('этапы без данных (pending/absent) — не-кнопки, без шеврона и деталей', () => {
    const r = run({
      stages: stages({
        cut: { key: 'cut', state: 'pending', passes: 0, taskIds: [] },
        plan: { key: 'plan', state: 'absent', passes: 0, taskIds: [] },
      }),
    });
    const html = render(r);
    const cut = html.slice(html.indexOf('data-stage="cut"'), html.indexOf('data-stage="plan"'));
    const plan = html.slice(html.indexOf('data-stage="plan"'), html.indexOf('data-stage="prod"'));
    for (const seg of [cut, plan]) {
      expect(seg).toContain('role="group"');
      expect(seg).not.toContain('<button');
      expect(seg).not.toContain('M4 6l4 4 4-4');
    }
  });

  it('одиночное раскрытие: клик по «Ревью» закрывает «Производство»', () => {
    const { root, container } = mount(createElement(PipelineFlow, { run: fullRun() }));
    const prod = findNodeByAttr(container, 'data-testid', 'stage-row-prod');
    const review = findNodeByAttr(container, 'data-testid', 'stage-row-review');
    expect(prod, 'кнопка «Производство»').not.toBeNull();
    expect(review, 'кнопка «Ревью»').not.toBeNull();
    // По умолчанию свёрнуто (без автораскрытия)
    expect(prod!.getAttribute('aria-expanded'), '«Производство» свёрнуто по умолчанию').toBe('false');
    // Раскрываем «Производство» вручную, затем клик по «Ревью» его закрывает (одиночное раскрытие)
    act(() => (reactProps(prod!)!.onClick as () => void)());
    expect(prod!.getAttribute('aria-expanded'), '«Производство» открыто').toBe('true');
    act(() => (reactProps(review!)!.onClick as () => void)());
    expect(prod!.getAttribute('aria-expanded'), '«Производство» закрылось').toBe('false');
    expect(review!.getAttribute('aria-expanded'), '«Ревью» открылось').toBe('true');
    root.unmount();
  });

  it('шаг раскрывается одиночно внутри этапа', () => {
    const { root, container } = mount(createElement(PipelineFlow, { run: fullRun(), initialExpandedStage: 'prod' }));
    const d1 = findNodeByAttr(container, 'data-testid', 'step-row-D1');
    const f1 = findNodeByAttr(container, 'data-testid', 'step-row-F1');
    expect(d1, 'строка шага D1').not.toBeNull();
    expect(f1, 'строка шага F1').not.toBeNull();
    expect(d1!.getAttribute('aria-expanded')).toBe('false');
    act(() => (reactProps(d1!)!.onClick as () => void)());
    expect(d1!.getAttribute('aria-expanded'), 'D1 раскрыт').toBe('true');
    act(() => (reactProps(f1!)!.onClick as () => void)());
    expect(d1!.getAttribute('aria-expanded'), 'D1 закрылся (F1 открыт)').toBe('false');
    expect(f1!.getAttribute('aria-expanded'), 'F1 открыт').toBe('true');
    root.unmount();
  });
});

// === F8: строка шага (метка источника, снят, длительность, «ждёт ревью», пружина) ===
describe('PipelineFlow: F8 (строка шага)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  it('метка источника «с ревью» только если source=review; без source — нет метки', () => {
    const withSrc = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг', source: 'review' })],
    });
    const html = renderOpen(withSrc);
    expect(html).toContain('с ревью');
    const noSrc = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг', source: null })],
    });
    expect(renderOpen(noSrc)).not.toContain('с ревью');
  });

  it('метки источника: «с проверки» (qa) и «с финала» (final)', () => {
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [
        chain({ stepRef: 'D1', title: 'A', source: 'qa' }),
        chain({ stepRef: 'D2', title: 'B', source: 'final' }),
      ],
    });
    const html = renderOpen(r);
    expect(html).toContain('с проверки');
    expect(html).toContain('с финала');
  });

  it('«· снят с выкладки» при outcome=withdrawn', () => {
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг', outcome: 'withdrawn' })],
    });
    expect(renderOpen(r)).toContain('· снят с выкладки');
  });

  it('идущий — длительность; pending — «ждёт ревью»; returned — только пружина (без текста)', () => {
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [
        chain({ stepRef: 'D1', title: 'Идёт', outcome: 'running', durationMinutes: 45 }),
        chain({ stepRef: 'D2', title: 'Ожидает', outcome: 'pending', durationMinutes: 0 }),
        chain({ stepRef: 'D3', title: 'Закрыт', outcome: 'returned', durationMinutes: 0, rounds: [round({ n: 1, verdict: 'accepted' })] }),
      ],
    });
    const html = renderOpen(r);
    // Идущий — длительность (мин/ч; точное значение не фиксируем — зависит от миллисекунд).
    expect(html).toMatch(/\d+ мин/);
    // pending — «ждёт ревью».
    expect(html).toContain('ждёт ревью');
    // returned — только пружина: «ждёт ревью» в строке D3 нет.
    const d3 = html.slice(html.indexOf('data-step-ref="D3"'));
    expect(d3, 'returned — без «ждёт ревью»').not.toContain('ждёт ревью');
  });

  it('пружина в строке шага: витки есть → Rounds; >6 колец → +N', () => {
    const many = Array.from({ length: 8 }, (_, i) => round({ n: i + 1, verdict: 'returned' as FeatureRoundVerdict }));
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг', outcome: 'accepted', rounds: many })],
    });
    const html = renderOpen(r);
    // 8 витков → показано 6 колец + «+2».
    expect(html).toContain('+2');
  });

  // r-2: название = номеру — «Ш8 Ш8» не дублируется (остаётся только моно-номер).
  // Считаем текстовые узлы «>Ш8<» (не атрибуты data-step-ref/data-testid, где номер тоже есть).
  // Мутация: верни название (stripStepRefPrefix без null-проверки) → «>Ш8<» два раза → краснеет.
  it('r-2: название = номеру — «Ш8 Ш8» не дублируется (остаётся только номер)', () => {
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'Ш8', title: 'Ш8' })],
    });
    const html = renderOpen(r);
    const count = (html.match(/>Ш8</g) ?? []).length;
    expect(count, 'текстовый узел «Ш8» ровно один (номер, без дубля названия)').toBe(1);
  });
});

// === F8: пружины-блоки (stg-rounds) у «Ревью»/«Проверки»/«Финального ревью» ===
describe('PipelineFlow: F8 (пружины-блоки)', () => {
  it('«Проверка»: пружина по stages[qa].rounds (видна, когда витки есть)', () => {
    const r = run({
      stages: stages({
        qa: { key: 'qa', state: 'done', passes: 1, taskIds: ['q1'], rounds: [round({ n: 1, verdict: 'returned' }), round({ n: 2, verdict: 'accepted' })] },
      }),
      qa: [task({ id: 'q1', title: 'Проверка' })],
    });
    const html = render(r);
    // Пружина-блок под «Проверкой»: aria-label пружины (Rounds) со списком витков.
    expect(html).toContain('2 витка');
  });

  it('«Финальное ревью»: пружина по stages[final].rounds', () => {
    const r = run({
      stages: stages({
        final: { key: 'final', state: 'done', passes: 1, taskIds: ['f1'], rounds: [round({ n: 1, verdict: 'accepted' })] },
      }),
      final: task({ id: 'f1', title: 'Финал' }),
    });
    const html = render(r);
    expect(html).toContain('1 виток');
  });

  it('«Ревью»: блок «дольше всех {ref} · {название}» + пружина, только если у шага >1 витка', () => {
    personaMap.set('denis', 'Денис');
    // >1 виток → блок виден.
    const many = run({
      stages: stages({ review: { key: 'review', state: 'done', passes: 1, taskIds: [], longestStepRef: 'D1' } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Длинный шаг', rounds: [round({ n: 1, verdict: 'returned' }), round({ n: 2, verdict: 'accepted' })] })],
    });
    const htmlMany = render(many);
    expect(htmlMany).toContain('дольше всех D1 · Длинный шаг');
    // ровно 1 виток → блока нет (мутация (б)).
    const one = run({
      stages: stages({ review: { key: 'review', state: 'done', passes: 1, taskIds: [], longestStepRef: 'D1' } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Один виток', rounds: [round({ n: 1, verdict: 'accepted' })] })],
    });
    expect(render(one)).not.toContain('дольше всех');
  });
});

// === F8: подпись-мета в разметке + поверхность деталей + minHeight + 360px ===
describe('PipelineFlow: F8 (мета, поверхность, 360px)', () => {
  it('мета «Егор · 1 задача» в строке «План» (done)', () => {
    personaMap.set('egor', 'Егор');
    const r = run({
      stages: stages({ plan: { key: 'plan', state: 'done', passes: 1, taskIds: ['p1'], personaIds: ['egor'], taskCount: 1 } }),
      plan: [task({ id: 'p1', title: 'План' })],
    });
    const html = render(r);
    expect(html).toContain('Егор · 1 задача');
  });

  it('мета с вердиктом: «Александр · возврат» (безличная) в строке «Ревью»', () => {
    personaMap.set('alex', 'Александр');
    const r = run({
      stages: stages({ review: { key: 'review', state: 'done', passes: 1, taskIds: [], personaIds: ['alex'], verdict: 'returned' } }),
    });
    expect(render(r)).toContain('Александр · возврат');
  });

  it('поверхность деталей: bgCard + borderLight + R.lg + margin-left 25px', () => {
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг' })],
    });
    const html = renderOpen(r);
    expect(html).toContain('stage-details-bgCard');
    expect(html).toContain('var(--c-bg-card)');
    expect(html).toContain('var(--c-border-light)');
    // margin-left 25px (DET_INDENT) у поверхности.
    expect(html).toContain('margin:2px 0 8px 25px');
  });

  it('minHeight строки шага: 28 (десктоп) — из TB.iconHitDesktop', () => {
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг' })],
    });
    const html = render(r);
    // useIsMobile()=false в SSR → десктоп: minHeight 32 у строк стадий;
    // строка шага — TB.iconHitDesktop=32 (та же шкала).
    expect(html).toContain('min-height:32px');
  });

  it('360px: пружина в правой колонке строки шага не переносится (flexShrink:0), название — переносится', () => {
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Очень длинное название шага для переноса', rounds: [round({ n: 1, verdict: 'returned' })] })],
    });
    const html = renderOpen(r);
    // Правая колонка (пружина + длительность) — flexShrink:0 (не переносится).
    expect(html).toContain('flex-shrink:0');
    // Название шага — overflow-wrap:anywhere (переносится).
    expect(html).toContain('overflow-wrap:anywhere');
  });
});

// === F8: мутации (тесты должны краснеть при поломке проверяемого поведения) ===
describe('PipelineFlow: F8 (мутации)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  // (а) шеврон у pending-этапа → тест краснеет.
  // pending — не-кнопка (role=group), шеврона быть не должно. Если нарисуем шеврон —
  // в разметке pending-строки появится path шеврона (M4 6l4 4 4-4).
  it('(а) pending-этап БЕЗ шеврона (мутация: нарисуй шеврон → краснеет)', () => {
    const r = run({
      stages: stages({
        cut: { key: 'cut', state: 'pending', passes: 0, taskIds: [] },
      }),
    });
    const html = render(r);
    const cut = html.slice(html.indexOf('data-stage="cut"'), html.indexOf('data-stage="plan"'));
    expect(cut, 'pending — нет шеврона').not.toContain('M4 6l4 4 4-4');
  });

  // (б) блок «дольше всех» при одном витке → тест краснеет.
  it('(б) «дольше всех» НЕ показывается при 1 витке (мутация: покажи → краснеет)', () => {
    const one = run({
      stages: stages({ review: { key: 'review', state: 'done', passes: 1, taskIds: [], longestStepRef: 'D1' } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Один виток', rounds: [round({ n: 1, verdict: 'accepted' })] })],
    });
    expect(render(one), '1 виток — блока «дольше всех» нет').not.toContain('дольше всех');
  });
});

// === F9: сигнал фокуса чипа «Шаг N фичи» (стор Ф1, per chat) ===
// Чип открывает план на нужном шаге: PipelineFlow раскрывает «Производство» и шаг, чья
// цепочка содержит задачу (taskIds ⊇ taskId); шаг не найден — только «Производство».
// Одноразовый: потреблённый seq запоминается (ререндер не перебивает ручное раскрытие);
// сигнал другого чата (sessionId) или другой фичи (featureId) этот поток не трогает.
describe('PipelineFlow: F9 (сигнал фокуса)', () => {
  beforeAll(() => {
    installFakeDom();
    // Node-окружение без rAF/cAF, а код зовёт ГЛОБАЛЬНЫЕ requestAnimationFrame/cancelAnimationFrame
    // (ретраит скролла по кадрам, cleanup — cancel). Стабы: rAF синхронный — элемент найден
    // сразу, один проход; cAF no-op.
    (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (fn: () => void) => { fn(); return 1; };
    (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame = () => {};
  });
  afterAll(() => { uninstallFakeDom(); });
  beforeEach(() => { __test.reset(); });

  function focusRun(): FeatureRun {
    return run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [] },
      }),
      stepChains: [
        chain({ stepRef: 'D1', title: 'Бэк-шаг', lane: 'back', taskIds: ['s1', 's2'] }),
        chain({ stepRef: 'F1', title: 'Фронт-шаг', lane: 'front', taskIds: ['s3'] }),
      ],
    });
  }

  function mountFlow(sessionId: string, r: FeatureRun) {
    // «Производство» раскрыто на старте: тесты берут строки шагов (data-step-ref/step-row)
    // ДО сигнала фокуса, а строки рендерятся только внутри раскрытого этапа.
    const node = createElement(ChatSessionContext.Provider, { value: sessionId }, createElement(PipelineFlow, { run: r, initialExpandedStage: 'prod' }));
    return mount(node);
  }

  it('сигнал: раскрыты «Производство» и шаг, чья цепочка содержит задачу (taskIds ⊇ taskId)', () => {
    const { root, container } = mountFlow('sess-1', focusRun());
    // Строка шага — data-step-ref (не id): скролл ищет её rootRef.querySelector (Глеб:
    // getElementById при двух PipelineFlow одной фичи брал первый). FNode без scrollIntoView —
    // стаб-запись вызова.
    const d1 = findNodeByAttr(container, 'data-step-ref', 'D1');
    expect(d1, 'строка шага D1 (data-step-ref)').not.toBeNull();
    const scrolled: unknown[] = [];
    (d1 as unknown as { scrollIntoView: (o: unknown) => void }).scrollIntoView = (o: unknown) => { scrolled.push(o); };

    act(() => { focusFeatureStep('sess-1', 'epic-1', 's2'); });

    expect(d1!.getAttribute('aria-expanded'), 'шаг D1 раскрыт (s2 в его цепочке)').toBe('true');
    expect(findNodeByAttr(container, 'data-testid', 'stage-row-prod')!.getAttribute('aria-expanded'), '«Производство» раскрыто').toBe('true');
    expect(scrolled, 'скролл на раскрытый шаг (block nearest, smooth — reduced-motion выключен)').toEqual([{ block: 'nearest', behavior: 'smooth' }]);
    root.unmount();
  });

  it('шаг не найден в цепочках — раскрыто только «Производство» (шага нет)', () => {
    const { root, container } = mountFlow('sess-1', focusRun());
    act(() => { focusFeatureStep('sess-1', 'epic-1', 'no-such-task'); });
    expect(findNodeByAttr(container, 'data-testid', 'stage-row-prod')!.getAttribute('aria-expanded'), '«Производство» раскрыто').toBe('true');
    expect(findNodeByAttr(container, 'data-testid', 'step-row-D1')!.getAttribute('aria-expanded'), 'D1 не раскрыт').toBe('false');
    expect(findNodeByAttr(container, 'data-testid', 'step-row-F1')!.getAttribute('aria-expanded'), 'F1 не раскрыт').toBe('false');
    root.unmount();
  });

  it('одноразовый: ререндер после сигнала не раскрывает заново шаг, свёрнутый пользователем', () => {
    const { root, container } = mountFlow('sess-1', focusRun());
    act(() => { focusFeatureStep('sess-1', 'epic-1', 's2'); });
    const d1 = findNodeByAttr(container, 'data-testid', 'step-row-D1')!;
    expect(d1.getAttribute('aria-expanded'), 'сигнал раскрыл D1').toBe('true');
    // Пользователь вручную сворачивает шаг
    act(() => (reactProps(d1)!.onClick as () => void)());
    expect(d1.getAttribute('aria-expanded'), 'пользователь свёрнул').toBe('false');
    // Ререндер (смена стора БЕЗ нового сигнала) — шаг не раскрывается заново
    act(() => { setTurnPlanMode('sess-1', 'feature'); });
    expect(d1.getAttribute('aria-expanded'), 'ререндер не перебивает ручное раскрытие').toBe('false');
    root.unmount();
  });

  it('сигнал другого чата (sessionId) не доходит', () => {
    const { root, container } = mountFlow('sess-A', focusRun());
    act(() => { focusFeatureStep('sess-B', 'epic-1', 's2'); });
    expect(findNodeByAttr(container, 'data-testid', 'step-row-D1')!.getAttribute('aria-expanded'), 'поток чата A сигнала чата B не получил').toBe('false');
    root.unmount();
  });

  it('сигнал другой фичи (featureId не совпадает) поток не трогает', () => {
    const { root, container } = mountFlow('sess-1', focusRun());
    act(() => { focusFeatureStep('sess-1', 'other-epic', 's2'); });
    expect(findNodeByAttr(container, 'data-testid', 'step-row-D1')!.getAttribute('aria-expanded'), 'D1 не раскрыт').toBe('false');
    root.unmount();
  });
});

// === F9.1: четыре ветки исхода строки шага (текст, пульс, глиф) + мутация (а) ===
// По бэку (FeatureProjector): running → пульс + длительность; pending → «ждёт ревью» +
// спокойный глиф без пульса; accepted/returned/withdrawn → справа только пружина, без текста.
describe('PipelineFlow: F9.1 (ветки исхода строки шага)', () => {
  function oneStep(outcome: string, over: Partial<FeatureStepChain> = {}): FeatureRun {
    return run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг', outcome, durationMinutes: 30, ...over })],
    });
  }
  // Срез HTML от строки шага D1 (одна цепочка — маркеры недвусмысленны).
  const row = (html: string) => html.slice(html.indexOf('data-step-ref="D1"'));

  it('running: пульс + длительность «N мин», без «ждёт ревью»', () => {
    const r = row(renderOpen(oneStep('running')));
    expect(r, 'running — пульс').toContain('feature-pulse');
    expect(r, 'running — длительность').toMatch(/\d+ мин/);
    expect(r, 'running — без «ждёт ревью»').not.toContain('ждёт ревью');
  });

  it('pending: «ждёт ревью», спокойный глиф БЕЗ пульса', () => {
    const r = row(renderOpen(oneStep('pending')));
    expect(r, 'pending — «ждёт ревью»').toContain('ждёт ревью');
    expect(r, 'pending — без пульса').not.toContain('feature-pulse');
  });

  it('accepted: только пружина — без «ждёт ревью» и без длительности', () => {
    const r = row(renderOpen(oneStep('accepted')));
    expect(r, 'accepted — без «ждёт ревью»').not.toContain('ждёт ревью');
    expect(r, 'accepted — без длительности').not.toMatch(/\d+ мин/);
    expect(r, 'accepted — без пульса').not.toContain('feature-pulse');
  });

  it('returned: только пружина — без «ждёт ревью» и без длительности', () => {
    const r = row(renderOpen(oneStep('returned')));
    expect(r, 'returned — без «ждёт ревью»').not.toContain('ждёт ревью');
    expect(r, 'returned — без длительности').not.toMatch(/\d+ мин/);
    expect(r, 'returned — без пульса').not.toContain('feature-pulse');
  });

  it('withdrawn: только пружина + «· снят с выкладки»', () => {
    const r = row(renderOpen(oneStep('withdrawn')));
    expect(r, 'withdrawn — без «ждёт ревью»').not.toContain('ждёт ревью');
    expect(r, 'withdrawn — без длительности').not.toMatch(/\d+ мин/);
    expect(r, 'withdrawn — «· снят с выкладки»').toContain('· снят с выкладки');
  });

  // (а) мутация: верни старую трактовку pending (пульс у «ждёт ревью») → тест краснеет.
  it('(а) pending — «ждёт ревью» БЕЗ пульса (мутация: пульс у «ждёт ревью» → краснеет)', () => {
    const r = row(renderOpen(oneStep('pending')));
    expect(r).toContain('ждёт ревью');
    expect(r, 'pending — пульса нет (старая трактовка — пульс, тест краснеет)').not.toContain('feature-pulse');
  });
});

// === F9.1: длительность running тикает (fake timers), без обратного пересчёта Date.now()−… ===
describe('PipelineFlow: F9.1 (длительность running тикает)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  it('база = durationMinutes; +2 мин (fake timers) → 47 мин (тикает, не пересчёт от Date.now()−…)', () => {
    vi.useFakeTimers();
    try {
      const r = run({
        stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
        stepChains: [chain({ stepRef: 'D1', title: 'Идёт', outcome: 'running', durationMinutes: 45 })],
      });
      const { root, container } = mount(createElement(PipelineFlow, { run: r, initialExpandedStage: 'prod' }));
      const d1 = findNodeByAttr(container, 'data-step-ref', 'D1')!;
      expect(textNodes(d1).join(' '), 'старт — 45 мин (база = durationMinutes)').toMatch(/45 мин/);
      act(() => { vi.advanceTimersByTime(120000); });
      expect(textNodes(d1).join(' '), 'после +2 мин — 47 мин (тикает)').toMatch(/47 мин/);
      root.unmount();
    } finally {
      vi.useRealTimers();
    }
  });
});

// === F9.1: детали «Разреза»/«Плана» (исполнитель, статус, время, ссылка) ===
describe('PipelineFlow: F9.1 (детали Разрез/План)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  function cutRun(): FeatureRun {
    return run({
      stages: stages({ cut: { key: 'cut', state: 'done', passes: 1, taskIds: ['c1'] } }),
      cut: [task({ id: 'c1', title: 'Разрез фичи', personaId: 'alex', status: 'done', projectId: 'proj-1', createdAt: '2026-10-15T07:49:00Z', completedAt: '2026-10-15T08:09:00Z' })],
    });
  }

  it('строка задачи: исполнитель (имя) + статус + время mono «ЧЧ:ММ–ЧЧ:ММ» + ссылка «Открыть задачу»', () => {
    personaMap.set('alex', 'Александр');
    const { root, container } = mount(createElement(PipelineFlow, { run: cutRun() }));
    const cut = findNodeByAttr(container, 'data-testid', 'stage-row-cut')!;
    act(() => (reactProps(cut)!.onClick as () => void)());
    const text = textNodes(container).join(' ');
    expect(text, 'имя исполнителя').toContain('Александр');
    expect(text, 'статус «закрыто»').toContain('закрыто');
    expect(text, 'время (местный пояс)').toContain(`${localHHMM('2026-10-15T07:49:00Z')}–${localHHMM('2026-10-15T08:09:00Z')}`);
    expect(text, 'ссылка «Открыть задачу»').toContain('Открыть задачу');
    root.unmount();
  });
});

// === F9.1: детали «Проверки»/«Финала» (время + ссылка на reviewTaskId) ===
describe('PipelineFlow: F9.1 (детали Проверки/Финала)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  function qaRun(): FeatureRun {
    return run({
      stages: stages({
        qa: { key: 'qa', state: 'done', passes: 1, taskIds: ['q1'], rounds: [round({ n: 1, verdict: 'returned', personaId: 'vera', reviewTaskId: 'q1', startedAt: '2026-10-15T08:36:00Z', completedAt: '2026-10-15T08:45:00Z' })] },
      }),
      qa: [task({ id: 'q1', title: 'Проверка', personaId: 'vera', projectId: 'proj-1' })],
    });
  }

  it('проход: время «ЧЧ:ММ–ЧЧ:ММ» + ссылка «Открыть задачу» на reviewTaskId', () => {
    personaMap.set('vera', 'Вера');
    const { root, container } = mount(createElement(PipelineFlow, { run: qaRun() }));
    const qa = findNodeByAttr(container, 'data-testid', 'stage-row-qa')!;
    act(() => (reactProps(qa)!.onClick as () => void)());
    const text = textNodes(container).join(' ');
    expect(text, 'время (местный пояс)').toContain(`${localHHMM('2026-10-15T08:36:00Z')}–${localHHMM('2026-10-15T08:45:00Z')}`);
    expect(text, 'ссылка «Открыть задачу»').toContain('Открыть задачу');
    root.unmount();
  });

  it('нет reviewTaskId — ссылки нет (без висящих разделителей)', () => {
    const r = run({
      stages: stages({
        qa: { key: 'qa', state: 'done', passes: 1, taskIds: ['q1'], rounds: [round({ n: 1, verdict: 'accepted', reviewTaskId: null })] },
      }),
    });
    const { root, container } = mount(createElement(PipelineFlow, { run: r }));
    const qa = findNodeByAttr(container, 'data-testid', 'stage-row-qa')!;
    act(() => (reactProps(qa)!.onClick as () => void)());
    expect(textNodes(container).join(' '), 'без reviewTaskId — ссылки нет').not.toContain('Открыть задачу');
    root.unmount();
  });
});

// === F9.1: детали «Мержа» (аватар исполнителя) ===
describe('PipelineFlow: F9.1 (детали Мержа)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  it('мерж: аватар + имя исполнителя (из stages[merge].personaIds)', () => {
    personaMap.set('mark', 'Марк');
    const r = run({
      stages: stages({ merge: { key: 'merge', state: 'done', passes: 1, taskIds: ['m1'], personaIds: ['mark'] } }),
      summary: { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 100, tasks: 5, steps: 3, loops: 1 },
    });
    const { root, container } = mount(createElement(PipelineFlow, { run: r }));
    const merge = findNodeByAttr(container, 'data-testid', 'stage-row-merge')!;
    act(() => (reactProps(merge)!.onClick as () => void)());
    const text = textNodes(container).join(' ');
    expect(text, 'имя исполнителя мержа').toContain('Марк');
    expect(text, 'текст «Смержено» остаётся').toContain('Смержено');
    root.unmount();
  });
});

// === F9.1: шеврон только при stageHasDetails ===
describe('PipelineFlow: F9.1 (шеvron)', () => {
  it('«Проверка» без проходов — шеврона нет; с проходами — есть', () => {
    // Без проходов: stageHasDetails(qa)=false → шеврон (path M4 6l4 4 4-4) в строке нет.
    const noRounds = run({
      stages: stages({ qa: { key: 'qa', state: 'done', passes: 1, taskIds: ['q1'], rounds: [] } }),
    });
    const htmlNo = render(noRounds);
    const qaNo = htmlNo.slice(htmlNo.indexOf('data-stage="qa"'), htmlNo.indexOf('data-stage="final"'));
    expect(qaNo, 'без проходов — шеврона нет').not.toContain('M4 6l4 4 4-4');
    // С проходами: stageHasDetails(qa)=true → шеврон есть.
    const withRounds = run({
      stages: stages({ qa: { key: 'qa', state: 'done', passes: 1, taskIds: ['q1'], rounds: [round({ n: 1, verdict: 'accepted' })] } }),
    });
    const htmlYes = render(withRounds);
    const qaYes = htmlYes.slice(htmlYes.indexOf('data-stage="qa"'), htmlYes.indexOf('data-stage="final"'));
    expect(qaYes, 'с проходами — шеврон есть').toContain('M4 6l4 4 4-4');
  });
});

// === F9.1: скролл к шагу при ДВУХ смонтированных PipelineFlow одной фичи (мутация (б)) ===
describe('PipelineFlow: F9.1 (скролл, два PipelineFlow)', () => {
  beforeAll(() => {
    installFakeDom();
    (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (fn: () => void) => { fn(); return 1; };
    (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame = () => {};
  });
  afterAll(() => { uninstallFakeDom(); });
  beforeEach(() => { __test.reset(); });

  function twoFlows() {
    // Два PipelineFlow одной фичи (карточка плана + меню пилюли) — один sessionId.
    const r = run({
      stages: stages({ prod: { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Бэк-шаг', lane: 'back', taskIds: ['s1'] })],
    });
    const node1 = createElement(ChatSessionContext.Provider, { value: 'sess-1' }, createElement(PipelineFlow, { run: r, initialExpandedStage: 'prod' }));
    const node2 = createElement(ChatSessionContext.Provider, { value: 'sess-1' }, createElement(PipelineFlow, { run: r, initialExpandedStage: 'prod' }));
    const m1 = mount(node1);
    const m2 = mount(node2);
    return { c1: m1.container, c2: m2.container };
  }

  it('(б) скролл попадает в СВОЙ узел (мутация: getElementById вместо ref → краснеет)', () => {
    const { c1, c2 } = twoFlows();
    // Отмечаем scrollIntoView обоих D1: каждый PipelineFlow скроллит СВОЙ (rootRef.querySelector).
    const d1a = findNodeByAttr(c1, 'data-step-ref', 'D1')!;
    const d1b = findNodeByAttr(c2, 'data-step-ref', 'D1')!;
    const scrolled: string[] = [];
    (d1a as unknown as { scrollIntoView: (o: unknown) => void }).scrollIntoView = () => { scrolled.push('first'); };
    (d1b as unknown as { scrollIntoView: (o: unknown) => void }).scrollIntoView = () => { scrolled.push('second'); };

    act(() => { focusFeatureStep('sess-1', 'epic-1', 's1'); });

    // Оба PipelineFlow скроллят СВОЙ узел (rootRef.querySelector), а не document.getElementById
    // (тот брал бы первый — мутация (б): верни getElementById → один из узлов не скроллится).
    expect(scrolled, 'оба PipelineFlow скроллят свой узел').toContain('first');
    expect(scrolled).toContain('second');
  });
});

// === K-2: строка шага — fontFamily inherit (не Arial) + явный fontSize FS.sm (12) ===
describe('PipelineFlow: K-2 (строка шага: шрифт)', () => {
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  function runWithStep(): FeatureRun {
    return run({
      stages: stages({ prod: { key: 'prod', state: 'done', passes: 1, taskIds: [] } }),
      stepChains: [chain({ stepRef: 'D1', title: 'Шаг', outcome: 'accepted', durationMinutes: 30 })],
    });
  }

  it('кнопка строки: font-size 12 (FS.sm) и font-family inherit', () => {
    const { root, container } = mount(createElement(PipelineFlow, { run: runWithStep(), initialExpandedStage: 'prod' }));
    const row = findNodeByAttr(container, 'data-step-ref', 'D1');
    expect(row, 'строка шага D1 не найдена').not.toBeNull();
    expect(row!.style.fontSize, 'K-2: font-size 12px').toBe('12px');
    expect(row!.style.fontFamily, 'K-2: font-family inherit').toBe('inherit');
    root.unmount();
  });
});
