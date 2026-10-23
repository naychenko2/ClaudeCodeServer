import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, Inbox, RotateCw } from 'lucide-react';
import { C, R, FS, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { useFeaturePage, refetchFeaturePage, useFeaturePageTab, setFeaturePageTab } from '../../lib/featurePage';
import { buildNavMarks } from '../../lib/featureNavMarks';
import { getBackNav, clearBackNav } from '../../lib/featureBackNav';
import { useFeaturesIndex } from '../../lib/featuresIndex';
import { api } from '../../lib/api';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { featureStatusTag, featureDisplayState, STAGE_NAME } from '../../lib/chatGroups';
import { openChatById } from '../../lib/openChat';
import { taskCounters } from './featurePageBody';
import { showToast } from '../../lib/toast';
import { FeatureMergeDialog } from './FeatureMergeDialog';
import { CenterViewShell } from '../CenterViewShell';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import type { FeatureGlyph as GlyphKind } from '../chat/feature/FeatureGlyphs';
import { Notice } from '../ui/Notice';
import { EmptyState } from '../ui/EmptyState';
import { Button } from '../ui/Button';
import { FeatureTimelineTab } from './FeatureTimelineTab';
import { FeaturePlansTab, plansTabCount } from './FeaturePlansTab';
import { FeatureChatsTab } from './FeatureChatsTab';
import { FeatureTasksTab } from './FeatureTasksTab';
import { FeatureEventsTab } from './FeatureEventsTab';
import { FeatureStatsTab } from './FeatureStatsTab';
import { FeatureMiniSummary } from './FeatureMiniSummary';
import {
  FeaturePageHeader, FeaturePageToolbar, FeaturePageAlert,
  featureRunStatusTag, toRunTag, type FeatureRunTag,
} from './FeaturePageHeader';
import { FeaturePageTabs } from './FeaturePageTabs';
import type { FeatureRun } from '../../types/featureRun';

// Куда ведёт закрытие (кнопка «Закрыть»):
//   chat — в исходный чат (указатель back-nav «чат → фича»);
//   list — в список (указателя нет).
// WorkspacePage исполняет выбор: закрывает страницу и ведёт в target.
export type FeaturePageBackTarget =
  | { kind: 'chat'; chatId: string }
  | { kind: 'list' };

export interface FeaturePageProps {
  // Id эпика (маршрут #/project/{pid}/feature/{fid}).
  featureId: string;
  // Проект чата (область запроса индекса); null — чат вне проектов.
  projectId: string | null;
  isMobile?: boolean;
  // Исполнитель закрытия: закрывает страницу и ведёт в target.
  onBack: (target: FeaturePageBackTarget) => void;
  // Открыть файл проекта (существующий просмотр; «Открыть макет» в «Планы и макеты»).
  onOpenFile?: (path: string) => void;
}

// Скелет загрузки (макет §5.7): 6 полос 14 px разной ширины (bgSelected, R 6, gap 10,
// пульс 1,4 с) + блок 120 (marginTop 6). При prefers-reduced-motion пульс выключен
// (инжект в шапке).
const SKELETON_WIDTHS = [70, 92, 84, 60, 88, 76];
function LoadingSkeleton() {
  return (
    <div data-testid="feature-page-skeleton" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {SKELETON_WIDTHS.map((w, i) => (
        <div
          key={i}
          className="cc-feature-page-skeleton"
          style={{ height: 14, width: `${w}%`, background: C.bgSelected, borderRadius: R.sm }}
        />
      ))}
      <div className="cc-feature-page-skeleton" style={{ height: 120, marginTop: SP.xs, background: C.bgSelected, borderRadius: R.sm }} />
    </div>
  );
}

// Пульс скелета загрузки (1,4 с); при prefers-reduced-motion — без анимации (макет §5.7).
const SKELETON_CLASS = 'cc-feature-page-skeleton';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-page-skeleton-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-page-skeleton-style';
  el.textContent =
    `@keyframes cc-feature-page-sk-pulse{0%,100%{opacity:1}50%{opacity:0.35}}` +
    `.${SKELETON_CLASS}{animation:cc-feature-page-sk-pulse 1.4s ease-in-out infinite;}` +
    `@media (prefers-reduced-motion: reduce){.${SKELETON_CLASS}{animation:none;opacity:0.55}}`;
  document.head.appendChild(el);
}

// Имя активной стадии для тега «идёт · {этап}»: первая active-стадия run.
function activeStageName(run: FeatureRun): string | null {
  const stage = (run.stages ?? []).find(s => s.state === 'active');
  return stage ? STAGE_NAME[stage.key] : null;
}

// (Мини-сводка — FeatureMiniSummary, Ф8.)

// Страница фичи (волна 3, Ф1 — каркас). Оболочка — «как файл» (CenterViewShell):
// Toolbar-шапка (закрытие, глиф, название, тег/действие/⋯), Esc, возврат к месту
// открытия; тело — мета-строка (макет §1), блок тревоги по attention (макет §2),
// липкие вкладки (макет §4) и содержимое: «Хронология» — мини-сводка (заглушка Ф8)
// + конвейер, остальные вкладки — «Скоро».
//
// Тег статуса — единая точка правды run.state (9 состояний, макет §3); старые данные
// (state нет) — фолбэк на тег индекса. Выбранная вкладка — в сторе страницы
// (useFeaturePageTab): переживает скрытие/возврат, не живёт в DOM.
export function FeaturePage({ featureId, projectId, isMobile = false, onBack, onOpenFile }: FeaturePageProps) {
  // Данные страницы (run + events) — из стора; здесь для фолбэка заголовка и 404/503.
  const page = useFeaturePage(featureId);
  const featuresOn = useFeature(FLAGS.featuresOverview);
  const index = useFeaturesIndex(projectId, featuresOn);
  const feature = index.features.find(f => f.featureId === featureId) ?? null;
  // «Открыть ключевой чат» (общее пустое «Планы и макеты»): тот же переход, что в шапке.
  const keyChat = feature?.keySessionId ?? null;
  const onOpenKeyChat = keyChat ? () => { void openChatById(keyChat); } : undefined;
  // Корень «просмотра» — для правила «скрытый центр не перехватывает Esc» (useEscapeClose):
  // если страница скрыта (display:none → offsetParent=null), Esc её не закрывает. Как у файла.
  const rootRef = useRef<HTMLDivElement>(null);

  // Название: из индекса, фолбэк — эпик страницы, затем нейтральное. Контракт:
  // UI обязан пережить отсутствие любого поля ответа — эпик без title не роняет страницу.
  const title = feature?.title ?? (page.status === 'ok' ? (page.page.run.epic?.title ?? 'Фича') : 'Фича');

  // Глиф + тег: тег — run.state (9 состояний), фолбэк — индекс (старые данные);
  // глиф — как раньше (из индекса, display state).
  const display = feature ? featureDisplayState(feature) : null;
  const glyph: GlyphKind = display ? display.kind : 'run';
  let tag: FeatureRunTag | null = null;
  if (page.status === 'ok' && page.page.run.state) {
    tag = featureRunStatusTag(page.page.run.state, activeStageName(page.page.run));
  } else if (feature) {
    tag = toRunTag(featureStatusTag(feature));
  }

  // Вкладка: из стора страницы (сохраняется при скрытии/возврате).
  const tab = useFeaturePageTab();

  // Закрытие: куда ведёт. Из чата — в исходный чат («К чату «…»»); из списка/диплинк —
  // в список: мобила «К списку», десктоп «Назад».
  const back = getBackNav();
  const fromChat = back && back.to.kind === 'feature' && back.to.id === featureId ? back.from : null;
  const [fromTitle, setFromTitle] = useState<string | null>(null);
  useEffect(() => {
    if (!fromChat) { setFromTitle(null); return; }
    let cancelled = false;
    void api.chats.get(fromChat)
      .then(s => { if (!cancelled) setFromTitle(s.name ?? null); })
      .catch(() => { if (!cancelled) setFromTitle(null); });
    return () => { cancelled = true; };
  }, [fromChat]);

  let target: FeaturePageBackTarget;
  let label: string;
  if (fromChat) {
    target = { kind: 'chat', chatId: fromChat };
    label = fromTitle ? `К чату «${fromTitle}»` : 'К чату';
  } else if (isMobile) {
    target = { kind: 'list' };
    label = 'К списку';
  } else {
    target = { kind: 'list' };
    label = 'Назад';
  }
  // Esc закрывает страницу — общим хуком useEscapeClose (в CenterViewShell): не
  // перехватываем, если печатают в поле/редакторе или уже открыт диалог/меню.
  const goBack = () => { onBack(target); clearBackNav(); };

  // === Мерж (Ф6): общая модалка подтверждения → POST /merge ===
  // Состояния: pending (запрос в полёте, кнопка «Запускаю…» заблокирована),
  // успех (тост + state → merging через перезапрос), 409 (state → merging, без
  // красной ошибки — идемпотентность), прочий сбой (сообщение сервера + «Повторить»).
  const [mergeOpen, setMergeOpen] = useState(false);
  const [mergePending, setMergePending] = useState(false);
  const [mergeError, setMergeError] = useState<string | null>(null);
  // Синхронный гард от двойного клика: state батчится (два клика в одном тике оба
  // видят pending=false), ref — нет. Сбрасывается, когда запрос завершился.
  const mergeInFlight = useRef(false);

  const openMergeDialog = () => {
    setMergeError(null);
    setMergeOpen(true);
  };

  const closeMergeDialog = () => {
    setMergeOpen(false);
    setMergePending(false);
    setMergeError(null);
    mergeInFlight.current = false;
  };

  const confirmMerge = async () => {
    if (mergeInFlight.current) return;
    mergeInFlight.current = true;
    setMergePending(true);
    setMergeError(null);
    try {
      await api.tasks.mergeFeature(featureId);
      // Успех: тост + state → merging (перезапрос страницы).
      showToast('Задача мержа создана', 'Марк начал мерж');
      refetchFeaturePage(featureId);
      closeMergeDialog();
    } catch (e) {
      const err = e as Error & { status?: number };
      if (err.status === 409) {
        // «Мерж уже идёт» (идемпотентность): обновить состояние, красной ошибки нет.
        refetchFeaturePage(featureId);
        closeMergeDialog();
      } else {
        // Прочий сбой: сообщение сервера + «Повторить» (диалог остаётся открыт).
        // Гард сбрасываем — повторный клик («Повторить») должен пройтись.
        mergeInFlight.current = false;
        setMergeError(err.message || 'Сервер не ответил');
        setMergePending(false);
      }
    }
  };

  // run для тела: null вне ok (loading/error/…) — мета строится по индексу.
  const run: FeatureRun | null = page.status === 'ok' ? page.page.run : null;

  // Тело: одно из пяти состояний (макет §5.7). В ok — каркас: мета, тревога,
  // вкладки, содержимое вкладки.
  let body: ReactNode;
  if (page.status === 'loading') {
    body = <LoadingSkeleton />;
  } else if (page.status === 'error') {
    body = (
      <Notice
        tone="danger"
        icon={AlertTriangle}
        title="Не удалось загрузить фичу."
        action={
          // m1: на 360 у всех ui/Button страницы minHeight 40 (тач-цель, §6).
          <Button size="xs" variant="ghost" style={{ background: C.bgWhite, ...(isMobile ? { minHeight: 40 } : {}) }}
            onClick={() => refetchFeaturePage(featureId)}>
            <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            Повторить
          </Button>
        }
      >
        Сервер не ответил; список и чаты работают как обычно.
      </Notice>
    );
  } else if (page.status === 'notFound') {
    body = (
      <EmptyState
        compact
        icon={<Inbox size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        iconRadius={R.full}
        title="Фичи больше нет"
        titleSize={FS.lg}
        subtitle="Эпик удалён в «Задачах». Чаты остались на месте и снова живут без фичи."
        action={
          <Button size="sm" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined}
            onClick={() => onBack({ kind: 'list' })}>
            К списку чатов
          </Button>
        }
      />
    );
  } else if (page.status === 'disabled') {
    body = (
      <Notice tone="info" icon={Inbox} title="Подсистема задач выключена">
        Страница фичи недоступна, чаты работают как обычно.
      </Notice>
    );
  } else {
    // ok: мета-строка (макет §1), тревога по attention (макет §2), липкие вкладки
    // (макет §4) и содержимое: «Хронология» — мини-сводка (заглушка Ф8) + конвейер,
    // остальные — «Скоро».
    // ok: run есть (page.page.run) — локальная переменная без null.
    const okRun = page.page.run;
    const chatsCount = (page.page.chats ?? []).filter(c => !c.archived).length;
    // Пометки «в архиве»/«задача закрыта» — из данных страницы (задачи run + чаты): одни
    // на все вкладки, шапку и тревогу.
    const marks = buildNavMarks(okRun, page.page.chats);
    body = (
      <>
        <FeaturePageHeader run={okRun} feature={feature} isMobile={isMobile} tag={tag} />
        <FeaturePageAlert run={okRun} feature={feature} isMobile={isMobile} onMerge={openMergeDialog} marks={marks} />
        <FeaturePageTabs
          value={tab}
          onChange={setFeaturePageTab}
          isMobile={isMobile}
          plansCount={plansTabCount(page.page)}
          chatsCount={chatsCount}
          // «Задачи N» — ОДИН источник со строками вкладки: число задач из run
          // (taskCounters), не taskCount из индекса (индекс может расходиться с run).
          tasksCount={taskCounters(okRun).total}
          // «События N» — что реально загружено/доступно (events.length), не eventsTotal
          // (общее до обрезки бэком): лента приходит по eventsLimit (100), eventsTotal —
          // всего. «Показать все» догружает всю ленту, и счётчик становится честным.
          eventsCount={page.page.events.length}
          waitingDot={okRun.attention?.kind === 'wait'}
        />
        <div data-testid="feature-page-body" style={{ marginTop: SP.md }}>
          {tab === 'timeline' ? (
            <>
              <FeatureMiniSummary stats={page.page.stats ?? null} onOpenStats={() => setFeaturePageTab('stats')} isMobile={isMobile} />
              <FeatureTimelineTab run={okRun} marks={marks} isMobile={isMobile} />
            </>
          ) : tab === 'plans' ? (
            <FeaturePlansTab page={page.page} isMobile={isMobile} onOpenFile={onOpenFile} onOpenKeyChat={onOpenKeyChat} marks={marks} />
          ) : tab === 'chats' ? (
            <FeatureChatsTab page={page.page} keySessionId={keyChat} featureId={featureId} marks={marks} isMobile={isMobile} />
          ) : tab === 'tasks' ? (
            <FeatureTasksTab run={okRun} featureId={featureId} marks={marks} isMobile={isMobile} />
          ) : tab === 'events' ? (
            <FeatureEventsTab events={page.page.events} eventsTotal={page.page.eventsTotal} featureId={featureId} featureTitle={page.page.run.epic?.title ?? null} marks={marks} isMobile={isMobile} />
          ) : (
            // «Статистика» (Ф7a): ступени Ганта из run + stats (features/{id}); stats null — пустое состояние.
            <FeatureStatsTab stats={page.page.stats ?? null} run={okRun} featureId={featureId} featureTitle={page.page.run.epic?.title ?? null} marks={marks} isMobile={isMobile} />
          )}
        </div>
      </>
    );
  }

  // Короткая ВИДИМая подпись BackButton (Майя M2): на 360 «К чату «…»» переносится
  // на 4 строки и ломает шапку. Видимое — «Чат»/«К списку», полный текст — в title/aria-label.
  const shortLabel = fromChat ? 'Чат' : (isMobile ? 'К списку' : 'Назад');

  return (
    <CenterViewShell
      isMobile={isMobile}
      onClose={goBack}
      closeLabel={shortLabel}
      mobileTitle={label}
      closeTitle="Закрыть"
      closeTestId="feature-page-close"
      name={
        // Название — единственный гибкий элемент (как имя файла): flex-item с ellipsis
        // (сжимается «…»). У страницы расширения нет — длинное название обрезается «…» (m1 Майи).
        <span style={{ flex: '1 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{title}</span>
      }
      nameTitle={title}
      nameTestId="feature-page-title"
      leading={
        // Глиф состояния — аналог плитки типа файла (14 px).
        <span data-testid="feature-page-glyph" style={{ display: 'flex', flexShrink: 0 }}>
          <FeatureGlyph kind={glyph} size={14} />
        </span>
      }
      trailing={
        // Тег (1280) · главное действие (1280) · ⋯ (макет §1/§11).
        <FeaturePageToolbar run={run} feature={feature} isMobile={isMobile} tag={tag} onMerge={openMergeDialog} />
      }
      rootRef={rootRef}
      rootTestId="feature-page"
      // Мобила: страница — прямой ребёнок колонки центра (flex: 1); десктоп — в Island
      // (FeatureCenter), корень height:100% по умолчанию.
      rootStyle={isMobile ? { flex: 1, overflow: 'hidden', height: 'auto' } : undefined}
    >
      <>
        {/* Тело: прокрутка, поля 16 16 24 (макет §5.4; 16 сверху — как у файла, Майя m2). */}
        <div style={{ flex: 1, overflow: 'auto', minHeight: 0, padding: `${SP.lg}px ${SP.lg}px ${SP.xl}px` }}>
          {body}
        </div>
        {/* Модалка подтверждения мержа (Ф6): портал, рендерится при mergeOpen. */}
        {mergeOpen && (
          <FeatureMergeDialog
            title={title}
            branch={feature?.branch ?? null}
            pending={mergePending}
            error={mergeError}
            onCancel={closeMergeDialog}
            onConfirm={confirmMerge}
          />
        )}
      </>
    </CenterViewShell>
  );
}
