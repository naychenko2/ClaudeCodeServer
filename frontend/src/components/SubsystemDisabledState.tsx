import { PowerOff } from 'lucide-react';
// Прямой файл, не './ui' (индекс): индекс тянет весь кит — PanelShell → lib/pointer
// с window.addEventListener на уровне модуля, и импорт через индекс в App ломает
// загрузку App.deeplink.test.ts (там window — plain-объект без addEventListener)
// и излишне утяжеляет бандл каркаса.
import { EmptyState } from './ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

interface Props {
  // Имя раздела: «Заметки», «Задачи»… Без него заголовок общий.
  title?: string;
}

// Заглушка раздела, чью подсистему выключил администратор (фича «Компоненты», S2b-3b).
// Стоит на EmptyState из components/ui: с title заголовок называет раздел
// («Раздел «Заметки» выключен…»), без него — общий. Подсказка — где включить.
// Действия под подсказкой здесь нет: маршрут в «Компоненты» для не-администратора
// определяет встраивание (S2b-3c), не этот компонент.
export function SubsystemDisabledState({ title }: Props) {
  return (
    <EmptyState
      icon={<PowerOff size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
      title={title ? `Раздел «${title}» выключен администратором` : 'Раздел выключен администратором'}
      subtitle="Включить его можно в «Компонентах» у администратора"
    />
  );
}
