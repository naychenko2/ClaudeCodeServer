import { useEffect, useState } from 'react';
import { C, FS, SP, R, FONT, TB } from '../../../lib/design';
import { usePrefersReducedMotion } from '../../../hooks/usePrefersReducedMotion';
import { useIsMobile } from '../../../lib/breakpoints';
import { getPersonaById } from '../../../lib/personas';
import { FeatureGlyph } from './FeatureGlyphs';
import type {
  FeatureRun, FeatureTask, FeatureStage, FeatureSourceReturns,
  StageKey, StageState, StageTone,
} from '../../../types/featureRun';

// Тик раз в минуту: длительность метки «сейчас/ждёт вас/стоит · N мин» не застывает,
// пока открыта карточка (как useMinuteTick в FeatureRunView).
function useMinuteTick(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 60000);
    return () => clearInterval(t);
  }, []);
  return now;
}

// === Геометрия строки стадии (единый источник, чтобы рельса не рвалась) ===
// Строка — <button> (макет v2 .row): рамка 1px + паддинг (SP.xs сверху/справа/снизу, 0 слева)
// + minHeight тач-цели (TB.iconHitDesktop/Mobile). Точка стадии — 16px (SVG), грид строки
// align-items:start, поэтому точка всегда у верха строки, а не по центру: центр точки =
// паддинг-top + рамка 1 + 16/2 = SP.xs + 1 + 8 = 13px от верха кнопки (не зависит от высоты).
// Сегмент рельсы: top = низ текущей точки (SP.xs + 1 + 16 = 21), bottom = -(SP.xs + 1)
// (до верха следующей точки: её рамка 1 + паддинг SP.xs = 5). Тогда рельса непрерывна.
const DOT = 16;
// Паддинг строки (макет v2 .row: 5px 4px 5px 0 → на шкале SP.xs сверху/справа/снизу, 0 слева).
const ROW_PAD_Y = SP.xs;   // 4 — top/bottom
const ROW_PAD_X = SP.xs;   // 4 — right (left = 0)
const RAIL_TOP = ROW_PAD_Y + 1 + DOT; // 21 — низ текущей точки, от верха строки
// Мин. высота строки стадии: тач-цель по шкале TB (макет v1: minHeight TB.iconHitDesktop/Mobile).
// Десктоп 32 / мобила 40 — из TB, не литерал 34 (34 в шкале SP не было).
const ROW_MIN_H_DESKTOP = TB.iconHitDesktop; // 32
const ROW_MIN_H_MOBILE = TB.iconHitMobile;   // 40
// Метка текущего этапа: line-height 18px и padding-x 8px — в шкалах SP/FS значений нет
// (SP.sm=8 есть, но макет v2 задаёт 0 8px и line-height 18px) — константы.
const LABEL_LH = 18;
// Бейджи возврата (↺N): паддинг 7px и line-height 18px — в шкалах SP/FS значений нет
// (SP.xs=4, SP.sm=8; FS.xs=11, FS.sm=12), значения макета v2 (.badge: padding 0 7px,
// line-height 18px) — константы.
const BADGE_PAD_X = 7;
const BADGE_LH = 18;
// Зазор грида строки (макет v2 .row: gap 9px) — константа, не литерал (Майя M11).
const ROW_GAP = 9;
// X-центр сегмента рельсы (left + translateX(-50%)): `left` считается от левого края обёртки
// (position:relative, без рамки/паддинга), а точка лежит в гриде кнопки, сдвинутой её рамкой
// 1px вправо: 1 (рамка кнопки) + 16/2 (центр колонки точки) = 9 — центр сегмента совпадает
// с центром кольца (Майя M2: рельса не рвётся и не сдвигается от центра точек).
const RAIL_LEFT = 1 + 16 / 2; // 9

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

// Линия бэк/фронт: total (все шаги), accepted (done), returns (шаги с возвратом),
// personaIds (исполнители; имена резолвятся в LaneBar через lib/personas).
export interface LaneInfo { label: string; total: number; accepted: number; returns: number; personaIds: string[] }

// Линии бэк/фронт из ответа бэка (run.lanes). total=0 → строка линии не выводится.
export function laneInfo(run: FeatureRun): { back: LaneInfo; front: LaneInfo } {
  const build = (lane: 'back' | 'front'): LaneInfo => {
    const l = run.lanes?.find(x => x.lane === lane);
    return {
      label: lane === 'back' ? 'Бэк' : 'Фронт',
      total: l?.total ?? 0,
      accepted: l?.accepted ?? 0,
      returns: l?.returns ?? 0,
      personaIds: l?.personaIds ?? [],
    };
  };
  return { back: build('back'), front: build('front') };
}

// Толщина дорожки линии: min(2 + r·0.5, 8), r = число шагов с возвратом.
export function laneTrackWidth(returns: number): number {
  return Math.min(2 + returns * 0.5, 8);
}

// Подпись стадии «нет в трекере» (absent) — по ключу стадии (pending — «ещё не начат»).
// Различие pending/absent держат три вещи: подпись, косая черта у absent, aria-label строки.
const ABSENT_LABEL: Record<StageKey, string> = {
  cut: 'задачи разреза в трекере нет',
  plan: 'задачи плана в трекере нет',
  prod: 'задачи производства в трекере нет',
  review: 'ревью в трекере нет',
  qa: 'задачи проверки в трекере нет',
  final: 'задачи финала в трекере нет',
  merge: 'задачи мержа в трекере нет',
};
export function absentLabel(key: StageKey): string {
  return ABSENT_LABEL[key];
}

// aria-label строки стадии: «{Имя} — нет в трекере» (absent) / «{Имя} — ещё не начат» (pending);
// у остальных — просто имя (строка нажимается, имя говорит само за себя).
export function stageAriaLabel(label: string, state: StageState): string {
  if (state === 'absent') return `${label} — нет в трекере`;
  if (state === 'pending') return `${label} — ещё не начат`;
  return label;
}

// Длительность активной стадии: минуты от runningSince до now. «25 мин» / «1 ч 10 мин»;
// null — старт не задан (метка «сейчас» без длительности).
export function durationLabel(since: string | null | undefined, now: number): string | null {
  if (!since) return null;
  const d = new Date(since);
  if (Number.isNaN(d.getTime())) return null;
  const min = Math.max(0, Math.floor((now - d.getTime()) / 60000));
  if (min < 1) return '0 мин';
  if (min < 60) return `${min} мин`;
  const h = Math.floor(min / 60);
  const rest = min % 60;
  return rest ? `${h} ч ${rest} мин` : `${h} ч`;
}

// Имена исполнителей линии: personaIds → имена (lib/personas), неизвестный id — пропускается
// (не выводим сырой id). Пусто — нет исполнителей (линия без имён).
export function laneNames(personaIds: string[]): string {
  const names = personaIds
    .map(id => getPersonaById(id)?.name)
    .filter((n): n is string => !!n && n.trim() !== '');
  return names.join(', ');
}

// Метка текущего этапа (активная стадия): текст по tone. «сейчас · N мин» (run),
// «ждёт вас · N мин» (wait), «стоит · N мин» (stopped), «доработка · N мин» (loop).
// «Стоит» — ТОЛЬКО упавший исполнитель (раньше «стоит сейчас» значило и «идёт», и «упал» — путало).
// Длительность — от runningSince; null — метка без длительности.
export function activeStageLabel(tone: StageTone | null | undefined, since: string | null | undefined, now: number): string {
  const dur = durationLabel(since, now);
  const suffix = dur ? ` · ${dur}` : '';
  switch (tone) {
    case 'wait': return `ждёт вас${suffix}`;
    case 'stopped': return `стоит${suffix}`;
    case 'loop': return `доработка${suffix}`;
    case 'run':
    default: return `сейчас${suffix}`;
  }
}

// Цвета метки по tone: run — bgSelected/textHeading; wait — infoBg/info; stopped — dangerBg/dangerText;
// loop — warningBg/warningText. Рамка = текст (макет v2: рамка 1px того же тона).
function activeStageTone(tone: StageTone | null | undefined): { bg: string; fg: string; border: string } {
  switch (tone) {
    case 'wait': return { bg: C.infoBg, fg: C.info, border: C.info };
    case 'stopped': return { bg: C.dangerBg, fg: C.dangerText, border: C.danger };
    case 'loop': return { bg: C.warningBg, fg: C.warningText, border: C.warning };
    case 'run':
    default: return { bg: C.bgSelected, fg: C.textHeading, border: C.textHeading };
  }
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

// Точка-индикатор стадии 16px: подложка — круг C.bgWhite поверх рельсы, поверх — глиф по
// состоянию. done — круг C.bgSelected с галкой C.textMuted (зелёный — только у итога фичи,
// не у строки «Мерж» — Майя M3); active по tone: run — пульс C.textHeading (reduced-motion —
// статичен, opacity .5 — через .feature-pulse), wait — ромб, stopped — квадрат, loop — петля
// C.warning (ромб/квадрат/петля — из FeatureGlyphs); pending — пунктирный круг C.dashed;
// absent — сплошной круг C.dashed с косой чертой (FeatureGlyphs).
function StageDot({ state, tone }: {
  state: StageState; tone?: StageTone | null;
}) {
  // Подложка: круг C.bgWhite r 7.5 — закрывает рельсу под точкой (viewBox 16).
  const bg = <circle cx="8" cy="8" r="7.5" fill={C.bgWhite} />;
  const svg = (children: React.ReactNode) => (
    <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">{bg}{children}</svg>
  );

  if (state === 'done') {
    // «Пройдено»: круг C.bgSelected + галка C.textMuted (не зелёный — зелёный только у итога).
    return svg(
      <>
        <circle cx="8" cy="8" r="6.5" fill={C.bgSelected} />
        <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.textMuted} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
      </>
    );
  }

  if (state === 'active') {
    // wait — ромб, stopped — квадрат, loop — петля: из FeatureGlyphs (не дублируем);
    // run (и дефолт active) — пульс C.textHeading (.feature-pulse; reduced-motion — CSS: opacity .5).
    if (tone === 'wait') return svg(<FeatureGlyph kind="wait" size={16} />);
    if (tone === 'stopped') return svg(<FeatureGlyph kind="stopped" size={16} />);
    if (tone === 'loop') return svg(<FeatureGlyph kind="loop" size={16} />);
    // Пульс — только на внешнем кольце (r 6.5), не на всём <svg> с подложкой C.bgWhite:
    // иначе сквозь точку видна рельса (Майя M4).
    return (
      <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
        {bg}
        <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.textHeading} strokeWidth="1.6" className="feature-pulse" />
        <circle cx="8" cy="8" r="3.5" fill={C.textHeading} />
      </svg>
    );
  }

  // absent — сплошной круг C.dashed с косой чертой (глиф из FeatureGlyphs); pending — пунктирный.
  if (state === 'absent') return svg(<FeatureGlyph kind="absent" size={16} />);
  return svg(<circle cx="8" cy="8" r="6" fill="none" stroke={C.dashed} strokeWidth="1.5" strokeDasharray="2.5 2" />);
}

// Горизонтальная полоса линии (бэк/фронт) под «Производством»:
//   «Бэк · Денис, Кира · N из M» + дорожка (заливка = доля принятых) + бейдж ↺r (hot при r ≥ 3).
// Сетка auto 1fr auto (строки, не колонки — на мобиле и десктопе). Имена исполнителей
// (personaIds → lib/personas) — через запятую; неизвестный id не выводится. На 360px подпись
// переносится (overflow-wrap), не вызывая горизонтального скролла.
function LaneBar({ info }: { info: LaneInfo }) {
  if (info.total === 0) return null; // 0 шагов в линии → строка не выводится
  const pct = info.total ? Math.round((info.accepted / info.total) * 100) : 0;
  const trackH = laneTrackWidth(info.returns);
  const hot = info.returns >= 3;
  const names = laneNames(info.personaIds);
  // «Бэк · Денис, Кира · 3 из 4»; без исполнителей — «Бэк · 3 из 4» (без двойного «·»).
  const head = names
    ? `${info.label} · ${names} · ${info.accepted} из ${info.total}`
    : `${info.label} · ${info.accepted} из ${info.total}`;
  return (
    <div
      style={{
        // minmax(0, max-content) — подпись не схлопывает дорожку при длинном списке исполнителей (Майя M7);
        // minmax(40px, 1fr) — дорожка не уже 40px; auto — бейдж.
        display: 'grid', gridTemplateColumns: 'minmax(0, max-content) minmax(40px, 1fr) auto', gap: SP.sm,
        alignItems: 'center', fontSize: FS.sm, color: C.textSecondary, minWidth: 0,
      }}
    >
      <span style={{ minWidth: 0, overflowWrap: 'anywhere' }}>{head}</span>
      <span
        role="progressbar"
        aria-label={`${info.label}: принято ${info.accepted} из ${info.total}`}
        aria-valuenow={info.accepted}
        aria-valuemax={info.total}
        style={{
          height: trackH, minHeight: 2, background: C.progressTrack,
          // R.max (999px), не R.full ('50%'): дорожка 200×4 не должна давать «линзу» (Майя M1).
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
        borderRadius: R.max, padding: `0 ${BADGE_PAD_X}px`, lineHeight: `${BADGE_LH}px`, whiteSpace: 'nowrap',
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
      <svg width="14" height="14" viewBox="0 0 16 16">
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
  // run.steps может быть undefined (старый ответ/кэш) — нормализуем, чтобы рендер не падал (Глеб).
  const steps = run.steps ?? [];
  if (steps.length === 0) return null;
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
  // run.steps может быть undefined (старый ответ/кэш) — ?? 0, чтобы рендер не падал (Глеб).
  return key === 'prod' && (run.steps?.length ?? 0) > 0;
}

// Конвейер фичи (режим «Исполнение фичи») — строго по макету v2.
//
// Структура (ОДНА вертикальная рельса, не две колонки):
//   • 7 стадий (разрез → план → производство → ревью → проверка → финальное ревью → мерж),
//     у каждого СЕГМЕНТА рельсы — СВОЯ толщина: `min(2 + (min(passes,5) − 1)·1.5, 8)` px, где
//     `passes` — ЧИСЛО ПРОХОДОВ через стадию (бэк: у «Производства» = 1 + возвраты, у остальных
//     — число их задач);
//   • стадии pending/absent — пунктирный сегмент (2px dashed);
//   • активная стадия — метка (текст + тон по tone) + глиф/пульс; фон и рамку строка НЕ носит
//     (текущую строку выделяют метка и глиф, не подсветка — Майя M3);
//   • «идёт сейчас» (▶) — когда у стадии есть runningSince; «ждёт вас» — tone=wait;
//   • под строкой «Производство» — полосы бэк/фронт (одна под другой) + бейджи возврата (на всех
//     ширинах, из run.returnsBySource).
//
// Компонент «глупый»: стадии/линии/возвраты считает БЭК (run.stages / run.lanes /
// run.returnsBySource), рендер только раскладывает. Нет stages (старый ответ/кэш) → 7 pending.
export function PipelineFlow({ run }: { run: FeatureRun }) {
  const reduced = usePrefersReducedMotion();
  const isMobile = useIsMobile();
  const now = useMinuteTick();
  const lanes = laneInfo(run);
  const badges = returnBadges(run);
  // Раскрытая стадия (2.16.5) — ОДИНОЧНАЯ: «Производство» открыто по умолчанию, остальные свёрнуты.
  // В макете v2 по умолчанию S.open=null, но prod раскрывается при переходе на карточку
  // (a==='tocard' → S.open='prod'); делаем prod открытым сразу, чтобы шаги были видны
  // без лишнего клика (главная претензия v4: «не вижу шаги»). Клик переключает:
  // открыть одну стадию — закрыть предыдущую (toggleExpanded).
  const [expanded, setExpanded] = useState<StageKey | null>('prod');
  // minHeight строки: тач-цель по TB (32 десктоп / 40 мобила).
  const rowMinH = isMobile ? ROW_MIN_H_MOBILE : ROW_MIN_H_DESKTOP;

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
        const tone = stage?.tone;
        const isPendingOrAbsent = state === 'pending' || state === 'absent';
        // Метка текущего этапа: только у active (текст + тон по tone).
        const label = active ? activeStageLabel(tone, stage?.runningSince, now) : null;
        const toneColors = active ? activeStageTone(tone) : null;
        // ×N: passes > 1, кроме стадии «Ревью».
        const showPasses = passes > 1 && s.key !== 'review';
        // aria-controls — только когда у стадии есть детали (prod с шагами); иначе id не существует
        // (Глеб: для остальных стадий и prod без шагов атрибута нет).
        const hasDetails = stageHasDetails(s.key, run);

        return (
          <div
            key={s.key}
            data-stage={s.key}
            aria-current={active ? 'step' : undefined}
            // Обёртка — только позиционирование для сегмента рельсы (absolute). Паддинг, minHeight,
            // рамка и фон — на <button> (макет v2 .row), чтобы строка держала тач-цель (Майя M2).
            // Текущую строку выделяют метка и глиф, а не фон/рамка (Майя M3).
            style={{ position: 'relative', width: '100%' }}
          >
            {/* Сегмент рельсы: своя толщина (width) или пунктир (pending/absent). */}
            {!isLast && (
              <span
                aria-hidden="true"
                style={{
                  position: 'absolute',
                  left: RAIL_LEFT,
                  // top = низ текущей точки (RAIL_TOP), bottom = до верха следующей
                  // (-(ROW_PAD_Y + 1): рамка+паддинг следующей строки) — рельса непрерывна.
                  top: RAIL_TOP,
                  bottom: -(ROW_PAD_Y + 1),
                  width: isPendingOrAbsent ? 0 : width,
                  background: isPendingOrAbsent ? 'none' : C.progressTrack,
                  borderLeft: isPendingOrAbsent ? `2px dashed ${C.dashed}` : 'none',
                  transform: 'translateX(-50%)',
                  // 2px — скругление сегмента: в шкале R значения 2 нет (R.sm=6).
                  borderRadius: 2,
                }}
              />
            )}
            {isPendingOrAbsent ? (
              // Не-кнопка: без шеврона и onClick, имя C.textMuted, подпись обязательна
              // (pending «ещё не начат» / absent «…в трекере нет»), aria-label строки.
              // Те же minHeight и паддинг, что у кнопки, — ритм строк не прыгает (Майя M2).
              <div
                data-testid={`stage-row-${s.key}`}
                role="group"
                aria-label={stageAriaLabel(s.label, state)}
                style={{
                  display: 'grid', gridTemplateColumns: '16px 1fr', gap: ROW_GAP, alignItems: 'start',
                  width: '100%', minHeight: rowMinH,
                  padding: `${ROW_PAD_Y}px ${ROW_PAD_X}px ${ROW_PAD_Y}px 0`,
                  border: '1px solid transparent', borderRadius: R.md,
                }}
              >
                <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1 }}>
                  <StageDot state={state} tone={tone} />
                </span>
                <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                  <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textMuted }}>{s.label}</span>
                  <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
                    {state === 'absent' ? absentLabel(s.key) : 'ещё не начат'}
                  </span>
                </span>
              </div>
            ) : (
              // Кнопка = строка (макет v2 .row): minHeight тач-цели + паддинг + рамка + hover/фокус.
              // Сетка 16px 1fr auto (точка + контент + шеврон), имя FS.base/600 C.textHeading,
              // метка текущего этапа (active), ×N (passes>1, не «Ревью»), бейджи возврата (prod).
              <button
                data-testid={`stage-row-${s.key}`}
                aria-expanded={expanded === s.key && hasDetails}
                aria-controls={hasDetails ? `stage-details-${s.key}` : undefined}
                onClick={() => setExpanded(prev => toggleExpanded(prev, s.key))}
                className="cc-stage-row"
                style={{
                  display: 'grid',
                  gridTemplateColumns: '16px 1fr auto',
                  gap: ROW_GAP,
                  // 'start' (не 'center'): точка всегда у верха строки (центр = ROW_PAD_Y + 1 + 8 = 13px),
                  // независимо от высоты строки (иначе в высокой «Производство» точка
                  // центрировалась вниз и рельса рвалась над ней).
                  alignItems: 'start',
                  width: '100%',
                  // minHeight тач-цели (TB.iconHitDesktop/Mobile) + паддинг макета (5px 4px 5px 0 → SP.xs)
                  // на самой кнопке, а не на обёртке — строка держит цель (Майя M2).
                  minHeight: rowMinH,
                  padding: `${ROW_PAD_Y}px ${ROW_PAD_X}px ${ROW_PAD_Y}px 0`,
                  border: '1px solid transparent',
                  // hover/tap/фокус — через .cc-stage-row (index.css); радиус R.md.
                  borderRadius: R.md,
                  cursor: 'pointer',
                  textAlign: 'left',
                  font: 'inherit',
                  color: 'inherit',
                }}
              >
                <span style={{ display: 'flex', justifyContent: 'center', position: 'relative', zIndex: 1 }}>
                  <StageDot state={state} tone={tone} />
                </span>
                <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0, gap: 2 }}>
                  <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
                    <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>{s.label}</span>
                    {showPasses && (
                      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>×{passes}</span>
                    )}
                    {active && label && toneColors && (
                      <span
                        style={{
                          fontSize: FS.xs, fontWeight: 700, borderRadius: R.max,
                          lineHeight: `${LABEL_LH}px`, padding: '0 8px', whiteSpace: 'nowrap',
                          border: `1px solid ${toneColors.border}`, background: toneColors.bg, color: toneColors.fg,
                        }}
                      >
                        {label}
                      </span>
                    )}
                    {/* Бейджи возврата (↺N с ревью/проверки/финала) — на всех ширинах, из run.returnsBySource. */}
                    {s.key === 'prod' && badges.map(b => <ReturnBadgeView key={b.key} b={b} />)}
                  </span>
                  {/* Полосы бэк/фронт под «Производством» (одна под другой, строки). */}
                  {s.key === 'prod' && (
                    <span style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, margin: `${SP.xxs}px 0 ${SP.xs}px 0` }}>
                      <LaneBar info={lanes.back} />
                      <LaneBar info={lanes.front} />
                    </span>
                  )}
                </span>
                {/* Шеврон: только у нажимаемых строк (pending/absent — не кнопки), поворот при раскрытии. */}
                <span style={{ display: 'inline-flex', alignItems: 'center', alignSelf: 'start', paddingTop: 2 }}>
                  <Chevron expanded={expanded === s.key} reduced={reduced} />
                </span>
              </button>
            )}
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
