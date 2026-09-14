import type { ReactNode } from 'react';
import { Activity, Book, BriefcaseBusiness, Calendar, Columns3, Folder, House, MessageCircle, Puzzle, Users } from 'lucide-react';
import { PillSwitch } from './Toolbar';
import { useModules } from '../lib/modules';
import { isSubsystemEnabled } from '../lib/subsystems';
import { getRegisteredSubsystems, useRegisteredSubsystems } from '../lib/subsystems/registry';
import { defaultHubTabs, isModuleTab, isSubsystemTab, subsystemKeyOf, subsystemTabValue, type HubTab, type HubTabValue } from './hubTabsModel';

// Чистая модель (типы HubTab/HubTabValue, helpers, defaultHubTabs) живёт в
// ./hubTabsModel — здесь остаётся компонент и иконки/подписи. Реэкспорт сохраняет
// прежний публичный адрес `./HubTabs` для потребителей.
export * from './hubTabsModel';

// Подписи разделов (единый источник для таббара и overflow-меню «Разделы»)
export const TAB_LABELS: Record<HubTab, string> = {
  home: 'Домой', chats: 'Чаты', wall: 'Стена', projects: 'Проекты', calendar: 'Календарь',
  personas: 'Персоны', specialties: 'Специальности', knowledge: 'Знания',
  notifications: 'Уведомления',
  telemetry: 'Телеметрия',
};

// Иконки фиксированных разделов (lucide-react, Feather-стиль). Подсистемные вкладки
// берут иконку из manifest.icon (см. tabIcon) — своей статики у них нет.
export const TAB_ICONS: Record<HubTab, ReactNode> = {
  home: <House size={18} strokeWidth={2} />,
  chats: <MessageCircle size={18} strokeWidth={2} />,
  wall: <Columns3 size={18} strokeWidth={2} />,
  projects: <Folder size={18} strokeWidth={2} />,
  calendar: <Calendar size={18} strokeWidth={2} />,
  personas: <Users size={18} strokeWidth={2} />,
  specialties: <BriefcaseBusiness size={18} strokeWidth={2} />,
  knowledge: <Book size={18} strokeWidth={2} />,
  notifications: <MessageCircle size={18} strokeWidth={2} />,
  telemetry: <Activity size={18} strokeWidth={2} />,
};

// Разделы, которые НЕ получают вкладку даже когда активны: вход к ним живёт
// не в таббаре, а в шапке — логотип «Домой», колокольчик «Уведомления», меню
// аватара «Знания», «Специальности» и «Аналитика токенов». Всплывающая только внутри раздела
// вкладка-призрак сбивает с толку: набор таббара скачет от того, где ты находишься.
const TABLESS: HubTab[] = ['home', 'notifications', 'knowledge', 'specialties', 'telemetry'];

// Подпись/иконка вкладки любого вида (подсистемная — из манифеста). Нужны и
// таббару, и скрытому эталону замера в HubHeader.
export function tabLabel(v: HubTabValue): string {
  const k = subsystemKeyOf(v);
  if (k) return getRegisteredSubsystems().find(m => m.key === k)?.title ?? k;
  return TAB_LABELS[v as HubTab] ?? String(v);
}
export function tabIcon(v: HubTabValue): ReactNode | undefined {
  const k = subsystemKeyOf(v);
  if (k) return getRegisteredSubsystems().find(m => m.key === k)?.icon;
  return TAB_ICONS[v as HubTab];
}

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь | Заметки | Персоны» — на общем PillSwitch.
// mobile: компакт-режим — неактивные сегменты иконками, подпись только у активного
// (разделы помещаются на 320px без обрезания и скролла).
// tablet: то же поведение, что у mobile, но autoCompact=true — полнотекстовые
// подписи остаются, пока влезают; при переполнении переходим в иконки. Это
// ступень 1 адаптива планшета; ступень 2 (скролл-полоса) — снаружи, в HubHeader.
export function HubTabs({ value, onChange, mobile, tablet, tabs }: {
  value: HubTabValue;
  onChange: (t: HubTabValue) => void;
  mobile?: boolean;
  tablet?: boolean;
  // Какие разделы показать. На мобиле HubHeader передаёт сокращённый primary-набор,
  // остальное уходит в «⋯ Разделы» (overflow), чтобы вкладки не скроллились под обрез.
  // Не задан — полный набор (фиксированные + подсистемные по order).
  tabs?: HubTabValue[];
}) {
  // Вкладки внешних модулей из реестра (ТЗ R6): дописываются в конец, значение `module:{id}`.
  const modules = useModules();
  const moduleOptions = modules
    .filter(m => m.tab)
    .map(m => ({ value: `module:${m.id}` as HubTabValue, label: m.tab!.label, icon: <Puzzle size={18} strokeWidth={2} /> }));

  // Подсистемные вкладки из реестра. Гейт: вкладка видна, только если подсистема
  // И зарегистрирована в бандле, И включена пользователю, И объявила раздел (`tab`).
  const subs = useRegisteredSubsystems();
  const activeSubs = subs.filter(m => m.tab && isSubsystemEnabled(m.key));
  const activeSubValues = new Set(activeSubs.map(m => subsystemTabValue(m.key)));

  // Входной набор — из входного `tabs` (мобильный primary) либо полный из реестра.
  const explicit = tabs ?? defaultHubTabs(activeSubs);
  const baseTabs = explicit.filter(t => !isSubsystemTab(t) || activeSubValues.has(t));

  // «Стена» — рабочий режим раздела проектов: своей вкладки нет, подсвечиваем
  // пилюлю «Проекты» (клик по ней со стены — выход к списку, App.switchHubTab).
  const displayValue: HubTabValue = value === 'wall' ? 'projects' : value;

  // Активный раздел вне набора табов: из TABLESS — не получает вкладку вовсе
  // (PillSwitch умеет «нет выбранного»); подсистемная (напр. «Заметки» на мобильном
  // primary-наборе) и прочие скрытые — дописываются условной вкладкой в конец, чтобы
  // было видно, где находишься. Модульный таб в набор фиксированных не входит — он
  // живёт в moduleOptions ниже, поэтому исключаем.
  const isMod = isModuleTab(displayValue);
  const isSub = isSubsystemTab(displayValue);
  const appendActive = isMod
    ? false
    : isSub
      ? (!baseTabs.includes(displayValue) && activeSubValues.has(displayValue))
      : !(baseTabs.includes(displayValue) || TABLESS.includes(displayValue as HubTab));
  const shown = appendActive ? [...baseTabs, displayValue] : baseTabs;

  // tablet: иконки у опций нужны как и на мобиле — если PillSwitch включит
  // compact (autoCompact сработает при переполнении), иконки уже на месте.
  const compactLike = mobile || tablet;
  const fixedOptions = shown.map(v => compactLike
    ? { value: v, label: tabLabel(v), icon: tabIcon(v) }
    : { value: v, label: tabLabel(v) });
  const options = compactLike ? [...fixedOptions, ...moduleOptions]
    : [...fixedOptions, ...moduleOptions.map(o => ({ value: o.value, label: o.label }))];
  return (
    <PillSwitch<HubTabValue>
      value={displayValue}
      onChange={onChange}
      draggable
      // tablet: compact включается АВТОМАТИЧЕСКИ при переполнении (ступень 1).
      // mobile: compact сразу (там места ещё меньше).
      compact={mobile}
      autoCompact={tablet}
      persistKey="hub-tabs"
      variant="hub"
      options={options}
    />
  );
}
