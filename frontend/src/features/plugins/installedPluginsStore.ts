import { useEffect, useSyncExternalStore } from 'react';
import { api, type PluginInfo } from '../../lib/api';
import { onMessage, onReconnected } from '../../lib/signalr';
import { wsPanels } from '../../pages/workspace/panelStackState';
import { setInstalledPlugins } from './pluginStore';

// Единый источник «установленные плагины проекта» (R-Gleb, minor 5).
//
// Раньше хук useInstalledPlugins был самодостаточным: каждый его вызов (страница
// воркспейса И секция «Плагины» в настройках) вёл СВОЙ GET /api/plugins, СВОЙ дифф
// сборки мусора и СВОЮ подписку на plugins_changed/reconnected. На одно событие это
// было два GET и два диффа GC. Теперь вся логика — в этом модуле, а хук — тонкий
// useSyncExternalStore-читатель: GET, дифф и подписки идут ОДИН раз на проект.
//
// Живое обновление (S7): хранилище испускает plugins_changed после каждой записи
// установки (install/обновление/grant/enable/uninstall/секрет) — событие без полезной
// нагрузки, клиент перечитывает GET. После обрыва (reconnected) тоже: события за время
// разрыва потеряны. Успешный GET ведёт сборку мусора по диффу: плагин, которого в новом
// списке нет, теряет свои ключи из раскладки (forgetPlugin, S3); УПАВШИЙ GET ничего не
// выносит — мимолётный сбой не сносит раскладку.
//
// «Чего-то не хватает» (для CountBadge): у плагина не выдано хотя бы одно запрошенное
// право ИЛИ не задан хотя бы один секрет. Флаг «обязательный» (required) в DTO бэка
// нет, поэтому «не задан» считаем по любому незаполненному секрету — честнее, чем
// молчать, и совпадает со статусом «Не задан секрет» на строке.

export type InstalledPluginsState = {
  plugins: PluginInfo[];
  loading: boolean;
  error: string | null;
};

type Entry = {
  projectId: string;
  state: InstalledPluginsState;
  // Последняя УСПЕШНАЯ загрузка: по её диффу идёт сборка мусора. Упавший GET её не
  // меняет — значит и раскладка при сбое не трогается.
  lastList: PluginInfo[];
  // Защита от гонок: применяется только ответ СВЕЖЕЙШЕГО запроса.
  seq: number;
  // Refcount активных потребителей (enabled=true): первый включает проект, последний —
  // выключает. Пока активен хоть один — события будят refresh, и проект не «засыпает».
  active: number;
  // Первичный GET уже ушёл (успешно или нет): повторный mount в пределах активного
  // периода не шлёт его заново. Сбрасывается, когда проект «засыпает» (active=0),
  // чтобы возврат в проект (повторный mount) перечитал список.
  started: boolean;
  listeners: Set<() => void>;
  // Стабильная подписка useSyncExternalStore (React требует неизменяемую функцию):
  // создаётся один раз на запись, а не на каждый рендер.
  subscribe: (fn: () => void) => () => void;
};

const _byProject = new Map<string, Entry>();

function entry(projectId: string): Entry {
  let e = _byProject.get(projectId);
  if (!e) {
    const listeners = new Set<() => void>();
    e = {
      projectId,
      state: { plugins: [], loading: false, error: null },
      lastList: [],
      seq: 0,
      active: 0,
      started: false,
      listeners,
      subscribe: (fn: () => void) => {
        listeners.add(fn);
        return () => { listeners.delete(fn); };
      },
    };
    _byProject.set(projectId, e);
  }
  return e;
}

function emit(e: Entry) {
  e.listeners.forEach(fn => fn());
}

// Подписки на события — ОДИН раз на модуль (не на каждый хук): plugins_changed и
// reconnected будят refresh всех АКТИВНЫХ (enabled) проектов.
let _subscribed = false;
function ensureSubscriptions() {
  if (_subscribed) return;
  _subscribed = true;
  onMessage(msg => {
    if (msg.type !== 'plugins_changed') return;
    for (const e of _byProject.values()) if (e.active > 0) void refresh(e);
  });
  onReconnected(() => {
    for (const e of _byProject.values()) if (e.active > 0) void refresh(e);
  });
}

async function refresh(e: Entry) {
  const seq = ++e.seq;
  e.state = { ...e.state, loading: true, error: null };
  emit(e);
  try {
    const list = await api.plugins.list();
    if (e.seq === seq) {
      // Не-хуковый путь (ops/getZones): refresh() идёт ПОСЛЕ await, т.е. вне рендера,
      // где wsPanels.use() (useSyncExternalStore) дал бы «Invalid hook call».
      // Сборка мусора по диффу УСПЕШНОЙ загрузки: плагин был в последней, а в новой
      // его нет — его ключи снимаем из раскладки. Только по УСПЕШНОМУ ответу: упавший
      // GET (ошибка/таймаут) ничего не выносит.
      for (const p of e.lastList) if (!list.some(n => n.id === p.id)) wsPanels.ops.forgetPlugin(p.id);
      // Итерация 2: orphan-ключи. Плагин удалён вне текущей сессии (другое окно,
      // другое устройство) и никогда не был в lastList — дифф его не видит. Сканируем
      // раскладку: ключ plugin:* чьё id не в текущем списке — сносим.
      const zones = wsPanels.getZones();
      const validIds = new Set(list.map(p => p.id));
      const allKeys = [
        ...zones.left.layout.flat(),
        ...zones.right.layout.flat(),
        ...zones.left.stash.flat(),
        ...zones.right.stash.flat(),
        ...Object.keys(zones.home),
        ...Object.keys(zones.weights),
        ...zones.tucked,
        ...zones.railOrder,
      ];
      const seen = new Set<string>();
      for (const k of allKeys) {
        if (!k.startsWith('plugin:')) continue;
        // Ключ: plugin:{publisher}/{slug}:{panelId} → pluginId = всё между 'plugin:' и последним ':'
        const lastColon = k.lastIndexOf(':');
        if (lastColon < 7) continue; // 'plugin:' = 7 символов, panelId пустой — мусор
        const pluginId = k.slice(7, lastColon);
        if (pluginId && !validIds.has(pluginId) && !seen.has(pluginId)) {
          seen.add(pluginId);
          wsPanels.ops.forgetPlugin(pluginId);
        }
      }
      e.lastList = list;
      e.state = { ...e.state, plugins: list, loading: false };
      setInstalledPlugins(e.projectId, list);
      emit(e);
    }
  } catch (err: unknown) {
    if (e.seq === seq) {
      e.state = { ...e.state, error: err instanceof Error ? err.message : 'Не удалось получить список', loading: false };
      emit(e);
    }
  }
}

// Хук: читатель общего состояния проекта. useSyncExternalStore — перерисовка только
// при emit (смена списка/ошибки/загрузки), snapshot стабилен. Пока enabled, держит
// проект АКТИВНЫМ (refcount): первый активный потребитель запускает первичный GET,
// а события будят refresh ОДИН раз на проект (не на каждый хук).
export function useInstalledPlugins(projectId: string, enabled: boolean) {
  const e = entry(projectId);

  const state = useSyncExternalStore(
    e.subscribe,
    () => e.state,
    () => e.state,
  );

  useEffect(() => {
    if (!enabled) return;
    e.active += 1;
    ensureSubscriptions();
    if (!e.started) {
      e.started = true;
      void refresh(e);
    }
    return () => {
      e.active -= 1;
      // Последний потребитель ушёл — проект «засыпает»: события его больше не будят.
      // Сбрасываем started, чтобы повторный mount (возврат в проект) послал новый GET:
      // иначе список остался бы старым (R-Финал: плагин, установленный, пока проект был
      // закрыт, не появлялся до F5).
      if (e.active === 0) e.started = false;
    };
  }, [enabled]);

  // Число плагинов, которым чего-то не хватает (для CountBadge на пункте навигации).
  const missingCount = state.plugins.filter(p =>
    p.requested.some(r => !p.granted.includes(r)) || p.secrets.some(s => !s.set),
  ).length;

  return {
    plugins: state.plugins,
    loading: state.loading,
    error: state.error,
    refresh: () => void refresh(e),
    missingCount,
  };
}

// Тест: сбросить модульное состояние (refcount, подписки, кэш) между прогонами.
export function _resetInstalledPluginsStoreForTest() {
  _byProject.clear();
  _subscribed = false;
}
