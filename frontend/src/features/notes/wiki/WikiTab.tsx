// Вкладка «Вики» (макет llm-wiki-v2.html, §1 «Дерево разделов и каталог»): дерево разделов
// + каталог выбранного раздела. Состояния: «Вики не заведена» с «Завести», сирота с
// «Удалить раздел» (ConfirmDialog, danger), локальный проект, ошибка загрузки с «Повторить»,
// отказ контракта — русским текстом, не сырым кодом.
//
// Данные — только стор S5a (lib/wiki): ensureWikiLoaded при монтировании, каталог раздела —
// GET /api/wiki/section (оглавление строится из него: проекции _index/_log через
// /api/wiki/page не читаются — 400 wiki_page_key, хвост ревью S2f Н1).
// Подключение к странице заметок и шапка «Заметки · Вики» — шаг S5d, здесь их нет.

import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, BookOpen, Check, Folder, Lock, Monitor, Shield, Trash2 } from 'lucide-react';
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
  useWikiCatalog,
  useWikiTree,
  type WikiPageInfoDto,
  type WikiSectionDto,
} from '../../../lib/wiki';
import { WikiSectionTree } from './WikiSectionTree';
import { WIKI_SUBTABS, wikiSubTabIcon, WikiSubTabStub, type WikiSubTabId } from './wikiTabs';

// Колонка дерева на десктопе; на мобиле — одна колонка, дерево над каталогом
const TREE_W = 300;

// ─── Словари подписей ───

// Статусы страницы — их считает код, не модель (ADR-035 §3, WikiFrontmatter.StatusValues)
const PAGE_STATUS: Record<string, { tone: BadgeTone; text: string; title: string }> = {
  active: { tone: 'success', text: 'актуальна', title: 'Утверждения подтверждены источниками, противоречий нет' },
  stale: { tone: 'neutral', text: 'устарела', title: 'Источник новее опроверг утверждение или страница не менялась 90 дней' },
  disputed: { tone: 'warning', text: 'спорная', title: 'Есть открытые противоречия — см. раздел на странице' },
  drift: { tone: 'danger', text: 'дрейф', title: 'Файл изменён мимо Хранителя' },
};

// Типы страниц — WikiFrontmatter.TypeValues
const PAGE_TYPE: Record<string, string> = {
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

// Отказ загрузки — с «Повторить»; отказ действия (отклонил бэк) — без повторa:
// повторять надо само действие, а не перечитку
function ErrorNotice({ e, onRetry }: { e: unknown; onRetry?: () => void }) {
  const text = e instanceof WikiError ? wikiErrorText(e) : 'Не удалось загрузить вики';
  return (
    <div data-testid="wiki-error" style={{ padding: SP.sm }}>
      <Notice
        tone="danger"
        icon={AlertTriangle}
        title={text}
        action={onRetry ? <Button variant="ghost" size="sm" onClick={onRetry}>Повторить</Button> : undefined}
      />
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

// Строка каталога: тип, название, бейдж статуса, summary, число источников.
// Открытие страницы — следующий шаг фичи, здесь строка не кликается.
function PageRow({ page }: { page: WikiPageInfoDto }) {
  const st = page.status ? PAGE_STATUS[page.status] : undefined;
  return (
    <div style={{ display: 'flex', gap: SP.sm, padding: `${SP.sm}px ${SP.sm}px`, borderRadius: R.md }}>
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
    </div>
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

  const load = useCallback(() => {
    setLoadError(null);
    // Смена владельца в этой же вкладке — известный минор S5a (N1): самолечится новым
    // ensureWikiLoaded; здесь не лечим.
    ensureWikiLoaded().catch(e => setLoadError(e));
  }, []);

  useEffect(() => {
    if (!enabled) return;
    load();
  }, [enabled, load]);

  const sections = tree?.sections ?? [];
  const selected = selectedKey ? sections.find(s => s.key === selectedKey) ?? null : null;
  const catalog = useWikiCatalog(selectedKey ?? '');

  // Раздел по умолчанию — когда своего ещё нет или выбранный исчез из дерева
  useEffect(() => {
    if (!tree?.initialized) return;
    if (selectedKey && tree.sections.some(s => s.key === selectedKey)) return;
    setSelectedKey(defaultSectionKey(tree.sections));
  }, [tree, selectedKey]);

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

  const commonKey = groupSectionsByKind(sections).common[0]?.key ?? null;

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
    if (selected.local) return <LocalSection onOpenCommon={commonKey ? () => setSelectedKey(commonKey) : undefined} />;

    const pages = catalog?.pages ?? [];
    return (
      <div style={{ padding: SP.sm }}>
        {selected.orphan && (
          <OrphanNotice onDelete={() => setConfirmDelete(selected)} />
        )}
        <SectionHeader section={selected} count={selected.pageCount} />
        {pages.length
          ? pages.map(p => <div key={p.pageKey}><PageRow page={p} /></div>)
          : (
            <div style={{ padding: `${SP.md}px ${SP.sm}px`, fontSize: FS.sm, color: C.textMuted }}>
              В разделе пока нет страниц. Их напишет Хранитель, когда разрешите разбор.
            </div>
          )}
      </div>
    );
  };

  const subtab = WIKI_SUBTABS.find(t => t.id === tab) ?? WIKI_SUBTABS[0];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minHeight: 0, height: '100%' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: `0 ${SP.sm}px` }}>
        <PillSwitch<WikiSubTabId>
          value={tab}
          fill
          isMobile={isMobile}
          iconsOnly={isMobile}
          onChange={setTab}
          options={WIKI_SUBTABS.map(t => ({ value: t.id, label: t.label, icon: wikiSubTabIcon(t), title: t.label }))}
        />
      </div>

      {actionError ? <div style={{ padding: `0 ${SP.sm}px` }}><ErrorNotice e={actionError} onRetry={load} /></div> : null}

      <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.md, flex: 1, minHeight: 0 }}>
        <div style={{ width: isMobile ? '100%' : TREE_W, flexShrink: 0 }}>
          <WikiSectionTree sections={sections} selectedKey={selectedKey} onSelect={setSelectedKey} />
        </div>
        <div style={{ flex: 1, minWidth: 0, overflow: 'auto' }}>
          {subtab.component
            ? subtab.component()
            : subtab.id === 'pages'
              ? center()
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
            await deleteWikiSection(confirmDelete.key);
            setConfirmDelete(null);
            setSelectedKey(null);
          }}
          onCancel={() => setConfirmDelete(null)}
        />
      )}
    </div>
  );
}
