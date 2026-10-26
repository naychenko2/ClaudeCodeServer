// Контрактный литерал `brief` (волна 6, F1): запись контракта поля `brief` ответа
// `GET /api/tasks/features/{id}` — в файле, который РЕАЛЬНО компилирует сборка
// (tsconfig.app.json включает src/**, кроме *.test.ts). Поэтому переименование поля
// в FeatureBrief красит `tsc -b` (TS2561/TS2322), а не проходит тихо: `satisfies`
// проверяет форму литерала без расширения типа. Рантайм-сторонит тест
// featurePage.test.ts, который импортирует BRIEF и проверяет поведение стора.

import type { FeaturePage } from '../types/featureIndex';
import type { FeatureBrief } from './featurePage';

// Полный brief по ФАКТИЧЕСКОМУ контракту бэка (FeatureBrief.cs, record Brief — полем в поле,
// в его порядке). Аддитивные поля (queueWaitMinutes B8, updatedAt B6) тоже здесь: литерал,
// опустивший поле, — та же слепота, что и литерал без поля вовсе (R-F2d3).
export const BRIEF = {
  kind: 'running',
  now: { stage: 'review', taskId: 't-rev', personaId: 'p-gleb', stepRef: 'Ш3', lane: 'back', since: '2026-10-25T10:00:00Z' },
  reason: null,
  done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' },
  next: [{ stage: 'qa', personaId: 'p-vera' }, { stage: 'final', personaId: null }],
  elapsedMinutes: 120,
  estimate: { remainingMinutes: 90, p25: 60, p75: 140, source: 'history', sampleSize: 4 },
  queue: { position: 2, total: 3 },
  // running: бэк отдаёт null («фича не в очереди», FeatureBrief.cs QueueWaitOf) — и здесь null.
  queueWaitMinutes: null,
  note: { text: 'Идём по плану', at: '2026-10-25T09:00:00Z', byPersonaId: 'p-sofya', byUser: false, stageAtWrite: 'prod', stale: false },
  // B6: время последнего изменения состояния; здесь — последний сдвиг состояния (старт ревью,
  // now.since). computedAt — момент ответа — позже него.
  updatedAt: '2026-10-25T10:00:00Z',
  computedAt: '2026-10-25T10:05:00Z',
} satisfies FeatureBrief;

// Поле `brief` обязано оставаться опциональным (старые ответы его не несут,
// featureIndex.ts:224): сделать его обязательным — следующая строка красит сборку
// (undefined ∉ FeaturePage['brief'] → never, true не присваивается).
type _BriefOptional = undefined extends FeaturePage['brief'] ? true : never;
export const BRIEF_IS_OPTIONAL: _BriefOptional = true;
