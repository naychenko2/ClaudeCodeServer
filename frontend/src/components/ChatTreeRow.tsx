import { useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { useDraggable, useDroppable } from '@dnd-kit/core';
import { C, R, FONT, FS } from '../lib/design';
import { formatGroupCount, type ChatTreeRowData } from '../lib/chatTree';
import { useChatDrag } from './useChatDrag';

// === Строка дерева чатов: отступ + connector-линии + chevron вокруг ChatCard ===
// Линии и контрол рисуются здесь, а НЕ в карточке: у ChatCard overflow:hidden,
// он обрезал бы вертикали. Сама карточка передаётся как children без изменений.
// Геометрия — docs/design/mockups/chat-list-tree-spec.md: одна формула оси spineX на всех
// уровнях, глубина отступа клампится на 6.
//
// Строка одновременно источник и цель перетаскивания (ручная группировка —
// ChatGroupingDnd). Вне DnD-провайдера useChatDrag отдаёт нейтральное состояние,
// поэтому строка рендерится и без него (flat-режим).

const STEP = 14;
const MAX_DEPTH = 6;
// Зазор между карточками — marginBottom у ChatCard. Держим синхронно: по нему
// обрезается рамка подсветки drop-цели, иначе она вылезает на соседнюю строку.
const CARD_GAP = 5;

interface Props {
  row: ChatTreeRowData;
  isMobile: boolean;
  onToggleCollapse: (id: string) => void;
  children: React.ReactNode;
}

export function ChatTreeRow({ row, isMobile, onToggleCollapse, children }: Props) {
  const [chevronHover, setChevronHover] = useState(false);

  const { draggingId, isValidTarget } = useChatDrag();
  const chatId = row.chat.id;
  // attributes от dnd-kit намеренно не применяем: они вешают на обёртку role="button"
  // и второй таб-стоп поверх кнопок карточки. Клавиатурный сенсор не подключён.
  const { setNodeRef: setDragRef, listeners, transform, isDragging } = useDraggable({ id: chatId });
  // Потомок перетаскиваемого чата целью быть не может — вложение замкнуло бы кольцо
  const { setNodeRef: setDropRef, isOver } = useDroppable({
    id: chatId,
    disabled: draggingId !== null && !isValidTarget(chatId),
  });
  const setRefs = (node: HTMLElement | null) => { setDragRef(node); setDropRef(node); };
  // Наведение на самого себя означает «вынести из группы» — рамкой «вложить сюда» не мигаем
  const highlight = isOver && !isDragging;

  const GUTTER = isMobile ? 28 : 20;
  const elbowY = isMobile ? 23 : 20;
  const offset = (d: number) => Math.min(d, MAX_DEPTH) * STEP;
  const spineX = (d: number) => offset(d) + GUTTER / 2;
  const cardLeftX = (d: number) => offset(d) + GUTTER;

  const { depth, isLast, hasChildren, collapsed, groupCount, groupRunningCount } = row;
  const collapsedHint = groupRunningCount > 0
    ? `Развернуть: вложенных чатов ${groupCount}, из них в работе ${groupRunningCount}`
    : `Развернуть: вложенных чатов ${groupCount}`;
  const lineColor = (accent: boolean) => (accent ? C.accent : C.divider);

  return (
    <div
      ref={setRefs}
      {...listeners}
      style={{
        position: 'relative',
        // flex-колонка, чтобы marginBottom карточки не схлопывался наружу —
        // иначе вертикали рвались бы в зазорах между строками
        display: 'flex',
        flexDirection: 'column',
        paddingLeft: cardLeftX(depth),
        // Карточка едет за курсором сама, без DragOverlay: строка уже позиционирована
        // относительно списка, дублировать её в портале незачем
        transform: transform ? `translate3d(${transform.x}px, ${transform.y}px, 0)` : undefined,
        opacity: isDragging ? 0.5 : 1,
        zIndex: isDragging ? 1 : undefined,
        // Палец на строке = long-press-перетаскивание (TouchSensor), но вертикальный
        // скролл списка должен оставаться за браузером
        touchAction: 'pan-y',
      }}
    >
      {/* Подсветка цели: рамка ровно по карточке, а не по всей строке с gutter.
          Радиус и нижний отступ повторяют ChatCard (радиус на мобиле шире, зазор
          между карточками — её marginBottom), иначе виден двойной контур. */}
      {highlight && (
        <div aria-hidden style={{
          position: 'absolute', left: cardLeftX(depth), right: 0, top: 0, bottom: CARD_GAP,
          border: `2px solid ${C.accent}`, borderRadius: isMobile ? 16 : R.xl,
          pointerEvents: 'none', zIndex: 3,
        }} />
      )}

      {/* Сквозные вертикали предковых уровней (у предка есть следующие сиблинги) */}
      {row.ancestors.map((a, lvl) => a.show && (
        <div key={lvl} aria-hidden style={{
          position: 'absolute', left: spineX(lvl), top: 0, bottom: 0,
          width: 1, background: lineColor(a.accent),
        }} />
      ))}

      {/* Вертикаль-связь к родителю; у последнего ребёнка — только до elbow */}
      {depth >= 1 && (
        <div aria-hidden style={{
          position: 'absolute', left: spineX(depth - 1), top: 0,
          ...(isLast ? { height: elbowY } : { bottom: 0 }),
          width: 1, background: lineColor(row.segAccent),
        }} />
      )}

      {/* Горизонталь-ввод в карточку по центру её первой строки */}
      {depth >= 1 && (
        <div aria-hidden style={{
          position: 'absolute', left: spineX(depth - 1), top: elbowY,
          width: cardLeftX(depth) - spineX(depth - 1), height: 1,
          background: lineColor(row.elbowAccent),
        }} />
      )}

      {/* Вертикаль под своим chevron — вниз к развёрнутым детям */}
      {hasChildren && !collapsed && (
        <div aria-hidden style={{
          position: 'absolute', left: spineX(depth), top: elbowY + 7, bottom: 0,
          width: 1, background: lineColor(row.stubAccent),
        }} />
      )}

      {children}

      {/* Контрол сворачивания — снаружи карточки, строго в gutter-колонке:
          hit-зона по ширине не выходит за cardLeftX, иначе перекрывала бы левый
          край карточки и клик по нему сворачивал бы ветку вместо открытия чата.
          Иконка flex-центром сидит на оси spineX. */}
      {hasChildren && (
        <button
          onClick={e => { e.stopPropagation(); onToggleCollapse(row.chat.id); }}
          // Сворачивание — не перетаскивание: гасим нажатие до сенсоров dnd-kit,
          // иначе тяга за chevron уносила бы всю ветку
          onMouseDown={e => e.stopPropagation()}
          onTouchStart={e => e.stopPropagation()}
          onMouseEnter={() => setChevronHover(true)}
          onMouseLeave={() => setChevronHover(false)}
          // Счётчики озвучиваются здесь, а не на бейдже: бейдж лежит поверх кнопки
          // с pointerEvents:none, его собственный title мышью не поймать
          title={collapsed ? collapsedHint : 'Свернуть вложенные чаты'}
          aria-label={collapsed ? collapsedHint : 'Свернуть вложенные чаты'}
          style={{
            position: 'absolute', left: offset(depth), top: elbowY - 16,
            width: GUTTER, height: 32, zIndex: 2,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            border: 'none', background: 'none', padding: 0, cursor: 'pointer',
            color: row.onActivePath ? C.accent : chevronHover ? C.textSecondary : C.textMuted,
          }}
        >
          <ChevronRight
            size={14} strokeWidth={2.2}
            style={{ transform: collapsed ? 'none' : 'rotate(90deg)', transition: 'transform 0.15s' }}
          />
        </button>
      )}

      {/* Счётчик спрятанной ветки у свёрнутого узла — микро-бейдж у верхне-правого угла
          chevron. Второе число через «/» — сколько из них сейчас в работе; при нуле не
          показывается, чтобы у спокойной ветки бейдж оставался прежним одиночным.
          Цвета сверены с легендой точки статуса (StatusIndicator): accent = «работает»,
          поэтому акцент несёт именно число работающих, а общее — нейтральное. Зелёный
          сюда не годится: в этом же списке C.success означает статус active, то есть
          «ход завершён» — ровно НЕ работу.
          Числа клампятся: бейдж выходит за свою gutter-колонку поверх карточки, и
          длинная строка накрыла бы точку статуса и начало названия чата. */}
      {hasChildren && collapsed && (
        <div
          aria-hidden
          style={{
            position: 'absolute', left: spineX(depth) - 1, top: elbowY - 19, zIndex: 2,
            pointerEvents: 'none',
            minWidth: 14, height: 14, padding: '0 3px', boxSizing: 'border-box',
            borderRadius: R.max, background: C.accentLight, color: C.textSecondary,
            fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 600,
            lineHeight: '14px', textAlign: 'center', whiteSpace: 'nowrap',
          }}
        >
          {formatGroupCount(groupCount)}
          {groupRunningCount > 0 && (
            <>
              /<span style={{ color: C.accent }}>{formatGroupCount(groupRunningCount)}</span>
            </>
          )}
        </div>
      )}
    </div>
  );
}
