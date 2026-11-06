import { request } from '../../lib/offline';
import type { FeatureFlagDefinition } from '../../types';
import type { ModuleInfo } from './types';

// Методы REST модулей платформы и фича-флагов; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.modules / api.subsystemModules / api.featureFlags остаются прежними).
export const modulesApi = {
  // Внешние модули (платформа): список включённых у юзера модулей для оболочки (R6).
  // Данные к самим модулям идут мимо этого API — через gateway /api/modules/{id}/** (YARP).
  modules: {
    list: () => request<{ items: ModuleInfo[] }>('/modules'),
  },

  // MF-remote подсистем (пилот Module Federation): список remotes, которые хост
  // загружает в рантайме и регистрирует в реестре слотов.
  subsystemModules: {
    list: () => request<{ items: { id: string; remoteUrl: string; exposedModule: string }[] }>('/subsystem-modules'),
  },

  featureFlags: {
    get: () => request<{ definitions: FeatureFlagDefinition[]; values: Record<string, boolean> }>('/feature-flags'),
    set: (key: string, enabled: boolean) =>
      request<{ values: Record<string, boolean> }>(`/feature-flags/${key}`, {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
  },
};
