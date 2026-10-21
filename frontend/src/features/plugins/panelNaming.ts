// Единые имя и иконка панели плагина. Раньше каждый конец (десктоп-карточка,
// мобильная шапка, переключатель) выводил имя/иконку по-своему: десктоп — сырым
// kebab («notes-tasks»), мобила — humanizeId («Notes Tasks»), и у всех карточек одна
// иконка Plug. Здесь — один источник: имя = title из манифеста (иначе humanizeId),
// иконка = детерминированно из id плагина (манифест пакета иконки не несёт).
import {
  FileText, ListChecks, BookOpen, Settings2, BarChart3, FolderOpen,
  type LucideIcon,
} from 'lucide-react';
import type { PluginInfo, PluginPanelView } from '../../lib/api';

// Человекочитаемое имя из id пакета {publisher}/{slug}: slug → слова с большой буквы
// (та же логика, что была в PluginsProjectSection).
export function humanizeId(id: string): string {
  const slug = id.split('/').pop() ?? id;
  return slug.split('-').filter(Boolean)
    .map(w => w[0].toUpperCase() + w.slice(1))
    .join(' ') || id;
}

// Имя панели: title из манифеста, иначе — имя плагина. Один и тот же вывод на
// десктопе, мобиле и в подписи переключателя (критерий: заголовок не дублируется).
export function panelDisplayName(plugin: PluginInfo, panel: PluginPanelView): string {
  return panel.title || humanizeId(plugin.id);
}

// Набор иконок для карточек плагинов. Манифест пакета (PluginPanel) несёт только
// id/slot/entry/title — иконки в нём нет, а бэк (не трогаем) её не поднимает.
// Поэтому иконку выводим из id плагина детерминированно: разные плагины в рельсе
// различимы (раньше у всех одна Plug).
const PLUGIN_ICONS: LucideIcon[] = [FileText, ListChecks, BookOpen, Settings2, BarChart3, FolderOpen];

export function pluginIconFor(pluginId: string): LucideIcon {
  let h = 0;
  for (let i = 0; i < pluginId.length; i++) h = (h * 31 + pluginId.charCodeAt(i)) >>> 0;
  return PLUGIN_ICONS[h % PLUGIN_ICONS.length];
}
