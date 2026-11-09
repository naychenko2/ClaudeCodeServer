import type { DesktopDevice } from '../../types';

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

// Подсказка при пустом списке устройств
export const NO_DEVICES_HINT =
  'Нет устройств с агентом AI Home. Подключите компьютер с папкой проекта: меню аватара → «Устройства».';

// Пункт «Устройства» в меню аватара — безусловно: фича local-projects снята с флагом и
// работает у всех, иначе скрывать нечего
export function useDevicesMenuVisible(): boolean {
  return true;
}
