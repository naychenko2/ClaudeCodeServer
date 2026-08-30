import { useEffect, type RefObject } from 'react';
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
  // Колбэк: «слушаю или нет». Форма вызывает это, чтобы спрятать свой textarea
  // и показать ряд индикации (точь-в-точь как в композере — на месте textarea
  // появляется [dot, mm:ss, Waveform])
  onListeningChange?: (listening: boolean) => void;
}

export function VoiceMicButton({ inputRef, inputGetter, variant = 'circle', style, isMobile, onListeningChange }: Props) {
  const { hasSpeech, isListening, startMic, stopMic } = useVoiceInput({
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

  // Эмитим состояние «слушаю/нет» форме — та прячет свой инпут и показывает
  // ряд [dot, mm:ss, Waveform] в его месте (точь-в-точь композер). Колбэк зовём
  // только на смене состояния, чтобы не палить лишних ререндеров
  useEffect(() => {
    onListeningChange?.(isListening);
  }, [isListening, onListeningChange]);

  if (!hasSpeech) return null;

  const isSuffix = variant === 'suffix';
  const btnSize = isSuffix ? 22 : (isMobile ? 36 : 32);

  return (
    <button
      type="button"
      onClick={isListening ? () => stopMic(true) : startMic}
      onContextMenu={(e) => e.preventDefault()}
      // Состояние «слушает» — красная заливка как в композере (C.danger)
      title={isListening ? 'Голосовой ввод идёт · остановить' : 'Голосовой ввод'}
      style={{
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
  );
}
