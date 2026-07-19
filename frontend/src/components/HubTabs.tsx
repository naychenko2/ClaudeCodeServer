import type { ReactNode } from 'react';
import { Book, Calendar, Folder, House, MessageCircle, Share2, Users } from 'lucide-react';
import { PillSwitch } from './Toolbar';

export type HubTab = 'home' | 'chats' | 'projects' | 'calendar' | 'notes' | 'personas' | 'knowledge' | 'notifications';

// Иконки разделов для мобильного компакт-режима (lucide-react, Feather-стиль).
const TAB_ICONS: Record<HubTab, ReactNode> = {
  home: <House size={18} strokeWidth={2} />,
  chats: <MessageCircle size={18} strokeWidth={2} />,
  projects: <Folder size={18} strokeWidth={2} />,
  calendar: <Calendar size={18} strokeWidth={2} />,
  notes: <Share2 size={18} strokeWidth={2} />,
  personas: <Users size={18} strokeWidth={2} />,
  knowledge: <Book size={18} strokeWidth={2} />,
  notifications: <MessageCircle size={18} strokeWidth={2} />,
};

// Подписи разделов (единый источник для таббара и overflow-меню «Разделы»)
export const TAB_LABELS: Record<HubTab, string> = {
  home: 'Домой', chats: 'Чаты', projects: 'Проекты', calendar: 'Календарь', notes: 'Заметки',
  personas: 'Персоны', knowledge: 'Знания', notifications: 'Уведомления',
};
// Полный набор разделов таббара по умолчанию (desktop)
const DEFAULT_TABS: HubTab[] = ['chats', 'projects', 'calendar', 'notes', 'personas'];
// Разделы, которые НЕ получают вкладку даже когда активны: вход к ним живёт
// в шапке постоянно (логотип «Домой», колокольчик «Уведомления») и там же
// подсвечивается активность.
const TABLESS: HubTab[] = ['home', 'notifications'];

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь | Заметки | Персоны» — на общем PillSwitch.
// mobile: компакт-режим — неактивные сегменты иконками, подпись только у активного
// (разделы помещаются на 320px без обрезания и скролла).
export function HubTabs({ value, onChange, mobile, tabs = DEFAULT_TABS }: {
  value: HubTab;
  onChange: (t: HubTab) => void;
  mobile?: boolean;
  // Какие разделы показать. На мобиле HubHeader передаёт сокращённый primary-набор,
  // остальное уходит в «⋯ Разделы» (overflow), чтобы вкладки не скроллились под обрез.
  tabs?: HubTab[];
}) {
  // Активный раздел вне набора табов: 'home' и 'notifications' вкладку НЕ получают —
  // у обоих свой постоянный вход в шапке (логотип и колокольчик), и он подсвечивается
  // сам, так что вкладка-призрак, всплывающая только внутри раздела, не нужна.
  // PillSwitch умеет «нет выбранного». Остальные скрытые (knowledge) — условной
  // вкладкой в конец (зеркально мобильному паттерну HubHeader).
  const shown = tabs.includes(value) || TABLESS.includes(value) ? tabs : [...tabs, value];
  const options = shown.map(v => mobile
    ? { value: v, label: TAB_LABELS[v], icon: TAB_ICONS[v] }
    : { value: v, label: TAB_LABELS[v] });
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
