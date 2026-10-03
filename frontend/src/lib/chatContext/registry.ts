// Фронтовый реестр видов контекста (слот context-kind): вид → вклад вертикали. Дубль kind в
// двух вкладах — исключение (зеркало бэкенд-теста ContextKindRegistryTests): иначе первый
// вклад молча перекрыл бы второй, и поведение зависело бы от порядка регистрации.

import { getSlotContributions, SLOT_CONTEXT_KIND } from '../subsystems/registryCore';
import type { ContextKindApi } from './types';

export function buildKindRegistry(
  contributions: readonly { name?: string; action?: ContextKindApi }[],
): ReadonlyMap<string, ContextKindApi> {
  const byKind = new Map<string, { api: ContextKindApi; owner: string }>();
  contributions.forEach((c, i) => {
    if (!c.action) return;
    const owner = c.name ?? `вклад #${i}`;
    for (const kind of c.action.kinds) {
      const prev = byKind.get(kind);
      if (prev) throw new Error(`Вид контекста «${kind}» объявлен дважды: ${prev.owner} и ${owner}`);
      byKind.set(kind, { api: c.action, owner });
    }
  });
  return new Map([...byKind].map(([k, v]) => [k, v.api]));
}

// Реестр собирается по текущему составу слота: подсистема может выключиться на лету
export function getKindRegistry(): ReadonlyMap<string, ContextKindApi> {
  return buildKindRegistry(getSlotContributions<never, ContextKindApi>(SLOT_CONTEXT_KIND));
}

export const getKindApi = (kind: string): ContextKindApi | null => getKindRegistry().get(kind) ?? null;
