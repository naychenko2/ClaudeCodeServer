import type { ReactNode } from 'react';
import { Activity, Archive, Book, Calendar, Coins, Columns3, Folder, House, MessageCircle, Puzzle, Share2, Users } from 'lucide-react';
import { PillSwitch } from './Toolbar';
import { useModules } from '../lib/modules';

// Раздел «Архив» (план архива чатов v4, шаг 5): полноценная вкладка наравне с
// «Чатами» (см. DEFAULT_TABS) — архив прячет чаты, а не удаляет, и пользователю
// нужен постоянный явный вход в раздел. В TABLESS его нет: вход живёт в таббаре.
export type HubTab = 'home' | 'chats' | 'archive' | 'wall' | 'projects' | 'calendar' | 'notes' | 'personas' | 'knowledge' | 'notifications' | 'spend' | 'telemetry';

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
// Экспортируем: HubHeader переиспользует их в скрытом компактном эталоне полного
// набора, по которому решает «5 табов влезают или нужен откат на 3+«⋯»». Геометрия
// эталона обязана повторять реальные компактные кнопки PillSwitch — иначе замер врёт.
export const TAB_ICONS: Record<HubTab, ReactNode> = {
  home: <House size={18} strokeWidth={2} />,
  chats: <MessageCircle size={18} strokeWidth={2} />,
  archive: <Archive size={18} strokeWidth={2} />,
  wall: <Columns3 size={18} strokeWidth={2} />,
  projects: <Folder size={18} strokeWidth={2} />,
  calendar: <Calendar size={18} strokeWidth={2} />,
  notes: <Share2 size={18} strokeWidth={2} />,
  personas: <Users size={18} strokeWidth={2} />,
  knowledge: <Book size={18} strokeWidth={2} />,
  notifications: <MessageCircle size={18} strokeWidth={2} />,
  spend: <Coins size={18} strokeWidth={2} />,
  telemetry: <Activity size={18} strokeWidth={2} />,
};

// Подписи разделов (единый источник для таббара и overflow-меню «Разделы»)
export const TAB_LABELS: Record<HubTab, string> = {
  home: 'Домой', chats: 'Чаты', archive: 'Архив', wall: 'Стена', projects: 'Проекты', calendar: 'Календарь', notes: 'Заметки',
  personas: 'Персоны', knowledge: 'Знания', notifications: 'Уведомления', spend: 'Аналитика',
  telemetry: 'Телеметрия',
};
// Полный набор разделов таббара по умолчанию (desktop). Экспортируем для HubHeader:
// он строит скрытый компактный эталон именно этого набора и меряет «все 5
// табов рядом», а не то, что сейчас отрисовано — иначе в откатной ветке (3 таба)
// эталон бы заведомо влезал и цикл переключал ветки туда-обратно.
// «Архив» (план архива чатов v4, шаг 5): чат ПРЯЧЕТСЯ, не удаляется (см.
// ArchivePage), и пользователю нужен явный вход в раздел. Делаем его табом по
// умолчанию — глубокая ветка без кнопки в шапке стала бы недоступной.
export const DEFAULT_TABS: HubTab[] = ['chats', 'archive', 'projects', 'calendar', 'notes', 'personas'];
// Разделы, которые НЕ получают вкладку даже когда активны: вход к ним живёт
// не в таббаре, а в шапке — логотип «Домой», колокольчик «Уведомления», меню
// аватара «Знания» и «Аналитика токенов». Всплывающая только внутри раздела
// вкладка-призрак сбивает с толку: набор таббара скачет от того, где ты находишься.
// «Стена» здесь не нужна: своей вкладки у неё нет, но как рабочий режим раздела
// проектов она подсвечивает пилюлю «Проекты» (displayValue ниже).
const TABLESS: HubTab[] = ['home', 'notifications', 'knowledge', 'spend', 'telemetry'];

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь | Заметки | Персоны» — на общем PillSwitch.
// mobile: компакт-режим — неактивные сегменты иконками, подпись только у активного
// (разделы помещаются на 320px без обрезания и скролла).
// tablet: то же поведение, что у mobile, но autoCompact=true — полнотекстовые
// подписи остаются, пока влезают; при переполнении переходим в иконки. Это
// ступень 1 адаптива планшета; ступень 2 (скролл-полоса) — снаружи, в HubHeader.
export function HubTabs({ value, onChange, mobile, tablet, tabs = DEFAULT_TABS }: {
  value: HubTabValue;
  onChange: (t: HubTabValue) => void;
  mobile?: boolean;
  tablet?: boolean;
  // Какие разделы показать. На мобиле HubHeader передаёт сокращённый primary-набор,
  // остальное уходит в «⋯ Разделы» (overflow), чтобы вкладки не скроллились под обрез.
  tabs?: HubTab[];
}) {
  // Вкладки внешних модулей из реестра (ТЗ R6): дописываются в конец, значение `module:{id}`.
  const modules = useModules();
  const moduleOptions = modules
    .filter(m => m.tab)
    .map(m => ({ value: `module:${m.id}` as HubTabValue, label: m.tab!.label, icon: <Puzzle size={18} strokeWidth={2} /> }));

  // «Стена» — рабочий режим раздела проектов: своей вкладки нет, подсвечиваем
  // пилюлю «Проекты» (клик по ней со стены — выход к списку, App.switchHubTab).
  const displayValue: HubTabValue = value === 'wall' ? 'projects' : value;

  // Переключение проектов живёт в доке воркспейса (ProjectRail), поэтому вкладка
  // «Проекты» в таббаре — обычная пилюля, отдельной зоны переключения нет.
  // Активный раздел вне набора табов: из TABLESS — не получает вкладку вовсе
  // (PillSwitch умеет «нет выбранного»), остальные скрытые дописываются условной
  // вкладкой в конец. На мобиле/планшете так всплывают «Заметки» и «Персоны» из
  // «⋯ Разделы», чтобы было видно, где находишься. Модульный таб в набор фиксированных
  // не входит — он живёт в moduleOptions ниже, поэтому из проверки исключаем.
  const isKnownFixed = !isModuleTab(displayValue) && (tabs.includes(displayValue) || TABLESS.includes(displayValue));
  const shown = isKnownFixed || isModuleTab(displayValue) ? tabs : [...tabs, displayValue as HubTab];
  // tablet: иконки у опций нужны как и на мобиле — если PillSwitch включит
  // compact (autoCompact сработает при переполнении), иконки уже на месте.
  const compactLike = mobile || tablet;
  const fixedOptions = shown.map(v => compactLike
    ? { value: v as HubTabValue, label: TAB_LABELS[v], icon: TAB_ICONS[v] }
    : { value: v as HubTabValue, label: TAB_LABELS[v] });
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
