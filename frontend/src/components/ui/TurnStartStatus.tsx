import type { StartPhase } from '../../types';
import { C, FS, SP } from '../../lib/design';
import { LiveDot } from './LiveDot';

// Тексты фаз старта хода. Утверждены Софьей 16.11.
export const START_PHASE_LABEL: Record<StartPhase, string> = {
  queued: 'В очереди — сначала доработает предыдущий ход',
  context: 'Собираю контекст…',
  launching: 'Запускаю…',
  waiting: 'Жду ответ модели…',
};

// Строка статуса старта хода: дышащая точка и подпись фазы, без таймера (секунды ожидание
// не сократят, а нервничать заставят). Текст переносится — на 360 px не выходит за ленту.
export function TurnStartStatus({ phase }: { phase: StartPhase }) {
  const label = START_PHASE_LABEL[phase];
  return (
    <div data-testid="turn-start-status" data-phase={phase} style={{
      alignSelf: 'flex-start', maxWidth: '100%', display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: `${SP.xs}px ${SP.md}px`, fontSize: FS.base, color: C.textSecondary,
    }}>
      <LiveDot label={label} />
      <span style={{ minWidth: 0, overflowWrap: 'anywhere' }}>{label}</span>
    </div>
  );
}
