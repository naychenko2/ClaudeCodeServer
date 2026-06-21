import { useEffect, useState, useRef } from 'react';
import type { Project, Session } from '../types';
import { api } from '../lib/api';
import { StatusBadge } from './StatusBadge';

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string, autoSelect?: boolean) => void;
  isMobile?: boolean;
}

export function SessionList({ project, activeSession, onSelect, isMobile = false }: Props) {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  const initializedRef = useRef(false);

  const createNew = async (): Promise<Session> => {
    const s = await api.sessions.create(project.id);
    setSessions(prev => [s, ...prev]);
    onSelect(s);
    return s;
  };

  useEffect(() => {
    initializedRef.current = false;

    const init = async () => {
      const list = await api.sessions.list(project.id);
      setSessions(list);
      if (!initializedRef.current) {
        initializedRef.current = true;
        // авто-выбор только если сессия ещё не выбрана
        if (!activeSession) {
          if (list.length > 0) {
            onSelect(list[0], undefined, true);
          } else {
            const s = await api.sessions.create(project.id);
            setSessions([s]);
            onSelect(s, undefined, true);
          }
        }
      }
    };

    init();
    const interval = setInterval(() => {
      api.sessions.list(project.id).then(setSessions);
    }, 5000);
    return () => clearInterval(interval);
  }, [project.id]);

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await api.sessions.delete(project.id, deleteTarget.id);
    const updated = sessions.filter(s => s.id !== deleteTarget.id);
    setSessions(updated);
    setDeleteTarget(null);
    if (activeSession?.id === deleteTarget.id) {
      if (updated.length > 0) {
        onSelect(updated[0], undefined, true);
      } else {
        const s = await api.sessions.create(project.id);
        setSessions([s]);
        onSelect(s);
      }
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div style={{ padding: '10px 12px', borderBottom: '1px solid #D4CFC4' }}>
        <button
          onClick={createNew}
          style={{
            width: '100%', padding: 11, borderRadius: 12,
            border: '1.5px dashed #D0C6B4', background: 'none', cursor: 'pointer',
            fontSize: 13, color: '#BE5536', fontWeight: 600,
            display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
          }}
        >
          + Новый чат
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 8px' }}>
        {sessions.map((s, index) => (
          <div
            key={s.id}
            onClick={() => onSelect(s)}
            style={{
              padding: isMobile ? '14px 16px' : '11px 12px',
              borderRadius: isMobile ? 16 : 12,
              marginBottom: 5,
              cursor: 'pointer',
              background: '#FFFFFF',
              border: '1px solid ' + (activeSession?.id === s.id ? '#D97757' : '#E8E1D4'),
              boxShadow: '0 2px 8px rgba(60,50,35,0.04)',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'flex-start',
            }}
          >
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
                {s.status === 'active' && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: '#5E8B4E', flexShrink: 0 }} />
                )}
                {s.status === 'waiting' && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: '#C9933A', flexShrink: 0 }} />
                )}
                <span style={{ fontSize: 13.5, fontWeight: 600, color: '#2A251F', flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.name ?? `Чат #${index + 1}`}
                </span>
                {(s.status === 'starting' || s.status === 'finished' || s.status === 'error') && (
                  <StatusBadge status={s.status} />
                )}
              </div>
              {s.lastMessage && (
                <div style={{ fontSize: 12, color: '#9A8F7E', lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.lastMessage}
                </div>
              )}
            </div>
            <button
              onClick={e => { e.stopPropagation(); setDeleteTarget(s); }}
              style={{
                background: 'none', border: 'none', cursor: 'pointer',
                color: '#9A8F7E', padding: '0 0 0 8px', flexShrink: 0,
                width: 24, height: 24, borderRadius: 6,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
              onMouseEnter={e => { e.currentTarget.style.color = '#C0392B'; e.currentTarget.style.background = '#FFF0EE'; }}
              onMouseLeave={e => { e.currentTarget.style.color = '#9A8F7E'; e.currentTarget.style.background = 'none'; }}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <polyline points="3 6 5 6 21 6" />
                <path d="M19 6l-1 14H6L5 6" />
                <path d="M10 11v6M14 11v6" />
                <path d="M9 6V4h6v2" />
              </svg>
            </button>
          </div>
        ))}
      </div>

      {deleteTarget && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div style={{ background: '#F4F0E8', borderRadius: 20, padding: 24, width: 340, boxShadow: '0 24px 60px rgba(23,19,15,0.4)' }}>
            <h3 style={{ fontFamily: "'PT Serif', serif", fontWeight: 500, fontSize: 20, margin: '0 0 8px', letterSpacing: '-0.01em' }}>Удалить чат?</h3>
            <p style={{ fontSize: 13, color: '#756B5E', marginBottom: 20 }}>Это действие нельзя отменить.</p>
            <div style={{ display: 'flex', gap: 10 }}>
              <button onClick={() => setDeleteTarget(null)} style={{ flex: 1, padding: 12, borderRadius: 12, border: 'none', background: '#EDE7DC', color: '#5C5246', cursor: 'pointer', fontWeight: 600, fontSize: 14 }}>Отмена</button>
              <button onClick={handleDelete} style={{ flex: 1, padding: 12, borderRadius: 12, border: 'none', background: '#C0392B', color: '#FFF', cursor: 'pointer', fontWeight: 600, fontSize: 14 }}>Удалить</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
