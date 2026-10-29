// Тесты карточки «Сводка» (волна 6, шаг F5, макет feature-summary-1):
// · 9 kind (включая 'merged' — для карточки ведётся так же, как 'closed', Ф-1.1):
//   строка «Сейчас» в DOM равна выходу briefNowText (F2), подстрока «Сейчас» и
//   «Сделано» — выходам briefSubText/briefDoneText (R-F5: слова перенесены в модель),
//   тег — выходу featureRunStatusTag (единая точка правды тега); ожидаемых строк в тесте
//   нет — они берутся из тех же моделей, что и в блоке;
// · служебные состояния: скелетон с aria-busy, «Сводка не загрузилась» + полоска,
//   brief = null и статов нет → блока нет;
// · «Мерж…» / «Открыть финальное ревью» — только waiting_you и только isMobile;
//   каждая кнопка — по своему пропу независимо (R-F5 п.6); «Открыть финальное ревью» —
//   secondary, не ghost (R-F8);
// · легенда — из MAIN-сегментов общей модели ленты, «⫽» — только при настоящем разрыве;
// · R-F8: порядок секций «Сделано · Дальше · Оценка»; подпись факта без значения не
//   рисуется; числа подстроки «Сейчас» — моно; «План готов» для queued; глиф у error;
//   minWidth: 0 у значения; одно «сейчас» на ответ (перерисовка тех же данных не сдвигает
//   относительные строки; новая ссылка данных — «сейчас» пересчитан, R-F2d3); без «Дальше»
//   «Оценка» в правой колонке грида, середина не пустует (R-F8 минор 2); на мобили
//   расстановки колонок нет даже без «Дальше» — гард !isMobile (F12);
// · Grep-сторож: в исходнике блока нет прямого обращения к полю состояния, полосе стадий
//   и полям таймлайна (включая `timeline` через деструктуризацию) — фронт ничего не
//   пересчитывает; и нет слов модели — полный список BRIEF_MODEL_WORDS (R-F8), комментарии
//   блока под него не попадают.
//
// Окружение node (без jsdom): fakeDom (mount), как в FeatureBriefLegend.test.ts.
// «Сейчас» — фиксированное системное время; даты fixtures — локальные компоненты Date
// (форматтеры читают локальные геттеры — TZ-независимо, CI Linux = dev Windows).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FeatureBriefBlock } from './FeatureBriefBlock';
import { briefEstimateText, briefNowText, briefReadinessText, briefSubText, BRIEF_MODEL_WORDS } from './FeatureBriefModel';
import { featureRunStatusTag } from './FeatureStatusTag';
import { stageLabel } from './FeatureStatsBits';
import { C, FONT } from '../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureBriefBlockProps } from './FeatureBriefBlock';
import type { FeatureBrief, FeatureBriefActive, FeatureBriefKind } from '../../lib/featurePage';
import type { FeatureStats } from '../../types/featureStats';

const HERE = dirname(fileURLToPath(import.meta.url));

// «Сейчас» по часам макета: 25.10.2026 14:10 (локальное).
const NOW = new Date(2026, 9, 25, 14, 10).getTime();
const at = (day: number, hh: number, mm = 0, ss = 0): string => new Date(2026, 9, day, hh, mm, ss).toISOString();

const NAMES: Record<string, string> = { 'p-gleb': 'Глеб', 'p-vera': 'Вера', 'p-kira': 'Кира', 'p-sofya': 'Софья' };
const nameOf = (id: string): string | null => NAMES[id] ?? null;

function brief(over: Partial<FeatureBrief> = {}): FeatureBrief {
  return {
    kind: 'running',
    now: null,
    reason: null,
    done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' },
    next: [],
    elapsedMinutes: null,
    estimate: null,
    queue: null,
    note: null,
    // updatedAt отличается от computedAt намеренно (14:00 против 14:08): строка «обновлено …»
    // обязана считаться от него — подмена на computedAt красит тесты (F15, дефект f2da9354).
    updatedAt: at(25, 14, 0),
    computedAt: at(25, 14, 8),
    ...over,
  };
}

// Статистика с валидным таймлайном (тот же shape, что в FeatureBriefTimeline.test.ts).
function stats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-25T10:00:00Z',
      to: '2026-10-25T14:10:00Z',
      stages: [{ key: 'prod', startedAt: '2026-10-25T10:00:00Z', barEnd: null }],
      segments: [
        { from: '2026-10-25T10:00:00Z', to: '2026-10-25T10:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
      ],
      current: { kind: 'waiting', since: '2026-10-25T14:00:00Z', taskId: 't-prod' },
      totals: {
        elapsedMinutes: 250, workWallMinutes: 30, agentsSumMinutes: 30, notWorkingMinutes: 220,
        waitingMinutes: 60, idleMinutes: 100, waitingCount: 1, longestWaitMinutes: 60,
        errorCount: 0, errorMinutes: 0,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: {
      inputTokens: 100000, outputTokens: 20000, cacheReadTokens: 0, cacheCreationTokens: 0,
      costUsd: 1.5, costPartial: false, hiddenCostModels: [],
      from: '2026-10-25T10:00:00Z',
      shared: null,
    },
    ...over,
  };
}

function render(props: Partial<FeatureBriefBlockProps> = {}) {
  const calls: string[] = [];
  const noop = (k: string) => () => { calls.push(k); };
  const { container } = mount(createElement(FeatureBriefBlock, {
    brief: null,
    stats: null,
    onOpenStats: noop('stats'),
    onRetry: noop('retry'),
    onMerge: noop('merge'),
    onOpenFinal: noop('final'),
    ...props,
  }));
  return { c: container, calls };
}

function click(c: ReturnType<typeof render>['c'], testId: string) {
  const node = findNodeByAttr(c, 'data-testid', testId);
  expect(node, `кнопка ${testId}`).not.toBeNull();
  act(() => { (reactProps(node!)!.onClick as () => void)(); });
}

beforeEach(() => {
  installFakeDom();
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
});
afterEach(() => {
  uninstallFakeDom();
  vi.useRealTimers();
});

// ── 1. Восемь kind: строка и тег — выход моделей, не строки теста ────────────────────

interface Case {
  name: string;
  kind: FeatureBriefKind;
  over?: Partial<FeatureBrief>;
  mergedAt?: string;
  // Ожидаемый тег — выход featureRunStatusTag (единой точки правды) для канонического
  // состояния kind; «в очереди» — новый вариант макета, состояния для него нет.
  tag: string;
}

const CASES: Case[] = [
  {
    name: 'running', kind: 'running',
    over: { now: { stage: 'review', taskId: 't-rev', personaId: 'p-gleb', stepRef: '3', lane: 'back', since: at(25, 14, 5) } },
    tag: featureRunStatusTag('running', stageLabel('review')).label,
  },
  { name: 'waiting_you (мерж)', kind: 'waiting_you', over: { reason: 'мерж' }, tag: featureRunStatusTag('waiting').label },
  { name: 'error', kind: 'error', over: { reason: 'сохранение журнала упало' }, tag: featureRunStatusTag('stopped').label },
  { name: 'paused', kind: 'paused', over: { reason: 'manual', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) } }, tag: featureRunStatusTag('paused').label },
  { name: 'queued', kind: 'queued', tag: 'в очереди' },
  { name: 'draft', kind: 'draft', tag: featureRunStatusTag('draft').label },
  { name: 'idle', kind: 'idle', tag: featureRunStatusTag('idle').label },
  { name: 'closed (смержена)', kind: 'closed', mergedAt: at(24, 18, 5), tag: featureRunStatusTag('merged').label },
  { name: 'closed (без мержа)', kind: 'closed', tag: featureRunStatusTag('closed').label },
  { name: 'merged (смержена, эпик ещё не закрыт)', kind: 'merged', mergedAt: at(24, 18, 5), tag: featureRunStatusTag('merged').label },
];

describe('FeatureBriefBlock: девять kind', () => {
  for (const cs of CASES) {
    it(`${cs.name}: строка «Сейчас» = выход briefNowText, тег = выход резолвера тега`, () => {
      const b = brief({ kind: cs.kind, ...cs.over });
      const mergedAt = cs.mergedAt ?? null;
      const { c } = render({ brief: b, mergedAt, nameOf });
      const line = findNodeByAttr(c, 'data-testid', 'brief-now-line');
      expect(line, 'строка «Сейчас»').not.toBeNull();
      expect(textNodes(line!).join(''), 'строка расходится с выходом F2').toBe(briefNowText(b, { nameOf, mergedAt }));
      const tag = findNodeByAttr(c, 'data-testid', 'brief-tag');
      expect(tag, 'тег состояния').not.toBeNull();
      expect(textNodes(tag!).join(''), 'тег расходится с резолвером').toBe(cs.tag);
    });
  }

  it('строка «Сейчас» — aria-live=polite, карточка — section с именем (макет §Поведение)', () => {
    const { c } = render({ brief: brief({ kind: 'idle' }) });
    const sec = findNodeByAttr(c, 'data-testid', 'feature-brief-block')!;
    expect(sec.attributes['aria-label'], 'имя карточки').toBe('Сводка состояния фичи');
    const line = findNodeByAttr(c, 'data-testid', 'brief-now-line')!;
    expect(line.attributes['aria-live'], 'переход этапа зачитается').toBe('polite');
  });

  it('подстрока «Сейчас» в DOM равна выходу briefSubText (слова модели, R-F5)', () => {
    const b = brief({ kind: 'running', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 58) } });
    const { c } = render({ brief: b, nameOf });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-now-sub')!).join('')).toBe(briefSubText(b, NOW));
  });

  it('running без now — строки «Сейчас», подстроки и её подписи нет (поля нет — строку не рисуем, R-F8)', () => {
    const { c } = render({ brief: brief({ kind: 'running' }) });
    expect(findNodeByAttr(c, 'data-testid', 'brief-now-line'), 'строки нет').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-now-sub'), 'подстроки нет').toBeNull();
    const sec = findNodeByAttr(c, 'data-testid', 'feature-brief-block')!;
    expect(textNodes(sec).join(' '), 'голой подписи «Сейчас» нет').not.toContain('Сейчас');
  });
});

// ── 2. «Готовность | Срок» ───────────────────────────────────────────────────────────

const HIST = { remainingMinutes: 150, p25: 110, p75: 220, source: 'history' as const, sampleSize: 14 };

function note(est: Record<string, unknown> | null = {}, over: Partial<NonNullable<FeatureBrief['note']>> = {}): FeatureBrief['note'] {
  return {
    text: 'голос', at: at(25, 13, 56), byPersonaId: 'p-sofya', byUser: false, stageAtWrite: 'prod', stale: false,
    estimate: est === null ? null : {
      done: 4, total: 9, percent: 45, inProgress: 3, remaining: ['проверка · Вера', 'финал'], etaMinutes: 160,
      why: null, agreement: 'within', etaEndsAt: at(25, 16, 50), ...est,
    } as NonNullable<FeatureBrief['note']>['estimate'],
    ...over,
  };
}

const joined = (c: ReturnType<typeof render>['c'], id: string): string => textNodes(findNodeByAttr(c, 'data-testid', id)!).join('');
const styleOf = (c: ReturnType<typeof render>['c'], id: string): Record<string, unknown> =>
  reactProps(findNodeByAttr(c, 'data-testid', id)!)!.style as Record<string, unknown>;

describe('«Готовность | Срок»', () => {
  it('«Готовность» в DOM равна выходам модели: подпись, подпись под полоской, факт трекера (слова модели, R-F5)', () => {
    const b = brief({ estimate: HIST, note: note() });
    const { c } = render({ brief: b, nameOf });
    const v = briefReadinessText(b, { nameOf });
    expect(joined(c, 'brief-ready-caption')).toBe(v.caption.map(p => p.text).join(''));
    expect(joined(c, 'brief-ready-caption')).toBe('4 готово · 3 в работе · из 9 · 45 %');
    expect(joined(c, 'brief-ready-tracker')).toBe(v.tracker.map(p => p.text).join(''));
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-ready')!).join(' ')).toContain('Готовность');
  });

  it('полоска — progressbar с aria-label и значением; заливка «готово» C.accent, штриховка «в работе» по inProgress', () => {
    const { c } = render({ brief: brief({ note: note() }), nameOf });
    const bar = findNodeByAttr(c, 'data-testid', 'brief-ready-bar')!;
    expect(bar.attributes.role).toBe('progressbar');
    expect(bar.attributes['aria-valuenow']).toBe('45');
    expect(bar.attributes['aria-label']).toBe('Готовность 45 %');
    expect(styleOf(c, 'brief-ready-fill').width).toBe('45%');
    expect(styleOf(c, 'brief-ready-fill').background).toBe(C.accent);
    expect(styleOf(c, 'brief-ready-hatch').width).toBe('33%');
    expect(String(styleOf(c, 'brief-ready-hatch').background)).toContain('repeating-linear-gradient(135deg');
  });

  it('устаревшая оценка — заливка C.accentMuted, штриховка accentMuted/progressTrack, метка у факта трекера', () => {
    const { c } = render({ brief: brief({ note: note({}, { stale: true }) }), nameOf });
    expect(styleOf(c, 'brief-ready-fill').background).toBe(C.accentMuted);
    expect(String(styleOf(c, 'brief-ready-hatch').background)).toContain(C.progressTrack);
    expect(joined(c, 'brief-ready-stale')).toBe('до смены этапа');
  });

  it('закрытая — заливка C.success, штриховки нет, вместо чипов «ничего — всё сделано»', () => {
    const { c } = render({ brief: brief({ kind: 'closed', note: note({ done: 9, total: 9, percent: 100, remaining: [] }) }), mergedAt: at(24, 18, 5), nameOf });
    expect(styleOf(c, 'brief-ready-fill').background).toBe(C.success);
    expect(findNodeByAttr(c, 'data-testid', 'brief-ready-hatch')).toBeNull();
    expect(joined(c, 'brief-chip-empty')).toBe('ничего — всё сделано');
  });

  it('«в master» (F1): сегмент C.success в начале заливки, остаток готового — обычная краска; пометка одна — в подписи', () => {
    const { c } = render({ brief: brief({ done: { stepsAccepted: 4, stepsTotal: 5, loops: 0, planState: 'cut' } }), inMaster: 3, nameOf });
    expect(styleOf(c, 'brief-ready-fill-master').width).toBe('60%');
    expect(styleOf(c, 'brief-ready-fill-master').background).toBe(C.success);
    expect(styleOf(c, 'brief-ready-fill').width).toBe('20%');
    expect(styleOf(c, 'brief-ready-fill').background).toBe(C.accent);
    expect(joined(c, 'brief-ready-caption')).toContain('3 в master');
    // минор 3 ревью 07eee660: в строке трекера пометки нет (дубль убран)
    expect(joined(c, 'brief-ready-tracker')).not.toContain('в master');
    expect(findNodeByAttr(c, 'data-testid', 'brief-ready-bar')!.attributes['aria-label']).toBe('Готовность 80 %, 3 в master');
  });

  it('без inMaster (старые ответы) — сегмента нет, заливка целиком прежняя', () => {
    const { c } = render({ brief: brief({ done: { stepsAccepted: 4, stepsTotal: 5, loops: 0, planState: 'cut' } }), nameOf });
    expect(findNodeByAttr(c, 'data-testid', 'brief-ready-fill-master')).toBeNull();
    expect(styleOf(c, 'brief-ready-fill').width).toBe('80%');
    expect(joined(c, 'brief-ready-tracker')).not.toContain('в master');
  });

  it('чипы «ещё не начато»: первый «следом», «за вами» — на фоне C.infoBg', () => {
    const { c } = render({ brief: brief({ note: note() }), nameOf });
    const chips = [findNodeByAttr(c, 'data-testid', 'brief-chip')!];
    expect(textNodes(chips[0]).join('')).toBe('следом · проверка · Вера');
    expect((reactProps(chips[0])!.style as Record<string, unknown>).background).toBe(C.accentLight);
    const w = render({ brief: brief({ kind: 'waiting_you', reason: 'мерж', note: note({ remaining: ['мерж'] }) }), nameOf }).c;
    const you = findNodeByAttr(w, 'data-testid', 'brief-chip')!;
    expect(textNodes(you).join('')).toBe('мерж · за вами');
    expect((reactProps(you)!.style as Record<string, unknown>).background).toBe(C.infoBg);
  });

  it('без оценки оркестратора — «по трекеру» из done и next', () => {
    const b = brief({ next: [{ stage: 'qa', personaId: 'p-vera' }] });
    const { c } = render({ brief: b, nameOf });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-ready')!).join(' ')).toContain('Готовность · по трекеру');
    expect(joined(c, 'brief-ready-caption')).toBe('2 из 5 шагов · 40 %');
    expect(joined(c, 'brief-chip')).toBe('следом · проверка · Вера');
  });

  it('«Срок» в DOM равен выходу briefEstimateText: число, хвост, источник, вторая оценка, метка', () => {
    const b = brief({ estimate: HIST, note: note() });
    const { c } = render({ brief: b, nameOf });
    const v = briefEstimateText(b, { nameOf, nowMs: NOW });
    expect(joined(c, 'brief-estimate-main')).toBe(`${v.main} ${v.tail}`);
    expect(joined(c, 'brief-estimate-main')).toBe('≈ 2 ч 40 мин · к 16:50');
    expect(joined(c, 'brief-estimate-source')).toBe(v.source);
    expect(joined(c, 'brief-estimate-alt')).toBe(`${v.alt} · сходится`);
    expect(joined(c, 'brief-estimate-verdict')).toBe('сходится');
    expect(styleOf(c, 'brief-estimate-verdict').color).toBe(C.successText);
    expect(findNodeByAttr(c, 'data-testid', 'brief-estimate-why')).toBeNull();
  });

  it('расхождение — метка на C.warningBg и «почему» с кромкой C.warning', () => {
    const b = brief({ estimate: HIST, note: note({ etaMinutes: 360, agreement: 'above', why: 'добавился шаг 4б', etaEndsAt: at(25, 20, 10) }) });
    const { c } = render({ brief: b, nameOf });
    expect(joined(c, 'brief-estimate-verdict')).toBe('дольше, чем по истории');
    expect(styleOf(c, 'brief-estimate-verdict').background).toBe(C.warningBg);
    expect(joined(c, 'brief-estimate-why')).toBe('добавился шаг 4б');
    expect(String(styleOf(c, 'brief-estimate-why').borderLeft)).toContain(C.warning);
  });

  it('нет никакой оценки — «Оценки пока нет» приглушённым, «≈ 0» нигде в карточке', () => {
    const { c } = render({ brief: brief({ elapsedMinutes: 70 }) });
    expect(joined(c, 'brief-estimate-main')).toBe('Оценки пока нет');
    expect(styleOf(c, 'brief-estimate-main').color).toBe(C.textMuted);
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'feature-brief-block')!).join(' ')).not.toMatch(/≈\s*0/);
  });

  it('closed — подпись «Итог»: «шла 6 ч 40 мин» и строка «на старте … обещала»', () => {
    const b = brief({ kind: 'closed', elapsedMinutes: 400, initialEtaMinutes: 300, note: note({ done: 9, total: 9, percent: 100, remaining: [] }) });
    const { c } = render({ brief: b, mergedAt: at(24, 18, 5), nameOf });
    const eta = joined(c, 'brief-eta');
    expect(eta).toContain('Итог');
    expect(eta).toContain('шла 6 ч 40 мин');
    expect(eta).toContain('на старте Софья обещала ≈ 5 ч');
  });

  it('queued с нарезанным планом — факт трекера «План готов · 0 из 4 шагов» (макет queue, R-F8)', () => {
    const b = brief({ kind: 'queued', done: { stepsAccepted: 0, stepsTotal: 4, loops: 0, planState: 'cut' } });
    const { c } = render({ brief: b });
    expect(joined(c, 'brief-ready-tracker')).toBe('План готов · 0 из 4 шагов');
  });

  it('queued: «Оценка — после старта» приглушённо; «в очереди N» — первым фактом строки итогов по queueWaitMinutes, из elapsedMinutes — никогда', () => {
    const b = brief({ kind: 'queued', elapsedMinutes: 0, queueWaitMinutes: 25 });
    const { c } = render({ brief: b, stats: stats() });
    expect(joined(c, 'brief-estimate-main')).toBe('Оценка — после старта');
    expect(joined(c, 'mini-summary-row')).toContain('в очереди 25 мин');
    const c2 = render({ brief: brief({ kind: 'queued', elapsedMinutes: 25 }), stats: stats() }).c;
    expect(joined(c2, 'mini-summary-row')).not.toContain('в очереди');
  });

  it('строка итогов: «идёт N» из elapsedMinutes первым фактом', () => {
    const { c } = render({ brief: brief({ elapsedMinutes: 70 }), stats: stats() });
    expect(joined(c, 'mini-summary-row').startsWith('идёт 1 ч 10 мин')).toBe(true);
  });
});

// ── 2a. Порядок секций, пустые подписи, вид значений (R-F8) ──────────────────────────

describe('Порядок секций и пустые подписи (R-F8)', () => {
  // running с историей, следующей стадией и оценкой оркестратора.
  const full = brief({
    now: { stage: 'review', taskId: 't-rev', personaId: 'p-gleb', stepRef: '3', lane: 'back', since: at(25, 14, 5) },
    next: [{ stage: 'qa', personaId: 'p-vera' }],
    estimate: HIST,
    note: note(),
  });

  it('порядок колонок «Готовность | Срок» закреплён; «Сделано · Дальше · Оценка» и «Жизни фичи» в сводке нет', () => {
    const { c } = render({ brief: full, stats: stats(), nameOf });
    const facts = textNodes(findNodeByAttr(c, 'data-testid', 'brief-facts')!).join(' ');
    expect(facts.indexOf('Готовность'), '«Готовность» есть').toBeGreaterThanOrEqual(0);
    expect(facts.indexOf('Срок'), '«Срок» после «Готовности»').toBeGreaterThan(facts.indexOf('Готовность'));
    const all = textNodes(findNodeByAttr(c, 'data-testid', 'feature-brief-block')!).join(' ');
    for (const gone of ['Сделано', 'Дальше', 'Жизнь фичи']) expect(all, gone).not.toContain(gone);
    for (const id of ['brief-life', 'mini-timeline', 'mini-timeline-axis', 'brief-legend-work']) {
      expect(findNodeByAttr(c, 'data-testid', id), id).toBeNull();
    }
  });

  it('грид — 1.4fr 1fr на десктопе и столбик на мобиле; у колонок minWidth: 0', () => {
    const d = render({ brief: full, nameOf }).c;
    expect(styleOf(d, 'brief-facts').gridTemplateColumns).toBe('1.4fr 1fr');
    const m = render({ brief: full, nameOf, isMobile: true }).c;
    expect(styleOf(m, 'brief-facts').gridTemplateColumns).toBe('1fr');
    for (const id of ['brief-ready', 'brief-eta']) expect(styleOf(d, id).minWidth, id).toBe(0);
  });

  it('числа в подстроке «Сейчас» — моно, текст подстроки — выход модели без изменений (R-F8, макет)', () => {
    const b = brief({ kind: 'running', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 58) } });
    const { c } = render({ brief: b, nameOf });
    const sub = findNodeByAttr(c, 'data-testid', 'brief-now-sub')!;
    expect(textNodes(sub).join(''), 'текст подстроки не изменился').toBe(briefSubText(b, NOW));
    const monoTexts: string[] = [];
    const walk = (n: NonNullable<ReturnType<typeof findNodeByAttr>>): void => {
      for (const ch of n.children) {
        if (ch.nodeName === 'SPAN' && ch.style.fontFamily === FONT.mono) monoTexts.push(textNodes(ch).join(''));
        walk(ch);
      }
    };
    walk(sub);
    expect(monoTexts, 'моно-обёрнуты ровно числовые токены').toEqual(['13:58', '12 мин']);
  });

  it('error — глиф состояния есть, тот же что у «Сводка не загрузилась» (R-F8, макет)', () => {
    const { c } = render({ brief: brief({ kind: 'error', reason: 'сохранение журнала упало' }) });
    expect(findNodeByAttr(c, 'data-testid', 'brief-glyph'), 'глиф ошибки').not.toBeNull();
  });

  it('перерисовка тех же данных не сдвигает относительные строки: одно «сейчас» на ответ (R-F8)', () => {
    const props: FeatureBriefBlockProps = {
      brief: brief({ kind: 'waiting_you', reason: 'мерж', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 0) } }),
      stats: null,
      onOpenStats: () => {},
      onRetry: () => {},
    };
    const { root, container } = mount(createElement(FeatureBriefBlock, props));
    const upd1 = textNodes(findNodeByAttr(container, 'data-testid', 'brief-updated')!).join('');
    expect(upd1, 'updatedAt 14:00 при «сейчас» 14:10').toBe('обновлено 10 мин назад');
    vi.setSystemTime(NOW + 10 * 60_000);
    // Новый элемент и новые props: компонент обязан пройтись тело рендера ещё раз.
    act(() => { root.render(createElement(FeatureBriefBlock, { ...props })); });
    const upd2 = textNodes(findNodeByAttr(container, 'data-testid', 'brief-updated')!).join('');
    expect(upd2, 'та же data — та же строка времени, часы в рендере не участвуют').toBe(upd1);
  });

  it('новая ссылка brief → «сейчас» пересчитан: deps эффекта — данные ответа (R-F2d3, вторая половина инварианта)', () => {
    const props: FeatureBriefBlockProps = {
      brief: brief({ kind: 'waiting_you', reason: 'мерж', now: { stage: '', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 0) } }),
      stats: null,
      onOpenStats: () => {},
      onRetry: () => {},
    };
    const { root, container } = mount(createElement(FeatureBriefBlock, props));
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'brief-updated')!).join(''), 'updatedAt 14:00 при «сейчас» 14:10').toBe('обновлено 10 мин назад');
    // Новый ответ — новый объект тех же данных: часы обязаны свериться (поломка deps=[] это красит).
    vi.setSystemTime(NOW + 10 * 60_000);
    act(() => { root.render(createElement(FeatureBriefBlock, { ...props, brief: { ...props.brief! } })); });
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'brief-updated')!).join(''), 'новая ссылка данных — новое «сейчас»: 14:20 − 14:00').toBe('обновлено 20 мин назад');
  });
});

// ── 2b. «обновлено …» (F15, дефект f2da9354): по updatedAt, не по computedAt ─────────

describe('«обновлено …» (F15, дефект f2da9354)', () => {
  it('updatedAt 3 часа назад — в карточке «обновлено 3 ч назад»; по computedAt было бы «2 мин назад» (критерий c1)', () => {
    const { c } = render({ brief: brief({ updatedAt: at(25, 11, 10) }) });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-updated')!).join('')).toBe('обновлено 3 ч назад');
  });

  it('updatedAt = null — строки «обновлено» в карточке нет, откат на computedAt не подставляется (критерий c2)', () => {
    const { c } = render({ brief: brief({ updatedAt: null }) });
    expect(findNodeByAttr(c, 'data-testid', 'brief-updated'), 'строки нет').toBeNull();
  });
});

// ── 3. Голос оркестратора ────────────────────────────────────────────────────────────

describe('Голос оркестратора', () => {
  const fresh = { text: 'Идём по плану', at: at(25, 13, 56), byPersonaId: 'p-sofya', byUser: false, stageAtWrite: 'review', stale: false };

  it('свежий — плашка с текстом и подписью «Имя · ведёт фичу · N мин назад»', () => {
    const { c } = render({ brief: brief({ note: fresh }), nameOf });
    const voice = findNodeByAttr(c, 'data-testid', 'brief-voice');
    expect(voice, 'плашка голоса').not.toBeNull();
    const all = textNodes(voice!).join(' ');
    expect(all).toContain('Идём по плану');
    expect(all).toContain('Софья · ведёт фичу · 14 мин назад');
    expect(findNodeByAttr(c, 'data-testid', 'brief-voice-stale'), 'метки устаревшего нет').toBeNull();
  });

  it('устаревший в паузе — метка «до паузы» рядом с подписью, текст не спрятан (макет hold)', () => {
    const { c } = render({
      brief: brief({ kind: 'paused', reason: 'manual', note: { ...fresh, at: at(25, 13, 25), stale: true } }),
      nameOf,
    });
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-voice-stale')!).join('')).toBe('до паузы');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'brief-voice')!).join(' ')).toContain('Идём по плану');
  });

  it('голос человека (percoны нет) — часть подписи с именем опускается, плашка остаётся', () => {
    const { c } = render({ brief: brief({ note: { ...fresh, byPersonaId: null, byUser: true } }), nameOf });
    const by = textNodes(findNodeByAttr(c, 'data-testid', 'brief-voice-by')!).join('');
    expect(by).toBe('ведёт фичу · 14 мин назад');
  });

  it('note = null — плашки голоса нет вовсе (макет §3: «оркестратор молчит» не пишем)', () => {
    const { c } = render({ brief: brief() });
    expect(findNodeByAttr(c, 'data-testid', 'brief-voice')).toBeNull();
  });
});

// ── 4. Служебные состояния ───────────────────────────────────────────────────────────

describe('Служебные состояния', () => {
  it('loading — скелетон с aria-busy=«true», без текста (ни нулей, ни прочерков)', () => {
    const { c } = render({ loading: true });
    const sk = findNodeByAttr(c, 'data-testid', 'feature-brief-skeleton');
    expect(sk, 'скелетон').not.toBeNull();
    expect(sk!.attributes['aria-busy'], 'aria-busy').toBe('true');
    expect(textNodes(sk!).join('').trim(), 'нулей и прочерков нет').toBe('');
    expect(findNodeByAttr(c, 'data-testid', 'feature-brief-block'), 'карточки под скелетоном нет').toBeNull();
  });

  it('brief = null при живых статах — «Сводка не загрузилась» + «Повторить» и строка итогов, полоски и оси нет', () => {
    const { c, calls } = render({ brief: null, stats: stats() });
    const sec = findNodeByAttr(c, 'data-testid', 'feature-brief-block');
    expect(sec, 'карточка есть').not.toBeNull();
    const all = textNodes(sec!).join(' ');
    expect(all).toContain('Сводка не загрузилась');
    expect(findNodeByAttr(c, 'data-testid', 'mini-timeline'), 'полоски жизни в сводке нет').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-totals'), 'строка итогов остаётся').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-ready'), 'полоски готовности без brief нет').toBeNull();
    click(c, 'brief-retry');
    expect(calls, '«Повторить» зовёт перезапрос').toContain('retry');
  });

  it('brief = null и статов нет — блока нет (макет §3)', () => {
    const { c } = render({ brief: null, stats: null });
    expect(findNodeByAttr(c, 'data-testid', 'feature-brief-block')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-brief-skeleton')).toBeNull();
  });

  it('«Статистика ›» в хвосте — зовёт onOpenStats', () => {
    const { c, calls } = render({ brief: null, stats: stats() });
    click(c, 'brief-go-stats');
    expect(calls).toContain('stats');
  });
});

// ── 4a. 'merged' — та же карточка, что 'closed' (Ф-1.1) ──────────────────────────────

describe('Closedность карточки (R-F8)', () => {
  it('brief.kind = merged с mergedAt — карточка равна closed с тем же mergedAt: тег, строка «Сейчас», сводка, готовность, срок (Ф-1.1)', () => {
    const mergedAt = at(24, 18, 5);
    const est = note({ done: 9, total: 9, percent: 100, remaining: [] });
    const closed = render({ brief: brief({ kind: 'closed', elapsedMinutes: 400, note: est }), stats: stats(), mergedAt, nameOf });
    const merged = render({ brief: brief({ kind: 'merged', elapsedMinutes: 400, note: est }), stats: stats(), mergedAt, nameOf });
    for (const id of ['brief-tag', 'brief-glyph', 'brief-now-line', 'brief-now-sub', 'brief-facts', 'brief-totals']) {
      const a = findNodeByAttr(closed.c, 'data-testid', id);
      const b = findNodeByAttr(merged.c, 'data-testid', id);
      expect(a, `элемент ${id} у closed`).not.toBeNull();
      expect(b, `элемент ${id} у merged`).not.toBeNull();
      expect(textNodes(b!).join(' '), `текст ${id} у merged отличается от closed`).toBe(textNodes(a!).join(' '));
    }
  });
});

// ── 5. Кнопки «ждёт вас»: только waiting_you и только мобильная раскладка ────────────

describe('Кнопки «Мерж…» и «Открыть финальное ревью»', () => {
  const waiting = brief({ kind: 'waiting_you', reason: 'мерж', next: [{ stage: 'merge', personaId: null }] });

  it('waiting_you на десктопе — кнопок в карточке нет (на десктопе «Мерж…» — в шапке страницы)', () => {
    const { c } = render({ brief: waiting, nameOf, isMobile: false });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final')).toBeNull();
  });

  it('waiting_you на мобиле — обе кнопки есть и зовут свои швы', () => {
    const { c, calls } = render({ brief: waiting, nameOf, isMobile: true });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge'), '«Мерж…»').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final'), '«Открыть финальное ревью»').not.toBeNull();
    click(c, 'brief-merge');
    click(c, 'brief-open-final');
    expect(calls).toEqual(expect.arrayContaining(['merge', 'final']));
  });

  it('«Мерж…» рисуется при одном onMerge: без живого чата финала onOpenFinal не передан (R-F5 п.6)', () => {
    const { c, calls } = render({ brief: waiting, nameOf, isMobile: true, onOpenFinal: undefined });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge'), '«Мерж…» при одном onMerge').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final'), 'без onOpenFinal второй кнопки нет').toBeNull();
    click(c, 'brief-merge');
    expect(calls, '«Мерж…» зовёт свой шов').toContain('merge');
  });

  it('«Открыть финальное ревью» рисуется при одном onOpenFinal: onMerge не передан', () => {
    const { c, calls } = render({ brief: waiting, nameOf, isMobile: true, onMerge: undefined });
    expect(findNodeByAttr(c, 'data-testid', 'brief-open-final'), '«Открыть финальное ревью» при одном onOpenFinal').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge'), 'без onMerge кнопки мержа нет').toBeNull();
    click(c, 'brief-open-final');
    expect(calls, 'кнопка зовёт свой шов').toContain('final');
  });

  it('«Открыть финальное ревью» — secondary, не ghost (R-F8, макет)', () => {
    const { c } = render({ brief: waiting, nameOf, isMobile: true });
    const btn = findNodeByAttr(c, 'data-testid', 'brief-open-final')!;
    expect(btn.style.border, 'secondary — без рамки ghost').toBe('none');
    expect(btn.style.background, 'secondary — фон bgPanel').toBe(C.bgPanel);
  });

  it('running на мобиле — кнопок мержа нет (не состояние «ждёт вас»)', () => {
    const { c } = render({
      brief: brief({ kind: 'running', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 14, 5) } }),
      nameOf, isMobile: true,
    });
    expect(findNodeByAttr(c, 'data-testid', 'brief-merge')).toBeNull();
  });
});

// ── 7. Grep-сторож: блок ничего не пересчитывает ─────────────────────────────────────

describe('Grep-сторож (инвариант 3: фронт не пересчитывает)', () => {
  const src = readFileSync(join(HERE, 'FeatureBriefBlock.tsx'), 'utf8');

  it('в исходнике блока нет прямого обращения к полю состояния, полосе стадий и полям таймлайна', () => {
    expect(/run\.state/.test(src), 'блок трогает поле состояния напрямую').toBe(false);
    expect(/\.stages/.test(src), 'блок трогает полосу стадий').toBe(false);
    expect(/stats\.timeline/.test(src), 'блок трогает таймлайн напрямую').toBe(false);
    // Деструктуризация не обходит сторож: `timeline.current` после `const { timeline } = stats`
    // — тоже сырое поле (R-F8). Блок не читает свойства таймлайна вовсе.
    expect(/\btimeline\./.test(src), 'блок читает сырой таймлайн').toBe(false);
  });

  it('перенесённых слов в исходнике блока нет: формулировка живёт в модели (R-F5, полный список R-F8)', () => {
    // Список — константа рядом с моделью (BRIEF_MODEL_WORDS): все статические строки,
    // которые модель может сказать в карточке. Проверяется код блока; комментарии — нет:
    // блок вправе называть строки модели в документации («Сделано · Дальше · Оценка»,
    // «обновлено …» — по updatedAt). Исключения — слова, по праву принадлежащие блоку:
    // «Дальше» (подпись секции блока, ровно без тире; модельный префикс error/idle
    // «Дальше — » — в списке) и «в очереди» (тег карточки: защищена метка тега, а не
    // факт модели «в очереди N» — любой вариант тега с хвостом с ним конфликтует).
    const code = src.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\/\/[^\n]*/g, ' ');
    for (const w of BRIEF_MODEL_WORDS) {
      expect(code.includes(w), `блок содержит «${w}»`).toBe(false);
    }
  });
});

// ── Задачи в работе: заголовок, список, «Ответить», тег, старт в очереди ──────────────

const A = (over: Partial<FeatureBriefActive> = {}): FeatureBriefActive => ({
  taskId: 't1', stage: 'prod', stepRef: '4', lane: 'front', personaId: 'p-kira', state: 'working',
  since: at(25, 14, 5), reason: null, ...over,
});
const allNodes = (c: ReturnType<typeof render>['c'], id: string) => {
  const out: NonNullable<ReturnType<typeof findNodeByAttr>>[] = [];
  const walk = (n: unknown): void => {
    const node = n as { children?: unknown[] } | null;
    if (!node || typeof node !== 'object') return;
    const p = reactProps(node as never);
    if (p && (p as Record<string, unknown>)['data-testid'] === id) out.push(node as never);
    (node.children ?? []).forEach(walk);
  };
  walk(c);
  return out;
};

describe('Задачи в работе (макет 2, «Итерация 2»)', () => {
  const three = [A(), A({ taskId: 't2', stage: 'review', stepRef: '3', lane: 'back', personaId: 'p-gleb', state: 'review' }), A({ taskId: 't3', personaId: 'p-vera', stepRef: '5' })];

  it('несколько задач: заголовок «3 задачи в работе», подстрока полос, список из role=list, тег «идёт · 3 в работе»', () => {
    const { c } = render({ brief: brief({ active: three }), nameOf });
    expect(joined(c, 'brief-now-line')).toBe('3 задачи в работе');
    expect(joined(c, 'brief-now-sub')).toBe('параллельно: фронт и бэк');
    expect(allNodes(c, 'brief-active-row')).toHaveLength(3);
    expect(reactProps(findNodeByAttr(c, 'data-testid', 'brief-active')!.children![0] as never)!.role).toBe('list');
    expect(joined(c, 'brief-tag')).toBe('идёт · 3 в работе');
    expect(findNodeByAttr(c, 'data-testid', 'brief-answer')).toBeNull();
  });

  it('строка — кнопка с aria-label и переходом в задачу', () => {
    const opened: string[] = [];
    const { c } = render({ brief: brief({ active: three }), nameOf, onOpenTask: id => opened.push(id) });
    const row = allNodes(c, 'brief-active-row')[1];
    expect(reactProps(row)!['aria-label']).toBe('Ревью шага 3 (бэк) — Глеб, открыть задачу');
    act(() => { (reactProps(row)!.onClick as () => void)(); });
    expect(opened).toEqual(['t2']);
  });

  it('одна задача — строка «Сейчас» как раньше, списка нет', () => {
    const { c } = render({ brief: brief({ now: { stage: 'review', taskId: 't2', personaId: 'p-gleb', stepRef: '3', lane: 'back', since: at(25, 13, 58) }, active: [A()] }), nameOf });
    expect(joined(c, 'brief-now-line')).toBe('Ревью шага 3 (бэк) — у Глеба');
    expect(findNodeByAttr(c, 'data-testid', 'brief-active')).toBeNull();
    expect(joined(c, 'brief-tag')).not.toContain('в работе');
  });

  it('ждёт человека: заголовок про задачу, рамка C.info, «Ответить Кире» открывает чат; тег «ждёт вас · 1 из 3»', () => {
    const answered: string[] = [];
    const list = [A({ state: 'waiting_you', since: at(25, 14, 2) }), A({ taskId: 't2', personaId: 'p-gleb' }), A({ taskId: 't3', personaId: 'p-vera' })];
    for (const isMobile of [false, true]) {
      const { c } = render({ brief: brief({ kind: 'waiting_you', active: list }), nameOf, isMobile, onAnswer: id => answered.push(id) });
      expect(joined(c, 'brief-now-line')).toBe('Кира ждёт вашего ответа по шагу 4');
      expect(joined(c, 'brief-now-sub')).toBe('ждёт 8 мин · шаг 4 стоит, 2 другие задачи идут');
      expect(styleOf(c, 'feature-brief-block').border).toBe(`1px solid ${C.info}`);
      expect(joined(c, 'brief-answer')).toBe('Ответить Кире');
      expect(joined(c, 'brief-tag')).toBe('ждёт вас · 1 из 3');
      // Мерж-кнопки не конкурируют с «Ответить»: ждут не мерж.
      expect(findNodeByAttr(c, 'data-testid', 'brief-merge')).toBeNull();
      click(c, 'brief-answer');
    }
    expect(answered).toEqual(['t1', 't1']);
  });

  it('пауза: заголовок паузы прежний, в списке остановленные («остановлен в 13:40»)', () => {
    const { c } = render({
      brief: brief({ kind: 'paused', now: { stage: 'prod', taskId: null, personaId: null, stepRef: null, lane: null, since: at(25, 13, 40) }, active: [A({ state: 'held', since: at(25, 13, 40) })] }),
      nameOf,
    });
    expect(joined(c, 'brief-now-line')).toBe('На паузе — поставили вы в 13:40');
    const row = allNodes(c, 'brief-active-row')[0];
    expect(reactProps(row)!['data-state']).toBe('held');
    expect(joined(c, 'brief-active-time')).toBe('остановлен в 13:40');
  });

  it('больше четырёх: 4 строки и хвост ведёт во вкладку «Задачи»', () => {
    const calls: string[] = [];
    const six = Array.from({ length: 6 }, (_, i) => A({ taskId: `t${i}` }));
    const { c } = render({ brief: brief({ active: six }), nameOf, onOpenTasks: () => calls.push('tasks') });
    expect(allNodes(c, 'brief-active-row')).toHaveLength(4);
    expect(joined(c, 'brief-active-tail')).toBe('ещё 2 в работе — во вкладке «Задачи» ›');
    click(c, 'brief-active-tail');
    expect(calls).toEqual(['tasks']);
  });

  it('ошибка: «· стоит» красным 600', () => {
    const { c } = render({ brief: brief({ active: [A({ state: 'error' }), A({ taskId: 't2' })] }), nameOf });
    const st = styleOf(c, 'brief-active-stopped');
    expect(st.color).toBe(C.dangerText);
    expect(st.fontWeight).toBe(600);
  });

  it('360: без отступа 24, высота строки 40, время вторым столбцом; десктоп — отступ SP.xl, строка в одну линию', () => {
    const m = render({ brief: brief({ active: three }), nameOf, isMobile: true });
    expect(styleOf(m.c, 'brief-active').marginLeft).toBe(0);
    expect(reactProps(allNodes(m.c, 'brief-active-row')[0])!.style).toMatchObject({ minHeight: 40 });
    const d = render({ brief: brief({ active: three }), nameOf, isMobile: false });
    expect(styleOf(d.c, 'brief-active').marginLeft).toBe(24);
    expect((reactProps(allNodes(d.c, 'brief-active-row')[0])!.style as Record<string, unknown>).minHeight).toBeUndefined();
  });

  it('в очереди: «старт ≈ 15:00» из queueStartEstimate; нет значения — строки нет', () => {
    const q = brief({ kind: 'queued', queueStartEstimate: at(25, 15, 0), estimate: HIST });
    expect(joined(render({ brief: q, nameOf }).c, 'brief-eta')).toContain('старт ≈ 15:00');
    const none = brief({ kind: 'queued', estimate: HIST });
    expect(joined(render({ brief: none, nameOf }).c, 'brief-eta')).not.toContain('старт ≈');
  });
});
