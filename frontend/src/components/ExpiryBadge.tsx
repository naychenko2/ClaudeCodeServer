import type { Session } from '../types';
import { C, FONT } from '../lib/design';
import { formatTimeLeft } from '../lib/expiry';

// Метка временного чата в списках: песочные часы + остаток времени («3 ч», «6 дн»).
// Ничего не рендерит для обычного чата.
export function ExpiryBadge({ session }: { session: Pick<Session, 'updatedAt' | 'expiresAfterMinutes'> }) {
  const left = formatTimeLeft(session);
  if (!left) return null;
  return (
    <span
      title={`Временный чат — удалится ${left}, если не будет активности`}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 3,
        fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted,
        lineHeight: 1, whiteSpace: 'nowrap',
      }}
    >
      <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M6 2h12M6 22h12M8 2v4l4 4 4-4V2M8 22v-4l4-4 4 4v4" />
      </svg>
      {left.replace('через ', '')}
    </span>
  );
}
