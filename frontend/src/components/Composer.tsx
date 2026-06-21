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
}

const MODE_LABELS: Record<string, string> = {
  auto: '⚡ Авто',
  plan: '📋 План',
  ask: '❓ Спросить',
};

const MODES: Array<'auto' | 'plan' | 'ask'> = ['auto', 'plan', 'ask'];

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
}: ComposerProps) {
  const [text, setText] = useState('');
  const [isListening, setIsListening] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const recognitionRef = useRef<any>(null);

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
    if (e.key === 'Enter' && !e.shiftKey) {
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

  // Цикл по режимам при клике на кнопку режима
  const handleModeClick = () => {
    const idx = MODES.indexOf(mode);
    onModeChange(MODES[(idx + 1) % MODES.length]);
  };

  // Стили контейнера
  const containerStyle: React.CSSProperties = {
    background: isGenerating ? '#FBF8F2' : '#FFFFFF',
    border: `1px solid ${isGenerating ? '#E8E1D4' : hasText ? '#D97757' : '#E0D7C8'}`,
    borderRadius: 14,
    padding: '7px 8px',
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

      {/* Основная строка */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
        {/* + Вложить */}
        <button
          onClick={onAttach}
          title="Прикрепить файл"
          style={{
            width: 32,
            height: 32,
            borderRadius: 9,
            border: 'none',
            background: 'none',
            cursor: 'pointer',
            color: '#8A8072',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            flexShrink: 0,
            fontSize: 20,
            lineHeight: 1,
          }}
        >
          +
        </button>

        {/* Textarea или анимация генерации */}
        {isGenerating ? (
          <div style={dotsStyle}>
            <ThreeDots />
            <span style={{ fontStyle: 'italic', color: '#9A8F7E', fontSize: 14 }}>
              Claude печатает…
            </span>
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
              border: 'none',
              outline: 'none',
              resize: 'none',
              fontSize: 15,
              color: '#39332B',
              background: 'transparent',
              minHeight: 34,
              maxHeight: 200,
              lineHeight: '1.5',
              padding: '6px 4px',
              fontFamily: 'inherit',
              overflowY: 'auto',
            }}
          />
        )}

        {/* Режим ⚡ */}
        <button
          onClick={handleModeClick}
          title="Режим"
          style={{
            height: 28,
            padding: '0 10px',
            borderRadius: 7,
            border: 'none',
            background: '#F4ECE1',
            color: '#756B5E',
            fontSize: 12.5,
            fontWeight: 600,
            cursor: 'pointer',
            whiteSpace: 'nowrap',
            flexShrink: 0,
          }}
        >
          {MODE_LABELS[mode]}
        </button>

        {/* Микрофон */}
        {hasSpeech && (
          <button
            onClick={toggleMic}
            title={isListening ? 'Остановить запись' : 'Голосовой ввод'}
            style={{
              width: 32,
              height: 32,
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
        )}

        {/* Отправить / Стоп */}
        {isGenerating ? (
          <button
            onClick={onStop}
            title="Остановить"
            style={{
              width: 34,
              height: 34,
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
              width: 34,
              height: 34,
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
        )}
      </div>
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
