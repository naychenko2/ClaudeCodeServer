// Карточка «Температуры»: линейный график рядов по времени.
//
// Окна 1 мин · 5 мин · 15 мин · 1 ч (своё состояние, дефолт 15 мин, не общее с LLM).
// Чипы семейств: память карт · ядро карт · горячая точка · процессор · чипсет · диски.
// По умолчанию выбрано ТОЛЬКО «память карт»; не больше 3 семейств сразу, остальные
// чипы disabled с подсказкой. Карты различаются начертанием: первая — сплошная,
// вторая — пунктир «4 3». При ОДНОМ выбранном семействе пунктиром C.warning рисуется
// его порог предупреждения (у дисков — порог диска кэша).
//
// Цвета рядов — только существующие токены (spec): mem C.accent, core C.info,
// hotspot C.plan, cpu C.success, chipset C.textSecondary, disk C.warning.
// При stale правый хвост закрыт C.bgInset 60 % с подписью «нет связи».

import { useState } from 'react';
import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import { InlineSegmented } from '../../components/ui';
import type { HardwareReport, HardwareSeries, HwFamily } from './hardwareTypes';
import { familyChipDisabled } from './hardwareFormat';
import type { HardwareWindow } from './useLocalModelHardware';

// Метаданные семейства: цвет ряда (только токены) + порог предупреждения для линии.
// Пороги — зеркало констант бэкенда (spec «Открытых вопросов нет»); используются
// ТОЛЬКО для линии-порога графика, уровень (level) фронт не пересчитывает.
const FAMILY_META: Record<HwFamily, { color: string; label: string; thr?: number; thrTxt?: string }> = {
  mem:     { color: C.accent,        label: 'Память карт',   thr: 95,  thrTxt: 'замедление памяти' },
  core:    { color: C.info,          label: 'Ядро карт',     thr: 85,  thrTxt: 'горячо' },
  hotspot: { color: C.plan,          label: 'Горячая точка', thr: 100, thrTxt: 'опасность' },
  cpu:     { color: C.success,       label: 'Процессор',     thr: 85,  thrTxt: 'горячо' },
  chipset: { color: C.textSecondary, label: 'Чипсет',        thr: 85,  thrTxt: 'горячо' },
  disk:    { color: C.warning,       label: 'Диски' },
};
const FAMILY_ORDER: HwFamily[] = ['mem', 'core', 'hotspot', 'cpu', 'chipset', 'disk'];
const MAX_FAMILIES = 3;

// Подписи осей времени по окну (как в макете).
const TIME_LABELS: Record<HardwareWindow, string[]> = {
  '1m': ['−1 мин', '−30 с', 'сейчас'],
  '5m': ['−5 мин', '−2,5', 'сейчас'],
  '15m': ['−15 мин', '−10', '−5', 'сейчас'],
  '1h': ['−1 ч', '−30', 'сейчас'],
};

// Ряд → цвет и пунктир: gpuIndex 0 → сплошная, 1 → пунктир «4 3», 2+ → «2 2».
function dashFor(series: HardwareSeries): string {
  if (series.gpuIndex == null) return '';
  return series.gpuIndex === 0 ? '' : series.gpuIndex === 1 ? '4 3' : '2 2';
}

// Строит polyline-сегменты ряда (null → разрыв).
function lineSegments(points: (number | null)[], x: (i: number) => number, y: (v: number) => number): string[][] {
  const segs: string[][] = [];
  let cur: string[] = [];
  points.forEach((v, i) => {
    if (v == null) { if (cur.length) { segs.push(cur); cur = []; } }
    else cur.push(`${x(i).toFixed(1)},${y(v).toFixed(1)}`);
  });
  if (cur.length) segs.push(cur);
  return segs;
}

export function TempChart({ report, window, onWindowChange, isMobile }: {
  report: HardwareReport;
  window: HardwareWindow;
  onWindowChange: (w: HardwareWindow) => void;
  isMobile: boolean;
}) {
  const [selected, setSelected] = useState<HwFamily[]>(['mem']);
  const stale = report.status === 'stale';

  const toggle = (f: HwFamily) => {
    setSelected(prev => prev.includes(f) ? prev.filter(x => x !== f) : (prev.length >= MAX_FAMILIES ? prev : [...prev, f]));
  };

  // Ряды выбранных семейств.
  const series = report.series.filter(s => selected.includes(s.family));

  // Масштаб Y: min…max всех точек (+порог, если один семейство) с запасом.
  const allVals: number[] = [];
  series.forEach(s => s.points.forEach(v => { if (v != null) allVals.push(v); }));
  let thr: number | null = null;
  let thrTxt = '';
  if (selected.length === 1) {
    const meta = FAMILY_META[selected[0]];
    if (selected[0] === 'disk') {
      // Порог диска кэша: critC − 10 (warn); critC — из узлов отчёта.
      const diskNode = report.nodes.find(n => n.kind === 'disk' && n.detail.role === 'kv');
      const crit = diskNode?.detail.critC;
      thr = crit != null ? crit - 10 : 70;
      thrTxt = 'горячо для диска кэша';
    } else if (meta.thr != null) {
      thr = meta.thr;
      thrTxt = meta.thrTxt ?? 'горячо';
    }
  }
  if (thr != null) allVals.push(thr);

  const H = 110;
  const PR = 26; // правое поле под подписи осей
  const pw = Math.max(80, (isMobile ? 300 : 340) - PR);
  const N = series.length ? Math.max(...series.map(s => s.points.length)) : 60;
  const lo = allVals.length ? Math.floor((Math.min(...allVals) - 5) / 10) * 10 : 30;
  const hi = allVals.length ? Math.ceil((Math.max(...allVals) + 3) / 10) * 10 : 100;
  const y = (v: number) => H - ((v - lo) / (hi - lo || 1)) * H;
  const x = (i: number) => (i * pw) / (N - 1 || 1);

  // Сетка: 3 горизонтали (lo, mid, hi) + подписи.
  const grid = [lo, (lo + hi) / 2, hi].map((v, j) => (
    <g key={j}>
      <line x1={0} x2={pw} y1={y(v)} y2={y(v)} stroke={j === 0 ? C.border : C.borderLight} />
      <text x={pw + PR} y={y(v) + (j === 2 ? 9 : j === 1 ? 3 : 0)} textAnchor="end"
        style={{ fontFamily: FONT.mono, fontSize: 10, fill: C.textMuted }}>{Math.round(v)}</text>
    </g>
  ));

  // Линии рядов.
  const lines = series.map((s, si) => {
    const color = FAMILY_META[s.family].color;
    const dash = dashFor(s);
    return lineSegments(s.points, x, y).map((seg, k) => (
      <polyline key={`${si}-${k}`} points={seg.join(' ')} fill="none" stroke={color}
        strokeWidth={1.75} strokeDasharray={dash} strokeLinejoin="round" />
    ));
  }).flat();

  // Линия-порог (один семейство).
  const thrLine = thr != null ? (
    <g>
      <line x1={0} x2={pw} y1={y(thr)} y2={y(thr)} stroke={C.warning} strokeDasharray="3 3" />
      <text x={2} y={y(thr) - 3} style={{ fontSize: 10, fill: C.warningText }}>{thrTxt} {thr}</text>
    </g>
  ) : null;

  // Zона «нет связи» (stale): правый хвост.
  const cut = Math.round(N * 0.86);
  const offZone = stale ? (
    <g>
      <rect x={x(cut)} y={0} width={pw - x(cut)} height={H} fill={C.bgInset} opacity={0.6} />
      <text x={(x(cut) + pw) / 2} y={H / 2} textAnchor="middle" style={{ fontFamily: FONT.mono, fontSize: 10, fill: C.textMuted }}>нет связи</text>
    </g>
  ) : null;

  // Легенда: цвет + пунктир + подпись + текущее значение.
  const legend = series.map((s, i) => {
    const color = FAMILY_META[s.family].color;
    const dash = dashFor(s);
    const last = [...s.points].reverse().find(v => v != null);
    const val = last == null ? '—' : `${Math.round(last)}°`;
    return (
      <span key={i} style={{ display: 'inline-flex', alignItems: 'center', gap: 4, whiteSpace: 'nowrap' }}>
        <svg width={14} height={6} style={{ display: 'inline-block' }}>
          <line x1={0} x2={14} y1={3} y2={3} stroke={color} strokeWidth={2} strokeDasharray={dash ? '3 2' : ''} />
        </svg>
        {s.label} <b style={{ fontFamily: FONT.mono, color: C.textHeading }}>{val}</b>
      </span>
    );
  });

  // Чипы семейств.
  const chips = FAMILY_ORDER.map(f => {
    const on = selected.includes(f);
    const dis = familyChipDisabled(on, selected.length, MAX_FAMILIES);
    return (
      <button key={f} onClick={() => toggle(f)} disabled={dis}
        title={dis ? 'Не больше трёх рядов сразу — снимите один' : undefined}
        aria-pressed={on}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 4, fontFamily: 'inherit', fontSize: FS.xs,
          padding: '2px 8px', borderRadius: R.max, border: `1px solid ${on ? C.accentMuted : C.border}`,
          background: on ? C.accentLight : 'transparent', color: on ? C.textHeading : C.textSecondary,
          fontWeight: on ? 600 : 400, cursor: dis ? 'not-allowed' : 'pointer',
          opacity: dis ? 0.45 : 1, whiteSpace: 'nowrap',
        }}>
        <i style={{ width: 10, height: 3, borderRadius: 2, display: 'inline-block', background: FAMILY_META[f].color }} />
        {FAMILY_META[f].label}
      </button>
    );
  });

  const timeLabels = TIME_LABELS[window];

  return (
    <section style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}>
      {/* Заголовок + окна */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 style={{ margin: 0, fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary, flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          Температуры
        </h3>
        <InlineSegmented<HardwareWindow>
          value={window}
          isMobile={isMobile}
          size="sm"
          onChange={onWindowChange}
          options={[
            { value: '1m', label: '1 мин' },
            { value: '5m', label: '5 мин' },
            { value: '15m', label: '15 мин' },
            { value: '1h', label: '1 ч' },
          ]}
        />
      </div>

      {/* Чипы семейств */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginBottom: SP.sm }}>
        {chips}
      </div>
      {selected.length >= MAX_FAMILIES && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `-${SP.xs}px 0 ${SP.sm}px` }}>
          Не больше трёх рядов сразу — так линии читаются.
        </div>
      )}

      {/* График или пустое состояние */}
      {series.length === 0 ? (
        <div style={{ height: H, borderRadius: 6, background: C.bgInset, display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: FS.sm, color: C.textMuted }}>
          Выберите, что показать
        </div>
      ) : (
        <>
          <svg width={pw + PR} height={H} style={{ display: 'block', overflow: 'visible' }}>
            {grid}
            {offZone}
            {thrLine}
            {lines}
          </svg>
          <div style={{ display: 'flex', justifyContent: 'space-between', fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, marginTop: 2, paddingRight: PR }}>
            {timeLabels.map((t, i) => <span key={i}>{t}</span>)}
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '2px 12px', fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xs }}>
            {legend}
          </div>
          {report.gpus.length > 1 && (
            <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `${SP.xs}px 0 0` }}>
              Карта 1 — сплошная линия, карта 2 — пунктир.
            </div>
          )}
        </>
      )}
    </section>
  );
}
