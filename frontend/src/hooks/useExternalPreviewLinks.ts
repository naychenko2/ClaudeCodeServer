// Ссылки внешнего доступа к дев-серверам (поддомен, а не путь /preview/**).
//
// Список СКВОЗНОЙ по проектам владельца, хотя живёт в проектной панели: забытая открытой
// витрина в соседнем проекте иначе осталась бы невидимой, а это ровно тот случай, ради
// которого список и нужен.
import { useCallback, useEffect, useState } from 'react';
import type { ExternalPreviewLink } from '../types';
import { api } from '../lib/api';

export function useExternalPreviewLinks() {
  // Пока не знаем — считаем выключенным: лучше не показать кнопку, чем показать нерабочую
  const [enabled, setEnabled] = useState(false);
  const [links, setLinks] = useState<ExternalPreviewLink[]>([]);

  const refresh = useCallback(async () => {
    try {
      const r = await api.externalPreview.list();
      setEnabled(r.enabled);
      setLinks(r.links);
    } catch {
      // офлайн — оставляем прежнее состояние, кнопки не мигают
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  // Отзыв показываем сразу, не дожидаясь сервера: доступ закрывается мгновенно, и
  // задержка в интерфейсе выглядела бы так, будто ссылка ещё жива
  const revoke = useCallback(async (jti: string) => {
    setLinks(prev => prev.filter(l => l.jti !== jti));
    try { await api.externalPreview.revoke(jti); } catch { void refresh(); }
  }, [refresh]);

  const revokeAll = useCallback(async () => {
    setLinks([]);
    try { await api.externalPreview.revokeAll(); } catch { void refresh(); }
  }, [refresh]);

  return { enabled, links, refresh, revoke, revokeAll };
}
