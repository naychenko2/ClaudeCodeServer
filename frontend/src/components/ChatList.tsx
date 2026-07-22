import { useState } from 'react';
import { Plus } from 'lucide-react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { EditSessionDialog } from './EditSessionDialog';
import { C, MODAL_W } from '../lib/design';
import { Modal, ModalActions, Button } from './ui';
import { groupChats } from '../lib/chatGroups';
import { usePersonas, usePersonasVersion } from '../lib/personas';
import { FilterBar } from './FilterBar';
import { useChatFilters, useSanitizePersonaFilter } from '../lib/chatFilters';
import { useLastMechanicVersion } from '../lib/lastMechanic';
import { ChatCard } from './ChatCard';

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
  // Чат с активным workflow — плашка «WF» на его карточке
  workflowRunningFor?: string;
}

export function ChatList({ chats, activeId, onSelect, onNew, creating, onEdited, onDeleted, isMobile = false, workflowRunningFor }: Props) {
  const online = useOnline();
  // Подписка на стор персон — перерисоваться, когда список подгрузится (аватары чатов персон)
  usePersonasVersion();
  // Подписка на стор механик — перерисовать список при запуске новой механики
  useLastMechanicVersion();
  const [editTarget, setEditTarget] = useState<Session | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  // Карточка под курсором — на ней показываем действия (на тач-устройствах hover нет, там действия видны всегда)
  const [hoveredId, setHoveredId] = useState<string | null>(null);

  // === Фильтры списка чатов ===
  // Персистятся в localStorage отдельно от проектных списков (scope 'global')
  const { filters, patch } = useChatFilters('global');
  const visibleOrigins = new Set(filters.origins);

  const personas = usePersonas();

  // Персоны в списке (для селектора фильтра)
  const personaIdsInList = [...new Set(chats.filter(c => c.personaId).map(c => c.personaId!))];
  useSanitizePersonaFilter(filters, patch, personaIdsInList, chats.length > 0);

  // Применение фильтров
  const filteredChats = chats.filter(c => {
    if (!visibleOrigins.has(c.origin)) return false;
    if (filters.activeOnly && Date.now() - new Date(c.updatedAt).getTime() > 5 * 60 * 1000) return false;
    if (filters.personaId && c.personaId !== filters.personaId) return false;
    return true;
  });
  const hiddenCount = chats.length - filteredChats.length;

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

  const groups = groupChats(filteredChats);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Новый чат — пунктирная «создать в сайдбаре» (единый стиль с SessionList/FileExplorer) */}
      <Button
        variant="dashed" size="md" fullWidth loading={creating}
        onClick={onNew} style={{ marginBottom: 8 }}
        leftIcon={
          <Plus size={15} strokeWidth={2.2} />
        }
      >
        Новый чат
      </Button>

      {/* Строка фильтров */}
      <FilterBar
        visibleOrigins={visibleOrigins}
        onChangeVisibleOrigins={v => patch({ origins: [...v] })}
        activeOnly={filters.activeOnly}
        onChangeActiveOnly={v => patch({ activeOnly: v })}
        filterPersonaId={filters.personaId}
        onChangeFilterPersona={id => patch({ personaId: id })}
        personaIdsInList={personaIdsInList}
        allPersonas={personas}
        hiddenCount={hiddenCount}
        isMobile={isMobile}
      />

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', margin: '0 -4px', padding: '0 4px' }}>
        {groups.length === 0 && chats.length === 0 && (
          <div style={{ padding: '24px 8px', textAlign: 'center', color: C.textMuted, fontSize: 13 }}>
            Пока нет чатов. Начните новый.
          </div>
        )}
        {groups.length === 0 && chats.length > 0 && (
          <div style={{ padding: '24px 8px', textAlign: 'center', color: C.textMuted, fontSize: 13 }}>
            Все чаты скрыты фильтрами
          </div>
        )}
        {groups.map(g => (
          <div key={g.title} style={{ marginBottom: 6 }}>
            <div style={{ padding: '9px 4px 6px', fontSize: 11, fontWeight: 700, color: C.textSecondary }}>
              {g.title}
            </div>
            {g.items.map(chat => (
              <ChatCard
                key={chat.id}
                session={chat}
                isActive={chat.id === activeId}
                isMobile={isMobile}
                fallbackName="Новый чат"
                online={online}
                hovered={hoveredId === chat.id}
                workflowRunning={workflowRunningFor === chat.id}
                onSelect={() => onSelect(chat)}
                onHover={h => setHoveredId(h ? chat.id : null)}
                onEdit={() => setEditTarget(chat)}
                onDelete={() => setDeleteTarget(chat)}
                onTogglePin={() => togglePin(chat)}
              />
            ))}
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
