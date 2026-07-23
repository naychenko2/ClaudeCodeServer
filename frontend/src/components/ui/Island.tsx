import type { CSSProperties, HTMLAttributes, ReactNode } from 'react';
import { C, FONT, ISLAND, R } from '../../lib/design';

// Карточка-остров (стиль Rider Islands): скруглённая рамка-клиппер на общем
// фоне-холсте. Компонент задаёт только рамку/тень/скругление и фон-подложку —
// контент красит свой корень сам (ребёнок с собственным background перекрывает
// подложку), поэтому существующие панели оборачиваются без правки внутренностей.
export function Island({ bg = ISLAND.bg, borderColor = ISLAND.border, shadow = ISLAND.shadow, style, rootProps, children }: {
  bg?: string;
  // Рамка/тень настраиваемы: PanelShell подсвечивает drop-таргет accent-рамкой,
  // а во fullscreen усиливает тень до модальной
  borderColor?: string;
  shadow?: string;
  // Мержится последним: flex-размеры, opacity, transform — от родителя
  style?: CSSProperties;
  // Атрибуты корневого div (drop-обработчики DnD и т.п.)
  rootProps?: HTMLAttributes<HTMLDivElement>;
  children: ReactNode;
}) {
  return (
    <div
      {...rootProps}
      style={{
        display: 'flex', flexDirection: 'column', overflow: 'hidden', minHeight: 0,
        background: bg, border: `1px solid ${borderColor}`,
        borderRadius: ISLAND.radius, boxShadow: shadow,
        ...style,
      }}
    >
      {children}
    </div>
  );
}

// Шапка острова: утоплена относительно тела карточки, читается как заголовочная
// зона. Геометрия — из эталонного PanelShell (RightPanelStack).
export function IslandHeader({ icon, title, badge, actions, headerProps, children }: {
  icon?: ReactNode;
  title: string;
  badge?: string | null;
  // Кнопки справа (fullscreen/close и т.п.)
  actions?: ReactNode;
  // Атрибуты корня шапки: draggable/drag-обработчики/cursor для DnD PanelShell
  headerProps?: HTMLAttributes<HTMLDivElement> & { draggable?: boolean };
  // Дополнительные контролы между заголовком и actions
  children?: ReactNode;
}) {
  const { style: headerStyle, ...restHeaderProps } = headerProps ?? {};
  return (
    <div
      {...restHeaderProps}
      style={{
        flexShrink: 0, height: ISLAND.headerH, display: 'flex', alignItems: 'center', gap: 7,
        padding: '0 6px 0 12px', borderBottom: `1px solid ${C.border}`,
        background: ISLAND.headerBg,
        ...headerStyle,
      }}
    >
      {icon}
      <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {title}
      </span>
      {badge && (
        <span style={{
          flexShrink: 0, fontFamily: FONT.mono, fontSize: 10.5, fontWeight: 600,
          padding: '2px 7px', borderRadius: R.sm, color: C.textSecondary, background: ISLAND.headerBg,
        }}>
          {badge}
        </span>
      )}
      {children}
      {actions}
    </div>
  );
}
