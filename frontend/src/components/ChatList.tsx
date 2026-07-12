import { useState } from 'react';
import { Plus, Pin, SquarePen, Trash2 } from 'lucide-react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { StatusBadge } from './StatusBadge';
import { EditSessionDialog } from './EditSessionDialog';
import { C, R, SHADOW, MODAL_W, FONT } from '../lib/design';
import { Modal, ModalActions, Button, IconButton } from './ui';
import { groupChats } from '../lib/chatGroups';
import { getPersonaById, usePersonasVersion, personaLabel } from '../lib/personas';
import { ExpiryBadge } from './ExpiryBadge';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import { agentDotColor } from './AgentSelector';

// Время создания чата: сегодня — часы:минуты, иначе — дата (группы и так разбиты по дням)
function chatTime(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  if (d.toDateString() === now.toDateString())
    return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
}

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
  // Подписка на стор персон — перерисоваться, когда список подгрузится (аватары чатов персон)
  usePersonasVersion();
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
      {/* Новый чат — пунктирная «создать в сайдбаре» (единый стиль с SessionList/FileExplorer) */}
      <Button
        variant="dashed" size="md" fullWidth loading={creating}
        onClick={onNew} style={{ marginBottom: 12 }}
        leftIcon={
          <Plus size={15} strokeWidth={2.2} />
        }
      >
        Новый чат
      </Button>

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
              // Чат от лица персоны: слева мини-аватар, имя персоны и акцент её цвета
              const persona = chat.personaId ? getPersonaById(chat.personaId) : undefined;
              // Групповой чат: стек мини-аватаров участников вместо одиночного + подпись «Групповой»
              const group = (chat.participants?.length ?? 0) > 1
                ? chat.participants!.map(id => getPersonaById(id)).filter(p => p !== undefined)
                : [];
              const accent = persona ? agentDotColor(persona.avatar?.color) : C.accent;
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
                    border: '1px solid ' + (isActive ? accent : C.borderLight),
                    boxShadow: isActive ? SHADOW.button : SHADOW.card,
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'flex-start',
                    gap: (persona || group.length > 1) ? 9 : 0,
                  }}
                >
                  {/* Акцентная полоса слева — маркер текущего чата (у чатов персоны — её цветом) */}
                  {isActive && (
                    <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: accent }} />
                  )}
                  {group.length > 1 ? (
                    // Вертикальный плотный стек: важно количество участников, а не лица —
                    // карточку не распирает по ширине даже при 4 персонах
                    <div style={{ flexShrink: 0, marginTop: 1, display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
                      {group.map((p, i) => (
                        <div key={p!.id} style={{
                          marginTop: i === 0 ? 0 : -15, position: 'relative', zIndex: group.length - i,
                          borderRadius: '50%', border: `1.5px solid ${C.bgWhite}`,
                        }}>
                          <PersonaAvatar persona={p!} size={22} />
                        </div>
                      ))}
                    </div>
                  ) : persona && (
                    <div style={{ flexShrink: 0, marginTop: 1 }}><PersonaAvatar persona={persona} size={28} /></div>
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
                    {group.length > 1 ? (
                      <div style={{ fontSize: 11.5, fontWeight: 600, color: accent, marginBottom: 3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        Групповой · {group.length} участника
                      </div>
                    ) : persona && (
                      <div style={{ fontSize: 11.5, fontWeight: 600, color: accent, marginBottom: 3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {personaLabel(persona)}
                      </div>
                    )}
                    {chat.lastMessage && (
                      <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {chat.lastMessage}
                      </div>
                    )}
                  </div>
                  {/* Правая колонка: время создания (сверху, выровнено вправо) + действия */}
                  <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 5, flexShrink: 0, paddingLeft: 6 }}>
                    <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, lineHeight: 1, whiteSpace: 'nowrap' }}>
                      {chatTime(chat.createdAt)}
                    </span>
                    <ExpiryBadge session={chat} />
                    {online && (<div style={{ display: 'flex' }}>
                      <IconButton
                        onClick={e => { e.stopPropagation(); togglePin(chat); }}
                        title={chat.isPinned ? 'Открепить' : 'Закрепить'}
                        size="xs" active={chat.isPinned}
                      >
                        <Pin size={14} strokeWidth={2} fill={chat.isPinned ? 'currentColor' : 'none'} />
                      </IconButton>
                      <IconButton onClick={e => { e.stopPropagation(); setEditTarget(chat); }} title="Настройки чата" size="xs">
                        <SquarePen size={14} strokeWidth={2} />
                      </IconButton>
                      <IconButton onClick={e => { e.stopPropagation(); setDeleteTarget(chat); }} title="Удалить чат" size="xs" tone="danger">
                        <Trash2 size={14} strokeWidth={2} />
                      </IconButton>
                    </div>)}
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
