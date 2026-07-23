import { useState } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, R, FONT, FS } from '../lib/design';
import type { ChatTreeRowData } from '../lib/chatTree';

// === Строка дерева чатов: отступ + connector-линии + chevron вокруг ChatCard ===
// Линии и контрол рисуются здесь, а НЕ в карточке: у ChatCard overflow:hidden,
// он обрезал бы вертикали. Сама карточка передаётся как children без изменений.
// Геометрия — docs/mockups/chat-list-tree-spec.md: одна формула оси spineX на всех
// уровнях, глубина отступа клампится на 6.

const STEP = 14;
const MAX_DEPTH = 6;

interface Props {
  row: ChatTreeRowData;
  isMobile: boolean;
  onToggleCollapse: (id: string) => void;
  children: React.ReactNode;
}

export function ChatTreeRow({ row, isMobile, onToggleCollapse, children }: Props) {
  const [chevronHover, setChevronHover] = useState(false);

  const GUTTER = isMobile ? 28 : 20;
  const elbowY = isMobile ? 23 : 20;
  const offset = (d: number) => Math.min(d, MAX_DEPTH) * STEP;
  const spineX = (d: number) => offset(d) + GUTTER / 2;
  const cardLeftX = (d: number) => offset(d) + GUTTER;

  const { depth, isLast, hasChildren, collapsed, childCount } = row;
  const lineColor = (accent: boolean) => (accent ? C.accent : C.divider);

  return (
    <div style={{
      position: 'relative',
      // flex-колонка, чтобы marginBottom карточки не схлопывался наружу —
      // иначе вертикали рвались бы в зазорах между строками
      display: 'flex',
      flexDirection: 'column',
      paddingLeft: cardLeftX(depth),
    }}>
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
          onMouseEnter={() => setChevronHover(true)}
          onMouseLeave={() => setChevronHover(false)}
          title={collapsed ? 'Развернуть вложенные чаты' : 'Свернуть вложенные чаты'}
          aria-label={collapsed ? 'Развернуть вложенные чаты' : 'Свернуть вложенные чаты'}
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

      {/* Счётчик прямых детей у свёрнутого узла — микро-бейдж у верхне-правого угла chevron */}
      {hasChildren && collapsed && (
        <div aria-hidden style={{
          position: 'absolute', left: spineX(depth) - 1, top: elbowY - 19, zIndex: 2,
          pointerEvents: 'none',
          minWidth: 14, height: 14, padding: '0 2px', boxSizing: 'border-box',
          borderRadius: R.max, background: C.accentLight, color: C.accent,
          fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 600,
          lineHeight: '14px', textAlign: 'center',
        }}>
          {childCount}
        </div>
      )}
    </div>
  );
}
