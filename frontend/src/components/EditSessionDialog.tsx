import { useState, useEffect } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';

interface Props {
  session: Session;
  onSaved: (session: Session) => void;
  onClose: () => void;
}

export function EditSessionDialog({ session, onSaved, onClose }: Props) {
  const [name, setName] = useState(session.name ?? '');
  const [model, setModel] = useState(session.model ?? '');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  const handleSave = async () => {
    setLoading(true);
    setError(null);
    try {
      const updated = await api.sessions.update(session.projectId, session.id, {
        name: name.trim() || null,
        model: model || null,
      });
      onSaved(updated);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
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

  const labelStyle: React.CSSProperties = {
    fontSize: 12, fontWeight: 600, color: '#756B5E',
    textTransform: 'uppercase', letterSpacing: '0.05em',
  };

  return (
    <div
      style={{
        position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)',
        display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000,
      }}
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        style={{
          background: '#F4F0E8', borderRadius: 20, padding: 28, width: 420,
          boxShadow: '0 24px 60px rgba(23,19,15,0.4)',
          display: 'flex', flexDirection: 'column', gap: 18,
        }}
      >
        <h2 style={{ fontFamily: "'PT Serif', Georgia, serif", fontWeight: 500, fontSize: 22, margin: 0, color: '#2A251F', letterSpacing: '-0.01em' }}>
          Настройки чата
        </h2>

        {/* Название */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <label style={labelStyle}>Название</label>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="авто из первого сообщения"
            style={fieldStyle}
            autoFocus
            onKeyDown={(e) => { if (e.key === 'Enter') handleSave(); }}
            onFocus={(e) => { e.currentTarget.style.borderColor = '#D97757'; e.currentTarget.style.boxShadow = '0 0 0 3px rgba(217,119,87,0.14)'; }}
            onBlur={(e) => { e.currentTarget.style.borderColor = '#E0D7C8'; e.currentTarget.style.boxShadow = 'none'; }}
          />
        </div>

        {/* Модель */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <label style={labelStyle}>Модель</label>
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
          <span style={{ fontSize: 11.5, color: '#9A8F7E' }}>
            Применится со следующего сообщения.
          </span>
        </div>

        {error && <p style={{ margin: 0, fontSize: 13, color: '#C0392B' }}>{error}</p>}

        <div style={{ display: 'flex', gap: 10 }}>
          <button
            onClick={onClose}
            style={{ flex: 1, padding: 13, borderRadius: 12, border: 'none', background: '#EDE7DC', color: '#5C5246', cursor: 'pointer', fontWeight: 600, fontSize: 14, fontFamily: 'inherit' }}
          >
            Отмена
          </button>
          <button
            onClick={handleSave}
            disabled={loading}
            style={{
              flex: 1, background: loading ? '#E8A990' : '#D97757', color: '#FBF8F2',
              border: 'none', borderRadius: 12, padding: 13, fontSize: 14, fontWeight: 600,
              fontFamily: 'inherit', cursor: loading ? 'not-allowed' : 'pointer', transition: 'background 0.15s',
            }}
          >
            {loading ? 'Сохраняем…' : 'Сохранить'}
          </button>
        </div>
      </div>
    </div>
  );
}
