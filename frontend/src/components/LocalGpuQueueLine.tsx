// Строка ожидания локальной GPU-генерации: Dot + текст + ProgressBar.
// Презентационный компонент поверх форматтера describeLocalGpuQueue.
// Точка и текст — aria-live="polite"; полоса — role="progressbar" с aria-valuenow
// (где значение есть) или aria-busy (indeterminate).
// Токены C.*; высота строки не больше сегодняшнего статуса карточки.

import { Dot, ProgressBar, type ProgressTone } from './ui';
import { C, FS, SP } from '../lib/design';
import { describeLocalGpuQueue, type QueueKind } from '../lib/localGpuQueue';
import type { LocalGpuQueueInfo } from '../types/localGpuQueue';

// Цвет точки по состоянию: queued — muted (ждёт), running — accent, busy — warning,
// unknown — accent, down — нет точки.
const DOT_COLOR: Partial<Record<QueueKind, string>> = {
  queued: C.textMuted,
  running: C.accent,
  busy: C.warning,
  unknown: C.accent,
};

// Тон полосы по состоянию: queued/busy — muted (не наш прогресс), running/unknown — accent.
const BAR_TONE: Partial<Record<QueueKind, ProgressTone>> = {
  queued: 'muted',
  busy: 'muted',
  running: 'accent',
  unknown: 'accent',
};

export interface LocalGpuQueueLineProps {
  info: LocalGpuQueueInfo;
  /** Короткая форма (контейнер < 400 px) */
  compact?: boolean;
  /** Текущий момент (unix-мс); по умолчанию Date.now() */
  now?: number;
}

export function LocalGpuQueueLine({ info, compact = false, now = Date.now() }: LocalGpuQueueLineProps) {
  const line = describeLocalGpuQueue(info, { compact, now });
  const { kind, text, progress } = line;

  // Состояние down: нет ни точки, ни полосы — только текст danger
  if (kind === 'down') {
    return (
      <div style={rowStyle} aria-live="polite">
        <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.dangerText }}>{text}</span>
      </div>
    );
  }

  const dotColor = DOT_COLOR[kind] ?? C.accent;
  const barTone: ProgressTone = BAR_TONE[kind] ?? 'accent';

  return (
    <div style={rowStyle}>
      <div style={lineStyle} aria-live="polite">
        <Dot color={dotColor} size={8} />
        <span style={textStyle}>{text}</span>
      </div>
      {/* label не передаём: текст уже озвучивает aria-live-блок выше, дубль — двойное чтение */}
      {progress && <ProgressBar value={progress.fraction ?? 0} tone={barTone}
        estimate={progress.mode === 'estimate'}
        indeterminate={progress.mode === 'indeterminate'}
        size="thin"
      />}
    </div>
  );
}

// === Стили (только токены) ===

const rowStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: SP.xs,
  minWidth: 0,
};

const lineStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: SP.xs,
  flexWrap: 'wrap',
  fontSize: FS.sm,
  color: C.textSecondary,
  minWidth: 0,
};

const textStyle: React.CSSProperties = {
  minWidth: 0,
  fontVariantNumeric: 'tabular-nums',
};
