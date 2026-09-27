import { useEffect, useState } from 'react';
import { MonitorSmartphone } from 'lucide-react';
import { interruptSession, onMessage } from '../../lib/signalr';
import { isFeatureAvailable } from '../../lib/projectCapabilities';
import { handsBadgeView, type HandsStatusSnapshot } from '../../lib/localHands';
import { useIsMobile } from '../../lib/breakpoints';
import { showToast } from '../../lib/toast';
import { Badge, Button } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { ProjectFeature, type Project, type Session } from '../../types';

// Бейдж рук локального проекта в шапке чата (ADR-016 §7). Внешне — как HandsBadge
// десктопного чата (та же пилюля и иконка), логика своя:
//  • нет «Попросить руки» — руки включает тумблер проекта, заявки с веба нет;
//  • состояние — событие hands_status, а не опрос; ручки начального состояния пока нет,
//    поэтому до первого события бейдж нейтральный, а не «активен»;
//  • «Стоп» прерывает ход тем же механизмом, что кнопка остановки в композере.
// Флаг local-hands проверяет вызывающий; бейдж есть, только пока руки включены и доступны.
export function LocalHandsBadge({ session, project, compact }: {
  session: Session;
  project: Project | null | undefined;
  compact: boolean;
}) {
  const isMobile = useIsMobile();
  const [status, setStatus] = useState<HandsStatusSnapshot | null>(null);
  const [busy, setBusy] = useState(false);
  const shown = project?.handsEnabled === true && isFeatureAvailable(project, ProjectFeature.Hands);

  useEffect(() => {
    if (!shown) return;
    return onMessage(msg => {
      if (msg.type !== 'hands_status' || msg.sessionId !== session.id) return;
      setStatus({ state: msg.state, deviceName: msg.deviceName, reason: msg.reason });
    });
  }, [shown, session.id]);

  if (!shown) return null;

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
