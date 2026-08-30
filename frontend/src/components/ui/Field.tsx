import { useState, useRef, useEffect } from 'react';
import type { ReactNode, CSSProperties, KeyboardEvent, Ref, RefObject } from 'react';
import { VoiceMicButton } from '../chat/VoiceMicButton';
import { VoiceRecordingRow } from '../chat/VoiceRecordingRow';
import { C, R, FONT, FIELD, SHADOW } from '../../lib/design';

// === Подпись поля (uppercase-лейбл формы) ===
export function FieldLabel({ children }: { children: ReactNode }) {
  return (
    <label style={{
      fontSize: 12, fontWeight: 600, color: C.textSecondary,
      textTransform: 'uppercase', letterSpacing: '0.05em',
    }}>
      {children}
    </label>
  );
}

// === Обёртка «лейбл + контрол + подсказка» ===
// error: текст ошибки у конкретного поля. Задан — рендерится вместо hint, цветом
// dangerText. Раньше поле не могло показать свою ошибку — общая шла плашкой выше
// формы, и человек не понимал, к чему она. Теперь фронт ошибки привязывает сюда
export function Field({ label, hint, error, children }: {
  label?: ReactNode; hint?: ReactNode; error?: ReactNode; children: ReactNode;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {label && <FieldLabel>{label}</FieldLabel>}
      {children}
      {error
        ? <span style={{ fontSize: 11.5, color: C.dangerText }}>{error}</span>
        : hint && <span style={{ fontSize: 11.5, color: C.textMuted }}>{hint}</span>}
    </div>
  );
}

// Базовый стиль контрола ввода с учётом фокуса и состояния ошибки
function controlStyle(focused: boolean, mono?: boolean, invalid?: boolean, extra?: CSSProperties): CSSProperties {
  const borderColor = invalid ? C.dangerBorder : (focused ? FIELD.borderFocus : C.border);
  return {
    background: FIELD.background,
    border: `1px solid ${borderColor}`,
    borderRadius: FIELD.borderRadius,
    padding: '10px 13px',
    fontSize: FIELD.fontSize,
    color: FIELD.color,
    outline: 'none',
    fontFamily: mono ? FONT.mono : 'inherit',
    width: '100%',
    boxSizing: 'border-box',
    boxShadow: invalid ? 'none' : (focused ? SHADOW.focus : 'none'),
    transition: 'border-color 0.15s, box-shadow 0.15s',
    ...extra,
  };
}

interface TextFieldProps {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
  mono?: boolean;
  autoFocus?: boolean;
  disabled?: boolean;
  letterSpacing?: string;
  onEnter?: () => void;
  // Поля с черновиком (значение уезжает в файл по завершении правки, а не по каждой букве)
  // должны знать о фокусе: пока поле в работе, его нельзя перезаписывать пришедшими данными
  onFocus?: () => void;
  onBlur?: () => void;
  onEscape?: () => void;
  // Автозаполнение. Дефолт «off»: иначе Android над клавиатурой выкидывает системную
  // плашку автозаполнения (пароли/карты/адреса) на любом поле, которое его эвристика
  // сочла формой. Настоящим полям логина и пароля значение передают явно
  autoComplete?: string;
  // Подсказка при наведении: нужна там, где под полем нет места для строки-пояснения
  // (поле в ряду чипов — вторая строка растянула бы ряд)
  title?: string;
  // Подкрашивает бордер C.dangerBorder и убирает focus-ring (красный сам по себе
  // уже достаточно заметный сигнал — лишний синий ореол перебивал бы тон)
  invalid?: boolean;
  style?: CSSProperties;
}

// Разметка поля, которому автозаполнение выключено. Chrome на Android вешает полосу
// автозаполнения (пароли, карты, адреса) над клавиатурой на ЛЮБОЙ однострочный input, а
// autocomplete="off" игнорирует умышленно — так решили в самом Chrome ещё в 2014-м. Единственный
// рычаг, который он слушает, — классификация поля: поля поиска автозаполнением не трогаются,
// это проверено на планшете. Отсюда подмена типа для полей с autoComplete="off".
// Роль возвращаем явно: без неё скринридер объявил бы «поле поиска» там, где вводят название
// заметки. Формально ARIA для type="search" роль textbox не разрешает — осознанный размен:
// живому человеку с озвучкой важнее правда о поле, чем чистота по букве спецификации.
function noFillProps(type: string, autoComplete: string) {
  const swap = type === 'text' && autoComplete === 'off';
  return { type: swap ? 'search' : type, role: swap ? 'textbox' : undefined };
}

// Voice-state для TextField/TextArea/IconField: когда voice=true, поле во
// время записи прячется, на его месте появляется VoiceRecordingRow (как в композере)
// — точка + mm:ss + волна + ✕. Таймер свой: useEffect с setInterval(1000), чистится
// при unmount и при смене isListening (React cleanup deps гарантирует).
function useVoiceFieldState() {
  const [isListening, setIsListening] = useState(false);
  const [recSeconds, setRecSeconds] = useState(0);
  useEffect(() => {
    if (!isListening) return;
    const t = setInterval(() => setRecSeconds(s => s + 1), 1000);
    return () => clearInterval(t);
  }, [isListening]);
  useEffect(() => {
    if (isListening) setRecSeconds(0);
  }, [isListening]);
  return { isListening, setIsListening, recSeconds };
}

// === Однострочное поле ввода с focus-ring ===
export function TextField({ value, onChange, placeholder, type = 'text', mono, autoFocus, disabled, letterSpacing, onEnter, onFocus, onBlur, onEscape, title, invalid, autoComplete = 'off', style, voice, isMobile }: TextFieldProps & { voice?: boolean; isMobile?: boolean }) {
  const [focused, setFocused] = useState(false);
  const elRef = useRef<HTMLInputElement>(null);
  const voiceState = useVoiceFieldState();
  // При включённом voice контейнер — relative, чтобы VoiceMicButton (variant='suffix',
  // размер 22×22, сидит на right:8) мог позиционироваться абсолютно справа внутри
  // поля. input получает правый паддинг 36 = 22px иконка + 8px right-offset + 6px запас
  const wrapperStyle: CSSProperties | undefined = voice ? { position: 'relative' } : undefined;
  const inputStyle: CSSProperties = voice
    ? controlStyle(focused, mono, invalid, { letterSpacing, paddingRight: 36, ...style })
    : controlStyle(focused, mono, invalid, { letterSpacing, ...style });
  return (
    <div style={wrapperStyle}>
      {voice && voiceState.isListening ? (
        <VoiceRecordingRow seconds={voiceState.recSeconds} onStop={() => voiceState.setIsListening(false)} isMobile={isMobile} />
      ) : (
        <input
          ref={elRef}
          {...noFillProps(type, autoComplete)}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          title={title}
          autoComplete={autoComplete}
          autoFocus={autoFocus}
          disabled={disabled}
          onFocus={() => { setFocused(true); onFocus?.(); }}
          onBlur={() => { setFocused(false); onBlur?.(); }}
          onKeyDown={onEnter || onEscape ? (e: KeyboardEvent) => {
            if (e.key === 'Enter') onEnter?.();
            if (e.key === 'Escape') onEscape?.();
          } : undefined}
          style={inputStyle}
        />
      )}
      {voice && !voiceState.isListening && (
        <VoiceMicButton
          inputRef={elRef as RefObject<HTMLInputElement | HTMLTextAreaElement | null>}
          variant="suffix"
          isMobile={isMobile}
          onListeningChange={voiceState.setIsListening}
        />
      )}
    </div>
  );
}

interface TextAreaProps {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  autoGrow?: boolean;
  minHeight?: number;
  // Потолок высоты: с autoGrow поле растёт до него, дальше — внутренний скролл
  // (иначе очень длинный текст разносит форму по высоте)
  maxHeight?: number;
  disabled?: boolean;
  autoComplete?: string;
  autoFocus?: boolean;
  onKeyDown?: (e: KeyboardEvent<HTMLTextAreaElement>) => void;
  style?: CSSProperties;
}

// === Многострочное поле с авто-ростом высоты ===
export function TextArea({ value, onChange, placeholder, autoGrow, minHeight = 80, maxHeight, disabled, autoFocus, onKeyDown, autoComplete = 'off', style, voice, isMobile }: TextAreaProps & { voice?: boolean; isMobile?: boolean }) {
  const [focused, setFocused] = useState(false);
  const ref = useRef<HTMLTextAreaElement>(null);
  const voiceState = useVoiceFieldState();

  useEffect(() => {
    if (!autoGrow) return;
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    // Ограничиваем рост потолком, если задан — дальше поле скроллится внутри
    el.style.height = `${maxHeight ? Math.min(el.scrollHeight, maxHeight) : el.scrollHeight}px`;
  }, [value, autoGrow, maxHeight]);

  // При включённом voice — оборачиваем в relative-div и даём textarea правый
  // паддинг под иконку 22px + 8px. VoiceMicButton в variant='suffix' позиционируется
  // абсолютно справа; иконка живёт поверх текста (zIndex 1), но поле остаётся
  // кликабельным по всей ширине
  const wrapperStyle: CSSProperties | undefined = voice ? { position: 'relative' } : undefined;
  const areaStyle: CSSProperties = voice
    ? controlStyle(focused, false, false, {
        minHeight, maxHeight, resize: 'none',
        overflow: autoGrow && !maxHeight ? 'hidden' : 'auto',
        lineHeight: 1.5, paddingRight: 36, ...style,
      })
    : controlStyle(focused, false, false, {
        minHeight, maxHeight, resize: 'none',
        overflow: autoGrow && !maxHeight ? 'hidden' : 'auto',
        lineHeight: 1.5, ...style,
      });

  return (
    <div style={wrapperStyle}>
      {voice && voiceState.isListening ? (
        <VoiceRecordingRow seconds={voiceState.recSeconds} onStop={() => voiceState.setIsListening(false)} isMobile={isMobile} />
      ) : (
        <textarea
          ref={ref}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          disabled={disabled}
          autoFocus={autoFocus}
          autoComplete={autoComplete}
          onKeyDown={onKeyDown}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          style={areaStyle}
        />
      )}
      {voice && !voiceState.isListening && (
        <VoiceMicButton
          inputRef={ref as RefObject<HTMLInputElement | HTMLTextAreaElement | null>}
          variant="suffix"
          isMobile={isMobile}
          onListeningChange={voiceState.setIsListening}
        />
      )}
    </div>
  );
}

interface IconFieldProps {
  icon?: ReactNode;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: string;
  mono?: boolean;
  disabled?: boolean;
  letterSpacing?: string;
  height?: number;
  radius?: number;
  fontSize?: number;
  style?: CSSProperties;
  // Дефолт «off» — см. комментарий у TextFieldProps: системная плашка автозаполнения
  // Android иначе лезет и на поля поиска
  autoComplete?: string;
  autoFocus?: boolean;
  onEnter?: () => void;
  inputRef?: Ref<HTMLInputElement>;
}

// === Поле с иконкой-префиксом (логин, поиск): бордер на обёртке, инпут без рамки ===
export function IconField({
  icon, value, onChange, placeholder, type = 'text', mono, disabled,
  letterSpacing, height = 50, radius = R.xxl, fontSize = 15, style,
  autoFocus, onEnter, inputRef, autoComplete = 'off', voice, isMobile,
}: IconFieldProps & { voice?: boolean; isMobile?: boolean }) {
  const [focused, setFocused] = useState(false);
  // Для voice нужен локальный ref (родительский inputRef мог быть не передан)
  const localRef = useRef<HTMLInputElement>(null);
  const wireRef = (inputRef ?? localRef) as RefObject<HTMLInputElement | null>;
  const voiceState = useVoiceFieldState();
  return (
    <div style={{
      position: 'relative',
      display: 'flex', alignItems: 'center', background: C.bgWhite,
      border: `1px solid ${focused ? C.accent : C.border}`,
      borderRadius: radius, padding: `0 ${voice ? 6 : 14}px 0 ${voice ? 6 : 14}px`, height,
      boxShadow: focused ? SHADOW.focus : 'none',
      transition: 'border-color 0.15s, box-shadow 0.15s',
      boxSizing: 'border-box', ...style,
    }}>
      {voice && voiceState.isListening ? (
        // Иконка слева/справа при записи не нужны — ряд сам показывает суть. Но обёртка
        // остаётся flex, и чтобы не схлопывалось, вешаем пустой боковой spacer
        <div style={{ flex: 1, display: 'flex', alignItems: 'center' }}>
          <VoiceRecordingRow seconds={voiceState.recSeconds} onStop={() => voiceState.setIsListening(false)} isMobile={isMobile} />
        </div>
      ) : (
        <>
          {icon && (
            <span style={{ color: focused ? C.accent : C.textMuted, marginRight: 9, display: 'flex', flexShrink: 0, transition: 'color 0.15s' }}>
              {icon}
            </span>
          )}
          <input
            ref={wireRef}
            {...noFillProps(type, autoComplete)}
            value={value}
            onChange={(e) => onChange(e.target.value)}
            placeholder={placeholder}
            disabled={disabled}
            autoComplete={autoComplete}
            autoFocus={autoFocus}
            onFocus={() => setFocused(true)}
            onBlur={() => setFocused(false)}
            onKeyDown={onEnter ? (e: KeyboardEvent) => { if (e.key === 'Enter') onEnter(); } : undefined}
            style={{
              border: 'none', background: 'none', flex: 1, fontSize,
              color: C.textHeading, fontFamily: mono ? FONT.mono : 'inherit',
              letterSpacing, outline: 'none', opacity: disabled ? 0.6 : 1, minWidth: 0,
              // 22px-иконка суффикса + 8px её right-offset + 6px запас = 36 (как в TextField/TextArea)
              paddingRight: voice ? 36 : undefined,
            }}
          />
        </>
      )}
      {voice && !voiceState.isListening && (
        <VoiceMicButton
          inputRef={wireRef as RefObject<HTMLInputElement | HTMLTextAreaElement | null>}
          variant="suffix"
          isMobile={isMobile}
          onListeningChange={voiceState.setIsListening}
        />
      )}
    </div>
  );
}
