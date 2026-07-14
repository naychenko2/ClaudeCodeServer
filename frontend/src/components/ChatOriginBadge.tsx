import { Wrench, Zap } from 'lucide-react';
import type { CSSProperties } from 'react';
import type { ChatOriginInfo } from '../lib/chatOrigin';
import { C, FONT, R } from '../lib/design';

const ICONS = { task: Wrench, automation: Zap };

// Бейдж происхождения чата (задача/автоматизация) — иконка+название, кликабельный,
// если цель (задача/правило) ещё существует. Переиспользуется в плашках списка чатов,
// шапке чата и панели артефактов.
export function ChatOriginBadge({ origin, style }: { origin: ChatOriginInfo; style?: CSSProperties }) {
  const Icon = ICONS[origin.kind];
  const color = origin.tone === 'info' ? C.info : C.warningText;
  const background = origin.tone === 'info' ? C.infoBg : C.warningBg;
  const shared: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 4, minWidth: 0, maxWidth: '100%',
    fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, lineHeight: 1.5,
    color, background, borderRadius: R.sm, padding: '1px 6px',
    ...style,
  };
  const content = (
    <>
      <Icon size={11} strokeWidth={2.2} style={{ flexShrink: 0 }} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{origin.label}</span>
    </>
  );

  if (!origin.onOpen) return <span style={shared} title={origin.label}>{content}</span>;

  return (
    <button
      type="button"
      onClick={e => { e.stopPropagation(); origin.onOpen!(); }}
      title={`${origin.label} — нажмите, чтобы открыть`}
      style={{ ...shared, border: 'none', cursor: 'pointer' }}
    >
      {content}
    </button>
  );
}
