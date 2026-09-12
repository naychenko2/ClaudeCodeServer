// Чистая модель таббара хаба: типы значений вкладок, порядок и сборка набора.
// Отделена от компонента HubTabs (тот тянет lucide/PillSwitch/реестр) — чтобы
// порядок и фильтрацию подсистемных вкладок можно было покрыть тестом без DOM.
//
// Подсистемы сюда приходят уже как список манифестов: фильтрация по показателю
// включённости (isSubsystemEnabled) — забота вызывающего (HubTabs/HubHeader).

import type { SubsystemManifest } from '../lib/subsystems/registryCore';

// Фиксированные (ядровые) разделы хаба. Подсистемные вкладки (например, «Заметки»)
// в этот union НЕ входят — они приходят из реестра как динамические значения
// `subsystem:{key}` (см. SubsystemTab) и вставляются по manifest.order.
export type HubTab = 'home' | 'chats' | 'wall' | 'projects' | 'calendar' | 'personas' | 'specialties' | 'knowledge' | 'notifications' | 'spend' | 'telemetry';

// Значение таба хаба: фиксированный раздел ЛИБО внешний модуль (`module:{id}`, ТЗ R6)
// ЛИБО раздел подсистемы (`subsystem:{key}`).
export type SubsystemTab = `subsystem:${string}`;
export type HubTabValue = HubTab | `module:${string}` | SubsystemTab;

export function isModuleTab(v: HubTabValue): v is `module:${string}` {
  return typeof v === 'string' && v.startsWith('module:');
}
export function moduleIdOf(v: HubTabValue): string | null {
  return isModuleTab(v) ? v.slice('module:'.length) : null;
}
export function isSubsystemTab(v: HubTabValue): v is SubsystemTab {
  return typeof v === 'string' && v.startsWith('subsystem:');
}
export function subsystemKeyOf(v: HubTabValue): string | null {
  return isSubsystemTab(v) ? v.slice('subsystem:'.length) : null;
}
export function subsystemTabValue(key: string): SubsystemTab {
  return `subsystem:${key}`;
}

// Порядок разделов таббара — общий для фиксированных и подсистемных вкладок
// (у подсистемы — её manifest.order). Задаёт ПОЗИЦИЮ: «Заметки» (order 35) встают
// между «Календарём» (30) и «Персонами» (50).
const FIXED_TABBAR_ORDER: Record<HubTab, number> = {
  home: 0, chats: 10, projects: 20, wall: 25, calendar: 30,
  personas: 50, specialties: 60, knowledge: 70, notifications: 80, spend: 90, telemetry: 100,
};
// Фиксированные разделы, получающие вкладку в таббаре. «Домой», «Знания»,
// «Уведомления», «Специальности», «Аналитика» и «Телеметрия» вкладок не имеют —
// вход к ним живёт в шапке (логотип, колокольчик, меню аватара). «Стена» — рабочий
// режим раздела проектов (подсвечивает пилюлю «Проекты»), своей вкладки тоже нет.
const FIXED_TABBAR: HubTab[] = ['chats', 'projects', 'calendar', 'personas'];

// Полный набор вкладок таббара: фиксированные разделы + вкладки подсистем,
// отсортированные по order. Подсистема БЕЗ `tab` в таббар не попадает вовсе
// (иначе пилюля вела бы в раздел, которого нет). Структурный набор без гейта
// включённости — фильтр по isSubsystemEnabled на стороне вызывающего.
export function defaultHubTabs(subs: SubsystemManifest[]): HubTabValue[] {
  const fixed = FIXED_TABBAR.map(t => ({ v: t as HubTabValue, o: FIXED_TABBAR_ORDER[t] }));
  const sub = subs
    .filter(m => m.tab)
    .map(m => ({ v: subsystemTabValue(m.key) as HubTabValue, o: m.order }));
  return [...fixed, ...sub].sort((a, b) => a.o - b.o).map(x => x.v);
}
