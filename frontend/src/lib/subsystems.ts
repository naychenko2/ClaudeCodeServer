// Глобальный стор включённости подсистем: эффективные значения приходят с бэка
// (поле `subsystems` ответа /api/auth/me) и раздаются компонентам хуком useSubsystem.
// Паттерн — как у featureFlags.ts: модульное состояние + подписки + useSyncExternalStore.
//
// Контракт с бэком: AuthController.Me отдаёт `subsystems` как МАССИВ активных
// ключей (`SubsystemStateStore.ActiveKeys()`), а не как `Record<string, boolean>`.
// Стороной Record приводил бы `{ ...arr }` к числовым ключам '0','1'… —
// `isSubsystemEnabled('notes')` возвращал false при включённой подсистеме,
// и гейт не работал ни в одном положении тумблера (см. Д-1 отчёта QA).
//
// ВНИМАНИЕ: App.tsx (вне папки фронтовых фич) на старте вызывает
//   setAllSubsystems(me.subsystems ?? [])
// — пока бэк не отдаёт поле subsystems, стор по умолчанию пуст и useSubsystem
// возвращает false для всех ключей (тумблер «выкл» по умолчанию).

import { useSyncExternalStore } from 'react';

// Тонкий реестр ключей для type-safe вызовов useSubsystem. Дублирует ключи из
// бэкового реестра подсистем (одна строка на подсистему). Описания/дефолты
// приходят с сервера — здесь только ключи.
export const SUBSYSTEMS = {
  notes: 'notes',
  spend: 'spend',
} as const;

export type SubsystemKey = (typeof SUBSYSTEMS)[keyof typeof SUBSYSTEMS];

let _subsystems: Record<string, boolean> = {};
const _listeners = new Set<() => void>();

function emit() {
  _listeners.forEach(fn => fn());
}

// Форма, которую бэк реально присылает — массив активных ключей. См.
// комментарий шапки: главный источник рассинхрона пилота.
// Алиас экспортируется для тестов и потребителей, которые хотят явно показать
// «это контракт от бэка», а не «объект, который я хочу залить».
export type SubsystemsFromServer = string[];

// Полный набор активных подсистем — единственная форма ввода: массив ключей от
// бэка (см. `SubsystemsFromServer`). Единственный вызывающий — `App.tsx` на старте.
// Приём `Record<string, boolean>` здесь был ради точечных патчей админской модалки;
// её больше нет — `a7d08f9f` перевёл экран подсистем в режим только чтение (без
// `Toggle` и PUT), и ветка стала недостижимой. Понадобится PUT — форму ввода добавит
// тот, кому она нужна, вместе с её вызывающим.
export function setAllSubsystems(subsystems: SubsystemsFromServer) {
  // Разворачиваем массив активных ключей в Record.
  // Пустой массив → пустой набор; null/undefined → не вызываем.
  _subsystems = {};
  for (const k of subsystems) _subsystems[k] = true;
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
