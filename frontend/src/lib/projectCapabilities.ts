// ЕДИНСТВЕННАЯ точка правды о локальности проекта и доступности подсистем (ADR-016 §4).
// Источник истины — матрица `Project.capabilities`, которую бэк собирает из
// ProjectCapabilities.cs (DTO). Панели спрашивают «есть ли мой ключ в features и
// доступна ли группа», а НЕ проверяют deviceId руками.
//
// Сторож G10 (ProjectCapabilitiesGuardTests) разрешает обращения к project.deviceId
// ТОЛЬКО в этом файле — любая такая проверка в другом месте краснеет. Разрешённые
// формы здесь — через тернарник и условный оператор (без сравнения с null/undefined и
// без double-bang). Проверки на «локальность» живут в `isLocalProject`, остальной код
// берёт матрицу через getProjectCapabilities.

import { useMemo } from 'react';
import type {
  Project,
  ProjectCapabilitiesView,
  ProjectFeatureKey,
  ProjectCapabilityGroup,
} from '../types';
import { ProjectFeature } from '../types';
import { agentSupports, relaySupports, type ProjectRoute } from './deviceAgentRoutes';

// Состав групп возможностей — зеркало ProjectFeatures.FileBound/Platform/ServerContent
// на бэке. Менять синхронно с ProjectFeatures.cs при расширении.
const FILE_FEATURES: ProjectFeatureKey[] = [
  ProjectFeature.Files,
  ProjectFeature.Diff,
  ProjectFeature.Git,
  ProjectFeature.FileWatcher,
  ProjectFeature.Terminal,
  ProjectFeature.DevServers,
  ProjectFeature.Skills,
  ProjectFeature.Attachments,
];
const PLATFORM_FEATURES: ProjectFeatureKey[] = [
  ProjectFeature.Chat,
  ProjectFeature.History,
  ProjectFeature.Tasks,
  ProjectFeature.Memory,
  ProjectFeature.Personas,
  ProjectFeature.Notes,
  ProjectFeature.Costs,
  ProjectFeature.Tts,
];
const SERVER_CONTENT_FEATURES: ProjectFeatureKey[] = [
  ProjectFeature.Knowledge,
  ProjectFeature.CodeGraph,
  ProjectFeature.Dossiers,
  ProjectFeature.Docs,
  ProjectFeature.MapHygiene,
];
const TRANSCRIPT_FEATURES: ProjectFeatureKey[] = [
  ProjectFeature.LiveSubagents,
  ProjectFeature.WorkflowView,
  ProjectFeature.ChatBranch,
];

// Серверный проект без capabilities (старый бэк): всё доступно, матрица пустая.
// Это ЕДИНСТВЕННОЕ место, где мы «придумываем» матрицу; остальной код видит то,
// что вернёт getProjectCapabilities, и больше про deviceId не спрашивает.
// Группа «всё доступно, на сервере» — дефолт для проекта без capabilities (старый бэк
// или редкий код-путь) и для серверных проектов с заполненной матрицей (в этом случае
// бэк сам выставляет host='server'/available=true).
function makeServerGroup(features: ProjectFeatureKey[]): ProjectCapabilityGroup {
  return { host: 'server', available: true, reason: null, features };
}

// Возвращает матрицу возможностей: готовый `project.capabilities` или вырожденный
// «всё на сервере» для совместимости со старым бэком. Никаких выводов про deviceId —
// только матрица.
export function getProjectCapabilities(project: Project | null | undefined): ProjectCapabilitiesView {
  if (project?.capabilities) {
    // Бэк без группы транскрипта — серверный транскрипт, как было до неё
    return project.capabilities.transcript
      ? project.capabilities
      : { ...project.capabilities, transcript: makeServerGroup(TRANSCRIPT_FEATURES) };
  }
  // Совместимость со старым бэком: трактуем проект как серверный с полным набором.
  return {
    host: 'server',
    deviceId: null,
    files: makeServerGroup(FILE_FEATURES),
    platform: makeServerGroup(PLATFORM_FEATURES),
    serverContent: makeServerGroup(SERVER_CONTENT_FEATURES),
    transcript: makeServerGroup(TRANSCRIPT_FEATURES),
    exec: { available: true, reason: null },
  };
}

// В какой группе живёт ключ. Возвращает группу или null для незнакомого ключа.
// Сторож G10 разрешает в этом файле ВСЕ формы обращения к deviceId — потому проверки
// локальности тут, а в панелях только host/features/reason из матрицы.
function findGroup(cap: ProjectCapabilitiesView, feature: ProjectFeatureKey): ProjectCapabilityGroup | null {
  if (FILE_FEATURES.includes(feature)) return cap.files;
  if (PLATFORM_FEATURES.includes(feature)) return cap.platform;
  if (SERVER_CONTENT_FEATURES.includes(feature)) return cap.serverContent;
  if (TRANSCRIPT_FEATURES.includes(feature)) return cap.transcript ?? makeServerGroup(TRANSCRIPT_FEATURES);
  return null;
}

// Доступна ли возможность прямо сейчас. Панель решает «рисовать / показать причину»
// по этой функции. Не знаем ключа — false (защита от тихих регрессий при расширении
// списка ключей на бэке).
export function isFeatureAvailable(project: Project | null | undefined, feature: ProjectFeatureKey): boolean {
  const cap = getProjectCapabilities(project);
  const group = findGroup(cap, feature);
  if (!group) return false;
  return group.available && group.features.includes(feature);
}

// Готовый текст причины недоступности (null если доступно). UI показывает его как
// tooltip к скрытой панели/кнопке.
export function featureReason(project: Project | null | undefined, feature: ProjectFeatureKey): string | null {
  const cap = getProjectCapabilities(project);
  const group = findGroup(cap, feature);
  if (!group) return 'Возможность недоступна в этой версии';
  if (!group.features.includes(feature)) return null; // ключ не в этой группе — не показываем
  if (!group.available) return group.reason;
  return null;
}

// Куда ходят запросы файлов, git, сервисов и навыков проекта: на сервер, в агента устройства на этой
// машине или через ретранслятор сервера (проект открыт с другого устройства, только чтение).
// ЕДИНСТВЕННОЕ место этого решения — api.ts берёт его отсюда через deviceAgent.ts.
export type ProjectFilesRoute = 'server' | 'agent' | 'relay';

// Где браузер относительно машины проекта. Узнать это можно только у агента: here — агент на этом
// компьютере ответил; elsewhere — не ответил или это агент другого устройства; unknown — ещё не
// спрашивали (тогда сначала пробуем агента). Помнит ответ deviceAgent.ts
export type DevicePresence = 'unknown' | 'here' | 'elsewhere';

export function projectFilesRoute(project: Project | null | undefined, presence: DevicePresence = 'unknown'): ProjectFilesRoute {
  if (getProjectCapabilities(project).host !== 'device') return 'server';
  return presence === 'elsewhere' ? 'relay' : 'agent';
}

// Есть ли действие на маршруте: у сервера — все, у агента — его список, у ретранслятора — только
// чтение из RELAY_ROUTES (deviceAgentRoutes.ts). Кнопку без маршрута панель прячет — так и
// получается «ни одного контрола записи» с другого устройства без проверок в самих панелях
export function routeSupports(filesRoute: ProjectFilesRoute, route: ProjectRoute): boolean {
  if (filesRoute === 'server') return true;
  return filesRoute === 'relay' ? relaySupports(route) : agentSupports(route);
}

export function projectSupportsRoute(project: Project | null | undefined, route: ProjectRoute, presence: DevicePresence = 'unknown'): boolean {
  return routeSupports(projectFilesRoute(project, presence), route);
}

// Почему действия нет у проекта (null — есть). Текст — для title недоступной кнопки.
const ROUTE_REASONS: Partial<Record<ProjectRoute, string>> = {
  'POST preview/external-link': 'Доступ снаружи идёт через поддомен сервера, а сервис локального проекта работает на его компьютере',
};
export const DEVICE_ROUTE_REASON = 'У локального проекта это действие недоступно: агент устройства его не умеет';
export const RELAY_ROUTE_REASON = 'С другого устройства локальный проект открыт только для просмотра: изменить его можно на компьютере проекта';

export function projectRouteReason(project: Project | null | undefined, route: ProjectRoute, presence: DevicePresence = 'unknown'): string | null {
  const filesRoute = projectFilesRoute(project, presence);
  if (routeSupports(filesRoute, route)) return null;
  if (filesRoute === 'relay') return RELAY_ROUTE_REASON;
  return ROUTE_REASONS[route] ?? DEVICE_ROUTE_REASON;
}

// Где работает ключ — сервер/устройство/выключено. Полезно для бейджей («на устройстве»).
export function hostFor(project: Project | null | undefined, feature: ProjectFeatureKey): 'server' | 'device' | 'off' {
  const cap = getProjectCapabilities(project);
  const group = findGroup(cap, feature);
  if (!group) return 'off';
  return group.host;
}

// Можно ли запускать ход прямо сейчас (chat send). reason — готовый текст «Устройство
// офлайн» и т.п., который UI кладёт в баннер отправки и в карточку ошибки.
export function canRunTurn(project: Project | null | undefined): { available: boolean; reason: string | null } {
  const cap = getProjectCapabilities(project);
  return cap.exec;
}

// Это ЛОКАЛЬНЫЙ проект (привязан к устройству). Единственная функция с этим именем
// во всём фронте; через неё идёт любой показ «локальности» (бейдж, заголовок,
// маршрутизация ассистента). Вне этого файла её не зовут.
export function isLocalProject(project: Project | null | undefined): boolean {
  // Прямое обращение к deviceId допустимо ТОЛЬКО в этом файле (сторож G10). Условие
  // без сравнения с null и без double-bang шаблон не ловит — ловит только формы,
  // указанные в самом тесте сторожа.
  return project?.deviceId ? true : false;
}

// Хук для компонентов: стабильная обёртка над getProjectCapabilities. Сейчас без
// подписки — матрица приходит с проектом в REST-ответе и обновляется целиком при
// refresh. Если позже понадобится реактивный стор (событие device_online_changed),
// точка расширения здесь, не в компонентах.
export function useProjectCapabilities(project: Project | null | undefined): ProjectCapabilitiesView {
  return useMemo(() => getProjectCapabilities(project), [project?.capabilities, project?.deviceId]);
}

// Удобный хук-чекер возможности — для частого использования в JSX
// (useProjectFeature(project, 'files') вместо isFeatureAvailable(project, 'files')).
export function useProjectFeature(project: Project | null | undefined, feature: ProjectFeatureKey): boolean {
  return useMemo(() => isFeatureAvailable(project, feature), [project?.capabilities, project?.deviceId, feature]);
}

// Бейдж «устройство офлайн» / «не готово»: project.device?.online=false или
// harnessReady=false. Возвращает короткую строку для UI; null если всё хорошо.
// Зовётся в шапке чата и в диалогах — НЕ в панелях (у панелей свои причины через
// featureReason).
export function deviceOfflineLabel(project: Project | null | undefined): string | null {
  if (!project?.device) return null;
  if (!project.device.online) return 'Устройство офлайн';
  if (!project.device.harnessReady) {
    return project.device.harnessProblem ?? 'Агент устройства не готов';
  }
  return null;
}

// Бейдж локального проекта для шапки чата: «на устройстве · имя · офлайн». null — проект
// серверный. offline — устройство не в сети или агент не готов: тогда бейдж предупреждает,
// а в title полная причина из deviceOfflineLabel. short — для узкой шапки, где длинный
// текст обрезался бы как раз на состоянии
export function projectDeviceBadge(project: Project | null | undefined): { text: string; short: string; offline: boolean; title: string } | null {
  if (!isLocalProject(project)) return null;
  const problem = deviceOfflineLabel(project);
  const name = project?.device?.name;
  const state = !problem ? null : project?.device?.online === false ? 'офлайн' : 'не готово';
  const where = `Проект живёт на устройстве${name ? ` «${name}»` : ''}`;
  return {
    text: ['на устройстве', name, state].filter(Boolean).join(' · '),
    short: state ? `устройство ${state}` : 'на устройстве',
    offline: problem !== null,
    title: problem ? `${where}. ${problem}` : where,
  };
}
