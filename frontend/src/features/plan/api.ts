// Домен «Планы» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// карта плана — часть B фичи «Визуальный разворот плана». Тип PlanMap — types/index.ts
// (доедет в эту папку в S5-P3).
import { readStoredToken } from '../../lib/offline';
import type { PlanMap } from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адрес api.plans.* остаётся прежним для всех потребителей).
export const plansApi = {
  // Карта плана (часть B фичи «Визуальный разворот плана», docs/plans/visual-plan.md):
  // структурный слепок markdown-плана для разворота схемой. Сборка платная — one-shot
  // модель (место plan-map), поэтому закрыта от делегированных ходов на бэке.
  // Контракт ответа:
  //   200 → PlanMap
  //   204 → карту собрать не удалось (любой сбой — фронт остаётся на тексте с работающими замечаниями)
  //   409 → сборка уже идёт для этого владельца; компонент сам не вызывает сборку, только показывает «уже собирается»
  //   400 → текст пуст или больше потолка (256 КБ)
  plans: {
    buildMap: async (plan: string): Promise<PlanMap | null> => {
      const token = readStoredToken();
      const res = await fetch('/api/plans/map', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        body: JSON.stringify({ plan }),
      });
      if (res.status === 204) return null;
      if (res.status === 409) {
        const err = await res.json().catch(() => ({ error: 'Сборка уже идёт' }));
        const e = new Error(err.error ?? 'Сборка уже идёт') as Error & { status?: number };
        e.status = 409;
        throw e;
      }
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: res.statusText }));
        const e = new Error(err.error ?? res.statusText) as Error & { status?: number };
        e.status = res.status;
        throw e;
      }
      return await res.json();
    },
  },
};
