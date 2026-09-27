import { useEffect, useState } from 'react';
import { MonitorSmartphone } from 'lucide-react';
import { interruptSession, onMessage } from '../../lib/signalr';
import { isFeatureAvailable } from '../../lib/projectCapabilities';
import {
  HANDS_BADGE_LOADING, handsBadgeStatus, handsBadgeView, handsEventReceived, handsInitialFailed,
  handsInitialLoaded, type HandsBadgeState,
} from '../../lib/localHands';
import { api } from '../../lib/api';
import { useIsMobile } from '../../lib/breakpoints';
import { showToast } from '../../lib/toast';
import { Badge, Button } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { ProjectFeature, type Project, type Session } from '../../types';

// Бейдж рук локального проекта в шапке чата (ADR-016 §7). Внешне — как HandsBadge
// десктопного чата (та же пилюля и иконка), логика своя:
//  • нет «Попросить руки» — руки включает тумблер проекта, заявки с веба нет;
//  • первая отрисовка — GET /api/sessions/{id}/hands-status, дальше события hands_status;
//    нейтральный вид — только пока ответ грузится или если запрос упал;
//  • «Стоп» прерывает ход тем же механизмом, что кнопка остановки в композере.
// Флаг local-hands проверяет вызывающий; бейдж есть, только пока руки включены и доступны.
export function LocalHandsBadge({ session, project, compact }: {
  session: Session;
  project: Project | null | undefined;
  compact: boolean;
}) {
  const isMobile = useIsMobile();
  const [state, setState] = useState<HandsBadgeState>(HANDS_BADGE_LOADING);
  const [busy, setBusy] = useState(false);
  const shown = project?.handsEnabled === true && isFeatureAvailable(project, ProjectFeature.Hands);

  useEffect(() => {
    if (!shown) return;
    setState(HANDS_BADGE_LOADING);
    let alive = true;
    // Подписка раньше запроса: событие между ответом и подпиской не теряется
    const off = onMessage(msg => {
      if (msg.type !== 'hands_status' || msg.sessionId !== session.id) return;
      setState(handsEventReceived({ state: msg.state, deviceName: msg.deviceName, reason: msg.reason }));
    });
    api.sessions.handsStatus(session.id)
      .then(view => { if (alive) setState(prev => handsInitialLoaded(prev, view)); })
      .catch(() => { if (alive) setState(handsInitialFailed); });
    return () => { alive = false; off(); };
  }, [shown, session.id]);

  if (!shown || state.kind === 'none') return null;

  const status = handsBadgeStatus(state);
  const view = handsBadgeView(status, project?.device?.name ?? null);

  const stop = async () => {
    setBusy(true);
    try { await interruptSession(session.id); }
    catch { /* ход мог закончиться сам — следующее событие покажет */ }
    finally { setBusy(false); }
  };

  return (
    <span data-local-hands-badge={status?.state ?? 'initial'}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0, minWidth: 0 }}>
      <Badge size="xs" tone={view.tone} title={view.title}
        icon={<MonitorSmartphone size={11} strokeWidth={ICON_STROKE} aria-hidden />}
        // На мобиле title не работает — тот же текст по тапу
        onClick={isMobile ? () => showToast(view.text, view.title) : undefined}>
        {compact ? view.short : view.text}
      </Badge>
      {view.canStop && (
        <Button variant="ghost" size="xs" loading={busy} onClick={() => void stop()}
          title="Прервать ход: окна, открытые ходом, закроет агент устройства">
          Стоп
        </Button>
      )}
    </span>
  );
}
