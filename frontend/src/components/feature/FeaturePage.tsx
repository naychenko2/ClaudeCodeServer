import { useEffect, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { AlertTriangle, Inbox, RotateCw } from 'lucide-react';
import { C, R, FS, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { useFeaturePage, refetchFeaturePage } from '../../lib/featurePage';
import { getBackNav, clearBackNav } from '../../lib/featureBackNav';
import { useFeaturesIndex } from '../../lib/featuresIndex';
import { api } from '../../lib/api';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { featureDisplayState, featureStatusTag, type FeatureStatusTag, type FeatureStatusTone } from '../../lib/chatGroups';
import { CenterViewShell } from '../CenterViewShell';
import { FeatureGlyph } from '../chat/feature/FeatureGlyphs';
import type { FeatureGlyph as GlyphKind } from '../chat/feature/FeatureGlyphs';
import { Notice } from '../ui/Notice';
import { EmptyState } from '../ui/EmptyState';
import { Button } from '../ui/Button';
import { PipelineFlow } from '../chat/feature/PipelineFlow';

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
}

// Тон тега статуса (макет §5.1): run — идёт, wait/merge — ждёт/к мержу,
// stop — стоит, loop — доработка, merged — смержена, closed — без мержа.
// Единый источник — chatGroups (та же пара типов, что у featureStatusTag).
const TAG_STYLE: Record<FeatureStatusTone, CSSProperties> = {
  run:    { background: C.bgSelected, color: C.textHeading },
  wait:   { background: C.infoBg, color: C.textPrimary },
  merge:  { background: C.infoBg, color: C.textPrimary },
  stop:   { background: C.dangerBg, color: C.dangerText, boxShadow: `inset 0 0 0 1px ${C.danger}` },
  loop:   { background: C.warningBg, color: C.warningText },
  merged: { background: C.successBg, color: C.successText },
  closed: { background: C.bgSelected, color: C.textSecondary },
};

// Точка-глиф (info) — у «ждёт вас»/«к мержу»: тон держит фон, текст — textPrimary (Р-10).
function TagDot() {
  return <span aria-hidden style={{ width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0 }} />;
}

// Тег статуса в шапке: 11/600, поля 1 7, R 6, gap 4 (макет §5.1). Аналог чипов
// файлового просмотра (diff/комментарии) — единственный «статусный» элемент строки.
function StatusTag({ tag }: { tag: FeatureStatusTag }) {
  return (
    <span
      data-testid="feature-page-status"
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0,
        fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans,
        padding: '1px 7px', borderRadius: R.sm,
        ...TAG_STYLE[tag.tone],
      }}
    >
      {(tag.tone === 'wait' || tag.tone === 'merge') && <TagDot />}
      {tag.label}
    </span>
  );
}

// Скелет загрузки (макет §5.7): 6 полос 14 px разной ширины (bgSelected, R 6, gap 10,
// пульс 1,4 с) + блок 120 (marginTop 6). При prefers-reduced-motion пульс выключен
// (инжект в шапке).
const SKELETON_WIDTHS = [70, 92, 84, 60, 88, 76];
function LoadingSkeleton() {
  return (
    <div data-testid="feature-page-skeleton" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {SKELETON_WIDTHS.map((w, i) => (
        <div
          key={i}
          className="cc-feature-page-skeleton"
          style={{ height: 14, width: `${w}%`, background: C.bgSelected, borderRadius: R.sm }}
        />
      ))}
      <div className="cc-feature-page-skeleton" style={{ height: 120, marginTop: 6, background: C.bgSelected, borderRadius: R.sm }} />
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

// Страница фичи (волна 2 «Обзор фич»). «Быстрый трек»: открывается КАК ФАЙЛ —
// оболочка просмотра файла (Toolbar-шапка, белый фон C.bgCard, «Закрыть» как у файла,
// Esc, возврат к месту открытия), а в теле — только конвейер (PipelineFlow).
//
// Шапка — как у файла (FileViewer): слева «Закрыть» (десктоп — иконка X, мобила —
// BackButton, ровно как у FileViewer), затем глиф состояния (аналог плитки типа файла),
// название фичи как «имя файла» (mono 700, единственный гибкий элемент) и тег статуса
// (аналог чипов файла). Ветка/возраст/«M задач»/главное действие/⋯ — вне шапки
// (у файла их нет) — вернём позже.
//
// Компонент самодостаточен: данные — из стора useFeaturePage, название/глиф/тег — из
// индекса фич (фолбэк — эпик страницы). Закрытие читает указатель возврата
// (featureBackNav). Тело — одно из пяти состояний: loading | ok | notFound | disabled |
// error (макет §5.7); в ok — конвейер.
export function FeaturePage({ featureId, projectId, isMobile = false, onBack }: FeaturePageProps) {
  // Данные страницы (run + events) — из стора; здесь для фолбэка заголовка и 404/503.
  const page = useFeaturePage(featureId);
  const featuresOn = useFeature(FLAGS.featuresOverview);
  const index = useFeaturesIndex(projectId, featuresOn);
  const feature = index.features.find(f => f.featureId === featureId) ?? null;

  // Название: из индекса, фолбэк — эпик страницы, затем нейтральное. Контракт:
  // UI обязан пережить отсутствие любого поля ответа — эпик без title не роняет страницу.
  const title = feature?.title ?? (page.status === 'ok' ? (page.page.run.epic?.title ?? 'Фича') : 'Фича');

  // Глиф + тег статуса: из индекса (display state); нет фичи в индексе — нейтральные.
  const display = feature ? featureDisplayState(feature) : null;
  const glyph: GlyphKind = display ? display.kind : 'run';
  const statusTag: FeatureStatusTag | null = feature ? featureStatusTag(feature) : null;

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

  // Тело: одно из пяти состояний (макет §5.7). В ok — только конвейер (PipelineFlow).
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
    // ok: только конвейер (быстрый трек). Тревога/шаги/чаты/задачи/лента — вернём позже.
    body = (
      <div data-testid="feature-page-body">
        <PipelineFlow run={page.page.run} />
      </div>
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
      name={title}
      nameTitle={label}
      nameTestId="feature-page-title"
      leading={
        // Глиф состояния — аналог плитки типа файла (14 px).
        <span data-testid="feature-page-glyph" style={{ display: 'flex', flexShrink: 0 }}>
          <FeatureGlyph kind={glyph} size={14} />
        </span>
      }
      trailing={statusTag && <StatusTag tag={statusTag} />}
      rootTestId="feature-page"
      // Мобила: страница — прямой ребёнок колонки центра (flex: 1); десктоп — в Island
      // (FeatureCenter), корень height:100% по умолчанию.
      rootStyle={isMobile ? { flex: 1, overflow: 'hidden', height: 'auto' } : undefined}
    >
      {/* Тело: прокрутка, поля 16 16 20 (макет §5.4; 16 сверху — как у файла, Майя m2). */}
      <div style={{ flex: 1, overflow: 'auto', minHeight: 0, padding: '16px 16px 20px' }}>
        {body}
      </div>
    </CenterViewShell>
  );
}
