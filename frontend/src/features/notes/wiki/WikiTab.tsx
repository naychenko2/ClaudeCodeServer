// Вкладка «Вики» (макет llm-wiki-v2.html, §1 «Дерево разделов и каталог»): дерево разделов
// + каталог выбранного раздела. Состояния: «Вики не заведена» с «Завести», сирота с
// «Удалить раздел» (ConfirmDialog, danger), локальный проект, ошибка загрузки с «Повторить»,
// отказ контракта — русским текстом, не сырым кодом.
//
// Данные — только стор S5a (lib/wiki): ensureWikiLoaded при монтировании, каталог раздела —
// GET /api/wiki/section (оглавление строится из него: проекции _index/_log через
// /api/wiki/page не читаются — 400 wiki_page_key, хвост ревью S2f Н1).
// Открытие страницы из каталога (S5b) — здесь одна точка: строка каталога зовёт openPage,
// центр вместо каталога рисует WikiPageView; смена или удаление раздела закрывают страницу.
// Подключение к странице заметок (переключатель «Заметки | Вики») — на стороне NotesPage (S5d).
// На мобили дерево — отдельный экран «Разделы» (кнопка «Раздел ▸» в каталоге): макет 360,
// хвост ревью S5c — каталог не должен схлопываться в нулевую высоту под деревом.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, BookOpen, Check, ChevronLeft, ChevronRight, Folder, Lock, Monitor, Shield, Trash2 } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../../lib/design';
import { Badge, type BadgeTone } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { ConfirmDialog } from '../../../components/ui/ConfirmDialog';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { PillSwitch } from '../../../components/ui/PillSwitch';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { useIsMobile } from '../../../lib/breakpoints';
import { FLAGS, useFeature } from '../../../lib/featureFlags';
import {
  WikiError,
  ensureWikiLoaded,
  deleteWikiSection,
  groupSectionsByKind,
  initializeWiki,
  parseSectionKey,
  loadWikiCatalog,
  loadWikiPage,
  useWikiCatalog,
  useWikiPage,
  useWikiTree,
  type WikiPageInfoDto,
  type WikiSectionDto,
} from '../../../lib/wiki';
import { WikiPageView } from './WikiPageView';
import { WikiSectionTree } from './WikiSectionTree';
import { WIKI_SUBTABS, wikiSubTabIcon, WikiSubTabStub, type WikiSubTabId } from './wikiTabs';

// Колонка дерева на десктопе; на мобиле дерева в каталоге нет вовсе — оно отдельный
// экран «Разделы» (кнопка «Раздел ▸»)
const TREE_W = 300;
// Строка кнопки «Раздел ▸» — та же тач-высота, что у строк дерева (макет .trow 44)
const SECTION_SWITCH_H = 44;

// ─── Словари подписей ───

// Статусы страницы — их считает код, не модель (ADR-035 §3, WikiFrontmatter.StatusValues).
// Экспорт: словарь общий для каталога и страницы (WikiPageView), один на всю фичу
export const PAGE_STATUS: Record<string, { tone: BadgeTone; text: string; title: string }> = {
  active: { tone: 'success', text: 'актуальна', title: 'Утверждения подтверждены источниками, противоречий нет' },
  stale: { tone: 'neutral', text: 'устарела', title: 'Источник новее опроверг утверждение или страница не менялась 90 дней' },
  disputed: { tone: 'warning', text: 'спорная', title: 'Есть открытые противоречия — см. раздел на странице' },
  drift: { tone: 'danger', text: 'дрейф', title: 'Файл изменён мимо Хранителя' },
};

// Типы страниц — WikiFrontmatter.TypeValues. Экспорт — общий для каталога и страницы
export const PAGE_TYPE: Record<string, string> = {
  overview: 'Обзор', topic: 'Тема', entity: 'Сущность', concept: 'Понятие',
  decision: 'Решение', chronicle: 'Хроника', source: 'Источник',
};

// Отказы контракта (WikiErrorCode, S2f) — понятным русским текстом; 401/404 приходят без
// тела и без кода, их отличаем по статусу. Формулировки — черновые (тексты фичи ведёт Софья).
export function wikiErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_foreign': return 'Нет доступа';
    case 'wiki_section_key': return 'Раздел не найден';
    case 'wiki_page_key': return 'Страница не найдена';
    case 'wiki_project_missing': return 'Проект не найден';
    case 'wiki_section_off': return 'Вики локальных проектов — отдельной фичей';
    case 'wiki_sphere_reserved': return 'Раздел сферы — следующая фича';
    case 'wiki_refused': return 'Правку нельзя применить к этому состоянию страницы';
    case 'wiki_drift': return 'Страницу изменили мимо Хранителя — сначала разберите дрейф';
    case 'wiki_not_orphan': return 'Удалить можно только раздел удалённого проекта';
  }
  if (e.status === 401 || e.status === 403) return 'Нет доступа';
  if (e.status === 404) return 'Не найдено';
  if (e.status === 409) return 'Действие отказано';
  return 'Не удалось выполнить действие';
}

// Префикс раздела — один форматтер на всю фичу (тот же вид, что у бэка в выдаче notes_*):
// «Проекты / AI Home», «Сферы / Банк», «Общее», «Личное»
export function sectionPrefix(s: WikiSectionDto): string {
  const ref = parseSectionKey(s.key);
  if (!ref) return '—';
  if (ref.kind === 'common') return 'Общее';
  if (ref.kind === 'personal') return 'Личное';
  if (ref.kind === 'sphere') return `Сферы / ${s.name ?? ''}`;
  return `Проекты / ${s.name ?? '—'}`;
}

// Что открыть, когда раздел ещё не выбран: живой проект, иначе «Общее», иначе «Личное»;
// локальный и сирота — в конце (их экраны — не каталог). Разделы сфер не выбираются.
export function defaultSectionKey(sections: readonly WikiSectionDto[]): string | null {
  const g = groupSectionsByKind(sections);
  const pick =
    g.project.find(s => !s.orphan && !s.local) ??
    g.common[0] ??
    g.personal[0] ??
    g.project.find(s => !s.local) ??
    null;
  return pick?.key ?? null;
}

// ─── Куски экрана ───

function LoadingSkeleton() {
  const bar = (w: number): ReactNode => (
    <div style={{ height: 10, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, margin: `${SP.sm}px 0` }} />
  );
  return (
    <div style={{ padding: SP.sm }} aria-hidden>
      {bar(40)}{bar(70)}{bar(85)}{bar(60)}{bar(75)}
    </div>
  );
}

// Отказ загрузки — с «Повторить» (перечитать и есть повторение загрузки); отказ действия —
// с «Закрыть»: повторять надо само действие (оно уже отказано), а не перечитку дерева,
// поэтому у отказа действия кнопки «Повторить» нет — его либо принимают, либо закрывают.
function ErrorNotice({ e, onRetry, onDismiss }: { e: unknown; onRetry?: () => void; onDismiss?: () => void }) {
  const text = e instanceof WikiError ? wikiErrorText(e) : 'Не удалось загрузить вики';
  const action = onRetry
    ? <Button variant="ghost" size="sm" onClick={onRetry}>Повторить</Button>
    : onDismiss
      ? <Button variant="ghost" size="sm" onClick={onDismiss}>Закрыть</Button>
      : undefined;
  return (
    <div data-testid="wiki-error" style={{ padding: SP.sm }}>
      <Notice tone="danger" icon={AlertTriangle} title={text} action={action} />
    </div>
  );
}

// Пустое «Вики не заведена» — тексты макета (emptyCenter), три пункта и одна кнопка на владельца
function EmptyWiki({ busy, onInit }: { busy: boolean; onInit: () => void }) {
  const step = (icon: ReactNode, text: string) => (
    <div style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start', textAlign: 'left' }}>
      <span style={{ flexShrink: 0, marginTop: 2, color: C.textSecondary }}>{icon}</span>
      <span>{text}</span>
    </div>
  );
  return (
    <EmptyState
      icon={<BookOpen size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
      title="Вики не заведена"
      subtitle={
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, maxWidth: 340 }}>
          <span>
            Хранитель соберёт из ваших заметок, файлов, задач и разговоров связную базу:
            раздел на каждый проект, «Общее» и «Личное». Каждое утверждение — с цитатой.
          </span>
          {step(<Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, 'Ваши заметки Хранитель не трогает: он пишет только в вики.')}
          {step(<Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, 'Неспорное применяется само, спорное ждёт во «Входящих». Любой пакет и весь источник можно откатить.')}
          {step(<Shield size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, 'Вики хранится на сервере и входит в бэкап данных.')}
        </div>
      }
      action={<Button variant="primary" size="md" dataTestId="wiki-init-btn" loading={busy} onClick={onInit}>Завести вики</Button>}
    />
  );
}

// Локальный проект: вики для него — отдельная фича (409 wiki_section_off на любой операции)
function LocalSection({ onOpenCommon }: { onOpenCommon?: () => void }) {
  return (
    <EmptyState
      icon={<Monitor size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
      title="Вики — пока для серверных проектов"
      subtitle={
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <span>
            Этот проект живёт на устройстве. Для локальных проектов вики появится позже:
            раздел будет храниться на сервере, а включать его вы будете в настройках проекта.
          </span>
          <span style={{ fontSize: FS.sm }}>Ваши заметки в этом проекте работают как раньше. Знание, нужное везде, можно положить в «Общее».</span>
        </div>
      }
      action={onOpenCommon ? (
        <Button variant="ghost" size="sm" onClick={onOpenCommon}>Открыть «Общее»</Button>
      ) : undefined}
    />
  );
}

// Сирота: раздел скрыт от чатов и агентов, страницы остались, сам он не удалится (ADR-036 §1).
// Имени и даты удаления бэк не отдаёт — поэтому подпись общая, а не «проект «…» удалён ДД.ММ»
function OrphanNotice({ onDelete }: { onDelete: () => void }) {
  return (
    <Notice
      tone="warning"
      icon={AlertTriangle}
      title="Раздел удалённого проекта"
      style={{ marginBottom: SP.md }}
      action={
        // danger-ghost макета: контур и текст danger, не заливка
        <Button
          variant="ghost"
          size="sm"
          dataTestId="wiki-delete-section-btn"
          leftIcon={<Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          style={{ color: C.dangerText, border: `1px solid ${C.dangerBorder}` }}
          onClick={onDelete}
        >
          Удалить раздел
        </Button>
      }
    >
      Он скрыт от всех чатов, агентов и персон. Страницы остались: удалите раздел, когда
      убедитесь, что знание не нужно. Сам он не удалится.
    </Notice>
  );
}

function SectionHeader({ section, count }: { section: WikiSectionDto; count: number }) {
  return (
    <div style={{ padding: `${SP.sm}px ${SP.sm}px ${SP.xs}px` }}>
      <div style={{ fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading }}>
        {section.name ?? 'Раздел'}
      </div>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
        {sectionPrefix(section)} · {count} стр.
      </div>
    </div>
  );
}

// Строка каталога: тип, название, бейдж статуса, summary, число источников. Клик открывает
// страницу (S5b) — единственное место фичи, где страница открывается (одна точка
// подключения). Hover — только на десктопе (тот же порядок, что у строк дерева S5c)
function PageRow({ page, onOpen }: { page: WikiPageInfoDto; onOpen: () => void }) {
  const isMobile = useIsMobile();
  const [hovered, setHovered] = useState(false);
  const st = page.status ? PAGE_STATUS[page.status] : undefined;
  return (
    <button
      type="button"
      data-testid="wiki-page-row"
      onClick={onOpen}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      style={{
        display: 'flex', gap: SP.sm, padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.md,
        border: 'none', background: !isMobile && hovered ? C.bgCard : 'transparent',
        cursor: 'pointer', textAlign: 'left', width: '100%', fontFamily: FONT.sans, color: C.textPrimary,
      }}
    >
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
          <span style={{ fontSize: FS.base, fontWeight: 500, color: C.textHeading }}>{page.title}</span>
          {st && <Badge tone={st.tone} size="xs" title={st.title}>{st.text}</Badge>}
        </div>
        {page.summary && <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>{page.summary}</div>}
        <div style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, marginTop: 2 }}>
          {PAGE_TYPE[page.type ?? ''] ?? 'Страница'} · {page.sourcesCount} ист.
        </div>
      </div>
    </button>
  );
}

// ─── Вкладка ───

export function WikiTab() {
  const enabled = useFeature(FLAGS.llmWiki);
  const isMobile = useIsMobile();
  const tree = useWikiTree();
  const [tab, setTab] = useState<WikiSubTabId>('pages');
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState<WikiSectionDto | null>(null);
  // Мобильный экран «Разделы» (S5d, хвост ревью S5c): true — вместо каталога показан
  // полноэкранный список разделов; выбор раздела возвращает к каталогу
  const [showSections, setShowSections] = useState(false);
  // Открытая страница (S5b): pageKey открытой страницы выбранного раздела; null — каталог.
  // Смена или удаление раздела закрывает её (страница принадлежит разделу) — selectSection
  const [openedPage, setOpenPage] = useState<string | null>(null);
  // Поколение последнего открытия (доработка S5b M3, образец — _pageGen в lib/wiki):
  // отказ прежнего запроса не должен закрывать уже открытую свежую страницу
  const openSeq = useRef({ gen: 0, pageKey: '' });

  const selectSection = useCallback((key: string | null) => {
    setSelectedKey(key);
    setOpenPage(null);
  }, []);

  const load = useCallback(() => {
    setLoadError(null);
    // Смена владельца в этой же вкладке — известный минор S5a (N1): самолечится новым
    // ensureWikiLoaded; здесь не лечим.
    ensureWikiLoaded().catch(e => setLoadError(e));
  }, []);

  useEffect(() => {
    if (!enabled) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании/флаге: load() синхронно снимает прошлую ошибку загрузки перед запросом — другого места старта нет
    load();
  }, [enabled, load]);

  const sections = tree?.sections ?? [];
  const selected = selectedKey ? sections.find(s => s.key === selectedKey) ?? null : null;
  const catalog = useWikiCatalog(selectedKey ?? '');
  const pageSnap = useWikiPage(selectedKey ?? '', openedPage ?? '');

  // Раздел по умолчанию — когда своего ещё нет или выбранный исчез из дерева
  useEffect(() => {
    if (!tree?.initialized) return;
    if (selectedKey && tree.sections.some(s => s.key === selectedKey)) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- инвариант выбора: на экране нет допустимого выбранного раздела — дефолт выбирается синхронно, экран без раздела хуже
    selectSection(defaultSectionKey(tree.sections));
  }, [tree, selectedKey, selectSection]);

  // Каталог выбранного раздела. Для локального проекта его нет вовсе (409 wiki_section_off).
  // Зависимость — ключ, а не объект selected: объект новый на каждый рендер, эффект
  // перечитывал бы каталог в каждом.
  const selectedLocal = selected?.local ?? false;
  useEffect(() => {
    if (!selectedKey || selectedLocal) return;
    loadWikiCatalog(selectedKey).catch(e => setActionError(e));
  }, [selectedKey, selectedLocal]);

  if (!enabled) return null;

  const runAction = (fn: () => Promise<unknown>) => {
    setBusy(true);
    setActionError(null);
    void fn()
      .catch(e => setActionError(e))
      .finally(() => setBusy(false));
  };

  // Открытие страницы из каталога — одна точка подключения (S5b): текст читает стор
  // (поколение и владелец — в нём). Отказ загрузки — возврат к каталогу с названным
  // отказом: пустой экран страницы хуже строки, из которой её открыли.
  // Отказ актуален только для последнего открытия (доработка S5b M3): поздний отказ A,
  // пока открыта B, не закрывает B и не показывает свою ошибку — иначе пользователь
  // вылетает из уже прочитанной страницы с чужой ошибкой.
  // Обычная функция, не useCallback: она ниже раннего return (условный хук — rules-of-hooks),
  // порядок — как у runAction рядом
  const openPage = (pageKey: string) => {
    if (!selectedKey) return;
    const mine = { gen: openSeq.current.gen + 1, pageKey };
    openSeq.current = mine;
    setOpenPage(pageKey);
    setActionError(null);
    loadWikiPage(selectedKey, pageKey).catch(e => {
      if (openSeq.current.gen !== mine.gen || openSeq.current.pageKey !== pageKey) return;
      setOpenPage(null);
      setActionError(e);
    });
  };

  const commonKey = groupSectionsByKind(sections).common[0]?.key ?? null;

  // Кнопка «Раздел ▸» (макет 360, строка каталога над заголовком раздела): на мобили
  // заменяет «дерево над каталогом» и открывает полноэкранный экран «Разделы».
  // Префикс раздела — общий для фичи sectionPrefix, тот же вид, что у бэка в выдаче notes_*.
  const sectionSwitch = () => (
    <div style={{ padding: `${SP.sm}px ${SP.sm}px 0` }}>
      <button
        type="button"
        data-testid="wiki-section-switch"
        onClick={() => setShowSections(true)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left',
          border: `1px solid ${C.border}`, borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
          minHeight: SECTION_SWITCH_H, background: 'transparent', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
        }}
      >
        <Folder size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        <span style={{ fontSize: FS.sm, color: C.textMuted, flexShrink: 0 }}>Раздел</span>
        <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 600, color: C.textHeading }}>
          {selected ? sectionPrefix(selected) : 'не выбран'}
        </span>
        <ChevronRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ marginLeft: 'auto', flexShrink: 0, color: C.textMuted }} />
      </button>
    </div>
  );

  const center = () => {
    if (loadError) return <ErrorNotice e={loadError} onRetry={load} />;
    if (!tree) return <LoadingSkeleton />;
    if (!tree.initialized) {
      return <EmptyWiki busy={busy} onInit={() => runAction(() => initializeWiki())} />;
    }
    if (!selected) {
      return (
        <EmptyState
          compact inline
          icon={<Folder size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Раздел не выбран"
          subtitle="Слева — ваша вики: разделы проектов, «Общее» и «Личное». Выберите раздел."
        />
      );
    }
    if (selected.local) return <LocalSection onOpenCommon={commonKey ? () => selectSection(commonKey) : undefined} />;

    // Открытая страница (S5b) — вместо каталога; текст и дрейф читает сам экран из стора
    // (доработка S5b M2), здесь остаётся только порог: пока страницы нет — спиннер.
    // key — по паре (раздел, страница): локальное состояние действия (занятость и отказ
    // adopt) не переезжает на другую страницу
    if (openedPage) {
      return pageSnap ? (
        <WikiPageView
          key={`${selectedKey}\n${openedPage}`}
          section={selected}
          pageKey={openedPage}
          onBack={() => setOpenPage(null)}
        />
      ) : <LoadingSkeleton />;
    }

    const pages = catalog?.pages ?? [];
    return (
      <div style={{ padding: SP.sm }}>
        {selected.orphan && (
          <OrphanNotice onDelete={() => setConfirmDelete(selected)} />
        )}
        <SectionHeader section={selected} count={selected.pageCount} />
        {pages.length
          ? pages.map(p => <div key={p.pageKey}><PageRow page={p} onOpen={() => openPage(p.pageKey)} /></div>)
          : (
            <div style={{ padding: `${SP.md}px ${SP.sm}px`, fontSize: FS.sm, color: C.textMuted }}>
              В разделе пока нет страниц. Их напишет Хранитель, когда разрешите разбор.
            </div>
          )}
      </div>
    );
  };

  const subtab = WIKI_SUBTABS.find(t => t.id === tab) ?? WIKI_SUBTABS[0];
  const SubTabComp = subtab.component;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minHeight: 0, height: '100%' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: `0 ${SP.sm}px` }}>
        <PillSwitch<WikiSubTabId>
          value={tab}
          fill
          isMobile={isMobile}
          iconsOnly={isMobile}
          // Смена подвкладки закрывает экран «Разделы» (минор 4 ревью S5d): возврат на
          // «Страницы» не должен открывать его снова — экран относится к «Страницам»
          onChange={(v) => { setTab(v); setShowSections(false); }}
          options={WIKI_SUBTABS.map(t => ({ value: t.id, label: t.label, icon: wikiSubTabIcon(t), title: t.label }))}
        />
      </div>

      {actionError ? <div style={{ padding: `0 ${SP.sm}px` }}><ErrorNotice e={actionError} onDismiss={() => setActionError(null)} /></div> : null}

      <div style={{ display: 'flex', gap: SP.md, flex: 1, minHeight: 0 }}>
        {/* Дерево — колонка слева только на десктопе. На мобили оно не стоит над каталогом
            (каталог схлопывался в нулевую высоту — хвост ревью S5c): там дерево — экран
            «Разделы», который открывает кнопка «Раздел ▸» внутри каталога */}
        {!isMobile && (
          <div style={{ width: TREE_W, flexShrink: 0 }}>
            <WikiSectionTree sections={sections} selectedKey={selectedKey} onSelect={selectSection} />
          </div>
        )}
        <div style={{ flex: 1, minWidth: 0, overflow: 'auto' }}>
          {/* Рендер элементом, не вызовом в теле WikiTab: хуки подвкладки живут в ней,
              переключение подвкладки не меняет число хуков родителя (реестр — wikiTabs) */}
          {SubTabComp
            ? <SubTabComp selectedKey={selectedKey} />
            : subtab.id === 'pages'
              ? (isMobile && showSections
                // Экран «Разделы» (макет 360): полноэкранный список без ограничения
                // высоты; выбор раздела возвращает к каталогу. Кнопка «Назад» — явный возврат
                // к каталогу выбранного раздела (минор 3 ревью S5d): состояние локальное,
                // в историю не пишется — аппаратный «назад» остаётся как есть
                ? <>
                    <div style={{ padding: `${SP.sm}px ${SP.sm}px 0` }}>
                      <Button
                        variant="ghost" size="sm"
                        dataTestId="wiki-sections-back"
                        ariaLabel="Вернуться к каталогу раздела"
                        leftIcon={<ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                        onClick={() => setShowSections(false)}
                      >
                        Назад
                      </Button>
                    </div>
                    <WikiSectionTree
                      sections={sections} selectedKey={selectedKey}
                      onSelect={k => { selectSection(k); setShowSections(false); }}
                    />
                  </>
                : <>
                    {/* Открытая страница — отдельный экран (макет 360): кнопки «Раздел ▸»
                        над ней нет, возврат — её кнопкой «Назад» */}
                    {isMobile && tree?.initialized && !openedPage && sectionSwitch()}
                    {center()}
                  </>)
              : <div style={{ padding: SP.sm }}><WikiSubTabStub label={subtab.label} when={subtab.stubNote ?? ''} /></div>}
        </div>
      </div>

      {confirmDelete && (
        <ConfirmDialog
          title={confirmDelete.name
            ? `Удалить раздел «${confirmDelete.name}»?`
            : 'Удалить раздел удалённого проекта?'}
          subtitle={`${confirmDelete.pageCount} стр. удалятся из вики и из поиска. Вернуть можно только из бэкапа данных.`}
          confirmLabel="Удалить раздел"
          confirmVariant="danger"
          onConfirm={async () => {
            // Отказ — тем же путём, что у действия в runAction: русским текстом в баннер.
            // Без catch rejection ушёл бы в void result.finally() (ConfirmDialog) и
            // пользователь не увидел бы ничего. Диалог закрываем при обоих исходах:
            // отказ видно в баннере вкладки, а не за спиннером диалога.
            try {
              await deleteWikiSection(confirmDelete.key);
            } catch (e) {
              setActionError(e);
              setConfirmDelete(null);
              return;
            }
            setConfirmDelete(null);
            selectSection(null);
          }}
          onCancel={() => setConfirmDelete(null)}
        />
      )}
    </div>
  );
}
