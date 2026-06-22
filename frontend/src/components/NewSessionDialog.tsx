import { useState, useRef, useEffect, useCallback } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';

interface NewSessionDialogProps {
  projectId: string;
  onCreated: (session: Session, firstMessage?: string) => void;
  onClose: () => void;
}

type Mode = 'auto' | 'plan' | 'ask';

const MODES: { value: Mode; label: string }[] = [
  { value: 'auto', label: '⚡ Авто' },
  { value: 'plan', label: '📋 План' },
  { value: 'ask', label: '❓ Спросить' },
];

export function NewSessionDialog({ projectId, onCreated, onClose }: NewSessionDialogProps) {
  const [name, setName] = useState('');
  const [firstMessage, setFirstMessage] = useState('');
  const [mode, setMode] = useState<Mode>('auto');
  const [model, setModel] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const adjustTextarea = useCallback(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight}px`;
  }, []);

  useEffect(() => {
    adjustTextarea();
  }, [firstMessage, adjustTextarea]);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  const handleSubmit = async () => {
    setLoading(true);
    setError(null);
    try {
      const sessionName = name.trim() || undefined;
      const session = await api.sessions.create(projectId, mode, undefined, sessionName, model || undefined);
      onCreated(session, firstMessage.trim() || undefined);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка создания чата');
    } finally {
      setLoading(false);
    }
  };

  const fieldStyle: React.CSSProperties = {
    background: '#FFFFFF',
    border: '1px solid #E0D7C8',
    borderRadius: 10,
    padding: '10px 13px',
    fontSize: 14,
    color: '#2A251F',
    outline: 'none',
    fontFamily: 'inherit',
    width: '100%',
    boxSizing: 'border-box',
  };

  return (
    <div
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(23,19,15,0.42)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 1000,
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        style={{
          background: '#F4F0E8',
          borderRadius: 20,
          padding: 28,
          width: 420,
          boxShadow: '0 24px 60px rgba(23,19,15,0.4)',
          display: 'flex',
          flexDirection: 'column',
          gap: 18,
        }}
      >
        {/* Заголовок */}
        <h2
          style={{
            fontFamily: "'PT Serif', Georgia, serif",
            fontWeight: 500,
            fontSize: 22,
            margin: 0,
            color: '#2A251F',
            letterSpacing: '-0.01em',
          }}
        >
          Новый чат
        </h2>

        {/* Поле «Название» */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: '#756B5E', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            Название
          </label>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="авто из первого сообщения"
            style={fieldStyle}
            onFocus={(e) => { e.currentTarget.style.borderColor = '#D97757'; e.currentTarget.style.boxShadow = '0 0 0 3px rgba(217,119,87,0.14)'; }}
            onBlur={(e) => { e.currentTarget.style.borderColor = '#E0D7C8'; e.currentTarget.style.boxShadow = 'none'; }}
          />
        </div>

        {/* Поле «Первое сообщение» */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: '#756B5E', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            Первое сообщение
          </label>
          <textarea
            ref={textareaRef}
            value={firstMessage}
            onChange={(e) => setFirstMessage(e.target.value)}
            placeholder="Опишите задачу…"
            style={{ ...fieldStyle, minHeight: 80, resize: 'none', overflow: 'hidden', lineHeight: '1.5' }}
            onFocus={(e) => { e.currentTarget.style.borderColor = '#D97757'; e.currentTarget.style.boxShadow = '0 0 0 3px rgba(217,119,87,0.14)'; }}
            onBlur={(e) => { e.currentTarget.style.borderColor = '#E0D7C8'; e.currentTarget.style.boxShadow = 'none'; }}
          />
        </div>

        {/* Выбор режима */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: '#756B5E', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            Режим
          </label>
          <div style={{ display: 'flex', gap: 8 }}>
            {MODES.map(({ value, label }) => (
              <button
                key={value}
                onClick={() => setMode(value)}
                style={{
                  flex: 1, padding: '9px 4px', borderRadius: 10, border: 'none', cursor: 'pointer',
                  fontSize: 13, fontWeight: 600, fontFamily: 'inherit',
                  background: mode === value ? '#D97757' : '#EDE7DC',
                  color: mode === value ? '#FBF8F2' : '#756B5E',
                  transition: 'background 0.15s, color 0.15s',
                }}
              >
                {label}
              </button>
            ))}
          </div>
        </div>

        {/* Выбор модели */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: '#756B5E', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            Модель
          </label>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
            {MODELS.map(({ value, label }) => (
              <button
                key={value}
                onClick={() => setModel(value)}
                style={{
                  flex: '1 1 calc(50% - 4px)', padding: '9px 4px', borderRadius: 10, border: 'none', cursor: 'pointer',
                  fontSize: 13, fontWeight: 600, fontFamily: 'inherit',
                  background: model === value ? '#D97757' : '#EDE7DC',
                  color: model === value ? '#FBF8F2' : '#756B5E',
                  transition: 'background 0.15s, color 0.15s',
                }}
              >
                {label}
              </button>
            ))}
          </div>
        </div>

        {/* Ошибка */}
        {error && (
          <p style={{ margin: 0, fontSize: 13, color: '#C0392B' }}>{error}</p>
        )}

        {/* Кнопка «Создать и начать» */}
        <button
          onClick={handleSubmit}
          disabled={loading}
          style={{
            width: '100%',
            background: loading ? '#E8A990' : '#D97757',
            color: '#FBF8F2',
            border: 'none',
            borderRadius: 12,
            padding: 14,
            fontSize: 15,
            fontWeight: 600,
            fontFamily: 'inherit',
            cursor: loading ? 'not-allowed' : 'pointer',
            transition: 'background 0.15s',
          }}
        >
          {loading ? 'Создаём…' : 'Создать и начать'}
        </button>
      </div>
    </div>
  );
}
