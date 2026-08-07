// KPI-лента вкладки «Квоты и деньги» (макет .kpis). До 4 плиток: «Ближе всего к пределу»,
// «Деньги на счетах» (админ), «По тарифам API · 5 дней» (админ), «Бесплатно работают».
// Денежные плитки приходят null у не-админа — лента честно сжимается до 2. Значения
// вычисляет QuotasTab; лента только рисует (нет бизнес-логики/запросов).
import type { CSSProperties, ReactNode } from 'react';
import { C, FONT, FS, R } from '../../lib/design';
import { plural } from '../../lib/spend';

export interface KpiHot { usedPct: number; label: string }       // label = «Claude 2 · неделя»
export interface KpiMoney { amount: number; accounts: number }
export interface KpiCost { amount: number }                       // costUsd за 5 дней
export interface KpiFree { alive: number; total: number; successRate: number }

export interface KpiRibbonProps {
  hot: KpiHot | null;
  money: KpiMoney | null;
  cost: KpiCost | null;
  free: KpiFree | null;
  loading?: boolean;
  isMobile: boolean;
}

// Деньги баланса: 4 dp у мелочи, 3 dp у десятых копеек, иначе 2 dp
const fmtMoney = (c: number) => (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));
// Крупная сумма по тарифам — округлённо, с разделителем тысяч: «3 411»
const fmtCost = (c: number) => Math.round(c).toLocaleString('ru-RU');

interface Tile { l: string; value: ReactNode; hot?: boolean }

function Kpi({ tile }: { tile: Tile }) {
  return (
    <div style={{
      background: C.bgWhite,
      border: `1px solid ${tile.hot ? C.warning : C.border}`,
      borderRadius: R.lg, padding: '9px 11px',
    }}>
      <div style={{ fontSize: FS.xs, color: C.textMuted }}>{tile.l}</div>
      <div style={{ marginTop: 2, fontFamily: FONT.mono, fontSize: FS.lg, fontWeight: 700, color: tile.hot ? C.warningText : C.textHeading }}>
        {tile.value}
      </div>
    </div>
  );
}

// Значение + мелкая серая подпись справа (макет <small>)
const V = (main: string, sub?: string): ReactNode => sub
  ? <>{main} <small style={{ fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 500, color: C.textMuted }}>{sub}</small></>
  : <>{main}</>;

export function KpiRibbon({ hot, money, cost, free, loading, isMobile }: KpiRibbonProps) {
  // Скелет: данные грузятся разными запросами, текст мигал бы на каждом ответе
  if (loading) {
    const wrap: CSSProperties = isMobile
      ? { display: 'flex', overflowX: 'auto', gap: 8 }
      : { display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8 };
    return (
      <div style={wrap}>
        {[0, 1, 2, 3].map(i => (
          <div key={i} style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '9px 11px', flex: isMobile ? '0 0 132px' : undefined }}>
            <span style={{ display: 'block', height: 11, width: '60%', borderRadius: R.sm, background: C.bgSelected }} />
            <span style={{ display: 'block', marginTop: 6, height: 16, width: '75%', borderRadius: R.sm, background: C.bgSelected }} />
          </div>
        ))}
      </div>
    );
  }

  const tiles: Tile[] = [];
  if (hot) tiles.push({ l: 'Ближе всего к пределу', value: V(`${hot.usedPct}%`, hot.label), hot: hot.usedPct >= 70 });
  if (money) tiles.push({ l: 'Деньги на счетах', value: V(`$${fmtMoney(money.amount)}`, plural(money.accounts, 'счёт', 'счёта', 'счетов')) });
  if (cost) tiles.push({ l: 'По тарифам API · 5 дней', value: V(`$${fmtCost(cost.amount)}`, 'столько стоил бы тот же объём') });
  if (free) tiles.push({ l: 'Бесплатно работают', value: V(`${free.alive} из ${free.total}`, `фоновых мест · успех ${Math.round(free.successRate)}%`) });

  if (tiles.length === 0) return null;

  // Мобила — горизонтальный скролл; десктоп — сетка по числу плиток (2..4)
  if (isMobile) {
    return (
      <div style={{ display: 'flex', overflowX: 'auto', gap: 8 }}>
        {tiles.map((t, i) => (
          <div key={i} style={{ flex: '0 0 132px' }}>
            <Kpi tile={t} />
          </div>
        ))}
      </div>
    );
  }

  const cols = Math.min(4, Math.max(2, tiles.length));
  return (
    <div style={{ display: 'grid', gridTemplateColumns: `repeat(${cols}, 1fr)`, gap: 8 }}>
      {tiles.map((t, i) => <Kpi key={i} tile={t} />)}
    </div>
  );
}
