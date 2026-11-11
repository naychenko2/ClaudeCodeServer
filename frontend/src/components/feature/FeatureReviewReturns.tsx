// Секция «Возвраты и потери» вкладки «Разбор» (FP-R1, вариант Б: записка
// docs/mockups/feature-review-returns-v1.md «Рекомендация — Б», «Состояния для реализации (Б)»).
// Состав: карточка «Исход ревью» (FeatureReturnsOutcomeBar: доля «с первого раза», полоса
// «сразу/с замечаниями/возвращены», легенда-чипы) и ниже «Главные потери» (FeatureStatsLosses
// как есть). Прежние плитка «Возвраты» (FeatureStatsTiles only=['loops']) и строки вердиктов
// (ReviewKv) убраны: одно и то же число читалось трижды на разных базах, а «52 возврата»
// путал замечания с возвратами (разбор Майи, п. 4–5). Данные — только run.verdicts как есть;
// verdicts.loops и returnsBySource здесь не используются. «Цены возврата во времени» и
// «главной потери одной фразой» здесь нет — «нужно от бэка» (тот же §5, «Б»).

import { SP } from '../../lib/design';
import { FeatureReturnsOutcomeBar } from './FeatureReturnsOutcomeBar';
import { FeatureStatsLosses } from './FeatureStatsLosses';
import type { FeatureNavCtx } from '../../lib/featureNavMarks';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureReviewReturnsProps {
  run: FeatureRun;
  stats: FeatureStats;
  // Контекст переходов «чат» / «задача» «Главных потерь» — пробрасывается в
  // FeatureStatsLosses без изменений (контракт «События»).
  nav?: FeatureNavCtx;
  isMobile?: boolean;
  // Фиксированное «сейчас» (тесты); в работе — Date.now() (как у FeatureStatsLosses).
  nowMs?: number;
}

export function FeatureReviewReturns({ run, stats, nav, isMobile = false, nowMs }: FeatureReviewReturnsProps) {
  return (
    <div data-testid="review-returns" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <FeatureReturnsOutcomeBar verdicts={run.verdicts} isMobile={isMobile} />
      <FeatureStatsLosses run={run} stats={stats} nav={nav} isMobile={isMobile} nowMs={nowMs} />
    </div>
  );
}
