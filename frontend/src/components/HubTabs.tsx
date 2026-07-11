import type { ReactNode } from 'react';
import { PillSwitch } from './Toolbar';

export type HubTab = 'chats' | 'projects' | 'calendar' | 'notes' | 'personas' | 'knowledge';

// Иконки разделов для мобильного компакт-режима (Feather-стиль, как по всему приложению).
// Определены локально: components не импортирует из features (слои), а геометрия
// повторяет канонические иконки разделов (чат, папка, календарь, ноды заметок).
const tabSvg = (children: ReactNode) => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
    strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{children}</svg>
);
const TAB_ICONS: Record<HubTab, ReactNode> = {
  chats: tabSvg(<path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" />),
  projects: tabSvg(<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />),
  calendar: tabSvg(<><rect x="3" y="4" width="18" height="18" rx="2" /><path d="M16 2v4M8 2v4M3 10h18" /><circle cx="12" cy="16" r="1.5" fill="currentColor" stroke="none" /></>),
  notes: tabSvg(<><circle cx="6" cy="7" r="2.5" /><circle cx="18" cy="8" r="2.5" /><circle cx="12" cy="18" r="2.5" /><path d="M7.7 9 10.7 16M16.6 10 13.4 16M8.5 7.4 15.5 7.8" /></>),
  personas: tabSvg(<><circle cx="12" cy="8" r="4" /><path d="M4 20c0-4 3.6-6 8-6s8 2 8 6" /></>),
  knowledge: tabSvg(<><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" /><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" /></>),
};

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь | Заметки | Персоны» — на общем PillSwitch.
// mobile: компакт-режим — неактивные сегменты иконками, подпись только у активного
// (разделы помещаются на 320px без обрезания и скролла).
export function HubTabs({ value, onChange, mobile }: {
  value: HubTab;
  onChange: (t: HubTab) => void;
  mobile?: boolean;
}) {
  const options = [
    { value: 'chats' as HubTab, label: 'Чаты' },
    { value: 'projects' as HubTab, label: 'Проекты' },
    { value: 'calendar' as HubTab, label: 'Календарь' },
    { value: 'notes' as HubTab, label: 'Заметки' },
    { value: 'personas' as HubTab, label: 'Персоны' },
    { value: 'knowledge' as HubTab, label: 'Знания' },
  ].map(o => mobile ? { ...o, icon: TAB_ICONS[o.value] } : o);
  return (
    <PillSwitch<HubTab>
      value={value}
      onChange={onChange}
      draggable
      compact={mobile}
      persistKey="hub-tabs"
      variant="hub"
      options={options}
    />
  );
}
