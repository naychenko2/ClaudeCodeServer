import { Wrench, Zap } from 'lucide-react';
import type { CSSProperties } from 'react';
import type { ChatOriginInfo } from '../lib/chatOrigin';
import { C, FONT, R } from '../lib/design';

const ICONS = { task: Wrench, automation: Zap };

// Бейдж происхождения чата (задача/автоматизация) — иконка+название, чисто
// информационный (без перехода): клик по карточке чата не должен уводить в раздел.
// Переиспользуется в плашках списка чатов, шапке чата и панели артефактов.
// iconOnly — компактный вариант (только иконка), для узких мест вроде подзаголовка
// шапки чата на мобиле; полный текст доступен через title/tooltip.
export function ChatOriginBadge({ origin, style, iconOnly }: { origin: ChatOriginInfo; style?: CSSProperties; iconOnly?: boolean }) {
  const Icon = ICONS[origin.kind];
  const color = origin.tone === 'info' ? C.info : C.warningText;
  const background = origin.tone === 'info' ? C.infoBg : C.warningBg;
  const shared: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: iconOnly ? 0 : 4, minWidth: 0, maxWidth: '100%',
    fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, lineHeight: 1.5,
    color, background, borderRadius: R.sm, padding: iconOnly ? '2px 4px' : '1px 6px',
    ...style,
  };
  return (
    <span style={shared} title={origin.label} aria-label={origin.label}>
      <Icon size={11} strokeWidth={2.2} style={{ flexShrink: 0 }} />
      {!iconOnly && <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{origin.label}</span>}
    </span>
  );
}
