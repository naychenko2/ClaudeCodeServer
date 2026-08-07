// Окно квоты — один ряд в карточке (.wins): подпись (ширина labelWidth), индикатор,
// значение. Два вида по ProviderQuotaWindow.unit:
//   percent — шкала ИЗРАСХОДОВАННОГО (в контракте value — остаток, переводим);
//   count   — сегменты «занято 3 из 5»: моментальный снимок, шкала расхода врёт.
// percent без доли (подписка «в пределах нормы») — без шкалы, значение sans/muted.
import type { ProviderQuotaWindow } from '../../types';
import { C, FONT, FS, SP } from '../../lib/design';

// Тон шкалы по израсходованной доле (тот же порог, что на экране «Использование»)
export const barTone = (used: number) => (used >= 90 ? C.danger : used >= 70 ? C.warning : C.success);
export const barTextTone = (used: number) =>
  used >= 90 ? C.dangerText : used >= 70 ? C.warningText : C.textHeading;

export interface QuotaWindowView {
  label: string;
  kind: 'percent' | 'count';
  usedPct: number | null;        // percent: израсходованная доля 0..100
  usedCount: number | null;      // count: занятые сегменты
  totalCount: number | null;     // count: всего сегментов
  valueText: string;             // «78%» либо «3 из 5»
  resetsAt: string | null;
  exhausted: boolean;
}

const COUNT_RE = /^\s*(\d+(?:[.,]\d+)?)\s*\/\s*(\d+(?:[.,]\d+)?)\s*$/;

// Сырое окно из ProviderBalanceInfo → вид. Парсинг не удался — честные null,
// рисуем значение как есть без индикатора (без выдуманных нулей).
export function parseQuotaWindow(w: ProviderQuotaWindow): QuotaWindowView {
  if (w.unit === 'count') {
    const m = COUNT_RE.exec(w.value);
    const used = m ? parseFloat(m[1].replace(',', '.')) : null;
    const total = m ? parseFloat(m[2].replace(',', '.')) : null;
    return {
      label: w.label,
      kind: 'count',
      usedPct: null,
      usedCount: used,
      totalCount: total,
      valueText: used !== null && total !== null ? `${used} из ${total}` : w.value,
      resetsAt: w.resetsAt,
      exhausted: used !== null && total !== null && total > 0 && used >= total,
    };
  }
  const remaining = parseFloat(w.value);
  const usedPct = isNaN(remaining) ? null : Math.round(Math.min(100, Math.max(0, 100 - remaining)));
  return {
    label: w.label,
    kind: 'percent',
    usedPct,
    usedCount: null,
    totalCount: null,
    valueText: usedPct === null ? w.value : `${usedPct}%`,
    resetsAt: w.resetsAt,
    exhausted: usedPct !== null && usedPct >= 100,
  };
}

// width подписи задаётся параметром labelWidth — у подписок подписи длиннее
// («Неделя · Opus», «Перерасход · месяц»), 64px их обрезает до «Неделя · …»
const labelStyle: React.CSSProperties = {
  flexShrink: 0, fontSize: FS.xs, color: C.textMuted,
  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
const valueStyle: React.CSSProperties = {
  flexShrink: 0, minWidth: 34, textAlign: 'right',
  fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
};
// «Спокойное» значение — percent-окно без доли (подписка «в пределах нормы»):
// sans/muted без minWidth, иначе разорвёт ряд моно-значений («в пределах нормы»
// рядом с «78%» уезжало бы и ломало выравнивание). Формулировка та же, что у
// UsageWidget.WindowRow — язык у продукта один.
const calmValueStyle: React.CSSProperties = {
  flexShrink: 0, textAlign: 'right',
  fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
};

// Ряд-сегменты для count-окна: дорожка C.track, занятые — C.info (решение §5: не шкала)
function CountSegments({ used, total }: { used: number; total: number }) {
  return (
    <span style={{ display: 'flex', gap: 3, flex: 1, minWidth: 0, flexWrap: 'wrap' }}>
      {Array.from({ length: total }, (_, i) => (
        <span key={i} style={{
          display: 'block', width: 14, height: 6, borderRadius: 3,
          background: i < used ? C.info : C.track,
        }} />
      ))}
    </span>
  );
}

export function QuotaWindow({ w, dim, labelWidth = 64 }: { w: QuotaWindowView; dim?: boolean; labelWidth?: number }) {
  const color = w.usedPct === null ? C.textMuted : barTextTone(w.usedPct);
  // percent-окно без доли → «спокойное» sans-значение вместо моно-жирного
  const calmValue = w.kind === 'percent' && w.usedPct === null;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
      <span style={{ ...labelStyle, width: labelWidth }} title={w.label}>{w.label}</span>
      {w.kind === 'percent' ? (
        w.usedPct === null ? (
          <span style={{ flex: 1 }} />
        ) : (
          <span style={{
            display: 'block', flex: 1, height: 6, borderRadius: 3,
            background: C.track, overflow: 'hidden', opacity: dim ? 0.4 : 1,
          }}>
            <span style={{ display: 'block', width: `${Math.min(100, Math.max(2, w.usedPct))}%`, height: '100%', background: barTone(w.usedPct) }} />
          </span>
        )
      ) : (
        w.usedCount !== null && w.totalCount !== null
          ? <CountSegments used={w.usedCount} total={w.totalCount} />
          : <span style={{ flex: 1 }} />
      )}
      <span style={calmValue ? calmValueStyle : { ...valueStyle, color }}>{w.valueText}</span>
    </div>
  );
}
