import { C, FONT } from '../lib/design';

export type HubTab = 'chats' | 'projects';

// Компактный сегмент-переключатель хаба «Чаты | Проекты» (пилюля, как в макете)
export function HubTabs({ value, onChange }: { value: HubTab; onChange: (t: HubTab) => void }) {
  const items: { value: HubTab; label: string }[] = [
    { value: 'chats', label: 'Чаты' },
    { value: 'projects', label: 'Проекты' },
  ];
  return (
    <div style={{ display: 'flex', gap: 3, background: C.bgPanel, borderRadius: 10, padding: 3, flexShrink: 0 }}>
      {items.map(it => {
        const active = it.value === value;
        return (
          <button
            key={it.value}
            onClick={() => onChange(it.value)}
            style={{
              padding: '8px 18px', borderRadius: 7, border: 'none', cursor: 'pointer',
              fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 600,
              background: active ? C.bgWhite : 'transparent',
              color: active ? C.textHeading : C.textSecondary,
              boxShadow: active ? '0 1px 2px rgba(0,0,0,.06)' : 'none',
              transition: 'background 0.15s, color 0.15s',
            }}
          >
            {it.label}
          </button>
        );
      })}
    </div>
  );
}
