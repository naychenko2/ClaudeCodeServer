import { useState } from 'react';
import type { ProjectGroup } from '../../types';
import { C, R, FIELD, SHADOW, FONT } from '../../lib/design';

interface Props {
  groups: ProjectGroup[];
  value: string;                    // groupId или '' для «без группы»
  onChange: (groupId: string) => void;
}

// Селект группы для диалогов проекта. Нативный <select> — корректно работает и на мобиле.
export function GroupSelect({ groups, value, onChange }: Props) {
  const [focused, setFocused] = useState(false);
  return (
    <div style={{ position: 'relative', display: 'flex', alignItems: 'center' }}>
      <select
        value={value}
        onChange={e => onChange(e.target.value)}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        style={{
          appearance: 'none', WebkitAppearance: 'none', MozAppearance: 'none',
          width: '100%', boxSizing: 'border-box',
          background: FIELD.background,
          border: `1px solid ${focused ? FIELD.borderFocus : C.border}`,
          borderRadius: R.xl, padding: '10px 34px 10px 13px',
          fontSize: FIELD.fontSize, color: FIELD.color, fontFamily: FONT.sans,
          outline: 'none', cursor: 'pointer',
          boxShadow: focused ? SHADOW.focus : 'none',
          transition: 'border-color 0.15s, box-shadow 0.15s',
        }}
      >
        <option value="">Без группы</option>
        {groups.map(g => (
          <option key={g.id} value={g.id}>{g.name}</option>
        ))}
      </select>
      {/* Стрелка */}
      <span style={{ position: 'absolute', right: 12, pointerEvents: 'none', color: C.textMuted, display: 'flex' }}>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
          <polyline points="6 9 12 15 18 9" />
        </svg>
      </span>
    </div>
  );
}
