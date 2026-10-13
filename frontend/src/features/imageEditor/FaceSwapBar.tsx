// Плашка режима «Подставить персонажа» над холстом (этап 2 · шаг 10b, по образцу CropBar):
// иконка шага + текст шага + Button ghost «Отмена». Фон C.bgPanel, рамка C.borderLight, R.lg, SHADOW.card.

import { Clock, ScanFace, X } from 'lucide-react';
import { Button, C, R, SHADOW, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { BarIcon } from './faceSwapView';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

function StepIcon({ icon }: { icon: BarIcon }) {
  if (icon === 'clock') return ic(Clock);
  if (icon === 'search') return (
    <span style={{
      width: 14, height: 14, flexShrink: 0, display: 'inline-block',
      border: '2px solid var(--c-progress-track)', borderTopColor: C.accent,
      borderRadius: '50%', animation: 'cc-spin 0.9s linear infinite',
    }} />
  );
  return ic(ScanFace);
}

export function FaceSwapBar({ text, icon, onCancel }: {
  text: string;
  icon: BarIcon;
  mobile: boolean;
  onCancel: () => void;
}) {
  return (
    <div data-face-swap-bar="true" style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: SP.sm, background: C.bgPanel, border: `1px solid ${C.borderLight}`,
      borderRadius: R.lg, boxShadow: SHADOW.card, fontSize: 12, color: C.textPrimary, lineHeight: 1.4,
      // Ширина по колонке холста, не больше 520: жёсткие 520 на планшете распирали колонку на панель (B1)
      width: '100%', maxWidth: 520, boxSizing: 'border-box',
    }}>
      <span style={{ color: C.accent, display: 'inline-flex', flexShrink: 0 }}><StepIcon icon={icon} /></span>
      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{text}</span>
      <Button size="xs" variant="ghost" leftIcon={ic(X)} onClick={onCancel}>Отмена</Button>
    </div>
  );
}
