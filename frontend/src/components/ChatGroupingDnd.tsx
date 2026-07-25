// === Ручная группировка чатов перетаскиванием ===
// Обёртка над списком в режиме «Иерархия»: чат тащится на другой чат и становится
// его дочерним. Общая для ChatList (глобальные чаты) и SessionList (сессии проекта) —
// логика одна, отличается только источник списка.
//
// Во «Плоском» режиме не подключается: там вместо дерева группы по датам (chatGroups),
// и результат перетаскивания было бы не видно.
import { createContext, useContext, useMemo, useState } from 'react';
import {
  DndContext, DragOverlay, MouseSensor, TouchSensor, pointerWithin,
  useDroppable, useSensor, useSensors,
} from '@dnd-kit/core';
import type { DragEndEvent, DragStartEvent } from '@dnd-kit/core';
import { CornerLeftUp } from 'lucide-react';
import { C, FS, FONT, R, SP } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { collectDescendants } from '../lib/chatTree';
import { DRAG_MOUSE_ACTIVATION, DRAG_TOUCH_ACTIVATION } from '../lib/dnd';
import { api } from '../lib/api';
import type { Session } from '../types';

// id drop-зоны «вынести в корень» — не пересекается с id чатов (те GUID-подобные)
export const ROOT_DROP_ID = '__chat-root__';

interface DragState {
  draggingId: string | null;
  // Допустимая цель: любой чат, кроме потомков перетаскиваемого. Сам перетаскиваемый
  // чат допустим — drop на себя означает «вынести из группы».
  isValidTarget: (id: string) => boolean;
}

const ChatDragContext = createContext<DragState>({ draggingId: null, isValidTarget: () => false });

// Состояние перетаскивания для строки списка (ChatTreeRow). Вне провайдера —
// нейтральный дефолт, поэтому строку можно рендерить и без DnD.
export function useChatDrag() {
  return useContext(ChatDragContext);
}

interface Props {
  chats: Session[];
  isMobile: boolean;
  // Применить обновлённый чат к списку (тот же колбэк, что у переименования/пина)
  onEdited: (chat: Session) => void;
  children: React.ReactNode;
}

export function ChatGroupingDnd({ chats, isMobile, onEdited, children }: Props) {
  const [draggingId, setDraggingId] = useState<string | null>(null);

  const sensors = useSensors(
    useSensor(MouseSensor, { activationConstraint: DRAG_MOUSE_ACTIVATION }),
    useSensor(TouchSensor, { activationConstraint: DRAG_TOUCH_ACTIVATION }),
  );

  // Потомки перетаскиваемого чата — запретные цели (вложение в них замкнуло бы кольцо)
  const forbidden = useMemo(
    () => (draggingId ? collectDescendants(chats, draggingId) : new Set<string>()),
    [chats, draggingId],
  );

  const ctx = useMemo<DragState>(() => ({
    draggingId,
    isValidTarget: (id: string) => !forbidden.has(id),
  }), [draggingId, forbidden]);

  const handleDragStart = (e: DragStartEvent) => setDraggingId(String(e.active.id));

  const handleDragEnd = async (e: DragEndEvent) => {
    const chatId = String(e.active.id);
    setDraggingId(null);
    if (!e.over) return;

    const overId = String(e.over.id);
    const chat = chats.find(c => c.id === chatId);
    if (!chat) return;

    // Drop на корневую зону или на самого себя — «вынести из группы»
    const nextParent = overId === ROOT_DROP_ID || overId === chatId ? null : overId;
    if (nextParent !== null && forbidden.has(nextParent)) return;
    if ((chat.parentSessionId ?? null) === nextParent) return;

    // Оптимистично: дерево перестраивается сразу, ответ сервера лишь подтверждает
    onEdited({ ...chat, parentSessionId: nextParent });
    try {
      onEdited(await api.chats.setParent(chatId, nextParent));
    } catch {
      onEdited(chat);
    }
  };

  return (
    <DndContext
      sensors={sensors}
      collisionDetection={pointerWithin}
      onDragStart={handleDragStart}
      onDragEnd={handleDragEnd}
      onDragCancel={() => setDraggingId(null)}
    >
      <ChatDragContext.Provider value={ctx}>
        {children}
        {draggingId && <RootDropZone isMobile={isMobile} />}
        {/* Overlay пустой: карточка едет за курсором сама (transform в ChatTreeRow).
            DragOverlay нужен только чтобы dnd-kit не сбрасывал курсор перетаскивания. */}
        <DragOverlay dropAnimation={null} />
      </ChatDragContext.Provider>
    </DndContext>
  );
}

// Зона «вынести в корень» — появляется на время перетаскивания и липнет к низу
// прокрутки: в длинном списке до неё иначе пришлось бы доскроллить пальцем,
// уже удерживая карточку. Фон непрозрачный — под зоной проезжают строки.
function RootDropZone({ isMobile }: { isMobile: boolean }) {
  const { setNodeRef, isOver } = useDroppable({ id: ROOT_DROP_ID });

  return (
    <div
      ref={setNodeRef}
      style={{
        position: 'sticky', bottom: 0, zIndex: 2,
        marginTop: SP.sm,
        padding: isMobile ? `${SP.md}px ${SP.sm}px` : `${SP.sm}px`,
        // Палец должен попадать без прицеливания — не полагаемся на line-height
        minHeight: isMobile ? 44 : 0,
        boxSizing: 'border-box',
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: SP.sm,
        border: `1px dashed ${isOver ? C.accent : C.dashed}`,
        borderRadius: R.xl,
        background: isOver ? C.accentLight : C.bgPanel,
        color: isOver ? C.accent : C.textMuted,
        fontFamily: FONT.sans, fontSize: FS.sm,
        transition: 'background 0.15s, border-color 0.15s, color 0.15s',
      }}
    >
      <CornerLeftUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      Вынести из группы
    </div>
  );
}
