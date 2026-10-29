// Строка итогов карточки «Сводка» (FeatureBriefBlock): идёт · работа ≈ · ждёт/не работали ·
// токены · цена. Полоска, ось и легенда «Жизни фичи» убраны из сводки (макет feature-summary-2):
// они живут на вкладке «Статистика».
//
// Данные — ТЕ ЖЕ stats (features/{id}), что во вкладке «Статистика»; фронт не пересчитывает.
// Честность: stats === null → строки нет (без нулей); «≈» при source=approx;
// «принято N из M» и «все готово» НЕ показываем (прогресс — строка «Сделано» карточки).

import { C, FS, FONT } from '../../lib/design';
import { formatDuration } from './FeatureStatsBits';
import { formatTokens, formatUsd } from './FeatureStatsFormat';
import type { FeatureStats } from '../../types/featureStats';

// Строка итогов: «идёт N · работа ≈ N · ждёт вас N» + токены + цена. lead — первый факт
// («идёт N» / «в очереди N»), слова приходят из модели.
export function SummaryRow({ stats, lead }: { stats: FeatureStats; lead?: { label: string; value: string } | null }) {
  const t = stats.timeline.totals;
  const isApprox = stats.source === 'approx';

  // Порог длительностей строки — тот же, что у shownDuration (F10): ноль от бэка — не число,
  // «ждала вас 0 мин» / «работа 0 мин» — та же болезнь нуля (дефект ef029aea, R-F12 п.3).
  // Нулевая часть строки не выводится; если выводить нечего — строка остаётся пустой.
  const shown = (v: number | null): string | null =>
    v != null && Math.round(v) > 0 ? formatDuration(v) : null;

  // «Ждёт вас» / «не работали» — только из данных бэка (фронт не пересчитывает):
  //   journal + waitingMinutes != null → «ждала вас N» (N = totals.waitingMinutes);
  //   current.kind === 'waiting' → «ждёт вас» (длительность текущего ожидания — не поле
  //     бэка: `current` несёт только `since`, без минут — число не вычисляем, минор);
  //   иначе → «не работали ≈ N» (N = totals.notWorkingMinutes).
  let waitPart: string | null = null;
  if (stats.source === 'journal' && t.waitingMinutes != null) {
    const d = shown(t.waitingMinutes);
    if (d) waitPart = `ждала вас ${d}`;
  } else if (stats.timeline.current?.kind === 'waiting') {
    waitPart = 'ждёт вас';
  } else {
    const d = shown(t.notWorkingMinutes);
    if (d) waitPart = `не работали ${isApprox ? '≈ ' : ''}${d}`;
  }
  const workDur = shown(t.workWallMinutes);

  // Токены «вход+выход» (spend).
  const spend = stats.spend;
  const tokens = spend ? spend.inputTokens + spend.outputTokens : null;
  const cost = spend && !spend.costPartial ? spend.costUsd : null;

  return (
    <div
      data-testid="mini-summary-row"
      style={{
        display: 'flex', alignItems: 'center', gap: '0 12px', flexWrap: 'wrap',
        fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans,
      }}
    >
      {lead && (
        <span>
          {lead.label} <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{lead.value}</b>
        </span>
      )}
      {workDur && (
        <span>
          работа <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{isApprox ? '≈ ' : ''}{workDur}</b>
        </span>
      )}
      {waitPart && <span>{waitPart}</span>}
      {tokens != null && tokens > 0 && (
        <span>
          <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{formatTokens(tokens)}</b> токенов
        </span>
      )}
      {cost != null && (
        <span>
          <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{formatUsd(cost)}</b>
        </span>
      )}
    </div>
  );
}
