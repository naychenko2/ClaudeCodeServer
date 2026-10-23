import { useEffect, useRef, useState, type ReactNode } from 'react';
import { C, R, FS, FONT, CHAT_MAX_W } from '../../../lib/design';
import { useFeature, FLAGS } from '../../../lib/featureFlags';
import { useFeaturesIndex, featureOf } from '../../../lib/featuresIndex';
import { useFeatureRuns, useFeatureRunsLoading, useFeatureRunsError } from '../../../lib/featureRuns';
import { openChatById } from '../../../lib/openChat';
import { STAGE_NAME } from '../../../lib/chatGroups';
import { CHAT_ROLE_LABEL } from '../../../lib/featureChatRole';
import { roleLabel } from '../../../lib/roleLabel';
import { getPersonaById } from '../../../lib/personas';
import type { FeatureGlyph as GlyphKind } from './FeatureGlyphs';
import { FeatureBannerSheet } from './FeatureBannerSheet';
import { statusPhrase, uniquePersonaNames } from './pillModel';
import { stepProgressFromIndex, formatStepProgress } from '../../../lib/featureProgress';
import type { FeatureIndexFeature, FeatureChatLink, FeatureRailKey, FeatureRailTone } from '../../../types/featureIndex';

// Плашка фичи в чате — ОДНА КОМПАКТНАЯ СТРОКА (макет feature-banner-1, вариант «А · Строка»
// + кликабельные точки этапов из «Г · Мини-хронология»; записка Майи 20.10, решения Андрея).
//
// Строка отвечает на четыре вопроса и ничего сверх (записка §1):
//   • ГДЕ МЫ     — 7 точек этапов (пройденные, текущий, впереди) + подпись «{этап} · {состояние}»;
//   • ЧТО ОТ МЕНЯ — одно главное действие, когда от вас что-то нужно (waiting/stopped/merge_pending);
//   • КТО РАБОТАЕТ — чип роли ЭТОГО чата («этот чат — Ш2 · фронт» / «ключевой»);
//   • КУДА ПЕРЕЙТИ — точки ведут на страницу фичи (вкладка «Хронология»); «Мерж…» НЕ на панели.
//
// Источник правды — БЭК: строка фичи из индекса (lib/featuresIndex, GET features/index)
// несёт state (9 состояний), rail (7 стадий), attention, keySessionId и связь «чат × фича».
// Фронт НЕ пересчитывает — только раскладывает в точки/подпись/действие/роль.
//
// Показывается под ДВУМЯ флагами: turn-plan-feature-mode (поверхность режима «Исполнение
// фичи») и features-overview (индекс). Dark launch: оба выключены — строки нет, лента как раньше.
//
// Честность (задача, п.5): «N из M» и «все готово» НЕ выводим; пока Б-pr2 не принят, прогресс
// по шагам на панели не выводится вообще (будет в Ф-pr).
//
// Мобила (360 px): название уходит (оно в шапке), строка переносится, тач-цели ≥ 40;
// тап по «где мы» открывает шторку (FeatureBannerSheet — отдельный шаг Ф-pl-b, здесь не изобретаем).

// Тон строки (макет .va.t-*): run — идёт, wait — ждёт/к мержу, stop — стоит, closed — закрыта.
type BannerTone = 'run' | 'wait' | 'stop' | 'closed';

// Точка этапа (макет .dots i): done — пройдена (зелёная), active — текущая (вытянутая,
// цвет тона), pending — впереди (дорожка), absent — нет в трекере (пунктир).
// «Впереди» (pending) и absent НЕ кликабельны (задача, п.2); done/active — кликабельны
// (ведут на страницу фичи, вкладка «Хронология», к этому этапу).
interface StageDot {
  key: FeatureRailKey;
  state: 'done' | 'active' | 'pending' | 'absent';
  tone: FeatureRailTone | null;
  clickable: boolean;
}

// Главное действие строки (задача, п.2, «таблица попапа»):
//   waiting      → «Ответить {имя}» (фокус в композер / чат, где ждут);
//   stopped      → «Чат {имя}» (в чат упавшего исполнителя);
//   merge_pending→ «Открыть» (без «Мерж…» — он в попапе и на странице);
//   иначе       → «Открыть страницу фичи».
interface BannerAction {
  label: string;
  kind: 'reply' | 'chat' | 'open' | 'page';
}

// Имя активной стадии (для «{этап} · …»); null — активной нет (всё впереди/закрыта).
function activeStageName(f: FeatureIndexFeature): string | null {
  const stage = f.rail.find(s => s.state === 'active');
  return stage ? STAGE_NAME[stage.key] : null;
}

// «Где мы» — словами, из ОДНОГО ИСТОЧНИКА с пилюлей (statusPhrase, pillModel.ts):
//   running → «{этап} · {идущие}» (имена — nowPersonaIds индекса, фолбэк now0.personaId);
//   waiting → «{этап} · ждёт вас»; stopped → «стоит · {имя}» (имя — из тревоги);
//   merged → «смержена» (без даты, как в пилюле); merge_pending → «Мерж · ждёт согласия»;
//   merging → «Мерж · идёт»; closed → «закрыта»; not_started/idle — своё слово.
function whereText(f: FeatureIndexFeature): string {
  const stage = activeStageName(f);
  // «Идут»: уникальные id из индекса (nowPersonaIds; фолбэк — now0.personaId).
  const nowIds = (f.nowPersonaIds && f.nowPersonaIds.length > 0)
    ? f.nowPersonaIds
    : (f.now0?.personaId ? [f.now0.personaId] : []);
  // merging: имя из attention.personaId недостижимо (при state=merging тревоги нет,
  // FeatureProjector.cs:1663) — подпись остаётся «Мерж · идёт» без имени; имя потребует
  // поля merge.personaId в строке индекса (бэк-шаг).
  const p = statusPhrase(f.state, stage, {
    running: uniquePersonaNames(nowIds),
    stopped: f.attention?.personaId ? (getPersonaById(f.attention.personaId)?.name ?? '') : '',
  });
  return `${p.bold}${p.post}`;
}

// Тон текста «где мы»: тревога (wait/stop) — цветная, иначе обычный.
function whereTone(f: FeatureIndexFeature): 'wait' | 'stop' | 'run' {
  if (f.state === 'waiting' || f.state === 'merge_pending') return 'wait';
  if (f.state === 'stopped') return 'stop';
  return 'run';
}

// Тон строки (фон/кромка): тревога (wait/stop) — цветная, закрыта (merged/closed) — серая,
// иначе нейтр. Выводится из state (единая точка правды; bucket/state не расходятся).
function bannerTone(f: FeatureIndexFeature): BannerTone {
  if (f.state === 'merged' || f.state === 'closed') return 'closed';
  if (f.state === 'waiting' || f.state === 'merge_pending') return 'wait';
  if (f.state === 'stopped') return 'stop';
  return 'run';
}

// Глиф состояния (для шторки): из state (единая точка правды; bucket/state не расходятся).
// merged → done (галочка), closed → closed (без мержа), тревога → wait/stopped, иначе run.
function bannerGlyph(f: FeatureIndexFeature): GlyphKind {
  if (f.state === 'merged') return 'done';
  if (f.state === 'closed') return 'closed';
  if (f.state === 'waiting' || f.state === 'merge_pending') return 'wait';
  if (f.state === 'stopped') return 'stopped';
  return 'run';
}

// Главное действие (таблица попапа, задача п.2): по state фичи.
//   waiting → «Ответить {имя}» (имя — attention.personaId, резолвит фронт);
//   stopped → «Чат {имя}»;
//   merge_pending → «Открыть» (без «Мерж…» — он в попапе и на странице);
//   иначе → «Открыть страницу фичи».
function mainAction(f: FeatureIndexFeature): BannerAction {
  const name = f.attention?.personaId ? (getPersonaById(f.attention.personaId)?.name ?? '') : '';
  if (f.state === 'waiting') return { label: name ? `Ответить ${name}` : 'Ответить', kind: 'reply' };
  if (f.state === 'stopped') return { label: name ? `Чат ${name}` : 'Чат', kind: 'chat' };
  if (f.state === 'merge_pending') return { label: 'Открыть', kind: 'open' };
  return { label: 'Открыть страницу фичи', kind: 'page' };
}

// Точки этапов: из rail (бэк считает state/tone). «Впереди» (pending) и absent —
// не кликабельны; done/active — кликабельны (ведут на страницу фичи, вкладка «Хронология»).
function stageDots(f: FeatureIndexFeature): StageDot[] {
  return f.rail.map(seg => ({
    key: seg.key,
    state: seg.state,
    tone: seg.tone,
    clickable: seg.state === 'done' || seg.state === 'active',
  }));
}

// Роль этого чата: ключевой — «ключевой», иначе ОДНА функция roleLabel (сырые
// taskRole/stepRef/lane): у step — «{stepRef} · {бэк|фронт}», у прочих ролей — слово;
// без роли (null/неизвестно) — фолбэк на вид связи (куратор/оркестратор/исполнитель).
function roleOf(f: FeatureIndexFeature, link: FeatureChatLink, sessionId: string): { role: string; isKey: boolean } {
  const isKey = f.keySessionId != null && sessionId === f.keySessionId;
  if (isKey) return { role: 'ключевой', isKey: true };
  const byRole = roleLabel(link.taskRole, link.stepRef, link.lane);
  return { role: byRole || (CHAT_ROLE_LABEL[link.kind] ?? 'участник'), isKey: false };
}

// Чистая модель строки: строка фичи + связь «этот чат × фича» → что рисует плашка.
// sessionId — id текущего чата: «этот чат ключевой» = (sessionId === keySessionId)
// (ADR-031 §1: бэк вид key в chats не отдаёт).
export interface FeatureBannerRow {
  tone: BannerTone;
  glyph: GlyphKind;
  dots: StageDot[];
  // «Где мы»: слова из statusPhrase (общий источник с пилюлей): «{этап} · {идущие}»,
  // «{этап} · ждёт вас», «стоит · {имя}», «смержена» и т.д.
  where: string;
  whereTone: 'wait' | 'stop' | 'run';
  action: BannerAction;
  role: string;
  isKey: boolean;
}

export function featureBannerRow(f: FeatureIndexFeature, link: FeatureChatLink, sessionId: string): FeatureBannerRow {
  const { role, isKey } = roleOf(f, link, sessionId);
  return {
    tone: bannerTone(f),
    glyph: bannerGlyph(f),
    dots: stageDots(f),
    where: whereText(f),
    whereTone: whereTone(f),
    action: mainAction(f),
    role,
    isKey,
  };
}

// Тон строки → фон/рамка (макет .va.t-*): wait → infoBg, stop → dangerBg,
// closed → bgInset (серая, второй план), run → bgCard с рамкой borderLight.
function toneStyle(tone: BannerTone): React.CSSProperties {
  switch (tone) {
    case 'wait':
      return { background: C.infoBg, border: '1px solid transparent' };
    case 'stop':
      return { background: C.dangerBg, border: '1px solid transparent' };
    case 'closed':
      return { background: C.bgInset, border: '1px solid transparent' };
    case 'run':
    default:
      return { background: C.bgCard, border: `1px solid ${C.borderLight}` };
  }
}

// Цвет активной точки по tone (макет .dots i.a.*): wait → C.info, stop → C.danger,
// run/loop → C.textHeading. tone null у active — редкость, дефолт (textHeading).
function activeDotColor(tone: FeatureRailTone | null): string {
  switch (tone) {
    case 'wait': return C.info;
    case 'stopped': return C.danger;
    case 'loop': return C.warning;
    case 'run':
    default: return C.textHeading;
  }
}

// Геометрия точки (макет .dots i): 6×6, скругление 3; active — вытянутая 16×6.
// Значений 6/16/3 в шкале SP/R нет (SP.xs=4, R.sm=6) — размеры макета, константы.
const DOT = 6;
const DOT_ACTIVE = 16;
const DOT_R = 3;

// Точка этапа: done — зелёная, active — вытянутая в цвет тона, pending — дорожка,
// absent — пунктирный контур. Кликабельные (done/active) — <button>, ведут на страницу
// фичи (вкладка «Хронология», к этому этапу); pending/absent — <i> (не кликабельны).
// data-testid — у КАЖДОЙ точки (и <i>, и <button>): тест отличает кликабельную от
// «впереди» по тегу элемента, а не по наличию testid.
function StageDotView({ dot, onOpen }: { dot: StageDot; onOpen: (key: FeatureRailKey) => void }) {
  const base: React.CSSProperties = { display: 'block', height: DOT, borderRadius: DOT_R, flexShrink: 0 };
  let style: React.CSSProperties;
  if (dot.state === 'done') style = { ...base, width: DOT, background: C.success };
  else if (dot.state === 'active') style = { ...base, width: DOT_ACTIVE, background: activeDotColor(dot.tone) };
  else if (dot.state === 'absent') style = { ...base, width: DOT, background: 'transparent', boxShadow: `inset 0 0 0 1px ${C.dashed}` };
  else style = { ...base, width: DOT, background: C.progressTrack };

  if (!dot.clickable) return <i data-testid={`feature-dot-${dot.key}`} aria-hidden="true" style={style} />;
  return (
    <button
      type="button"
      data-testid={`feature-dot-${dot.key}`}
      aria-label={`Этап: ${STAGE_NAME[dot.key]}`}
      title={STAGE_NAME[dot.key]}
      onClick={() => onOpen(dot.key)}
      style={{ ...style, border: 'none', padding: 0, cursor: 'pointer', position: 'relative' }}
    >
      {/* Тач-цель ≥ 40 на 360: точка 6 px, кликабельная область — расширена (inset). */}
      <span style={{ position: 'absolute', inset: -9, display: 'block' }} />
    </button>
  );
}

// Чип роли этого чата (макет .role): mono, фон bgSelected; у ключевого — accent/accentLight
// + вес 700 (макет .role.key).
function RoleChip({ label, isKey }: { label: string; isKey: boolean }) {
  return (
    <span
      data-testid="feature-role-chip"
      style={{
        fontFamily: FONT.mono, fontSize: FS.xs,
        fontWeight: isKey ? 700 : 400,
        color: isKey ? C.accent : C.textSecondary,
        background: isKey ? C.accentLight : C.bgSelected,
        borderRadius: 6, padding: '0 5px', whiteSpace: 'nowrap', flexShrink: 0,
      }}
    >
      {label}
    </span>
  );
}

// Кнопка строки (макет .lnk/.btn): текст, без рамки, 12/600, C.accent (действие) или
// C.textSecondary (тихое). Общий Button не подходит: не прокидывает data-testid/aria.
function RowButton({ testId, onClick, children, tone = 'accent', style }: {
  testId?: string;
  onClick: () => void;
  children: ReactNode;
  tone?: 'accent' | 'muted';
  style?: React.CSSProperties;
}) {
  return (
    <button
      type="button"
      data-testid={testId}
      onClick={onClick}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4,
        border: 'none', background: 'transparent', cursor: 'pointer',
        color: tone === 'accent' ? C.accent : C.textSecondary,
        fontFamily: FONT.sans, fontWeight: 600, fontSize: FS.sm,
        padding: '4px 6px', borderRadius: R.md, whiteSpace: 'nowrap', flexShrink: 0,
        ...style,
      }}
    >
      {children}
    </button>
  );
}

interface FeatureBannerProps {
  // Id текущего чата (для featureOf и «этот чат ключевой»).
  sessionId: string;
  // Проект чата — область запроса индекса (null — чат вне проектов).
  projectId: string | null;
  isMobile?: boolean;
  // Задача-владелец чата (Session.TaskId) — для шторки (Ф-pl-b).
  chatTaskId?: string | null;
  // Главное действие, когда ждут В ЭТОМ чате: фокус в композер (ChatPanel передаёт
  // бамп focusSignal; без колбэка действие открывает страницу фичи).
  onFocusComposer?: () => void;
}

// Плашка фичи в слоте баннеров ChatPanel. Возвращает null, когда строки нет
// (флаги выключены или чат не принадлежит ни одной фиче).
export function FeatureBanner({ sessionId, projectId, isMobile = false, chatTaskId, onFocusComposer }: FeatureBannerProps) {
  // Гейт: оба флага (режим «Исполнение фичи» + «Обзор фич»).
  const featureMode = useFeature(FLAGS.turnPlanFeatureMode);
  const featuresOverview = useFeature(FLAGS.featuresOverview);
  const enabled = featureMode && featuresOverview;
  // Индекс фич проекта (реактивная подписка; один запрос на проект). Возвращаемое
  // значение здесь не нужно (шторка берёт данные из runs) — но вызов хука обязателен:
  // он грузит стор, из которого читает featureOf(sessionId) (строка + переключатель).
  useFeaturesIndex(projectId, enabled);
  // Агрегаты фич чата (GET features?sessionId=) — данные ШТОРКИ (Ф-pl-b): 7 этапов,
  // исполнители, переключатель фич. Тот же источник, что у попапа, без догрузки features/{id}.
  const runs = useFeatureRuns(sessionId, enabled);
  // Состояния запроса шторки (F-pop-a): loading/error — шторка показывает их,
  // а не молча не рисуется (минор: aria-expanded не виси в воздухе).
  const runsLoading = useFeatureRunsLoading(sessionId);
  const runsError = useFeatureRunsError(sessionId);
  // Элемент «где мы»: на него возвращается фокус при закрытии шторки (Ф-pl-b).
  const whereRef = useRef<HTMLSpanElement | null>(null);
  // Мультифича: какая фича показана (индекс в featureOf, порядок — от бэка).
  const [selIdx, setSelIdx] = useState(0);
  // Шторка: тап по строке (мобила И десктоп) открывает; сбрасывается при смене чата.
  const [sheetOpen, setSheetOpen] = useState(false);

  // Фичи, к которым принадлежит чат; первая — сильнейшая связь (порядок — от бэка).
  const entries = featureOf(sessionId);
  // Кламп, если фич стало меньше; сброс выбора/шторки при смене чата.
  const idx = Math.min(selIdx, Math.max(0, entries.length - 1));
  const entry = entries[idx];
  const feature = entry?.feature;
  const link = entry?.link;

  useEffect(() => {
    setSelIdx(0);
    setSheetOpen(false);
  }, [sessionId]);

  if (!enabled || entries.length === 0 || !feature || !link) return null;

  const model = featureBannerRow(feature, link, sessionId);
  const more = Math.max(0, entries.length - 1);

  // «Фича →» / клик по точке: страница фичи на вкладке «Хронология» (cc-open-feature;
  // WorkspacePage слушает, fromChat — крошка «← К чату «…»»). stage — подсказка для Ф-pl-b.
  const openFeaturePage = (stage?: FeatureRailKey) => {
    window.dispatchEvent(new CustomEvent('cc-open-feature', {
      detail: { featureId: feature.featureId, fromChatId: sessionId, fromChat: true, stage },
    }));
  };
  // Главное действие (M2; эталон — featurePrimaryAction/waitingStoppedAction): цель =
  // ЧАТ ТРЕВОГИ (attention.sessionId в индексе; в run — linkedSessionId), не ключевой чат:
  //   ждут В ЭТОМ чате → фокус в композер (ответ пишут здесь);
  //   тревога в другом чате → переход в тот чат;
  //   чата тревоги нет (null) → страница фичи.
  // open/page (merge_pending/дефолт) — страница фичи.
  const onAction = () => {
    if (model.action.kind === 'reply' || model.action.kind === 'chat') {
      const target = feature.attention?.sessionId ?? null;
      if (target === sessionId) {
        if (onFocusComposer) { onFocusComposer(); return; }
        openFeaturePage();
        return;
      }
      if (target) { void openChatById(target); return; }
    }
    openFeaturePage();
  };

  // Шторка реально нарисована (не только «открыта»): runs непусты ИЛИ идёт
  // загрузка/ошибка (шторка показывает состояние). runs пуст и loading/error нет —
  // шторка не рисуется → aria-expanded=false (минор: раньше «висел» в воздухе).
  const sheetShown = sheetOpen && (runs.length > 0 || runsLoading || runsError != null);

  // «Где мы»: тап открывает шторку (Ф-pl-b) — на мобиле bottom-sheet, на десктопе
  // раскрывающуюся панель под строкой. Роль button, клавиатура Enter/Space, aria-expanded.
  // whereRef — элемент, на который возвращается фокус при закрытии шторки.
  const whereNode = (
    <span
      ref={whereRef}
      data-testid="feature-where"
      onClick={() => setSheetOpen(true)}
      role="button"
      tabIndex={0}
      aria-expanded={sheetShown}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setSheetOpen(true); } }}
      aria-label={`Фича «${feature.title}»: ${model.where}. Подробнее`}
      style={{
        fontSize: FS.sm,
        color: model.whereTone === 'wait' ? C.info : model.whereTone === 'stop' ? C.dangerText : C.textHeading,
        fontWeight: model.whereTone === 'run' ? 400 : 600,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
        cursor: 'pointer',
      }}
    >
      {model.where}
    </span>
  );

  // Честный прогресс (Ф-pr): из строки индекса; null — старые ответы (не показываем).
  // Ellipsis-рецепт (перенос из FeatureGroupHeader): узел сжимается (flexShrink:1 +
  // minWidth:0) и обрезает текст (overflow:hidden + text-overflow:ellipsis) — на узкой
  // плашке с пятью корзинами («план частично» + четыре числа) строка не выходит за край;
  // полная строка — в title/aria-label.
  const progress = stepProgressFromIndex(feature);
  const progressNode = progress && (
    <span
      data-testid="feature-progress"
      title={formatStepProgress(progress)}
      aria-label={formatStepProgress(progress)}
      style={{
        fontSize: FS.xs, color: C.textMuted,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        flexShrink: 1, minWidth: 0,
      }}
    >
      {formatStepProgress(progress)}
    </span>
  );

  // Ряд точек (7): done/active кликабельны, pending/absent — нет. aria-hidden — только
  // когда кликабельных точек НЕТ (минор 1): интерактивные кнопки внутри aria-hidden
  // скрыты от скринридера, но остаются фокусируемыми — нарушение ARIA.
  const dotsRow = (
    <span
      data-testid="feature-dots"
      aria-hidden={model.dots.some(d => d.clickable) ? undefined : 'true'}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 3, flexShrink: 0 }}
    >
      {model.dots.map(dot => (
        <StageDotView key={dot.key} dot={dot} onOpen={k => openFeaturePage(k)} />
      ))}
    </span>
  );

  // Чип роли этого чата: «этот чат — {роль}» (ключевой — просто «ключевой»).
  const roleChip = (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0, whiteSpace: 'nowrap' }}>
      <span style={{ fontSize: FS.xs, color: C.textMuted }}>этот чат —</span>
      <RoleChip label={model.role} isKey={model.isKey} />
    </span>
  );

  // Мобила (360 px, макет .va): строка переносится, название уходит (оно в шапке),
  // тач-цели ≥ 40; taп по «где мы» открывает шторку.
  let inner: ReactNode;
  if (isMobile) {
    inner = (
      <div
        role="region"
        aria-label="Фича этого чата"
        data-testid="feature-banner"
        data-banner-tone={model.tone}
        data-chat-task-id={chatTaskId ?? undefined}
        style={{
          ...toneStyle(model.tone),
          display: 'flex', flexDirection: 'column',
          borderRadius: R.lg, padding: '8px 10px', margin: 0,
          boxSizing: 'border-box', fontFamily: FONT.sans,
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', rowGap: 6 }}>
          {dotsRow}
          {whereNode}
          {progressNode}
          {more > 0 && (
            <RowButton testId="feature-more" tone="muted" onClick={() => setSelIdx(i => (i + 1) % entries.length)} style={{ fontSize: FS.xs }}>
              · ещё {more}
            </RowButton>
          )}
          <RowButton testId="feature-action" tone="accent" onClick={onAction} style={{ marginLeft: 'auto', minHeight: 40 }}>
            {model.action.label}
          </RowButton>
        </div>
        <div style={{ marginTop: 6 }}>{roleChip}</div>
        {sheetOpen && (
          <FeatureBannerSheet
            runs={runs}
            loading={runsLoading}
            error={runsError}
            order={entries.map(e => e.feature.featureId)}
            initialSel={idx}
            isMobile
            onClose={() => setSheetOpen(false)}
            focusTarget={whereRef}
            projectId={projectId}
          />
        )}
      </div>
    );
  } else {
    // Десктоп: ОДНА строка ≈36 px — точки · название · «где мы» · [· ещё N] · действие · роль.
    // Без переноса: nowrap + ellipsis у названия/«где мы», у точек/действия/чипа flex-shrink:0.
    // Тап по строке раскрывает панель фичи ПОД строкой (Ф-pl-b) — раскрывающаяся панель,
    // не bottom-sheet (тот паттерн — на мобиле).
    inner = (
      <>
      <div
        role="region"
        aria-label="Фича этого чата"
        data-testid="feature-banner"
        data-banner-tone={model.tone}
        data-chat-task-id={chatTaskId ?? undefined}
        style={{
          ...toneStyle(model.tone),
          display: 'flex', flexDirection: 'column',
          borderRadius: R.lg, padding: '5px 10px 5px 12px', margin: 0,
          minHeight: 36, boxSizing: 'border-box', fontFamily: FONT.sans,
        }}
      >
      <div style={{ display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'nowrap', minWidth: 0, flex: 1 }}>
        {dotsRow}
        <span
          title={feature.title}
          style={{
            fontSize: FS.base, fontWeight: 700, color: C.textHeading,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            minWidth: 40, flex: 1,
          }}
        >
          {feature.title}
        </span>
        {whereNode}
        {progressNode}
        {more > 0 && (
          <RowButton testId="feature-more" tone="muted" onClick={() => setSelIdx(i => (i + 1) % entries.length)}>
            · ещё {more}
          </RowButton>
        )}
        <RowButton testId="feature-action" tone="accent" onClick={onAction} style={{ marginLeft: 'auto' }}>
          {model.action.label}
        </RowButton>
        {roleChip}
      </div>
    </div>
      {sheetOpen && (
        <FeatureBannerSheet
          runs={runs}
          loading={runsLoading}
          error={runsError}
          order={entries.map(e => e.feature.featureId)}
          initialSel={idx}
          onClose={() => setSheetOpen(false)}
          focusTarget={whereRef}
          projectId={projectId}
        />
      )}
      </>
    );
  }

  // M1: карточка = колонка чата (950), как у композера (ChatPanel:3109).
  // Обёртка: maxWidth = CHAT_MAX_W + 2×16 (982) при padding 0 16px и border-box →
  // контент = 982 − 32 = 950 = колонка. margin '0 auto' центрирует.
  // Старая обвязка (CSS-переменные useChatGutter + marginLeft:auto) прижимала панель
  // к правому краю окна, а лента центрирована.
  // На узком окне (< 982) maxWidth не режет — баннер во всю ширину с полями.
  return (
    <div
      data-testid="feature-banner-wrap"
      style={{
        width: '100%',
        maxWidth: CHAT_MAX_W + 2 * 16,
        margin: '0 auto',
        padding: '0 16px',
        boxSizing: 'border-box',
      }}
    >
      {inner}
    </div>
  );
}
