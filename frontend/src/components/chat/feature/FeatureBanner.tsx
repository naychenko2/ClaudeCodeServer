import { useEffect, useRef, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { C, FS, FONT, SP, R } from '../../../lib/design';
import { useFeature, FLAGS } from '../../../lib/featureFlags';
import { useFeaturesIndex, featureOf } from '../../../lib/featuresIndex';
import { useFeatureRuns, useFeatureRunsLoading, useFeatureRunsError, refetchFeatureRuns } from '../../../lib/featureRuns';
import { openChatById } from '../../../lib/openChat';
import { getPersonaById } from '../../../lib/personas';
import { Button } from '../../ui';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import { statusPhrase, uniquePersonaNames } from './pillModel';
import { stepProgressFromIndex } from '../../../lib/featureProgress';
import { roleLabel } from '../../../lib/roleLabel';
import { CHAT_ROLE_LABEL } from '../../../lib/featureChatRole';
import { FeatureTruthBody } from './FeatureTruthBody';
import { FeatureTrackerBar, trackerBarAria } from './FeatureTrackerBar';
import { alarmView, featureEstimateView, featureTruthBar, stepWhatLabel, type NameOf } from './featureTruthModel';
import type { FeatureIndexFeature, FeatureChatLink } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';

// Вторая строка шапки чата — В2 «Трек» (утверждён макетом feature-chat-header-row2-v3;
// место — слот шапки под заголовком, даёт его ChatHeaderBar featureBar).
//
// Строка отвечает на три вопроса и ничего сверх (макет §В2):
//   • КТО СЕЙЧАС  — «{название} · Сейчас {Имя} — {Ш8 · задача} +N»; в тревоге — фраза
//     тоном warn/danger и действие одним тапом;
//   • НАСКОЛЬКО ПРАВДИВ ПРОГРЕСС — полоса трекера из пяти корзин на всю колонку,
//     «в master» первой;
//   • СКОЛЬКО И КОГДА — справа «{pct} % · ≈ 18:30» (оценка оркестратора; без неё — только %).
// Шеврон раскрывает сводку (FeatureTruthBody): «Сейчас», «Готовность | Срок», «По
// трекеру», «Трекер расходится», подвал «этот чат — роль · Страница фичи →».
//
// Ушло из чата (вместе со старой плашкой и шторкой): точки этапов, список этапов со
// временем, исполнители этапов — всё это на странице фичи («Хронология»), туда ведёт
// подвал. Тревога в раскрытии не повторяется: действие уже в строке.
//
// Источник правды — БЭК: строка индекса (state/attention/now) + run чата (stepProgress
// с inMaster, now, tracker, epic.note.estimate). Фронт не пересчитывает: полосы и
// проценты — featureTruthModel (общие с «Готовностью» страницы фичи).
//
// Показывается под ДВУМЯ флагами: turn-plan-feature-mode и features-overview (как прежде).
// Мобила (360): процент уходит в левую фразу («{pct} % · Сейчас {Имя} +2»), справа —
// только срок; в тревоге остаётся фраза и действие, процент гаснет (макет v3 §В2).

const nameOfPersona: NameOf = (id) => getPersonaById(id)?.name ?? null;

// Наведение шеврона — CSS :hover (дизайн-ревью F2, minor 11): на тач-устройстве мышь
// не уходит, и после тапа у шеврона оставался фон bgSelected (снимок stop-m-light).
// Паттерн FOCUS_CLASS (FeatureGroupHeader/SessionList): один <style>, класс на элементе.
const HOVER_CLASS = 'cc-feature-row-hover';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-row-hover-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-row-hover-style';
  el.textContent = `.${HOVER_CLASS}:hover{background:${C.bgSelected};}`;
  (document.head ?? document.body).appendChild(el);
}

// ── Модель строки (чистая, тестируется без DOM) ──────────────────────────────────────
export type BannerAlarm = 'wait' | 'stop';

export interface FeatureRowModel {
  // Тревога: wait — «ждёт вас»/«ждёт согласия» (warn), stop — «стоит» (danger); null — нет.
  alarm: BannerAlarm | null;
  // Фраза тревоги (целиком, тоном span'а): десктоп — с причиной, мобила — с именем.
  alarmText: string;
  // Действие тревоги (Button справа): «Ответить {Имя}» / «Чат {Имя}» / «К мержу».
  actionLabel: string;
  // «Сейчас»: первый идущий (имя + «{stepRef} · {задача}») и «+N» остальных. Имени нет —
  // строка «Сейчас» не рисуется (нижняя ветка — слово состояния).
  nowName: string | null;
  nowWhat: string | null;
  nowRest: number;
  // Слово состояния вместо «Сейчас» (statusPhrase — общий источник слов с пилюлей):
  // «смержена», «закрыта», «На паузе», «В очереди · 2 из 3», «Черновик», …
  statusWord: string | null;
  statusWordPost: string;
}

export function featureRowModel(feature: FeatureIndexFeature, run: FeatureRun | null, isMobile: boolean): FeatureRowModel {
  const none = { nowName: null, nowWhat: null, nowRest: 0, statusWord: null, statusWordPost: '' };
  const alarmName = feature.attention?.personaId ? nameOfPersona(feature.attention.personaId) : null;

  // Тревога — общие слова с попапом П2 (alarmView): «ждёт вас»/«ждёт согласия» (warn),
  // «стоит» (danger). Причина «мерж» всегда без reason (FeatureProjector.cs:1663).
  if (feature.state === 'waiting' || feature.state === 'merge_pending' || feature.state === 'stopped') {
    const a = alarmView(feature.state, feature.attention?.reason ?? null, alarmName, isMobile);
    return { alarm: a.tone === 'warn' ? 'wait' : 'stop', alarmText: a.text, actionLabel: a.actionLabel, ...none };
  }
  if (feature.state === 'merged' || feature.state === 'closed') {
    const p = statusPhrase(feature.state, null, {});
    return { alarm: null, alarmText: '', actionLabel: '', ...none, statusWord: p.bold, statusWordPost: p.post };
  }

  // Идущие: сначала run.now (имя + задача), фолбэк — имена индекса (run ещё не пришёл:
  // «Сейчас {Имя}» без «что», это честная деградация, не выдумка).
  const runNow = run?.now ?? [];
  if (runNow.length > 0) {
    const first = runNow[0];
    const named = runNow.filter(n => n.personaId).length;
    return {
      alarm: null, alarmText: '', actionLabel: '',
      nowName: first.personaId ? nameOfPersona(first.personaId) : null,
      nowWhat: stepWhatLabel(first.stepRef, first.title),
      nowRest: Math.max(0, named - 1),
      statusWord: null, statusWordPost: '',
    };
  }
  const ids = feature.nowPersonaIds && feature.nowPersonaIds.length > 0
    ? feature.nowPersonaIds
    : (feature.now0?.personaId ? [feature.now0.personaId] : []);
  const names = uniquePersonaNames(ids);
  if (names) {
    const list = names.split(', ');
    return { alarm: null, alarmText: '', actionLabel: '', nowName: list[0], nowWhat: null, nowRest: Math.max(0, list.length - 1), statusWord: null, statusWordPost: '' };
  }

  // Никто не идёт — слово состояния (statusPhrase: очередь/пауза/черновик/не начата/…).
  const p = statusPhrase(feature.state, null, {}, { holdReason: feature.holdReason, queue: feature.queue });
  return { alarm: null, alarmText: '', actionLabel: '', ...none, statusWord: p.bold, statusWordPost: p.post };
}

// Роль этого чата (тот же разбор, что был в плашке): ключевой — «ключевой», иначе
// рольLabel по связи «чат × фича», фолбэк — вид связи.
function roleOf(f: FeatureIndexFeature, link: FeatureChatLink, sessionId: string): { role: string; isKey: boolean } {
  const isKey = f.keySessionId != null && sessionId === f.keySessionId;
  if (isKey) return { role: 'ключевой', isKey: true };
  const byRole = roleLabel(link.taskRole, link.stepRef, link.lane);
  return { role: byRole || (CHAT_ROLE_LABEL[link.kind] ?? 'участник'), isKey: false };
}

interface FeatureBannerProps {
  sessionId: string;
  projectId: string | null;
  isMobile?: boolean;
  chatTaskId?: string | null;
  // Ждут В ЭТОМ чате: фокус в композер (ChatPanel), не переход.
  onFocusComposer?: () => void;
}

// Строка фичи в шапке чата (слот featureBar у ChatHeaderBar). Возвращает null, когда
// строки нет (флаги выключены или чат не принадлежит ни одной фиче).
export function FeatureBanner({ sessionId, projectId, isMobile = false, chatTaskId, onFocusComposer }: FeatureBannerProps) {
  const featureMode = useFeature(FLAGS.turnPlanFeatureMode);
  const featuresOverview = useFeature(FLAGS.featuresOverview);
  const enabled = featureMode && featuresOverview;
  // Индекс — строки фич и связь «чат × фича» (стор, один запрос на проект).
  useFeaturesIndex(projectId, enabled);
  // Run'ы чата — данные полосы («в master»), «Сейчас», расхождений и оценки. Тот же
  // источник, что у попапа пилюли, без догрузки features/{id}.
  const runs = useFeatureRuns(sessionId, enabled);
  const runsLoading = useFeatureRunsLoading(sessionId);
  const runsError = useFeatureRunsError(sessionId);
  const [selIdx, setSelIdx] = useState(0);
  const [expanded, setExpanded] = useState(false);
  const bodyRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    setSelIdx(0);
    setExpanded(false);
  }, [sessionId]);

  const entries = featureOf(sessionId);
  const idx = Math.min(selIdx, Math.max(0, entries.length - 1));
  const entry = entries[idx];
  const feature = entry?.feature;
  const link = entry?.link;
  if (!enabled || entries.length === 0 || !feature || !link) return null;

  const run = runs.find(r => r.featureId === feature.featureId) ?? null;
  const model = featureRowModel(feature, run, isMobile);
  const more = Math.max(0, entries.length - 1);

  // Полоса: из run (с inMaster), фолбэк — строка индекса (те же числа без inMaster).
  const sp = run?.stepProgress ?? stepProgressFromIndex(feature);
  const bar = featureTruthBar(sp, run?.stepProgress?.inMaster ?? null);
  const est = run ? featureEstimateView(run, Date.now()) : null;

  const openFeaturePage = () => {
    window.dispatchEvent(new CustomEvent('cc-open-feature', {
      detail: { featureId: feature.featureId, fromChatId: sessionId, fromChat: true },
    }));
  };
  // Действие тревоги (тот же маршрут, что был в плашке, M2): цель — ЧАТ ТРЕВОГИ.
  const onAction = () => {
    if (model.actionLabel === 'К мержу') { openFeaturePage(); return; }
    const target = feature.attention?.sessionId ?? null;
    if (target === sessionId) {
      if (onFocusComposer) { onFocusComposer(); return; }
      openFeaturePage();
      return;
    }
    if (target) { void openChatById(target); return; }
    openFeaturePage();
  };

  const role = roleOf(feature, link, sessionId);
  const alarmColor = model.alarm === 'wait' ? C.warningText : C.dangerText;

  // Левая фраза: тревога — тоном целиком; иначе «{название} · …» (на мобиле название
  // не дублируется — оно в шапке; процент становится ведущим).
  const statusPhraseText = model.nowName
    ? (
      <>
        Сейчас <b style={{ color: C.textHeading, fontWeight: 600 }}>{model.nowName}</b>
        {!isMobile && model.nowWhat ? ` — ${model.nowWhat}` : ''}
        {model.nowRest > 0 ? ` +${model.nowRest}` : ''}
      </>
    )
    : model.statusWord
      ? (
        <>
          <b style={{ color: C.textHeading, fontWeight: 600 }}>{model.statusWord}</b>
          {model.statusWordPost}
        </>
      )
      : null;

  // Нижний отступ строки (10, макет .r2) — на самой строке, не на слоте шапки:
  // пустой слот (чат без фичи) не добавляет шапке ни пикселя. На мобиле разделитель —
  // линия слота, своего нижнего поля нет.
  return (
    <div
      data-testid="feature-banner"
      data-banner-alarm={model.alarm ?? undefined}
      data-chat-task-id={chatTaskId ?? undefined}
      style={isMobile ? undefined : { paddingBottom: 10 }}
    >
      <div
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0,
          ...(isMobile ? { minHeight: 48 } : null),
        }}
      >
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 5, padding: '3px 0' }}>
          {/* alignItems: center (дизайн-ревью F2, minor 6): по baseline строка прыгала
              с 41 до 49 px, когда вместо «Сейчас» вставала кнопка тревоги 24. */}
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
            {model.alarm ? (
              <span
                data-testid="feature-row-alarm"
                style={{ fontSize: FS.sm, fontWeight: 600, color: alarmColor, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}
              >
                {model.alarmText}
              </span>
            ) : (
              <>
                {/* Порядок сжатия (дизайн-ревью F2, minor 7): при нехватке места первым
                    сжимается название (большой flexShrink, пол не ниже 120), фраза
                    «Сейчас» — только после него: иначе резались оба сразу пополам. */}
                {!isMobile && (
                  <span
                    data-testid="feature-row-title"
                    title={feature.title}
                    style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 120, flexShrink: 4 }}
                  >
                    <b style={{ color: C.textHeading, fontWeight: 600 }}>{feature.title}</b>
                  </span>
                )}
                {isMobile && bar && (
                  <span data-testid="feature-row-pct" style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.textHeading, flexShrink: 0, whiteSpace: 'nowrap' }}>
                    {bar.percent} %
                  </span>
                )}
                {statusPhraseText && (
                  <span data-testid="feature-row-status" style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0, flexShrink: 1 }}>
                    {(isMobile ? (bar ? ' · ' : '') : ' · ')}{statusPhraseText}
                  </span>
                )}
              </>
            )}
            <span style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: SP.sm, flexShrink: 0 }}>
              {model.alarm ? (
                <Button
                  variant="primary"
                  size="xs"
                  onClick={onAction}
                  // Высота действия — по макету: 24 (десктоп) / 32 (мобиле).
                  style={isMobile ? { height: 32, minHeight: 32 } : undefined}
                >
                  {model.actionLabel}
                </Button>
              ) : bar ? (
                /* Процент при слове состояния тоже (дизайн-ревью F2, minor 9): на десктопе
                   его не было у «смержена»/«Простаивает», а на мобиле был — одна величина
                   в двух ширинах. Не показывается он только при тревоге (выше). */
                <>
                  {!isMobile && (
                    <span data-testid="feature-row-pct" style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap' }}>
                      {bar.percent} %
                    </span>
                  )}
                  {est?.rowEta && (
                    <span data-testid="feature-row-eta" style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted, whiteSpace: 'nowrap' }}>
                      {isMobile ? '' : '· '}{est.rowEta}
                    </span>
                  )}
                </>
              ) : null}
            </span>
          </div>
          {bar && (
            <FeatureTrackerBar
              bar={bar}
              ariaLabel={trackerBarAria(bar)}
              testId="feature-row-bar"
              style={{ width: '100%', flexShrink: 1 }}
            />
          )}
        </div>
        <button
          type="button"
          data-testid="feature-row-expand"
          aria-expanded={expanded}
          aria-label={expanded ? 'Свернуть сводку фичи' : 'Подробнее о фиче'}
          onClick={() => setExpanded(e => !e)}
          className={HOVER_CLASS}
          style={{
            width: isMobile ? 40 : 28, height: isMobile ? 40 : 28, borderRadius: R.md,
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
            color: C.textMuted, border: 'none', background: 'transparent', cursor: 'pointer', flexShrink: 0, padding: 0,
          }}
        >
          <span style={{ display: 'flex', transform: expanded ? 'rotate(180deg)' : 'none', transition: 'transform 150ms' }}>
            <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </span>
        </button>
      </div>

      {/* Раскрытие — вниз внутри шапки (макет .exp): десктоп — две колонки, мобила —
          одна, потолок 380 с прокруткой. Пустые секции рисует FeatureTruthBody. */}
      {expanded && (
        <div
          ref={bodyRef}
          data-testid="feature-row-body"
          style={{
            borderTop: `1px solid ${C.borderLight}`, paddingTop: 10,
            ...(isMobile ? { maxHeight: 380, overflowY: 'auto' } : null),
          }}
        >
          {run ? (
            <FeatureTruthBody
              run={run}
              sessionId={sessionId}
              isMobile={isMobile}
              roleText={role.role}
              isKey={role.isKey}
              onOpenPage={openFeaturePage}
              more={more}
              onCycle={more > 0 ? () => setSelIdx(i => (i + 1) % entries.length) : undefined}
              columns={!isMobile}
            />
          ) : runsLoading ? (
            // Состояния запроса run (F-pop-a): скелет 3 строк / «Повторить» — не пустота.
            <div data-testid="feature-row-skeleton" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
              {[0, 1, 2].map(i => <div key={i} style={{ height: 14, borderRadius: R.sm, background: C.bgInset }} />)}
            </div>
          ) : runsError != null ? (
            <div data-testid="feature-row-error" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
              Подробности не загрузились
              <Button variant="secondary" size="xs" onClick={() => refetchFeatureRuns(sessionId)}>Повторить</Button>
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
