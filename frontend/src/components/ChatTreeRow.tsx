import { useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { useDraggable, useDroppable } from '@dnd-kit/core';
import { C, R, FONT, FS } from '../lib/design';
import { formatGroupCount, type ChatTreeRowData } from '../lib/chatTree';
import type { Session } from '../types';
import { useChatDrag } from './ChatGroupingDnd';

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
// Отдельной колонки слева у строки больше нет: карточка корня стоит вплотную к краю
// списка, как в плоском виде. Ось связи проходит ПО карточке, в SPINE_INSET от её
// левого края — там же, в шве, сидит контрол ветки. Инсет меньше внутреннего отступа
// карточки (12/16), поэтому ось и контрол ложатся на её поле, а не на текст.
const SPINE_INSET = 8;
const SPINE_INSET_MOBILE = 10;
// Контрол ветки (треугольник / счётчик свёрнутого) — круг такого диаметра с центром
// на оси. Карточка освобождает под него место слева: CONTROL_INSET у развёрнутой
// ветки, у свёрнутой больше — там пилюля с числом, и она шире.
const CONTROL = 16;
const CONTROL_MOBILE = 20;
const CONTROL_INSET = 8;
const CONTROL_INSET_COLLAPSED = 14;

/**
 * Насколько сдвинуть содержимое карточки вправо, чтобы контрол ветки не лёг на
 * название (проп leadingInset у ChatCard). Строка без детей контрола не несёт.
 */
export function treeLeadingInset(row: ChatTreeRowData): number {
  if (!row.hasChildren) return 0;
  return row.collapsed ? CONTROL_INSET_COLLAPSED : CONTROL_INSET;
}

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

  const inset = isMobile ? SPINE_INSET_MOBILE : SPINE_INSET;
  const control = isMobile ? CONTROL_MOBILE : CONTROL;
  const elbowY = isMobile ? 23 : 20;
  // Левый край карточки уровня d — он же отступ строки: у корня ноль
  const offset = (d: number) => Math.min(d, MAX_DEPTH) * STEP;
  const spineX = (d: number) => offset(d) + inset;

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
        paddingLeft: offset(depth),
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
      {/* Подсветка цели: рамка ровно по карточке, а не по всей строке со ступенькой.
          Радиус и нижний отступ повторяют ChatCard (радиус на мобиле шире, зазор
          между карточками — её marginBottom), иначе виден двойной контур. */}
      {highlight && (
        <div aria-hidden style={{
          position: 'absolute', left: offset(depth), right: 0, top: 0, bottom: CARD_GAP,
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

      {/* Горизонталь-ввод в левый край карточки по центру её первой строки */}
      {depth >= 1 && (
        <div aria-hidden style={{
          position: 'absolute', left: spineX(depth - 1), top: elbowY,
          width: offset(depth) - spineX(depth - 1), height: 1,
          background: lineColor(row.elbowAccent),
        }} />
      )}

      {/* Вертикаль под своим контролом ветки — вниз к развёрнутым детям. Идёт по
          карточке, но рисуется ДО неё: непрозрачная карточка её закрывает, и линия
          выходит наружу уже в зазоре под собой, у строк потомков. */}
      {hasChildren && !collapsed && (
        <div aria-hidden style={{
          position: 'absolute', left: spineX(depth), top: elbowY + 7, bottom: 0,
          width: 1, background: lineColor(row.stubAccent),
        }} />
      )}

      {children}

      {/* Контрол ветки — в шве на левом крае карточки, по центру её первой строки.
          Отдельной колонки под него нет: карточка сама освобождает место отступом
          leadingInset (CONTROL_INSET), поэтому контрол лежит на её поле, а не на
          названии. Кромка состояния под ним видна выше и ниже — она во всю высоту.
          Развёрнутая ветка — треугольник, свёрнутая — счётчик спрятанных чатов:
          два состояния одного места, поэтому отдельного бейджа-числа больше нет.
          Сколько из них в работе, несёт цвет (accent — как у кромки «работает»),
          а не второе число: пилюля растёт вправо, и «99+/99+» накрыла бы заголовок.
          Обе цифры остаются в подсказке. */}
      {hasChildren && (
        <button
          onClick={e => { e.stopPropagation(); onToggleCollapse(row.chat.id); }}
          // Сворачивание — не перетаскивание: гасим нажатие до сенсоров dnd-kit,
          // иначе тяга за контрол уносила бы всю ветку
          onMouseDown={e => e.stopPropagation()}
          onTouchStart={e => e.stopPropagation()}
          onMouseEnter={() => setChevronHover(true)}
          onMouseLeave={() => setChevronHover(false)}
          title={collapsed ? collapsedHint : 'Свернуть вложенные чаты'}
          aria-label={collapsed ? collapsedHint : 'Свернуть вложенные чаты'}
          style={{
            position: 'absolute',
            // Вертикаль — по центру ВЫСОТЫ карточки, а не первой строки: контрол
            // единственный на ветке и должен стоять ровно. Строка — flex-колонка из
            // одной карточки, её высота = карточка + marginBottom(CARD_GAP); поэтому
            // 50% высоты строки сдвинуты на CARD_GAP/2 ниже центра карточки — убираем.
            top: `calc(50% - ${CARD_GAP / 2}px)`,
            transform: 'translateY(-50%)',
            // Горизонталь — единая для обоих состояний: треугольник развёрнутой и
            // пилюля свёрнутой стоят на одной X, контрол не прыгает при тоггле.
            // Сдвинуты чуть правее оси (в центр пустой зоны между кромкой состояния
            // и текстом). Контент карточки не двигается: место резервирует leadingInset.
            left: spineX(depth) - control / 2 + (isMobile ? 6 : 5),
            minWidth: control, height: control, padding: collapsed ? '0 3px' : 0,
            boxSizing: 'border-box', zIndex: 2,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            border: 'none', borderRadius: R.max, cursor: 'pointer',
            // Подложка только у свёрнутой: у неё внутри число, и без неё оно
            // читалось бы как часть карточки. Развёрнутая — голый треугольник
            background: collapsed
              ? (groupRunningCount > 0 ? C.accentLight : C.bgSelected)
              : 'none',
            color: collapsed && groupRunningCount > 0
              ? C.accent
              : row.onActivePath ? C.accent : chevronHover ? C.textSecondary : C.textMuted,
            fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 600, lineHeight: 1,
            whiteSpace: 'nowrap',
          }}
        >
          {collapsed ? formatGroupCount(groupCount) : (
            <ChevronRight
              size={12} strokeWidth={2.2}
              style={{ transform: 'rotate(90deg)', transition: 'transform 0.15s' }}
            />
          )}
        </button>
      )}
    </div>
  );
}

// === Рекурсивный рендер дерева с анимацией collapse/expand ===
//
// buildChatTreeRows отдаёт строки плоским DFS-массивом (удобно для метрик и
// секционирования по корням). Для анимации высоты при сворачивании ветки этого
// мало: нужен DOM-контейнер, оборачивающий ТОЛЬКО детей узла, — его и схлопываем.
// Поэтому плоский массив на стороне рендера собирается обратно в дерево (по depth),
// а дети узла рисуются под grid-контейнером 0fr↔1fr: свёрнут — 0fr (высота 0,
// overflow:hidden обрезает строки), развёрнут — 1fr. React детей не размонтирует,
// переход высоты плавный в обе стороны. Поддержка grid-rows transition — все
// современные браузеры; где её нет, ветка просто скачет без анимации (функционально ок).

export interface ChatTreeNode {
  row: ChatTreeRowData;
  children: ChatTreeNode[];
}

/** Плоский DFS-массив строк → дерево по depth (инвариант: строки идут DFS-порядком). */
export function nestTreeRows(rows: ChatTreeRowData[]): ChatTreeNode[] {
  const roots: ChatTreeNode[] = [];
  const stack: ChatTreeNode[] = [];
  for (const row of rows) {
    // Выходим из веток, чья глубина не меньше текущей: братья (та же depth) и
    // возврат к меньшей глубины — всё корректно для DFS-порядка от emit()
    while (stack.length && stack[stack.length - 1].row.depth >= row.depth) stack.pop();
    const node: ChatTreeNode = { row, children: [] };
    (stack.length ? stack[stack.length - 1].children : roots).push(node);
    stack.push(node);
  }
  return roots;
}

const EXPAND_MS = 200;

interface BranchProps {
  node: ChatTreeNode;
  isMobile: boolean;
  onToggleCollapse: (id: string) => void;
  renderCard: (chat: Session, leadingInset: number) => React.ReactNode;
}

/**
 * Ветка дерева: строка + (если есть дети) схлопывающийся контейнер с дочерними
 * ветками. Общий для ChatList и SessionList — отличается только renderCard.
 */
export function ChatTreeBranch({ node, isMobile, onToggleCollapse, renderCard }: BranchProps) {
  const { row, children } = node;
  const collapsed = row.collapsed;
  return (
    <>
      <ChatTreeRow row={row} isMobile={isMobile} onToggleCollapse={onToggleCollapse}>
        {renderCard(row.chat, treeLeadingInset(row))}
      </ChatTreeRow>
      {children.length > 0 && (
        <div
          style={{
            display: 'grid',
            gridTemplateRows: collapsed ? '0fr' : '1fr',
            opacity: collapsed ? 0 : 1,
            // При сворачивании контент бледнеет чуть быстрее обрезки (не видно, как
            // строки «втягиваются»); при разворачивании — проявляется погодя, когда
            // высота уже набирается. Симметрично по ощущению.
            transition: collapsed
              ? `grid-template-rows ${EXPAND_MS}ms ease, opacity ${EXPAND_MS - 70}ms ease`
              : `grid-template-rows ${EXPAND_MS}ms ease, opacity ${EXPAND_MS}ms ease 60ms`,
          }}
        >
          {/* Внутренний слой держит overflow:hidden — grid-контейнер схлопывается
              по высоте, а этот слой обрезает торчащие строки/линии детей. */}
          <div style={{ minHeight: 0, overflow: 'hidden' }}>
            {children.map(c => (
              <ChatTreeBranch
                key={c.row.chat.id}
                node={c}
                isMobile={isMobile}
                onToggleCollapse={onToggleCollapse}
                renderCard={renderCard}
              />
            ))}
          </div>
        </div>
      )}
    </>
  );
}
