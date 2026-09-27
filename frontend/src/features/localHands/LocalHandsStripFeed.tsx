import { useEffect } from 'react';
import { onMessage } from '../../lib/signalr';
import { isFeatureAvailable } from '../../lib/projectCapabilities';
import { api } from '../../lib/api';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { ProjectFeature, type Project, type Session } from '../../types';
import {
  applyHandsFocus, handsStatusFailed, handsStatusLoaded, handsStripOnMessage, resetHandsSession,
  setHandsProject, setHandsSessionProvider,
} from './handsStrip';
import { watchHandsAvailability } from './handsStripManifest';

// Питатель полосы «Руки» (ADR-016 §7): живёт в чате, пока тот открыт, и ничего не рисует.
// Полоса рисуется только активной, поэтому запросить фокус сама не может — это делает он:
// первая отрисовка — GET /api/sessions/{id}/hands-status, дальше события hands_status.
export function LocalHandsStripFeed({ session, project }: { session: Session; project: Project }) {
  const flag = useFeature(FLAGS.localHands);
  const available = project.handsEnabled === true && isFeatureAvailable(project, ProjectFeature.Hands);
  const deviceName = project.device?.name ?? null;

  useEffect(() => {
    setHandsProject(project.id, { available: flag && available, deviceName });
  }, [project.id, flag, available, deviceName]);

  useEffect(() => { setHandsSessionProvider(session.id, session.provider ?? null); }, [session.id, session.provider]);

  useEffect(() => watchHandsAvailability(project.id, session.id), [project.id, session.id]);

  useEffect(() => {
    // Руки выключили — полоса не держит фокус
    if (!flag || !available) { applyHandsFocus(session.id, null); return; }
    resetHandsSession(session.id);
    let alive = true;
    // Подписка раньше запроса: событие между ответом и подпиской не теряется
    const off = onMessage(msg => handsStripOnMessage(session.id, msg));
    api.sessions.handsStatus(session.id)
      .then(view => { if (alive) handsStatusLoaded(session.id, view); })
      .catch(() => { if (alive) handsStatusFailed(session.id); });
    return () => { alive = false; off(); };
  }, [flag, available, session.id]);

  return null;
}
