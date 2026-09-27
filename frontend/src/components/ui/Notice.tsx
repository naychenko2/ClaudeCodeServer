// Плашка-баннер причины: «исполнитель ждёт устройство», «ход недоступен» и т. п.
// Строка текста во всю ширину с иконкой причины слева — в отличие от Badge, который
// метка в ряду чипов.
//
// Зачем примитив: одну и ту же warning-плашку рисовали руками карточка задачи и
// композер, и рамка, отступы и иконка в копиях разъехались (дизайн-ревью 4.7, S2).

import type { HTMLAttributes, ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from './icons';

export type NoticeTone = 'warning' | 'danger';

const TONE: Record<NoticeTone, { bg: string; fg: string; border: string }> = {
  warning: { bg: C.warningBg, fg: C.warningText, border: C.warning },
  danger: { bg: C.dangerBg, fg: C.dangerText, border: C.dangerBorder },
};

interface Props extends Omit<HTMLAttributes<HTMLDivElement>, 'title'> {
  tone?: NoticeTone;
  // Иконка причины — компонент lucide: размер и обводку задаёт сама плашка
  icon: LucideIcon;
  // Жирная первая строка; без неё плашка — одна строка текста
  title?: ReactNode;
  children?: ReactNode;
}

export function Notice({ tone = 'warning', icon: Icon, title, children, style, ...rest }: Props) {
  const t = TONE[tone];
  return (
    <div
      role="status"
      {...rest}
      style={{
        display: 'flex', alignItems: 'flex-start', gap: SP.sm,
        padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
        border: `1px solid ${t.border}`, background: t.bg, color: t.fg,
        fontFamily: FONT.sans, fontSize: FS.sm, lineHeight: 1.45,
        ...style,
      }}
    >
      <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0, marginTop: SP.xxs }} />
      <div style={{ minWidth: 0 }}>
        {title && <div style={{ fontWeight: 600 }}>{title}</div>}
        {children}
      </div>
    </div>
  );
}
