// Вкладка «Вики» (макет llm-wiki-v2.html, §1 «Дерево разделов и каталог»): дерево разделов
// + каталог выбранного раздела. Состояния: «Вики пока пустая» без кнопки (вики заводится
// сама, LI-3), сирота с «Удалить раздел» (ConfirmDialog, danger), локальный проект,
// ошибка загрузки с «Повторить», отказ контракта — русским текстом, не сырым кодом.
//
// Данные — только стор S5a (lib/wiki): ensureWikiLoaded при монтировании, каталог раздела —
// GET /api/wiki/section (оглавление строится из него: проекции _index/_log через
// /api/wiki/page не читаются — 400 wiki_page_key, хвост ревью S2f Н1).
// Открытие страницы из каталога (S5b) — здесь одна точка: строка каталога зовёт openPage,
// центр вместо каталога рисует WikiPageView; смена или удаление раздела закрывают страницу.
// Запрос «Открыть хронику» из шапки чата (OPEN-1) — тоже здесь: из стора вики выбирается
// раздел и открывается страница, запрос после обработки берётся (он ждёт загрузки дерева).
// Подключение к странице заметок (переключатель «Заметки | Вики») — на стороне NotesPage (S5d).
// На мобили дерево — отдельный экран «Разделы» (кнопка «Раздел ▸» в каталоге): макет 360,
// хвост ревью S5c — каталог не должен схлопываться в нулевую высоту под деревом.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, ChevronLeft, ChevronRight, Folder, FolderTree, Monitor, PanelRight, Trash2 } from 'lucide-react';
import {
  C, FONT, FS, R, SP, useIsMobile, FLAGS, useFeature,
  WikiError, ensureWikiLoaded, deleteWikiSection, groupSectionsByKind,
  getWikiTreeSnapshot, parseSectionKey, loadWikiCatalog, loadWikiPage, loadWikiTree,
  useWikiCatalog, useWikiPage, useWikiPageGone, useWikiTree,
  consumeOpenWikiPage, useWikiOpenRequest,
  type WikiPageInfoDto, type WikiSectionDto,
} from 'aihome_shell/kit';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { ConfirmDialog } from '../../../components/ui/ConfirmDialog';
import { EmptyState } from '../../../components/ui/EmptyState';
import { Notice } from '../../../components/ui/Notice';
import { PillSwitch } from '../../../components/ui/PillSwitch';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { WikiEmpty } from './WikiEmpty';
import { WikiPageView } from './WikiPageView';
import { WikiSectionTree } from './WikiSectionTree';
import { panelTabs, PanelHead, ReplacementDot, WikiDrawer, WikiPanelBody } from './WikiUnifiedChrome';
import { WIKI_SUBTABS, wikiSubTabIcon, WikiSubTabStub, type WikiSubTabId } from './wikiTabs';
import { PAGE_STATUS, PAGE_TYPE, sectionPrefix, wikiErrorText } from './wikiLabels';

// Колонка дерева прежней раскладки на десктопе; на мобиле дерева в каталоге нет вовсе —
// оно отдельный экран «Разделы» (кнопка «Раздел ▸»)
const TREE_W = 300;
// Строка кнопки «Раздел ▸» — та же тач-высота, что у строк дерева (макет .trow 44)
const SECTION_SWITCH_H = 44;
// Кнопки-триггеры шторок — квадрат 40×40 (кадр mob-closed): тач-цель ровно 40
const DRAWER_BTN = 40;
// id шторок для aria-controls кнопок-триггеров
const DRAWER_TREE_ID = 'wiki-drawer-tree';
const DRAWER_PANEL_ID = 'wiki-drawer-panel';

// ─── Словари подписей ───

// Словари и форматтер — в wikiLabels.ts: их читают каталог, страница, «Входящие» и
// карточка пакета, а прежний импорт страницы отсюда давал цикл WikiTab ↔ WikiPageView
// (хвост ревью S5b). Re-export ниже — фасад прежних путей для «Входящих» и карточки
// пакета (WikiInbox, WikiPackageView): их перевод на './wikiLabels' вне размера этой
// правки; цикла фасад не создаёт — wikiLabels не импортирует ничего из фичи.
export { PAGE_STATUS, PAGE_TYPE, sectionPrefix, wikiErrorText } from './wikiLabels';

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

// Имя раздела для строки в шторке панели — та же короткая форма, что у бейджа шапки на
// десктопе (записка notes-wiki-panels-v1, п. 1): «Общее», «Личное», имя проекта или
// сферы; безымянный заместитель — тот же, что в дереве («Удалённый проект», «Сфера»)
function sectionLabel(s: WikiSectionDto): string {
  const ref = parseSectionKey(s.key);
  if (ref?.kind === 'common') return 'Общее';
  if (ref?.kind === 'personal') return 'Личное';
  return s.name ?? (ref?.kind === 'sphere' ? 'Сфера' : 'Удалённый проект');
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

// unified — единая раскладка «Заметки + Вики» (ADR-038, F1; флаг notesWikiUnified держит
// NotesPage): в центре только документ + рельса с панелью справа, на 360 — две шторки.
// Дерево разделов на десктопе стоит в ЛЕВОЙ зоне NotesPage: состояние выбора живёт здесь,
// поэтому дерево уходит туда порталом в узел treeHost (null — левая зона закрыта, дерева
// нет). Рельса и панель вики на десктопе живут в NotesPage (видны и в режиме заметок,
// когда эта вкладка не смонтирована), выбранный раздел для панели отдаёт onSectionChange.
// Без unified — прежние вкладки, поведение не меняется.
// drawerTree — единое дерево «заметки + вики» для шторки на 360 (строит NotesPage: заметки у
// него); не передано — прежнее дерево разделов.
export function WikiTab({ unified = false, treeHost = null, drawerTree, onSectionChange }: {
  unified?: boolean; treeHost?: HTMLElement | null; drawerTree?: (close: () => void) => ReactNode;
  onSectionChange?: (key: string | null) => void;
} = {}) {
  const enabled = useFeature(FLAGS.llmWiki);
  const isMobile = useIsMobile();
  const tree = useWikiTree();
  // Запрос «Открыть хронику» из шапки чата (OPEN-1) — в сторе вики до нашего монтирования
  const openReq = useWikiOpenRequest();
  const [tab, setTab] = useState<WikiSubTabId>('pages');
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const [confirmDelete, setConfirmDelete] = useState<WikiSectionDto | null>(null);
  // Мобильный экран «Разделы» (S5d, хвост ревью S5c): true — вместо каталога показан
  // полноэкранный список разделов; выбор раздела возвращает к каталогу
  const [showSections, setShowSections] = useState(false);
  // Единая раскладка: открытая панель (null — закрыта) и шторка — на 360; на десктопе панель в NotesPage
  const [panelTab, setPanelTab] = useState<WikiSubTabId | null>(null);
  const [drawer, setDrawer] = useState<'tree' | 'panel' | null>(null);
  // Открытая страница (S5b): pageKey открытой страницы выбранного раздела; null — каталог.
  // Смена или удаление раздела закрывает её (страница принадлежит разделу) — selectSection
  const [openedPage, setOpenPage] = useState<string | null>(null);
  // Поколение последнего открытия или осознанного закрытия (доработка S5b M3, S5b-дельта W4-2;
  // образец — _pageGen в lib/wiki): отказ прежнего запроса не должен закрывать уже открытую
  // свежую страницу и не должен писаться в каталог, к которому перешли после закрытия
  const openSeq = useRef({ gen: 0, pageKey: '' });

  // Осознанное закрытие страницы: «Назад» страницы (S5b-дельта, W4-2). Поднимает поколение —
  // та же сверка, что в openPage: без этого отказ запроса страницы, закрытой во время чтения,
  // проходит сверку (gen и pageKey прежние) и пишет свою ошибку над новым состоянием.
  // Отказ текущей страницы (catch openPage) сюда не идёт: он относится к открытому
  // состоянию и показывается.
  const closePage = useCallback(() => {
    openSeq.current = { ...openSeq.current, gen: openSeq.current.gen + 1 };
    setOpenPage(null);
  }, []);

  // Смена раздела: страница закрывается всегда (страница принадлежит разделу).
  // intentional=true — действие пользователя (строка дерева или экрана «Разделы», «Открыть
  // «Общее»», удаление раздела): закрыть страницу осознанно, поднять поколение, как closePage:
  // отказ страницы, закрытой во время чтения, не пишется в каталог нового раздела
  // (S5b-дельта, W4-2). Автоматические вызовы — дефолтный выбор и OPEN-1 — не поднимают:
  // там отказ запрошенной страницы относится к действию пользователя и показывается
  // (тест «чужий раздел», S5b).
  const selectSection = useCallback((key: string | null, intentional = false) => {
    setSelectedKey(key);
    if (intentional) closePage(); else setOpenPage(null);
  }, [closePage]);

  // Открытие страницы — одна точка подключения (S5b): текст читает стор
  // (поколение и владелец — в нём). Отказ загрузки — возврат к каталогу с названным
  // отказом: пустой экран страницы хуже строки, из которой её открыли.
  // Отказ актуален только для последнего открытия (доработка S5b M3) и пока страница не
  // закрыта осознанно (closePage/selectSection поднимают поколение): поздний отказ A,
  // пока открыта B или уже закрыта, не закрывает B и не показывает свою ошибку — иначе
  // пользователь вылетает из уже прочитанной страницы с чужой ошибкой.
  // sectionKey — явный параметр (OPEN-1): запрос из шапки чата открывает страницу своего
  // раздела, а selectedKey в момент того эффекта ещё прежний (выбор и открытие — в одном
  // коммите). Вызывающий из каталога передаёт только страницу. useCallback — чтобы
  // эффект запроса (хук до раннего return) мог держать на ней зависимость; поведение
  // прежнее.
  const openPage = useCallback((pageKey: string, sectionKey: string | null = selectedKey) => {
    if (!sectionKey) return;
    const mine = { gen: openSeq.current.gen + 1, pageKey };
    openSeq.current = mine;
    setOpenPage(pageKey);
    setActionError(null);
    loadWikiPage(sectionKey, pageKey).catch(e => {
      if (openSeq.current.gen !== mine.gen || openSeq.current.pageKey !== pageKey) return;
      setOpenPage(null);
      setActionError(e);
    });
  }, [selectedKey]);

  // Стор снял открытую страницу 404 перечитки после мутации (откат снес страницу —
  // W4-5, дефект 3f3449e4): намерение «страница открыта» живёт здесь, и снимает его
  // только этот экран — без возврата центр висел бы на скелетоне без выхода.
  // Факт снятия — в сторе (useWikiPageGone), НЕ в отсутствии снимка: пока страница
  // грузится ответа ещё нет, но её никто не снимал — скелетон остаётся (c2).
  // Закрытие — не closePage (осознанного действия нет, сиротить нечего): если запрос
  // этой же страницы ещё идёт, его отказ доходит штатным путём openPage — «ошибку
  // показывает штатный путь». Эффект объявлен до эффектов выбора/OPEN-1: при
  // однокоммитном открытии другой страницы её открытие идёт последним и побеждает —
  // снятие прежней не закрывает новую (конкурентность W4-5).
  const pageGone = useWikiPageGone(selectedKey ?? '', openedPage ?? '');
  useEffect(() => {
    if (!openedPage || !pageGone) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- стор уже снял страницу: возврат к каталогу — единственный исход, держать открытой нечего
    setOpenPage(null);
  }, [openedPage, pageGone]);

  const load = useCallback(() => {
    setLoadError(null);
    // Смена владельца в этой же вкладке — известный минор S5a (N1): самолечится новым
    // ensureWikiLoaded; здесь не лечим.
    // (LI-3) Пока вики не заведена, экран перечитывает дерево при каждом показе: сервер
    // заводит её при действиях человека («Разобрать этот чат», перенос заметки), и
    // устаревший пустой снимок не должен оставаться на экране. Сверка — по снимку ДО
    // загрузки: если дерева здесь ещё не читали, свой GET делает ensureWikiLoaded,
    // второй не нужен.
    const staleUninitialized = getWikiTreeSnapshot()?.initialized === false;
    ensureWikiLoaded()
      .then(() => {
        if (staleUninitialized) return loadWikiTree();
      })
      .catch(e => setLoadError(e));
  }, []);

  useEffect(() => {
    if (!enabled) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- старт загрузки при монтировании/флаге: load() синхронно снимает прошлую ошибку загрузки перед запросом — другого места старта нет
    load();
  }, [enabled, load]);

  // Выбранный раздел — наружу: панель рельсы (NotesPage) читает ту же секцию, что и центр
  useEffect(() => { onSectionChange?.(selectedKey); }, [selectedKey, onSectionChange]);

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

  // Запрос открыть страницу из шапки чата (OPEN-1): выбрать названный раздел и открыть
  // названную страницу. Ждём дерево: пока оно грузится, запрос остаётся в сторе — эффект
  // вернётся к нему, когда дерево придёт (стор уведомляет подпиской). После прочитанного
  // дерева запрос берётся в любом случае: дальше ждать нечего, а «Вики не заведена» —
  // состояние экрана, а не запроса. Чужой или несуществующий раздел экран не роняет:
  // эффект дефолтного выбора вернёт на живой раздел, а отказ страницы покажется текстом
  // (поведение openPage). Повторный запрос на ту же страницу работает снова: запрос —
  // новый объект, и после закрытой страницы страница открывается заново (не залипает).
  // Порядок после эффекта дефолтного выбора — свой выбор идёт последним и побеждает.
  useEffect(() => {
    if (!openReq || !tree) return;
    if (tree.initialized) {
      selectSection(openReq.section);
      openPage(openReq.path, openReq.section);
    }
    consumeOpenWikiPage();
  }, [openReq, tree, selectSection, openPage]);

  // Каталог выбранного раздела. Для локального проекта его нет вовсе (409 wiki_section_off).
  // Зависимость — ключ, а не объект selected: объект новый на каждый рендер, эффект
  // перечитывал бы каталог в каждом.
  const selectedLocal = selected?.local ?? false;
  useEffect(() => {
    if (!selectedKey || selectedLocal) return;
    loadWikiCatalog(selectedKey).catch(e => setActionError(e));
  }, [selectedKey, selectedLocal]);

  if (!enabled) return null;

  const commonKey = groupSectionsByKind(sections).common[0]?.key ?? null;

  // «Настройки» из пустого состояния (LI-3) — там, где панель достижима состоянием этой
  // вкладки: подкладкой вкладки (не-unified) или шторкой панели (unified, мобиле). На
  // десктопной unified панель живёт в NotesPage — связь без её правок невозможна: там
  // текст остаётся без ссылки (отмечено в отчёте LI-3).
  const openSettings = unified
    ? (isMobile ? () => { setPanelTab('settings'); setDrawer('panel'); } : undefined)
    : () => setTab('settings');

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
    // «Вики пока пустая» — и когда вики ещё не заведена, и когда заведена, но разделов
    // ещё нет: голый каталог здесь не о чём (LI-3)
    if (!tree.initialized || !tree.sections.length) {
      return <WikiEmpty onOpenSettings={openSettings} />;
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
    if (selected.local) return <LocalSection onOpenCommon={commonKey ? () => selectSection(commonKey, true) : undefined} />;

    // Открытая страница (S5b) — вместо каталога; текст и дрейф читает сам экран из стора
    // (доработка S5b M2), здесь остаётся только порог: пока страница грузится — спиннер;
    // если её снял откат — эффект выше (W4-5) уже закрыл страницу, до скелетона не доходит.
    // key — по паре (раздел, страница): локальное состояние действия (занятость и отказ
    // adopt) не переезжает на другую страницу
    if (openedPage) {
      return pageSnap ? (
        <WikiPageView
          key={`${selectedKey}\n${openedPage}`}
          section={selected}
          pageKey={openedPage}
          onBack={closePage}
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

  const dialog = confirmDelete && (
        <ConfirmDialog
          title={confirmDelete.name
            ? `Удалить раздел «${confirmDelete.name}»?`
            : 'Удалить раздел удалённого проекта?'}
          subtitle={`${confirmDelete.pageCount} стр. удалятся из вики и из поиска. Вернуть можно только из бэкапа данных.`}
          confirmLabel="Удалить раздел"
          confirmVariant="danger"
          onConfirm={async () => {
            // Отказ — как у прочих действий вкладки: русским текстом в баннер.
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
            selectSection(null, true);
          }}
          onCancel={() => setConfirmDelete(null)}
        />
      );

  const errorBanner = actionError
    ? <div style={{ padding: `0 ${SP.sm}px` }}><ErrorNotice e={actionError} onDismiss={() => setActionError(null)} /></div>
    : null;

  // Единая раскладка: навигации вики в центре нет — ни вкладок, ни «Назад», ни крошек
  if (unified) {
    const panelLabel = panelTabs().find(t => t.id === panelTab)?.label ?? 'Панель вики';
    const renderTree = (onPick?: () => void) => (
      <WikiSectionTree
        sections={sections} selectedKey={selectedKey}
        onSelect={k => { selectSection(k, true); onPick?.(); }}
      />
    );
    const centerPane = (
      <div data-testid="wiki-doc" style={{ flex: 1, minWidth: 0, overflow: 'auto' }}>{center()}</div>
    );
    if (isMobile) {
      // Раздел для строки под переключателем (записка notes-wiki-panels-v1, п. 1 и таблица
      // «Шторка 360»): ровно то же значение, по которому тело шторки уже работает —
      // selectedKey, до выбора — defaultSectionKey. Своего состояния здесь нет: строка —
      // только чтение раздела панели. Правила п. 1 держит то, что доходит до selectedKey:
      // строки, у которых есть раздел (строка дерева, открытие страницы). Заметка из
      // «Других заметок» (OTHER_KEY — ключа нет) и клик по заголовку узла его не меняют.
      const shownKey = selectedKey ?? defaultSectionKey(sections);
      const shownSection = shownKey ? sections.find(s => s.key === shownKey) ?? null : null;
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minHeight: 0, height: '100%' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: `0 ${SP.sm}px` }}>
            <Button variant="ghost" size="md" dataTestId="wiki-open-tree" ariaLabel="Дерево разделов" title="Дерево разделов"
              leftIcon={<FolderTree size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} ariaExpanded={drawer === 'tree'} ariaControls={DRAWER_TREE_ID}
              style={{ width: DRAWER_BTN, height: DRAWER_BTN, padding: 0 }}
              onClick={() => setDrawer(d => d === 'tree' ? null : 'tree')}>{null}</Button>
            <span style={{ flex: 1 }} />
            <span style={{ position: 'relative', display: 'inline-flex' }}>
            <Button variant="ghost" size="md" dataTestId="wiki-open-panel" ariaLabel="Решения, журнал, здоровье, настройки"
              title="Решения, журнал, здоровье, настройки"
              leftIcon={<PanelRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} ariaExpanded={drawer === 'panel'} ariaControls={DRAWER_PANEL_ID}
              style={{ width: DRAWER_BTN, height: DRAWER_BTN, padding: 0 }}
              onClick={() => {
                // Панель открывается на выбранном разделе; не выбран — на первом, и выбор запоминается
                if (!panelTab) setPanelTab(panelTabs()[0].id);
                setDrawer(d => d === 'panel' ? null : 'panel');
              }}>{null}</Button>
            <ReplacementDot />
            </span>
          </div>
          {errorBanner}
          {centerPane}
          {drawer === 'tree' && (
            <WikiDrawer side="left" label="Дерево разделов" id={DRAWER_TREE_ID} onClose={() => setDrawer(null)}>
              <PanelHead title="Разделы" onClose={() => setDrawer(null)} />
              <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>{drawerTree ? drawerTree(() => setDrawer(null)) : renderTree(() => setDrawer(null))}</div>
            </WikiDrawer>
          )}
          {drawer === 'panel' && (
            <WikiDrawer side="right" label="Панель вики" id={DRAWER_PANEL_ID} onClose={() => setDrawer(null)}>
              <PanelHead title={panelTab ? panelLabel : 'Панель вики'} onClose={() => setDrawer(null)} />
              <div style={{ display: 'flex', justifyContent: 'center', padding: SP.xs, borderBottom: `1px solid ${C.border}` }}>
                <PillSwitch<WikiSubTabId>
                  value={panelTab ?? panelTabs()[0].id} fill isMobile iconsOnly
                  onChange={setPanelTab}
                  options={panelTabs().map(t => ({ value: t.id, label: t.label, icon: wikiSubTabIcon(t), title: t.label }))}
                />
              </div>
              {/* Строка раздела под переключателем (записка п. 1, кадр mob-journal): заменяет
                  бейдж шапки на мобиле; имя — короткое, как у бейджа; полное — в title */}
              {shownSection && (
                <div
                  data-testid="wiki-drawer-section-line"
                  title={`Раздел вики: ${sectionLabel(shownSection)}`}
                  style={{
                    display: 'flex', alignItems: 'center', gap: SP.sm, flexShrink: 0,
                    padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm, color: C.textSecondary,
                    borderBottom: `1px solid ${C.borderLight}`,
                  }}
                >
                  <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                  <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    Раздел: <b style={{ color: C.textHeading, fontWeight: 600 }}>{sectionLabel(shownSection)}</b>
                  </span>
                </div>
              )}
              <WikiPanelBody tab={panelTab ?? panelTabs()[0].id} selectedKey={selectedKey} />
            </WikiDrawer>
          )}
          {dialog}
        </div>
      );
    }
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minHeight: 0, height: '100%' }}>
        {errorBanner}
        <div style={{ display: 'flex', gap: SP.md, flex: 1, minHeight: 0 }}>
          {treeHost && createPortal(renderTree(), treeHost)}
          {centerPane}
        </div>
        {dialog}
      </div>
    );
  }

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

      {errorBanner}

      <div style={{ display: 'flex', gap: SP.md, flex: 1, minHeight: 0 }}>
        {/* Дерево — колонка слева только на десктопе. На мобили оно не стоит над каталогом
            (каталог схлопывался в нулевую высоту — хвост ревью S5c): там дерево — экран
            «Разделы», который открывает кнопка «Раздел ▸» внутри каталога */}
        {!isMobile && (
          <div style={{ width: TREE_W, flexShrink: 0 }}>
            <WikiSectionTree sections={sections} selectedKey={selectedKey} onSelect={(k) => selectSection(k, true)} />
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
                      {/* size="md": тач-цель ≥40 px (S5d-дельта, W4-2): sm — 32 px, ниже цели */}
                      <Button
                        variant="ghost" size="md"
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
                      onSelect={k => { selectSection(k, true); setShowSections(false); }}
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

      {dialog}
    </div>
  );
}
