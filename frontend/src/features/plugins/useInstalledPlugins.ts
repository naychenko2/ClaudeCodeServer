// useInstalledPlugins — теперь тонкий читатель общего стора (R-Gleb, minor 5).
// Логика (GET /api/plugins, дифф сборки мусора, подписки на plugins_changed/reconnected)
// вынесена в installedPluginsStore: один источник на проект, а не на каждый вызов.
// Путь импорта сохранён (features/plugins/index.ts, WorkspacePage, PluginsProjectSection).
export { useInstalledPlugins } from './installedPluginsStore';
