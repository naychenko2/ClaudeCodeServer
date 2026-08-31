import { useCallback, useEffect, useRef, type RefObject } from 'react';
import { Mic, X } from 'lucide-react';
import { C, R } from '../../lib/design';
import { useVoiceInput } from '../../hooks/useVoiceInput';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { VoiceRecordingRow } from './VoiceRecordingRow';

// Кнопка голосового ввода для произвольного `<input>`/`<textarea>` (включая
// React-контролируемые). Распознанный кусок дописывается в конец значения (UX как
// в Google Keep / iOS Notes). Значение правится через нативный setter + 'input'
// event — так onChange родителя срабатывает штатно (React-контролируемое поле
// перерендерится без рассинхрона с DOM).
//
// inputRef — стандартный RefObject. Если inputRef не подходит (например, массив refs
// у нескольких похожих полей), родитель передаёт inputGetter: () => element | null.
// Используется ровно один — что передали.
//
// ВАЖНО для родителя: поле, в которое пишем, обязано оставаться СМОНТИРОВАННЫМ всю
// запись (прятать — display: 'none', не размонтированием). Ушедшее поле обнуляет ref,
// и распознанному тексту некуда приезжать.

type InputElement = HTMLInputElement | HTMLTextAreaElement;
type InputGetter = () => InputElement | null;

// Движок распознавания на страницу ОДИН: второй startMic прерывает первый на уровне
// Web Speech API, но первая кнопка об этом не узнаёт и осталась бы висеть «в записи».
// Держим активную кнопку здесь и гасим её явно, до старта новой.
let activeStop: ((confirm: boolean) => void) | null = null;

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
  // Колбэк: «слушаю или нет». Форма вызывает это, чтобы спрятать своё поле — на его
  // месте кнопка сама рисует ряд индикации (см. recordingRow)
  onListeningChange?: (listening: boolean) => void;
  // Показывать на время записи ряд [точка, mm:ss, волна, ✕] вместо самой кнопки —
  // точь-в-точь композер. Родитель по onListeningChange прячет своё поле, а ряд встаёт
  // на его место. Ряд живёт ЗДЕСЬ, а не у родителя: иначе кнопка на время записи
  // размонтировалась бы, а вместе с ней — и распознавание (useVoiceInput гасит движок
  // при уходе компонента).
  recordingRow?: boolean;
  // Стиль ряда индикации. Нужен, когда родитель — flex-строка (IconField): ряд иначе
  // сжимается по содержимому. К кнопке отношения не имеет — у неё свой style
  rowStyle?: React.CSSProperties;
}

export function VoiceMicButton({ inputRef, inputGetter, variant = 'circle', style, isMobile, onListeningChange, recordingRow, rowStyle }: Props) {
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

  // Колбэк родителя держим в ref: без него смена его идентичности (а он часто
  // объявлен инлайном в JSX) дёргала бы эффект на каждый ререндер родителя
  const onListeningChangeRef = useRef(onListeningChange);
  useEffect(() => { onListeningChangeRef.current = onListeningChange; });

  // Эмитим состояние «слушаю/нет» форме — та прячет своё поле. Зависимость ровно одна,
  // поэтому колбэк зовётся только на реальной смене состояния
  useEffect(() => {
    onListeningChangeRef.current?.(isListening);
  }, [isListening]);

  // Ушли со страницы во время записи — снимаем себя с «активной кнопки», иначе
  // следующий startMic позвал бы stopMic мёртвого экземпляра
  useEffect(() => () => { if (activeStop === stopMic) activeStop = null; }, [stopMic]);

  const handleStart = useCallback(() => {
    if (activeStop && activeStop !== stopMic) activeStop(true);
    activeStop = stopMic;
    startMic();
  }, [startMic, stopMic]);

  // Остановка всегда с confirm=true: распознанное приезжает в поле сразу по кускам,
  // отменять нечего, а abort() потерял бы последнюю фразу
  const handleStop = useCallback(() => {
    if (activeStop === stopMic) activeStop = null;
    stopMic(true);
  }, [stopMic]);

  if (!hasSpeech) return null;

  if (recordingRow && isListening) {
    return <VoiceRecordingRow seconds={recSeconds} onStop={handleStop} isMobile={isMobile} style={rowStyle} />;
  }

  const isSuffix = variant === 'suffix';
  const btnSize = isSuffix ? 22 : (isMobile ? 36 : 32);
  const iconSize = isSuffix ? ICON_SIZE.xs : ICON_SIZE.sm;

  return (
    <button
      type="button"
      onClick={isListening ? handleStop : handleStart}
      onContextMenu={(e) => e.preventDefault()}
      // Состояние «слушает» — красная заливка как в композере (C.danger)
      title={isListening ? 'Голосовой ввод идёт · остановить' : 'Голосовой ввод'}
      style={{
        // Суффикс встаёт справа ВНУТРИ рамки поля (обёртка — position: relative,
        // поле держит правый паддинг под иконку)
        position: isSuffix ? 'absolute' : 'static',
        top: isSuffix ? '50%' : undefined,
        right: isSuffix ? 8 : undefined,
        transform: isSuffix ? 'translateY(-50%)' : undefined,
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
        ? <X size={iconSize} strokeWidth={ICON_STROKE} />
        : <Mic size={iconSize} strokeWidth={ICON_STROKE} />}
    </button>
  );
}
