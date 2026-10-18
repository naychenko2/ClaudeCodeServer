// Плитка узла стенда (HwTile): R.lg, паддинг SP.sm, position:relative, overflow:hidden.
// Сверху вниз: точка-светофор + имя (FS.xs 600 C.textSecondary, многоточие),
// значение (mono 20 px 600), подпись датчика (10 px C.textSecondary), мини-график
// (h 24, на всю ширину), деталь (FS.xs, числа mono C.textPrimary).
//
// Фоновая иконка lucide: size 52, strokeWidth 1.5, C.textMuted, opacity 0.12,
// position:absolute top:SP.xs right:-6px, pointerEvents:none, aria-hidden — идёт
// ПЕРВОЙ в DOM, контент поверх. Плитка не кликабельна, hover нет.
//
// Окраска по level (бэкенд): ok → C.bgWhite/C.borderLight, точка C.success;
// warn → C.warningBg/C.warning, значение C.warningText; danger → C.dangerBg/
// C.dangerBorder, значение C.dangerText; нет данных → C.textMuted курсив, точка
// C.track, вместо графика подложка.

import type { ComponentType } from 'react';
import { Gpu, Cpu, Zap, CircuitBoard, HardDrive, MemoryStick, Fan } from 'lucide-react';
import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import type { HwTileView } from './hardwareFormat';
import { ruNum } from './hardwareFormat';
import { Sparkline, SparklinePlaceholder } from './Sparkline';

// kind → иконка lucide (имя из hardwareFormat.hwIcon).
const ICONS: Record<string, ComponentType<{ size?: number; strokeWidth?: number; color?: string; style?: React.CSSProperties; className?: string }>> = {
  Gpu, Cpu, Zap, CircuitBoard, HardDrive, MemoryStick, Fan,
};

// Тон плитки по level: фон/рамка/точка/цвет значения. stale — данные устарели:
// без окраски (C.textMuted), точка C.track, график обрывается.
function tone(level: HwTileView['level'], noData: boolean, stale: boolean): {
  bg: string; border: string; dot: string; value: string;
} {
  if (stale) return { bg: C.bgWhite, border: C.borderLight, dot: C.track, value: C.textMuted };
  if (noData) return { bg: C.bgWhite, border: C.borderLight, dot: C.track, value: C.textMuted };
  switch (level) {
    case 'danger': return { bg: C.dangerBg, border: C.dangerBorder, dot: C.danger, value: C.dangerText };
    case 'warn': return { bg: C.warningBg, border: C.warning, dot: C.warning, value: C.warningText };
    default: return { bg: C.bgWhite, border: C.borderLight, dot: C.success, value: C.textHeading };
  }
}

export function HwTile({ view, stale }: { view: HwTileView; stale?: boolean }) {
  const noData = view.value == null;
  const t = tone(view.level, noData, stale ?? false);
  const Icon = ICONS[view.icon] ?? Cpu;
  return (
    <div
      title={view.thresholdTitle || undefined}
      style={{
        position: 'relative', overflow: 'hidden', minWidth: 0,
        background: t.bg, border: `1px solid ${t.border}`, borderRadius: R.lg, padding: SP.sm,
      }}
    >
      {/* Фоновая иконка — первая в DOM, контент поверх (position:relative) */}
      <Icon
        size={52}
        strokeWidth={1.5}
        color={C.textMuted}
        aria-hidden
        style={{ position: 'absolute', top: SP.xs, right: -6, opacity: 0.12, pointerEvents: 'none' }}
      />
      {/* Точка-светофор + имя */}
      <div style={{ position: 'relative', display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden' }}>
        <span style={{ width: 8, height: 8, borderRadius: R.full, background: t.dot, flex: 'none' }} />
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{view.name}</span>
      </div>
      {/* Значение: mono 20 px 600; нет данных — курсив C.textMuted */}
      <div style={{
        position: 'relative', fontFamily: FONT.mono,
        fontSize: 20, fontWeight: 600, lineHeight: 1.2, margin: '2px 0 0', whiteSpace: 'nowrap',
        color: t.value, fontStyle: noData ? 'italic' : 'normal',
      }}>
        {noData ? (view.sensor === 'none' ? 'датчика нет' : 'нет данных') : `${view.dec ? ruNum(view.value!) : Math.round(view.value!)} °C`}
      </div>
      {/* Подпись датчика */}
      {view.sensorLabel && (
        <div style={{ position: 'relative', fontSize: 10, color: C.textSecondary, marginTop: 2 }}>{view.sensorLabel}</div>
      )}
      {/* Мини-график (h 24) или подложка, если нет данных */}
      <div style={{ position: 'relative' }}>
        {noData
          ? <SparklinePlaceholder />
          : <Sparkline points={view.spark} color={t.dot === C.track ? C.textMuted : (view.level === 'danger' ? C.danger : view.level === 'warn' ? C.warning : C.success)} />}
      </div>
      {/* Деталь: FS.xs, числа mono C.textPrimary */}
      {view.detail && (
        <div style={{ position: 'relative', fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.35, marginTop: 2 }}>{view.detail}</div>
      )}
    </div>
  );
}
