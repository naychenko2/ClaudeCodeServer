import { Info } from 'lucide-react';
import { SP } from '../../lib/design';
import { Button } from '../ui/Button';
import { Notice } from '../ui/Notice';

// Плашка над полосой после снятия выбора человеком: «Картинка снята — дальше рисуем
// новую · Вернуть». Живёт, пока useReleaseUndo держит предложение (4 с).
export function ReleaseNotice({ text, onUndo, isMobile }: { text: string; onUndo: () => void; isMobile?: boolean }) {
  return (
    <Notice
      tone="info"
      icon={Info}
      style={{ alignItems: 'center', padding: `${SP.xs + 2}px ${isMobile ? SP.sm : SP.md - 2}px` }}
      action={<Button size="xs" variant="ghost" onClick={onUndo} style={{ flexShrink: 0 }}>Вернуть</Button>}
    >
      {text}
    </Notice>
  );
}
