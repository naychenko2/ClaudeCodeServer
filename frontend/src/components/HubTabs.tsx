import type { ReactNode } from 'react';
import { Book, Calendar, Folder, MessageCircle, Share2, Users } from 'lucide-react';
import { PillSwitch } from './Toolbar';

export type HubTab = 'chats' | 'projects' | 'calendar' | 'notes' | 'personas' | 'knowledge' | 'notifications';

// Иконки разделов для мобильного компакт-режима (lucide-react, Feather-стиль).
const TAB_ICONS: Record<HubTab, ReactNode> = {
  chats: <MessageCircle size={18} strokeWidth={2} />,
  projects: <Folder size={18} strokeWidth={2} />,
  calendar: <Calendar size={18} strokeWidth={2} />,
  notes: <Share2 size={18} strokeWidth={2} />,
  personas: <Users size={18} strokeWidth={2} />,
  knowledge: <Book size={18} strokeWidth={2} />,
  notifications: <MessageCircle size={18} strokeWidth={2} />,
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
  ]
    // 5 разделов в компакт-таббаре (compact-режим: неактивные иконками, подпись только у активного).
    // «Знания» убраны из хаба — вызов живёт в меню аватара («Настройка знаний»); сам экран доступен
    // по onTab('knowledge') и диплинку #/knowledge/{id}. «Уведомления» — только колокольчик.
    .map(o => mobile ? { ...o, icon: TAB_ICONS[o.value] } : o);
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
