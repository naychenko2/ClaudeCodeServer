// Глобальный стор включённости подсистем: эффективные значения приходят с бэка
// (поле `subsystems` ответа /api/auth/me) и раздаются компонентам хуком useSubsystem.
// Паттерн — как у featureFlags.ts: модульное состояние + подписки + useSyncExternalStore.
//
// ВНИМАНИЕ: App.tsx (вне папки фронтовых фич) на старте вызывает
//   setAllSubsystems(me.subsystems ?? {})
// — пока бэк не отдаёт поле subsystems, стор по умолчанию пуст и useSubsystem
// возвращает false для всех ключей (тумблер «выкл» по умолчанию).

import { useSyncExternalStore } from 'react';

// Тонкий реестр ключей для type-safe вызовов useSubsystem. Дублирует ключи из
// бэкового реестра подсистем (одна строка на подсистему). Описания/дефолты
// приходят с сервера — здесь только ключи.
export const SUBSYSTEMS = {
  notes: 'notes',
} as const;

export type SubsystemKey = (typeof SUBSYSTEMS)[keyof typeof SUBSYSTEMS];

let _subsystems: Record<string, boolean> = {};
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Заменить весь набор подсистем (на старте/после логина из ответа me)
export function setAllSubsystems(subsystems: Record<string, boolean>) {
  _subsystems = { ...subsystems };
  emit();
}

// Синхронный геттер для использования вне React-рендера (обработчики событий,
// обёртки в features/*/index.ts): не подписывается, читает текущее значение.
export function isSubsystemEnabled(key: string): boolean {
  return _subsystems[key] ?? false;
}

export function getAllSubsystems(): Record<string, boolean> {
  return { ..._subsystems };
}

// Внутренний сброс стора для тестов: возвращает состояние к пустому набору
// и оповещает подписчиков. В проде не нужен — состояние живёт от загрузки me
// до логаута, между ними не пустеет.
export function __resetSubsystems() {
  _subsystems = {};
  emit();
}

export function subscribeSubsystems(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Подписка компонента на конкретную подсистему. Возвращает true, если подсистема
// включена для текущего пользователя.
export function useSubsystem(key: SubsystemKey): boolean {
  return useSyncExternalStore(
    subscribeSubsystems,
    () => isSubsystemEnabled(key),
    () => isSubsystemEnabled(key),
  );
}
