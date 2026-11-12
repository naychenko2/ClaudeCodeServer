// Контрактный литерал `brief` (волна 6, F1): запись контракта поля `brief` ответа
// `GET /api/tasks/features/{id}` — в файле, который РЕАЛЬНО компилирует сборка
// (tsconfig.app.json включает src/**, кроме *.test.ts). Поэтому переименование поля
// в FeatureBrief красит `tsc -b` (TS2561/TS2322), а не проходит тихо: `satisfies`
// проверяет форму литерала без расширения типа. Рантайм-сторонит тест
// featurePage.test.ts, который импортирует BRIEF и проверяет поведение стора.

import type { FeaturePage } from '../types/featureIndex';
import type { FeatureBrief } from './featurePage';

// Полный brief по ФАКТИЧЕСКОМУ контракту бэка (FeatureBrief.cs, record Brief — полем в поле,
// в его порядке). Аддитивные поля (queueWaitMinutes B8, updatedAt B6, ask FS-E4, eta FS-V2,
// note.estimate, active, initialEtaMinutes, queueStartEstimate) тоже здесь: литерал,
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
  note: {
    text: 'Идём по плану', at: '2026-10-25T09:00:00Z', byPersonaId: 'p-sofya', byUser: false, stageAtWrite: 'prod', stale: false,
    // Живой голос (stale false) несёт оценку оркестратора — именно её бэк выбирает в eta
    // (EtaOf): 90 мин против [60,140] истории → agreement 'within'; etaEndsAt = note.at + 90 мин.
    estimate: { remaining: ['qa', 'final', 'merge'], etaMinutes: 90, why: null, agreement: 'within', etaEndsAt: '2026-10-25T10:30:00Z' },
  },
  // Срок первой оценки после старта (бэк: TaskItem.InitialEtaMinutes эпика): «на старте
  // обещали ≈ 3 ч» в итоге закрытой; больше текущей оценки — фича ехала медленнее плана.
  initialEtaMinutes: 180,
  // B6: время последнего ЗАПИСАННОГО события состояния; здесь — последний сдвиг состояния
  // (старт ревью, now.since). computedAt — момент ответа — позже него.
  updatedAt: '2026-10-25T10:00:00Z',
  // Все активные задачи фичи (макет 2, таблица 3): здесь одна — та же строка, что now
  // (бэк: brief.now — первая строка Active).
  active: [{ taskId: 't-rev', stage: 'review', stepRef: 'Ш3', lane: 'back', personaId: 'p-gleb', state: 'review', since: '2026-10-25T10:00:00Z', reason: null }],
  // running: не в очереди — оценки старта нет (бэк QueueStartEstimateOf: null).
  queueStartEstimate: null,
  // FS-E4: running без тревоги — AskOf(Attention null) = null; непустой ask бывает только
  // при attention (wait | merge | stopped).
  ask: null,
  // FS-V2: живой срок из живой оценки оркестратора (EtaOf: note → history): source 'note',
  // minutes = estimate.etaMinutes, endsAt = estimate.etaEndsAt; часы идут (running) → clockStopped null.
  eta: { minutes: 90, endsAt: '2026-10-25T10:30:00Z', source: 'note', clockStopped: null },
  computedAt: '2026-10-25T10:05:00Z',
} satisfies FeatureBrief;

// Поле `brief` обязано оставаться опциональным (старые ответы его не несут,
// featureIndex.ts:224): сделать его обязательным — следующая строка красит сборку
// (undefined ∉ FeaturePage['brief'] → never, true не присваивается).
type _BriefOptional = undefined extends FeaturePage['brief'] ? true : never;
export const BRIEF_IS_OPTIONAL: _BriefOptional = true;
