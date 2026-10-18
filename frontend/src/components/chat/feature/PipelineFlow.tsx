import { useContext, useEffect, useRef, useState } from 'react';
import { ArrowRight } from 'lucide-react';
import { C, FS, SP, R, FONT, TB } from '../../../lib/design';
import { usePrefersReducedMotion } from '../../../hooks/usePrefersReducedMotion';
import { useIsMobile } from '../../../lib/breakpoints';
import { getPersonaById, usePersonasVersion } from '../../../lib/personas';
import { openTaskInSection } from '../../../lib/tasks';
import { useFeatureStepFocus } from '../../../lib/featureRuns';
import { PersonaAvatar } from '../../../features/personas/PersonaAvatar';
import { ChatSessionContext } from '../contexts';
import { FeatureGlyph } from './FeatureGlyphs';
import { Rounds } from './Rounds';
import { StepDetails } from './StepDetails';
import { hhmm } from './featureTime';
import type {
  FeatureRun, FeatureTask, FeatureStage, FeatureSourceReturns, FeatureStepChain,
  FeatureRound, FeatureRoundVerdict, StageKey, StageState, StageTone,
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
// Отступ деталей/пружин-блоков слева (макет v2 .det/.stg-rounds/.lanes: margin-left 25px):
// 16 (колонка точки) + 9 (зазор грида) — контент начинается рядом с рельсой, а не сквозь неё.
const DET_INDENT = 25;

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

// Длительность в минутах → «N мин» / «1 ч 10 мин»; min < 0 → 0. Числовой формат (в отличие
// от durationLabel, который считает от ISO-момента): для строки шага, где длительность
// приходит готовым числом (chain.durationMinutes) и тикает как база + прошедшее.
export function minutesLabel(min: number): string {
  const m = Math.max(0, Math.round(min));
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const rest = m % 60;
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

// === F8: чистая логика раскрытия/меток/пружин ===

// Нормализация линии шага (из FeatureStepChain.lane): back | front | other.
// lane в данных бывает 'back'/'front' (англ.) или 'бэк'/'фронт' (рус.) — нормализуем;
// прочие линии (тест, дизайн) — в «прочее».
export function laneOfChain(lane?: string | null): 'back' | 'front' | 'other' {
  const l = (lane ?? '').trim().toLowerCase();
  if (l === 'back' || l === 'backend' || l === 'бэк' || l === 'бэкенд') return 'back';
  if (l === 'front' || l === 'frontend' || l === 'фронт' || l === 'фронтенд') return 'front';
  return 'other';
}

// Русские формы: задача/задачи/задач.
function plural(n: number, one: string, few: string, many: string): string {
  const n10 = n % 10, n100 = n % 100;
  if (n10 === 1 && n100 !== 11) return one;
  if (n10 >= 2 && n10 <= 4 && (n100 < 10 || n100 >= 20)) return few;
  return many;
}

// Исход стадии словами (макет .ms) — БЕЗЛИЧНЫЕ формы (Майя: глагол мужского рода
// «принял/вернул» не совпадает с родом имени исполнителя): «возврат» / «принято с правками»
// / «принято» / «снято» / «идёт».
const VERDICT_WORD: Record<FeatureRoundVerdict, string> = {
  returned: 'возврат',
  accepted_with_remarks: 'принято с правками',
  accepted: 'принято',
  withdrawn: 'снято',
  running: 'идёт',
};

// Подпись-мета этапа (макет .ms, 12, C.textSecondary): «{имена} · {исход | N задач}».
// Исполнители — имена из personaIds через запятую (неизвестный id не показываем);
// исход — из verdict (словом); иначе число задач с правильным склонением.
// Нет данных — null (подпись не рисуется). pending/absent — подписи Ф6, не мета (решает вызывающий).
export function stageMeta(stage: FeatureStage | null): string | null {
  if (!stage) return null;
  if (stage.state === 'pending' || stage.state === 'absent') return null;
  // Имена исполнителей: ≤2 — все через запятую; ≥3 — первые два + «+N» (Майя: не плодить
  // длинный список в подписи-мета).
  const allNames = (stage.personaIds ?? [])
    .map(id => getPersonaById(id)?.name)
    .filter((n): n is string => !!n && n.trim() !== '');
  const names = allNames.length <= 2
    ? allNames.join(', ')
    : `${allNames.slice(0, 2).join(', ')} +${allNames.length - 2}`;
  // m-7: упавший исполнитель (tone=stopped) — исход в мете не показываем (только имя);
  // тег «стоит · …» остаётся (activeStageLabel). Иначе verdict «running» читался «идёт».
  if (stage.tone === 'stopped') return names || null;
  let second: string | null = null;
  if (stage.verdict) second = VERDICT_WORD[stage.verdict] ?? null;
  else if (stage.taskCount != null && stage.taskCount > 0) {
    second = `${stage.taskCount} ${plural(stage.taskCount, 'задача', 'задачи', 'задач')}`;
  }
  if (!names && !second) return null;
  if (names && second) return `${names} · ${second}`;
  return names || second;
}

// Метка источника возврата (макет .src): review → «с ревью», qa → «с проверки», final → «с финала».
// Цвета: review — bgSelected/textSecondary; qa — infoBg/info; final — accentLight/accent.
// Метка есть ТОЛЬКО если source не null (догадок не делаем).
const SOURCE_META: Record<string, { label: string; bg: string; fg: string }> = {
  review: { label: 'с ревью', bg: C.bgSelected, fg: C.textSecondary },
  qa:     { label: 'с проверки', bg: C.infoBg, fg: C.info },
  final:  { label: 'с финала', bg: C.accentLight, fg: C.accent },
};

// Цепочка шага по stepRef (для «Ревью»: пружина самого длинного шага из longestStepRef).
export function stepChainByRef(run: FeatureRun, ref: string | null | undefined): FeatureStepChain | null {
  if (!ref) return null;
  return (run.stepChains ?? []).find(c => c.stepRef === ref) ?? null;
}

// Есть ли у стадии раскрытие (детали):
//   cut/plan — есть задачи; prod — есть шаги; review — есть шаги с витками;
//   qa/final — есть проходы (rounds стадии); merge — всегда (строка мержа).
// Для aria-expanded/aria-controls: стадия без деталей не считается «раскрытой».
// QA/Final — по rounds стадии (не по сырым спискам задач: те — только в StepDetails).
export function stageHasDetails(key: StageKey, run: FeatureRun): boolean {
  switch (key) {
    case 'cut':   return (run.cut ?? []).length > 0;
    case 'plan':  return (run.plan ?? []).length > 0;
    case 'prod':  return (run.stepChains ?? []).length > 0;
    case 'review': return (run.stepChains ?? []).some(c => c.rounds.length > 0);
    case 'qa':
    case 'final': return (stageByKey(run, key)?.rounds?.length ?? 0) > 0;
    case 'merge': return true;
    default: return false;
  }
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

// === F8: строка шага (внутри «Производства») ===

// Глиф строки шага 14 (макет v2 stepsList): идёт — пульс C.textHeading; ждёт ревью (pending)
// — спокойный круг БЕЗ пульса; принят/закрыт без повторного ревью (accepted/returned) —
// «пройдено» (круг bgSelected + галка textMuted, как done); снят (withdrawn) — круг с чертой.
function StepGlyph({ chain }: { chain: FeatureStepChain }) {
  if (chain.outcome === 'withdrawn') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" style={{ display: 'block' }}>
        <circle cx="8" cy="8" r="6" fill="none" stroke={C.textMuted} strokeWidth="1.5" />
        <path d="M5 8h6" stroke={C.textMuted} strokeWidth="1.5" strokeLinecap="round" />
      </svg>
    );
  }
  if (chain.outcome === 'running') {
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" style={{ display: 'block' }}>
        <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.textHeading} strokeWidth="1.6" className="feature-pulse" />
        <circle cx="8" cy="8" r="3.5" fill={C.textHeading} />
      </svg>
    );
  }
  if (chain.outcome === 'pending') {
    // «Ждёт ревью»: спокойный круг без пульса (не «идёт» и не «пройдено»).
    return (
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" style={{ display: 'block' }}>
        <circle cx="8" cy="8" r="6" fill="none" stroke={C.textMuted} strokeWidth="1.5" />
      </svg>
    );
  }
  // accepted/returned — «пройдено» (закрыт: принят или закрыт без повторного ревью).
  return (
    <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true" style={{ display: 'block' }}>
      <circle cx="8" cy="8" r="6.5" fill={C.bgSelected} />
      <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.textMuted} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

// Метка источника возврата (10.5/700, R.sm): «с ревью»/«с проверки»/«с финала».
// Есть только если chain.source не null (догадок не делаем).
function SourceTag({ source }: { source?: string | null }) {
  if (!source) return null;
  const m = SOURCE_META[source];
  if (!m) return null;
  return (
    <span
      style={{
        display: 'inline-flex', alignItems: 'center', fontSize: SOURCE_TAG_SIZE, fontWeight: 700,
        borderRadius: R.sm, padding: '0 6px', lineHeight: '17px', whiteSpace: 'nowrap',
        background: m.bg, color: m.fg,
      }}
    >
      {m.label}
    </span>
  );
}

// m-6: заголовок задачи в трекере часто начинается с «{stepRef}: …» (напр. «Ш1: парсер»).
// При показе рядом с моно-номером (stepRef) ведущий «{stepRef}:» (с пробелом) отрезается,
// чтобы не было «Ш1 Ш1: парсер».
function stripStepRefPrefix(title: string, stepRef: string | null | undefined): string {
  const ref = (stepRef ?? '').trim();
  if (!ref) return title;
  const prefix = `${ref}:`;
  if (title.startsWith(prefix)) return title.slice(prefix.length).replace(/^\s+/, '');
  return title;
}

// Строка шага (макет v2 .step): кнопка, сетка 14px 1fr auto, зазор 7, minHeight 32 (десктоп,
// TB.iconHitDesktop) / 40 (мобила, TB.iconHitMobile), hover и раскрытая — C.bgSelected, R.md.
// Слева глиф 14 → номер шага (mono, muted) → название → метка источника → «· снят с выкладки».
// Справа пружина (Rounds, >6 → +N) + mono 11: длительность идущего шага (тикает) или «ждёт ревью»;
// у accepted/returned/withdrawn — только пружина, без текста (макет v2 stepsList).
// На 360px пружина не переносится (правая колонка flexShrink 0), переносится название.
function StepRow({ chain, expanded, onToggle, isMobile, now }: {
  chain: FeatureStepChain; expanded: boolean; onToggle: () => void; isMobile: boolean; now: number;
}) {
  const isRunning = chain.outcome === 'running';
  const isPending = chain.outcome === 'pending';
  // База длительности идущего шага: durationMinutes на момент получения агрегата + прошедшее
  // с тех пор (тикает, как в деталях шага). Без обратного пересчёта new Date(Date.now() − …):
  // значение бэка берём как есть, база фиксируется на первом рендере.
  const baseRef = useRef<{ min: number; at: number } | null>(null);
  if (baseRef.current === null) baseRef.current = { min: chain.durationMinutes, at: Date.now() };
  // running — длительность (тикает); pending — «ждёт ревью»; accepted/returned/withdrawn —
  // только пружина, без текста.
  const right = isRunning
    ? minutesLabel(baseRef.current.min + (now - baseRef.current.at) / 60000)
    : isPending
      ? 'ждёт ревью'
      : null;
  const minH = isMobile ? TB.iconHitMobile : TB.iconHitDesktop;
  return (
    <button
      // data-step-ref — для scrollIntoView по сигналу фокуса (F9): поиск ВНУТРИ СВОЕГО
      // PipelineFlow (rootRef.querySelector), не document.getElementById (Глеб: два смонтированных
      // PipelineFlow одной фичи дублировали id, getElementById брал первый).
      data-step-ref={chain.stepRef}
      data-testid={`step-row-${chain.stepRef}`}
      aria-expanded={expanded}
      aria-controls={expanded ? `step-details-${chain.stepRef}` : undefined}
      onClick={onToggle}
      className="cc-step-row"
      style={{
        display: 'grid', gridTemplateColumns: '14px 1fr auto', gap: '7px 7px', alignItems: 'start',
        width: '100%', minHeight: minH, padding: `${SP.xs}px ${SP.xs}px`,
        border: '1px solid transparent', borderRadius: R.md, cursor: 'pointer', textAlign: 'left',
        // K-2: fontFamily наследуется (не Arial), размер явный FS.sm (12).
        fontFamily: 'inherit', fontSize: FS.sm, color: 'inherit', background: expanded ? C.bgSelected : 'transparent',
      }}
    >
      <StepGlyph chain={chain} />
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', minWidth: 0 }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{chain.stepRef}</span>
        {/* m-6: ведущий «{stepRef}:» от названия отрезается (рядом уже моно-номер). */}
        <span style={{ color: C.textPrimary, overflowWrap: 'anywhere' }}>{stripStepRefPrefix(chain.title || 'Без названия', chain.stepRef)}</span>
        <SourceTag source={chain.source} />
        {chain.outcome === 'withdrawn' && (
          <span style={{ color: C.textMuted, whiteSpace: 'nowrap' }}>· снят с выкладки</span>
        )}
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, whiteSpace: 'nowrap' }}>
        {/* M-6: на 360 — только кольца max=3 без подписи (caption=false); длительность (right) остаётся. */}
        {chain.rounds.length > 0 && <Rounds rounds={chain.rounds} max={isMobile ? 3 : 6} caption={!isMobile} />}
        {right && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{right}</span>}
      </span>
    </button>
  );
}

// === F8: детали шага (F7) — раскрывается под строкой ===

// === F8: детали этапа (по ключу) ===

// Размеры шрифта, которых нет в шкале FS (FS.xs=11, FS.sm=12) — именованные константы
// с комментарием (Майя M5: не литералы 10/10.5 в разметке).
// Заголовок секции деталей (макет .det .h): 10.
const SECTION_H_SIZE = 10;
// Метка источника возврата (макет .src): 10.5.
const SOURCE_TAG_SIZE = 10.5;

// Заголовок секции деталей (макет .det .h): 10, 700, uppercase, letter-spacing .06em, C.textMuted.
function SectionH({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: SECTION_H_SIZE, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted }}>
      {children}
    </div>
  );
}

// Диапазон времени «ЧЧ:ММ–ЧЧ:ММ»; только старт — «ЧЧ:ММ»; нет старта — null (без висячих
// разделителей). hhmm — общий форматтер (featureTime): ISO → «ЧЧ:ММ» в местном поясе.
function timeRange(startIso?: string | null, endIso?: string | null): string | null {
  const s = hhmm(startIso);
  if (!s) return null;
  const e = hhmm(endIso);
  return e ? `${s}–${e}` : s;
}

// Статус задачи словами (нейтральная форма: «закрыто»/«в работе»/«ожидает»).
function taskStatus(t: FeatureTask): string {
  return t.status === 'done' ? 'закрыто' : t.status === 'inProgress' ? 'в работе' : 'ожидает';
}

// Ссылка «Открыть задачу →» (как в StepDetails): 12/600 C.accent, ArrowRight 13,
// minHeight 24 (мобила 40). Действие — openTaskInSection на задачу.
function TaskOpenLink({ target, isMobile }: { target: { id: string; projectId?: string }; isMobile: boolean }) {
  const [hovered, setHovered] = useState(false);
  return (
    <button
      onClick={() => openTaskInSection(target)}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      title="Открыть задачу"
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 5,
        alignSelf: 'flex-end', border: 'none', background: 'transparent', cursor: 'pointer',
        minHeight: isMobile ? 40 : 24, padding: 0,
        fontSize: FS.sm, fontWeight: 600, color: C.accent, fontFamily: 'inherit',
      }}
    >
      Открыть задачу
      <span style={{ display: 'flex', transform: hovered ? 'translateX(3px)' : 'none', transition: 'transform 0.15s' }}>
        <ArrowRight size={13} strokeWidth={2} />
      </span>
    </button>
  );
}

// Строка задачи «Разреза»/«Плана» (Майя, «итог по макетам»): глиф по статусу + заголовок +
// исполнитель (аватар 18 + имя) + статус + время (mono 11, «ЧЧ:ММ–ЧЧ:ММ») + ссылка «Открыть
// задачу →». Данные — из run.cut/run.plan (FeatureTask): personaId, status,
// claudeStartedAt/createdAt, completedAt, projectId.
function TaskLine({ t, isMobile }: { t: FeatureTask; isMobile: boolean }) {
  usePersonasVersion(); // реактивность: аватар/имя появятся, когда персона загрузится
  const persona = t.personaId ? getPersonaById(t.personaId) : undefined;
  const name = persona?.name;
  const status = taskStatus(t);
  const time = timeRange(t.claudeStartedAt ?? t.createdAt, t.completedAt);
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '14px 1fr', gap: SP.xs, alignItems: 'start', fontSize: FS.sm }}>
      <span style={{ display: 'flex', justifyContent: 'center', marginTop: 2 }}>
        {t.status === 'inProgress'
          ? <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="6.5" fill="none" stroke={C.textHeading} strokeWidth="1.6" className="feature-pulse" /><circle cx="8" cy="8" r="3.5" fill={C.textHeading} /></svg>
          : t.status === 'done'
            ? <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="6.5" fill={C.bgSelected} /><path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.textMuted} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" /></svg>
            : <span style={{ width: 14, height: 14, borderRadius: '50%', border: `1.5px dashed ${C.dashed}`, display: 'inline-block' }} />}
      </span>
      <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, flexWrap: 'wrap' }}>
          <span style={{ color: C.textPrimary, overflowWrap: 'anywhere' }}>{t.title || 'Без заголовка'}</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.xs, color: C.textMuted }}>
          {persona && <PersonaAvatar persona={persona} size={18} />}
          {name && <span style={{ color: C.textSecondary }}>{name}</span>}
          <span>{status}</span>
          {time && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, whiteSpace: 'nowrap' }}>{time}</span>}
        </div>
        <TaskOpenLink target={{ id: t.id, projectId: t.projectId ?? undefined }} isMobile={isMobile} />
      </div>
    </div>
  );
}

// Цель ссылки на задачу витка: id = reviewTaskId, projectId — из задачи в агрегате.
// null — задачи нет (ссылку не рисуем).
function resolveRoundTarget(run: FeatureRun, id: string | null | undefined): { id: string; projectId?: string } | null {
  if (!id) return null;
  const all = [
    ...(run.steps ?? []), ...(run.reviews ?? []), ...(run.qa ?? []),
    ...(run.final ? [run.final] : []), ...(run.cut ?? []), ...(run.plan ?? []),
  ];
  const t = all.find(x => x.id === id);
  return { id, projectId: t?.projectId ?? undefined };
}

// Строка прохода «Проверки»/«Финального ревью» (stages[].rounds, Майя «итог по макетам»):
// глиф по вердикту + исполнитель (аватар 18 + имя) + исход + время (mono 11, «ЧЧ:ММ–ЧЧ:ММ»)
// + ссылка «Открыть задачу →» на reviewTaskId. Нет задачи — ссылки нет; нет времени — без
// висящих разделителей. Сырых списков задач не читает — всё из витка (personaId/verdict/
// startedAt/completedAt/reviewTaskId/reason).
function RoundLine({ r, run, isMobile }: { r: FeatureRound; run: FeatureRun; isMobile: boolean }) {
  usePersonasVersion();
  const persona = r.personaId ? getPersonaById(r.personaId) : undefined;
  const name = persona?.name;
  const verdictWord = VERDICT_WORD[r.verdict] ?? null;
  const time = timeRange(r.startedAt, r.completedAt);
  const target = resolveRoundTarget(run, r.reviewTaskId);
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '14px 1fr', gap: SP.xs, alignItems: 'start', fontSize: FS.sm }}>
      <span style={{ display: 'flex', justifyContent: 'center', marginTop: 2 }}>
        {r.verdict === 'accepted'
          ? <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="6.5" fill={C.bgSelected} /><path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.textMuted} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" /></svg>
          : r.verdict === 'running'
            ? <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="6.5" fill="none" stroke={C.textHeading} strokeWidth="1.6" className="feature-pulse" /><circle cx="8" cy="8" r="3.5" fill={C.textHeading} /></svg>
            : <span style={{ width: 14, height: 14, borderRadius: '50%', border: `1.5px dashed ${C.dashed}`, display: 'inline-block' }} />}
      </span>
      <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.xs, color: C.textMuted }}>
          {persona && <PersonaAvatar persona={persona} size={18} />}
          {name && <span style={{ color: C.textSecondary }}>{name}</span>}
          {verdictWord && <span>· {verdictWord}</span>}
          {time && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, whiteSpace: 'nowrap' }}>{time}</span>}
        </div>
        {r.reason && <div style={{ overflowWrap: 'anywhere', color: C.textSecondary, fontSize: FS.xs }}>{r.reason}</div>}
        {target && <TaskOpenLink target={target} isMobile={isMobile} />}
      </div>
    </div>
  );
}

// Пружина-блок под строкой этапа (макет .stg-rounds): margin 0 0 SP 25, колонка с зазором 3.
// У «Проверки»/«Финального ревью» — по stages[].rounds; у «Ревью» — пружина самого длинного
// шага (longestStepRef) + подпись «дольше всех {ref} · {название}» (только если витков > 1).
function StageRounds({ stage, run }: { stage: FeatureStage; run: FeatureRun }) {
  const key = stage.key;
  let label: string | null = null;
  let rounds: FeatureRound[] = [];
  if (key === 'qa' || key === 'final') {
    rounds = stage.rounds ?? [];
  } else if (key === 'review') {
    const worst = stepChainByRef(run, stage.longestStepRef);
    if (worst && (worst.rounds?.length ?? 0) > 1) {
      label = `дольше всех ${worst.stepRef} · ${worst.title}`;
      rounds = worst.rounds ?? [];
    }
  }
  if (rounds.length === 0) return null;
  return (
    <div style={{ margin: `0 0 ${SP.xs}px ${DET_INDENT}px`, display: 'flex', flexDirection: 'column', gap: 3 }}>
      {label && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{label}</span>}
      <Rounds rounds={rounds} max={6} />
    </div>
  );
}

// Исполнитель мержа (из stages[merge].personaIds, как в других строках; нет — без аватара).
// Аватар 18 + имя; реактивность — usePersonasVersion (аватар появится, когда персона загрузится).
function MergeExecutor({ personaIds }: { personaIds?: string[] }) {
  usePersonasVersion();
  const id = (personaIds ?? [])[0];
  const persona = id ? getPersonaById(id) : undefined;
  if (!persona) return null;
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
      <PersonaAvatar persona={persona} size={18} />
      <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{persona.name}</span>
    </span>
  );
}

// Детали этапа (макет .det): фон C.bgCard, рамка 1px C.borderLight, R.lg, padding SP.sm,
// margin-left 25px (рельса идёт рядом, а не сквозь). На мобиле детали ШАГА — на всю ширину
// (margin-left 0 у StepDetails, не у этапа).
function StageDetails({ id, stageKey, run, expandedStep, onStep, isMobile, now }: {
  id: string; stageKey: StageKey; run: FeatureRun;
  expandedStep: string | null; onStep: (ref: string) => void; isMobile: boolean; now: number;
}) {
  const stage = stageByKey(run, stageKey);
  const surface: React.CSSProperties = {
    display: 'flex', flexDirection: 'column', gap: SP.sm,
    background: C.bgCard, border: `1px solid ${C.borderLight}`,
    borderRadius: R.lg, padding: `${SP.sm}px ${SP.sm + 2}px`,
    margin: `2px 0 ${SP.sm}px ${DET_INDENT}px`,
  };

  // «Производство»: шаги по линиям (бэк/фронт/прочее).
  if (stageKey === 'prod') {
    const chains = run.stepChains ?? [];
    if (chains.length === 0) return null;
    const groups: { lane: 'back' | 'front' | 'other'; list: FeatureStepChain[] }[] = [];
    for (const lane of ['back', 'front', 'other'] as const) {
      const list = chains.filter(c => laneOfChain(c.lane) === lane);
      if (list.length) groups.push({ lane, list });
    }
    // Заголовки линий: имена исполнителей из run.lanes (back/front), «прочее» — без имён.
    const laneHeader = (lane: 'back' | 'front' | 'other'): string => {
      if (lane === 'other') return 'прочее';
      const info = laneInfo(run)[lane];
      const names = laneNames(info.personaIds);
      return names ? `${info.label} · ${names}` : info.label;
    };
    return (
      <div id={id} className="stage-details-bgCard" style={surface}>
        {groups.map(g => (
          <div key={g.lane} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `${SP.xs}px 0 0` }}>{laneHeader(g.lane)}</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              {g.list.map(c => (
                <div key={c.stepRef}>
                  <StepRow chain={c} expanded={expandedStep === c.stepRef} onToggle={() => onStep(c.stepRef)} isMobile={isMobile} now={now} />
                  {expandedStep === c.stepRef && (
                    <StepDetails key={c.stepRef} chain={c} run={run} isMobile={isMobile} />
                  )}
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    );
  }

  // «Ревью»: «Круги ревью по шагам» — на каждый шаг с витками: ID (mono, muted, minWidth 52) + бусины.
  if (stageKey === 'review') {
    const chains = (run.stepChains ?? []).filter(c => c.rounds.length > 0);
    return (
      <div id={id} className="stage-details-bgCard" style={surface}>
        <SectionH>Круги ревью по шагам</SectionH>
        {chains.length === 0 ? (
          <div style={{ fontSize: FS.sm, color: C.textMuted }}>ревью ещё не было</div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            {chains.map(c => (
              <div key={c.stepRef} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm }}>
                <span style={{ fontFamily: FONT.mono, color: C.textSecondary, minWidth: 52, flexShrink: 0 }}>{c.stepRef}</span>
                <VerdictBeads rounds={c.rounds} />
              </div>
            ))}
          </div>
        )}
      </div>
    );
  }

  // «Проверка»/«Финальное ревью»: проходы стадии (stages[].rounds) с вердиктом и причиной.
  // Сырых списков задач не читаем (только StepDetails) — всё из готового агрегата.
  if (stageKey === 'qa' || stageKey === 'final') {
    const rounds = stage?.rounds ?? [];
    if (rounds.length === 0) return null;
    return (
      <div id={id} className="stage-details-bgCard" style={surface}>
        <SectionH>{stageKey === 'qa' ? 'Проходы проверки' : 'Финальное ревью'}</SectionH>
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {rounds.map(r => <RoundLine key={r.n} r={r} run={run} isMobile={isMobile} />)}
        </div>
      </div>
    );
  }

  // «Разрез»/«План»: задачи списком (заголовок, исполнитель, статус).
  if (stageKey === 'cut' || stageKey === 'plan') {
    const tasks = stageKey === 'cut' ? (run.cut ?? []) : (run.plan ?? []);
    if (tasks.length === 0) return null;
    return (
      <div id={id} className="stage-details-bgCard" style={surface}>
        <SectionH>Задачи этапа</SectionH>
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>{tasks.map(t => <TaskLine key={t.id} t={t} isMobile={isMobile} />)}</div>
      </div>
    );
  }

  // «Мерж»: одна строка.
  if (stageKey === 'merge') {
    const merged = run.summary?.mergedAt;
    const text = merged
      ? `Смержено ${formatDate(merged)}`
      : (stage?.state === 'active' && stage?.tone === 'wait')
        ? 'Ждёт вашего решения — Марк получит запрос'
        : 'Начнётся после финального ревью';
    return (
      <div id={id} className="stage-details-bgCard" style={surface}>
        <SectionH>Мерж</SectionH>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
          <MergeExecutor personaIds={stage?.personaIds} />
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{text}</span>
        </div>
      </div>
    );
  }

  return null;
}

// Бусины вердиктов (макет .chain): до 5 бусин, ранние свёрнуты в бусину «+N».
// Цвета по вердикту (макет v2 .bead): returned — warningBg/warningText; accepted_with_remarks —
// bgCard + пунктир C.warning; accepted — successBg/successText; running — bgSelected + пунктир C.dashed.
const BEAD: Record<FeatureRound['verdict'], { bg: string; fg: string; border?: string; text: string }> = {
  returned:              { bg: C.warningBg, fg: C.warningText, text: 'вернул' },
  accepted_with_remarks: { bg: C.bgCard, fg: C.warningText, border: C.warning, text: 'принял с правками' },
  accepted:              { bg: C.successBg, fg: C.successText, text: 'принял' },
  running:               { bg: C.bgSelected, fg: C.textHeading, border: C.dashed, text: 'идёт' },
  withdrawn:             { bg: C.bgSelected, fg: C.textMuted, text: 'снят' },
};

function VerdictBeads({ rounds }: { rounds: FeatureRound[] }) {
  if (rounds.length === 0) return null;
  const max = 5;
  const shown = rounds.length > max ? rounds.slice(-max) : rounds;
  const skip = rounds.length - shown.length;
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 3, flexWrap: 'wrap' }}>
      {skip > 0 && (
        <span style={{ display: 'inline-flex', alignItems: 'center', fontSize: FS.xs, fontWeight: 700, borderRadius: R.max, padding: '0 7px', lineHeight: '18px', whiteSpace: 'nowrap', background: C.bgSelected, color: C.textHeading, border: `1px dashed ${C.dashed}` }} title="ранние витки">
          +{skip}
        </span>
      )}
      {shown.map((r) => {
        const b = BEAD[r.verdict] ?? BEAD.running;
        return (
          <span key={r.n} style={{ display: 'inline-flex', alignItems: 'center', gap: 3, fontSize: FS.xs, fontWeight: 700, borderRadius: R.max, padding: '0 7px', lineHeight: '18px', whiteSpace: 'nowrap', background: b.bg, color: b.fg, border: b.border ? `1px dashed ${b.border}` : '1px solid transparent' }}>
            {r.n} {b.text}
          </span>
        );
      })}
    </span>
  );
}

// ISO → «15.10.2026» (дата мержа). Невалидная/пустая → ''.
function formatDate(iso?: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)}.${d.getFullYear()}`;
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
//   • F8: раскрывается КАЖДЫЙ этап (детали по ключу), шаги «Производства» — отдельные строки
//     со пружиной, у этапов — пружины-блоки (stg-rounds) и подпись-мета (.ms).
//
// Компонент «глупый»: стадии/линии/возвраты/шаги считает БЭК (run.stages / run.lanes /
// run.returnsBySource / run.stepChains), рендер только раскладывает. Нет stages → 7 pending.
export function PipelineFlow({ run }: { run: FeatureRun }) {
  const reduced = usePrefersReducedMotion();
  const isMobile = useIsMobile();
  const now = useMinuteTick();
  const lanes = laneInfo(run);
  const badges = returnBadges(run);
  // F8: управляемое раскрытие. «Производство» открыто по умолчанию; шаг — одиночно внутри этапа.
  // F9: сигнал фокуса чипа «Шаг N фичи» (стор Ф1, per chat) выставляет их по сигналу.
  const [expandedStage, setExpandedStage] = useState<StageKey | null>('prod');
  const [expandedStep, setExpandedStep] = useState<string | null>(null);
  // minHeight строки: тач-цель по TB (32 десктоп / 40 мобила).
  const rowMinH = isMobile ? ROW_MIN_H_MOBILE : ROW_MIN_H_DESKTOP;
  // Корень конвейера: скролл к шагу (F9) ищет строку ВНУТРИ СВОЕГО PipelineFlow (Глеб:
  // document.getElementById при двух смонтированных PipelineFlow одной фичи брал первый).
  const rootRef = useRef<HTMLDivElement>(null);

  // F9: сигнал фокуса: раскрыть «Производство» и шаг, чья цепочка содержит задачу
  // (taskIds ⊇ taskId); шаг не найден — раскрыто только «Производство». Одноразовый:
  // потреблённый seq запоминается — ререндер не раскрывает шаг заново и не перебивает
  // ручное раскрытие пользователя; сигнал другой фичи (featureId не совпадает) этот
  // поток не трогает; сигнал другого чата (другой sessionId) сюда не доходит.
  const sessionId = useContext(ChatSessionContext);
  const focus = useFeatureStepFocus(sessionId);
  const focusSeq = useRef(0);
  useEffect(() => {
    if (!focus || focus.seq === focusSeq.current) return;
    focusSeq.current = focus.seq;
    if (focus.featureId !== run.featureId) return;
    const stepRef = (run.stepChains ?? []).find(c => c.taskIds.includes(focus.taskId))?.stepRef ?? null;
    setExpandedStage('prod');
    setExpandedStep(stepRef);
    if (!stepRef) return;
    // Строка шага: «Производство» раскрыто по умолчанию — строка уже в DOM; если пользователь
    // сворачивал стадию — строка появится на следующий кадр: повтор до 30 кадров (паттерн ChatPanel).
    // prefers-reduced-motion — без плавной прокрутки.
    let tries = 0;
    let raf = 0;
    const tryScroll = () => {
      // Свой корень (rootRef), не document: два PipelineFlow одной фичи (карточка плана +
      // меню пилюли) — getElementById брал первый, скролл уходил не туда (Глеб).
      const el = rootRef.current?.querySelector(`[data-step-ref="${stepRef}"]`);
      if (el) {
        el.scrollIntoView({ block: 'nearest', behavior: reduced ? 'auto' : 'smooth' });
      } else if (++tries < 30) {
        raf = requestAnimationFrame(tryScroll);
      }
    };
    raf = requestAnimationFrame(tryScroll);
    return () => cancelAnimationFrame(raf);
  }, [focus, run.featureId, run.stepChains, reduced]);

  // Клик по стадии: одиночное раскрытие (открыть — закрыть предыдущую); смена стадии сбрасывает шаг.
  const onStage = (key: StageKey) => {
    const next = expandedStage === key ? null : key;
    setExpandedStage(next);
    if (next !== expandedStage) setExpandedStep(null);
  };
  // Клик по шагу: одиночное раскрытие внутри этапа.
  const onStep = (ref: string) => setExpandedStep(prev => (prev === ref ? null : ref));

  return (
    <div
      ref={rootRef}
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
        // aria-controls — только когда у стадии есть детали; иначе id не существует.
        const hasDetails = stageHasDetails(s.key, run);
        // Подпись-мета (.ms): у done/active — «{имена} · {исход | N задач}»; pending/absent — подписи Ф6.
        const meta = stageMeta(stage);
        const isExpanded = expandedStage === s.key;

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
              // метка текущего этапа (active), ×N (passes>1, не «Ревью»), бейджи возврата (prod),
              // подпись-мета (.ms, done/active).
              <button
                data-testid={`stage-row-${s.key}`}
                aria-expanded={isExpanded && hasDetails}
                aria-controls={hasDetails ? `stage-details-${s.key}` : undefined}
                onClick={() => onStage(s.key)}
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
                  {/* Подпись-мета (.ms): у done/active — «{имена} · {исход | N задач}»; нет данных — нет. */}
                  {meta && (
                    <span style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 1, overflowWrap: 'anywhere' }}>{meta}</span>
                  )}
                </span>
                {/* Шеврон: только у строк, у которых ЕСТЬ раскрытие (stageHasDetails) — у
                    «Проверки»/«Финала» без проходов раскрывать нечего (Майя: шеврон без действия).
                    pending/absent — и вовсе не кнопки. Поворот — при раскрытии. */}
                {hasDetails && (
                  <span style={{ display: 'inline-flex', alignItems: 'center', alignSelf: 'start', paddingTop: 2 }}>
                    <Chevron expanded={isExpanded} reduced={reduced} />
                  </span>
                )}
              </button>
            )}
            {/* Линии бэк/фронт (prod): под кнопкой, не внутри; margin-left 25 (рядом с рельсой). */}
            {!isPendingOrAbsent && s.key === 'prod' && (lanes.back.total > 0 || lanes.front.total > 0) && (
              <div style={{ margin: `0 0 ${SP.xs}px ${DET_INDENT}px`, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                <LaneBar info={lanes.back} />
                <LaneBar info={lanes.front} />
              </div>
            )}
            {/* Пружина-блок (stg-rounds): под кнопкой, не внутри. У «Ревью» — самый длинный шаг
                (только если витков > 1); у «Проверки»/«Финального ревью» — по stages[].rounds. */}
            {!isPendingOrAbsent && stage && (
              <StageRounds stage={stage} run={run} />
            )}
            {/* Раскрытие: детали по ключу (F8). Только когда стадия раскрыта;
                StageDetails сам отдаёт null для стадий без деталей. */}
            {isExpanded && (
              <StageDetails
                key={s.key}
                id={`stage-details-${s.key}`}
                stageKey={s.key}
                run={run}
                expandedStep={expandedStep}
                onStep={onStep}
                isMobile={isMobile}
                now={now}
              />
            )}
          </div>
        );
      })}
    </div>
  );
}
