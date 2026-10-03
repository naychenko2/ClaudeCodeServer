import { useEffect } from 'react';
import { onMessage } from '../../lib/signalr';
import { api } from '../../lib/api';
import type { Project, Session } from '../../types';
import {
  handsProjectInfo, handsStatusFailed, handsStatusLoaded, handsStripOnMessage, resetHandsSession,
  setHandsProject, setHandsSessionProvider,
} from './handsStrip';
// Побочный эффект: модуль регистрирует вклад «Руки» (пилюля composer-chip) — питатель его и подключает
import './handsStripManifest';

// Питатель состояния рук (ADR-016 §7): живёт в чате, пока тот открыт, и ничего не рисует.
// Первая отрисовка — GET /api/sessions/{id}/hands-status, дальше события hands_status.
export function LocalHandsStripFeed({ session, project }: { session: Session; project: Project }) {
  const { available, deviceName } = handsProjectInfo(project);

  useEffect(() => {
    setHandsProject(project.id, { available, deviceName });
  }, [project.id, available, deviceName]);

  useEffect(() => { setHandsSessionProvider(session.id, session.provider ?? null); }, [session.id, session.provider]);

  useEffect(() => {
    if (!available) return;
    resetHandsSession(session.id);
    let alive = true;
    // Подписка раньше запроса: событие между ответом и подпиской не теряется
    const off = onMessage(msg => handsStripOnMessage(session.id, msg));
    api.sessions.handsStatus(session.id)
      .then(view => { if (alive) handsStatusLoaded(session.id, view); })
      .catch(() => { if (alive) handsStatusFailed(session.id); });
    return () => { alive = false; off(); };
  }, [available, session.id]);

  return null;
}
