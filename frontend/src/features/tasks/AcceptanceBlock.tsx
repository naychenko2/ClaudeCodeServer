// Секция «Критерии приёмки»: по каждому критерию — свидетельство (как проверено и результат)
// либо явная пометка «Нет свидетельства». Без критериев не рисуется вовсе.

import type { Task } from '../../types';
import { C, FONT, FS, R } from '../../lib/design';
import { Badge } from '../../components/ui/Badge';

export function AcceptanceBlock({ task }: { task: Pick<Task, 'acceptance' | 'evidence'> }) {
  const criteria = task.acceptance ?? [];
  if (criteria.length === 0) return null;
  const evidence = task.evidence ?? [];
  return (
    <div
      data-testid="acceptance-block"
      style={{
        background: C.bgWhite, border: `1px solid ${C.borderLight}`,
        borderRadius: R.xl, overflow: 'hidden',
      }}
    >
      {criteria.map((c, i) => {
        const items = evidence.filter(e => e.criterionId === c.id);
        return (
          <div
            key={c.id}
            data-testid="acceptance-row"
            style={{
              padding: '12px 18px', borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
              display: 'flex', flexDirection: 'column', gap: 6, minWidth: 0,
            }}
          >
            <div style={{
              fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600, color: C.textPrimary,
              lineHeight: 1.45, overflowWrap: 'anywhere',
            }}>
              {c.text}
            </div>
            {items.length === 0 ? (
              <div data-testid="acceptance-missing" style={{ alignSelf: 'flex-start', maxWidth: '100%' }}>
                <Badge tone="warning" size="xs">Нет свидетельства</Badge>
              </div>
            ) : items.map(e => (
              <div
                key={`${c.id}:${e.how}`}
                data-testid="acceptance-evidence"
                style={{
                  fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary,
                  lineHeight: 1.45, overflowWrap: 'anywhere',
                }}
              >
                <div><span style={{ color: C.textMuted }}>Как проверено: </span>{e.how}</div>
                <div><span style={{ color: C.textMuted }}>Результат: </span>{e.result}</div>
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}
