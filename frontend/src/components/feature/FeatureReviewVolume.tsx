// Секция «Объём» вкладки «Разбор» (FS-FR2 + FS-FRv; записка
// docs/mockups/feature-page-simplify-v4-clickable.md §5 «2. Объём», в макете — секция rz-vol):
// раскладка первого экрана «N из M» (карточку FeatureVolumeCard здесь не повторяю): строки
// stepProgress и строки плана (исходный знаменатель, расширение, доработки после мержа —
// решение Андрея 11.11 «новые задачи в почти готовой фиче») — только у открытой (макет ov.vol
// :513: у закрытой группа «Всего шагов/Приняты…» не рисуется, иначе рядом «Всего шагов 44»
// и «Шагов 12» — два разных числа), строки по линиям, медиана шага по проекту, медиана шага
// этой фичи и смены знаменателя (величины 5 и 8 — бэк их отдаёт: FS-M56/FS-M478; в карточке
// FS-FR2 они стояли «вне шага», здесь — в секции) и — только у закрытой — итоги закрытой.
// Все числа — из run/stats как есть, своё фронт не считает. Раскладка целиком через ReviewKv
// (FS-FR0): на 360 те же две колонки «ключ — значение», переполнения нет.

import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
import { formatDuration } from './FeatureStatsBits';
import { laneWord } from '../../lib/roleLabel';
import { plural } from '../../lib/plural';
import type { PlanState } from '../../lib/featureProgress';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureReviewVolumeProps {
  run: FeatureRun;
  stats: FeatureStats;
  // isMobile — различий в раскладке нет: ReviewKv на 360 и 1280 одинаковые две колонки
  // (контракт «Мобила»); проп — под единый сигнатур секций «Разбора» (FS-FR).
  isMobile?: boolean;
}

// Подписи «Состояния плана» — существующие слова, не выдуманные: pending/partial — из
// formatStepProgress (featureProgress.ts:71-72, «план не разобран»/«план частично»);
// cut — из макета (feature-page-simplify-v4-clickable.html:486, «разрезан»); done — из
// isStepProgressDone («готово», featureProgress.ts:57). В FeatureVolumeCard словаря нет
// (только предупреждение «план разрезан не весь», FeatureVolumeCard.tsx:70-74) — тот же
// смысл теми же словами; здесь — полная таблица на четыре значения.
const PLAN_STATE_LABEL: Record<PlanState, string> = {
  pending: 'не разобран',
  partial: 'частично',
  cut: 'разрезан',
  done: 'готово',
};

// Подпись линии: laneWord (roleLabel.ts:8) — «бэк»/«фронт»; третья нормализованная
// линия бэка (FeatureLane.lane = 'other', featureRun.ts:301) — «прочее» (подпись макета,
// feature-page-simplify-v4-clickable.html:487).
function laneLabel(lane: string): string {
  return laneWord(lane) || 'прочее';
}

export function FeatureReviewVolume({ run, stats }: FeatureReviewVolumeProps) {
  const rows: ReviewKvRow[] = [];

  // Признак закрытой — общий по странице (FeatureReviewTime.tsx:56): одно значение на обе
  // ветки секции (итоги — только у закрытой, группа шагов — только у открытой).
  const closed = run.closed === true || run.state === 'merged' || run.state === 'closed';

  // 1. Шаги. stepProgress аддитивен (старые ответы его не несут) — поля нет, строк нет.
  // У закрытой группа не рисуется вовсе (решение постановщика, макет ov.vol :513): её
  // настоящий объём — строки итогов ниже, «Всего шагов» рядом с «Шагов» — два числа.
  const sp = run.stepProgress;
  if (sp && !closed) {
    rows.push(
      { label: 'Всего шагов', value: sp.total },
      { label: 'Приняты', value: sp.accepted },
      { label: 'На ревью', value: sp.review },
      { label: 'В работе', value: sp.work },
      { label: 'Не заведены', value: sp.notCreated },
      { label: 'Сняты', value: sp.withdrawn },
      // inMaster ортогонален и аддитивен: поля нет — строки нет (ReviewKv опускает null).
      { label: 'Уже в master', value: sp.inMaster ?? null },
      { label: 'Состояние плана', value: PLAN_STATE_LABEL[sp.planState] },
    );

    // 1a. План и доработки (решение Андрея 11.11 «новые задачи в почти готовой фиче»;
    // те же условия, что в FeatureVolumeCard.tsx:40,43 — там одна строка на всё, здесь три
    // отдельные). «Исходный план» — знаменатель на момент первой оценки: baselineTotal null
    // или отсутствует (не зафиксирована, старый ответ) — строки нет. «Расширение» — только
    // если план реально рос (total > baselineTotal): при равном знаменателе «+0» — ложь.
    // «Доработки после мержа» — только при followUpTotal > 0 (вне total, отдельная строка).
    const base = sp.baselineTotal;
    const grew = base != null && sp.total > base;
    rows.push(
      {
        label: 'Исходный план',
        value: base != null ? `${base} ${plural(base, 'шаг', 'шага', 'шагов')}` : null,
      },
      { label: 'Расширение', value: grew ? `+${sp.total - base}` : null },
      {
        label: 'Доработки после мержа',
        value: (sp.followUpTotal ?? 0) > 0 ? `${sp.followUpAccepted ?? 0} из ${sp.followUpTotal}` : null,
      },
    );
  }

  // 2. Линии (макет .lanes): одна строка на линию — «{линия}: N шагов · принято A ·
  // возвратов R» (значение — вторая колонка ReviewKv).
  for (const l of run.lanes ?? []) {
    const value = [
      `${l.total} ${plural(l.total, 'шаг', 'шага', 'шагов')}`,
      `принято ${l.accepted}`,
      `возвратов ${l.returns}`,
    ].join(' · ');
    rows.push({ label: laneLabel(l.lane), value });
  }

  // 3. Медиана шага по проекту: stats.typical.stepMinutes.median (null — фич с шагами
  // меньше трёх, B2a) — значения нет, строки нет.
  const median = stats.typical?.stepMinutes?.median;
  rows.push({ label: 'Медиана шага по проекту', value: median != null ? formatDuration(median) : null });

  // 3a. Медиана шага ЭТОЙ фичи (величина 5, stats.stepMedianMinutes: null — принятых
  // цепочек нет; аддитивное поле, старый ответ его не несёт) — строки нет. Смены знаменателя
  // (величина 8, estimateDrift.denominatorChanges): строка есть, только если estimateDrift
  // есть — 0 здесь значение («знаменатель не менялся»), а не «нет данных».
  const featureMedian = stats.stepMedianMinutes;
  rows.push(
    { label: 'Медиана шага этой фичи', value: featureMedian != null ? formatDuration(featureMedian) : null },
    { label: 'Смены знаменателя', value: stats.estimateDrift ? stats.estimateDrift.denominatorChanges : null },
  );

  // 4. Итоги закрытой — только у закрытой. «Открытых шагов» здесь нет: поля summary.stepsOpen
  // в контракте нет (записка §5) — поля нет, строки тоже.
  if (closed) {
    const s = run.summary;
    rows.push(
      { label: 'Шагов', value: s?.steps ?? null },
      { label: 'Задач всего', value: s?.tasks ?? null },
      { label: 'Пачек ревью', value: run.reviews.length },
      { label: 'Слияний', value: run.merges?.length ?? 0 },
      { label: 'Хвостов после мержа', value: run.postMergeTails?.length ?? 0 },
    );
  }

  return (
    <div data-testid="review-volume">
      <ReviewKv rows={rows} />
    </div>
  );
}
