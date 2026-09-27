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

// Пункт «Устройства» в меню аватара нужен обеим граням: и клиенту рук (desktop-agent), и агенту
// локальных проектов (local-projects) — модалка сама выбирает режим по local-projects
export function devicesMenuVisible(desktopAgent: boolean, localProjects: boolean): boolean {
  return desktopAgent || localProjects;
}

export function useDevicesMenuVisible(): boolean {
  return devicesMenuVisible(useFeature(FLAGS.desktopAgent), useFeature(FLAGS.localProjects));
}

// Подсказка при пустом списке устройств. Без пункта «Устройства» в меню аватара подсказка
// обязана сначала отправить включить флаг
export function useNoDevicesHint(): string {
  const devicesMenu = useDevicesMenuVisible();
  return devicesMenu
    ? 'Нет устройств с агентом AI Home. Подключите компьютер с папкой проекта: меню аватара → «Устройства».'
    : 'Нет устройств с агентом AI Home. Включите «Десктопный агент» в меню аватара → «Эксперименты», затем подключите компьютер с папкой проекта в пункте «Устройства» того же меню.';
}
