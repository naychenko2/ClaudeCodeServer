// Лента активности хаба персон: агрегация на клиенте из реальных источников
// (без нового бэкенда) — разговоры, факты памяти, выполненные задачи, создание
// персоны. Тип события «упоминание» из макета не реализован — на бэке нигде не
// логируются вызовы persona_ask, честного источника данных для него нет.

import { useEffect, useMemo, useState } from 'react';
import type { Persona, PersonaMemoryEntry, Session, Task } from '../../types';
import { api } from '../../lib/api';
import { ensureTasksLoaded, useTasks } from '../../lib/tasks';

export type ActivityKind = 'chat' | 'memory' | 'task' | 'created';

export interface ActivityItem {
  id: string;
  kind: ActivityKind;
  at: string;           // ISO-время события — по нему сортировка и группировка по дням
  personaId: string;
  session?: Session;     // kind === 'chat'
  memoryEntry?: PersonaMemoryEntry; // kind === 'memory'
  task?: Task;           // kind === 'task'
}

// Сколько персон учитываем при сборе активности (по свежести updatedAt) — витрина
// «Твои помощники» показывает всех, лента активности капается ради разумного числа
// параллельных запросов.
const ACTIVITY_PERSONA_LIMIT = 15;
// Сколько последних записей памяти персоны берём в ленту (память может расти
// годами через autolearn — не тащим весь массив ради 2-3 строк в фиде)
const MEMORY_PER_PERSONA_LIMIT = 3;

export function usePersonasActivity(personas: Persona[]): { items: ActivityItem[]; loading: boolean } {
  const allTasks = useTasks();
  useEffect(() => { void ensureTasksLoaded(); }, []);

  // Стабильный ключ набора персон — не триггерим рефетч чатов/памяти на простой
  // реордер того же списка (например, после смены сортировки)
  const personaIds = useMemo(() => personas.map(p => p.id).sort().join(','), [personas]);

  // Разговоры и память — best-effort снапшот на момент захода в хаб (тот же паттерн,
  // что PersonaPreview использует для одной персоны), без live-подписки
  const [raw, setRaw] = useState<{ chats: ActivityItem[]; memory: ActivityItem[] } | null>(null);

  useEffect(() => {
    let alive = true;
    setRaw(null);
    if (personas.length === 0) { setRaw({ chats: [], memory: [] }); return; }

    const targets = [...personas].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, ACTIVITY_PERSONA_LIMIT);
    const memoryTargets = targets.filter(p => p.memoryEnabled);

    Promise.all([
      Promise.all(targets.map(p =>
        api.personas.chats(p.id).then(list => ({ p, list })).catch(() => ({ p, list: [] as Session[] })),
      )),
      Promise.all(memoryTargets.map(p =>
        api.personas.memory(p.id).then(list => ({ p, list })).catch(() => ({ p, list: [] as PersonaMemoryEntry[] })),
      )),
    ]).then(([chatResults, memoryResults]) => {
      if (!alive) return;
      const chats: ActivityItem[] = chatResults.flatMap(({ p, list }) =>
        list.map(s => ({ id: `chat:${s.id}`, kind: 'chat' as const, at: s.updatedAt, personaId: p.id, session: s })));
      const memory: ActivityItem[] = memoryResults.flatMap(({ p, list }) =>
        [...list]
          .filter(e => !e.pending)
          .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
          .slice(0, MEMORY_PER_PERSONA_LIMIT)
          .map(e => ({ id: `memory:${e.id}`, kind: 'memory' as const, at: e.createdAt, personaId: p.id, memoryEntry: e })));
      setRaw({ chats, memory });
    });

    return () => { alive = false; };
  }, [personaIds]);

  // Создание персоны и выполненные задачи считаются из уже загруженных реактивных
  // сторов (usePersonas/useTasks) — пересчёт дешёвый, без сети
  const items = useMemo(() => {
    const created: ActivityItem[] = personas.map(p => ({
      id: `created:${p.id}`, kind: 'created' as const, at: p.createdAt, personaId: p.id,
    }));
    const personaIdSet = new Set(personas.map(p => p.id));
    const tasks: ActivityItem[] = allTasks
      .filter(t => t.personaId && personaIdSet.has(t.personaId) && t.status === 'done' && t.completedAt)
      .map(t => ({ id: `task:${t.id}`, kind: 'task' as const, at: t.completedAt!, personaId: t.personaId!, task: t }));

    return [...created, ...(raw?.chats ?? []), ...(raw?.memory ?? []), ...tasks]
      .sort((a, b) => b.at.localeCompare(a.at));
  }, [personas, allTasks, raw]);

  return { items, loading: raw === null };
}

// Группировка по дням для рендера ленты — 4 корзины, без пустых
export type DayBucket = 'Сегодня' | 'Вчера' | 'На этой неделе' | 'Ранее';

export function dayBucketOf(iso: string): DayBucket {
  const now = new Date();
  const d = new Date(iso);
  const startOfDay = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diffDays = Math.round((startOfDay(now) - startOfDay(d)) / 86400000);
  if (diffDays <= 0) return 'Сегодня';
  if (diffDays === 1) return 'Вчера';
  if (diffDays <= 7) return 'На этой неделе';
  return 'Ранее';
}

export function groupByDay(items: ActivityItem[]): { bucket: DayBucket; items: ActivityItem[] }[] {
  const order: DayBucket[] = ['Сегодня', 'Вчера', 'На этой неделе', 'Ранее'];
  const map = new Map<DayBucket, ActivityItem[]>();
  for (const it of items) {
    const b = dayBucketOf(it.at);
    if (!map.has(b)) map.set(b, []);
    map.get(b)!.push(it);
  }
  return order.filter(b => map.has(b)).map(b => ({ bucket: b, items: map.get(b)! }));
}
