// Рельса НАБОРА стены — левая капсула (по контенту, не во всю высоту).
// Сверху вниз: [К проектам] [sep] [монеты 1..N] [+ добавить].
//
// Монета — ЦИФРОВАЯ, в геометрии кнопок проектов (квадратик со скруглением):
// номер = ПОЗИЦИЯ в наборе (1..N сверху вниз; после reorder номера остаются по
// порядку — они про места, не про чаты). В покое — нейтральный контур; hover
// красит цифру и рамку в цвет проекта; фокусная — акцентный контур; вне экрана
// (монет больше, чем слотов) — приглушена, клик меняет её местами с последней
// видимой колонкой. Статусная точка: янтарь «идёт ход», красный «ждёт вас».
import { useRef, useState } from 'react';
import { LogOut, Plus, X } from 'lucide-react';
import { C, FONT, R } from '../../lib/design';
import type { Session } from '../../types';
import { RailCapsule, RailIconButton, RailSep } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { agentDotColor } from '../../components/AgentSelector';
import { projectColor } from '../../lib/tasks';
import { useWallState, chatStatus, removeChat, reorderChat, moveToVisible, focusChat } from './wallStore';

// Статусная точка: не accent — оранжевый в системе значит «активное/выбранное»
const DOT_COLOR: Record<string, string> = { working: C.warning, waiting: C.danger };
const DOT_TITLE: Record<string, string> = { working: 'идёт ход', waiting: 'ждёт вас' };

const COIN = 26; // монета внутри бокса кнопки рельсы

export function WallRail({ slots, onOpenPicker, onExit }: {
  // Сколько колонок сейчас на экране (первые slots монет — видимые)
  slots: number;
  onOpenPicker: () => void;
  // Выход из режима — к проектам (спящий воркспейс вернётся сам)
  onExit: () => void;
}) {
  const { chats, projects, focusId } = useWallState();
  // Drag-sort: HTML5 DnD внутри рельсы, состояние локальное (набор маленький)
  const dragFrom = useRef<number | null>(null);
  const [dragOver, setDragOver] = useState<number | null>(null);
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);

  const coin = (s: Session, idx: number) => {
    const project = s.projectId ? projects.get(s.projectId) : undefined;
    const hidden = idx >= slots;
    const focused = s.id === focusId && !hidden;
    const hovered = hoverIdx === idx;
    const status = chatStatus(s);
    const dot = DOT_COLOR[status];
    const chatName = s.name?.trim() || 'Без названия';
    const where = project ? project.name : 'Чат вне проекта';
    const label = `${idx + 1}. ${chatName} — ${where}${dot ? ` · ${DOT_TITLE[status]}` : ''}`;
    // Цвет проекта для hover-окраски цифры; внепроектный чат — нейтральный акцент текста
    const projColor = project
      ? (project.icon?.color ? agentDotColor(project.icon.color) : projectColor(project.id).main)
      : C.textSecondary;
    const border = focused ? C.accent : hovered ? projColor : C.border;
    const text = focused ? C.accent : hovered ? projColor : C.textMuted;

    return (
      <RailIconButton
        key={s.id}
        side="left"
        variant="media"
        label={label}
        active={focused}
        action={{
          Icon: X,
          title: 'Убрать со стены',
          onClick: () => removeChat(s.id),
        }}
        wrapper={{
          draggable: true,
          onDragStart: (e: React.DragEvent) => { dragFrom.current = idx; e.dataTransfer.effectAllowed = 'move'; },
          onDragOver: (e: React.DragEvent) => { e.preventDefault(); setDragOver(idx); },
          onDragLeave: () => setDragOver(cur => (cur === idx ? null : cur)),
          onDrop: (e: React.DragEvent) => {
            e.preventDefault();
            if (dragFrom.current !== null) reorderChat(dragFrom.current, idx);
            dragFrom.current = null;
            setDragOver(null);
          },
          onDragEnd: () => { dragFrom.current = null; setDragOver(null); },
          onMouseEnter: () => setHoverIdx(idx),
          onMouseLeave: () => setHoverIdx(cur => (cur === idx ? null : cur)),
          style: dragOver === idx ? { outline: `2px solid ${C.accent}`, borderRadius: R.md } : undefined,
        }}
        onClick={() => (hidden ? moveToVisible(s.id, slots) : focusChat(s.id))}
      >
        <span style={{ position: 'relative', display: 'flex', opacity: hidden ? 0.45 : 1 }}>
          <span style={{
            width: COIN, height: COIN, borderRadius: Math.round(COIN * 0.22),
            border: `1px solid ${border}`, boxSizing: 'border-box',
            color: text, background: 'transparent',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            fontFamily: FONT.sans, fontWeight: focused || hovered ? 700 : 600, fontSize: 12,
            lineHeight: 1, flexShrink: 0, transition: 'color 0.12s, border-color 0.12s',
          }}>
            {idx + 1}
          </span>
          {dot && (
            <span style={{
              position: 'absolute', top: -2, right: -2, width: 8, height: 8, borderRadius: R.full,
              background: dot, border: `1.5px solid ${C.bgMain}`, pointerEvents: 'none',
            }} />
          )}
        </span>
      </RailIconButton>
    );
  };

  return (
    // Капсула по контенту: рельса не тянется на всю высоту холста
    <RailCapsule side="left" style={{ alignSelf: 'flex-start' }}>
      {/* Выход из режима — ПЕРВЫМ, у верхней кромки */}
      <RailIconButton side="left" label="К проектам" onClick={onExit}>
        <LogOut size={16} strokeWidth={ICON_STROKE} style={{ transform: 'rotate(180deg)' }} />
      </RailIconButton>
      <RailSep />
      {chats.map(coin)}
      {/* Приёмник добавления: пунктирная монета «+» → пикер чатов */}
      <RailIconButton side="left" label="Добавить чат на стену" onClick={onOpenPicker}>
        <span style={{
          width: COIN, height: COIN, borderRadius: Math.round(COIN * 0.22), border: `1px dashed ${C.border}`,
          color: C.textMuted, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
          boxSizing: 'border-box',
        }}>
          <Plus size={14} strokeWidth={ICON_STROKE} />
        </span>
      </RailIconButton>
    </RailCapsule>
  );
}
