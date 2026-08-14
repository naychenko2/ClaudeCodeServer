// Хук «Сейчас пойдёт» для формы задачи: цепочка вызовов /api/models/preview
// для места «Исполнитель задач» (USAGE.tasksExecutor) с учётом выбранного уровня
// и персоны-исполнителя. Кэширует по ключу «place|personaId|tier» в локальном
// Map, чтобы несколько полей не дёргали сервер на каждый рендер.
//
// «Сейчас пойдёт» живёт во многих местах (форма персоны, композер, новая задача) —
// общий кэш уже есть в lib/presets.ts, но он не принимает personaId+tier для места
// «tasks-executor» (toQuery() собирает только place). Локальный кэш решает задачу,
// пока бэкенд не подтвердит контракт и presets.ts не научится этой комбинации.

import { useEffect, useState } from 'react';
import { api } from '../../lib/api';
import { type ModelTierKey } from '../../lib/modelTiers';
import type { ModelPreviewResponse } from '../../types';

type CacheKey = string;

const _cache = new Map<CacheKey, ModelPreviewResponse | null>();
const _inflight = new Set<CacheKey>();

function keyFor(place: string, personaId: string | null | undefined, tier: string | null | undefined): CacheKey {
  return [place, personaId ?? '', tier ?? ''].join('|');
}

export interface TaskPreviewOptions {
  // Место каталога (например, USAGE.tasksExecutor). Обязательно.
  place: string;
  // Персона-исполнитель ('' или null — без персоны, исполнитель «Claude» по умолчанию)
  personaId?: string | null;
  // Уровень модели задачи ('' или null — по дефолту места: сильная для tasks-executor)
  tier?: ModelTierKey | '' | null;
}

// Возвращает сырой ответ /api/models/preview либо null (грузится / ошибка / нет смысла резолвить)
// value === '' — поле уровня не задано, бэкенд применит дефолт места
export function useTaskPreview({ place, personaId, tier }: TaskPreviewOptions): ModelPreviewResponse | null {
  const personaIdNorm = personaId || '';
  const tierNorm = tier || '';
  const k = keyFor(place, personaIdNorm, tierNorm);
  const [val, setVal] = useState<ModelPreviewResponse | null>(() => _cache.get(k) ?? null);

  useEffect(() => {
    if (_cache.has(k) || _inflight.has(k)) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- синхронизация с кэшем под новый ключ
      setVal(_cache.get(k) ?? null);
      return;
    }
    _inflight.add(k);
    api.models.preview({ place, personaId: personaIdNorm || undefined, tier: tierNorm || undefined })
      .then(d => { _cache.set(k, d); setVal(d); })
      .catch(() => { _cache.set(k, null); setVal(null); })
      .finally(() => { _inflight.delete(k); });
  }, [k, place, personaIdNorm, tierNorm]);

  return val;
}
