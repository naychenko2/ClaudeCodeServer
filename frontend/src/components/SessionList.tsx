import { useEffect, useState } from 'react';
import type { Project, Session } from '../types';
import { api } from '../lib/api';
import { StatusBadge } from './StatusBadge';
import { NewSessionDialog } from './NewSessionDialog';
import { EmptyState } from './EmptyState';

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string) => void;
  isMobile?: boolean;
}

const ChatIcon = () => (
  <svg width="40" height="40" viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg">
    <rect x="4" y="6" width="32" height="22" rx="4" stroke="#B0A898" strokeWidth="2" fill="none" />
    <text x="20" y="21" textAnchor="middle" fontSize="9" fill="#B0A898" fontFamily="sans-serif">чат</text>
    <path d="M12 28 L10 34 L18 30" fill="none" stroke="#B0A898" strokeWidth="2" strokeLinejoin="round" />
  </svg>
);

export function SessionList({ project, activeSession, onSelect, isMobile = false }: Props) {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  const [showNewDialog, setShowNewDialog] = useState(false);

  const load = () => api.sessions.list(project.id).then(setSessions);

  useEffect(() => {
    load();
    const interval = setInterval(load, 5000);
    return () => clearInterval(interval);
  }, [project.id]);

  const handleCreated = (s: Session, firstMessage?: string) => {
    setSessions(prev => [s, ...prev]);
    onSelect(s, firstMessage);
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    await api.sessions.delete(project.id, deleteTarget.id);
    setSessions(prev => prev.filter(s => s.id !== deleteTarget.id));
    setDeleteTarget(null);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div style={{ padding: '10px 12px', borderBottom: '1px solid #D4CFC4' }}>
        <button
          onClick={() => setShowNewDialog(true)}
          style={{
            width: '100%', padding: 11, borderRadius: 12,
            border: '1.5px dashed #D0C6B4', background: 'none', cursor: 'pointer',
            fontSize: 13, color: '#BE5536', fontWeight: 600,
            display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
            marginTop: 0,
          }}
        >
          + Новая сессия
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 8px' }}>
        {sessions.length === 0 ? (
          <EmptyState
            icon={<ChatIcon />}
            title="Ещё нет сессий"
            subtitle="Начните новую сессию"
            action={
              <button
                onClick={() => setShowNewDialog(true)}
                style={{ padding: '8px 16px', borderRadius: 8, border: 'none', background: '#D97757', color: '#FFF', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}
              >
                + Новая сессия
              </button>
            }
          />
        ) : (
          sessions.map((s, index) => (
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
                  {/* Активная точка статуса — только для active */}
                  {s.status === 'active' && (
                    <div style={{
                      width: 7, height: 7, borderRadius: '50%',
                      background: '#5E8B4E', flexShrink: 0,
                    }} />
                  )}
                  {/* Жёлтая точка для waiting */}
                  {s.status === 'waiting' && (
                    <div style={{
                      width: 7, height: 7, borderRadius: '50%',
                      background: '#C9933A', flexShrink: 0,
                    }} />
                  )}
                  <span style={{ fontSize: 13.5, fontWeight: 600, color: '#2A251F', flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {s.name ?? `Сессия #${index + 1}`}
                  </span>
                  {/* Статусный badge только для не-активных состояний */}
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
          ))
        )}
      </div>

      {showNewDialog && (
        <NewSessionDialog
          projectId={project.id}
          onCreated={handleCreated}
          onClose={() => setShowNewDialog(false)}
        />
      )}

      {deleteTarget && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div style={{ background: '#F4F0E8', borderRadius: 20, padding: 24, width: 340, boxShadow: '0 24px 60px rgba(23,19,15,0.4)' }}>
            <h3 style={{ fontFamily: "'PT Serif', serif", fontWeight: 500, fontSize: 20, margin: '0 0 8px', letterSpacing: '-0.01em' }}>Удалить сессию?</h3>
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
