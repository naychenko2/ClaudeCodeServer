// Страница вики (только чтение): макет llm-wiki-v2.html, экраны «Страница вики» и «Хроника
// чата». Страница только для чтения — ни одного контрола правки текста; единственное
// действие — «Принять как есть» при дрейфе (решение человека о файле, ADR-035, решение 6):
// только при drift из ответа бэка, блокируется на время запроса, ошибка называется (не молча).
//
// Данные — страница из стора (lib/wiki: GET /api/wiki/page отдаёт {pageKey, content,
// drift?, manifestState?} — контракт S2f плюс доработка S5b): экран читает слот последней
// прочитанной страницы, поэтому перечитывание после adopt видно здесь без локального
// состояния. Дрейф — поле ответа (его считает бэк сверкой файла с манифестом), а не шапка
// status: шапку пишет модель, она не сигнал (ревью S5b M1). Разбор здесь — зеркало бэкового
// WikiPage.Parse
// (WikiPage.cs:68): шапка → '# заголовок' → '## секции' → '[^sN]: [[цель]] — «цитата»'.
// Фронт-разбор терпимый: страница с неразобранной шапкой или секциями остаётся видимой
// (тот же принцип, что у каталога, lib/wiki.ts:40) — строгость бэка это его гейт записи,
// не витрина. Нераспознанную шапку при этом человек видит: плашка вместо молчаливого
// pageKey в заголовке, который иначе читается как настоящее имя страницы (хвост ревью
// S5b). Порядок секций на странице — порядок файла: канонический пишет сам Хранитель
// (ADR-035, решение 3), витрина его не переупорядочивает. Рендер сносок в общем
// MarkdownViewer — S5e, здесь его не ждём: маркер-
// надстрочник с цитатой в title и блок «Источники» из определений сносок.
//
// Открытие страницы — из каталога (WikiTab, одна точка подключения); открытие по ссылкам
// внутри страницы не в этом шаге. Сирота: имени и даты удаления проекта бэк не отдаёт
// (хвост S2f) — плашка общая, как в каталоге (WikiTab.OrphanNotice).

import { useMemo, useState } from 'react';
import type { MouseEvent, KeyboardEvent } from 'react';
import type { ReactNode } from 'react';
import { AlertTriangle, CheckCircle2, ChevronLeft, Lock, Pencil, Quote, ScrollText, Sparkles, Undo2 } from 'lucide-react';
import { C, FONT, FS, R, SP, useIsMobile, useFeature, FLAGS, WikiError, adoptWikiPage, useWikiPage, wikiAuthorshipOf, wikiFootnoteLabel, wikiOverridesOf, type WikiAuthorship, type WikiOverrideDto, type WikiPageOverriddenDto, type WikiSectionDto } from 'aihome_shell/kit';
import { Badge } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { Notice } from '../../../components/ui/Notice';
import { PressableArea } from '../../../components/ui/PressableArea';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { fmtDayMonth, WikiFootnotePopover, type FootnoteView } from './WikiFootnotePopover';
import { WikiPageEditor, type EditDone } from './WikiPageEditor';
import { EDIT_TEXT, OVERRIDE_TEXT, PAGE_STATUS, PAGE_TYPE, sectionPrefix, wikiErrorText } from './wikiLabels';
import { useRestoreWikiStatement } from './useRestoreWikiStatement';

// ─── Разбор страницы (зеркало бэкового WikiPage.Parse, терпимый) ───

const TITLE_RX = /^# (.+)$/;
const SECTION_RX = /^## (.+)$/;
// Определение сноски: [^id]: [[цель]] — «цитата» (зеркало бэковой WikiPage.cs:37;
// скобка в классе экранирована: в JS `[^]` — «любой символ», не отрицание)
const FN_DEF_RX = /^\[\^([^\]\r\n]+)\]: (.+?) — «(.*)»$/;
// Ссылка сноски и ссылка вики внутри строки: первая группа — id сноски, вторая — цель [[…]]
const INLINE_RX = /\[\^([^\]\r\n]+)\]|\[\[([^\]\r\n]+)\]\]/g;

export interface ParsedWikiPage {
  type: string | null;
  kind: string | null;
  status: string | null;
  summary: string | null;
  updated: string | null;
  aliases: string[];
  title: string | null;
  sections: { name: string; lines: string[] }[];
  footnotes: { id: string; target: string; quote: string }[];
  // Строки вне секций — для бэка это ошибка формата, для витрины — текст страницы
  strayLines: string[];
  // Шапка разобрана по минимуму бэковой валидности: ограда закрыта и в ней есть 'type'
  // из семи (WikiFrontmatter.cs:125 — без type модель null). Остальное витрина терпит
  frontmatterOk: boolean;
}

// [a, b] — та же списочная форма, что у бэка (WikiFrontmatter.ParseList); прочее — пусто
function parseListValue(v: string): string[] {
  if (!v.startsWith('[') || !v.endsWith(']')) return [];
  const inner = v.slice(1, -1).trim();
  return inner ? inner.split(', ').map(s => s.trim()).filter(Boolean) : [];
}

export function parseWikiPage(text: string): ParsedWikiPage {
  const lines = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  const page: ParsedWikiPage = {
    type: null, kind: null, status: null, summary: null, updated: null, aliases: [],
    title: null, sections: [], footnotes: [], strayLines: [], frontmatterOk: false,
  };
  let i = 0;
  // Шапка: только известные ключи (type/kind/status/summary/updated/aliases);
  // неизвестные и кривые строки пропускаем — витрине они не нужны
  if (lines[0] === '---') {
    const close = lines.indexOf('---', 1);
    if (close > 0) {
      for (let j = 1; j < close; j++) {
        const sep = lines[j].indexOf(': ');
        if (sep <= 0) continue;
        const key = lines[j].slice(0, sep);
        const value = lines[j].slice(sep + 2);
        if (key === 'type') page.type = value;
        else if (key === 'kind') page.kind = value;
        else if (key === 'status') page.status = value;
        else if (key === 'summary') page.summary = value;
        else if (key === 'updated') page.updated = value;
        else if (key === 'aliases') page.aliases = parseListValue(value);
      }
      i = close + 1;
      // «Разобрана» — по минимуму бэка: тип из семи (WikiFrontmatter.cs:125); прочие поля
      // витрина терпит. Неразобранная — плашка на экране, а не молчаливый pageKey
      page.frontmatterOk = page.type !== null && page.type in PAGE_TYPE;
    }
  }
  let current: { name: string; lines: string[] } | null = null;
  for (; i < lines.length; i++) {
    const line = lines[i];
    if (!line) continue;
    const fn = FN_DEF_RX.exec(line);
    if (fn) {
      const raw = fn[2].trim();
      page.footnotes.push({
        id: fn[1],
        target: raw.startsWith('[[') && raw.endsWith(']]') ? raw.slice(2, -2) : raw,
        quote: fn[3],
      });
      continue;
    }
    const t = TITLE_RX.exec(line);
    if (t && !page.title) { page.title = t[1]; continue; }
    const s = SECTION_RX.exec(line);
    if (s) { current = { name: s[1], lines: [] }; page.sections.push(current); continue; }
    if (current) current.lines.push(line);
    else page.strayLines.push(line);
  }
  return page;
}

// Дата шапки — ISO yyyy-MM-dd (тот формат, что в фикстурах бэка); в UI — Д.М.ГГГГ, как в
// макете. Неразобранное — как написано (не выдумываем дату)
function fmtDate(s: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : s;
}

// Строка содержания: [^sN] → надстрочник-маркер с цитатой в title; [[X]] → текст без
// скобок (открытие по ссылке — не этот шаг). Полный markdown-рендер — S5e
// Со включённым авторством (F3a) сноска — нажимаемая плашка: у ИИ прежний номер, у человека
// «✎ имя, дата»; клик открывает поповер. Без fnx — прежний надстрочник.
interface FootnoteUi { views: Map<string, FootnoteView>; onOpen: (v: FootnoteView, el: HTMLElement) => void }

function FootnotePlate({ v, ui }: { v: FootnoteView; ui: FootnoteUi }) {
  const open = (e: MouseEvent | KeyboardEvent) => ui.onOpen(v, e.currentTarget as HTMLElement);
  return (
    <PressableArea
      onPress={open}
      aria-haspopup="dialog"
      aria-label={v.human ? `Сноска: правка человека, ${fmtDayMonth(v.captured)}` : `Сноска ${v.label}`}
      data-testid="wiki-footnote"
      style={{
        display: 'inline-flex', alignItems: 'center', verticalAlign: 'baseline', minHeight: 22, padding: '0 6px',
        margin: '0 2px', borderRadius: R.sm, fontSize: FS.xxs, fontWeight: 700, whiteSpace: 'nowrap',
        color: v.human ? C.textSecondary : C.info, background: v.human ? C.bgSelected : C.infoBg,
      }}
    >
      {v.human ? `✎ ${v.author ?? 'вы'}${v.captured ? `, ${fmtDayMonth(v.captured)}` : ''}` : v.label}
    </PressableArea>
  );
}

function renderLine(line: string, quotes: Map<string, string>, keyBase: string, ui?: FootnoteUi): ReactNode[] {
  const parts: ReactNode[] = [];
  let last = 0;
  let k = 0;
  INLINE_RX.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = INLINE_RX.exec(line))) {
    if (m.index > last) parts.push(line.slice(last, m.index));
    const view = m[1] !== undefined ? ui?.views.get(m[1]) : undefined;
    if (view && ui) {
      parts.push(<FootnotePlate key={`${keyBase}-fn${k++}`} v={view} ui={ui} />);
    } else if (m[1] !== undefined) {
      parts.push(
        <sup key={`${keyBase}-fn${k++}`} title={quotes.get(m[1]) ?? m[1]} style={{ color: C.accent, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap' }}>
          {m[1]}
        </sup>,
      );
    } else {
      parts.push(m[2]);
    }
    last = m.index + m[0].length;
  }
  if (last < line.length) parts.push(line.slice(last));
  return parts;
}

// ─── Метки замен (F3b) ───

// Сравнение строки страницы с утверждением из overrides[]: сервер отдаёт его без сносок и
// маркера, на странице — со сносками. Как у сервера: снимаем сноски, схлопываем пробелы, NFC
const REF_STRIP_RX = /\s*\[\^[^\]\r\n]+\]/g;
export function normStatement(s: string): string {
  return s.replace(BULLET_RX, '').replace(REF_STRIP_RX, '').replace(/\s+/g, ' ').trim().normalize('NFC');
}

// Что дорисовать у строки: метка у действующего утверждения / подпись у прежней строки «Истории»
export interface LineMarks {
  tags: Map<string, { kind: 'replaced' | 'disputed'; at: string }>;
  history: Set<string>;
}

function buildMarks(overrides: WikiOverrideDto[]): LineMarks | undefined {
  if (!overrides.length) return undefined;
  const marks: LineMarks = { tags: new Map(), history: new Set() };
  for (const o of overrides) {
    if (o.op === 'supersede') {
      if (o.statement) marks.tags.set(normStatement(o.statement), { kind: 'replaced', at: o.at });
      if (o.previous) marks.history.add(normStatement(o.previous));
    } else if (o.previous ?? o.statement) {
      // Спор: человеческий текст на месте — метка у него (previous), довод ИИ лежит в «Противоречиях»
      marks.tags.set(normStatement((o.previous ?? o.statement) as string), { kind: 'disputed', at: o.at });
    }
  }
  return marks;
}

function MarkTag({ kind, at }: { kind: 'replaced' | 'disputed'; at: string }) {
  const replaced = kind === 'replaced';
  return (
    <span
      data-testid="wiki-override-tag"
      data-kind={kind}
      title={replaced ? 'Метка видна 14 дней после замены' : undefined}
      style={{ marginLeft: SP.xs, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap', color: replaced ? C.info : C.warningText }}
    >
      {replaced ? `${OVERRIDE_TEXT.replacedTag} · ${fmtDayMonth(at)}` : `⚠ ${OVERRIDE_TEXT.disputedTag}`}
    </span>
  );
}

// ─── Куски страницы ───

// Пункт строки: маркер списка («- ») за собой не повторяем
const BULLET_RX = /^[-*]\s+/;

const EMPTY_HINT: Record<string, string> = {
  Противоречия: 'Открытых противоречий нет.',
  История: 'Ничего не замещено.',
};

function SectionBlock({ name, lines, quotes, ui, marks }: {
  name: string;
  lines: string[];
  quotes: Map<string, string>;
  ui?: FootnoteUi;
  marks?: LineMarks;
}) {
  return (
    <section data-testid="wiki-section" data-section-name={name} style={{ marginTop: SP.lg }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
        <h3 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>{name}</h3>
        {lines.length > 0 && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{lines.length}</span>}
      </div>
      {lines.length ? (
        <ul style={{ margin: `${SP.sm}px 0 0`, paddingLeft: SP.lg, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {lines.map((ln, i) => (
            <li key={i} style={{ fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5, overflowWrap: 'anywhere' }}>
              {renderLine(ln.replace(BULLET_RX, ''), quotes, `s${i}`, ui)}
              {marks && name !== 'История' && (() => {
                const t = marks.tags.get(normStatement(ln));
                return t ? <MarkTag kind={t.kind} at={t.at} /> : null;
              })()}
              {marks && name === 'История' && marks.history.has(normStatement(ln)) && (
                <span data-testid="wiki-history-caption" style={{ marginLeft: SP.xs, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>
                  · {OVERRIDE_TEXT.historyCaption}
                </span>
              )}
            </li>
          ))}
        </ul>
      ) : (
        <div style={{ marginTop: SP.xs, fontSize: FS.sm, color: C.textMuted }}>
          {EMPTY_HINT[name] ?? 'Пока ничего нет.'}
        </div>
      )}
    </section>
  );
}

// «Источники» — блок из определений сносок; отдельно в файле он не хранится (макет)
function SourcesBlock({ footnotes }: { footnotes: ParsedWikiPage['footnotes'] }) {
  return (
    <section data-testid="wiki-sources" style={{ marginTop: SP.lg }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
        <h3 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>Источники</h3>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{footnotes.length}</span>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginTop: SP.sm }}>
        {footnotes.map(f => (
          <div key={f.id} style={{ display: 'flex', gap: SP.sm, padding: SP.sm, border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard }}>
            <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, minWidth: 28, flexShrink: 0 }}>{f.id}</span>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{ fontSize: FS.base, fontWeight: 500, color: C.textHeading, overflowWrap: 'break-word' }}>{f.target}</div>
              <div style={{ fontFamily: FONT.serif, fontSize: FS.md, color: C.textSecondary, marginTop: 2 }}>«{f.quote}»</div>
            </div>
          </div>
        ))}
      </div>
      <div style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textMuted }}>
        Блок собирается из сносок — отдельно в файле он не хранится.
      </div>
    </section>
  );
}

// Сводный бейдж авторства страницы (записка макета, §3)
const AUTHORSHIP_BADGE: Record<WikiAuthorship, string> = {
  ai: 'Ведёт ИИ',
  together: 'Ведёте вместе',
  human: 'Написали вы',
};

// ─── Страница ───

interface Props {
  section: WikiSectionDto;
  pageKey: string;
  onBack: () => void;
}

export function WikiPageView({ section, pageKey, onBack }: Props) {
  const isMobile = useIsMobile();
  // Страницу и её дрейф читает стор (ревью S5b M2): после adopt он перечитывает страницу,
  // и экран показывает новый ответ бэка, а не локальную копию прежнего
  const dto = useWikiPage(section.key, pageKey);
  const content = dto?.content ?? '';
  const page = useMemo(() => parseWikiPage(content), [content]);
  const [adoptBusy, setAdoptBusy] = useState(false);
  const [adoptError, setAdoptError] = useState<unknown>(null);
  // Авторство и поповер сносок — только с флагом единых «Заметок и Вики» и включённой вики
  const unifiedFlag = useFeature(FLAGS.notesWikiUnified);
  const wikiFlag = useFeature(FLAGS.llmWiki);
  const unified = unifiedFlag && wikiFlag;
  const [openFn, setOpenFn] = useState<{ view: FootnoteView; anchor: DOMRect | null; pageId: string } | null>(null);

  // Дрейф — поле ответа (его считает бэк сверкой файла с манифестом). Шапка status: drift
  // — текст модели, не сигнал: плашка и кнопка по ней не поднимаются (ревью S5b M1)
  const isChronicle = page.type === 'chronicle';
  const st = page.status ? PAGE_STATUS[page.status] : undefined;
  const drift = dto?.drift === true;
  const quotes = new Map(page.footnotes.map(f => [f.id, f.quote]));

  // Сноски с происхождением (footnotes[] страницы, B7); нет поля — прежний вид
  const authored = dto as WikiPageOverriddenDto | null;
  const pageId = `${section.key}\n${pageKey}`;
  const footnoteUi = useMemo<FootnoteUi | undefined>(() => {
    if (!unified || !authored?.footnotes?.length) return undefined;
    const views = new Map<string, FootnoteView>();
    for (const f of authored.footnotes) {
      const label = wikiFootnoteLabel(f.label);
      views.set(label, {
        label, card: f.card, cardPage: f.cardPage ?? null, human: f.origin === 'human', author: f.author ?? null,
        captured: f.captured ?? null, quote: page.footnotes.find(p => p.id === label)?.quote ?? '',
      });
    }
    return { views, onOpen: (view, el) => setOpenFn({ view, anchor: el.getBoundingClientRect(), pageId }) };
  }, [unified, authored?.footnotes, page.footnotes, pageId]);
  // Поповер принадлежит странице, на которой открыт: со сменой страницы он не показывается
  const popFn = openFn && openFn.pageId === pageId ? openFn : null;
  const authorship: WikiAuthorship = wikiAuthorshipOf(authored ?? {});
  // Замены и споры за 14 дней — окно считает сервер; без флага метки не рисуем
  const overrides = useMemo(() => (unified ? wikiOverridesOf(authored) : []), [unified, authored]);
  const marks = useMemo(() => buildMarks(overrides), [overrides]);
  const restorer = useRestoreWikiStatement(section.key, pageKey);

  // «О чём» — не секция-заголовок, а строка-описание под заголовком (макет «Хроника»)
  const aboutSection = isChronicle ? page.sections.find(s => s.name === 'О чём') : undefined;
  const bodySections = isChronicle ? page.sections.filter(s => s.name !== 'О чём') : page.sections;
  const aboutText = aboutSection?.lines.map(l => l.replace(BULLET_RX, '')).join(' ') || page.summary;

  // Правка (F4a): состояние привязано к странице — со сменой страницы оно сбрасывается само
  const [editUi, setEditUi] = useState<{ id: string; editing: boolean; off: boolean; flash: 'saved' | 'unchanged' | 'restored' | null }>(
    { id: pageId, editing: false, off: false, flash: null },
  );
  const ui = editUi.id === pageId ? editUi : { id: pageId, editing: false, off: false, flash: null };
  const canEdit = unified && !ui.off && !!authored?.hash && !isChronicle && !drift && !section.orphan && !section.local && !section.sphereReserved;
  const editing = canEdit && ui.editing;
  const canRestore = canEdit && !editing && !!authored?.hash;
  const runRestore = (o: WikiOverrideDto) => {
    if (!authored?.hash) return;
    void restorer.restore(o, authored.hash).then(r => {
      if (r === 'done' || r === 'unchanged') {
        setEditUi({ id: pageId, editing: false, off: false, flash: r === 'done' ? 'restored' : 'unchanged' });
      }
    });
  };
  const onEditDone = (r: EditDone) => setEditUi({
    id: pageId, editing: false, off: r.kind === 'off',
    flash: r.kind === 'saved' ? 'saved' : r.kind === 'unchanged' ? 'unchanged' : null,
  });

  // Страницы в сторе нет — её ещё читают: родитель держит свой спиннер, здесь рендерить нечего
  if (!dto) return null;

  const runAdopt = () => {
    setAdoptBusy(true);
    setAdoptError(null);
    // После принятого adopt стор перечитывает страницу (lib/wiki) — плашка и кнопка уходят
    // по drift=false в новом ответе; локального флага «принято» больше нет (ревью S5b M2).
    // Отказ по-прежнему называется, кнопка блокируется на время запроса.
    void adoptWikiPage(section.key, pageKey)
      .catch(e => setAdoptError(e))
      .finally(() => setAdoptBusy(false));
  };

  return (
    <div data-testid="wiki-page" style={{ padding: SP.sm, maxWidth: 760 }}>
      {/* «Назад» — к каталогу раздела. На мобиле — тач-цель ≥ 40 px (урок ревью S5c/S5d) */}
      <Button
        variant="ghost"
        size={isMobile ? 'md' : 'sm'}
        dataTestId="wiki-page-back"
        ariaLabel="Вернуться к каталогу раздела"
        leftIcon={<ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        onClick={onBack}
      >
        Назад
      </Button>

      {section.orphan && (
        <Notice tone="warning" icon={AlertTriangle} title="Проект удалён" style={{ marginTop: SP.md }}>
          Раздел скрыт от всех чатов, агентов и персон. Страницы остались: их можно читать.
          Удалить раздел — из каталога, кнопкой «Назад».
        </Notice>
      )}

      {ui.flash && (
        <div data-testid="wiki-edit-flash" style={{ marginTop: SP.md }}>
          <Notice tone="info" icon={CheckCircle2} title={ui.flash === 'saved' ? EDIT_TEXT.saved : ui.flash === 'restored' ? OVERRIDE_TEXT.restored : EDIT_TEXT.unchanged} />
        </div>
      )}

      {ui.off && (
        <div data-testid="wiki-edit-off" style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title={EDIT_TEXT.offTitle}>{EDIT_TEXT.offText}</Notice>
        </div>
      )}

      {overrides.map(o => o.op === 'supersede' ? (
        <div key={o.packageId} data-testid="wiki-override-replaced" style={{ marginTop: SP.md }}>
          <Notice
            tone="info"
            icon={Sparkles}
            title={OVERRIDE_TEXT.replacedTitle}
            action={o.previous !== null && canRestore ? (
              <Button
                variant="secondary"
                size={isMobile ? 'md' : 'sm'}
                dataTestId="wiki-restore-btn"
                leftIcon={<Undo2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                loading={restorer.busy}
                onClick={() => runRestore(o)}
              >
                {OVERRIDE_TEXT.restore}
              </Button>
            ) : undefined}
          >
            {fmtDayMonth(o.at)}. {OVERRIDE_TEXT.replacedText}
          </Notice>
        </div>
      ) : (
        <div key={o.packageId} data-testid="wiki-override-disputed" style={{ marginTop: SP.md }}>
          <Notice tone="warning" icon={AlertTriangle} title={OVERRIDE_TEXT.disputedTitle}>
            {OVERRIDE_TEXT.disputedText}
          </Notice>
        </div>
      ))}

      {restorer.error ? (
        <div data-testid="wiki-restore-error" style={{ marginTop: SP.sm }}>
          <Notice
            tone="danger"
            icon={AlertTriangle}
            title={restorer.error}
            action={<Button variant="ghost" size="sm" onClick={restorer.clearError}>Закрыть</Button>}
          />
        </div>
      ) : null}

      {drift && (
        <Notice
          tone="warning"
          icon={AlertTriangle}
          title="Расходится с журналом Хранителя"
          style={{ marginTop: SP.md }}
          action={
            <Button
              variant="secondary"
              size={isMobile ? 'md' : 'sm'}
              dataTestId="wiki-adopt-btn"
              title="Хранитель запомнит текущий файл как верный и запишет в журнал «ручная правка вне Хранителя»"
              loading={adoptBusy}
              onClick={runAdopt}
            >
              Принять как есть
            </Button>
          }
        >
          Файл изменён мимо Хранителя. Пока вы не решите, пакеты по этой странице не
          применяются — ни сами, ни из «Входящих».
        </Notice>
      )}

      {adoptError ? (
        <div data-testid="wiki-adopt-error" style={{ marginTop: SP.sm }}>
          <Notice
            tone="danger"
            icon={AlertTriangle}
            title={adoptError instanceof WikiError ? wikiErrorText(adoptError) : 'Не удалось принять страницу'}
            action={<Button variant="ghost" size="sm" onClick={() => setAdoptError(null)}>Закрыть</Button>}
          >
            Страница осталась как есть — ничего не изменено.
          </Notice>
        </div>
      ) : null}

      {/* Шапка не прочитана — это видно: ниже в заголовке имя файла (фолбэк h2), а не
          молчаливое «что-то похоже на страницу» (хвост ревью S5b) */}
      {!page.frontmatterOk && (
        <Notice
          tone="warning"
          icon={AlertTriangle}
          title="Шапка страницы не разобрана — показано имя файла"
          style={{ marginTop: SP.md }}
        />
      )}

      {/* Кикер: префикс раздела (тот же форматтер, что у каталога) + тип + статус */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', marginTop: SP.md }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}>
          {sectionPrefix(section)}{isChronicle ? ' / Хроники' : ''} /
        </span>
        {isChronicle ? (
          <>
            <ScrollText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textSecondary }} />
            <span style={{ fontSize: FS.sm, color: C.textSecondary }}>хроника · чат</span>
          </>
        ) : (
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
            {PAGE_TYPE[page.type ?? ''] ?? 'Страница'}{page.kind ? ` · ${page.kind}` : ''}
          </span>
        )}
        {st && <Badge tone={st.tone} size="xs" title={st.title}>{st.text}</Badge>}
      </div>

      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 500, color: C.textHeading, lineHeight: 1.25, overflowWrap: 'break-word' }}>
        {page.title ?? pageKey}
      </h2>

      {isChronicle && aboutText ? (
        <p style={{ margin: `${SP.xs}px 0 0`, fontSize: FS.md, color: C.textSecondary, lineHeight: 1.5 }}>
          <b style={{ color: C.textHeading, fontWeight: 600 }}>О чём:</b> {aboutText}
        </p>
      ) : !isChronicle && page.summary ? (
        <p style={{ margin: `${SP.xs}px 0 0`, fontSize: FS.md, color: C.textSecondary, lineHeight: 1.5 }}>
          {page.summary}
        </p>
      ) : null}

      {unified && (
        <div data-testid="wiki-authorship" data-authorship={authorship} style={{ marginTop: SP.xs }}>
          <Badge tone={authorship === 'human' ? 'neutral' : 'info'} size="xs">
            {AUTHORSHIP_BADGE[authorship]}
          </Badge>
        </div>
      )}

      {canEdit && !editing && (
        <div style={{ marginTop: SP.sm }}>
          <Button
            variant="secondary"
            size={isMobile ? 'md' : 'sm'}
            dataTestId="wiki-edit-open"
            leftIcon={<Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            onClick={() => setEditUi({ id: pageId, editing: true, off: false, flash: null })}
          >
            {EDIT_TEXT.edit}
          </Button>
        </div>
      )}

      <div style={{ display: 'flex', gap: SP.md, flexWrap: 'wrap', marginTop: SP.sm, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          <Quote size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          {page.footnotes.length} ист.
        </span>
        {page.updated && <span>обновлено {fmtDate(page.updated)}</span>}
        {page.aliases.length > 0 && <span>aliases: {page.aliases.join(', ')}</span>}
      </div>

      {editing && authored?.hash ? (
        <WikiPageEditor
          key={pageId}
          section={section}
          pageKey={pageKey}
          content={content}
          hash={authored.hash}
          footnotes={authored.footnotes}
          onDone={onEditDone}
        />
      ) : (
        <>
      {/* Плашка «только чтение» — тихая строка, не Notice (макет): правки идут через Хранителя */}
      <div data-testid="wiki-readonly" style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start', marginTop: SP.md, padding: `${SP.sm}px ${SP.md}px`, background: C.bgInset, borderRadius: R.md, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
        <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0, marginTop: 2, color: C.textMuted }} />
        <span>
          {isChronicle ? (
            <>
              <b style={{ color: C.textHeading }}>Хроника пишется сама</b> каждую ночь, когда в чате
              появилось новое. Неверное — откатите чат целиком: чат получит пометку «Не разбирать».
            </>
          ) : (
            <>
              <b style={{ color: C.textHeading }}>Только чтение</b> — вики ведёт Хранитель: правки
              идут через него. Неверное утверждение? Откатите его пакет в «Журнале» или весь
              источник по сноске.
            </>
          )}
        </span>
      </div>

      {page.strayLines.length > 0 && (
        <div style={{ marginTop: SP.md, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
          {page.strayLines.join('\n')}
        </div>
      )}

      {bodySections.map(s => (
        <SectionBlock key={s.name} name={s.name} lines={s.lines} quotes={quotes} ui={footnoteUi} marks={marks} />
      ))}

      {page.footnotes.length > 0 && <SourcesBlock footnotes={page.footnotes} />}
        </>
      )}

      {popFn && (
        <WikiFootnotePopover
          sectionKey={section.key}
          pageKey={pageKey}
          fn={popFn.view}
          anchor={popFn.anchor}
          onClose={() => setOpenFn(null)}
        />
      )}
    </div>
  );
}
