// Окно квоты — один ряд в карточке (.wins): подпись (ширина labelWidth), индикатор,
// значение. Четыре вида по ProviderQuotaWindow.unit:
//   percent  — шкала ИЗРАСХОДОВАННОГО (в контракте value — остаток, переводим);
//   consumed — то же, но value уже «N/M» израсходовано из лимита (растёт, сбрасывается);
//   count    — сегменты «занято 3 из 5»: моментальный снимок, шкала расхода врёт;
//   alive    — сегменты «живых 6 из 6»: чем больше, тем лучше (FreeLLM). Не окно
//              расхода и не исчерпание: пул без живых платформ закрывается флагом
//              balance.available, а не значением окна.
// percent без доли (подписка «в пределах нормы») — без шкалы, значение sans/muted.
import type { ProviderQuotaWindow } from '../../types';
import { C, FONT, FS, SP } from '../../lib/design';

// Тон шкалы по израсходованной доле (тот же порог, что на экране «Использование»)
export const barTone = (used: number) => (used >= 90 ? C.danger : used >= 70 ? C.warning : C.success);
export const barTextTone = (used: number) =>
  used >= 90 ? C.dangerText : used >= 70 ? C.warningText : C.textHeading;

export interface QuotaWindowView {
  label: string;
  kind: 'percent' | 'count' | 'consumed' | 'alive';
  usedPct: number | null;        // percent/consumed: израсходованная доля 0..100
  usedCount: number | null;      // count/consumed/alive: занятые/израсходованные/живые единицы
  totalCount: number | null;     // count/consumed/alive: всего (лимит)
  valueText: string;             // «78%», «3 из 5», «101 из 4 000»
  resetsAt: string | null;
  exhausted: boolean;
}

const COUNT_RE = /^\s*(\d+(?:[.,]\d+)?)\s*\/\s*(\d+(?:[.,]\d+)?)\s*$/;

// Сырое окно из ProviderBalanceInfo → вид. Парсинг не удался — честные null,
// рисуем значение как есть без индикатора (без выдуманных нулей).
export function parseQuotaWindow(w: ProviderQuotaWindow): QuotaWindowView {
  if (w.unit === 'count' || w.unit === 'consumed' || w.unit === 'alive') {
    const m = COUNT_RE.exec(w.value);
    const used = m ? parseFloat(m[1].replace(',', '.')) : null;
    const total = m ? parseFloat(m[2].replace(',', '.')) : null;
    if (w.unit === 'consumed') {
      // Израсходовано из лимита: шкала как у percent (светофор barTone), но числа — из value.
      // usedPct только при total > 0; иначе индикатора нет, значение печатаем как есть
      const usedPct = used !== null && total !== null && total > 0
        ? Math.min(100, Math.max(0, Math.round(used / total * 100)))
        : null;
      return {
        label: w.label, kind: 'consumed', usedPct,
        usedCount: used, totalCount: total,
        valueText: used !== null && total !== null
          ? `${used.toLocaleString('ru-RU')} из ${total.toLocaleString('ru-RU')}`
          : w.value,
        resetsAt: w.resetsAt,
        exhausted: used !== null && total !== null && total > 0 && used >= total,
      };
    }
    // count/alive: сегменты «N из M», моментальный снимок — шкала расхода врала бы.
    // exhausted только у count (выбор лимита целиком): alive — это живые платформы из
    // всех, чем больше, тем лучше; пул без живых закрывается флагом balance.available,
    // а не «исчерпанием» окна
    return {
      label: w.label, kind: w.unit, usedPct: null,
      usedCount: used, totalCount: total,
      valueText: used !== null && total !== null ? `${used} из ${total}` : w.value,
      resetsAt: w.resetsAt,
      exhausted: w.unit === 'count' && used !== null && total !== null && total > 0 && used >= total,
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
  flexShrink: 0, minWidth: 34, textAlign: 'right', whiteSpace: 'nowrap',
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

// Выше этого знаменателя стена плашек перестаёт читаться (~4 ряда десктоп, ~7 на 320px) —
// вместо сегментов дорожка-заливка. Цвет C.info, как у самих сегментов: занятость — не расход,
// светофор barTone врал бы («30 сессий заняты» это ожидание, а не исчерпание)
const MAX_SEGMENTS = 40;

// Сегменты «занято N из M»: занятые — C.info, остальные — C.track. Выше MAX_SEGментов —
// дорожка-заливка тем же C.info. label — слово в title fallback-режима: для квот «Занято»,
// для живых платформ FreeLLM передаётся «Живо» (одно поведение, минус копия разметки).
export function CountSegments({ used, total, label = 'Занято' }: { used: number; total: number; label?: string }) {
  if (total > MAX_SEGMENTS) {
    const pct = Math.min(100, Math.max(2, Math.round(used / total * 100)));
    return (
      <span title={`${label} ${used.toLocaleString('ru-RU')} из ${total.toLocaleString('ru-RU')}`}
        style={{ display: 'block', flex: 1, minWidth: 24, height: 6, borderRadius: 3, background: C.track, overflow: 'hidden' }}>
        <span style={{ display: 'block', width: `${pct}%`, height: '100%', background: C.info }} />
      </span>
    );
  }
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

// title на ряд для consumed: «Израсходовано 101 из 4 000 · 3%». Доля округляется в ноль при
// ненулевом расходе → «· менее 1%»; при нулевом (или без знаменателя) — без доли
function consumedTitle(w: QuotaWindowView): string | undefined {
  if (w.kind !== 'consumed' || w.usedCount === null || w.totalCount === null) return undefined;
  const base = `Израсходовано ${w.usedCount.toLocaleString('ru-RU')} из ${w.totalCount.toLocaleString('ru-RU')}`;
  if (w.usedPct === null || w.usedCount === 0) return base;
  return w.usedPct === 0 ? `${base} · менее 1%` : `${base} · ${w.usedPct}%`;
}

export function QuotaWindow({ w, dim, labelWidth = 64 }: { w: QuotaWindowView; dim?: boolean; labelWidth?: number }) {
  const color = w.usedPct === null ? C.textMuted : barTextTone(w.usedPct);
  // percent-окно без доли → «спокойное» sans-значение вместо моно-жирного
  const calmValue = w.kind === 'percent' && w.usedPct === null;
  // Дорожка шкалы — percent/consumed при наличии доли; count/alive рисуют сегменты отдельно.
  // minWidth дорожки страхует от схлопывания в ноль на 320px при длинном значении
  const hasBar = w.kind !== 'count' && w.kind !== 'alive' && w.usedPct !== null;
  const pct = w.usedPct;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }} title={consumedTitle(w)}>
      <span style={{ ...labelStyle, width: labelWidth }} title={w.label}>{w.label}</span>
      {w.kind === 'count' || w.kind === 'alive' ? (
        w.usedCount !== null && w.totalCount !== null
          ? <CountSegments used={w.usedCount} total={w.totalCount} label={w.kind === 'alive' ? 'Живых' : 'Занято'} />
          : <span style={{ flex: 1 }} />
      ) : hasBar && pct !== null ? (
        <span style={{
          display: 'block', flex: 1, minWidth: 24, height: 6, borderRadius: 3,
          background: C.track, overflow: 'hidden', opacity: dim ? 0.4 : 1,
        }}>
          <span style={{ display: 'block', width: `${Math.min(100, Math.max(2, pct))}%`, height: '100%', background: barTone(pct) }} />
        </span>
      ) : (
        <span style={{ flex: 1 }} />
      )}
      <span style={calmValue ? calmValueStyle : { ...valueStyle, color }}>{w.valueText}</span>
    </div>
  );
}
