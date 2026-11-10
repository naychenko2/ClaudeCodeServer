// Секция «Расход» вкладки «Разбор» (FS-FR5; записка docs/mockups/feature-page-simplify-v4-clickable.md
// §5 «5. Расход», в макете — секция rz-spend): сверху одна плитка «Расход» (деньги и токены,
// FeatureStatsTiles с only=['spend']), ниже — строки токенов, общих чатов, моделей без цены,
// «данные с» и полноты цены (ReviewKv). Порядок строк — по записке §5: токены → общие чаты →
// модели без цены → «данные с» → «цена неполная». Все числа — из stats.spend как есть, своё
// фронт не считает. «Доля кэша одной цифрой», «стоимость по шагам/возвратов», «дороже
// типичной» — «нужно от бэка» (§5, «Б»), здесь их нет.
//
// spend null — одна строка «Расход по этой фиче не учитывался»: нулевых плиток и нулевых
// строк нет (плитку «Расход» бэк не отдаёт без spend — FeatureStatsTiles.tsx:3-4).

import { C, FS, SP, FONT } from '../../lib/design';
import { FeatureStatsTiles } from './FeatureStatsTiles';
import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
import { formatDateTime } from './FeatureStatsBits';
import { formatTokens, modelGroupLabel } from './FeatureStatsFormat';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureReviewSpendProps {
  run: FeatureRun;
  stats: FeatureStats;
  // isMobile — различий в раскладке нет: ReviewKv на 360 и 1280 одинаковые две колонки
  // (контракт «Мобила»), сетку плиток держит FeatureStatsTiles; проп — под единый сигнатур
  // секций «Разбора» (FS-FR).
  isMobile?: boolean;
}

export function FeatureReviewSpend({ run, stats, isMobile = false }: FeatureReviewSpendProps) {
  const spend = stats.spend;

  // Нет расхода — одна строка словом: ни плиток, ни строк с нулями (своих нулей не заводим).
  if (!spend) {
    return (
      <div data-testid="review-spend">
        <span data-testid="review-spend-none" style={{ fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted }}>
          Расход по этой фиче не учитывался
        </span>
      </div>
    );
  }

  const rows: ReviewKvRow[] = [
    // Токены — формат тот же, что в «Подробнее» (FeatureStatsTab.tsx:163-165): formatTokens.
    { label: 'Вход', value: formatTokens(spend.inputTokens) },
    { label: 'Выход', value: formatTokens(spend.outputTokens) },
    { label: 'Чтение кэша', value: formatTokens(spend.cacheReadTokens) },
    { label: 'Запись кэша', value: formatTokens(spend.cacheCreationTokens) },
    // «Доля общих чатов» — поле shared это объект (токены, $, ходы, метод), не процент:
    // формулировка существующая, из «Подробнее» (FeatureStatsTab.tsx:172-177) — токены
    // input+output общей части и метод оценки из shared.method (не из stats.source).
    {
      label: 'Доля общих чатов',
      value: spend.shared
        ? `≈ ${formatTokens(spend.shared.inputTokens + spend.shared.outputTokens)} токенов` +
          ` (оценка, по ${spend.shared.method === 'journal' ? 'журналу' : 'задачам ≈'})`
        : null,
    },
    // Модели без цены — подписи через modelGroupLabel (тот же словарь, что у группы «Модели»);
    // пустой список — строки нет (ReviewKv опускает null).
    {
      label: 'Модели без цены',
      value: spend.hiddenCostModels.length > 0 ? spend.hiddenCostModels.map(modelGroupLabel).join(', ') : null,
    },
    // «Данные с» — formatDateTime (тот же формат, что «закрыта · 10 ноя, 15:48» в FeatureStatsBits.ts:167-169);
    // дата нечитаемая ('' по формату) — строки нет.
    { label: 'Данные с', value: formatDateTime(spend.from) || null },
    // Полнота цены — только при costPartial; формулировка та же, что в подсказке плитки
    // (FeatureStatsTiles.tsx:120).
    { label: 'Цена неполная', value: spend.costPartial ? 'часть моделей без цены' : null },
  ];

  return (
    <div data-testid="review-spend" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <FeatureStatsTiles run={run} stats={stats} isMobile={isMobile} only={['spend']} />
      <ReviewKv rows={rows} />
    </div>
  );
}
