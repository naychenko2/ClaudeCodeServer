// Harness для e2e на живом PluginFrame (R-Gleb, minor 8).
//
// Бандлится в самодостаточный IIFE (Vite build, см. plugin-bridge-happy.spec.ts) и
// инжектится в страницу: React + ReactDOM + PluginFrame + все зависимости — внутри.
// Leaf-модули стабим ЧЕРЕЗ их экспортируемые объекты (api — объект, свойства
// мутабельны); useIsMobile/useProjectFeature/getEffectiveTheme читают window/localStorage,
// их состояние задаёт тест (viewport, project без capabilities → плагины доступны).
//
// Панель (в sandbox-iframe) по init постит __pf_init (version, granted) в top-документ:
// так e2e видит, какой init дошёл до НОВОГО фрейма, не лезя в cross-origin окно.
import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { PluginFrame } from '../src/features/plugins/PluginFrame';
import { api } from '../src/lib/api';
import type { PluginInfo, PluginPanelView, Project } from '../src/types';

const g = window as unknown as {
  __pf?: {
    inits: { version: string; granted: string[] }[];
    setVersion: (v: string) => void;
    setGranted: (gr: string[]) => void;
    plugin: PluginInfo;
  };
};

// Панель: по init постит версию и набор прав в top. ccsPlugin — шим кита (встроен
// хостом в srcdoc), on('init', cb) получает init-сообщение хоста.
const PANEL_HTML =
  '<script>' +
  'ccsPlugin.on("init", function (d) {' +
  '  (window.top || window).postMessage({ type: "__pf_init", version: d.plugin && d.plugin.version, granted: d.granted || [] }, "*");' +
  '});' +
  '</script><p>pf</p>';

// api.plugins.* — стабы: panelHtml отдаёт html панели, остальные — пустые ответы.
api.plugins.panelHtml = async () => PANEL_HTML;
api.plugins.list = async () => [];
api.plugins.call = async () => ({});
api.plugins.grant = async () => ({});

// Project без capabilities: pluginsAvailable → true (дефолт), useIsMobile задаёт viewport.
const project: Project = {
  id: 'proj-1', name: 'T', rootPath: '/t',
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
};

let plugin: PluginInfo = {
  id: 'acme/x', version: '1.0.0', sha256: 'a',
  requested: ['notes:read'], granted: [], enabled: true, source: 'upload:x', secrets: [],
  contributes: { panels: [{ id: 'main', title: 'Main', entry: 'panel/index.html' }] },
};
const panel: PluginPanelView = { id: 'main', title: 'Main', entry: 'panel/index.html' };

const root = createRoot(document.getElementById('root')!);
function render() {
  root.render(createElement(PluginFrame, { project, plugin, panel }));
}
render();

g.__pf = {
  inits: [],
  setVersion: (v: string) => { plugin = { ...plugin, version: v }; render(); },
  setGranted: (gr: string[]) => { plugin = { ...plugin, granted: gr }; render(); },
  get plugin() { return plugin; },
};

// Сбор init'ов: панель постит __pf_init в top (cross-origin postMessage).
window.addEventListener('message', (e) => {
  const d = e.data as { type?: string; version?: string; granted?: string[] } | null;
  if (d && d.type === '__pf_init') g.__pf!.inits.push({ version: d.version ?? '', granted: d.granted ?? [] });
});
