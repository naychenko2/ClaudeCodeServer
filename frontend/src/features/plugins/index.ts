// Плагины владельца (ADR-031): экран «Плагины» в настройках проекта, диалог согласия,
// слот панели в sidebar, словарь прав. Экспорт — точка входа для других фич.
export { PluginsProjectSection } from './PluginsProjectSection';
export { ConsentDialog } from './ConsentDialog';
export type { PluginConsentData, PluginConsentSecret, PluginContribution } from './ConsentDialog';
export { useInstalledPlugins } from './useInstalledPlugins';
export { useHasInstalledPlugins } from './pluginStore';
export { PluginFrame } from './PluginFrame';
export { PluginPanelsSwitcher } from './PluginPanelsSwitcher';
export { panelDisplayName, pluginDisplayName, pluginIconFor, humanizeId } from './panelNaming';
export {
  isNetPermission, netHostOf, secretNameOf, permissionLabel, NET_PERMISSION_WARNING,
} from './permissionLabels';
