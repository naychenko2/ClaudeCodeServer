// Метаданные внешнего модуля из GET /api/modules (контракт §2/§7)
export interface ModuleInfo {
  id: string;
  displayName: string;
  description?: string | null;
  version: string;
  schemaVersion: string;
  apiBase: string;                 // "/api/modules/{id}"
  tab?: { label: string; icon?: string | null; order: number } | null;
  remoteEntry?: string | null;     // с ?v={version}
  exposedModule?: string | null;   // "./Tab"
}
