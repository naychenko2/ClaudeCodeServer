import { useState, type ReactNode } from 'react';
import { Loader2, MonitorOff, Plug, ShieldAlert, Unplug } from 'lucide-react';
import type { Project } from '../types';
import { useDeviceAgent, probeDeviceAgent, type DeviceAgentStatus } from '../lib/deviceAgent';
import { isRootNotAllowed } from '../lib/agentInstall';
import { FLAGS, useFeature } from '../lib/featureFlags';
import { SP } from '../lib/design';
import { EmptyState, Button } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { CapabilityUnavailable } from './CapabilityGate';
import { DevicesModal } from '../features/desktop/DevicesModal';
import { RootsAddHint } from '../features/desktop/AgentCommands';

interface Props {
  project: Project;
  children: ReactNode;
  // Панель умеет работать через ретранслятор (файлы, изменения): с другого устройства она
  // открывается только на чтение. Значение — заголовок плашки, когда устройство файлы не отдаёт
  relayTitle?: string;
}

type View = { icon: ReactNode; title: string; subtitle: string } | null;

function describe(status: DeviceAgentStatus, agentMode: boolean): View {
  const icon = (I: typeof Unplug) => <I size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />;
  switch (status.kind) {
    case 'ready':
      return null;
    case 'checking':
      return { icon: <Loader2 size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} style={{ animation: 'spin 1s linear infinite' }} />, title: 'Ищем агент AI Home на этом компьютере', subtitle: 'Файлы, терминал и сервисы локального проекта открываются через агента AI Home на этом компьютере' };
    case 'unreachable':
      return { icon: icon(Unplug), title: 'Агент AI Home на этом компьютере не найден', subtitle: agentMode
        ? 'Файлы, терминал и сервисы локального проекта доступны на компьютере с агентом AI Home. Поставьте агента: Устройства → Подключить компьютер — или откройте проект с машины проекта'
        : 'Файлы, терминал и сервисы локального проекта доступны только на его компьютере с запущенным агентом AI Home. Запустите агента или откройте проект с машины проекта' };
    case 'refused':
      return { icon: icon(ShieldAlert), title: 'Доступ к проекту на устройстве не выдан', subtitle: status.reason };
    case 'rejected':
      return { icon: icon(MonitorOff), title: 'Это не компьютер проекта', subtitle: status.reason };
    // Панель без чтения через сервер (терминал, сервисы, навыки) с другого устройства не работает
    case 'relay':
    case 'relay-unavailable':
      return { icon: icon(MonitorOff), title: 'Только на компьютере проекта', subtitle: 'Терминал, сервисы и навыки локального проекта работают на его компьютере. С этого устройства доступны файлы и изменения — только для просмотра' };
  }
}

// Панель локального проекта рисуется, только когда устройство отдаёт её данные: с машины проекта —
// агент ответил и принял билет (ADR-016, задача 4.4), с другого устройства — через ретранслятор
// сервера, если панель его умеет (задача 5.2). До того — понятное состояние вместо пустого дерева.
// Серверному проекту гейт прозрачен. Хуки панели живут в children — ранний выход здесь их не ломает.
export function DeviceAgentGate({ project, children, relayTitle }: Props) {
  const status = useDeviceAgent(project);
  const agentMode = useFeature(FLAGS.localProjects);
  const [devicesOpen, setDevicesOpen] = useState(false);
  const retry = (
    <Button variant="secondary" size="sm" onClick={() => void probeDeviceAgent(project.id)}>
      Проверить снова
    </Button>
  );
  const reason = 'reason' in status ? status.reason : null;
  // Агент отказал: папка проекта не под разрешёнными корнями машины — даём готовую команду
  const rootsHint = agentMode && isRootNotAllowed(reason);
  const action = rootsHint
    ? (
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.md, maxWidth: 420 }}>
        <RootsAddHint rootPath={project.rootPath} platform={project.device?.platform} bare />
        {retry}
      </div>
    )
    : retry;

  if (relayTitle !== undefined && status.kind === 'relay') return <>{children}</>;
  // Устройство не в сети или агент без ретранслятора — та же плашка «недоступно с причиной», что у матрицы
  if (relayTitle !== undefined && status.kind === 'relay-unavailable')
    return <CapabilityUnavailable feature="relay" title={relayTitle} reason={status.reason} action={action} />;
  const view = describe(status, agentMode);
  if (!view) return <>{children}</>;
  const unreachableAction = agentMode && status.kind === 'unreachable'
    ? (
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
        <Button variant="primary" size="sm" leftIcon={<Plug size={14} strokeWidth={2.2} />} onClick={() => setDevicesOpen(true)}>
          Подключить компьютер
        </Button>
        {retry}
      </div>
    )
    : null;
  return (
    <div role="status" data-device-agent-state={status.kind} style={{ height: '100%' }}>
      <EmptyState
        compact
        icon={view.icon}
        title={rootsHint ? 'Папка проекта не разрешена агенту' : view.title}
        subtitle={rootsHint ? 'Агент на этом компьютере открывает только папки, которые вы разрешили сами' : view.subtitle}
        action={status.kind === 'checking' ? undefined : unreachableAction ?? action}
      />
      {devicesOpen && <DevicesModal onClose={() => setDevicesOpen(false)} />}
    </div>
  );
}
