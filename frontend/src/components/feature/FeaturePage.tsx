import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Inbox } from 'lucide-react';
import { R, FS, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { useFeaturePage, refetchFeaturePage, useFeatureMainTab, setFeatureMainTab, stepsTabCount, type FeatureBrief } from '../../lib/featurePage';
import { useMe } from '../../lib/defaultPersona';
import { buildNavMarks } from '../../lib/featureNavMarks';
import { getBackNav, clearBackNav, registerFeaturePage, recordFeatureReturn } from '../../lib/featureBackNav';
import { useFeaturesIndex } from '../../lib/featuresIndex';
import { api } from '../../lib/api';
import { featureStatusTag, featureDisplayState } from '../../lib/chatGroups';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { showToast } from '../../lib/toast';
import { FeatureMergeDialog } from './FeatureMergeDialog';
import { CenterViewShell } from '../CenterViewShell';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import type { FeatureGlyph as GlyphKind } from '../chat/feature/FeatureGlyphs';
import { Notice } from '../ui/Notice';
import { EmptyState } from '../ui/EmptyState';
import { Button } from '../ui/Button';
import { FeaturePlansTab, plansTabCount } from './FeaturePlansTab';
import { FeatureTasksTab, allFeatureTasks } from './FeatureTasksTab';
import { FeatureProgressTab, type FeatureProgressTabProps } from './FeatureProgressTab';
import { FeatureReviewTab } from './FeatureReviewTab';
import { stageLabel } from './FeatureStatsBits';
import { ensurePersonasLoaded, getPersonaById, usePersonasVersion } from '../../lib/personas';
import {
  FeaturePageToolbar, FeaturePageAlert,
  featurePageStatusTag, toRunTag, type FeatureRunTag,
} from './FeaturePageHeader';
import { FeaturePageTabs } from './FeaturePageTabs';
import { FeatureFirstScreen } from './FeatureFirstScreen';
import { analysisLineState } from './FeatureAnalysisModel';
import type { FeatureRun, MergeTailDecision } from '../../types/featureRun';

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
  // Владелец проекта (для гейта кнопки «Закрыть фичу», F2); null — старый проект
  // без поля: владелец по умолчанию (тот же приём, что у ProjectIntroCard).
  projectOwnerId?: string | null;
  isMobile?: boolean;
  // Исполнитель закрытия: закрывает страницу и ведёт в target.
  onBack: (target: FeaturePageBackTarget) => void;
  // Открыть файл проекта (существующий просмотр; «Открыть макет» в «Материалы»).
  onOpenFile?: (path: string) => void;
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

// Имя активной стадии для тега «идёт · {этап}»: brief.now.stage — тот же источник,
// что у сводки (FeatureBriefBlock.tsx:75), а не первая active-стадия run: тег тулбара
// и сводка не должны показывать разные этапы (Ш2). Пустой/отсутствующий stage — null
// (тега этапа нет); неизвестный ключ — как есть (stageLabel).
function activeStageName(brief: FeatureBrief | null | undefined): string | null {
  const stage = brief?.now?.stage;
  return stage ? stageLabel(stage) : null;
}

// Вкладка «Ход» + имена персон: getPersonaById сам по себе не реактивен
// (lib/personas.ts:75) — обёртка подписывается на версию стора и просит загрузку
// (тот же приём, что LeadChip). Подписка на обёртке, не на странице: подъём стора
// персон не должен перерисовывать всю страницу.
function FeatureProgressWithNames(props: Omit<FeatureProgressTabProps, 'nameOf'>) {
  usePersonasVersion();
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  return <FeatureProgressTab {...props} nameOf={(id) => getPersonaById(id)?.name ?? null} />;
}

// Страница фичи (волна 3, Ф1 — каркас). Оболочка — «как файл» (CenterViewShell):
// Toolbar-шапка (закрытие, глиф, название, тег/действие/⋯), Esc, возврат к месту
// открытия; тело — мета-строка (макет §1), блок тревоги по attention (макет §2),
// липкие вкладки (макет §4) и содержимое: «Хронология» — блок «Сводка» (волна 6, Ф6:
// заменяет мини-сводку Ф8) + конвейер, остальные вкладки — «Скоро».
//
// Тег статуса — единая точка правды run.state (9 состояний, макет §3); старые данные
// (state нет) — фолбэк на тег индекса. Выбранная вкладка — в сторе страницы
// (useFeatureMainTab): переживает скрытие/возврат, не живёт в DOM.
export function FeaturePage({ featureId, projectId, projectOwnerId, isMobile = false, onBack, onOpenFile }: FeaturePageProps) {
  // Данные страницы (run + events) — из стора; здесь для фолбэка заголовка и 404/503.
  const page = useFeaturePage(featureId);
  const index = useFeaturesIndex(projectId);
  const feature = index.features.find(f => f.featureId === featureId) ?? null;
  // Корень «просмотра» — для правила «скрытый центр не перехватывает Esc» (useEscapeClose):
  // если страница скрыта (display:none → offsetParent=null), Esc её не закрывает. Как у файла.
  const rootRef = useRef<HTMLDivElement>(null);

  // Кнопка «Закрыть фичу» (F2): только владелец проекта — бэковый гард принимает
  // пользовательский JWT, этот гейт чисто UI-ный. Фолбэка «поля нет — владелец по
  // умолчанию» нет: бэк отдаёт ownerId у всех проектов (миграция ProjectManager),
  // а для разрушительного действия отсутствие данных — не разрешение, а неизвестность.
  const me = useMe();
  const canClose = !!me.userId && projectOwnerId === me.userId;

  // Название: из индекса, фолбэк — эпик страницы, затем нейтральное. Контракт:
  // UI обязан пережить отсутствие любого поля ответа — эпик без title не роняет страницу.
  const title = feature?.title ?? (page.status === 'ok' ? (page.page.run.epic?.title ?? 'Фича') : 'Фича');

  // Ключевой чат: id нужен вкладке «Чаты», а общее пустое «Материалы» — переход в него.
  // Переход — тот же, что у остальных мест страницы (FS-FKEY): сначала запись возврата
  // к фиче (плашка «← к фиче {название}» в чате), потом открытие. Объявлен под `title`:
  // запись требует название.
  const keyChat = feature?.keySessionId ?? null;
  const onOpenKeyChat = keyChat
    ? () => {
        recordFeatureReturn(featureId, { kind: 'chat', id: keyChat }, title);
        void openChatById(keyChat);
      }
    : undefined;

  // Глиф + тег: тег — run.state (9 состояний), фолбэк — индекс (старые данные);
  // глиф — как раньше (из индекса, display state).
  const display = feature ? featureDisplayState(feature) : null;
  const glyph: GlyphKind = display ? display.kind : 'run';
  let tag: FeatureRunTag | null = null;
  if (page.status === 'ok' && page.page.run.state) {
    tag = featurePageStatusTag(page.page.run.state, activeStageName(page.page.brief), {
      holdReason: page.page.run.hold?.reason, queue: page.page.run.queue,
    });
  } else if (feature) {
    tag = toRunTag(featureStatusTag(feature));
  }

  // Вкладка: из стора страницы (сохраняется при скрытии/возврате).
  const mainTab = useFeatureMainTab();

  // Регистрация страницы в канале окна возврата (GC-F11R4): окно снимка живёт ради этого
  // открытия страницы — закрытие гасит его (отложенная проверка в featureBackNav), а
  // перерегистрация (мнимый unmount→mount StrictMode) её отменяет. Регистрация — только
  // сигнал жизни страницы; само окно живёт в сторе.
  useEffect(() => registerFeaturePage(featureId), [featureId]);

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
  // успех (тост + state → merging через перезапрос), 409 без кода (state → merging,
  // без красной ошибки — идемпотентность), прочий сбой (сообщение сервера + «Повторить»).
  // Закрытый хвост (FS-XF1, §8.3): решение о нём — состояние страницы, диалог
  // его только показывает; без решения при хвосте «Мерж» неактивен (и гард ниже).
  // 409 с машинным кодом tail_* — не закрытие и не красная ошибка: диалог ждёт
  // новый выбор по перечитанным рискам (FS-XT1, TasksController.cs:1645,1670).
  const [mergeOpen, setMergeOpen] = useState(false);
  const [mergePending, setMergePending] = useState(false);
  const [mergeError, setMergeError] = useState<string | null>(null);
  const [tailChoice, setTailChoice] = useState<MergeTailDecision | null>(null);
  const [tailNotice, setTailNotice] = useState<string | null>(null);
  // Синхронный гард от двойного клика: state батчится (два клика в одном тике оба
  // видят pending=false), ref — нет. Сбрасывается, когда запрос завершился.
  const mergeInFlight = useRef(false);

  // Есть ли закрытый хвост по последнему снимку (тот же источник, что у диалога:
  // attention.mergeRisks). Фронт сам хвост не выводит.
  const hasMergeTail = () => (run?.attention?.mergeRisks ?? []).some((r) => r.kind === 'tail');

  const openMergeDialog = () => {
    setMergeError(null);
    setTailChoice(null);
    setTailNotice(null);
    setMergeOpen(true);
  };

  const closeMergeDialog = () => {
    setMergeOpen(false);
    setMergePending(false);
    setMergeError(null);
    setTailChoice(null);
    setTailNotice(null);
    mergeInFlight.current = false;
  };

  const confirmMerge = async () => {
    if (mergeInFlight.current) return;
    // Диалог блокирует «Мерж» без решения, но это тот же гард и на странице:
    // мерж с хвостом без решения бэк отвергает 409 tail_decision_required.
    if (hasMergeTail() && !tailChoice) return;
    mergeInFlight.current = true;
    setMergePending(true);
    setMergeError(null);
    try {
      await api.tasks.mergeFeature(featureId, tailChoice ?? undefined);
      // Успех: тост + state → merging (перезапрос страницы).
      showToast('Задача мержа создана', 'Марк начал мерж');
      refetchFeaturePage(featureId);
      closeMergeDialog();
    } catch (e) {
      const err = e as Error & { status?: number; body?: { code?: string } };
      const code = err.status === 409 ? err.body?.code : undefined;
      if (code === 'tail_decision_required' || code === 'tail_withdraw_not_applied') {
        // Хвост требует решения (или изменился, пока шёл запрос): диалог остаётся
        // открытым, это не сбой сети и не красная ошибка. Выбор сбрасывается,
        // риски перечитываются перезапросом страницы; при «изменился» — предупреждение.
        mergeInFlight.current = false;
        setMergePending(false);
        setTailChoice(null);
        setTailNotice(code === 'tail_withdraw_not_applied' ? 'Хвост изменился, проверь список' : null);
        refetchFeaturePage(featureId);
      } else if (err.status === 409) {
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
    body = <FeatureFirstScreen status="loading" mainTab={mainTab} isMobile={isMobile} onRetry={() => refetchFeaturePage(featureId)} />;
  } else if (page.status === 'error') {
    body = <FeatureFirstScreen status="error" mainTab={mainTab} isMobile={isMobile} onRetry={() => refetchFeaturePage(featureId)} />;
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
    // (макет §4) и содержимое: «Хронология» — блок «Сводка» (волна 6, Ф6) + конвейер,
    // остальные — «Скоро».
    // ok: run есть (page.page.run) — локальная переменная без null.
    const okRun = page.page.run;
    // Строки «Сейчас»: переход в задачу.
    const openActiveTask = (taskId: string) => {
      const task = allFeatureTasks(okRun).find(t => t.id === taskId);
      recordFeatureReturn(featureId, { kind: 'task', id: taskId }, title);
      openTaskInSection({ id: taskId, projectId: task?.projectId ?? undefined });
    };
    // «Ответить …» плашки «от тебя»: мерж — диалог мержа; иначе чат тревоги (запись
    // возврата к фиче + открытие). Чата нет — кнопки нет.
    const ask = page.page.brief?.ask ?? null;
    const askChat = ask?.sessionId ?? null;
    const onAnswerAsk = ask?.source === 'merge'
      ? openMergeDialog
      : askChat
        ? () => {
            recordFeatureReturn(featureId, { kind: 'chat', id: askChat }, title);
            void openChatById(askChat);
          }
        : undefined;
    const holdLike = okRun.hold?.reason === 'queue' || okRun.state === 'paused' || okRun.state === 'draft';
    // Пометки «в архиве»/«задача закрыта» — из данных страницы (задачи run + чаты): одни
    // на все вкладки, шапку и тревогу.
    const marks = buildNavMarks(okRun, page.page.chats);
    // Состояния, которых первый экран не закрывает: сторож («без движения / молчит / тихо»,
    // только когда attention нет — иначе то же говорит плашка «от тебя») и «ещё не начата».
    const lineKey = okRun.attention ? null : analysisLineState(okRun, Date.now()).key;
    const watchdogOrIdle = okRun.state === 'not_started' || lineKey === 'stuck' || lineKey === 'nudged' || lineKey === 'quiet';
    body = (
      <>
        <FeatureFirstScreen
          status="ok"
          brief={page.page.brief ?? null}
          run={okRun}
          mainTab={mainTab}
          isMobile={isMobile}
          onAnswer={onAnswerAsk}
          onOpenSteps={() => setFeatureMainTab('progress')}
          onOpenReview={() => setFeatureMainTab('review')}
          onRetry={() => refetchFeaturePage(featureId)}
          // Пауза, черновик и очередь несут свои действия («Запустить», «Возобновить»), ответ
          // без brief плашку строить не из чего, а тревога сторожа и «не начата» плашкой «от
          // тебя» не покрыты: во всех этих случаях блок состояния остаётся на её месте.
          stateBlock={holdLike || !page.page.brief || watchdogOrIdle ? <FeaturePageAlert run={okRun} feature={feature} isMobile={isMobile} onMerge={openMergeDialog} marks={marks} /> : undefined}
        />
        <FeaturePageTabs
          value={mainTab}
          onChange={setFeatureMainTab}
          isMobile={isMobile}
          plansCount={plansTabCount(page.page)}
        />
        <div data-testid="feature-page-body" style={{ marginTop: SP.md }}>
          {mainTab === 'progress' ? (
            <FeatureProgressWithNames
              stages={okRun.stages ?? []}
              next={page.page.brief?.next ?? []}
              active={page.page.brief?.active ?? []}
              isMobile={isMobile}
              // «Все N шагов этапа ›» раскрывает шаги внутри «Производства».
              stepsSlot={<FeatureTasksTab run={okRun} featureId={featureId} marks={marks} isMobile={isMobile} />}
              stepsTotal={stepsTabCount(okRun)}
              onOpenActive={openActiveTask}
            />
          ) : mainTab === 'materials' ? (
            <FeaturePlansTab page={page.page} isMobile={isMobile} onOpenFile={onOpenFile} onOpenKeyChat={onOpenKeyChat} marks={marks} featureId={featureId} projectId={projectId} />
          ) : (
            <FeatureReviewTab
              run={okRun}
              stats={page.page.stats ?? null}
              brief={page.page.brief ?? null}
              events={page.page.events}
              eventsTotal={page.page.eventsTotal}
              featureId={featureId}
              featureTitle={page.page.run.epic?.title ?? null}
              projectId={page.page.run.epic?.projectId ?? undefined}
              marks={marks}
              isMobile={isMobile}
            />
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
        <FeaturePageToolbar run={run} feature={feature} isMobile={isMobile} tag={tag} onMerge={openMergeDialog} canClose={canClose} />
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
            // Риски мержа (Ш8-wire): из attention последнего снимка страницы, а не из
            // момента открытия — при перезапросе страницы диалог перерисовывается с
            // новыми рисками (исчезли — блока нет). Отсутствия поля UI обязан пережить.
            risks={run?.attention?.mergeRisks}
            // Состав закрытого хвоста — только из данных бэка (FS-XF1): ключи страницы.
            tailKeys={run?.closedTailKeys}
            tailChoice={tailChoice}
            onTailChoice={setTailChoice}
            tailNotice={tailNotice}
            onCancel={closeMergeDialog}
            onConfirm={confirmMerge}
          />
        )}
      </>
    </CenterViewShell>
  );
}
