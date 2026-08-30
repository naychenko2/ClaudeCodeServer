import { type RefObject, type CSSProperties } from 'react';
import { Mic } from 'lucide-react';
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

interface Props {
  inputRef: RefObject<HTMLInputElement | HTMLTextAreaElement | null>;
  // Вид кнопки. По умолчанию — круглая иконка 32px, как в композере. Для тонких
  // однострочных полей (Field, IconField) уместна «встроенная» — справа внутри
  // рамки (absolute-positioned суффикс), стилизованная матчером
  variant?: 'circle' | 'suffix';
  // Стиль для absolute-позиционирования в variant='suffix' (top/right внутри поля)
  style?: CSSProperties;
  isMobile?: boolean;
}

export function VoiceMicButton({ inputRef, variant = 'circle', style, isMobile }: Props) {
  const { hasSpeech, isListening, startMic, stopMic } = useVoiceInput({
    onResult: (chunk) => {
      const el = inputRef.current;
      if (!el) return;
      const newValue = el.value + chunk;
      // React-проксируемые поля: setter через прототип + dispatchEvent(input) — стандартный
      // приём, чтобы React заметил изменение (https://react.dev/reference/react-dom/components/input#controlling-an-input-with-a-state-variable)
      const proto = Object.getPrototypeOf(el) as object;
      const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
      if (setter) setter.call(el, newValue);
      else el.value = newValue;
      el.dispatchEvent(new Event('input', { bubbles: true }));
    },
    // Движка распознавания нет — фокусируем поле: штатный голосовой ввод Android
    // (на панели клавиатуры) подхватит
    onKeyboardFallback: () => inputRef.current?.focus(),
  });

  if (!hasSpeech) return null;

  const isSuffix = variant === 'suffix';
  const size = isSuffix ? 22 : (isMobile ? 36 : 32);

  return (
    <button
      type="button"
      onClick={isListening ? () => stopMic(true) : startMic}
      onContextMenu={(e) => e.preventDefault()}
      // Состояние «слушает» даём тоном заливки — рядом с обычными полями оно
      // смотрится спокойнее вибро/анимации
      title={isListening ? 'Голосовой ввод идёт · остановить' : 'Голосовой ввод'}
      style={{
        position: isSuffix ? 'absolute' : 'static',
        top: isSuffix ? '50%' : undefined,
        right: isSuffix ? 8 : undefined,
        transform: isSuffix ? 'translateY(-50%)' : undefined,
        width: size, height: size,
        borderRadius: R.pill,
        border: 'none',
        background: isListening ? C.accentLight : 'transparent',
        color: isListening ? C.accent : C.textMuted,
        cursor: 'pointer',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        flexShrink: 0,
        transition: 'color 0.15s, background 0.15s',
        zIndex: 1,
        ...style,
      }}
    >
      <Mic size={isSuffix ? 12 : ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </button>
  );
}
