import type { DesktopDevice } from '../../types';
import { FLAGS, useFeature } from '../../lib/featureFlags';

// Устройство годится для локального проекта, только если оно живое (не отозвано) и
// умеет exec (агент локальных проектов). capabilities у старого бэка нет — трактуем
// как «exec неизвестен» и не показываем (безопасный дефолт). Общий фильтр для
// «Добавить проект» и секции «Устройство» в настройках — иначе они расходятся
export function isLocalProjectDevice(d: DesktopDevice): boolean {
  return !d.revoked && d.capabilities?.exec === true;
}

// Подпись устройства в пикере: имя, платформа, признак «не в сети»
export function deviceLabel(d: DesktopDevice): string {
  return `${d.name}${d.platform ? ` (${d.platform})` : ''}${d.online ? '' : ' · не в сети'}`;
}

// Подсказка при пустом списке устройств. Пункт «Устройства» живёт в меню аватара и только
// под флагом desktop-agent: без флага подсказка обязана сначала отправить включить его
export function useNoDevicesHint(): string {
  const devicesMenu = useFeature(FLAGS.desktopAgent);
  return devicesMenu
    ? 'Нет устройств с агентом AI Home. Подключите компьютер с папкой проекта: меню аватара → «Устройства».'
    : 'Нет устройств с агентом AI Home. Включите «Десктопный агент» в меню аватара → «Эксперименты», затем подключите компьютер с папкой проекта в пункте «Устройства» того же меню.';
}
