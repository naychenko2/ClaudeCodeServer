// Замороженный набор имён верхнеуровневых объявлений types/index.ts
// (export interface|type|const|function|enum X). Его проверяет защёлка в
// apiFileBudget.test.ts: НОВОЕ объявление в types/index.ts — красное; поле в
// существующем типе — свободное. Реэкспорты доменов (`export type *`) сюда не
// входят: их имена живут в features/<домен>/types.ts.
// Шаги переноса домена (P10…P15) только убирают вынесенные имена отсюда. Имена,
// пришедшие с мержем master, в types/index.ts не кладутся: шаг интеграции мержа
// пишет их сразу в домен-адресат по карте линии
// docs/research/frontend-api-split-2026-11.md (пример — K8 07.11), и список при
// мерже не растёт. Имя без адресата в карте не вносится ни сюда, ни в хребет:
// о нём сообщают, адресат назначает планировщик. Новый тип своей фичи пишется
// сразу в features/<домен>/types.ts.
export const FROZEN_TYPES_INDEX_DECLARATIONS: readonly string[] = [
  'AgentInfo',
  'ChatItem',
  'CliLayer',
  'CliSkill',
  'DeleteSphereResponse',
  'DocBacklink',
  'DocDetail',
  'DocEntry',
  'DocFolderCandidate',
  'DocHeading',
  'DocLink',
  'DocLinkKind',
  'DocOption',
  'DocProperty',
  'DocPropertyChoice',
  'DocPropertyColor',
  'DocPropertyDef',
  'DocPropertyKind',
  'DocRootFileCandidate',
  'DocSearchHit',
  'DocsScope',
  'DocsScopeInfo',
  'DocTypeGroup',
  'DocTypeSchema',
  'FeatureFlagDefinition',
  'FileEntry',
  'GeneratedSkill',
  'HomeSessionInfo',
  'HomeSummaryResponse',
  'ImageLaunchFields',
  'ImageSnapshotMark',
  'ModelTierValue',
  'ModuleRecordFields',
  'ProjectGroup',
  'ProjectMemoryEntry',
  'PromptSection',
  'PromptSnapshot',
  'RateLimitInfo',
  'RegistrySkill',
  'ServerMessage',
  'Session',
  'SessionContextEntry',
  'SessionContextType',
  'SessionImageChat',
  'SkillInfo',
  'SkillsData',
  'SkillSuggestion',
  'Sphere',
  'SphereMemoryEntry',
  'SphereMemoryProjectShelf',
  'SphereMemoryPromotion',
  'SphereMemoryResponse',
  'SphereMemoryType',
  'SphereOverview',
  'StoredWorkflowProgress',
  'SystemPromptPart',
  'ToolProgress',
  'ToolRunFailure',
  'ToolRunTotals',
  'ToolStage',
  'UsageInfo',
  'WorkflowAgentBlock',
  'WorkflowAgentInfo',
  'WorkLoopState',
];

// Замороженный перечень спецификаторов реэкспорта доменных типов — строк
// `export type * from '…'` в общих файлах: types/index.ts (24 строки) и lib/api.ts
// (10 строк). Его проверяет защёлка в domainReexports.test.ts: фактический список
// файла обязан быть ПОДМНОЖЕСТВОМ этого — новый реэкспорт домена в общий файл
// красный, удаление реэкспорта свободное (список может только убывать; при удалении
// убери и строку отсюда). Строку сюда не добавлять: новый домен пишет свои типы
// сразу в features/<домен>/types.ts, и его потребители читают их с этого адреса —
// S5-B, разрез Александра (d358bb59), раздел «Цель линии типов» карты
// docs/research/frontend-api-split-2026-11.md.
export const FROZEN_TYPES_INDEX_REEXPORTS: readonly string[] = [
  '../features/account/types',
  '../features/changelog/types',
  '../features/dossiers/types',
  '../features/desktop/types',
  '../features/git/types',
  '../features/hostControl/types',
  '../features/imageGeneration/types',
  '../features/knowledge/types',
  '../features/mapHygiene/types',
  '../features/mcp/types',
  '../features/notes/types',
  '../features/notifications/types',
  '../features/personas/types',
  '../features/plan/types',
  '../features/projects/types',
  '../features/providerAccounts/types',
  '../features/quickPhrases/types',
  '../features/reader/types',
  '../features/specialties/types',
  '../features/spend/types',
  '../features/tasks/types',
  '../features/team/types',
  '../features/telemetry/types',
  '../features/video/types',
];

export const FROZEN_LIB_API_REEXPORTS: readonly string[] = [
  '../features/account/types',
  '../features/deploy/types',
  '../features/hostControl/types',
  '../features/knowledge/types',
  '../features/localModel/types',
  '../features/modules/types',
  '../features/plugins/types',
  '../features/projects/types',
  '../features/providerAccounts/types',
  '../features/reader/types',
];
