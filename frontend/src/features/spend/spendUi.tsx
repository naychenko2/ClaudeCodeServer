// Общие примитивы раздела «Аналитика токенов»: пустые состояния, скелетоны,
// чипы, иконки узлов. Только токены design.ts, инлайн-стили.
import type { ReactNode, CSSProperties } from 'react';
import { C, FONT, GROUP_COLORS, R } from '../../lib/design';
import { Dot } from '../../components/ui';
import type { SpendDim } from '../../lib/spend';

// Детерминированный цвет аватара-инициала по строке (как у групп проектов)
export function hashColor(s: string): string {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0;
  return GROUP_COLORS[Math.abs(h) % GROUP_COLORS.length];
}

// Круглый аватар-инициал узла (пользователь/проект/персона)
export function NodeAvatar({ name }: { name: string }) {
  return (
    <span style={{
      width: 20, height: 20, borderRadius: R.full, flexShrink: 0, background: hashColor(name),
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      fontSize: 9, fontWeight: 700, color: C.onDark, fontFamily: FONT.sans,
    }}>
      {(name[0] ?? '·').toUpperCase()}
    </span>
  );
}

// Мини-тег типа чата: чат / задача
export function KindTag({ meta }: { meta: string | null }) {
  const isTask = meta === 'task';
  return (
    <span style={{
      fontSize: 10, padding: '1px 7px', borderRadius: R.sm, flexShrink: 0, fontFamily: FONT.sans,
      background: isTask ? C.infoBg : C.accentLight, color: isTask ? C.info : C.accent,
    }}>
      {isTask ? 'задача' : 'чат'}
    </span>
  );
}

// Цветная точка серии (источники) переехала в общие примитивы — здесь реэкспорт,
// чтобы не переписывать импорты во всех потребителях spend
export { Dot };

// Иконка узла по разрезу
export function nodeIcon(dim: SpendDim, name: string, meta: string | null, srcColor?: string): ReactNode {
  if (dim === 'user' || dim === 'project' || dim === 'persona') return <NodeAvatar name={name} />;
  if (dim === 'chat') return <KindTag meta={meta} />;
  if (dim === 'source' && srcColor) return <Dot color={srcColor} />;
  return null;
}

// Центрированное пустое состояние (empty-state / «под срез ничего не попало» / ошибка)
export function EmptyBody({ pic, title, text, action }: {
  pic: string; title: string; text: string; action?: ReactNode;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: '44px 24px', textAlign: 'center' }}>
      <div style={{ fontSize: 34, marginBottom: 12 }}>{pic}</div>
      <div style={{ fontFamily: FONT.serif, fontSize: 18, color: C.textHeading, marginBottom: 6 }}>{title}</div>
      <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary, maxWidth: 320, lineHeight: 1.5, marginBottom: action ? 10 : 0 }}>{text}</div>
      {action}
    </div>
  );
}

// Скелетон загрузки (анимация cc-skeleton в index.css)
export function Skel({ w, h = 14, style }: { w: number | string; h?: number; style?: CSSProperties }) {
  return <div className="cc-skel" style={{ width: w, height: h, borderRadius: R.md, ...style }} />;
}

// Скелетон-заглушка экрана: несколько строк разной ширины
export function SkelBlock() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, padding: 16 }}>
      <Skel w="38%" h={26} />
      <Skel w="100%" h={90} />
      <div style={{ display: 'flex', gap: 8 }}>
        <Skel w="33%" h={120} /><Skel w="33%" h={120} /><Skel w="33%" h={120} />
      </div>
    </div>
  );
}

// Ошибка загрузки с повтором
export function LoadError({ onRetry }: { onRetry: () => void }) {
  return (
    <EmptyBody
      pic="⚠️"
      title="Не удалось загрузить"
      text="Данные аналитики не ответили. Проверьте соединение и попробуйте ещё раз."
      action={<GhostBtn onClick={onRetry}>Повторить</GhostBtn>}
    />
  );
}

// Кнопка-призрак в стиле прототипа (btn.ghost)
export function GhostBtn({ onClick, children, style }: { onClick: () => void; children: ReactNode; style?: CSSProperties }) {
  return (
    <button
      onClick={onClick}
      style={{
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 6,
        background: 'none', border: `1px solid ${C.border}`, color: C.textPrimary,
        borderRadius: R.xl, padding: '7px 16px', fontSize: 12.5, fontWeight: 600,
        cursor: 'pointer', fontFamily: FONT.sans, ...style,
      }}
    >
      {children}
    </button>
  );
}

// Чип (фильтр/действие): filter — активный accent-чип с крестиком
export function Chip({ children, onClick, filter, dashed, title }: {
  children: ReactNode; onClick?: () => void; filter?: boolean; dashed?: boolean; title?: string;
}) {
  return (
    <span
      onClick={onClick}
      title={title}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 11, padding: '3px 10px',
        borderRadius: R.max, whiteSpace: 'nowrap', fontFamily: FONT.sans, flexShrink: 0,
        border: `1px ${dashed ? 'dashed' : 'solid'} ${filter ? C.accentMuted : C.border}`,
        background: filter ? C.accentLight : C.bgCard,
        color: filter ? C.accent : C.textSecondary,
        fontWeight: filter ? 600 : 400,
        cursor: onClick ? 'pointer' : 'default',
      }}
    >
      {children}
    </span>
  );
}

// Крестик внутри чипа/уровня
export function ChipX({ onClick }: { onClick: () => void }) {
  return (
    <span
      onClick={e => { e.stopPropagation(); onClick(); }}
      style={{ fontWeight: 700, opacity: 0.8, cursor: 'pointer', padding: '0 1px' }}
    >
      ×
    </span>
  );
}

// Горизонтальная полоса-доля (топ моделей, состав хода)
export function HBar({ label, value, share, color, icon }: {
  label: string; value: string; share: number; color: string; icon?: ReactNode;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 7 }}>
      <span style={{
        width: 112, fontSize: 11, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden',
        textOverflow: 'ellipsis', flexShrink: 0, display: 'flex', alignItems: 'center', gap: 5, fontFamily: FONT.sans,
      }}>
        {icon}{label}
      </span>
      <div style={{ flex: 1, height: 8, borderRadius: 4, background: C.bgSelected, overflow: 'hidden' }}>
        <div style={{ display: 'block', height: '100%', borderRadius: 4, width: `${Math.max(2, Math.round(share * 100))}%`, background: color }} />
      </div>
      <span style={{ width: 58, textAlign: 'right', fontFamily: FONT.mono, fontSize: 11, color: C.textSecondary, flexShrink: 0 }}>{value}</span>
    </div>
  );
}

// Выпадающее меню (в стиле .menu прототипа); привязка — родитель с position:relative
export function DropMenu({ children, width = 240 }: { children: ReactNode; width?: number }) {
  return (
    <div style={{
      position: 'absolute', top: 'calc(100% + 4px)', left: 0, zIndex: 60, width,
      background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
      boxShadow: 'var(--shadow-dropdown)', padding: 6, fontSize: 12, fontFamily: FONT.sans,
    }}>
      {children}
    </div>
  );
}

export function MenuItem({ children, onClick, hint, disabled }: {
  children: ReactNode; onClick?: () => void; hint?: string; disabled?: boolean;
}) {
  return (
    <div
      onClick={disabled ? undefined : onClick}
      style={{
        display: 'flex', alignItems: 'center', gap: 8, padding: '7px 10px', borderRadius: R.md,
        cursor: disabled ? 'default' : 'pointer', color: C.textPrimary, opacity: disabled ? 0.5 : 1,
      }}
      onMouseEnter={e => { if (!disabled) e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
    >
      {children}
      {hint && <span style={{ marginLeft: 'auto', fontSize: 10, color: C.textMuted, fontFamily: FONT.mono }}>{hint}</span>}
    </div>
  );
}

// Бейдж границы детального окна («● Детально: N дней»)
export function WindowBadge({ days, compact }: { days: number; compact?: boolean }) {
  return (
    <span
      title={`Детальные записи ходов хранятся последние ${days} дней; старше — только дневные агрегаты`}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 11, padding: '3px 10px',
        borderRadius: R.max, background: C.infoBg, color: C.info, whiteSpace: 'nowrap',
        fontFamily: FONT.sans, flexShrink: 0,
      }}
    >
      ● {compact ? `${days} дней` : `Детально: ${days} дней`}
    </span>
  );
}
