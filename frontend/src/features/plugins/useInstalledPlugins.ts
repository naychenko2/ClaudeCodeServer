import { useCallback, useEffect, useRef, useState } from 'react';
import { api, type PluginInfo } from '../../lib/api';
import { setInstalledPlugins } from './pluginStore';

// Загрузка списка установленных плагинов (GET /api/plugins) + производные для UI.
// Самодостаточный хук: секция «Плагины» в настройках проекта (и позже слот панели)
// берут данные отсюда.
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

  const refresh = useCallback(async () => {
    const seq = ++seqRef.current;
    setLoading(true);
    setError(null);
    try {
      const list = await api.plugins.list();
      if (seqRef.current === seq) {
        setPlugins(list);
        setInstalledPlugins(projectId, list);
      }
    } catch (e: unknown) {
      if (seqRef.current === seq) setError(e instanceof Error ? e.message : 'Не удалось получить список');
    } finally {
      if (seqRef.current === seq) setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    if (!enabled) return;
    void refresh();
  }, [enabled, refresh]);

  // Число плагинов, которым чего-то не хватает (для CountBadge на пункте навигации).
  const missingCount = plugins.filter(p =>
    p.requested.some(r => !p.granted.includes(r)) || p.secrets.some(s => !s.set),
  ).length;

  return { plugins, loading, error, refresh, missingCount };
}
