// «Пульс» вкладки «Статистика» (волна 5, ST-F3; записка docs/mockups/feature-stats-5.md §A,
// макет feature-stats-5.html). Чистые функции: состояние героя, состав времени, плитки,
// главные потери. Бэк считает числа — фронт только раскладывает (тот же принцип, что в
// FeatureStatsBits). Вид (StatsHero / FeatureStatsTiles / FeatureStatsLosses, ST-F4…ST-F7)
// читает эту модель; цвета видов — из kindBackground (FeatureStatsBits), здесь их нет.

import type {
  FeatureRun, FeatureReturnsBySource, FeatureRoundVerdict, FeatureStage, FeatureStepChain,
} from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';
import { stageLabel } from './FeatureStatsBits';

// === Состояние героя ===

// Порог «мало данных» (минуты): прошло меньше — считать доли рано. Значение согласовано с Софьей (ST-F3).
export const FEW_DATA_MIN = 30;

export type HeroState = 'running' | 'waiting' | 'merged' | 'few';

// Приоритет сверху вниз: закрытая → ждёт человека (действие читателя важнее отсутствия
// долей) → мало данных → всё остальное — идёт.
export function pickHeroState(run: FeatureRun, stats: FeatureStats): HeroState {
  // 1. Закрытая фича: герой — итог, сколько бы данных ни было.
  if (run.state === 'merged' || run.state === 'closed') return 'merged';

  // 2. Явная тревога бэка: ответ в чате — самое ценное действие на экране.
  if (run.attention?.kind === 'wait') return 'waiting';

  // 3. Мало данных: сегменты только у одного этапа (или их нет), или прошло меньше FEW_DATA_MIN.
  const stageCount = new Set((stats.timeline.segments ?? []).map(s => s.stage)).size;
  const elapsed = stats.timeline.totals?.elapsedMinutes ?? 0;
  if (stageCount <= 1 || elapsed < FEW_DATA_MIN) return 'few';

  // 4. Всё остальное — идёт.
  return 'running';
}

// === Состав времени ===

export type CompositionKind = 'work' | 'waiting' | 'idle' | 'error';

export interface CompositionRow {
  kind: CompositionKind;
  // Подпись в прошедшем времени (KL макета): состав — о том, что уже было.
  label: string;
  minutes: number;   // как дал бэк
  percent: number;   // 0..100, округление
  percentText: string; // «63 %»
}

const COMP_LABEL: Record<CompositionKind, string> = {
  work: 'работа',
  waiting: 'ждали вас',
  idle: 'простой',
  error: 'ошибки',
};

// Строки «из чего сложилось время» в порядке макета: работа, ожидание, простой, ошибки.
// Поле null (не считалось) или ноль — строки нет. Процент — от elapsed.
export function compositionRows(totals: FeatureTimeline['totals']): CompositionRow[] {
  const elapsed = totals.elapsedMinutes;
  if (!elapsed) return [];
  const parts: Array<[CompositionKind, number | null]> = [
    ['work', totals.workWallMinutes],
    ['waiting', totals.waitingMinutes],
    ['idle', totals.idleMinutes],
    ['error', totals.errorMinutes],
  ];
  return parts
    .filter(([, v]) => v != null && v > 0)
    .map(([kind, raw]) => {
      const v = raw as number;
      const percent = Math.round((v / elapsed) * 100);
      return { kind, label: COMP_LABEL[kind], minutes: v, percent, percentText: `${percent} %` };
    });
}

// === Плитки ===

// Пометки витков для ряда ✓/↩: returned — ↩; принятые — ✓; withdrawn и running не показываются
// (возврата не было, виток ещё идёт).
export type RoundMark = 'ok' | 'ret';

function roundMark(verdict: FeatureRoundVerdict): RoundMark | null {
  if (verdict === 'returned') return 'ret';
  if (verdict === 'accepted' || verdict === 'accepted_with_remarks') return 'ok';
  return null;
}

// Пометки витков по цепочкам шагов; порядок витков даёт бэк — не пересортировывать.
export function roundMarks(chains: FeatureStepChain[] | undefined): RoundMark[] {
  const out: RoundMark[] = [];
  for (const ch of chains ?? []) {
    for (const r of ch.rounds ?? []) {
      const m = roundMark(r.verdict);
      if (m) out.push(m);
    }
  }
  return out;
}

export type StatTile =
  | { key: 'work'; label: 'Работали'; minutes: number; agentsSum: number | null }
  | { key: 'waiting'; label: 'Ждали вас'; minutes: number | null; count: number | null; longest: number | null }
  | { key: 'loops'; label: 'Возвраты'; loops: number; bySource: FeatureReturnsBySource | null; rounds: RoundMark[] }
  | { key: 'spend'; label: 'Расход'; costUsd: number; tokens: number; costPartial: boolean };

// Три плитки — работа, ожидание, возвраты. Четвёртая (расход) — только если бэк дал spend;
// без него «Расход» не стоит нулем, а просто отсутствует (макет §A, п. 3).
export function tilesModel(run: FeatureRun, stats: FeatureStats): StatTile[] {
  const t = stats.timeline.totals;
  const tiles: StatTile[] = [
    { key: 'work', label: 'Работали', minutes: t.workWallMinutes, agentsSum: t.agentsSumMinutes ?? null },
    { key: 'waiting', label: 'Ждали вас', minutes: t.waitingMinutes ?? null, count: t.waitingCount ?? null, longest: t.longestWaitMinutes ?? null },
    { key: 'loops', label: 'Возвраты', loops: run.verdicts?.loops ?? 0, bySource: run.returnsBySource ?? null, rounds: roundMarks(run.stepChains) },
  ];
  const spend = stats.spend;
  if (spend) {
    const tokens = spend.inputTokens + spend.outputTokens + spend.cacheReadTokens + spend.cacheCreationTokens;
    tiles.push({ key: 'spend', label: 'Расход', costUsd: spend.costUsd, tokens, costPartial: spend.costPartial });
  }
  return tiles;
}

// === Главные потери ===

export type LossCard =
  // 1. Ожидание: живая, если фича сейчас ждёт человека.
  | { no: 1; kind: 'wait'; title: string; minutes: number | null; live: boolean; count: number | null }
  // 2. Шаг с наибольшим числом витков.
  | { no: 2; kind: 'loops'; title: string; stepRef: string; stepTitle: string; loops: number; rounds: RoundMark[]; durationMinutes: number }
  // 3. Сейчас: задача, идущая дольше всех.
  | { no: 3; kind: 'longest'; title: string; stepRef: string | null; personaId: string | null; minutes: number; share: number | null }
  // 3. У закрытой фичи — самый долгий этап.
  | { no: 3; kind: 'stage'; title: string; stageKey: string; stageLabel: string; minutes: number };

export interface LossesModel {
  // Нечего показать — пунктир «Потери появятся с первым ожиданием, возвратом или ошибкой».
  empty: boolean;
  cards: LossCard[];
}

// Три главные потери (макет §C, перенесён в A по записке §«Рекомендация»).
export function lossesModel(run: FeatureRun, stats: FeatureStats): LossesModel {
  const cards: LossCard[] = [];
  const t = stats.timeline.totals;
  const live = run.attention?.kind === 'wait';

  // 1. Долше всего ждали вас; живая карточка — пока фича ждёт человека.
  if (t.longestWaitMinutes != null) {
    cards.push({
      no: 1,
      kind: 'wait',
      title: live ? 'Ждёт вас сейчас' : 'Дольше всего ждали вас',
      minutes: t.longestWaitMinutes,
      live,
      count: t.waitingCount ?? null,
    });
  }

  // 2. Шаг с максимальным loops; витков нет — карточки нет.
  let bestChain: FeatureStepChain | null = null;
  for (const ch of run.stepChains ?? []) {
    if (ch.loops > 0 && (bestChain === null || ch.loops > bestChain.loops)) bestChain = ch;
  }
  if (bestChain) {
    cards.push({
      no: 2,
      kind: 'loops',
      title: 'Больше всего витков',
      stepRef: bestChain.stepRef,
      stepTitle: bestChain.title,
      loops: bestChain.loops,
      rounds: roundMarks([bestChain]),
      durationMinutes: bestChain.durationMinutes,
    });
  }

  // 3. Дольше всех сейчас; у закрытой фичи — самый долгий этап по approxMinutes.
  const longest = stats.longestNow;
  if (longest) {
    cards.push({
      no: 3,
      kind: 'longest',
      title: 'Дольше всех сейчас',
      stepRef: longest.stepRef ?? null,
      personaId: longest.personaId ?? null,
      minutes: longest.minutes,
      share: longest.share ?? null,
    });
  } else if (run.closed === true || run.state === 'merged' || run.state === 'closed') {
    let bestStage: FeatureStage | null = null;
    for (const s of run.stages ?? []) {
      if (s.approxMinutes > 0 && (bestStage === null || s.approxMinutes > bestStage.approxMinutes)) bestStage = s;
    }
    if (bestStage) {
      cards.push({
        no: 3,
        kind: 'stage',
        title: 'Самый долгий этап',
        stageKey: bestStage.key,
        stageLabel: stageLabel(bestStage.key),
        minutes: bestStage.approxMinutes,
      });
    }
  }

  return { empty: cards.length === 0, cards };
}
