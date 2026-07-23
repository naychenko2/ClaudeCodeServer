import type { ReactNode } from 'react';
import { Book, Calendar, Folder, House, MessageCircle, Puzzle, Share2, Users } from 'lucide-react';
import { PillSwitch } from './Toolbar';
import { ProjectSwitcherZone } from '../features/projects/ProjectSwitcherZone';
import { useModules } from '../lib/modules';
import { useFeature, FLAGS } from '../lib/featureFlags';

export type HubTab = 'home' | 'chats' | 'projects' | 'calendar' | 'notes' | 'personas' | 'knowledge' | 'notifications';

// Значение таба хаба: фиксированный раздел ЛИБО внешний модуль (`module:{id}`, ТЗ R6).
// Модульные табы приходят из реестра (GET /api/modules) и генерятся динамически.
export type HubTabValue = HubTab | `module:${string}`;

export function isModuleTab(v: HubTabValue): v is `module:${string}` {
  return typeof v === 'string' && v.startsWith('module:');
}
export function moduleIdOf(v: HubTabValue): string | null {
  return isModuleTab(v) ? v.slice('module:'.length) : null;
}

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
// не в таббаре, а в шапке — логотип «Домой», колокольчик «Уведомления», меню
// аватара «Знания». Всплывающая только внутри раздела вкладка-призрак сбивает
// с толку: набор таббара скачет от того, где ты находишься.
const TABLESS: HubTab[] = ['home', 'notifications', 'knowledge'];

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь | Заметки | Персоны» — на общем PillSwitch.
// mobile: компакт-режим — неактивные сегменты иконками, подпись только у активного
// (разделы помещаются на 320px без обрезания и скролла).
export function HubTabs({ value, onChange, mobile, tabs = DEFAULT_TABS, currentProjectId }: {
  value: HubTabValue;
  onChange: (t: HubTabValue) => void;
  mobile?: boolean;
  // Какие разделы показать. На мобиле HubHeader передаёт сокращённый primary-набор,
  // остальное уходит в «⋯ Разделы» (overflow), чтобы вкладки не скроллились под обрез.
  tabs?: HubTab[];
  // Открытый проект — для подсветки активного значка в зоне переключения. Зона
  // раскрывается только когда активен раздел «Проекты» и это десктоп (не mobile).
  currentProjectId?: string;
}) {
  // Вкладки внешних модулей из реестра (ТЗ R6): дописываются в конец, значение `module:{id}`.
  const modules = useModules();
  const moduleOptions = modules
    .filter(m => m.tab)
    .map(m => ({ value: `module:${m.id}` as HubTabValue, label: m.tab!.label, icon: <Puzzle size={18} strokeWidth={2} /> }));

  // Зона проектов заменяет вкладку «Проекты», когда раздел активен (десктоп). Вне
  // раздела и на мобиле — обычная вкладка. При включенном переключателе проектов
  // в сайдбаре (флаг sidebar-project-switcher) зона скрыта совсем — переключение
  // живет в плашке воркспейса, вкладка «Проекты» остается обычной пилюлей.
  const sidebarSwitcher = useFeature(FLAGS.sidebarProjectSwitcher);
  const showZone = !mobile && value === 'projects' && !sidebarSwitcher;
  // Активный раздел вне набора табов: из TABLESS — не получает вкладку вовсе
  // (PillSwitch умеет «нет выбранного»), остальные скрытые дописываются условной
  // вкладкой в конец. На мобиле так всплывают «Заметки» и «Персоны» из «⋯ Разделы»,
  // чтобы было видно, где находишься. Модульный таб в набор фиксированных не входит —
  // он живёт в moduleOptions ниже, поэтому из проверки исключаем.
  const isKnownFixed = !isModuleTab(value) && (tabs.includes(value) || TABLESS.includes(value));
  const shown = isKnownFixed || isModuleTab(value) ? tabs : [...tabs, value as HubTab];
  const fixedOptions = shown.map(v => mobile
    ? { value: v as HubTabValue, label: TAB_LABELS[v], icon: TAB_ICONS[v] }
    : { value: v as HubTabValue, label: TAB_LABELS[v] });
  const options = mobile ? [...fixedOptions, ...moduleOptions]
    : [...fixedOptions, ...moduleOptions.map(o => ({ value: o.value, label: o.label }))];
  return (
    <PillSwitch<HubTabValue>
      value={value}
      onChange={onChange}
      draggable
      compact={mobile}
      persistKey="hub-tabs"
      variant="hub"
      options={options}
      renderOption={showZone
        ? opt => opt.value === 'projects'
          ? <ProjectSwitcherZone currentProjectId={currentProjectId} onOpenHub={() => onChange('projects')} />
          : null
        : undefined}
    />
  );
}
