// Карточка видеокарты (GpuDetailCard) — только на вкладке «Железо».
// C.bgWhite, R.lg, SP.sm; warn/danger — рамка/фон по уровню карты.
//
// Главное — температура ПАМЯТИ GDDR6X: крупно (22 px) + шкала «запас до
// замедления N °C» (при ≥ 95 — «память замедляется»), маркер 95. Дальше плашки:
// «Сбрасывает частоту от перегрева» (danger, тревожная) и «Упирается в лимит
// мощности» (info, информационная). Строки: Ядро, Горячая точка, Вентилятор,
// Загрузка (bar 4 px C.accent), Видеопамять, Мощность. Внизу «Подробнее ▾»:
// частоты, шина памяти, режим питания, PCIe, шина.
//
// Вентилятор «0 %» — нейтральный (водянка/внешнее управление): сам по себе
// тревогой не считается, окрашивается цветом уровня карты, только если level
// карты warn/danger.

import { useState } from 'react';
import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import type { HardwareGpu, HwLevel } from './hardwareTypes';
import { hwGpuOrdinal, hwMemMargin, hwRolesLabel, hwScalePos } from './hardwareFormat';

// Тон значения по уровню: ok → обычный, warn/danger → warning/danger text.
function levelColor(level: HwLevel): string {
  if (level === 'danger') return C.dangerText;
  if (level === 'warn') return C.warningText;
  return C.textPrimary;
}

// Строка «ключ — значение»: FS.xs, значение mono 600; null → «—» (C.textMuted).
function Kv({ k, v, color }: { k: string; v: string; color?: string }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.xs, fontSize: FS.xs, padding: '1px 0', minWidth: 0 }}>
      <span style={{ color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{k}</span>
      <span style={{ fontFamily: FONT.mono, fontWeight: 600, color: color ?? C.textPrimary, whiteSpace: 'nowrap' }}>{v}</span>
    </div>
  );
}

// Плашка: info (C.infoBg/C.info) или danger (C.dangerBg/C.dangerText).
function Badge({ kind, text, title }: { kind: 'info' | 'danger'; text: string; title?: string }) {
  const bg = kind === 'danger' ? C.dangerBg : C.infoBg;
  const fg = kind === 'danger' ? C.dangerText : C.info;
  return (
    <span title={title} style={{
      display: 'inline-flex', alignItems: 'center', background: bg, color: fg,
      borderRadius: R.max, padding: `2px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600,
      whiteSpace: 'normal', lineHeight: 1.3,
    }}>{text}</span>
  );
}

export function GpuDetailCard({ gpu, total }: { gpu: HardwareGpu; total: number }) {
  const [open, setOpen] = useState(false);
  const L = gpu.level;
  const warnOrDanger = L === 'warn' || L === 'danger';
  const cardBg = L === 'danger' ? C.dangerBg : C.bgWhite;
  const cardBorder = L === 'danger' ? C.dangerBorder : L === 'warn' ? C.warning : C.borderLight;

  const totalGb = gpu.memTotalMb != null ? Math.round(gpu.memTotalMb / 1024) : null;
  const mem = gpu.memTempC;
  const memLevel = gpu.levels.mem;
  const scaleColor = memLevel === 'danger' ? C.danger : memLevel === 'warn' ? C.warning : C.success;

  // Вентилятор: 0 % нейтрален; цвет — только если уровень карты warn/danger.
  const fanColor = (gpu.fanPerc != null && warnOrDanger) ? levelColor(L) : C.textPrimary;

  const badges: React.ReactNode[] = [];
  if (gpu.throttle.thermal) badges.push(<Badge key="th" kind="danger" text="Сбрасывает частоту от перегрева" />);
  if (gpu.throttle.powerCap) badges.push(
    <Badge key="pc" kind="info" text="Упирается в лимит мощности"
      title="Карта берёт максимум разрешённой мощности — поэтому быстрее не станет. Это не поломка." />);

  const more = open && (
    <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: SP.xs, paddingTop: SP.xs }}>
      <Kv k="Частота ядра" v={gpu.clockMhz != null && gpu.clockMaxMhz != null ? `${gpu.clockMhz}/${gpu.clockMaxMhz} МГц` : '—'} />
      <Kv k="Частота памяти" v={gpu.memClockMhz != null ? `${gpu.memClockMhz} МГц` : '—'} />
      <Kv k="Шина памяти занята" v={gpu.memBusPerc != null ? `${gpu.memBusPerc} %` : '—'} />
      <Kv k="Режим питания" v={gpu.pstate ?? '—'} />
      <Kv k="Подключение" v={gpu.pcie.gen != null ? `PCIe ${gpu.pcie.gen}.0 ×${gpu.pcie.width ?? '?'}` : '—'} />
      <Kv k="Шина" v={gpu.busId} />
    </div>
  );

  return (
    <div style={{
      background: cardBg, border: `1px solid ${cardBorder}`, borderRadius: R.lg, padding: SP.sm, minWidth: 0,
    }}>
      {/* Шапка: точка + имя · роли */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.sm, fontWeight: 700, color: C.textHeading, minWidth: 0 }}>
        <span style={{ width: 8, height: 8, borderRadius: R.full, background: L === 'danger' ? C.danger : L === 'warn' ? C.warning : C.success, flex: 'none' }} />
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1, minWidth: 0 }}>
          {gpu.name} · {hwRolesLabel(gpu.roles)}
        </span>
      </div>
      {/* Подстрока: карта N из M · N ГБ */}
      <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `0 0 ${SP.xs}px`, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {hwGpuOrdinal(gpu.index, total)}{totalGb != null ? ` · ${totalGb} ГБ` : ''}
      </div>

      {/* Главное: температура памяти (22 px) + шкала */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 6 }}>
        <span style={{ fontFamily: FONT.mono, fontSize: 22, fontWeight: 600, lineHeight: 1.1, color: memLevel === 'danger' ? C.dangerText : memLevel === 'warn' ? C.warningText : C.textHeading }}>
          {mem == null ? <span style={{ fontSize: 13, fontStyle: 'italic', color: C.textMuted }}>нет данных</span> : `${Math.round(mem)} °C`}
        </span>
        <span style={{ fontSize: FS.xs, color: C.textSecondary }}>память карты</span>
      </div>
      {mem == null ? (
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>датчик памяти не ответил</div>
      ) : (
        <>
          <div style={{ position: 'relative', height: 6, background: C.track, borderRadius: R.max, margin: `${SP.xs}px 0 2px` }}>
            <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: `${hwScalePos(mem)}%`, borderRadius: R.max, background: scaleColor }} />
            <div style={{ position: 'absolute', top: -3, width: 2, height: 12, background: C.textHeading, borderRadius: 1, left: `${hwScalePos(95)}%` }} />
          </div>
          <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 10, color: C.textMuted, marginBottom: SP.xs, whiteSpace: 'nowrap' }}>
            <span>{hwMemMargin(mem)}</span>
            <span>95</span>
          </div>
        </>
      )}

      {badges.length > 0 && <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, margin: `${SP.xs}px 0` }}>{badges}</div>}

      <Kv k="Ядро" v={gpu.coreTempC != null ? `${Math.round(gpu.coreTempC)} °C` : '—'} color={levelColor(gpu.levels.core)} />
      <Kv k="Горячая точка" v={gpu.hotspotTempC != null ? `${Math.round(gpu.hotspotTempC)} °C` : '—'} color={levelColor(gpu.levels.hotspot)} />
      <Kv k="Вентилятор" v={gpu.fanPerc != null ? `${gpu.fanPerc} %` : '—'} color={fanColor} />
      <Kv k="Загрузка" v={gpu.utilPerc != null ? `${gpu.utilPerc} %` : '—'} />
      <div style={{ height: 4, background: C.track, borderRadius: R.max, overflow: 'hidden', margin: `0 0 ${SP.xs}px` }}>
        <div style={{ width: `${gpu.utilPerc != null ? Math.max(0, Math.min(100, gpu.utilPerc)) : 0}%`, height: '100%', borderRadius: R.max, background: C.accent }} />
      </div>
      <Kv k="Видеопамять" v={gpu.memUsedMb != null && gpu.memTotalMb != null
        ? `${(gpu.memUsedMb / 1024).toFixed(1).replace('.', ',')}/${Math.round(gpu.memTotalMb / 1024)} ГБ` : '—'} />
      <Kv k="Мощность" v={gpu.powerW != null
        ? (gpu.powerLimitW != null ? `${Math.round(gpu.powerW)}/${Math.round(gpu.powerLimitW)} Вт` : `${Math.round(gpu.powerW)} Вт`) : '—'} />

      {more}
      <button type="button" onClick={() => setOpen(o => !o)}
        style={{
          display: 'inline-flex', gap: 4, marginTop: SP.xs, background: 'transparent', border: 'none',
          cursor: 'pointer', fontSize: FS.xs, fontWeight: 600, color: C.accent, padding: 0,
        }}>
        {open ? 'Свернуть ▴' : 'Подробнее ▾'}
      </button>
    </div>
  );
}
