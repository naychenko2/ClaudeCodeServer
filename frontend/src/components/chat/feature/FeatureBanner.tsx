import { useEffect, useState, type ReactNode } from 'react';
import { C, FS, R, FONT, SHADOW } from '../../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { ArrowLeft, ArrowRight, ChevronDown } from 'lucide-react';
import { useFeature, FLAGS } from '../../../lib/featureFlags';
import { useFeaturesIndex, featureOf, type FeatureOfEntry } from '../../../lib/featuresIndex';
import { useFeatureRuns } from '../../../lib/featureRuns';
import { useBackNavFrom, getBackNav, clearBackNav, recordBackNav } from '../../../lib/featureBackNav';
import { openChatById } from '../../../lib/openChat';
import { openTaskInSection } from '../../../lib/tasks';
import { featureDisplayState } from '../../../lib/chatGroups';
import { CHAT_ROLE_LABEL } from '../../../lib/featureChatRole';
import { roleLabel } from '../../../lib/roleLabel';
import { FeatureGlyph, type FeatureGlyph as GlyphKind } from './FeatureGlyphs';
import { FeatureRail } from './FeatureRail';
import { FeatureModeBody } from './FeatureModeBody';
import { FeatureBannerSheet } from './FeatureBannerSheet';
import { dateOnly } from './featureTime';
import type { FeatureIndexFeature, FeatureChatLink } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';

// Плашка фичи в чате (макет features-overview.html, .fs; записка
// features-overview-proposal.md, «Общее для всех вариантов: плашка фичи»).
//
// Сценарий: «я открыл чат Дениса и не понимаю, где фича и где главный чат».
// Плашка отвечает: глиф состояния · название фичи · мини-рельса · статус ·
// счётчик · роль ЭТОГО чата в фиче · кнопка перехода в ключевой чат.
//
// Фронт НИЧЕГО не считает: источник правды — индекс фич (lib/featuresIndex.ts,
// бэк считает bucket/attention/rail/summary/ключевой чат). Компонент только
// раскладывает строку фичи в глиф/тон/статус/счётчик/роль.
//
// Показывается под ДВУМЯ флагами: turn-plan-feature-mode (поверхность режима
// «Исполнение фичи») и features-overview (загрузка индекса). Dark launch:
// оба выключены — плашки нет, лента как раньше.
//
// Тон фона (записка Майи): ждёт вас/к мержу — C.infoBg, упала — C.dangerBg,
// закрытая/смержена — C.bgInset (серая, второй план), идёт/доработка — C.bgCard
// с рамкой C.borderLight.
//
// Ф4 (волна 1 «Обзор фич»):
//   • В КЛЮЧЕВОМ чате вместо перехода «Ключевой чат →» — «Конвейер ▾»: раскрывает
//     под плашкой тело режима «Исполнение фичи» (FeatureModeBody) + легенду; данные
//     конвейера — из стора featureRuns по фиче.
//   • «← назад» (слева) — только ПОСЛЕ перехода из плашки чата фичи в ключевой чат:
//     возвращает в исходный чат одним кликом. При обычном открытии чата — нет.
//   • Мультифича (чат связан с ≥ 2 фичами): «фича X · ещё N» с выбором следующей;
//     порядок — как отдал бэк (по силе связи), фронт не сортирует и не считает.

// Тон фона плашки.
type BannerTone = 'wait' | 'stop' | 'closed' | 'run';

// Кнопка справа: 'key' — в ключевой чат (openChatById), 'feature' — открыть
// фичу в трекере (openTaskInSection), когда живого ключевого чата нет;
// 'pipeline' — «Конвейер ▾» в ключевом чате (раскрывает FeatureModeBody).
interface BannerButton {
  label: string;
  kind: 'key' | 'feature' | 'pipeline';
}

// Модель плашки: чистая функция от строки фичи + связи чата. Тестируется
// без рендера (SSR не нужен для логики).
export interface FeatureBannerModel {
  tone: BannerTone;
  glyph: GlyphKind;
  status: string;
  // m2: тон текста статуса (макет .tn-wait/.tn-stop): 'wait' → C.info 600,
  // 'stop' → C.dangerText 600, 'run' → обычный (textHeading, как у «идёт»).
  statusTone: 'wait' | 'stop' | 'run';
  counter: string;
  // Роль этого чата: «ключевой» (сам ключевой) или «этот чат: Ш2 · бэк».
  role: string;
  isKey: boolean;
  button: BannerButton;
}

// Подпись роли чата по виду связи — общий словарь (lib/featureChatRole, один с
// featurePageBody): curator/orchestrator/executor. Мёртвая ветка `key` убрана
// (ADR-031 §1: бэк вид key в chats не отдаёт — ключевой чат приходит как
// curator/orchestrator и определяется по keySessionId, не по kind).

// Чистая модель: строка фичи + связь «этот чат × фича» → что рисует плашка.
// sessionId — id текущего чата: «этот чат ключевой» = (sessionId === keySessionId)
// (ADR-031 §1: бэк вид key в chats не отдаёт).
export function featureBannerModel(
  feature: FeatureIndexFeature,
  link: FeatureChatLink,
  sessionId: string,
): FeatureBannerModel {
  const st = featureDisplayState(feature);
  const isKey = feature.keySessionId != null && sessionId === feature.keySessionId;

  // Тон фона: тревога (wait/stop) → цветной, закрыта/смержена → серая, иначе нейтр.
  let tone: BannerTone;
  if (st.alarm === 'wait') tone = 'wait';
  else if (st.alarm === 'stop') tone = 'stop';
  else if (st.kind === 'closed' || st.kind === 'done') tone = 'closed';
  else tone = 'run';

  // Статус: у смерженной — «смержена <дата>» (локальное время, featureTime),
  // у закрытой без мержа — «закрыта», иначе статус из featureDisplayState.
  let status: string;
  if (st.kind === 'done') {
    const d = dateOnly(feature.summary?.mergedAt ?? null);
    status = d ? `смержена ${d}` : 'смержена';
  } else if (st.kind === 'closed') {
    status = 'закрыта';
  } else {
    status = st.status;
  }

  // Счётчик: принятые/все шаги + число возвратов (↺N).
  const counter = feature.loops > 0
    ? `${feature.stepsAccepted}/${feature.stepsTotal} ↺${feature.loops}`
    : `${feature.stepsAccepted}/${feature.stepsTotal}`;

  // Роль этого чата: ключевой — «ключевой», иначе — ОДНА функция roleLabel (сырые
  // taskRole/stepRef/lane): у step — «{stepRef} · {бэк|фронт}», у прочих ролей — слово;
  // без роли (null/неизвестно) — фолбэк на вид связи (куратор/оркестратор/исполнитель).
  let role: string;
  if (isKey) {
    role = 'ключевой';
  } else {
    const byRole = roleLabel(link.taskRole, link.stepRef, link.lane);
    role = byRole || (CHAT_ROLE_LABEL[link.kind] ?? 'участник');
  }

  // Кнопка: в КЛЮЧЕВОМ чате — «Конвейер ▾» (раскрывает тело фичи); в другом
  // чате фичи — есть живой ключевой чат → «Ключевой чат →» (openChatById, сам
  // переключает проект), живого нет (keySessionId = null) → «Открыть фичу»
  // (openTaskInSection на эпик, без перехода в чат). Архивный ключевой
  // (keyArchived, контракт §1) помечен в подписи — человек не попадает в
  // архив без предупреждения (R3, минор 1).
  let button: BannerButton;
  if (isKey) {
    button = { label: 'Конвейер', kind: 'pipeline' };
  } else if (feature.keySessionId != null) {
    button = { label: feature.keyArchived ? 'Ключевой чат (в архиве)' : 'Ключевой чат', kind: 'key' };
  } else {
    button = { label: 'Открыть фичу', kind: 'feature' };
  }

  // m2: тон текста статуса по тону плашки (макет .tn-wait/.tn-stop): ждёт → 'wait',
  // стоит → 'stop', иначе → 'run' (обычный текст).
  const statusTone: 'wait' | 'stop' | 'run' = tone === 'wait' ? 'wait' : tone === 'stop' ? 'stop' : 'run';

  return { tone, glyph: st.kind, status, statusTone, counter, role, isKey, button };
}

// «← назад»: роль исходного чата в ЭТОЙ фиче — roleLabel (сырые taskRole/stepRef/lane);
// без роли — вид связи. Пусто — исходный чат не принадлежит этой фиче (переход не из её плашки).
export function backRoleLabel(sourceChatId: string, featureId: string): string {
  const entry = featureOf(sourceChatId).find(e => e.feature.featureId === featureId);
  if (!entry) return '';
  const byRole = roleLabel(entry.link.taskRole, entry.link.stepRef, entry.link.lane);
  return byRole || (CHAT_ROLE_LABEL[entry.link.kind] ?? 'участник');
}

// Мультифича: число ДРУГИХ фич (entries.length − 1). Фронт не сортирует и не
// считает порядок — только показывает, сколько ещё фич у чата (порядок — от бэка).
export function moreFeaturesCount(entries: FeatureOfEntry[]): number {
  return Math.max(0, entries.length - 1);
}

// Тон фона → стиль подложки/рамки плашки.
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

// m1: кнопка плашки по макету .go/.back: текст (C.accent у .go, C.textSecondary у
// .back), БЕЗ рамки, 12/600, padding 4/8 (go) / 4/6 (back), без переноса. Общий Button
// не подходит: не прокидывает data-testid/aria-expanded (нужно для тестов и a11y).
function GoButton({ testId, onClick, children, ariaExpanded, tone = 'go', style }: {
  testId?: string;
  onClick: () => void;
  children: ReactNode;
  ariaExpanded?: boolean;
  tone?: 'go' | 'back';
  style?: React.CSSProperties;
}) {
  const isGo = tone === 'go';
  return (
    <button
      data-testid={testId}
      aria-expanded={ariaExpanded}
      onClick={onClick}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4,
        border: 'none', background: 'transparent',
        color: isGo ? C.accent : C.textSecondary,
        // M2: нативная <button> не наследует font-family — задаём Hanken явно
        // (кнопки .go/.back и «· ещё N» — иначе Arial).
        fontFamily: FONT.sans,
        // m8: «← назад» (back) — вес 400 (макет .back), «.go» — 600
        fontWeight: isGo ? 600 : 400, fontSize: FS.sm,
        padding: isGo ? '4px 8px' : '4px 6px',
        borderRadius: R.md, cursor: 'pointer', whiteSpace: 'nowrap',
        flexShrink: 0,
        ...style,
      }}
    >
      {children}
    </button>
  );
}

// m3: чип роли по макету .role: mono, фон C.bgSelected; у ключевого C.accentLight /
// C.accent. n2: у ключевого вес 700 (макет .role.key: font-weight:700). Тот же вид,
// что в шторке (FeatureBannerSheet) — плашка и шторка не расходятся внутри фичи.
function RoleChip({ label, isKey }: { label: string; isKey: boolean }) {
  return (
    <span style={{
      fontFamily: FONT.mono, fontSize: FS.xs,
      fontWeight: isKey ? 700 : 400,
      color: isKey ? C.accent : C.textSecondary,
      background: isKey ? C.accentLight : C.bgSelected,
      borderRadius: 6, padding: '0 5px', whiteSpace: 'nowrap', flexShrink: 0,
    }}>
      {label}
    </span>
  );
}

// M2: карточка «Конвейер ▾» (макет .fsx) — отдельная карточка ПОД плашкой, не
// раскрытие внутри: max-height 430, overflow auto, SHADOW.card, фон bgWhite, рамка
// borderLight, R.xl. Содержимое — FeatureModeBody (режим «Исполнение фичи» + легенда).
// Вынесена в компонент: тест M2 рендерит её напрямую и проверяет max-height/overflow.
export function PipelineCard({ run, sessionId, isMobile }: {
  run: FeatureRun;
  sessionId: string;
  isMobile: boolean;
}) {
  return (
    <div
      data-testid="feature-pipeline-body"
      style={{
        margin: '6px 14px 0',
        border: `1px solid ${C.borderLight}`,
        borderRadius: R.xl,
        background: C.bgWhite,
        boxShadow: SHADOW.card,
        padding: '10px 12px 12px',
        maxHeight: 430,
        overflow: 'auto',
      }}
    >
      <FeatureModeBody
        sessionId={sessionId}
        todos={[]}
        runs={[run]}
        isMobile={isMobile}
        variant="card"
        headerPad="0"
        bodyPad="0"
        headerBorder="none"
      />
    </div>
  );
}

interface FeatureBannerProps {
  // Id текущего чата (для featureOf и «этот чат ключевой»).
  sessionId: string;
  // Проект чата — область запроса индекса (null — чат вне проектов).
  projectId: string | null;
  isMobile?: boolean;
}

// Плашка фичи в слоте баннеров ChatPanel. Возвращает null, когда плашки нет
// (флаги выключены или чат не принадлежит ни одной фиче).
export function FeatureBanner({ sessionId, projectId, isMobile = false }: FeatureBannerProps) {
  // Гейт: оба флага (режим «Исполнение фичи» + «Обзор фич»).
  const featureMode = useFeature(FLAGS.turnPlanFeatureMode);
  const featuresOverview = useFeature(FLAGS.featuresOverview);
  const enabled = featureMode && featuresOverview;
  // Индекс фич проекта (реактивная подписка; один запрос на проект:
  // первичная загрузка при 0→1 потребителях).
  // Захватываем сам индекс — шторка (мобила) читает из него чаты фичи.
  const index = useFeaturesIndex(projectId, enabled);
  // «← назад»: исходный чат (в памяти вкладки); null, пока перехода из плашки не было.
  const backFrom = useBackNavFrom(sessionId);

  // Мультифича: какая фича показана (индекс в featureOf, порядок — от бэка).
  const [selIdx, setSelIdx] = useState(0);
  // «Конвейер ▾»: раскрытие (локальное, сбрасывается при смене чата/фичи).
  const [fxOpen, setFxOpen] = useState(false);
  // Шторка (мобила): тап по плашке открывает; сбрасывается при смене чата.
  const [sheetOpen, setSheetOpen] = useState(false);

  // Фичи, к которым принадлежит чат; первая — сильнейшая связь (порядок — от бэка).
  const entries = featureOf(sessionId);
  // Кламп, если фич стало меньше; сброс выбора/раскрытия при смене чата.
  const idx = Math.min(selIdx, Math.max(0, entries.length - 1));
  // entries[idx] — undefined, когда entries пуст (idx = 0); берём через entry?.*,
  // т.к. деструктуризация идёт ДО early-return (нужно feature для useFeatureRuns ниже).
  const entry = entries[idx];
  const feature = entry?.feature;
  const link = entry?.link;

  // m7: агрегат фичи живёт в сторе featureRuns под КЛЮЧЕВОЙ сессией (эпик
  // SourceSessionId = keySessionId), а не под сессией исполнителя — поэтому
  // запрашиваем по keySessionId (как в ключевом чате) и находим нужную по featureId.
  // Ключевой чат: keySessionId === sessionId, запрос прежний.
  const runs = useFeatureRuns(feature?.keySessionId ?? sessionId, featureMode);

  // При смене чата одним эффектом сбрасываем: «← назад» (записанный переход
  // ведёт в ДРУГОЙ чат, а не в этот — мажем S.prev = null, макет) и локальное
  // состояние (выбор/раскрытие/шторка — иначе новый чат наследовал бы их).
  useEffect(() => {
    const back = getBackNav();
    // Сброс, если записанный переход ведёт в ДРУГОЙ чат (обычное открытие, не из плашки).
    // `to` — объект { kind, id }: сравниваем id только когда это чат (не страница фичи).
    if (back && back.to.kind === 'chat' && back.to.id !== sessionId) clearBackNav();
    setSelIdx(0); setFxOpen(false); setSheetOpen(false);
  }, [sessionId]);

  if (!enabled || entries.length === 0) return null;

  const model = featureBannerModel(feature, link, sessionId);
  const isKey = model.isKey;
  const more = moreFeaturesCount(entries);

  // «← назад»: только в ключевом чате, после перехода из плашки, и исходный чат
  // принадлежит ЭТОЙ фиче (иначе роль пуста → кнопки нет).
  const backLabel = isKey && backFrom ? backRoleLabel(backFrom, feature.featureId) : '';
  const goBack = () => {
    if (backFrom) void openChatById(backFrom);
    clearBackNav();
  };
  // «Ключевой чат →»: запоминаем, откуда пришли (для «← назад» в ключевом чате).
  const goKey = () => {
    if (!feature.keySessionId) return;
    recordBackNav(sessionId, feature.keySessionId);
    void openChatById(feature.keySessionId);
  };
  // «Конвейер ▾»: раскрывает/сворачивает тело фичи под плашкой.
  const toggleFx = () => setFxOpen(o => !o);
  // Мультифича: выбор следующей фичи (цикл по порядку от бэка).
  const cycleFeature = () => setSelIdx(i => (i + 1) % entries.length);
  // «Фича →» (Ф2.3): страница фичи. Указатель «чат → фича» и navPush исполняет
  // WorkspacePage (cc-open-feature); fromChatId = этот чат (крошка «← К чату «…»»).
  const openFeaturePage = () => {
    // fromChat: true — вход из чата: WorkspacePage запишет указатель возврата (крошка
    // «← К чату «…»»). В списке (SessionList) fromChat: false — указатель не пишется (M3).
    window.dispatchEvent(new CustomEvent('cc-open-feature', { detail: { featureId: feature.featureId, fromChatId: sessionId, fromChat: true } }));
  };

  // Тело «Конвейер ▾»: FeatureModeBody (режим «Исполнение фичи») + легенда (внутри
  // FeatureRunView). Агрегат — из стора featureRuns по фиче (runs[0] = эта фича).
  const run = runs.find(r => r.featureId === feature.featureId) ?? null;

  // Мобила (360 px, макет .app.m .fs): плашка одной строкой 44 px — глиф + название
  // (обрезка) + рельса + счётчик + [· ещё N]; справа тач-цель 40×40 («→» / «▾»). Тап по
  // телу плашки открывает шторку (FeatureBannerSheet). Статус/роль-текст скрыты (у
  // «← назад» — только иконка); горизонтального скролла нет (nowrap + ellipsis).
  // m7: рамка — по тону (toneStyle): run/key → 1px borderLight (плашка не сливается
  // с полем), wait/stop/closed → transparent. n4: высота плашки 48 (не 52):
  // кнопка-тач-цель 40 + отступы 4/4 (border-box, пересчёт макета .app.m .fs).
  if (isMobile) {
    return (
      <div
        role="region"
        aria-label="Фича этого чата"
        data-testid="feature-banner"
        data-banner-tone={model.tone}
        style={{
          ...toneStyle(model.tone),
          display: 'flex',
          flexDirection: 'column',
          borderRadius: R.lg,
          padding: '4px 4px 4px 10px',
          margin: '0 8px',
          minHeight: 48,
          boxSizing: 'border-box',
          fontFamily: FONT.sans,
        }}
      >
        <div
          data-testid="feature-banner-row"
          style={{ display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'nowrap', minHeight: 40 }}
        >
          {/* «← назад»: иконка (без текста), только после перехода из плашки (ключевой чат). */}
          {backLabel && (
            <GoButton
              testId="feature-back"
              tone="back"
              onClick={goBack}
              style={{ width: 40, height: 40, padding: 0, justifyContent: 'center' }}
            >
              <ArrowLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </GoButton>
          )}
          {/* Тело плашки: тап открывает шторку. Глиф + название + рельса + счётчик. */}
          <GoButton
            testId="feature-banner-body"
            tone="back"
            onClick={() => setSheetOpen(true)}
            style={{ flex: 1, minWidth: 0, justifyContent: 'flex-start', padding: 0, height: 'auto', gap: 9 }}
          >
            <FeatureGlyph kind={model.glyph} size={13} />
            <span
              title={feature.title}
              style={{
                flex: 1, minWidth: 40,
                fontSize: FS.base, fontWeight: 700, color: C.textHeading,
                whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              }}
            >
              {feature.title}
            </span>
            <FeatureRail rail={feature.rail} closed={feature.bucket === 'closed'} />
            <span style={{
              fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap',
              fontFamily: FONT.mono, flexShrink: 0,
            }}>
              {model.counter}
            </span>
          </GoButton>
          {/* m5: мультифича «· ещё N» в строке (N = другие фичи) — выбор следующей.
              n3: тач-цель ≥ 40 по высоте (было 20): minHeight 40, текст по центру. */}
          {more > 0 && (
            <GoButton testId="feature-more" tone="back" onClick={cycleFeature} style={{ fontSize: FS.xs, minHeight: 40 }}>
              · ещё {more}
            </GoButton>
          )}
          {/* Кнопка-тач-цель 40×40: «▾» (ключевой, конвейер) / «→» (не ключевой, в ключевой чат). */}
          <GoButton
            testId={model.button.kind === 'pipeline' ? 'feature-pipeline' : 'feature-go'}
            tone="go"
            ariaExpanded={model.button.kind === 'pipeline' ? fxOpen : undefined}
            onClick={() => {
              // M2: на 360 «Конвейер ▾» (ключевой чат) открывает ШТОРКУ, а не раскрывает
              // тело внутри плашки (как на десктопе).
              if (model.button.kind === 'pipeline') setSheetOpen(true);
              else if (model.button.kind === 'key') goKey();
              else openTaskInSection({ id: feature.featureId, projectId: feature.projectId ?? undefined });
            }}
            style={{ width: 40, height: 40, padding: 0, justifyContent: 'center' }}
          >
            {model.button.kind === 'pipeline'
              ? <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ transform: fxOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
              : <ArrowRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          </GoButton>
          {/* «Фича →» (Ф2.3): страница фичи. Компактная текстовая кнопка — тач-цель
              выше остаётся для основной роли; название плашки уступает место (обрезка).
              M2: на 360 minHeight 40 (тач-цель), десктоп — без minHeight. */}
          <GoButton testId="feature-page-go" tone="go" onClick={openFeaturePage} style={{ minHeight: 40 }}>
            Фича
            <ArrowRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </GoButton>
        </div>

        {/* M2: на 360 «Конвейер ▾» раскрывает ШТОРКУ (ниже), а не inline-тело —
            inline-тела на мобиле нет (в отличие от десктопа). */}

        {/* Шторка: тап по телу плашки или «▾» (мобила). */}
        {sheetOpen && (
          <FeatureBannerSheet
            feature={feature}
            model={model}
            projectId={projectId}
            index={index}
            run={run}
            onGoKey={goKey}
            onClose={() => setSheetOpen(false)}
          />
        )}
      </div>
    );
  }

  // Десктоп (макет .fs): ОДНА строка ≈36 px — глиф · название (обрезка) · [· ещё N] ·
  // рельса · статус (с тоном) · счётчик · роль-чип · кнопка .go. Без переноса:
  // nowrap + ellipsis у названия/статуса, у кнопки/рели/счётчика flex-shrink:0.
  // n1: карточка «Конвейер ▾» (.fsx) — РЯДОМ с плашкой, а не внутри её div:
  // фон, рамка и отступ плашки её не обнимают (в макете .fs и .fsx — соседи).
  // Плашка при открытом «Конвейере» остаётся 36 px.
  return (
    <>
      <div
        role="region"
        aria-label="Фича этого чата"
        data-testid="feature-banner"
        data-banner-tone={model.tone}
        style={{
          ...toneStyle(model.tone),
          display: 'flex',
          flexDirection: 'column',
          borderRadius: R.lg,
          padding: '5px 10px 5px 12px',
          margin: '0 14px',
          minHeight: 36,
          boxSizing: 'border-box',
          fontFamily: FONT.sans,
        }}
      >
        {/* Строка плашки: одна строка, без переноса */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'nowrap', minWidth: 0, flex: 1 }}>
          {/* «← назад»: слева, только после перехода из плашки (ключевой чат). */}
          {backLabel && (
            <GoButton testId="feature-back" tone="back" onClick={goBack}>
              ← {backLabel}
            </GoButton>
          )}
          {/* Глиф состояния фичи */}
          <FeatureGlyph kind={model.glyph} size={13} />
          {/* Название фичи: одна строка, обрезка многоточием (m4: FS.base 13) */}
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
          {/* Мультифича: «· ещё N» (N = другие фичи) — выбор следующей. */}
          {more > 0 && (
            <GoButton testId="feature-more" tone="back" onClick={cycleFeature}>
              · ещё {more}
            </GoButton>
          )}
          {/* Мини-рельса (7 сегментов) */}
          <FeatureRail rail={feature.rail} closed={feature.bucket === 'closed'} />
          {/* Статус (m2: тон текста — ждёт C.info 600, стоит C.dangerText 600, иначе обычный) */}
          <span style={{
            fontSize: FS.sm,
            color: model.statusTone === 'wait' ? C.info : model.statusTone === 'stop' ? C.dangerText : C.textHeading,
            fontWeight: model.statusTone === 'run' ? 400 : 600,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
          }}>
            {model.status}
          </span>
          {/* Счётчик шагов + возвраты */}
          <span style={{
            fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap',
            fontFamily: FONT.mono, flexShrink: 0,
          }}>
            {model.counter}
          </span>
          {/* Роль этого чата (m3: чип .role, как в шторке): «ключевой» или «· этот чат: {чип}» */}
          {model.isKey
            ? <RoleChip label={model.role} isKey />
            : (
              <span style={{ fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap', flexShrink: 0 }}>
                · этот чат: <RoleChip label={model.role} isKey={false} />
              </span>
            )}
          {/* Кнопка (m1: .go — C.accent, без рамки, 12/600): «Конвейер ▾» / «Ключевой чат →» / «Открыть фичу» */}
          <GoButton
            testId={model.button.kind === 'pipeline' ? 'feature-pipeline' : 'feature-go'}
            ariaExpanded={model.button.kind === 'pipeline' ? fxOpen : undefined}
            tone="go"
            style={{ marginLeft: 'auto' }}
            onClick={() => {
              if (model.button.kind === 'pipeline') toggleFx();
              else if (model.button.kind === 'key') goKey();
              else openTaskInSection({ id: feature.featureId, projectId: feature.projectId ?? undefined });
            }}
          >
            {model.button.label}
            {model.button.kind === 'pipeline'
              ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ transform: fxOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
              : <ArrowRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          </GoButton>
          {/* «Фича →» (Ф2.3): страница фичи. Сосед основной кнопки, не её замена:
              ключевой/конвейер ведут в чат, а это — на отдельную поверхность фичи. */}
          <GoButton testId="feature-page-go" tone="go" onClick={openFeaturePage} style={{ marginLeft: 4 }}>
            Фича
            <ArrowRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </GoButton>
        </div>
      </div>

      {/* n1: «Конвейер ▾» (ключевой чат) — карточка .fsx РЯДОМ с плашкой (вне её div). */}
      {isKey && fxOpen && run && (
        <PipelineCard run={run} sessionId={sessionId} isMobile={isMobile} />
      )}
    </>
  );
}
