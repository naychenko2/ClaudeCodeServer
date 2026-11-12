// Секция «Возвраты и потери» вкладки «Разбор» (FS-FR3; записка docs/mockups/feature-page-simplify-v4-clickable.md
// §5 «3. Возвраты и потери», в макете — секция rz-ret): собрана из готовых блоков, своей
// вёрстки нет — только порядок: плитка «Возвраты» (FeatureStatsTiles с only=['loops'],
// в ней уже витки ✓/↩ и разбивка по источникам в хвосте), строки вердиктов (ReviewKv),
// затем «Главные потери» (FeatureStatsLosses как есть). Все числа — из run.verdicts как
// есть, своё фронт не считает. «Цена возврата во времени» и «главная потеря одной
// фразой» — «нужно от бэка» (§5, «Б»), здесь не рисуем.
// «Откуда возвраты» здесь не дублируется (FS-FR3, ревью-находка 2, решение постановщика):
// те же три числа печатает хвост плитки «Возвраты» (FeatureStatsTiles.tsx:105-106),
// в макете v4 (:740) они стоят один раз.

import { SP } from '../../lib/design';
import { FeatureStatsTiles } from './FeatureStatsTiles';
import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
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

// Подписи вердиктов — по макету (feature-page-simplify-v4-clickable.html:489):
// «Приняты / С замечаниями / Возвращены / Отозваны» — по run.verdicts. Нули рисуются:
// 0 — значение («возвратов не было»), а не «нет данных».
const VERDICT_ROWS: [keyof FeatureRun['verdicts'], string][] = [
  ['accepted', 'Приняты'],
  ['acceptedWithRemarks', 'С замечаниями'],
  ['returned', 'Возвращены'],
  ['withdrawn', 'Отозваны'],
];

export function FeatureReviewReturns({ run, stats, nav, isMobile = false, nowMs }: FeatureReviewReturnsProps) {
  const rows: ReviewKvRow[] = VERDICT_ROWS.map(([key, label]) => ({ label, value: run.verdicts[key] }));

  return (
    <div data-testid="review-returns" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <FeatureStatsTiles run={run} stats={stats} isMobile={isMobile} only={['loops']} />
      <ReviewKv rows={rows} />
      <FeatureStatsLosses run={run} stats={stats} nav={nav} isMobile={isMobile} nowMs={nowMs} />
    </div>
  );
}
