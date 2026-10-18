import { useState } from 'react';
import { C, FS, SP, R, FONT } from '../../../lib/design';
import { usePrefersReducedMotion } from '../../../hooks/usePrefersReducedMotion';
import { getPersonaById } from '../../../lib/personas';
import type {
  FeatureRun, FeatureTask, FeatureStage, FeatureSourceReturns,
  StageKey, StageState,
} from '../../../types/featureRun';

// === Геометрия строки стадии (единый источник, чтобы рельса не рвалась) ===
// Точка стадии — 14px (SVG). Строка несёт рамку 1px + паддинг SP.xs, поэтому
// центр точки = SP.xs + 1 + 14/2 = 12px от верха строки (не зависит от высоты строки,
// т.к. грид строки — align-items:start: точка всегда у верха, а не по центру).
// Сегмент рельсы: top = низ текущей точки (SP.xs + 1 + 14 = 19), bottom = -(SP.xs + 1)
// (до верха следующей точки: её рамка 1 + паддинг SP.xs = 5). Тогда рельса непрерывна.
const DOT = 14;
const RAIL_TOP = SP.xs + 1 + DOT;     // 19 — низ текущей точки, от верха строки
// Мин. высота строки стадии: 34 — текущего значения нет в шкале SP (ближайшее SP.xxl=32),
// поэтому константа: 34 = DOT + 2·(SP.xs + 1) + 10 — точка + рамка/паддинг + запас под мета.
const ROW_MIN_H = 34;
// Бейджи возврата (↺N): паддинг 7px и line-height 18px — в шкалах SP/FS значений нет
// (SP.xs=4, SP.sm=8; FS.xs=11, FS.sm=12), значения макета v2 (.badge: padding 0 7px,
// line-height 18px) — константы.
const BADGE_PAD_X = 7;
const BADGE_LH = 18;
// X-центр сегмента рельсы (left + translateX(-50%)): отступ строки слева (padding-left
// SP.xs) + половина колонки точки 16px. `left` считается от padding-box (внутренний край
// рамки), поэтому рамку 1px не прибавляем: SP.xs + 16/2 = 4 + 8 = 12 — центр сегмента
// совпадает с центром кольца.
const RAIL_LEFT = SP.xs + 16 / 2; // 12

// Стадии в порядке жизненного цикла. Ключи — как в бэке (FeatureStage.Key:
// cut | plan | prod | review | qa | final | merge).
const STAGES: { key: StageKey; label: string }[] = [
  { key: 'cut',    label: 'Разрез' },
  { key: 'plan',   label: 'План' },
  { key: 'prod',   label: 'Производство' },
  { key: 'review', label: 'Ревью' },
  { key: 'qa',     label: 'Проверка' },
  { key: 'final',  label: 'Финальное ревью' },
  { key: 'merge',  label: 'Мерж' },
];

// === Чистая логика (тестируется в node, без DOM) ===
// Фронт НЕ пересчитывает конвейер: стадии/линии/возвраты приходят из бэка
// (run.stages / run.lanes / run.returnsBySource), компонент только раскладывает.

// Стадия из ответа бэка по ключу; null — бэк её не отдал (старый ответ/кэш).
export function stageByKey(run: FeatureRun, key: StageKey): FeatureStage | null {
  const list = run.stages;
  if (!list) return null;
  return list.find(s => s.key === key) ?? null;
}

// Состояние стадии: из бэка; нет stages → pending (не падаем и не считаем сами).
export function stageState(run: FeatureRun, key: StageKey): StageState {
  return stageByKey(run, key)?.state ?? 'pending';
}

// Толщина сегмента рельсы: pending/absent → 2 (пунктир), иначе формула макета.
// Потолок 5 проходов — фронт (чистая презентация), бэк отдаёт честное число.
export function stageWidth(passes: number, state: StageState): number {
  if (state === 'pending' || state === 'absent') return 2;
  return Math.min(2 + (Math.min(passes, 5) - 1) * 1.5, 8);
}

// Линия бэк/фронт: total (все шаги), accepted (done), returns (шаги с возвратом).
export interface LaneInfo { label: string; total: number; accepted: number; returns: number }

// Линии бэк/фронт из ответа бэка (run.lanes). total=0 → строка линии не выводится.
export function laneInfo(run: FeatureRun): { back: LaneInfo; front: LaneInfo } {
  const build = (lane: 'back' | 'front'): LaneInfo => {
    const l = run.lanes?.find(x => x.lane === lane);
    return {
      label: lane === 'back' ? 'бэк' : 'фронт',
      total: l?.total ?? 0,
      accepted: l?.accepted ?? 0,
      returns: l?.returns ?? 0,
    };
  };
  return { back: build('back'), front: build('front') };
}

// Толщина дорожки линии: min(2 + r·0.5, 8), r = число шагов с возвратом.
export function laneTrackWidth(returns: number): number {
  return Math.min(2 + returns * 0.5, 8);
}

// Бейдж возврата (↺N с ревью/проверки/финала) в строке «Производство».
// Построен из run.returnsBySource; показывается на всех ширинах, только где count > 0.
// Тон: open > 0 → тревожный (доработка ещё не done).
export interface ReturnBadge { key: 'review' | 'qa' | 'final'; label: string; count: number; open: number }

export function returnBadges(run: FeatureRun): ReturnBadge[] {
  const src = run.returnsBySource;
  if (!src) return [];
  const map: { key: 'review' | 'qa' | 'final'; label: string; s: FeatureSourceReturns }[] = [
    { key: 'review', label: 'с ревью', s: src.review },
    { key: 'qa',     label: 'с проверки', s: src.qa },
    { key: 'final',  label: 'с финала', s: src.final },
  ];
  return map
    .filter(m => m.s.count > 0)
    .map(m => ({ key: m.key, label: m.label, count: m.s.count, open: m.s.open }));
}

// Переключение раскрытия стадии — ОДИНОЧНОЕ (макет v2: S.open = S.open === v ? null : v):
// открыть одну стадию — закрыть предыдущую. Клик по уже раскрытой стадии сворачивает её (null).
// Возвращает новый ключ (иммутабельно, чтобы React видел смену состояния).
export function toggleExpanded(prev: StageKey | null, key: StageKey): StageKey | null {
  return prev === key ? null : key;
}

// === Рендер ===

// Точка-индикатор стадии: active — акцент с пульсом, done — галочка, pending/absent — пунктир.
// ready — «Мерж» завершён и фича готова (run.ready): done с зелёным акцентом
// (кольцо C.success, галочка C.successText) — финал фичи зелёный, не просто серая галочка.
function StageDot({ state, ready = false }: { state: StageState; ready?: boolean }) {
  if (state === 'active') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" className="feature-pulse">
        <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.accent} strokeWidth="1.6" />
        <circle cx="8" cy="8" r="3.5" fill={C.accent} />
      </svg>
    );
  }
  if (state === 'done') {
    const ring = ready ? C.success : C.textMuted;
    const check = ready ? C.successText : C.textMuted;
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
        <circle cx="8" cy="8" r="7" fill="none" stroke={ring} strokeWidth="1.4" />
        <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={check} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    );
  }
  // pending и absent — пунктирный кружок (F6 добавит у absent отдельный глиф «нет в трекере»).
  return (
    <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
      <circle cx="8" cy="8" r="6" fill="none" stroke={C.dashed} strokeWidth="1.4" strokeDasharray="2.5 2" />
    </svg>
  );
}

// Иконка «часы» (ожидание человека).
function ClockIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 16 16" aria-hidden="true">
      <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.info} strokeWidth="1.4" />
      <path d="M8 4.5V8l2.5 1.5" fill="none" stroke={C.info} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

// Горизонтальная полоса линии (бэк/фронт) под «Производством»:
//   «бэк · N из M» + дорожка (заливка = доля принятых) + бейдж ↺r (hot при r ≥ 3).
// Сетка auto 1fr auto (строки, не колонки — на мобиле и десктопе).
function LaneBar({ info }: { info: LaneInfo }) {
  if (info.total === 0) return null; // 0 шагов в линии → строка не выводится
  const pct = info.total ? Math.round((info.accepted / info.total) * 100) : 0;
  const trackH = laneTrackWidth(info.returns);
  const hot = info.returns >= 3;
  return (
    <div
      style={{
        display: 'grid', gridTemplateColumns: 'auto 1fr auto', gap: SP.sm,
        alignItems: 'center', fontSize: FS.sm, color: C.textSecondary,
      }}
    >
      <span style={{ whiteSpace: 'nowrap' }}>{info.label} · {info.accepted} из {info.total}</span>
      <span
        role="progressbar"
        aria-label={`${info.label}: принято ${info.accepted} из ${info.total}`}
        aria-valuenow={info.accepted}
        aria-valuemax={info.total}
        style={{
          height: trackH, minHeight: 2, background: C.progressTrack,
          borderRadius: R.max, overflow: 'hidden', display: 'block',
        }}
      >
        <span style={{ display: 'block', height: '100%', width: `${pct}%`, background: C.textMuted }} />
      </span>
      {info.returns > 0 ? (
        <span
          style={{
            display: 'inline-flex', alignItems: 'center', gap: SP.xxs, fontSize: FS.xs, fontWeight: 700,
            borderRadius: R.max, padding: `0 ${BADGE_PAD_X}px`, lineHeight: `${BADGE_LH}px`, whiteSpace: 'nowrap',
            // «Холодный» (r < 3) — textSecondary на bgSelected; «горячий» (r ≥ 3) —
            // warningText на warningBg (контраст по макету v2, .badge.hot).
            background: hot ? C.warningBg : C.bgSelected,
            color: hot ? C.warningText : C.textSecondary,
          }}
        >
          ↺{info.returns}
        </span>
      ) : (
        <span />
      )}
    </div>
  );
}

// Бейдж возврата в строке «Производство»: «↺N с ревью/проверки/финала».
// На всех ширинах (раньше — только мобила, из дуг). Тон: open > 0 → тревожный
// (warningText на warningBg), иначе textSecondary на bgSelected (Майя).
function ReturnBadgeView({ b }: { b: ReturnBadge }) {
  const hot = b.open > 0;
  return (
    <span
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xxs, fontSize: FS.xs, fontWeight: 700,
        borderRadius: R.full, padding: `0 ${BADGE_PAD_X}px`, lineHeight: `${BADGE_LH}px`, whiteSpace: 'nowrap',
        background: hot ? C.warningBg : C.bgSelected,
        color: hot ? C.warningText : C.textSecondary,
      }}
    >
      ↺{b.count} {b.label}
    </span>
  );
}

// === Раскрытие стадии: список шагов «Производства» (2.15.2) ===

// ISO → «ЧЧ:ММ» (отметка «с этого момента ждёт человека»). Невалидная/пустая дата → ''.
function formatWhen(iso?: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// Имя статуса (fallback, когда personaId нет): в каком состоянии задача.
function statusLabel(status?: FeatureTask['status']): string {
  switch (status) {
    case 'done': return 'закрыто';
    case 'inProgress': return 'в работе';
    case 'todo': return 'ожидает';
    default: return '—';
  }
}

// Исполнитель: имя персоны из стора персон (lib/personas), или null, если персона не найдена
// (вызывающий тогда падает на имя статуса, а не на сырой id — блокер 2.16.2).
function personaName(id?: string | null): string | null {
  if (!id) return null;
  return getPersonaById(id)?.name ?? null;
}

// Шеврон раскрытия: поворачивается на 180° при раскрытии. reduced-motion — без плавного перехода.
function Chevron({ expanded, reduced }: { expanded: boolean; reduced: boolean }) {
  return (
    <span
      aria-hidden="true"
      style={{
        display: 'inline-flex',
        transform: expanded ? 'rotate(180deg)' : 'none',
        transition: reduced ? 'none' : 'transform 0.15s ease',
        color: C.textMuted,
      }}
    >
      <svg width="12" height="12" viewBox="0 0 16 16">
        <path d="M4 6l4 4 4-4" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </span>
  );
}

// Глиф статуса шага (макет v2 .step: grid 14px 1fr auto, глиф слева, 2.16.3):
//   todo — приглушённый кружок (пунктирная рамка), inProgress — ▶ (accent), done — галочка (muted).
function StepGlyph({ status }: { status?: FeatureTask['status'] }) {
  if (status === 'inProgress') {
    return (
      <span aria-hidden="true" style={{ width: 14, display: 'inline-flex', justifyContent: 'center', color: C.accent, fontSize: FS.xs, lineHeight: 1 }}>▶</span>
    );
  }
  if (status === 'done') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
        <path d="M3.5 8.5 6.5 11.5 12.5 5" fill="none" stroke={C.textMuted} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    );
  }
  // todo (и дефолт, когда статуса нет): приглушённый кружок с пунктирной рамкой.
  return (
    <span aria-hidden="true" style={{ width: 14, height: 14, borderRadius: '50%', border: `1.5px dashed ${C.dashed}`, display: 'inline-block', flexShrink: 0 }} />
  );
}

// Строка шага в раскрытии «Производства»: глиф статуса, линия, stepRef, заголовок, исполнитель, ожидание человека.
function StepRow({ t }: { t: FeatureTask }) {
  const lane = laneOf(t);
  const laneLabel = lane === 'back' ? 'бэк' : lane === 'front' ? 'фронт' : (t.lane?.trim() || '—');
  // Исполнитель: имя персоны (lib/personas); если персона не найдена — имя статуса, не сырой id (2.16.2).
  const who = personaName(t.personaId) ?? statusLabel(t.status);
  const waiting = !!t.awaitingHumanSince;
  const when = formatWhen(t.awaitingHumanSince);
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '14px 1fr', gap: SP.xs, alignItems: 'start', fontSize: FS.sm }}>
      <StepGlyph status={t.status} />
      <div style={{ display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, flexWrap: 'wrap' }}>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{t.stepRef ?? '—'}</span>
          <span style={{ color: C.textPrimary }}>{t.title || 'Без заголовка'}</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.xs, color: C.textMuted }}>
          <span>{laneLabel}</span>
          <span>·</span>
          <span>{who}</span>
          {waiting && (
            <>
              <span>·</span>
              <span style={{ color: C.info }} title={t.awaitingHumanReason ?? undefined}>
                {t.awaitingHumanReason ?? 'ждёт человека'}{when ? ` · с ${when}` : ''}
              </span>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

// Линия шага: 'back' | 'front' | null. lane в данных бывает 'back'/'front' (англ.)
// или 'бэк'/'фронт' (рус.) — нормализуем; прочие линии (тест, дизайн) не в бэк/фронт.
// (F8 переделывает раскрытие шагов; пока используется только StepRow/StageDetails.)
function laneOf(t: FeatureTask): 'back' | 'front' | null {
  const l = (t.lane ?? '').trim().toLowerCase();
  if (l === 'back' || l === 'backend' || l === 'бэк' || l === 'бэкенд') return 'back';
  if (l === 'front' || l === 'frontend' || l === 'фронт' || l === 'фронтенд') return 'front';
  return null;
}

// Раскрытие стадии: список шагов «Производства», сгруппированный по линии (бэк/фронт/прочее).
// `stageKey` — ключ стадии: детали рендерятся ТОЛЬКО для «Производства» (2.16.1), для остальных — null.
// (Имя не `key`: `key` — спец-проп React для реконсиляции, в props компонента не попадает.)
// `id` — цель aria-controls кнопки-строки. Пустой набор шагов → null (раскрытия нет).
// Поверхность (2.16.4, макет .det): фон C.bgCard, рамка C.borderLight, радиус R.md, паддинг SP.sm.
function StageDetails({ id, stageKey, run }: { id: string; stageKey: StageKey; run: FeatureRun }) {
  if (stageKey !== 'prod') return null;
  const steps = run.steps;
  if (!steps.length) return null;
  const back = steps.filter(s => laneOf(s) === 'back');
  const front = steps.filter(s => laneOf(s) === 'front');
  const other = steps.filter(s => laneOf(s) === null);
  const groups: { label: string; list: FeatureTask[] }[] = [];
  if (back.length) groups.push({ label: 'бэк', list: back });
  if (front.length) groups.push({ label: 'фронт', list: front });
  if (other.length) groups.push({ label: 'прочее', list: other });
  // Отступ слева: колонка точки (16px, см. gridTemplateColumns) + gap (SP.sm) —
  // детали выровнены по колонке контента, а не по точке.
  const indent = 16 + SP.sm;
  return (
    <div
      id={id}
      className="stage-details-bgCard"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.xs,
        background: C.bgCard,
        border: `1px solid ${C.borderLight}`,
        borderRadius: R.md,
        padding: `${SP.sm}px ${SP.sm}px ${SP.sm}px ${indent}px`,
      }}
    >
      {groups.map(g => (
        <div key={g.label} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          <div style={{ fontSize: FS.xs, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.04em' }}>
            {g.label}
          </div>
          {g.list.map(s => <StepRow key={s.id} t={s} />)}
        </div>
      ))}
    </div>
  );
}

// Есть ли у стадии раскрытие (детали): только «Производство» с хотя бы одним шагом (2.16.1).
// Для aria-expanded: стадия без деталей не считается «раскрытой», даже если она — текущая expanded.
function stageHasDetails(key: StageKey, run: FeatureRun): boolean {
  return key === 'prod' && run.steps.length > 0;
}

// Конвейер фичи (режим «Исполнение фичи») — строго по макету v2.
//
// Структура (ОДНА вертикальная рельса, не две колонки):
//   • 7 стадий (разрез → план → производство → ревью → проверка → финальное ревью → мерж),
//     у каждого СЕГМЕНТА рельсы — СВОЯ толщина: `min(2 + (min(passes,5) − 1)·1.5, 8)` px, где
//     `passes` — ЧИСЛО ПРОХОДОВ через стадию (бэк: у «Производства» = 1 + возвраты, у остальных
//     — число их задач);
//   • стадии pending/absent — пунктирный сегмент (2px dashed);
//   • активная стадия — фон C.accentLight + жирная подпись + пульсирующая точка;
//   • «идёт сейчас» (▶) — когда у стадии есть runningSince; «ждёт вас» (рамка + часы) — tone=wait;
//   • под строкой «Производство» — полосы бэк/фронт (одна под другой) + бейджи возврата (на всех
//     ширинах, из run.returnsBySource).
//
// Компонент «глупый»: стадии/линии/возвраты считает БЭК (run.stages / run.lanes /
// run.returnsBySource), рендер только раскладывает. Нет stages (старый ответ/кэш) → 7 pending.
export function PipelineFlow({ run }: { run: FeatureRun }) {
  const reduced = usePrefersReducedMotion();
  const lanes = laneInfo(run);
  const badges = returnBadges(run);
  // Раскрытая стадия (2.16.5) — ОДИНОЧНАЯ: «Производство» открыто по умолчанию, остальные свёрнуты.
  // В макете v2 по умолчанию S.open=null, но prod раскрывается при переходе на карточку
  // (a==='tocard' → S.open='prod'); делаем prod открытым сразу, чтобы шаги были видны
  // без лишнего клика (главная претензия v4: «не вижу шаги»). Клик переключает:
  // открыть одну стадию — закрыть предыдущую (toggleExpanded).
  const [expanded, setExpanded] = useState<StageKey | null>('prod');

  return (
    <div
      role="group"
      aria-label="Конвейер фичи: стадии, линии бэк/фронт, возврат"
      style={{ display: 'flex', flexDirection: 'column' }}
    >
      {STAGES.map((s, i) => {
        const stage = stageByKey(run, s.key);
        const state = stage?.state ?? 'pending';
        const active = state === 'active';
        const isLast = i === STAGES.length - 1;
        const passes = stage?.passes ?? 0;
        const width = stageWidth(passes, state);

        // Состояния активной стадии: «ждёт вас» (tone=wait) / «идёт сейчас» (runningSince).
        const waiting = active && stage?.tone === 'wait';
        const inProgress = active && !!stage?.runningSince;
        // run.ready + «Мерж» done → зелёная подсветка строки и точки (финал фичи зелёный).
        const readyMerge = s.key === 'merge' && state === 'done' && run.ready;

        return (
          <div
            key={s.key}
            data-stage={s.key}
            aria-current={active ? 'step' : undefined}
            style={{
              position: 'relative',
              minHeight: ROW_MIN_H,
              padding: `${SP.xs}px ${SP.xs}px`,
              borderRadius: R.lg,
              // active → подсветка C.accentLight; «Мерж» done + run.ready → зелёная (C.successBg).
              background: active ? C.accentLight : readyMerge ? C.successBg : 'transparent',
              // Состояние ожидания: поверх подсветки — C.wait border (C.info) + иконка «часы».
              border: waiting ? `1px solid ${C.info}` : '1px solid transparent',
            }}
          >
            {/* Сегмент рельсы: своя толщина (width) или пунктир (pending/absent). */}
            {!isLast && (
              <span
                aria-hidden="true"
                style={{
                  position: 'absolute',
                  left: RAIL_LEFT,
                  // top = низ текущей точки (RAIL_TOP), bottom = до верха следующей
                  // (-(SP.xs + 1): рамка+паддинг следующей строки) — рельса непрерывна.
                  top: RAIL_TOP,
                  bottom: -(SP.xs + 1),
                  width: (state === 'pending' || state === 'absent') ? 0 : width,
                  background: (state === 'pending' || state === 'absent') ? 'none' : C.progressTrack,
                  borderLeft: (state === 'pending' || state === 'absent') ? `2px dashed ${C.dashed}` : 'none',
                  transform: 'translateX(-50%)',
                  // 2px — скругление сегмента: в шкале R значения 2 нет (R.sm=6).
                  borderRadius: 2,
                }}
              />
            )}
            {/* Строка стадии — кнопка (2.15.1): aria-expanded/aria-controls, клик переключает
                раскрытие. Грид 16px 1fr (точка + контент). padding:0, чтобы точка осталась
                на месте (геометрия рельсы). */}
            <button
              data-testid={`stage-row-${s.key}`}
              aria-expanded={expanded === s.key && stageHasDetails(s.key, run)}
              aria-controls={`stage-details-${s.key}`}
              onClick={() => setExpanded(prev => toggleExpanded(prev, s.key))}
              style={{
                display: 'grid',
                gridTemplateColumns: '16px 1fr',
                gap: SP.sm,
                // 'start' (не 'center'): точка всегда у верха строки (центр = 12px от верха),
                // независимо от высоты строки (иначе в высокой «Производство» точка
                // центрировалась вниз и рельса рвалась над ней).
                alignItems: 'start',
                width: '100%',
                padding: 0,
                border: 'none',
                background: 'transparent',
                cursor: 'pointer',
                textAlign: 'left',
                font: 'inherit',
                color: 'inherit',
              }}
            >
              <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1 }}>
                <StageDot state={state} ready={readyMerge} />
              </span>
              <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
                  {/* Метка «идёт сейчас»: ▶ (accent) слева от имени, когда стадия в работе. */}
                  {inProgress && (
                    <span aria-hidden="true" style={{ color: C.accent, fontSize: FS.xs, lineHeight: 1 }}>▶</span>
                  )}
                  <span
                    style={{
                      fontSize: FS.sm,
                      fontWeight: active ? 700 : 400,
                      color: active ? C.textHeading : state === 'done' ? C.textSecondary : C.textMuted,
                    }}
                  >
                    {s.label}
                  </span>
                  {/* Ожидание человека: иконка «часы» (встроена в стадию, не отдельный StatusAlert). */}
                  {waiting && (
                    <span title={run.awaitingHumanReason ?? 'Ждёт вашего решения'} style={{ display: 'inline-flex', alignItems: 'center' }}>
                      <ClockIcon />
                    </span>
                  )}
                  {/* Бейджи возврата (↺N с ревью/проверки/финала) — на всех ширинах, из run.returnsBySource. */}
                  {s.key === 'prod' && badges.map(b => <ReturnBadgeView key={b.key} b={b} />)}
                  {/* Шеврон раскрытия: справа в строке, поворот при раскрытии (reduced-motion — без перехода). */}
                  <span style={{ marginLeft: 'auto', display: 'inline-flex', alignItems: 'center' }}>
                    <Chevron expanded={expanded === s.key} reduced={reduced} />
                  </span>
                </span>
                {/* Мета под именем: «ещё не начат» (pending/absent) / «идёт сейчас» (inProgress) / reason (wait). */}
                {state === 'pending' || state === 'absent' ? (
                  <span style={{ fontSize: FS.sm, color: C.textMuted }}>ещё не начат</span>
                ) : inProgress ? (
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>идёт сейчас</span>
                ) : waiting ? (
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>{run.awaitingHumanReason ?? 'ждёт вашего решения'}</span>
                ) : null}
                {/* Полосы бэк/фронт под «Производством» (одна под другой, строки). */}
                {s.key === 'prod' && (
                  <span style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, margin: `${SP.xxs}px 0 ${SP.xs}px 0` }}>
                    <LaneBar info={lanes.back} />
                    <LaneBar info={lanes.front} />
                  </span>
                )}
              </span>
            </button>
            {/* Раскрытие: список шагов «Производства» (2.15.2/2.16.1). Только когда стадия раскрыта;
                StageDetails сам отдаёт null для стадий без деталей (только «Производство» их имеет).
                `key` — React-реконсиляция, `stageKey` — данные (см. StageDetails). */}
            {expanded === s.key && (
              <StageDetails key={s.key} id={`stage-details-${s.key}`} stageKey={s.key} run={run} />
            )}
          </div>
        );
      })}
    </div>
  );
}
