import { useCallback, useEffect, useRef, useState } from 'react';
import { api, type PluginInfo } from '../../lib/api';
import { onMessage, onReconnected } from '../../lib/signalr';
import { wsPanels } from '../../pages/workspace/panelStackState';
import { setInstalledPlugins } from './pluginStore';

// Загрузка списка установленных плагинов (GET /api/plugins) + производные для UI.
// Самодостаточный хук: секция «Плагины» в настройках проекта (и слот панели)
// берут данные отсюда.
//
// Живое обновление (S7): хранилище плагинов испускает plugins_changed после
// каждой записи установки (install/обновление/grant/enable/uninstall/секрет) —
// событие без полезной нагрузки, клиент перечитывает GET /api/plugins. После
// обрыва (reconnected) список тоже перечитывается: события за время разрыва
// потеряны. Успешный GET ведёт сборку мусора по диффу: плагин, которого в
// новом списке нет, теряет свои ключи из раскладки (forgetPluginKeys, S3);
// УПАВШИЙ GET ничего не выносит — мимолётный сбой не сносит раскладку.
//
// «Чего-то не хватает» (для CountBadge): у плагина не выдано хотя бы одно запрошенное
// право ИЛИ не задан хотя бы один секрет. Флаг «обязательный» (required) в DTO бэка
// нет, поэтому «не задан» считаем по любому незаполненному секрету — честнее, чем
// молчать, и совпадает со статусом «Не задан секрет» на строке.
export function useInstalledPlugins(projectId: string, enabled: boolean) {
  const [plugins, setPlugins] = useState<PluginInfo[]>([]);
  const [loading, setLoading] = useState(enabled);
  const [error, setError] = useState<string | null>(null);
  const seqRef = useRef(0);
  // Последняя УСПЕШНАЯ загрузка: по её диффу идёт сборка мусора. Упавший GET
  // её не меняет — значит и раскладка при сбое не трогается.
  const lastListRef = useRef<PluginInfo[]>([]);
  // Стор раскладки воркспейса: ключи панелей плагинов (plugin:{...}) живут только
  // в нём (у других экранов маркера pluginPanels в allowedKeys нет).
  const { forgetPlugin } = wsPanels.use();

  const refresh = useCallback(async () => {
    const seq = ++seqRef.current;
    setLoading(true);
    setError(null);
    try {
      const list = await api.plugins.list();
      if (seqRef.current === seq) {
        // Сборка мусора по диффу живого списка: плагин был в последней успешной
        // загрузке, а в новой его нет — его ключи снимаем из раскладки.
        // Только по УСПЕШНОМУ ответу: упавший GET (ошибка/таймаут) ничего не выносит.
        for (const p of lastListRef.current) {
          if (!list.some(n => n.id === p.id)) forgetPlugin(p.id);
        }
        lastListRef.current = list;
        setPlugins(list);
        setInstalledPlugins(projectId, list);
      }
    } catch (e: unknown) {
      if (seqRef.current === seq) setError(e instanceof Error ? e.message : 'Не удалось получить список');
    } finally {
      if (seqRef.current === seq) setLoading(false);
    }
  }, [projectId, forgetPlugin]);

  useEffect(() => {
    if (!enabled) return;
    void refresh();
  }, [enabled, refresh]);

  // plugins_changed (S6): хранилище испускает его владельцу после каждой записи
  // установки — перечитываем список. Полезной нагрузки в событии нет.
  useEffect(() => {
    if (!enabled) return;
    return onMessage(msg => {
      if (msg.type === 'plugins_changed') void refresh();
    });
  }, [enabled, refresh]);

  // Reconnected: за время обрыва события терялись — перечитываем список. Если
  // версия/права сменились, фреймы пересоздадутся сами (ключ фрейма их несёт).
  useEffect(() => {
    if (!enabled) return;
    return onReconnected(() => { void refresh(); });
  }, [enabled, refresh]);

  // Число плагинов, которым чего-то не хватает (для CountBadge на пункте навигации).
  const missingCount = plugins.filter(p =>
    p.requested.some(r => !p.granted.includes(r)) || p.secrets.some(s => !s.set),
  ).length;

  return { plugins, loading, error, refresh, missingCount };
}
