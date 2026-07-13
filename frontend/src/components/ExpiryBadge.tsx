import { Hourglass } from 'lucide-react';
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
      <Hourglass size={10} strokeWidth={2} style={{ flexShrink: 0 }} />
      {left.replace('через ', '')}
    </span>
  );
}
