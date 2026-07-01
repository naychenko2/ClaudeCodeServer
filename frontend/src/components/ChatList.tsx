import { useState } from 'react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { StatusBadge } from './StatusBadge';
import { EditSessionDialog } from './EditSessionDialog';
import { C, R, SHADOW, MODAL_W } from '../lib/design';
import { Modal, ModalActions } from './ui';
import { groupChats } from '../lib/chatGroups';

interface Props {
  chats: Session[];
  activeId: string | null;
  onSelect: (chat: Session) => void;
  onNew: () => void;
  creating?: boolean;
  // Чат отредактирован/закреплён — обновить в списке
  onEdited: (updated: Session) => void;
  // Чат удалён — убрать из списка
  onDeleted: (id: string) => void;
  isMobile?: boolean;
}

export function ChatList({ chats, activeId, onSelect, onNew, creating, onEdited, onDeleted, isMobile = false }: Props) {
  const online = useOnline();
  const [editTarget, setEditTarget] = useState<Session | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);

  const togglePin = async (chat: Session) => {
    try {
      const updated = await api.chats.update(chat.id, { pinned: !chat.isPinned });
      onEdited(updated);
    } catch { /* сеть упала — не блокируем */ }
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    try {
      await api.chats.delete(deleteTarget.id);
    } catch {
      setDeleteTarget(null);
      return;
    }
    onDeleted(deleteTarget.id);
    setDeleteTarget(null);
  };

  const groups = groupChats(chats);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Новый чат */}
      <button
        onClick={onNew}
        disabled={creating}
        style={{
          display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
          height: 42, borderRadius: 11, border: 'none',
          background: C.accent, color: C.onAccent,
          fontSize: 14, fontWeight: 600,
          boxShadow: '0 4px 14px rgba(217,119,87,0.30)',
          marginBottom: 12, cursor: creating ? 'default' : 'pointer', opacity: creating ? 0.7 : 1,
        }}
      >
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
          <path d="M12 5v14M5 12h14" />
        </svg>
        Новый чат
      </button>

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', margin: '0 -4px', padding: '0 4px' }}>
        {groups.length === 0 && (
          <div style={{ padding: '24px 8px', textAlign: 'center', color: C.textMuted, fontSize: 13 }}>
            Пока нет чатов. Начните новый.
          </div>
        )}
        {groups.map(g => (
          <div key={g.title} style={{ marginBottom: 6 }}>
            <div style={{ padding: '9px 4px 6px', fontSize: 11, fontWeight: 700, color: C.textSecondary }}>
              {g.title}
            </div>
            {g.items.map(chat => {
              const isActive = chat.id === activeId;
              return (
                <div
                  key={chat.id}
                  onClick={() => onSelect(chat)}
                  style={{
                    position: 'relative',
                    paddingTop: isMobile ? 14 : 11,
                    paddingBottom: isMobile ? 14 : 11,
                    paddingRight: isMobile ? 16 : 12,
                    paddingLeft: (isMobile ? 16 : 12) + (isActive ? 6 : 0),
                    borderRadius: isMobile ? 16 : R.xl,
                    marginBottom: 5,
                    cursor: 'pointer',
                    overflow: 'hidden',
                    background: isActive ? C.accentLight : C.bgWhite,
                    border: '1px solid ' + (isActive ? C.accent : C.borderLight),
                    boxShadow: isActive ? '0 2px 10px rgba(217,119,87,0.18)' : SHADOW.card,
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'flex-start',
                  }}
                >
                  {/* Акцентная полоса слева — маркер текущего чата */}
                  {isActive && (
                    <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: C.accent }} />
                  )}
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
                      {chat.status === 'active' && (
                        <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.success, flexShrink: 0 }} />
                      )}
                      {chat.status === 'finished' && (
                        <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.textMuted, flexShrink: 0 }} />
                      )}
                      <span style={{ fontSize: 13.5, fontWeight: isActive ? 700 : 600, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {chat.name ?? 'Новый чат'}
                      </span>
                      {(chat.status === 'starting' || chat.status === 'working' || chat.status === 'waiting' || chat.status === 'error' || chat.status === 'orphaned') && (
                        <StatusBadge status={chat.status} />
                      )}
                    </div>
                    {chat.lastMessage && (
                      <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {chat.lastMessage}
                      </div>
                    )}
                  </div>
                  <div style={{ display: 'flex', flexShrink: 0, paddingLeft: 6 }}>
                    {online && (<>
                      <button
                        onClick={e => { e.stopPropagation(); togglePin(chat); }}
                        title={chat.isPinned ? 'Открепить' : 'Закрепить'}
                        style={{
                          background: 'none', border: 'none', cursor: 'pointer',
                          color: chat.isPinned ? C.accent : C.textMuted, padding: 0, flexShrink: 0,
                          width: 24, height: 24, borderRadius: R.sm,
                          display: 'flex', alignItems: 'center', justifyContent: 'center',
                        }}
                        onMouseEnter={e => { e.currentTarget.style.background = C.bgPanel; }}
                        onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
                      >
                        <svg width="14" height="14" viewBox="0 0 24 24" fill={chat.isPinned ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                          <path d="M9 4h6l-1 7 4 3v2H6v-2l4-3z" /><line x1="12" y1="16" x2="12" y2="22" />
                        </svg>
                      </button>
                      <button
                        onClick={e => { e.stopPropagation(); setEditTarget(chat); }}
                        title="Настройки чата"
                        style={{
                          background: 'none', border: 'none', cursor: 'pointer',
                          color: C.textMuted, padding: 0, flexShrink: 0,
                          width: 24, height: 24, borderRadius: R.sm,
                          display: 'flex', alignItems: 'center', justifyContent: 'center',
                        }}
                        onMouseEnter={e => { e.currentTarget.style.color = C.textPrimary; e.currentTarget.style.background = C.bgPanel; }}
                        onMouseLeave={e => { e.currentTarget.style.color = C.textMuted; e.currentTarget.style.background = 'none'; }}
                      >
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                          <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                          <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                        </svg>
                      </button>
                      <button
                        onClick={e => { e.stopPropagation(); setDeleteTarget(chat); }}
                        title="Удалить чат"
                        style={{
                          background: 'none', border: 'none', cursor: 'pointer',
                          color: C.textMuted, padding: 0, flexShrink: 0,
                          width: 24, height: 24, borderRadius: R.sm,
                          display: 'flex', alignItems: 'center', justifyContent: 'center',
                        }}
                        onMouseEnter={e => { e.currentTarget.style.color = C.danger; e.currentTarget.style.background = C.dangerBg; }}
                        onMouseLeave={e => { e.currentTarget.style.color = C.textMuted; e.currentTarget.style.background = 'none'; }}
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
        ))}
      </div>

      {editTarget && (
        <EditSessionDialog
          session={editTarget}
          onSaved={updated => { onEdited(updated); }}
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
