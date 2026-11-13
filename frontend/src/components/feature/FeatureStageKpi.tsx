// Плитки KPI области этапа на странице фичи (F11, макет feature-page.html P40–P47): время этапа с
// разбивкой «исп · ревью · вы», ходы, токены с кэшем, «ждёт вас». Числа — из stageSummary.kpi как
// есть (фронт ничего не досчитывает, кроме подписи). Легаси-журнал (approx) — «≈» перед числом;
// нет замера вовсе — «нет замера», а не нули.

import { C, FONT, FS, R, SP } from '../../lib/design';
import { formatDuration } from '../../lib/chatGroups';
import { formatTokens } from './FeatureStatsFormat';
import type { StageSummaryKpi } from '../../types/featureStage';

export interface KpiTile { key: 'time' | 'turns' | 'tokens' | 'wait'; label: string; value: string; sub: string | null }

const NO_MEASURE = 'нет замера';

// Чистая модель плиток: тестируется без DOM.
export function kpiTiles(kpi: StageSummaryKpi): KpiTile[] {
  const approx = kpi.approx ? '≈ ' : '';
  const noTime = kpi.approx && kpi.totalMinutes <= 0;
  const tokens = kpi.tokens;
  const tokenTotal = tokens ? tokens.input + tokens.output : null;
  const waited = kpi.waitedHumanMinutes;
  return [
    {
      key: 'time', label: 'Время',
      value: noTime ? NO_MEASURE : `${approx}${formatDuration(kpi.totalMinutes)}`,
      sub: noTime ? null : `исп ${formatDuration(kpi.execMinutes)} · ревью ${formatDuration(kpi.reviewMinutes)} · вы ${formatDuration(kpi.waitedHumanMinutes)}`,
    },
    {
      key: 'turns', label: 'Ходы',
      value: kpi.approx && kpi.turns <= 0 ? NO_MEASURE : `${approx}${kpi.turns}`,
      sub: null,
    },
    {
      key: 'tokens', label: 'Токены',
      value: tokenTotal == null ? NO_MEASURE : `${approx}${formatTokens(tokenTotal)}`,
      sub: tokens ? `кэш ${formatTokens(tokens.cache)}` : null,
    },
    {
      key: 'wait', label: 'Ждёт вас',
      value: kpi.approx && waited <= 0 ? NO_MEASURE : `${approx}${waited > 0 ? formatDuration(waited) : '0 мин'}`,
      sub: null,
    },
  ];
}

export function FeatureStageKpi({ kpi }: { kpi: StageSummaryKpi }) {
  return (
    <div data-testid="stage-kpi" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(130px, 1fr))', gap: SP.sm }}>
      {kpiTiles(kpi).map(t => (
        <div key={t.key} data-testid={`stage-kpi-${t.key}`}
          style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: SP.sm, minWidth: 0,
            borderRadius: R.md, background: C.bgInset, border: `1px solid ${C.borderLight}` }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>{t.label}</span>
          <span data-testid={`stage-kpi-${t.key}-value`}
            style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans, overflowWrap: 'anywhere' }}>
            {t.value}
          </span>
          {t.sub && <span data-testid={`stage-kpi-${t.key}-sub`} style={{ fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{t.sub}</span>}
        </div>
      ))}
    </div>
  );
}
