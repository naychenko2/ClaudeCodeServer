import type { ProjectGroup } from '../../types';
import { C, FONT } from '../../lib/design';

interface Props {
  group: ProjectGroup;
  count: number;
}

// Заголовок группы в списке проектов: цветная полоска-индикатор + имя + счётчик.
export function GroupHeader({ group, count }: Props) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '6px 2px 3px' }}>
      <span style={{ width: 4, height: 15, borderRadius: 2, background: group.color || C.textMuted, flexShrink: 0 }} />
      <span style={{ flex: 1, fontSize: 12, fontWeight: 700, color: '#5C5246', fontFamily: FONT.sans, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {group.name}
      </span>
      <span style={{ fontSize: 11, color: '#9A8F7E', flexShrink: 0 }}>{count}</span>
    </div>
  );
}
