// Секция «Объём» вкладки «Разбор» (FS-FR2 + FS-FRv; FP-R3: записка
// docs/mockups/feature-review-returns-v1.md, раздел «„Объём“: что оставить, что убрать»,
// в макете — секция rz-volume, режим vol=new): итоги закрытой фичи — ровно три строки
// (решение Андрея 11.11): «Шагов» = summary.steps с подписью-пояснением «бэк N · фронт M»
// из линий (есть линия 'other' с total > 0 — «· прочее K»), «Слияний» = merges,
// «Доработок после мержа» = postMergeTails (переименовано: прежнее название — жаргон
// «хвост», а эта формулировка уже есть у открытой фичи). Убрано из секции: строки по линиям
// («возвратов» дублирует блок «Возвраты и потери»), счётчики задач и пачек ревью (внутренняя
// кухня конвейера), медианы шага и смены знаменателя (их перенёс FP-R2: в «Время» и в шкалу
// срока). Группа stepProgress и строки плана — только у открытой (у закрытой не рисуется
// вовсе — решение постановщика, макет ov.vol :513): настоящий объём закрытой — эти три
// строки. Все числа — из run/stats как есть, своё фронт не считает. Раскладка целиком
// через ReviewKv (FS-FR0): на 360 те же две колонки «ключ — значение», переполнения нет.

import { type CSSProperties } from 'react';
import { C, FS } from '../../lib/design';
import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
import { laneWord } from '../../lib/roleLabel';
import { plural } from '../../lib/plural';
import type { PlanState } from '../../lib/featureProgress';
import type { FeatureRun, FeatureLane } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureReviewVolumeProps {
  run: FeatureRun;
  // stats не читается с FP-R3: медианы шага и смены знаменателя перенесены в «Время»
  // и в шкалу срока (FP-R2). Проп остался под единый сигнатур секций «Разбора» —
  // вызывающий передаёт его всем секциям одним списком (FeatureReviewTab.tsx).
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

// Подпись-пояснение к «Шагов» (макет .sub): отдельная строка в колонке значения,
// приглушённая, не жирная.
const STEPS_SUB: CSSProperties = { display: 'block', fontSize: FS.xs, fontWeight: 400, color: C.textMuted };

// Пояснение к «Шагов» (FP-R3): суммы total по нормализованным линиям — «бэк N · фронт M»
// (слова — laneWord, roleLabel.ts:8, та же нормализация, что у прежних строк по линиям).
// Третья нормализованная линия бэка ('other', featureRun.ts:311-312) — «прочее K»,
// только при total > 0. Линий нет (старый ответ) или все суммы нулевые — пояснения нет,
// число стоит одно.
function stepsSub(lanes: FeatureLane[] | undefined): string | null {
  if (!lanes?.length) return null;
  const totalOf = (pred: (lane: FeatureLane) => boolean): number =>
    lanes.reduce((acc, l) => (pred(l) ? acc + l.total : acc), 0);
  const back = totalOf((l) => laneWord(l.lane) === 'бэк');
  const front = totalOf((l) => laneWord(l.lane) === 'фронт');
  const other = totalOf((l) => !laneWord(l.lane));
  const parts: string[] = [];
  if (back > 0) parts.push(`бэк ${back}`);
  if (front > 0) parts.push(`фронт ${front}`);
  if (other > 0) parts.push(`прочее ${other}`);
  return parts.length > 0 ? parts.join(' · ') : null;
}

export function FeatureReviewVolume({ run }: FeatureReviewVolumeProps) {
  const rows: ReviewKvRow[] = [];

  // Признак закрытой — общий по странице (тот же, что в «Время и срок», FeatureReviewTime):
  // одно значение на обе ветки секции (итоги — только у закрытой, группа шагов — только у открытой).
  const closed = run.closed === true || run.state === 'merged' || run.state === 'closed';

  // 1. Шаги. stepProgress аддитивен (старые ответы его не несут) — поля нет, строк нет.
  // У закрытой группа не рисуется вовсе (решение постановщика, макет ov.vol :513): её
  // настоящий объём — строки итогов ниже, «Всего шагов» рядом с «Шагов» — два разных числа.
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

  // 2. Итоги закрытой — ровно три строки (FP-R3, вместо прежних пяти плюс строки по линиям
  // и медианы; переименование третьей строки — см. начало файла).
  if (closed) {
    // «Шагов» — главный объём (summary бывает только у терминальной фичи,
    // FeatureProjector.cs:572-601): поля нет (старый ответ) — строки нет.
    const steps = run.summary?.steps;
    if (steps != null) {
      const sub = stepsSub(run.lanes);
      rows.push({
        label: 'Шагов',
        value: sub === null ? steps : (
          <>
            {steps}
            <span data-testid="volume-steps-sub" style={STEPS_SUB}>{sub}</span>
          </>
        ),
      });
    }
    // «Слияний» и «Доработок после мержа» — счётчики списков; поля аддитивны (старые
    // ответы их не несут) — 0, отсутствие мержей и доработок (0 — значение, не «нет данных»).
    rows.push(
      { label: 'Слияний', value: run.merges?.length ?? 0 },
      { label: 'Доработок после мержа', value: run.postMergeTails?.length ?? 0 },
    );
  }

  return (
    <div data-testid="review-volume">
      <ReviewKv rows={rows} />
    </div>
  );
}
