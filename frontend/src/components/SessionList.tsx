import { useEffect, useState, useRef } from 'react';
import type { Project, Session } from '../types';
import { api } from '../lib/api';
import { onMessage, onReconnected } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { isOnline } from '../lib/offline';
import { StatusBadge } from './StatusBadge';
import { EditSessionDialog } from './EditSessionDialog';
import { modelLabel } from '../lib/models';

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string, autoSelect?: boolean) => void;
  onSessionUpdated?: (session: Session) => void;
  isMobile?: boolean;
}

export function SessionList({ project, activeSession, onSelect, onSessionUpdated, isMobile = false }: Props) {
  const online = useOnline();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  const [editTarget, setEditTarget] = useState<Session | null>(null);
  const initializedRef = useRef(false);

  const createNew = async (): Promise<Session> => {
    const s = await api.sessions.create(project.id);
    setSessions(prev => [s, ...prev]);
    onSelect(s);
    return s;
  };

  // Загрузка и поллинг сессий
  useEffect(() => {
    initializedRef.current = false;

    const init = async () => {
      // Офлайн без кэша — список недоступен, выходим без выбора
      const list = await api.sessions.list(project.id).catch(() => null);
      if (!list) return;
      setSessions(list);
      if (!initializedRef.current) {
        initializedRef.current = true;
        if (!activeSession) {
          if (list.length > 0) {
            onSelect(list[0], undefined, true);
          } else if (isOnline()) {
            // Офлайн чат не создаём — мутации недоступны
            const s = await api.sessions.create(project.id);
            setSessions([s]);
            onSelect(s, undefined, true);
          }
        }
      }
    };

    init();
    const interval = setInterval(() => {
      api.sessions.list(project.id).then(setSessions).catch(() => {});
    }, 5000);
    return () => clearInterval(interval);
  }, [project.id]);

  // Подписка на статусы в реальном времени. Членство в project-группе держит WorkspacePage.
  useEffect(() => {
    let mounted = true;

    // Переподключение — рефетчим статусы (могли пропустить status_changed)
    onReconnected(() => {
      if (!mounted) return;
      api.sessions.list(project.id).then(list => {
        if (mounted) setSessions(list);
      }).catch(() => {});
    });

    const unsub = onMessage(msg => {
      if (!mounted) return;
      if (msg.type !== 'status_changed') return;
      setSessions(prev => prev.map(s =>
        s.id === msg.sessionId
          ? {
              ...s,
              status: msg.status as Session['status'],
              ...(msg.lastMessage !== undefined && { lastMessage: msg.lastMessage }),
              ...(msg.messageCount !== undefined && msg.messageCount > 0 && { messageCount: msg.messageCount }),
            }
          : s
      ));
    });

    return () => {
      mounted = false;
      unsub();
    };
  }, [project.id]);

  // Если активную сессию отредактировали из шапки чата — подхватываем название/модель,
  // не затирая статус, который приходит по realtime
  useEffect(() => {
    if (!activeSession) return;
    setSessions(prev => prev.map(s =>
      s.id === activeSession.id ? { ...s, name: activeSession.name, model: activeSession.model } : s
    ));
  }, [activeSession?.id, activeSession?.name, activeSession?.model]);

  const handleSessionUpdated = (updated: Session) => {
    setSessions(prev => prev.map(s => s.id === updated.id ? { ...s, ...updated } : s));
    if (activeSession?.id === updated.id) onSessionUpdated?.(updated);
  };

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
      {online && (
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
      )}

      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 8px' }}>
        {sessions.map((s, index) => {
          const isActive = activeSession?.id === s.id;
          return (
          <div
            key={s.id}
            onClick={() => onSelect(s)}
            style={{
              position: 'relative',
              // отдельные longhand-свойства: со shorthand + undefined React обнуляет padding-left
              paddingTop: isMobile ? 14 : 11,
              paddingBottom: isMobile ? 14 : 11,
              paddingRight: isMobile ? 16 : 12,
              // у активной карточки добавляем слева место под акцентную полосу
              paddingLeft: (isMobile ? 16 : 12) + (isActive ? 6 : 0),
              borderRadius: isMobile ? 16 : 12,
              marginBottom: 5,
              cursor: 'pointer',
              overflow: 'hidden',
              background: isActive ? '#FBF1EA' : '#FFFFFF',
              border: '1px solid ' + (isActive ? '#D97757' : '#E8E1D4'),
              boxShadow: isActive
                ? '0 2px 10px rgba(217,119,87,0.18)'
                : '0 2px 8px rgba(60,50,35,0.04)',
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'flex-start',
            }}
          >
            {/* Акцентная полоса слева — явный маркер текущего чата */}
            {isActive && (
              <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: '#D97757' }} />
            )}
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
                {s.status === 'active' && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: '#5E8B4E', flexShrink: 0 }} />
                )}
                {s.status === 'waiting' && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: '#C9933A', flexShrink: 0 }} />
                )}
                <span style={{ fontSize: 13.5, fontWeight: isActive ? 700 : 600, color: '#2A251F', flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.name ?? `Чат #${index + 1}`}
                </span>
                {(s.status === 'starting' || s.status === 'working' || s.status === 'finished' || s.status === 'error') && (
                  <StatusBadge status={s.status} />
                )}
              </div>
              {s.lastMessage && (
                <div style={{ fontSize: 12, color: '#9A8F7E', lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.lastMessage}
                </div>
              )}
              {/* Бейдж используемой модели — тех-инфо моноширинным шрифтом */}
              <div style={{ marginTop: 5, display: 'flex' }}>
                <span style={{
                  fontFamily: "'JetBrains Mono', monospace",
                  fontSize: 10.5,
                  fontWeight: 500,
                  letterSpacing: '-0.01em',
                  color: isActive ? '#BE5536' : '#9A8F7E',
                  background: isActive ? '#F4DECF' : '#F0EAE0',
                  padding: '1px 6px',
                  borderRadius: 5,
                  maxWidth: '100%',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}>
                  {modelLabel(s.model)}
                </span>
              </div>
            </div>
            <div style={{ display: 'flex', flexShrink: 0, paddingLeft: 6 }}>
              {online && (<>
              <button
                onClick={e => { e.stopPropagation(); setEditTarget(s); }}
                title="Настройки чата"
                style={{
                  background: 'none', border: 'none', cursor: 'pointer',
                  color: '#9A8F7E', padding: 0, flexShrink: 0,
                  width: 24, height: 24, borderRadius: 6,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                }}
                onMouseEnter={e => { e.currentTarget.style.color = '#5A5040'; e.currentTarget.style.background = '#F0EAE0'; }}
                onMouseLeave={e => { e.currentTarget.style.color = '#9A8F7E'; e.currentTarget.style.background = 'none'; }}
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                  <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                </svg>
              </button>
              <button
                onClick={e => { e.stopPropagation(); setDeleteTarget(s); }}
                title="Удалить чат"
                style={{
                  background: 'none', border: 'none', cursor: 'pointer',
                  color: '#9A8F7E', padding: 0, flexShrink: 0,
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
              </>)}
            </div>
          </div>
          );
        })}
      </div>

      {editTarget && (
        <EditSessionDialog
          session={editTarget}
          onSaved={handleSessionUpdated}
          onClose={() => setEditTarget(null)}
        />
      )}

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
