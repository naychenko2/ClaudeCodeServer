import { C, R, SP } from '../../../lib/design';
import type { FeatureRailSegment } from '../../../types/featureIndex';
import type { FeatureQueue } from '../../../types/featureRun';

// Мини-рельса фичи: конвейер, «положенный набок» (макет features-overview.html,
// .mr; proposal §«Общее для всех вариантов: плашка фичи»). 8 сегментов по массиву
// rail[] индекса — бэк считает состояния (state/tone), фронт только раскладывает:
//   done    — пройден        → C.success
//   active  — активный       → длиннее (15 против 8) и в цвет тона
//                                (run→C.textHeading, wait→C.info, stopped→C.danger, loop→C.warning)
//   pending — впереди        → C.progressTrack
//   absent  — нет в трекере → прозрачный + inset 1px dashed C.dashed
//   skipped — необязательный этап без задач (design, W4-DES-B) → нейтральная
//             заливка C.bgInset: «этапа не было», не «впереди» и не «пройден»
// У закрытой фичи (closed) вся рельса приглушена: C.textMuted, opacity .5
// (absent — только пунктирный контур, без заливки).
//
// Рельса ничего не считает сама: rail[] и closed даёт бэк (контракт индекса фич).
// Компактная: inline-flex, gap 2px, не сжимается (flex-shrink:0) — на 360 px живёт
// в одной строке плашки.
//
// Очередь фич (волна 6, макет feature-queue-1): при waiting (фича в очереди,
// holdReason=queue) активный сегмент — C.textMuted вместо цвета тона (тон очереди
// = тон паузы, тревоги нет), к подписи добавляется «· в очереди N из M». Позицию
// считает бэк (queue.position/length); queue нет или нули — без цифр.

// Геометрия сегмента (макет .mr i): высота 5px, скругление pill (R.max).
// 8px — обычный сегмент, 15px — активный. Значений 8/15/5 в шкале SP нет (SP.xs=4,
// SP.sm=8) — это размеры макета, константы (не magic numbers в разметке).
const SEG_H = 5;
const SEG_W = 8;
const SEG_ACTIVE_W = 15;

// Имя этапа по ключу (порядок и названия — те же, что в STAGE_NAME, chatGroups.ts).
const STAGE_NAME: Record<FeatureRailSegment['key'], string> = {
  cut: 'Разрез',
  design: 'Дизайн',
  plan: 'План',
  prod: 'Производство',
  review: 'Ревью',
  qa: 'Проверка',
  final: 'Финальное ревью',
  merge: 'Мерж',
};

// Слово состояния для title сегмента: «{этап} — {пройден|сейчас|впереди|нет в трекере|не было}».
function stateWord(seg: FeatureRailSegment): string {
  switch (seg.state) {
    case 'done': return 'пройден';
    case 'active': return 'сейчас';
    case 'absent': return 'нет в трекере';
    case 'skipped': return 'не было';
    case 'pending':
    default: return 'впереди';
  }
}

// Цвет активной стадии по tone (макет .mr i.a.*): run→textHeading, wait→info,
// stopped→danger, loop→warning. tone null у active — редкость (бэк обычно даёт тон),
// но дефолт (textHeading) держим, чтобы рельса не «провалилась» в прозрачность.
function activeColor(tone: FeatureRailSegment['tone']): string {
  switch (tone) {
    case 'wait': return C.info;
    case 'stopped': return C.danger;
    case 'loop': return C.warning;
    case 'run':
    default: return C.textHeading;
  }
}

// Сегмент рельсы. Закрытая фича: заливка/контур → C.textMuted + opacity .5,
// absent — прозрачный (только пунктирный контур, без заливки).
// waiting (фича в очереди): активный сегмент — C.textMuted вместо цвета тона.
function segmentStyle(seg: FeatureRailSegment, closed: boolean, waiting: boolean): React.CSSProperties {
  const base: React.CSSProperties = {
    display: 'block',
    height: SEG_H,
    borderRadius: R.max,
    flexShrink: 0,
  };
  if (closed) {
    if (seg.state === 'absent') {
      // Прозрачный: только пунктирный контур (inset 1px), без заливки.
      return { ...base, width: SEG_W, background: 'transparent', boxShadow: `inset 0 0 0 1px ${C.dashed}`, opacity: 0.5 };
    }
    if (seg.state === 'skipped') {
      // Бэк держит skipped и у закрытой фичи: «этапа не было» — не «пройден».
      return { ...base, width: SEG_W, background: C.bgInset, opacity: 0.5 };
    }
    // done/active/pending — приглушённая заливка C.textMuted, opacity .5.
    return { ...base, width: seg.state === 'active' ? SEG_ACTIVE_W : SEG_W, background: C.textMuted, opacity: 0.5 };
  }
  switch (seg.state) {
    case 'done':
      return { ...base, width: SEG_W, background: C.success };
    case 'active':
      return { ...base, width: SEG_ACTIVE_W, background: waiting ? C.textMuted : activeColor(seg.tone) };
    case 'absent':
      return { ...base, width: SEG_W, background: 'transparent', boxShadow: `inset 0 0 0 1px ${C.dashed}` };
    case 'skipped':
      return { ...base, width: SEG_W, background: C.bgInset };
    case 'pending':
    default:
      return { ...base, width: SEG_W, background: C.progressTrack };
  }
}

// Суффикс подписи очереди: « · в очереди N из M» (макет feature-queue-1). Нет queue
// (бэк ещё не отдал) или нули — без цифр; позицию считает бэк, фронт не пересчитывает.
function queueSuffix(queue: FeatureQueue | null | undefined): string {
  if (!queue || queue.position <= 0 || queue.length <= 0) return ' · в очереди';
  return ` · в очереди ${queue.position} из ${queue.length}`;
}

// aria-label всей рельсы: «Этап: {активный этап}» (макет) или «Этап: закрыта».
// Активного нет (все pending/absent) — «Этап: не начат».
// waiting (фича в очереди): к подписи добавляется «· в очереди N из M».
function railAriaLabel(rail: FeatureRailSegment[], closed: boolean, waiting: boolean, queue: FeatureQueue | null | undefined): string {
  if (closed) return 'Этап: закрыта';
  const suffix = waiting ? queueSuffix(queue) : '';
  const active = rail.find(s => s.state === 'active');
  if (!active) return `Этап: не начат${suffix}`;
  return `Этап: ${STAGE_NAME[active.key]}${suffix}`;
}

// props: rail — 8 сегментов индекса (бэк); closed — терминальная фича (merged/closed): приглушить рельсу; label — своя подпись вместо «Этап: закрыта»;
// waiting — фича в очереди (holdReason=queue): активный сегмент C.textMuted, к подписи «· в очереди N из M»; queue — место в очереди с бэка (позицию фронт не считает).
export function FeatureRail({ rail, closed = false, label: labelOverride, waiting = false, queue = null }: { rail: FeatureRailSegment[]; closed?: boolean; label?: string; waiting?: boolean; queue?: FeatureQueue | null }) {
  // Рельса «глупая»: не считает, только раскладывает rail[].
  const label = labelOverride ?? railAriaLabel(rail, closed, waiting, queue);
  return (
    <span
      role="img"
      aria-label={label}
      data-testid="feature-rail"
      style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, flexShrink: 0 }}
    >
      {rail.map(seg => (
        <i
          key={seg.key}
          title={`${STAGE_NAME[seg.key]} — ${stateWord(seg)}`}
          style={segmentStyle(seg, closed, waiting)}
        />
      ))}
    </span>
  );
}
