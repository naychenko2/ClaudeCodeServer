import { useState, useRef, useEffect, useCallback } from 'react';

export interface ComposerProps {
  onSend: (text: string, attachments: string[]) => void;
  onStop: () => void;
  onAttach: () => void;
  isGenerating: boolean;
  mode: 'auto' | 'plan' | 'ask';
  onModeChange: (mode: 'auto' | 'plan' | 'ask') => void;
  attachments: string[];
  onRemoveAttachment: (path: string) => void;
  isMobile?: boolean;
}

type Mode = 'auto' | 'plan' | 'ask';
const MODE_META: Record<Mode, { label: string; desc: string }> = {
  auto: { label: 'Авто', desc: 'Claude действует сам и применяет правки' },
  plan: { label: 'План', desc: 'Сначала показывает план, ждёт подтверждения' },
  ask: { label: 'Спросить', desc: 'Спрашивает разрешение на каждое действие' },
};

const MODES: Mode[] = ['auto', 'plan', 'ask'];

// Штриховые иконки режимов (вместо цветных эмодзи) — монохромная иконографика эталона
function ModeIcon({ mode }: { mode: Mode }) {
  const p = { width: 14, height: 14, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const };
  if (mode === 'auto') return <svg {...p}><path d="M13 3v7h6l-8 11v-7H5l8-11z" /></svg>;
  if (mode === 'plan') return <svg {...p}><rect x="9" y="3" width="6" height="4" rx="1" /><path d="M9 5H7a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-2" /><path d="M9 12h6M9 16h4" /></svg>;
  return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M9.2 9.2a3 3 0 0 1 5.6 1c0 2-2.8 2.4-2.8 2.4" /><line x1="12" y1="17.2" x2="12.01" y2="17.2" /></svg>;
}

// Получить имя файла из пути
function basename(filePath: string): string {
  return filePath.replace(/\\/g, '/').split('/').pop() ?? filePath;
}

// Иконка файла по расширению
function FileIcon({ name }: { name: string }) {
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  const color =
    ['ts', 'tsx'].includes(ext) ? '#3178C6' :
    ['js', 'jsx'].includes(ext) ? '#F7DF1E' :
    ext === 'json' ? '#CB8A1F' :
    ext === 'md' ? '#5C5246' :
    ext === 'cs' ? '#9B4F96' :
    '#8A8072';

  return (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" style={{ flexShrink: 0 }}>
      <rect x="2" y="1" width="8" height="11" rx="1.5" fill={color} opacity="0.18" stroke={color} strokeWidth="1" />
      <text x="6" y="9" textAnchor="middle" fontSize="4.5" fill={color} fontFamily="monospace" fontWeight="700">
        {ext.slice(0, 3).toUpperCase()}
      </text>
    </svg>
  );
}

// SVG микрофона
function MicIcon() {
  return (
    <svg width="17" height="17" viewBox="0 0 17 17" fill="none">
      <rect x="6" y="1" width="5" height="9" rx="2.5" fill="currentColor" />
      <path d="M3 8.5C3 11.538 5.239 14 8.5 14C11.761 14 14 11.538 14 8.5" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <line x1="8.5" y1="14" x2="8.5" y2="16" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <line x1="6" y1="16" x2="11" y2="16" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  );
}

// SVG стрелки отправки
function SendIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
      <path d="M8 13V3M8 3L4 7M8 3L12 7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

// SVG стоп
function StopIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
      <rect x="2" y="2" width="12" height="12" rx="2" fill="currentColor" />
    </svg>
  );
}

export function Composer({
  onSend,
  onStop,
  onAttach,
  isGenerating,
  mode,
  onModeChange,
  attachments,
  onRemoveAttachment,
  isMobile,
}: ComposerProps) {
  const [text, setText] = useState('');
  const [isListening, setIsListening] = useState(false);
  const [modeMenuOpen, setModeMenuOpen] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const recognitionRef = useRef<any>(null);
  const modeRef = useRef<HTMLDivElement>(null);

  // Закрытие меню режимов по клику вне него
  useEffect(() => {
    if (!modeMenuOpen) return;
    const onDown = (e: MouseEvent) => {
      if (modeRef.current && !modeRef.current.contains(e.target as Node)) setModeMenuOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [modeMenuOpen]);

  const hasSpeech = typeof window !== 'undefined' &&
    ('SpeechRecognition' in window || 'webkitSpeechRecognition' in window);

  const hasText = text.trim().length > 0;

  // Авторазмер textarea
  const autoResize = useCallback(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 200) + 'px';
  }, []);

  useEffect(() => {
    autoResize();
  }, [text, autoResize]);

  const handleSend = () => {
    const t = text.trim();
    if (!t && attachments.length === 0) return;
    onSend(t, attachments);
    setText('');
    if (textareaRef.current) {
      textareaRef.current.style.height = '34px';
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // На мобиле Enter переносит строку, отправка — только кнопкой (десктоп: Enter отправляет)
    if (e.key === 'Enter' && !e.shiftKey && !isMobile) {
      e.preventDefault();
      handleSend();
    }
  };

  const toggleMic = () => {
    if (!hasSpeech) return;

    if (isListening) {
      recognitionRef.current?.stop();
      return;
    }

    const SpeechRecognitionCtor =
      (window as any).SpeechRecognition ?? (window as any).webkitSpeechRecognition;
    const rec = new SpeechRecognitionCtor() as any;
    rec.lang = 'ru-RU';
    rec.interimResults = false;
    rec.maxAlternatives = 1;

    rec.onresult = (e: SpeechRecognitionEvent) => {
      const transcript = e.results[0][0].transcript;
      setText(prev => (prev ? prev + ' ' + transcript : transcript));
    };

    rec.onend = () => setIsListening(false);
    rec.onerror = () => setIsListening(false);

    recognitionRef.current = rec;
    rec.start();
    setIsListening(true);
  };

  // Стили контейнера
  const containerStyle: React.CSSProperties = {
    background: isGenerating ? '#FBF8F2' : '#FFFFFF',
    border: `1px solid ${isGenerating ? '#E8E1D4' : hasText ? '#D97757' : '#E0D7C8'}`,
    borderRadius: 14,
    padding: isMobile ? '8px 10px' : '7px 8px',
    boxShadow: hasText && !isGenerating ? '0 3px 12px rgba(217,119,87,0.10)' : 'none',
    display: 'flex',
    flexDirection: 'column',
    gap: 0,
    transition: 'border-color 0.15s, box-shadow 0.15s, background 0.15s',
  };

  // Анимация трёх точек
  const dotsStyle: React.CSSProperties = {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    flex: 1,
    minHeight: 34,
    padding: '0 4px',
  };

  // --- Контролы (переиспользуются в обеих раскладках) ---

  const attachButton = (
    <button
      onClick={onAttach}
      title="Прикрепить файл"
      style={{
        width: 32, height: 32, borderRadius: 9, border: 'none', background: 'none',
        cursor: 'pointer', color: '#8A8072', display: 'flex', alignItems: 'center',
        justifyContent: 'center', flexShrink: 0,
      }}
    >
      <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
        <line x1="12" y1="5" x2="12" y2="19" />
        <line x1="5" y1="12" x2="19" y2="12" />
      </svg>
    </button>
  );

  const inputArea = isGenerating ? (
    <div style={dotsStyle}>
      <ThreeDots />
      <span style={{ fontStyle: 'italic', color: '#9A8F7E', fontSize: 14 }}>
        Claude печатает…
      </span>
    </div>
  ) : isListening ? (
    <div style={{ ...dotsStyle, gap: 9 }}>
      <span style={{ width: 9, height: 9, borderRadius: '50%', background: '#D9534F', animation: 'pulsedot 1s ease-in-out infinite', flexShrink: 0 }} />
      <span style={{ fontSize: 14, color: '#C2532E', fontWeight: 500, flexShrink: 0 }}>Слушаю… говорите</span>
      {text && <span style={{ fontSize: 13, color: '#9A8F7E', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1, minWidth: 0 }}>{text}</span>}
    </div>
  ) : (
    <textarea
      ref={textareaRef}
      value={text}
      onChange={(e) => setText(e.target.value)}
      onKeyDown={handleKeyDown}
      onInput={autoResize}
      placeholder="Спросите Claude…"
      rows={1}
      style={{
        flex: 1,
        width: isMobile ? '100%' : undefined,
        border: 'none',
        outline: 'none',
        resize: 'none',
        fontSize: isMobile ? 16 : 15, // 16px — чтобы iOS не зумил при фокусе
        color: '#39332B',
        background: 'transparent',
        minHeight: 34,
        maxHeight: 200,
        lineHeight: '1.5',
        padding: isMobile ? '6px 8px' : '6px 4px',
        fontFamily: 'inherit',
        overflowY: 'auto',
        boxSizing: 'border-box',
      }}
    />
  );

  const modeButton = (
    <div ref={modeRef} style={{ position: 'relative', flexShrink: 0 }}>
      <button
        onClick={() => setModeMenuOpen(o => !o)}
        title="Режим работы"
        style={{
          height: isMobile ? 32 : 28, padding: '0 10px', borderRadius: 8, border: 'none',
          background: modeMenuOpen ? '#EBE3D6' : '#F4ECE1', color: '#756B5E',
          fontSize: 12.5, fontWeight: 600, cursor: 'pointer', whiteSpace: 'nowrap',
          display: 'flex', alignItems: 'center', gap: 6,
        }}
      >
        <ModeIcon mode={mode} />
        {MODE_META[mode].label}
        <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round"
          style={{ opacity: 0.55, transform: modeMenuOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>
          <path d="M6 9l6 6 6-6" />
        </svg>
      </button>
      {modeMenuOpen && (
        <div style={{
          position: 'absolute', bottom: 'calc(100% + 6px)', left: 0, minWidth: 248,
          background: '#FFFFFF', border: '1px solid #E0D7C8', borderRadius: 12,
          boxShadow: '0 8px 28px rgba(60,50,35,0.16)', padding: 5, zIndex: 50,
        }}>
          {MODES.map(m => {
            const active = m === mode;
            return (
              <button key={m} onClick={() => { onModeChange(m); setModeMenuOpen(false); }}
                style={{
                  width: '100%', display: 'flex', alignItems: 'flex-start', gap: 9, padding: '8px 9px',
                  borderRadius: 9, border: 'none', background: active ? '#F4ECE1' : 'transparent',
                  cursor: 'pointer', textAlign: 'left',
                }}
                onMouseEnter={e => { if (!active) e.currentTarget.style.background = '#F7F2EA'; }}
                onMouseLeave={e => { if (!active) e.currentTarget.style.background = 'transparent'; }}
              >
                <span style={{ color: active ? '#D97757' : '#9A8F7E', display: 'flex', marginTop: 1, flexShrink: 0 }}><ModeIcon mode={m} /></span>
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: '#2A251F' }}>{MODE_META[m].label}</span>
                  <span style={{ display: 'block', fontSize: 11.5, color: '#9A8F7E', marginTop: 1, lineHeight: 1.35 }}>{MODE_META[m].desc}</span>
                </span>
                {active && (
                  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#D97757" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0, marginTop: 2 }}><path d="M20 6L9 17l-5-5" /></svg>
                )}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );

  const micButton = hasSpeech ? (
    <button
      onClick={toggleMic}
      title={isListening ? 'Остановить запись' : 'Голосовой ввод'}
      style={{
        width: isMobile ? 36 : 32,
        height: isMobile ? 36 : 32,
        borderRadius: 9,
        border: 'none',
        background: isListening ? '#FDECEA' : 'none',
        cursor: 'pointer',
        color: isListening ? '#D97757' : '#9A8F7E',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexShrink: 0,
        transition: 'color 0.15s, background 0.15s',
      }}
    >
      <MicIcon />
    </button>
  ) : null;

  const sendButton = isGenerating ? (
    <button
      onClick={onStop}
      title="Остановить"
      style={{
        width: isMobile ? 38 : 34,
        height: isMobile ? 38 : 34,
        borderRadius: 9,
        border: 'none',
        background: '#2A251F',
        color: '#F4F0E8',
        cursor: 'pointer',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexShrink: 0,
      }}
    >
      <StopIcon />
    </button>
  ) : (
    <button
      onClick={handleSend}
      disabled={!hasText && attachments.length === 0}
      title="Отправить (Enter)"
      style={{
        width: isMobile ? 38 : 34,
        height: isMobile ? 38 : 34,
        borderRadius: 9,
        border: 'none',
        background: hasText || attachments.length > 0 ? '#D97757' : '#E7DFD2',
        color: hasText || attachments.length > 0 ? '#FBF8F2' : '#B0A697',
        cursor: hasText || attachments.length > 0 ? 'pointer' : 'default',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        flexShrink: 0,
        transition: 'background 0.15s, color 0.15s',
      }}
    >
      <SendIcon />
    </button>
  );

  return (
    <div style={containerStyle}>
      {/* Чипы вложений */}
      {attachments.length > 0 && (
        <div
          style={{
            display: 'flex',
            flexWrap: 'wrap',
            gap: 7,
            padding: '11px 12px 8px',
          }}
        >
          {attachments.map((filePath) => {
            const name = basename(filePath);
            return (
              <div
                key={filePath}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 5,
                  background: '#F4ECE1',
                  borderRadius: 8,
                  height: 30,
                  padding: '0 9px 0 7px',
                  fontSize: 12,
                  color: '#5C5246',
                }}
              >
                <FileIcon name={name} />
                <span style={{ maxWidth: 160, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {name}
                </span>
                <button
                  onClick={() => onRemoveAttachment(filePath)}
                  style={{
                    background: 'none',
                    border: 'none',
                    cursor: 'pointer',
                    padding: 0,
                    marginLeft: 2,
                    color: '#9A8F7E',
                    lineHeight: 1,
                    fontSize: 13,
                    display: 'flex',
                    alignItems: 'center',
                  }}
                  title="Удалить"
                >
                  ✕
                </button>
              </div>
            );
          })}
        </div>
      )}

      {isMobile ? (
        /* Мобильная раскладка: поле ввода во всю ширину, контролы — отдельным рядом снизу */
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <div style={{ display: 'flex' }}>{inputArea}</div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            {attachButton}
            {modeButton}
            <div style={{ flex: 1 }} />
            {micButton}
            {sendButton}
          </div>
        </div>
      ) : (
        /* Десктоп: всё в одну строку */
        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
          {attachButton}
          {inputArea}
          {modeButton}
          {micButton}
          {sendButton}
        </div>
      )}
    </div>
  );
}

// Анимация трёх точек
function ThreeDots() {
  return (
    <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
      <div className="composer-dot" />
      <div className="composer-dot" />
      <div className="composer-dot" />
    </div>
  );
}
