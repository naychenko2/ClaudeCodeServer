import { useEffect, useMemo, useState } from 'react';
import type { BindingTarget, PersonaBinding } from '../../types';
import { bindingLabel, catalogTypeFor, fetchBindingTargets } from './bindingMeta';

// Хук: подгружает каталоги целей под типы имеющихся привязок и отдаёт
// резолвер подписи. Пока каталог не загружен — подпись деградирует до raw id.
// Хук вынесен из компонентного bindingMeta.tsx: экспорт хука рядом с компонентом
// ломает fast refresh (см. eslint.config.js, примечание к react-refresh/only-export-components).
export function useBindingLabels(bindings: PersonaBinding[] | null): (b: PersonaBinding) => string {
  const [targets, setTargets] = useState<Map<string, BindingTarget>>(() => new Map());

  // Набор нужных каталогов — стабильный ключ, чтобы не перезапрашивать на каждый рендер
  const typesKey = useMemo(
    () => [...new Set((bindings ?? []).map(b => catalogTypeFor(b.type)))].sort().join(','),
    [bindings],
  );

  useEffect(() => {
    if (!typesKey) return;
    let alive = true;
    void Promise.all(typesKey.split(',').map(async type => {
      try {
        const list = await fetchBindingTargets(type);
        return list.map(t => [`${type}:${t.id}`, t] as const);
      } catch {
        return [];
      }
    })).then(chunks => {
      if (!alive) return;
      setTargets(new Map(chunks.flat()));
    });
    return () => { alive = false; };
  }, [typesKey]);

  return useMemo(() => (b: PersonaBinding) => bindingLabel(b, targets), [targets]);
}
