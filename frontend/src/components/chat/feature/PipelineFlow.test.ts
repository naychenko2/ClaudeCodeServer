// Конвейер фичи (PipelineFlow) — строго «фронт только показывает»: стадии/линии/возвраты
// приходят из бэка (run.stages / run.lanes / run.returnsBySource), компонент раскладывает.
//
// Окружение node (без jsdom): чистые функции (stageByKey, stageState, stageWidth, laneInfo,
// laneTrackWidth, returnBadges, toggleExpanded) + рендер renderToStaticMarkup
// (паттерн FeatureRunView.test.ts). useIsMobile() = false → десктоп-раскладка.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { act } from 'react';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureRun, FeatureTask, FeatureStage, StageKey } from '../../../types/featureRun';
import { C, SP, FONT } from '../../../lib/design';
import { findNodeByAttr, installFakeDom, mount, reactProps, uninstallFakeDom } from '../__tests__/fakeDom';
import {
  PipelineFlow, stageByKey, stageState, stageWidth,
  laneInfo, laneTrackWidth, returnBadges, toggleExpanded,
} from './PipelineFlow';

// Стор персон (lib/personas) — мок: hoisted-карта id→имя. Исполнитель резолвится по имени
// (блокер 2.16.2); тесты подставляют нужных персон. Компоненту нужен только .name.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', () => ({
  getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
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
// `over` — точечные переопределения по ключу стадии.
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

const render = (r: FeatureRun) => renderToStaticMarkup(createElement(PipelineFlow, { run: r }));

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
    // Подписи: «с ревью» / «с проверки» / «с финала».
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
    // Все 7 подписей на месте.
    for (const label of ['Разрез', 'План', 'Производство', 'Ревью', 'Проверка', 'Финальное ревью', 'Мерж']) {
      expect(html).toContain(label);
    }
    // «Мерж» pending → не зелёный (нет successBg), не done (нет галочки).
    const from = html.indexOf('data-stage="merge"');
    const seg = html.slice(from, from + 400);
    expect(seg).not.toContain('var(--c-success-bg)');
  });

  // Ключевая фикстура: «финал зелёный, merge-задачи нет» → «Мерж» НЕ done.
  // Мутация (а): вернуть `|| run.ready` в состояние «Мерж» → эта строка покраснеет.
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
    // Строка «Мерж» без зелёной подсветки (C.successBg) — бэк сказал pending, не done.
    const seg = html.slice(from, from + 500);
    expect(seg, '«Мерж» pending → нет зелёной подсветки').not.toContain('var(--c-success-bg)');
    // Точка «Мерж» — не зелёная галочка (C.success / C.successText).
    expect(seg, 'точка «Мерж» не зелёная').not.toContain('var(--c-success)');
  });

  it('pending/absent → пунктирный сегмент (2px dashed C.dashed)', () => {
    const r = run({
      stages: stages({
        merge: { key: 'merge', state: 'absent', passes: 0, taskIds: [] },
      }),
    });
    const html = render(r);
    // Сегмент «Мерж» — последний, сегмента под ним нет; проверяем пунктир на «Финальном ревью»
    // (pending по умолчанию в base) — нет, base final=done. Возьму qa=absent.
    const r2 = run({
      stages: stages({
        qa: { key: 'qa', state: 'absent', passes: 0, taskIds: [] },
      }),
    });
    const html2 = render(r2);
    // Сегмент после «Проверки» (qa=absent) — пунктирный: 2px dashed C.dashed.
    const from = html2.indexOf('data-stage="qa"');
    const seg = html2.slice(from, from + 600);
    expect(seg).toContain('2px dashed var(--c-dashed)');
    // (html1 не используется напрямую — оставляю для наглядности фикстуры.)
    void html;
  });

  it('толщина по passes: passes=3 → 5px, passes=5 → 8px (потолок)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'done', passes: 3, taskIds: [] },
        review: { key: 'review', state: 'done', passes: 5, taskIds: [] },
      }),
    });
    const html = render(r);
    // Сегмент «Производства» (passes=3) → width 5px.
    const prodFrom = html.indexOf('data-stage="prod"');
    const prodSeg = html.slice(prodFrom).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    expect(prodSeg).toContain('width:5px');
    // Сегмент «Ревью» (passes=5) → width 8px (потолок).
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
    // Все три бейджа присутствуют (на всех ширинах).
    expect(html).toContain('↺18 с ревью');
    expect(html).toContain('↺2 с проверки');
    expect(html).toContain('↺1 с финала');
    // open>0 (с проверки) → тревожный тон: warningBg + warningText.
    // Ищем бейдж «с проверки» и проверяем, что он несёт warning-токены.
    const at = html.indexOf('↺2 с проверки');
    const badge = html.slice(at - 200, at + 50);
    expect(badge, 'бейдж с open>0 — тревожный фон').toContain('var(--c-warning-bg)');
    expect(badge, 'бейдж с open>0 — тревожный текст').toContain('var(--c-warning-text)');
  });

  it('3 back-шага (2 done) → линия «Бэк · 2 из 3» + дорожка', () => {
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
    // Метка текущего этапа: «сейчас · …» (tone=run). Не «стоит» (только упавший).
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
    // wait-обводка: border 1px solid C.info (var(--c-info)).
    expect(html).toContain('var(--c-info)');
    // Метка текущего этапа: «ждёт вас · …».
    expect(html).toContain('ждёт вас ·');
  });

  it('tone=stopped → метка «стоит · …» (только упавший исполнитель)', () => {
    const r = run({
      stages: stages({
        prod: { key: 'prod', state: 'active', tone: 'stopped', passes: 1, taskIds: [], runningSince: '2026-10-17T10:00:00Z' },
      }),
    });
    const html = render(r);
    // «Стоит» — только для упавшего; старая подпись «стоит сейчас» (значила и «идёт») — нет.
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
    // Строка «Мерж» — обычный done (bgSelected + textMuted), без зелёной подсветки и зелёной точки.
    const seg = html.slice(from, from + 1000);
    expect(seg, '«Мерж» done — без зелёного фона строки').not.toContain('var(--c-success-bg)');
    expect(seg, '«Мерж» done — точка не зелёная').not.toContain('var(--c-success)');
    expect(seg, '«Мерж» done — галка не successText').not.toContain('var(--c-success-text)');
  });
});

// === Ф6: различение pending/absent, метки, ×N, линии, рельса, minHeight, 360px ===
describe('PipelineFlow: Ф6 (строки и линии)', () => {
  // Регион стадии в HTML: от её data-stage до data-stage следующей (для точечных проверок).
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
    // absent: подпись «задачи разреза в трекере нет» + aria-label «Разрез — нет в трекере».
    const cut = region(html, 'cut', 'plan');
    expect(cut).toContain('задачи разреза в трекере нет');
    expect(cut).toContain('aria-label="Разрез — нет в трекере"');
    // pending: подпись «ещё не начат» + aria-label «План — ещё не начат».
    const plan = region(html, 'plan', 'prod');
    expect(plan).toContain('ещё не начат');
    expect(plan).toContain('aria-label="План — ещё не начат"');
    // Обе — не-кнопки (div role=group), без шеврона (path M4 6l4 4 4-4) и без <button>.
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
    // «Производство» (passes=3) — ×3; «Ревью» (passes=4) — без ×N.
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
    // Два исполнителя — через запятую; неизвестный id не выводится (тут оба известны).
    expect(html).toContain('Бэк · Денис, Кира · 3 из 4');
    // front: total=0 → линия не рисуется.
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
    // Сегмент рельсы «Производства» — background C.progressTrack.
    expect(prod).toContain('var(--c-progress-track)');
  });

  it('minHeight строк: 32 (десктоп) — из TB.iconHitDesktop', () => {
    const r = run({ stages: stages() });
    const html = render(r);
    // useIsMobile()=false в SSR → десктоп: minHeight 32.
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
    // Подпись линии и имя стадии — overflow-wrap:anywhere: длинное переносится, а не скроллится.
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
    // Дорожка (progressbar) — border-radius:999px (R.max), не 50% (R.full → «линза»).
    const track = html.match(/role="progressbar"[^>]*style="([^"]*)"/)?.[1] ?? '';
    expect(track, 'дорожка — R.max').toContain('border-radius:999px');
    expect(track, 'дорожка — не R.full').not.toContain('border-radius:50%');
    // Бейдж ↺2 — тоже R.max.
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
    // Кнопка (done-стадия) несёт minHeight 32 (TB.iconHitDesktop) в своём style.
    const btn = html.match(/<button[^>]*data-testid="stage-row-prod"[^>]*style="([^"]*)"/)?.[1] ?? '';
    expect(btn, 'кнопка — minHeight 32').toContain('min-height:32px');
    // Не-кнопка (pending) — тот же minHeight, ритм строк не прыгает.
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
    // Круг — C.dashed, черта — C.textMuted (главный знак, в тёмной теме C.dashed сливается).
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
    // Круг done — обводка C.textMuted (иначе в тёмной теме bgSelected≈bgWhite и круг невидим).
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
    // Петля: дуга (штрих 2.2) + стрелка (штрих 2), обе C.warning; не «просто круг».
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
    // prod без шагов → aria-expanded=false и блока деталей нет.
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
    // 1) Грид строки — на <button>: align-items:start (точка у верха).
    const rowTag = html.match(/<button[^>]*data-testid="stage-row-prod"[^>]*>/)?.[0] ?? '';
    const rowStyle = rowTag.match(/style="([^"]*)"/)?.[1] ?? '';
    expect(rowStyle).toContain('align-items:start');
    // 2) Сегмент «Производства»: top = низ текущей точки, bottom = до верха следующей.
    const from = html.indexOf('data-stage="prod"');
    const segStyle = html.slice(from).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    const top = segStyle.match(/top:([^;]+)/)?.[1]?.trim();
    const bottom = segStyle.match(/bottom:([^;]+)/)?.[1]?.trim();
    const pad = SP.xs + 1; // рамка 1px + паддинг SP.xs = 5
    expect(top).toBe(`${pad + 16}px`);        // 21 = верх точки (5) + 16 (низ точки)
    expect(bottom).toBe(`-${pad}px`);         // -5 = до верха следующей точки (5)
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
    // Точка лежит в гриде кнопки, сдвинутой её рамкой 1px вправо: центр = 1 + 16/2 = 9.
    const dotCenter = 1 + 16 / 2;
    expect(Math.abs(segmentLeft - dotCenter), `left=${segmentLeft}, центр точки=${dotCenter}`).toBeLessThan(1);
  });
});

// === Покрытие: FONT.mono, null-guard (находки Глеба) ===
describe('Покрытие: FONT.mono, null-guard', () => {
  it('FONT.mono: токен моноширинного шрифта (не пустой, содержит monospace)', () => {
    expect(FONT.mono).toBeTruthy();
    expect(FONT.mono).toContain('monospace');
  });

  it('null-guard: null-эпик не роняет рендер (stages/lanes/returnsBySource опциональны)', () => {
    const r = run({ epic: null });
    expect(() => render(r)).not.toThrow();
  });
});

// === Раскрытие строк стадий (2.15): aria-expanded + список шагов «Производства» ===

// Агрегат с шагами в линиях бэк/фронт (для проверки раскрытия «Производства»).
// stages: базовые (prod/review — done → строки-кнопки; без stages все стадии pending → не-кнопки).
function runWithSteps(): FeatureRun {
  return run({
    stages: stages(),
    steps: [
      task({ id: 's1', title: 'Бэк-шаг', stepRef: 'Ш1', lane: 'back', status: 'done', personaId: 'denis' }),
      task({
        id: 's2', title: 'Фронт-шаг', stepRef: 'Ш2', lane: 'front', status: 'inProgress',
        personaId: 'kira', awaitingHumanSince: '2026-10-17T10:00:00Z', awaitingHumanReason: 'нужен выбор',
      }),
    ],
  });
}

describe('PipelineFlow: раскрытие строк (2.15)', () => {
  // fake-DOM для интерактивного кейса (клик); SSR-кейсы на renderToStaticMarkup не зависят от него.
  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });

  // SSR: «Производство» раскрыто по умолчанию → список шагов в разметке.
  it('Prod_СодержитШаги_РаскрытПоУмолчанию: шаги показаны (stepRef, lane, имя исполнителя, ожидание)', () => {
    personaMap.set('denis', 'Денис');
    personaMap.set('kira', 'Кира');
    const html = render(runWithSteps());
    expect(html).toContain('stage-details-prod');
    expect(html).toContain('Ш1');
    expect(html).toContain('Бэк-шаг');
    expect(html).toContain('Ш2');
    expect(html).toContain('Фронт-шаг');
    expect(html).toContain('Денис');
    expect(html).toContain('Кира');
    expect(html).toContain('нужен выбор');
    expect(html).toMatch(/с \d{2}:\d{2}/);
  });

  it('Review_НетШагов_НетДеталей: aria-expanded=false, деталей нет', () => {
    const r = run({
      stages: stages(),
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    const html = render(r);
    const reviewBtn = html.match(/<button[^>]*data-testid="stage-row-review"[^>]*>/)?.[0] ?? '';
    expect(reviewBtn).toContain('aria-expanded="false"');
    expect(html).not.toContain('<div id="stage-details-review"');
  });

  it('StageRows_КликПереключаетРаскрытие: aria-expanded переключается', () => {
    const { root, container } = mount(createElement(PipelineFlow, { run: runWithSteps() }));
    const btn = findNodeByAttr(container, 'data-testid', 'stage-row-prod');
    expect(btn, 'кнопка «Производство» не найдена').not.toBeNull();
    expect(btn!.getAttribute('aria-expanded')).toBe('true');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.getAttribute('aria-expanded')).toBe('false');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.getAttribute('aria-expanded')).toBe('true');
    root.unmount();
  });

  it('StageRows_КликОткрываетТолькоОдну: клик по другой стадии закрывает «Производство»', () => {
    const { root, container } = mount(createElement(PipelineFlow, { run: runWithSteps() }));
    const prod = findNodeByAttr(container, 'data-testid', 'stage-row-prod');
    const review = findNodeByAttr(container, 'data-testid', 'stage-row-review');
    expect(prod, 'кнопка «Производство» не найдена').not.toBeNull();
    expect(review, 'кнопка «Ревью» не найдена').not.toBeNull();
    expect(prod!.getAttribute('aria-expanded')).toBe('true');
    act(() => (reactProps(review!)!.onClick as () => void)());
    expect(prod!.getAttribute('aria-expanded'), '«Производство» должно закрыться при открытии «Ревью»').toBe('false');
    act(() => (reactProps(review!)!.onClick as () => void)());
    expect(prod!.getAttribute('aria-expanded')).toBe('false');
    root.unmount();
  });

  it('Review_КликНетШагов_НетStageDetails: клик по «Ревью» — деталей (шагов) нет', () => {
    const { root, container } = mount(createElement(PipelineFlow, { run: runWithSteps() }));
    const review = findNodeByAttr(container, 'data-testid', 'stage-row-review');
    expect(review, 'кнопка «Ревью» не найдена').not.toBeNull();
    act(() => (reactProps(review!)!.onClick as () => void)());
    expect(review!.getAttribute('aria-expanded')).toBe('false');
    expect(findNodeByAttr(container, 'id', 'stage-details-review'), 'у «Ревью» не должно быть деталей').toBeNull();
    root.unmount();
  });
});

// === Исполнитель, поверхность деталей, глифы статуса (2.16.2–2.16.4) ===
describe('PipelineFlow: исполнитель / поверхность / глифы (2.16)', () => {
  it('Step_Who_ОтображаетИмяПерсоны: personaId → имя (не GUID)', () => {
    personaMap.set('fake-id', 'Кирилл');
    const r = run({
      steps: [task({ id: 's1', title: 'Шаг', lane: 'back', status: 'inProgress', personaId: 'fake-id' })],
    });
    const html = render(r);
    expect(html, 'исполнитель — имя персоны').toContain('Кирилл');
    expect(html, 'сырой id не выводится').not.toContain('fake-id');
  });

  it('Step_Who_ПерсонаНеНайдена_FallbackНаСтатус: нет id в выводе', () => {
    const r = run({
      steps: [task({ id: 's1', title: 'Шаг', lane: 'back', status: 'todo', personaId: 'ghost-id' })],
    });
    const html = render(r);
    expect(html).not.toContain('ghost-id');
    expect(html).toContain('ожидает');
  });

  it('Stage_Детали_ИмеютПоверхность: детали в поверхности (bgCard/borderLight/R.md)', () => {
    const r = run({
      steps: [task({ id: 's1', title: 'Шаг', lane: 'back', status: 'done' })],
    });
    const html = render(r);
    expect(html, 'class поверхности с токеном bgCard').toContain('stage-details-bgCard');
    expect(html, 'фон — токен bgCard').toContain('var(--c-bg-card)');
    expect(html, 'рамка — токен borderLight').toContain('var(--c-border-light)');
  });

  it('Step_Глиф_Присутствует_ДляКаждогоСтатуса: todo/inProgress/done → глиф', () => {
    const r = run({
      steps: [
        task({ id: 's1', title: 'Т1', lane: 'back', status: 'todo' }),
        task({ id: 's2', title: 'Т2', lane: 'front', status: 'inProgress' }),
        task({ id: 's3', title: 'Т3', lane: 'back', status: 'done' }),
      ],
    });
    const html = render(r);
    // todo — пунктирный кружок (рамка dashed, токен C.dashed).
    expect(html, 'todo — пунктирный кружок').toContain('1.5px dashed var(--c-dashed)');
    // done — галочка (path со специфичной геометрией, stroke C.textMuted).
    expect(html, 'done — галочка').toContain('M3.5 8.5 6.5 11.5 12.5 5');
    // inProgress — ▶ (акцент).
    const playCount = (html.match(/▶/g) ?? []).length;
    expect(playCount, 'inProgress — глиф ▶').toBeGreaterThanOrEqual(1);
  });
});
