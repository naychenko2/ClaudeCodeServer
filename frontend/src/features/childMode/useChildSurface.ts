import type { ChildSurface } from '../account/types';

// Разбор снимка детской поверхности из ответа «обо мне» (ADR-041 ред. 8, решение 76, И11;
// разрез §14.1). Точка правды — поле childSurface в GET /api/auth/me: оно есть только у
// ребёнка, взрослому сервер присылает null.
//
// Default-deny: ребёнок без поля (или с null) — не взрослый набор, а состояние
// 'child-no-surface': экран «нет связи» рисует Ф1f. Взрослый (isChild ≠ true) — 'adult',
// snapshot-а нет и пересечения ни с чем не строятся.
//
// Чистый разбор без состояния и опросов: surface приходит вместе с me-ответом (стор me),
// отдельного канала доставки нет — здесь только классификация. Интерфейс параметра —
// минимальный (то, что читается): подходит и Me (ответ /auth/me), и его копия из стора me.
// Пересечение с набором экрана и матрицей проекта (workspaceKeysFor ∩ ProjectCapabilities
// ∩ panels) — не здесь: это wiring WorkspacePage (Ф1b).

export type ChildSurfaceSource = { isChild?: boolean; childSurface?: ChildSurface | null } | null | undefined;

export type ChildSurfaceState =
  | { kind: 'adult' }
  | { kind: 'child'; surface: ChildSurface }
  | { kind: 'child-no-surface' };

export function useChildSurface(auth: ChildSurfaceSource): ChildSurfaceState {
  if (auth?.isChild !== true) return { kind: 'adult' };
  // Поле есть только у ребёнка; у взрослого — null (Me_Adult_ReturnsNullChildSurface),
  // сюда не попадаем. Осталось различить детский профиль со снимком и без него.
  return auth.childSurface ? { kind: 'child', surface: auth.childSurface } : { kind: 'child-no-surface' };
}
