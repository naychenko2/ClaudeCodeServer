import { useEffect, useRef } from 'react';
import { Info } from 'lucide-react';
import { SP } from '../../lib/design';
import { setFabObstacle } from '../../lib/ai/fabObstacle';
import { Button } from '../ui/Button';
import { Notice } from '../ui/Notice';

// Плашка над полосой после снятия выбора человеком: «Картинка снята — дальше рисуем
// новую · Вернуть». Живёт, пока useReleaseUndo держит предложение (4 с).
// На телефоне «Вернуть» — кнопка высотой 40: действие живёт 4 с, попасть пальцем
// надо с первого раза. raiseFab — плашка над полем ввода: пока она видна, она
// препятствие круглешка AI (слот notice), и круг сам уходит с «Вернуть» (useFabPlacement).
export function ReleaseNotice({ text, onUndo, isMobile, raiseFab }: {
  text: string; onUndo: () => void; isMobile?: boolean; raiseFab?: boolean;
}) {
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = box.current;
    if (!raiseFab || !el) return;
    return setFabObstacle(el, 'notice');
  }, [raiseFab]);
  return (
    <div ref={box}>
      <Notice
        tone="info"
        icon={Info}
        style={{ alignItems: 'center', padding: `${SP.xs + 2}px ${isMobile ? SP.sm : SP.md - 2}px` }}
        action={<Button size={isMobile ? 'md' : 'xs'} variant="ghost" onClick={onUndo} style={{ flexShrink: 0 }}>Вернуть</Button>}
      >
        {text}
      </Notice>
    </div>
  );
}
