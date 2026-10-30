// Строка итогов карточки «Сводка» (FeatureBriefBlock): идёт · работа ≈ · ждёт/не работали ·
// токены (цены нет — «Расход $» во вкладке «Статистика»). Полоска, ось и легенда «Жизни фичи» убраны из сводки (макет feature-summary-2):
// они живут на вкладке «Статистика».
//
// Данные — ТЕ ЖЕ stats (features/{id}), что во вкладке «Статистика»; фронт не пересчитывает.
// Честность: stats === null → строки нет (без нулей); «≈» при source=approx;
// «принято N из M» и «все готово» НЕ показываем (прогресс — строка «Сделано» карточки).

import { C, FS, FONT } from '../../lib/design';
import { formatDuration } from '../../lib/chatGroups';
import { BRIEF_WAIT_WORD } from './FeatureBriefModel';
import { formatTokens } from './FeatureStatsFormat';
import type { FeatureStats } from '../../types/featureStats';

// Строка итогов: «с начала N · работа ≈ N · ждёт вас N» + токены (цены нет — «Расход $» в «Статистике»).
// lead — «в очереди N» у очереди, слова приходят из модели.
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
  //   current.kind === 'waiting' → «нужен ответ» (длительность текущего ожидания — не поле
  //     бэка: `current` несёт только `since`, без минут — число не вычисляем, минор);
  //   иначе → «не работали ≈ N» (N = totals.notWorkingMinutes).
  let waitPart: string | null = null;
  if (stats.source === 'journal' && t.waitingMinutes != null) {
    const d = shown(t.waitingMinutes);
    if (d) waitPart = `ждала вас ${d}`;
  } else if (stats.timeline.current?.kind === 'waiting') {
    waitPart = BRIEF_WAIT_WORD;
  } else {
    const d = shown(t.notWorkingMinutes);
    if (d) waitPart = `не работали ${isApprox ? '≈ ' : ''}${d}`;
  }
  const workDur = shown(t.workWallMinutes);
  // «С начала» — стена от старта (totals.elapsedMinutes), «работа» — стена работы
  // (workWallMinutes): два разных числа с двумя подписями (П3, Ш5).
  const sinceStart = shown(t.elapsedMinutes);

  // Токены «вход+выход» (spend).
  const spend = stats.spend;
  const tokens = spend ? spend.inputTokens + spend.outputTokens : null;

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
      {sinceStart && (
        <span>
          с начала <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{sinceStart}</b>
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
    </div>
  );
}
