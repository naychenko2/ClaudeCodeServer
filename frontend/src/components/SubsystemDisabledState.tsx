import { PowerOff } from 'lucide-react';
// Прямой файл, не './ui' (индекс): индекс тянет весь кит — PanelShell → lib/pointer
// с window.addEventListener на уровне модуля, и импорт через индекс в App ломает
// загрузку App.deeplink.test.ts (там window — plain-объект без addEventListener)
// и излишне утяжеляет бандл каркаса.
import { EmptyState } from './ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { SUBSYSTEM_DISABLED_REASON } from '../lib/subsystems';

interface Props {
  // Имя раздела: «Заметки», «Задачи»… Без него заголовок общий.
  title?: string;
}

// Заглушка раздела, чью подсистему выключил администратор (фича «Компоненты», S2b-3b).
// Стоит на EmptyState из components/ui: с title заголовок называет раздел
// («Раздел «Заметки» выключен…»), без него — общий. Формулировка причины —
// из SUBSYSTEM_DISABLED_REASON (единый источник, п. 1a), литерала здесь нет.
// Подсказка — где включить.
// Действия под подсказкой здесь нет: маршрут в «Компоненты» для не-администратора
// определяет встраивание (S2b-3c), не этот компонент.
// Обёртка role="status" + data-subsystem-disabled — как у соседних гейтов
// (CapabilityGate, DeviceAgentGate): скринридер объявляет смену состояния,
// тестам встраивания есть за что цепляться.
export function SubsystemDisabledState({ title }: Props) {
  return (
    <div role="status" data-subsystem-disabled={title ?? ''} style={{ height: '100%' }}>
      <EmptyState
        icon={<PowerOff size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title={title ? `Раздел «${title}» ${SUBSYSTEM_DISABLED_REASON}` : `Раздел ${SUBSYSTEM_DISABLED_REASON}`}
        subtitle="Включить его можно в «Компонентах» у администратора"
      />
    </div>
  );
}
