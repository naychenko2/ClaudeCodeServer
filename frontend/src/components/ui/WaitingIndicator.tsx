import { useState, useEffect } from 'react';
import { C } from '../../lib/design';
import { pickVerb } from '../chat/thinkingVerbs';
import aiHome from '../../assets/ai-home.png';
import { useContextPersona } from '../../lib/contextPersona';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';

// Высота бокса лица индикатора. Аватар — 28px; сверху и снизу резерв по 14px, чтобы
// максимальный размах колец «Эхо» (scale 1.85 от 28 ≈ +12px во все стороны) лежал
// ВНУТРИ бокса, а не торчал наружу. Торчащие transform'ом кольца входят в scrollable
// overflow контейнера ленты → scrollHeight пульсирует в такт анимации → лента дрожит.
// С резервом visual overflow бокса нулевой, scrollHeight стабилен. Подтверждено стендом
// (.cc-attachments/jitter-verify): при резерве 0 ΔscrollHeight=9px, при ≥12 — Δ=0.
const ECHO_FACE_W = 28;
const ECHO_FACE_H = 56;

// Живой индикатор ожидания: значок-логотип «AI Home» с расходящимися кольцами «Эхо»
// вокруг аватара персоны (или логотипа, если персоны нет) + «печатная машинка» по синонимам.
// Текст печатается посимвольно с курсором, в конце дописывается «…», держит паузу,
// затем стирается и сменяется новым случайным синонимом. Общий для чата и любых
// других долгих ИИ-операций (подбор/генерация по кнопке «✨ …») — hint поясняет,
// что именно происходит и сколько примерно ждать.
// В режиме awaitingResponse=true — фиксированный текст «Ожидаю ответа…» без анимации,
// т.к. Claude ждёт ввода пользователя.
export function WaitingIndicator({ planning, hint, awaitingResponse }: {
  planning?: 'planning' | 'replanning';
  hint?: string;
  awaitingResponse?: boolean;
} = {}) {
  const reduced = typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  // Шутливые глаголы крутятся всегда; в режиме планирования отличается только цвет колец (индиго)
  const pulseColor = planning ? C.plan : C.accent;
  // Цвет колец «Эхо»: нейтральный smoke по умолчанию, plan — в режиме планирования
  const ringColor = planning ? C.plan : C.smoke;
  // Лицо индикатора = релевантная персона контекста (фича default-personas-onboarding).
  // Аватар 28px с расходящимися кольцами «Эхо» поверх.
  const facePersona = useContextPersona();

  const [text, setText] = useState(awaitingResponse ? 'Ожидаю ответа…' : '');

  useEffect(() => {
    // Режим «Ожидаю ответа» — фиксированный текст без анимации печатания (кольца остаются)
    // eslint-disable-next-line react-hooks/set-state-in-effect -- анимация «печатания» текста таймерами
    if (awaitingResponse) { setText('Ожидаю ответа…'); return; }
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
  }, [reduced, awaitingResponse]);

  // Обёртка лица: внешний бокс с вертикальным резервом (ECHO_FACE_H), внутри — аватар
  // 28px по центру с двумя кольцами «Эхо» поверх. Резерв вмещает размах колец, чтобы они
  // не торчали за бокс (см. ECHO_FACE_H). --cc-echo-color на аватар-обёртке задаёт цвет
  // border колец — так одна анимация работает для smoke и plan режимов.
  const faceBox = (inner: React.ReactNode) => (
    <span
      style={{
        position: 'relative', width: ECHO_FACE_W, height: ECHO_FACE_H, flexShrink: 0,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      <span
        style={{
          position: 'relative', width: ECHO_FACE_W, height: ECHO_FACE_W, display: 'block',
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          ['--cc-echo-color' as any]: ringColor,
        }}
      >
        {inner}
        {!reduced && (
          <>
            <span className="cc-echo-ring cc-echo-ring--gutter" />
            <span className="cc-echo-ring cc-echo-ring--2 cc-echo-ring--gutter" />
          </>
        )}
      </span>
    </span>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      {/* minHeight = бокс лица (ECHO_FACE_H): строка НЕ меняет высоту ни при смене глагола,
          ни в пустой фазе печати. Сам бокс уже вмещает весь размах колец, так что visual
          overflow нулевой — ResizeObserver на contentRef не дёргается, scrollHeight ленты
          стабилен. */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, minHeight: ECHO_FACE_H }}>
        {/* Лицо: аватар релевантной персоны (фича default-personas-onboarding),
            fallback — логотип «AI Home» маской (тонируется под тему/режим) */}
        {facePersona ? faceBox(
          <span style={{
            position: 'absolute', inset: 0, display: 'block',
            borderRadius: '50%', overflow: 'hidden',
          }}>
            <PersonaAvatar persona={facePersona} size={28} />
          </span>
        ) : faceBox(
          <span style={{
            display: 'block', width: 28, height: 28, background: pulseColor,
            WebkitMaskImage: `url(${aiHome})`, maskImage: `url(${aiHome})`,
            WebkitMaskRepeat: 'no-repeat', maskRepeat: 'no-repeat',
            WebkitMaskPosition: 'center', maskPosition: 'center',
            WebkitMaskSize: 'contain', maskSize: 'contain',
          }} />
        )}
        {/* Текст + курсор. nowrap + ellipsis: длинный глагол не переносится (высота
            стабильна), а обрезается многоточием — у типичных коротких вариантов
            («Думаю», «Работаю») места хватает на любой ширине. alignItems center, а не
            baseline: в пустой фазе (между глаголами) baseline задаёт один курсор, и
            строку чуть перекашивало по высоте каждый цикл. */}
        <span style={{ display: 'inline-flex', alignItems: 'center', minHeight: 17, minWidth: 0, overflow: 'hidden' }}>
          <span className="cc-shimmer-text" style={{
            fontSize: 13, fontWeight: 600, fontFamily: 'inherit',
            whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden',
          }}>
            {text}
          </span>
          <span style={{
            display: 'inline-block', width: 2, height: '0.95em', marginLeft: 2, flexShrink: 0,
            background: pulseColor, borderRadius: 1, alignSelf: 'center',
            animation: (reduced || awaitingResponse) ? 'none' : 'blink 1s step-start infinite',
          }} />
        </span>
      </div>
      {hint && (
        <span style={{
          fontSize: 11.5, color: C.textMuted, marginLeft: 38, fontFamily: 'inherit',
          maxWidth: '100%',
        }}>
          {hint}
        </span>
      )}
    </div>
  );
}
