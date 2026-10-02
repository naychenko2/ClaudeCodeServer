import { useEffect, useRef } from 'react';
import { Info } from 'lucide-react';
import { SP } from '../../lib/design';
import { Button } from '../ui/Button';
import { Notice } from '../ui/Notice';

// Подъём круглешка AI над плашкой: он стоит над композером (--cc-fab-bottom), а плашка
// висит над полосой вне высоты композера — без подъёма круг ложится на «Вернуть» (360)
export const FAB_RAISE_VAR = '--cc-fab-raise';

// Плашка над полосой после снятия выбора человеком: «Картинка снята — дальше рисуем
// новую · Вернуть». Живёт, пока useReleaseUndo держит предложение (4 с).
// На телефоне «Вернуть» — кнопка высотой 40: действие живёт 4 с, попасть пальцем
// надо с первого раза. raiseFab — плашка над полем ввода: пока она видна, круг AI
// поднимается на её высоту.
export function ReleaseNotice({ text, onUndo, isMobile, raiseFab }: {
  text: string; onUndo: () => void; isMobile?: boolean; raiseFab?: boolean;
}) {
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = box.current;
    if (!raiseFab || !el) return;
    const root = document.documentElement;
    const lift = () => root.style.setProperty(FAB_RAISE_VAR, `${Math.ceil(el.getBoundingClientRect().height) + SP.xs}px`);
    lift();
    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(lift) : null;
    ro?.observe(el);
    return () => { ro?.disconnect(); root.style.removeProperty(FAB_RAISE_VAR); };
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
