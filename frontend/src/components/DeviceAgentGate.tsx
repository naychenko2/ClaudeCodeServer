import type { ReactNode } from 'react';
import { Loader2, MonitorOff, ShieldAlert, Unplug } from 'lucide-react';
import type { Project } from '../types';
import { useDeviceAgent, probeDeviceAgent, type DeviceAgentStatus } from '../lib/deviceAgent';
import { EmptyState, Button } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

interface Props {
  project: Project;
  children: ReactNode;
}

function describe(status: DeviceAgentStatus): { icon: ReactNode; title: string; subtitle: string } | null {
  const icon = (I: typeof Unplug) => <I size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />;
  switch (status.kind) {
    case 'ready':
      return null;
    case 'checking':
      return { icon: <Loader2 size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} style={{ animation: 'spin 1s linear infinite' }} />, title: 'Подключаемся к агенту', subtitle: 'Файлы, терминал и сервисы локального проекта открываются через агента AI Home на этом компьютере' };
    case 'unreachable':
      return { icon: icon(Unplug), title: 'Агент устройства не найден', subtitle: 'Файлы, терминал и сервисы локального проекта доступны только на его компьютере с запущенным агентом AI Home. Запустите агента или откройте проект с машины проекта' };
    case 'refused':
      return { icon: icon(ShieldAlert), title: 'Доступ к проекту на устройстве не выдан', subtitle: status.reason };
    case 'rejected':
      return { icon: icon(MonitorOff), title: 'Это не компьютер проекта', subtitle: status.reason };
  }
}

// Панель локального проекта рисуется, только когда агент на этой машине ответил и принял
// билет; до того — понятное состояние вместо пустого дерева (ADR-016, задача 4.4). Серверному
// проекту гейт прозрачен. Хуки панели живут в children — ранний выход здесь их не ломает.
export function DeviceAgentGate({ project, children }: Props) {
  const status = useDeviceAgent(project);
  const view = describe(status);
  if (!view) return <>{children}</>;
  return (
    <div role="status" data-device-agent-state={status.kind} style={{ height: '100%' }}>
      <EmptyState
        compact
        icon={view.icon}
        title={view.title}
        subtitle={view.subtitle}
        action={status.kind === 'checking' ? undefined : (
          <Button variant="secondary" size="sm" onClick={() => void probeDeviceAgent(project.id)}>
            Проверить снова
          </Button>
        )}
      />
    </div>
  );
}
