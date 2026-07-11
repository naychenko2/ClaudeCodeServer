// Каталог пантеона OmO: единый модульный стор (как lib/personas), лениво грузит
// GET /api/personas/pantheon и материализует виртуальные роли по требованию.
// «Виртуальная» роль = ещё не подключённая персона (connectedPersonaId == null):
// в селекторах она видна всегда, а при выборе тихо создаётся (connect по ключу).

import { useEffect, useSyncExternalStore } from 'react';
import type { PantheonTemplate, Persona } from '../../types';
import { api } from '../../lib/api';
import { bumpPersonas } from '../../lib/personas';

let _templates: PantheonTemplate[] = [];
let _loaded = false;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();

function emit() { _listeners.forEach(fn => fn()); }

async function reload(): Promise<void> {
  const { templates } = await api.personas.pantheon();
  _templates = templates;
  _loaded = true;
  emit();
}

function ensureLoaded(): Promise<void> {
  if (_loaded) return Promise.resolve();
  if (!_loading) _loading = reload().finally(() => { _loading = null; });
  return _loading;
}

// Материализовать роль пантеона по ключу: создаёт (идемпотентно) глобальную персону
// и возвращает её. Обновляет кэш каталога и стор персон.
export async function materializePantheon(key: string): Promise<Persona> {
  const [persona] = await api.personas.connectPantheon([key]);
  await reload();
  bumpPersonas();
  return persona;
}

// Хук каталога: { templates, virtual } — virtual = ещё не подключённые роли.
export function usePantheon(): { templates: PantheonTemplate[]; virtual: PantheonTemplate[] } {
  useEffect(() => { void ensureLoaded(); }, []);
  const templates = useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _templates,
    () => _templates,
  );
  return { templates, virtual: templates.filter(t => !t.connectedPersonaId) };
}
