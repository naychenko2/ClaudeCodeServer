import type { Project, ProjectCapabilitiesView, ProjectFeatureKey } from '../types';
import { type ProjectRoute } from './deviceAgentRoutes';
export declare function getProjectCapabilities(project: Project | null | undefined): ProjectCapabilitiesView;
export declare function isFeatureAvailable(project: Project | null | undefined, feature: ProjectFeatureKey): boolean;
export declare function featureReason(project: Project | null | undefined, feature: ProjectFeatureKey): string | null;
export type ProjectFilesRoute = 'server' | 'agent' | 'relay';
export type DevicePresence = 'unknown' | 'here' | 'elsewhere';
export declare function projectFilesRoute(project: Project | null | undefined, presence?: DevicePresence): ProjectFilesRoute;
export declare function routeSupports(filesRoute: ProjectFilesRoute, route: ProjectRoute): boolean;
export declare function projectSupportsRoute(project: Project | null | undefined, route: ProjectRoute, presence?: DevicePresence): boolean;
export declare const DEVICE_ROUTE_REASON = "\u0423 \u043B\u043E\u043A\u0430\u043B\u044C\u043D\u043E\u0433\u043E \u043F\u0440\u043E\u0435\u043A\u0442\u0430 \u044D\u0442\u043E \u0434\u0435\u0439\u0441\u0442\u0432\u0438\u0435 \u043D\u0435\u0434\u043E\u0441\u0442\u0443\u043F\u043D\u043E: \u0430\u0433\u0435\u043D\u0442 \u0443\u0441\u0442\u0440\u043E\u0439\u0441\u0442\u0432\u0430 \u0435\u0433\u043E \u043D\u0435 \u0443\u043C\u0435\u0435\u0442";
export declare const RELAY_ROUTE_REASON = "\u0421 \u0434\u0440\u0443\u0433\u043E\u0433\u043E \u0443\u0441\u0442\u0440\u043E\u0439\u0441\u0442\u0432\u0430 \u043B\u043E\u043A\u0430\u043B\u044C\u043D\u044B\u0439 \u043F\u0440\u043E\u0435\u043A\u0442 \u043E\u0442\u043A\u0440\u044B\u0442 \u0442\u043E\u043B\u044C\u043A\u043E \u0434\u043B\u044F \u043F\u0440\u043E\u0441\u043C\u043E\u0442\u0440\u0430: \u0438\u0437\u043C\u0435\u043D\u0438\u0442\u044C \u0435\u0433\u043E \u043C\u043E\u0436\u043D\u043E \u043D\u0430 \u043A\u043E\u043C\u043F\u044C\u044E\u0442\u0435\u0440\u0435 \u043F\u0440\u043E\u0435\u043A\u0442\u0430";
export declare function projectRouteReason(project: Project | null | undefined, route: ProjectRoute, presence?: DevicePresence): string | null;
export declare function hostFor(project: Project | null | undefined, feature: ProjectFeatureKey): 'server' | 'device' | 'off';
export declare function canRunTurn(project: Project | null | undefined): {
    available: boolean;
    reason: string | null;
};
export declare function isLocalProject(project: Project | null | undefined): boolean;
export declare function useProjectCapabilities(project: Project | null | undefined): ProjectCapabilitiesView;
export declare function useProjectFeature(project: Project | null | undefined, feature: ProjectFeatureKey): boolean;
export declare function deviceOfflineLabel(project: Project | null | undefined): string | null;
export declare function projectDeviceBadge(project: Project | null | undefined): {
    text: string;
    short: string;
    offline: boolean;
    title: string;
} | null;
