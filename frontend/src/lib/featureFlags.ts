// Глобальный стор фич-флагов: эффективные значения per-user приходят с бэка
// (через /api/auth/me и /api/feature-flags) и раздаются компонентам хуком useFeature.
// Паттерн — как у offline.ts: модульное состояние + подписки + useSyncExternalStore.

import { useSyncExternalStore } from 'react';

// Тонкий реестр ключей для type-safe вызовов useFeature. Дублирует ключи из
// бэкового FeatureFlagCatalog (одна строка на флаг). Описания/дефолты/стадии
// приходят с сервера — здесь только ключи.
export const FLAGS = {
  workspaceDestructive: 'workspace-destructive',
  changeDossiersRecall: 'change-dossiers-recall',
  desktopAgent: 'desktop-agent',
  specialtyPromptSections: 'specialty-prompt-sections',
  // Автоправило архивации чатов (план «Архив чатов» v4, шаг 6): фоновый сервис
  // раз в час убирает в архив чаты без активности дольше порога. Флаг закрывает
  // ТОЛЬКО автоправило и его настройки; ручной архив, раздел «Архив» и сводка
  // карточки работают без флага.
  chatAutoArchive: 'chat-auto-archive',
  // Визуальный разворот плана: контекстные замечания к разделам (часть A).
  // Слой маркеров у заголовков и отправка обратной связи существующим
  // контрактом onRespond; сама разметка картой живёт под этим же флагом.
  visualPlan: 'visual-plan',
  // Каталог MCP-серверов (волна 1, задача 9fa075ec): поиск по реестру и предзаполнение
  // формы из черновика записи. Флаг — на вход в раздел; ручной путь «Добавить» работает
  // всегда.
  mcpCatalog: 'mcp-catalog',
} as const;

export type FlagKey = (typeof FLAGS)[keyof typeof FLAGS];

let _flags: Record<string, boolean> = {};
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Заменить весь набор флагов (на старте/после логина из ответа me)
export function setAllFlags(flags: Record<string, boolean>) {
  _flags = { ...flags };
  emit();
}

// Оптимистичное локальное обновление одного флага (для мгновенной реакции тумблера)
export function setFlagLocal(key: string, value: boolean) {
  _flags = { ..._flags, [key]: value };
  emit();
}

export function getFlag(key: string): boolean {
  return _flags[key] ?? false;
}

export function getAllFlags(): Record<string, boolean> {
  return _flags;
}

export function subscribeFlags(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Подписка компонента на конкретный флаг. Возвращает true, если фича включена.
export function useFeature(key: FlagKey): boolean {
  return useSyncExternalStore(
    subscribeFlags,
    () => getFlag(key),
    () => getFlag(key),
  );
}
