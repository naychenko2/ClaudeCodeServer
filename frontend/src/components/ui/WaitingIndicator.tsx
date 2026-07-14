import { useState, useEffect } from 'react';
import { C } from '../../lib/design';
import { pickVerb } from '../chat/thinkingVerbs';

// Живой индикатор ожидания: пульс-аватар Claude + «печатная машинка» по синонимам.
// Текст печатается посимвольно с курсором, в конце дописывается «…», держит паузу,
// затем стирается и сменяется новым случайным синонимом. Общий для чата и любых
// других долгих ИИ-операций (подбор/генерация по кнопке «✨ …») — hint поясняет,
// что именно происходит и сколько примерно ждать.
export function WaitingIndicator({ planning, hint }: {
  planning?: 'planning' | 'replanning';
  hint?: string;
} = {}) {
  const [text, setText] = useState('');
  const reduced = typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  // Шутливые глаголы крутятся всегда; в режиме планирования отличается только цвет пульса (индиго)
  const pulseColor = planning ? C.plan : C.accent;

  useEffect(() => {
    // При reduced-motion — статичная подпись без анимации печати
    if (reduced) { setText(pickVerb() + '…'); return; }
    let timer = 0;
    let verb = pickVerb();
    let shown = '';
    let phase: 'typing' | 'pausing' | 'deleting' = 'typing';
    const tick = () => {
      const full = verb + '…';
      if (phase === 'typing') {
        shown = full.slice(0, shown.length + 1);
        setText(shown);
        if (shown.length >= full.length) { phase = 'pausing'; timer = window.setTimeout(tick, 1700); }
        else timer = window.setTimeout(tick, 55 + Math.random() * 50);
      } else if (phase === 'pausing') {
        phase = 'deleting';
        timer = window.setTimeout(tick, 35);
      } else {
        shown = shown.slice(0, -1);
        setText(shown);
        if (shown.length === 0) { verb = pickVerb(verb); phase = 'typing'; timer = window.setTimeout(tick, 260); }
        else timer = window.setTimeout(tick, 26);
      }
    };
    timer = window.setTimeout(tick, 140);
    return () => clearTimeout(timer);
  }, [reduced]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <span className="cc-pulse-ring" style={{
          width: 22, height: 22, borderRadius: 6, background: pulseColor,
          display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        }}>
          <span style={{ width: 9, height: 9, borderRadius: '50%', background: C.bgMain }} />
        </span>
        <span style={{ display: 'inline-flex', alignItems: 'baseline', minHeight: 17 }}>
          <span className="cc-shimmer-text" style={{ fontSize: 13, fontWeight: 600, fontFamily: 'inherit' }}>
            {text}
          </span>
          <span style={{
            display: 'inline-block', width: 2, height: '0.95em', marginLeft: 2,
            background: pulseColor, borderRadius: 1, alignSelf: 'center',
            animation: reduced ? 'none' : 'blink 1s step-start infinite',
          }} />
        </span>
      </div>
      {hint && (
        <span style={{ fontSize: 11.5, color: C.textMuted, marginLeft: 32, fontFamily: 'inherit' }}>
          {hint}
        </span>
      )}
    </div>
  );
}
