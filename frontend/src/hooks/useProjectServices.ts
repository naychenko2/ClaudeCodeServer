// Дев-сервисы проекта (панель «Сервисы»): список + запуск/остановка + live-статусы.
// Вынесено из WorkspacePage; воркспейсная навигация (переключение вкладки
// инструментов при старте) осталась у вызывающего — колбэк opts.onStarted.
import { useCallback, useEffect, useState } from 'react';
import type { ProjectService } from '../types';
import { api } from '../lib/api';
import { onMessage } from '../lib/signalr';

export function useProjectServices(projectId: string, opts?: { onStarted?: (svc: ProjectService) => void }) {
  const [services, setServices] = useState<ProjectService[]>([]);
  const [activePreviewId, setActivePreviewId] = useState<string | null>(null);
  const onStarted = opts?.onStarted;

  const refresh = useCallback(async () => {
    try {
      const r = await api.projects.services(projectId);
      setServices(r.services);
      if (r.activeServiceId) setActivePreviewId(r.activeServiceId);
    } catch { /* офлайн — оставляем как есть */ }
  }, [projectId]);

  const start = useCallback(async (svc: ProjectService) => {
    setServices(prev => prev.map(s => s.id === svc.id ? { ...s, status: 'starting', error: null } : s));
    setActivePreviewId(svc.id);
    onStarted?.(svc);
    try {
      const r = await api.projects.previewStart(projectId, {
        serviceId: svc.id, name: svc.name, command: svc.command, args: svc.args,
        cwd: svc.cwd ?? undefined, port: svc.suggestedPort ?? undefined, autoPort: svc.autoPort,
      });
      setServices(prev => prev.map(s => s.id === svc.id
        ? { ...s, status: r.status, runningPort: r.port ?? null, error: r.error ?? null }
        : s));
    } catch {
      setServices(prev => prev.map(s => s.id === svc.id ? { ...s, status: 'error' } : s));
    }
  }, [projectId, onStarted]);

  const stop = useCallback(async (serviceId: string) => {
    try { await api.projects.previewStop(projectId, serviceId); } catch { /* ignore */ }
    setServices(prev => prev.map(s => s.id === serviceId ? { ...s, status: 'stopped', runningPort: null } : s));
  }, [projectId]);

  // Живой статус сервисов из broadcast preview_status (группа user_*)
  useEffect(() => {
    return onMessage(msg => {
      if (msg.type !== 'preview_status' || !msg.serviceId) return;
      const sid = msg.serviceId;
      setServices(prev => prev.map(s => s.id === sid
        ? { ...s, status: msg.status, runningPort: msg.port ?? s.runningPort, error: msg.error ?? null }
        : s));
    });
  }, []);

  return { services, activePreviewId, setActivePreviewId, refresh, start, stop };
}
