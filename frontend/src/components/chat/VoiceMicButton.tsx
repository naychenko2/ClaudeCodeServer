import { type RefObject } from 'react';
import { Mic, X } from 'lucide-react';
import { C, R } from '../../lib/design';
import { useVoiceInput } from '../../hooks/useVoiceInput';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Кнопка голосового ввода для произвольного `<input>`/`<textarea>` (включая
// React-контролируемые). Распознанный кусок дописывается в конец значения (UX как
// в Google Keep / iOS Notes). Значение правится через нативный setter + 'input'
// event — так onChange родителя срабатывает штатно (React-контролируемое поле
// перерендерится без рассинхрона с DOM).
//
// Каждый экземпляр VoiceMicButton ведёт свой useVoiceInput. Одновременная запись
// с двух полей невозможна технически: движок распознавания один на страницу, второй
// startMic попросту прервёт первый.
//
// inputRef — стандартный RefObject. Если inputRef не подходит (например, массив refs
// у нескольких похожих полей), родитель передаёт inputGetter: () => element | null.
// Используется ровно один — что передали.

type InputElement = HTMLInputElement | HTMLTextAreaElement;
type InputGetter = () => InputElement | null;

interface Props {
  inputRef?: RefObject<InputElement | null>;
  inputGetter?: InputGetter;
  // Вид кнопки. По умолчанию — круглая иконка 32px, как в композере. Для тонких
  // однострочных полей (Field, IconField) уместна «встроенная» — справа внутри
  // рамки (absolute-positioned суффикс), стилизованная матчером
  variant?: 'circle' | 'suffix';
  // Стиль для absolute-позиционирования в variant='suffix'
  style?: React.CSSProperties;
  isMobile?: boolean;
}

const WAVE_DELAYS = [0.0, 0.12, 0.28, 0.45, 0.6, 0.32, 0.15, 0.5, 0.05, 0.36, 0.18, 0.42];

// mm:ss с ведущими нулями — как у секундомера в композере
function fmtRecTime(s: number): string {
  const mm = Math.floor(s / 60);
  const ss = s % 60;
  return `${mm}:${ss < 10 ? '0' : ''}${ss}`;
}

export function VoiceMicButton({ inputRef, inputGetter, variant = 'circle', style, isMobile }: Props) {
  const { hasSpeech, isListening, recSeconds, startMic, stopMic } = useVoiceInput({
    onResult: (chunk) => {
      const el = (inputGetter ?? (() => inputRef?.current ?? null))();
      if (!el) return;
      const newValue = el.value + chunk;
      const proto = Object.getPrototypeOf(el) as object;
      const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
      if (setter) setter.call(el, newValue);
      else el.value = newValue;
      el.dispatchEvent(new Event('input', { bubbles: true }));
    },
    onKeyboardFallback: () => {
      const el = (inputGetter ?? (() => inputRef?.current ?? null))();
      el?.focus();
    },
  });

  if (!hasSpeech) return null;

  const isSuffix = variant === 'suffix';
  const btnSize = isSuffix ? 22 : (isMobile ? 36 : 32);

  // === Индикация записи: пульсирующая точка + таймер + Waveform ===
  // Стиль матчит композер (dot/pulse + fmtRecTime + cc-wave-bar). Появляется ТОЛЬКО
  // когда мы реально слушаем — иначе кнопка мигает «я слушаю» до первого клика
  const indicator = isListening ? (
    <div
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        // suffix-вариант: уезжаем вверх и налево от кнопки, чтобы не наезжать
        // на текст поля; circle-вариант: справа от кнопки
        ...(isSuffix
          ? { position: 'absolute', right: 0, bottom: 'calc(100% + 4px)' }
          : { marginLeft: 8 }),
        padding: '3px 8px', borderRadius: 999,
        background: C.accentLight,
        fontFamily: 'var(--cc-font-mono, monospace)',
        fontSize: 11.5, fontWeight: 600, color: C.accent,
        whiteSpace: 'nowrap',
      }}
    >
      <span style={{
        width: 8, height: 8, borderRadius: '50%', background: C.accent,
        animation: 'pulsedot 1s ease-in-out infinite',
      }} />
      <span>{fmtRecTime(recSeconds)}</span>
      <span style={{ display: 'inline-flex', gap: 1.5, alignItems: 'center', height: 14 }}>
        {WAVE_DELAYS.map((d, i) => (
          <span key={i} className="cc-wave-bar" style={{ height: 14, animationDelay: `${d}s` }} />
        ))}
      </span>
    </div>
  ) : null;

  return (
    <>
      {indicator}
      <button
        type="button"
        onClick={isListening ? () => stopMic(true) : startMic}
        onContextMenu={(e) => e.preventDefault()}
        // Состояние «слушает» — красная заливка как в композере (C.danger)
        title={isListening ? 'Голосовой ввод идёт · остановить' : 'Голосовой ввод'}
        style={{
          position: isSuffix ? 'static' : 'static',
          top: isSuffix ? undefined : undefined,
          right: isSuffix ? undefined : undefined,
          transform: isSuffix ? undefined : undefined,
          width: btnSize, height: btnSize,
          borderRadius: R.pill,
          border: 'none',
          background: isListening ? C.dangerBg : 'transparent',
          color: isListening ? C.danger : C.textMuted,
          cursor: 'pointer',
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          flexShrink: 0,
          transition: 'color 0.15s, background 0.15s',
          zIndex: 1,
          ...style,
        }}
      >
        {isListening
          ? <X size={isSuffix ? 12 : ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          : <Mic size={isSuffix ? 12 : ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      </button>
    </>
  );
}
