import { useEffect, useState, useRef } from 'react';
import type { Project, Session, AgentInfo, Role } from '../types';
import { api } from '../lib/api';
import { onMessage, onReconnected } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { isOnline } from '../lib/offline';
import { StatusBadge } from './StatusBadge';
import { EditSessionDialog } from './EditSessionDialog';
import { RoleAvatar } from './RoleAvatar';
import { C, R, SHADOW, MODAL_W, FONT } from '../lib/design';
import { Modal, ModalActions, Button, IconButton } from './ui';

// Время создания сессии: сегодня — часы:минуты, иначе — дата
function sessTime(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  if (d.toDateString() === now.toDateString())
    return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
}

interface Props {
  project: Project;
  activeSession: Session | null;
  onSelect: (session: Session, firstMessage?: string, autoSelect?: boolean) => void;
  onSessionUpdated?: (session: Session) => void;
  onSessionsChanged?: (count: number) => void;
  isMobile?: boolean;
  workflowRunningFor?: string;
  selectedAgent?: AgentInfo | null;
}

export function SessionList({ project, activeSession, onSelect, onSessionUpdated, onSessionsChanged, isMobile = false, workflowRunningFor, selectedAgent }: Props) {
  const online = useOnline();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  const [editTarget, setEditTarget] = useState<Session | null>(null);
  const initializedRef = useRef(false);

  // Роли — для аватара на карточке чата (мапим session.roleId → роль). Берём весь пул,
  // а не команду проекта: у старых чатов роль могла быть откреплена от проекта.
  useEffect(() => {
    api.team.list().then(setRoles).catch(() => {});
  }, [project.id]);
  const roleMap = new Map(roles.map(r => [r.id, r]));

  useEffect(() => { onSessionsChanged?.(sessions.length); }, [sessions.length, onSessionsChanged]);

  const createNew = async (): Promise<Session> => {
    const s = await api.sessions.create(project.id, 'auto', undefined, undefined, undefined, selectedAgent?.fileName);
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
    // Кнопка удаления скрыта офлайн, но сеть могла упасть между показом и кликом —
    // защищаемся от unhandled rejection и не закрываем диалог при сбое
    try {
      await api.sessions.delete(project.id, deleteTarget.id);
    } catch {
      setDeleteTarget(null);
      return;
    }
    const updated = sessions.filter(s => s.id !== deleteTarget.id);
    setSessions(updated);
    setDeleteTarget(null);
    if (activeSession?.id === deleteTarget.id) {
      if (updated.length > 0) {
        onSelect(updated[0], undefined, true);
      } else {
        try {
          const s = await api.sessions.create(project.id);
          setSessions([s]);
          onSelect(s);
        } catch { /* офлайн/сбой — список пуст, создастся при возврате онлайн */ }
      }
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {online && (
        <div style={{ padding: '10px 12px', borderBottom: `1px solid ${C.divider}` }}>
          <Button variant="dashed" size="md" fullWidth onClick={createNew}
            leftIcon={
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
                <path d="M12 5v14M5 12h14" />
              </svg>
            }
          >
            Новый чат
          </Button>
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
              borderRadius: isMobile ? 16 : R.xl,
              marginBottom: 5,
              cursor: 'pointer',
              overflow: 'hidden',
              background: isActive ? C.accentLight : C.bgWhite,
              border: '1px solid ' + (isActive ? C.accent : C.borderLight),
              boxShadow: isActive
                ? '0 2px 10px rgba(217,119,87,0.18)'
                : SHADOW.card,
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'flex-start',
            }}
          >
            {/* Акцентная полоса слева — явный маркер текущего чата */}
            {isActive && (
              <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: C.accent }} />
            )}
            <div style={{ display: 'flex', alignItems: 'flex-start', gap: 9, flex: 1, minWidth: 0 }}>
              {(() => {
                const role = s.roleId ? roleMap.get(s.roleId) : undefined;
                return role ? <div style={{ marginTop: 1 }}><RoleAvatar name={role.name} avatar={role.avatar} color={role.color} size={26} title={role.title ? `${role.name} · ${role.title}` : role.name} /></div> : null;
              })()}
              <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
                {(s.status === 'active') && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.success, flexShrink: 0 }} />
                )}
                {s.status === 'finished' && (
                  <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.textMuted, flexShrink: 0 }} />
                )}
                <span style={{ fontSize: 13.5, fontWeight: isActive ? 700 : 600, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.name ?? `Чат #${index + 1}`}
                </span>
                {(s.status === 'starting' || s.status === 'working' || s.status === 'waiting' || s.status === 'error' || s.status === 'orphaned') && (
                  <StatusBadge status={s.status} />
                )}
                {workflowRunningFor === s.id && (
                  <div title="Выполняется Workflow" style={{
                    display: 'flex', alignItems: 'center', gap: 3,
                    padding: '1px 5px',
                    background: '#F4ECE1', border: '1px solid #EAD3C5', borderRadius: 4,
                    flexShrink: 0,
                  }}>
                    <div className="tool-spinner" style={{ width: 8, height: 8 }} />
                    <span style={{ fontFamily: 'Hanken Grotesk, sans-serif', fontSize: 10, fontWeight: 600, color: '#D97757', lineHeight: 1 }}>WF</span>
                  </div>
                )}
              </div>
              {s.lastMessage && (
                <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {s.lastMessage}
                </div>
              )}
              </div>
            </div>
            {/* Правая колонка: время создания (сверху, вправо) + действия */}
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 5, flexShrink: 0, paddingLeft: 6 }}>
              <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, lineHeight: 1, whiteSpace: 'nowrap' }}>
                {sessTime(s.createdAt)}
              </span>
              {online && (<div style={{ display: 'flex' }}>
              <IconButton onClick={e => { e.stopPropagation(); setEditTarget(s); }} title="Настройки чата" size="xs">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                  <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                </svg>
              </IconButton>
              <IconButton onClick={e => { e.stopPropagation(); setDeleteTarget(s); }} title="Удалить чат" size="xs" tone="danger">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <polyline points="3 6 5 6 21 6" />
                  <path d="M19 6l-1 14H6L5 6" />
                  <path d="M10 11v6M14 11v6" />
                  <path d="M9 6V4h6v2" />
                </svg>
              </IconButton>
              </div>)}
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
        <Modal
          title="Удалить чат?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Чат «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name ?? 'Новый чат'}</strong>» будет удалён без возможности восстановления.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleDelete}
              onCancel={() => setDeleteTarget(null)}
            />
          }
        />
      )}
    </div>
  );
}
