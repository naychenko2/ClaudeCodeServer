import { cloneElement, isValidElement, useCallback, useEffect, useId, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import type { KeyboardEvent as ReactKeyboardEvent, ReactElement, ReactNode } from 'react';
import { createPortal } from 'react-dom';
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';
import rehypeSanitize from 'rehype-sanitize';
import type { Components } from 'react-markdown';
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter';
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';
import { ListChecks, MessageSquare } from 'lucide-react';
import { MermaidDiagram } from './MermaidDiagram';
import { Button } from './ui/Button';
import { C, FONT, R, SHADOW, Z } from '../lib/design';
import { api } from '../lib/api';
import { openChatById } from '../lib/openChat';
import { readStoredToken } from '../lib/offline';
import { openTaskInSection } from '../lib/tasks';
import { showToast } from '../lib/toast';
import { HTML_SCHEMA } from '../lib/markdownHtml';

// Результат резолва вики-имени (для hover-preview и embed-вставок)
export interface ResolvedNote {
  title: string;
  content: string;   // фрагмент по якорю либо весь текст
}

interface Props {
  content: string;
  // Позиции блоков в ИСХОДНОМ markdown: блочные элементы получают data-md-start/end
  // (сквозь препроцессинг wikilinks — офсеты переводятся картой). Нужны комментариям
  // к документам: выделение → офсеты источника, подсветка якорных блоков.
  blockPos?: boolean;
  // Клик по вики-ссылке [[Заголовок]] — target = «сырой» текст цели (до | и #)
  onWikilink?: (target: string) => void;
  // Нормализованные (lower/trim) заголовки существующих заметок — чтобы отличить
  // «живую» ссылку (accent) от «призрачной» (серый пунктир, цели ещё нет)
  existingTitles?: Set<string>;
  // Резолв заметки по имени (+якорь) — включает hover-preview и embed ![[…]]
  resolveNote?: (name: string, anchor?: string) => Promise<ResolvedNote | null>;
  // Источник текущей заметки — для URL картинок-вложений ![[img.png]]
  embedSource?: string;
  // Глубина вложенности embed (защита от циклов: на глубине ≥1 embed → просто ссылка)
  embedDepth?: number;
  // Скрыть H1-блок в самом начале документа (offset 0): заголовок рендерит
  // потребитель (hero-шапка заметки), дубль не нужен. Контент НЕ режется —
  // оффсеты остальных блоков не меняются, якоря комментариев продолжают жить.
  hideLeadingH1?: boolean;
  // Режим документации проекта (панель «Документы»): клик по НЕвнешней ссылке уходит сюда
  // вместо перехода браузера — так работают переходы между README/docs/** и открытие
  // файлов кода в центре. Внешние ссылки при этом уходят в новую вкладку.
  // Взаимоисключим с режимом заметок: onWikilink сильнее (см. notesMode ниже).
  onDocLink?: (href: string) => void;
  // Резолв относительного src картинки в загружаемый URL. Без него `![](img/x.png)` и
  // `<img src="docs/y.png">` в документе проекта не грузятся: путь относителен корню
  // репозитория, а не адресу страницы. Возвращает undefined — оставить src как есть.
  resolveImageSrc?: (src: string) => string | undefined;
  // Данные вики-сносок (S5e, доработка по ревью 684d71c0): id → {цитата, ref источника}.
  // Их передаёт вызывающий (вики-страница): компонент сам НЕ выводит сноски из текста —
  // форма цели в тексте страницы бэком не гарантирована. Без пропа [^id] не трогается
  // вообще: обычную GFM-сноску целиком рендерит remark-gfm, как до S5e. С пропом
  // перехватываются только [^id] с записью в карте; остальные — тоже GFM.
  wikiFootnotes?: Record<string, { quote: string; source: string }>;
}

const mono = FONT.mono;
const serif = FONT.serif;


// Отрезает YAML-frontmatter для ПРОСМОТРА (иначе title/annotates/теги рендерятся
// как текст: «key: value» перед `---` становится жирным setext-заголовком).
// offset — длина отрезанного: комментарии к документам сдвигают на неё офсеты якорей.
export function stripFrontmatter(md: string): { body: string; offset: number } {
  if (!md.startsWith('---')) return { body: md, offset: 0 };
  const m = /^---\r?\n[\s\S]*?\r?\n---[ \t]*(\r?\n|$)/.exec(md);
  return m ? { body: md.slice(m[0].length), offset: m[0].length } : { body: md, offset: 0 };
}

const IMG_EXT = /\.(png|jpe?g|gif|webp|svg|bmp|avif)$/i;

// Сегмент карты офсетов «препроцессированный → сырой» (для blockPos сквозь препроцессинг)
interface MapSegment { pre: number; raw: number; preLen: number; literal: boolean }

// Ограда ```/~~~забора по CommonMark — те же правила, что у бэкенда (Core/MarkdownFence):
// забор помнит СИМВОЛ и ДЛИНУ открывающего маркера и закрывается только им же, длиной не
// меньше и без хвоста; info-string бэктикового забора не может содержать бэктик. Иначе
// вложенный пример (внешний забор из четырёх бэктиков, внутри обычный ```) закрывался бы
// внутренним, а незакрытый забор от опечатки глушил бы весь остаток файла.
class FenceState {
  private marker = '';
  private len = 0;
  inFence(): boolean { return this.len > 0; }
  // true — строка сама маркер забора (её содержимое — код)
  consume(line: string): boolean {
    const m = /^ {0,3}(`{3,}|~{3,})/.exec(line);
    if (!m) return false;
    const run = m[1];
    const tail = line.slice(m[0].length);
    if (!this.inFence()) {
      if (run[0] === '`' && tail.includes('`')) return false;
      this.marker = run[0];
      this.len = run.length;
      return true;
    }
    if (run[0] !== this.marker || run.length < this.len || tail.trim().length > 0) return false;
    this.len = 0;
    return true;
  }
}

// Маска «внутри кода»: true — позиция i внутри ```/~~~забора или inline-`…`-кода.
// Те же границы, что у бэкенда (NotesService.CodeMask): ссылка/тег в коде — не ссылка и
// не тег (Obsidian): пример синтаксиса давал бы «призраков» графа и ложные теги.
// Экспортирована: редактор меток (features/notes/NoteEditor) считает «код» этой же
// функцией — второй копии этой маски во фронт быть не должно.
export function codeMask(md: string): boolean[] {
  const mask = new Array<boolean>(md.length).fill(false);
  const fence = new FenceState();
  let offset = 0;
  for (const raw of md.split('\n')) {
    const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
    if (fence.consume(line) || fence.inFence())
      for (let i = 0; i < raw.length && offset + i < mask.length; i++) mask[offset + i] = true;
    offset += raw.length + 1;   // +1 на '\n', на котором резали
  }
  for (const m of md.matchAll(/`[^`\n]+`/g))
    for (let i = m.index; i < m.index + m[0].length && i < mask.length; i++) mask[i] = true;
  return mask;
}

// Единый проход по синтаксису заметок и вики: [[…]]-ссылки/embeds и — только когда вызывающий
// передал карту вики-сносок (см. Props.wikiFootnotes) — определения и ссылки сносок [^id].
// Определение стоит ПЕРЕД ссылкой в альтернативе: иначе [^s1] в начале строки определения
// прочитался бы ссылкой, и определение рассыпалось бы на маркер + текст. Без карты ветки
// сносок не срабатывают вовсе: обычная GFM-сноска («[^1]: пояснение» без цитаты) — не синтаксис
// вики, её целиком и байт-в-байт делает remark-gfm (маркер sup > a и секция Footnotes).
const NOTE_SYNTAX_RX = /(!?)\[\[([^\]|]+)(?:\|([^\]]+))?\]\]|^\[\^([^\]\r\n]+)\]:[^\r\n]*$|\[\^([^\]\r\n]+)\]/gm;

// Данные сноски: цель и цитата — из карты вызывающего, недоверенный текст, никуда не
// интерпретируется как HTML (урок ревью S5b); цель опознаётся позже, по белому списку форм
// (classifyFnTarget).
export interface FootnoteDef { id: string; target: string; quote: string }

// Значение внутренней схемы в URL: битый percent-encoding не должен ронять рендер —
// в «fnref:%zz» процент литеральный и decode бросает URIError; остаётся сырое значение.
function decodeScheme(raw: string): string {
  try { return decodeURIComponent(raw); } catch { return raw; }
}

// Единый проход по [[Заметка]] / [[Заметка|подпись]] / ![[…]]: embeds и wikilinks
// переписываются в markdown-ссылки со схемами wikilink:/noteembed:/noteatt:
// (urlTransform ниже пропускает эти схемы, иначе санитайзер react-markdown их вырежет).
// Сноски — ТОЛЬКО для id из переданной карты footnoteIds (данные карточки компонент сам
// из текста не выводит — решение по ревью S5e): ссылки таких id переписываются в маркеры
// со схемой fnref:, определения выносятся из потока (блок «Источники» собирается UI из
// карты вызывающего, ADR-035 решение 3). Остальные [^id] остаются литеральными — их ведёт
// remark-gfm. Параллельно строится карта офсетов — blockPos переводит позиции блоков в сырой текст.
export function preprocessNotes(
  md: string,
  depth: number,
  footnoteIds?: Set<string>,
): { text: string; map: MapSegment[] } {
  // [[…]] внутри кода — не ссылка: переписанная, она стала бы URL-мусором в блоке кода
  // (и мусором в графе: бэкенд такие связи не считает). То же для сносок: определение или
  // ссылка в заборе — пример синтаксиса, не сноска. Пропущенное совпадение остаётся
  // литеральным сегментом карты офсетов — сдвиги blockPos от этого не меняются.
  const code = codeMask(md);
  let out = '';
  const map: MapSegment[] = [];
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = NOTE_SYNTAX_RX.exec(md)) !== null) {
    if (code[m.index]) continue;
    // Сноска без записи в карте вызывающего — не синтаксис вики: совпадение пропускается,
    // текст остаётся литеральным (следующий литеральный сегмент начнётся с не сдвинутого last)
    if ((m[4] !== undefined || m[5] !== undefined) && !footnoteIds?.has(m[4] ?? m[5]!)) continue;
    if (m.index > last) {
      map.push({ pre: out.length, raw: last, preLen: m.index - last, literal: true });
      out += md.slice(last, m.index);
    }
    const full = m[0];
    let replaced: string;
    if (m[4] !== undefined) {
      // Определение известной вики-сноски: из потока убирается целиком — её данные в карте
      // вызывающего; remark-gfm до неё не доходит
      replaced = '';
    } else if (m[5] !== undefined) {
      const id = m[5];
      replaced = `[${id}](fnref:${encodeURIComponent(id)})`;
    } else {
      const [bang, targetRaw, labelRaw] = [m[1], m[2], m[3]];
      const t = targetRaw.trim();
      const text = (labelRaw ?? targetRaw).trim();
      if (bang) {
        if (IMG_EXT.test(t)) replaced = `![${text}](noteatt:${encodeURIComponent(t)})`;
        else if (depth >= 1) replaced = `[${text}](wikilink:${encodeURIComponent(t)})`;
        else replaced = `[${text}](noteembed:${encodeURIComponent(t)})`;
      } else {
        replaced = `[${text}](wikilink:${encodeURIComponent(t)})`;
      }
    }
    map.push({ pre: out.length, raw: m.index, preLen: replaced.length, literal: false });
    out += replaced;
    last = m.index + full.length;
  }
  if (last < md.length) {
    map.push({ pre: out.length, raw: last, preLen: md.length - last, literal: true });
    out += md.slice(last);
  }
  return { text: out, map };
}

// Перевод офсета препроцессированного текста в сырой: внутри литерального сегмента 1:1,
// внутри заменённого — начало исходной [[…]]-конструкции.
function makeToRaw(map: MapSegment[] | null): (off: number) => number {
  if (!map || map.length === 0) return off => off;
  return (off: number) => {
    let lo = 0, hi = map.length - 1, idx = 0;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (map[mid].pre <= off) { idx = mid; lo = mid + 1; } else hi = mid - 1;
    }
    const seg = map[idx];
    return seg.literal ? seg.raw + Math.min(off - seg.pre, seg.preLen) : seg.raw;
  };
}

// Обёртка блочных компонентов: data-md-start/end с позициями исходного markdown
const POS_KEYS = ['h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'ul', 'ol', 'li', 'blockquote', 'table'] as const;

function withBlockPos(base: Components, toRaw: (off: number) => number): Components {
  const out = { ...base } as Record<string, unknown>;
  for (const k of POS_KEYS) {
    const orig = base[k] as (props: Record<string, unknown>) => ReactNode;
    out[k] = (props: Record<string, unknown>) => {
      const el = orig(props);
      const node = props.node as { position?: { start?: { offset?: number }; end?: { offset?: number } } } | undefined;
      const start = node?.position?.start?.offset;
      const end = node?.position?.end?.offset;
      if (!isValidElement(el) || start == null || end == null) return el;
      return cloneElement(el as ReactElement, {
        'data-md-start': toRaw(start),
        'data-md-end': toRaw(end),
      } as Record<string, unknown>);
    };
  }
  return out as Components;
}

// Имя цели для проверки существования: последний сегмент пути, без якоря, lower/trim
function normTarget(target: string): string {
  return target.split('/').pop()!.split('#')[0].trim().toLowerCase();
}

// «Имя#Якорь» → [имя, якорь?]
function splitAnchor(name: string): [string, string | undefined] {
  const i = name.indexOf('#');
  return i < 0 ? [name, undefined] : [name.slice(0, i), name.slice(i + 1)];
}

// Кэш резолва на сессию (hover-preview дёргается часто)
const resolveCache = new Map<string, ResolvedNote | null>();

async function cachedResolve(
  resolveNote: NonNullable<Props['resolveNote']>, name: string,
): Promise<ResolvedNote | null> {
  if (resolveCache.has(name)) return resolveCache.get(name)!;
  const [n, anchor] = splitAnchor(name);
  const r = await resolveNote(n, anchor).catch(() => null);
  resolveCache.set(name, r);
  return r;
}

// Сбрасывается стором заметок при изменениях (см. lib/notes.ts)
export function clearResolveCache(): void {
  resolveCache.clear();
}

// ─── Сноски (S5e, ADR-035 §12б) ───
// Цель сноски опознаётся ТОЛЬКО по белому списку форм (урок ревью S5b: содержимое страницы
// недоверенно): session:{id}#m{a}-{b}, task:{id}, [[…]] или внутренний путь. Всё прочее —
// javascript:, data:, //host, неизвестное слово — остаётся текстом, ссылкой не становится.

const FN_CHAT_RX = /^session:([A-Za-z0-9_-]+)#m(\d+)-(\d+)$/;
const FN_TASK_RX = /^task:([A-Za-z0-9_-]+)$/;
const FN_WIKI_RX = /^\[\[([^\]|]+)(?:\|([^\]]+))?\]\]$/;
// Внутренний путь заметки/файла: сегменты через '/', без протокола (':' до '/' — схема:
// javascript:, data:, C:\) и не '//host' (протокол-относительный URL — внешняя цель).
const FN_PATH_RX = /^[^:/\s][^:/]*\/[^:]+$/;

type FnTarget =
  | { kind: 'chat'; sessionId: string; range: string }
  | { kind: 'task'; taskId: string }
  | { kind: 'note'; target: string; label: string }
  | { kind: 'text' };

function classifyFnTarget(raw: string): FnTarget {
  const c = FN_CHAT_RX.exec(raw);
  if (c) return { kind: 'chat', sessionId: c[1], range: `m${c[2]}-${c[3]}` };
  const t = FN_TASK_RX.exec(raw);
  if (t) return { kind: 'task', taskId: t[1] };
  const w = FN_WIKI_RX.exec(raw);
  if (w) return { kind: 'note', target: w[1].trim(), label: (w[2] ?? w[1]).trim() };
  if (FN_PATH_RX.test(raw)) return { kind: 'note', target: raw, label: raw };
  return { kind: 'text' };
}

// Нумерацию s1…sN Хранителя маркер и карточка показывают числом (макет M2); прочий id — как есть
function fnDisplay(id: string): string {
  const m = /^s(\d+)$/.exec(id);
  return m ? m[1] : id;
}

const TASK_MISSING_HINT = 'Задача не найдена — возможно, она удалена';

function openTaskFromFootnote(taskId: string): void {
  void api.tasks.get(taskId)
    .then(t => { if (t) openTaskInSection(t); else showToast('Задача', TASK_MISSING_HINT, 'info'); })
    .catch(() => showToast('Задача', TASK_MISSING_HINT, 'info'));
}

// Содержание сноски: цитата + цель по типу (чат / задача / заметка / текст). Одно на три:
// поповер, мобильная шторка и строка блока «Источники». Котировка и цель — текст, не HTML.
function FootnoteBody({ def, onOpenNote }: { def: FootnoteDef; onOpenNote?: (target: string) => void }) {
  const target = classifyFnTarget(def.target);
  return (
    <>
      <div data-testid={`fn-quote-${def.id}`} style={{ fontFamily: serif, fontSize: 13.5, color: C.textHeading, borderLeft: `3px solid ${C.accentMuted}`, paddingLeft: 9, marginBottom: 8, overflowWrap: 'anywhere' }}>
        «{def.quote}»
      </div>
      {target.kind === 'chat' && (
        <>
          <div data-testid={`fn-src-${def.id}`} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 12, color: C.textSecondary, overflowWrap: 'anywhere' }}>
            <MessageSquare size={13} strokeWidth={1.7} style={{ flexShrink: 0 }} aria-hidden />
            <span>чат <span style={{ fontFamily: mono, fontSize: 11 }}>#{target.range}</span></span>
          </div>
          {/* Якоря сообщения в маршруте чата во фронте нет: кнопка открывает сам чат (без прокрутки к m{a}) */}
          <Button variant="ghost" size="xs" dataTestId={`fn-open-chat-${def.id}`} style={{ marginTop: 6 }} onClick={() => { void openChatById(target.sessionId); }}>
            Открыть в чате
          </Button>
        </>
      )}
      {target.kind === 'task' && (
        <div data-testid={`fn-src-${def.id}`} style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', overflowWrap: 'anywhere' }}>
          <Button
            variant="ghost"
            size="xs"
            dataTestId={`fn-open-task-${def.id}`}
            leftIcon={<ListChecks size={13} strokeWidth={1.7} />}
            title={`task:${target.taskId}`}
            onClick={() => openTaskFromFootnote(target.taskId)}
          >
            Задача {target.taskId.slice(0, 8)}
          </Button>
        </div>
      )}
      {target.kind === 'note' && (
        onOpenNote ? (
          <a
            role="link"
            tabIndex={0}
            data-testid={`fn-open-note-${def.id}`}
            onClick={e => { e.preventDefault(); onOpenNote(target.target); }}
            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpenNote(target.target); } }}
            style={{ color: C.accent, fontWeight: 500, cursor: 'pointer', overflowWrap: 'anywhere' }}
          >{target.label}</a>
        ) : (
          <span data-testid={`fn-src-${def.id}`} style={{ color: C.textSecondary, overflowWrap: 'anywhere' }}>{target.label}</span>
        )
      )}
      {target.kind === 'text' && (
        <span data-testid={`fn-src-${def.id}`} style={{ color: C.textSecondary, overflowWrap: 'anywhere' }}>{def.target}</span>
      )}
    </>
  );
}

// Маркер сноски в тексте: надстрочник accent. На десктопе — по наведению, на мобиле — по тапу;
// с клавиатуры: Enter/Space открывает, Esc закрывает. Клик и Enter «пришпиливают» поповер,
// повторный клик/Enter по маркеру его закрывает (и возвращает фокус маркеру). Уход мыши
// закрывает с задержкой: поповер сам продлевает себе жизнь по onMouseEnter — иначе кнопка
// внутри него исчезала бы раньше, чем до неё дойдёт курсор. Тач-цель ≥ 40 px на мобиле —
// прозрачные поля с отрицательными компенсаторами: область попадания растёт, строка текста
// не разъезжается.
function FootnoteMarker({ def, touchMode, subscribe, snapshot, onOpen, onClose, onHoverClose, describedBy }: {
  def: FootnoteDef; touchMode: boolean;
  // открытое состояние читается подпиской, а не пропом: карта components при открытии
  // поповера не пересобирается и остальные ссылки документа не перемонтируются
  subscribe: (cb: () => void) => () => void;
  snapshot: () => string | null;
  onOpen: (el: HTMLElement, pinned: boolean) => void;
  onClose: (refocus: boolean) => void;
  onHoverClose: () => void;
  // id открытой панели ЭТОГО экземпляра (useId): маркер связан со своей панелью, а не с
  // общим на всех id — два рендера на странице не перепутали бы описания
  describedBy: string;
}) {
  const open = useSyncExternalStore(subscribe, snapshot) === def.id;
  const display = fnDisplay(def.id);
  // 40×40 и для односимвольного маркера: 10 px текста + поля 15/17 по краям
  const hit = touchMode ? { padding: '15px 17px', margin: '-15px -17px' } : { padding: '2px 4px', margin: '-2px -4px' };
  return (
    <span
      data-testid={`fn-ref-${def.id}`}
      role="button"
      tabIndex={0}
      aria-label={`Сноска ${display}: источник и цитата`}
      aria-describedby={open ? describedBy : undefined}
      onClick={e => { e.preventDefault(); if (open) onClose(true); else onOpen(e.currentTarget, true); }}
      onMouseEnter={touchMode ? undefined : e => onOpen(e.currentTarget, false)}
      // отложенное закрытие (см. scheduleFnClose у компонента): поповер живой, пока курсор в нём
      onMouseLeave={touchMode ? undefined : onHoverClose}
      onKeyDown={e => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); if (open) onClose(true); else onOpen(e.currentTarget, true); }
        else if (e.key === 'Escape') onClose(true);
      }}
      style={{
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        verticalAlign: 'super', fontFamily: mono, fontSize: 10, fontWeight: 700, lineHeight: 1,
        color: C.accent, cursor: 'pointer', userSelect: 'none',
        background: open ? C.accentMuted : 'transparent', borderRadius: 4,
        ...hit,
      }}
    >{display}</span>
  );
}

// Embed-вставка ![[Заметка]]: рамка-цитата с заголовком-ссылкой и содержимым
function NoteEmbed({ name, resolveNote, onWikilink, embedSource }: {
  name: string;
  resolveNote: NonNullable<Props['resolveNote']>;
  onWikilink?: (target: string) => void;
  embedSource?: string;
}) {
  const [data, setData] = useState<ResolvedNote | null | 'loading'>('loading');
  useEffect(() => {
    let alive = true;
    void cachedResolve(resolveNote, name).then(d => { if (alive) setData(d); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name]);

  if (data === 'loading')
    return <span style={{ color: C.textMuted, fontSize: 12 }}>…</span>;
  if (!data)
    return (
      <a role="link" tabIndex={0} onClick={() => onWikilink?.(name)}
        style={{ color: C.textMuted, borderBottom: `1px dashed ${C.textMuted}`, cursor: 'pointer' }}>
        {name}
      </a>
    );
  return (
    <span style={{
      display: 'block', margin: '10px 0', padding: '10px 14px',
      background: C.bgCard, border: `1px solid ${C.borderLight}`,
      borderLeft: `3px solid ${C.accent}`, borderRadius: `0 ${R.lg}px ${R.lg}px 0`,
    }}>
      <a role="link" tabIndex={0} onClick={() => onWikilink?.(name)}
        style={{ color: C.accent, fontWeight: 600, fontSize: 13, cursor: 'pointer', display: 'block', marginBottom: 6 }}>
        {data.title}
      </a>
      <span style={{ display: 'block' }}>
        <MarkdownViewer
          content={(() => { const b = stripFrontmatter(data.content).body; return b.length > 1500 ? b.slice(0, 1500) + '…' : b; })()}
          onWikilink={onWikilink} embedDepth={1} embedSource={embedSource}
        />
      </span>
    </span>
  );
}

const components: Components = {
  h1: ({ children }) => (
    <h1 style={{ fontFamily: serif, fontSize: 26, fontWeight: 700, margin: '0 0 16px', color: C.textHeading, letterSpacing: '-0.02em', lineHeight: 1.25 }}>{children}</h1>
  ),
  h2: ({ children }) => (
    <h2 style={{ fontFamily: serif, fontSize: 21, fontWeight: 700, margin: '24px 0 12px', color: C.textHeading, letterSpacing: '-0.01em', lineHeight: 1.3 }}>{children}</h2>
  ),
  h3: ({ children }) => (
    <h3 style={{ fontFamily: serif, fontSize: 17, fontWeight: 700, margin: '20px 0 8px', color: C.textHeading, lineHeight: 1.35 }}>{children}</h3>
  ),
  h4: ({ children }) => (
    <h4 style={{ fontFamily: serif, fontSize: 15, fontWeight: 700, margin: '16px 0 6px', color: C.textHeading }}>{children}</h4>
  ),
  h5: ({ children }) => (
    <h5 style={{ fontFamily: serif, fontSize: 13.5, fontWeight: 700, margin: '14px 0 5px', color: C.textHeading }}>{children}</h5>
  ),
  h6: ({ children }) => (
    <h6 style={{ fontFamily: serif, fontSize: 12.5, fontWeight: 700, margin: '12px 0 4px', color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.03em' }}>{children}</h6>
  ),
  p: ({ children }) => (
    <p style={{ margin: '0 0 14px', lineHeight: 1.7, color: C.textHeading }}>{children}</p>
  ),
  a: ({ href, children }) => (
    <a href={href} style={{ color: C.accent, textDecoration: 'underline' }}>{children}</a>
  ),
  strong: ({ children }) => (
    <strong style={{ fontWeight: 700, color: C.textHeading }}>{children}</strong>
  ),
  em: ({ children }) => (
    <em style={{ fontStyle: 'italic' }}>{children}</em>
  ),
  // pre не оборачиваем — блоки кода целиком собираются в компоненте code ниже
  pre: ({ children }) => <>{children}</>,
  code: ({ children, className }) => {
    const language = /language-(\w+)/.exec(className || '')?.[1];
    const text = String(children).replace(/\n$/, '');
    if (language === 'mermaid') {
      return <MermaidDiagram code={text} />;
    }
    if (language) {
      return (
        <SyntaxHighlighter
          language={language}
          style={oneDark}
          customStyle={{ borderRadius: 10, fontSize: 12.5, margin: '0 0 16px', padding: '14px 16px', fontFamily: mono, overflowX: 'auto' }}
        >
          {text}
        </SyntaxHighlighter>
      );
    }
    // Многострочный блок без указания языка (например ASCII-схема) — моноширинный на светлом фоне
    if (text.includes('\n')) {
      return (
        <pre style={{ margin: '0 0 16px', background: C.bgInset, borderRadius: 10, padding: '14px 16px', overflowX: 'auto', fontFamily: mono, fontSize: 12.5, lineHeight: 1.6, color: C.textHeading }}>
          <code style={{ fontFamily: mono }}>{text}</code>
        </pre>
      );
    }
    return (
      <code style={{ fontFamily: mono, fontSize: 12.5, background: C.bgInset, padding: '1px 5px', borderRadius: 4, color: C.textHeading }}>
        {children}
      </code>
    );
  },
  // display: flow-root у блоков с собственным фоном, рамкой или маркерами — не украшение,
  // а условие соседства с плавающими элементами (колонка свойств и комментариев у открытого
  // документа): обычный блок обтекается только строками, а его бокс уходит ПОД флоат —
  // подложка цитаты заезжала под панель, маркеры списка прятались за ней. Своё BFC заставляет
  // блок встать рядом. У pre и таблицы оно уже есть — от overflow
  ul: ({ children }) => (
    <ul style={{ margin: '0 0 14px', paddingLeft: 22, lineHeight: 1.7, display: 'flow-root' }}>{children}</ul>
  ),
  ol: ({ children }) => (
    <ol style={{ margin: '0 0 14px', paddingLeft: 22, lineHeight: 1.7, display: 'flow-root' }}>{children}</ol>
  ),
  li: ({ children }) => (
    <li style={{ margin: '3px 0', color: C.textHeading }}>{children}</li>
  ),
  blockquote: ({ children }) => (
    <blockquote style={{ margin: '0 0 14px', padding: '10px 16px', borderLeft: `3px solid ${C.accent}`, background: C.bgMain, borderRadius: '0 8px 8px 0', color: C.textSecondary, display: 'flow-root' }}>
      {children}
    </blockquote>
  ),
  hr: () => (
    <hr style={{ margin: '20px 0', border: 'none', borderTop: `1px solid ${C.border}` }} />
  ),
  table: ({ children }) => (
    <div style={{ overflowX: 'auto', marginBottom: 14 }}>
      <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 13 }}>{children}</table>
    </div>
  ),
  th: ({ children }) => (
    <th style={{ padding: '7px 12px', background: C.bgInset, fontWeight: 700, textAlign: 'left', borderBottom: `2px solid ${C.border}`, color: C.textHeading }}>{children}</th>
  ),
  td: ({ children }) => (
    <td style={{ padding: '7px 12px', borderBottom: `1px solid ${C.border}`, color: C.textHeading }}>{children}</td>
  ),
  img: ({ src, alt }) => (
    <img src={src} alt={alt ?? ''} style={{ maxWidth: '100%', borderRadius: 8, margin: '8px 0' }} />
  ),
};

// URL картинки-вложения из vault (JWT в query — <img> не шлёт заголовки)
function attachmentUrl(path: string, source?: string): string {
  const token = readStoredToken();
  const qs = new URLSearchParams({ source: source ?? 'personal', path });
  if (token) qs.set('access_token', token);
  return `/api/notes/attachment?${qs}`;
}

const CUSTOM_SCHEMES = ['wikilink:', 'noteembed:', 'noteatt:', 'fnref:'];

interface HoverState { x: number; y: number; data: ResolvedNote }

export function MarkdownViewer({ content, blockPos, onWikilink, existingTitles, resolveNote, embedSource, embedDepth = 0, hideLeadingH1, onDocLink, resolveImageSrc, wikiFootnotes }: Props) {
  // Режим заметок: включаем рендер [[wikilinks]]/![[embeds]] и внешние ссылки синим
  // (info), чтобы три класса ссылок различались (живая accent / призрак / внешняя).
  const notesMode = onWikilink != null;
  // Режим документации проекта — только когда режим заметок НЕ включён: у notes/*.md
  // внутри проекта могли бы сойтись оба, и приоритет отдан заметкам (их ссылочная
  // модель богаче: hover-preview, embeds, призрачные ссылки).
  const docsMode = !notesMode && onDocLink != null;
  // id панели сноски этого экземпляра: маркер описывает её через aria-describedby, а не
  // общим на всех рендерами id (два MarkdownViewer на странице не перепутали бы описания)
  const fnId = useId();
  const footnoteIds = useMemo(
    () => (wikiFootnotes ? new Set(Object.keys(wikiFootnotes)) : undefined),
    [wikiFootnotes]);
  const pre = useMemo(
    () => (notesMode ? preprocessNotes(content, embedDepth, footnoteIds) : null),
    [notesMode, content, embedDepth, footnoteIds]);
  const source = pre ? pre.text : content;
  const toRaw = useMemo(() => makeToRaw(pre?.map ?? null), [pre]);

  // Hover-preview вики-ссылок (только при resolveNote, не на тач-устройствах)
  const [hover, setHover] = useState<HoverState | null>(null);
  const hoverTimer = useRef<number | null>(null);
  const hoverEnabled = resolveNote != null && embedDepth === 0 &&
    typeof window !== 'undefined' && !window.matchMedia('(hover: none)').matches;

  const cancelHover = () => {
    if (hoverTimer.current) { window.clearTimeout(hoverTimer.current); hoverTimer.current = null; }
    setHover(null);
  };
  useEffect(() => cancelHover, []);

  const scheduleHover = (target: string, el: HTMLElement) => {
    if (!hoverEnabled) return;
    if (hoverTimer.current) window.clearTimeout(hoverTimer.current);
    hoverTimer.current = window.setTimeout(() => {
      void cachedResolve(resolveNote!, target).then(data => {
        if (!data) return;
        const rect = el.getBoundingClientRect();
        const x = Math.min(rect.left, window.innerWidth - 396);
        const y = rect.bottom + 6;
        setHover({ x: Math.max(8, x), y, data });
      });
    }, 350);
  };

  // ─── Сноски: состояния и переходы (S5e, доработка по ревью 684d71c0) ───
  // Данные — из карты вызывающего (Props.wikiFootnotes): компонент сам их из текста не
  // выводит. Без карты сносок нет вовсе: свои [^id] целиком ведёт remark-gfm. Ни котировка,
  // ни цель в DOM как HTML не попадают — FootnoteBody рендерит их текстовыми узлами.
  const footnoteDefs = useMemo(() => {
    const fm = new Map<string, FootnoteDef>();
    if (wikiFootnotes) for (const [id, d] of Object.entries(wikiFootnotes)) fm.set(id, { id, target: d.source, quote: d.quote });
    return fm;
  }, [wikiFootnotes]);
  const touchMode = typeof window !== 'undefined' && window.matchMedia('(hover: none)').matches;
  // Состояние поповера — в ref с подпиской (как стор): маркер читает его через
  // useSyncExternalStore, а не через проп — map components при открытии не пересобирается,
  // и весь массив ссылок документа не перемонтируется. setFnTick будит сам рендер
  // (портал поповера/шторки).
  const fnStateRef = useRef<{ id: string; x: number; y: number; pinned: boolean } | null>(null);
  const [fnTick, setFnTick] = useState(0);
  const fnSubsRef = useRef<Set<() => void>>(new Set());
  const fnTriggerRef = useRef<HTMLElement | null>(null);
  // DOM открытой панели (поповер/шторка) — цель переноса фокуса при открытии
  const fnPanelRef = useRef<HTMLDivElement | null>(null);
  // маркер, которому вернуть фокус при закрытии (после комита рендера, см. эффекты ниже)
  const closeRef = useRef<HTMLElement | null>(null);
  const notifyFn = () => { for (const cb of [...fnSubsRef.current]) cb(); };
  const subscribeFn = useCallback((cb: () => void) => {
    fnSubsRef.current.add(cb);
    return () => { fnSubsRef.current.delete(cb); };
  }, []);
  const fnSnapshot = useCallback(() => fnStateRef.current?.id ?? null, []);

  // refocus=true — закрытие с клавиатуры (Esc) или повторным кликом/Enter по маркеру:
  // фокус возвращается маркеру, а не теряется; с затемнения/мыши — не крадём ничей
  const closeFootnote = (refocus: boolean) => {
    if (!fnStateRef.current) return;
    fnStateRef.current = null;
    setFnTick(t => t + 1);
    notifyFn();
    closeRef.current = refocus ? fnTriggerRef.current : null;
  };
  // Уход мыши закрывает только непришпиленный поповер (открытый наведением)
  const hoverCloseFootnote = () => {
    if (fnStateRef.current && !fnStateRef.current.pinned) closeFootnote(false);
  };
  // Закрытие по наведению — с задержкой: уход с маркера ставит таймер, вход в поповер его
  // снимает (панель сама продлевает себе жизнь): иначе кнопка «Открыть в чате» внутри поповера
  // исчезала бы раньше, чем до неё дойдёт курсор
  const fnTimerRef = useRef<number | null>(null);
  const clearFnTimer = () => { if (fnTimerRef.current != null) { window.clearTimeout(fnTimerRef.current); fnTimerRef.current = null; } };
  const scheduleFnClose = () => {
    clearFnTimer();
    fnTimerRef.current = window.setTimeout(() => { fnTimerRef.current = null; hoverCloseFootnote(); }, 250);
  };
  useEffect(() => clearFnTimer, []);

  const openFootnote = (id: string, el: HTMLElement, pinned: boolean) => {
    const rect = el.getBoundingClientRect();
    const vw = typeof window.innerWidth === 'number' ? window.innerWidth : 0;
    clearFnTimer();
    fnTriggerRef.current = el;
    fnStateRef.current = { id, x: Math.max(8, Math.min(rect.left, vw - 330)), y: rect.bottom + 6, pinned };
    setFnTick(t => t + 1);
    notifyFn();
  };
  // Esc — с любой точки экрана (и для мобильной шторки тоже)
  useEffect(() => {
    if (!fnStateRef.current) return;
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') closeFootnote(true); };
    document.addEventListener('keydown', h);
    return () => document.removeEventListener('keydown', h);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fnTick]);

  // Фокус за состоянием (содержимое панели интерактивно — кнопка чата/задачи/заметки):
  // при открытии кликом/Enter — внутрь панели; при закрытии с возвратом — маркеру.
  // Открытие наведением фокус не переносит: мышь не должна двигать клавиатурный фокус.
  // rAF: при смене состояния панель может ещё не быть в DOM (портал) — фокус после комита.
  // Нет rAF (node-тесты) — синхронно: там рендер к моменту эффекта уже завершён.
  const afterPaint = (cb: () => void) =>
    typeof requestAnimationFrame === 'function' ? requestAnimationFrame(cb) : (cb(), 0);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!fnStateRef.current || !fnStateRef.current.pinned) return;
    const h = afterPaint(() => fnPanelRef.current?.focus());
    return () => { if (typeof cancelAnimationFrame === 'function') cancelAnimationFrame(h); };
  }, [fnTick]);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (fnStateRef.current || !closeRef.current) return;
    const el = closeRef.current;
    closeRef.current = null;
    const h = afterPaint(() => el.focus());
    return () => { if (typeof cancelAnimationFrame === 'function') cancelAnimationFrame(h); };
  }, [fnTick]);

  // Клавиатура внутри панели: Esc закрывает (фокус — обратно маркеру). На модальной шторке
  // Tab не уходит за её пределы (focus-trap): с последнего элемента — к первому, с первого
  // с Shift — к последнему; фокусируемых нет — фокус держим на самой панели.
  const onPanelKey = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Escape') { closeFootnote(true); return; }
    if (e.key !== 'Tab' || !touchMode) return;
    const panel = fnPanelRef.current;
    if (!panel) return;
    const nodes = Array.from(panel.querySelectorAll('a,button,input,select,textarea,[tabindex]'));
    if (nodes.length === 0) { e.preventDefault(); panel.focus(); return; }
    const first = nodes[0] as HTMLElement;
    const last = nodes[nodes.length - 1] as HTMLElement;
    const active = document.activeElement as HTMLElement | null;
    if (e.shiftKey ? active === first || active === panel : active === last) {
      e.preventDefault();
      (e.shiftKey ? last : first).focus();
    }
  };

  const merged: Components = notesMode
    ? {
        ...components,
        a: ({ href, children }) => {
          if (href?.startsWith('fnref:')) {
            const id = decodeScheme(href.slice('fnref:'.length));
            const def = footnoteDefs.get(id);
            // Страховка: в fnref препроцессинг несёт только id из карты вызывающего —
            // без записи в карте [^id] остаётся литеральным и до этой ветки не доходит
            if (!def) return <span>{`[^${id}]`}</span>;
            return (
              <FootnoteMarker
                def={def}
                touchMode={touchMode}
                subscribe={subscribeFn}
                snapshot={fnSnapshot}
                onOpen={(el, pinned) => openFootnote(id, el, pinned)}
                onClose={closeFootnote}
                onHoverClose={scheduleFnClose}
                describedBy={fnId}
              />
            );
          }
          if (href?.startsWith('noteembed:') && resolveNote) {
            const name = decodeScheme(href.slice('noteembed:'.length));
            return <NoteEmbed name={name} resolveNote={resolveNote} onWikilink={onWikilink} embedSource={embedSource} />;
          }
          if (href?.startsWith('wikilink:') || href?.startsWith('noteembed:')) {
            const scheme = href.startsWith('wikilink:') ? 'wikilink:' : 'noteembed:';
            const target = decodeScheme(href.slice(scheme.length));
            const live = existingTitles ? existingTitles.has(normTarget(target)) : true;
            return (
              <a
                role="link"
                tabIndex={0}
                onClick={e => { e.preventDefault(); cancelHover(); onWikilink?.(target); }}
                onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onWikilink?.(target); } }}
                onMouseEnter={live ? e => scheduleHover(target, e.currentTarget) : undefined}
                onMouseLeave={cancelHover}
                title={live ? undefined : `Создать заметку «${target}»`}
                style={{
                  color: live ? C.accent : C.textMuted,
                  textDecoration: 'none',
                  cursor: 'pointer',
                  fontWeight: 500,
                  borderBottom: live ? 'none' : `1px dashed ${C.textMuted}`,
                }}
              >{children}</a>
            );
          }
          // Внешняя ссылка в заметке — синим (info), чтобы отличать от вики-ссылок
          return <a href={href} target="_blank" rel="noopener noreferrer" style={{ color: C.info, textDecoration: 'underline' }}>{children}</a>;
        },
        img: ({ src, alt }) => {
          const url = typeof src === 'string' && src.startsWith('noteatt:')
            ? attachmentUrl(decodeScheme(src.slice('noteatt:'.length)), embedSource)
            : src;
          return <img src={url} alt={alt ?? ''} style={{ maxWidth: '100%', borderRadius: 8, margin: '8px 0' }} />;
        },
      }
    : docsMode
    ? {
        ...components,
        a: ({ href, children }) => {
          // Внешняя ссылка — в новую вкладку: дефолтный рендер увёл бы из приложения
          // в текущей, а в репозиторных доках http-ссылок много
          if (!href || /^(https?:\/\/|mailto:|\/\/)/i.test(href))
            return <a href={href} target="_blank" rel="noopener noreferrer" style={{ color: C.info, textDecoration: 'underline' }}>{children}</a>;
          return (
            <a
              href={href}
              onClick={e => { e.preventDefault(); onDocLink!(href); }}
              style={{ color: C.accent, textDecoration: 'underline', cursor: 'pointer' }}
            >{children}</a>
          );
        },
      }
    : components;

  // hideLeadingH1: глушим ТОЛЬКО H1-узел с позицией 0 (самое начало документа) —
  // это дубль заголовка, который рендерит hero-шапка; заголовки ниже по тексту живут
  const withHidden: Components = hideLeadingH1
    ? {
        ...merged,
        h1: props => {
          const node = (props as { node?: { position?: { start?: { offset?: number } } } }).node;
          if (node?.position?.start?.offset === 0) return null;
          return (merged.h1 as (p: unknown) => ReactNode)(props);
        },
      }
    : merged;

  // Картинки документа лежат в репозитории: путь в src относителен документа, а не адреса
  // страницы. Слой поверх режимов, а не внутри docs-ветки: в центральной области документ
  // рендерит DocComments — там режима доков нет, а картинки те же
  const withImages: Components = resolveImageSrc
    ? {
        ...withHidden,
        img: ({ src, alt, width, height }) => (
          <img
            src={(src && resolveImageSrc(src)) ?? src}
            alt={alt ?? ''}
            width={width}
            height={height}
            style={{ maxWidth: '100%', height: 'auto', borderRadius: 8, margin: '8px 0' }}
          />
        ),
      }
    : withHidden;

  // рендер поповера читает текущий ref (пробуждение — setFnTick)
  const fnOpen = fnStateRef.current;

  const finalComponents = useMemo(
    () => (blockPos ? withBlockPos(withImages, toRaw) : withImages),
    // merged пересобирается каждый рендер — зависимость от стабильных первопричин.
    // onDocLink в списке обязателен: панель «Документы» пересоздаёт колбэк при смене
    // открытого документа, и без него в components остался бы устаревший переход.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [blockPos, toRaw, notesMode, docsMode, onDocLink, resolveImageSrc, existingTitles, resolveNote, embedSource, hideLeadingH1]);

  return (
    <div style={{ fontFamily: FONT.sans, fontSize: 14, lineHeight: 1.7, color: C.textHeading, width: '100%' }}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        // Порядок обязателен: raw превращает HTML-строки в узлы, sanitize следом их чистит.
        // Поменять местами — значит пропустить сырой HTML мимо белого списка
        rehypePlugins={[rehypeRaw, [rehypeSanitize, HTML_SCHEMA]]}
        urlTransform={url => (CUSTOM_SCHEMES.some(s => url.startsWith(s)) ? url : defaultUrlTransform(url))}
        components={finalComponents}
      >{source}</ReactMarkdown>
      {/* «Источники» — блок из карты вызывающего (ADR-035, решение 3: рендерит его UI):
          без wikiFootnotes блока нет — обычные GFM-сноски остаются в секции remark-gfm. */}
      {notesMode && footnoteDefs.size > 0 && (
        <section data-testid="md-sources" style={{ marginTop: 24 }}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
            <h3 style={{ margin: 0, fontFamily: serif, fontSize: 15, fontWeight: 700, color: C.textHeading }}>Источники</h3>
            <span style={{ fontFamily: mono, fontSize: 10, color: C.textMuted }}>{footnoteDefs.size}</span>
          </div>
          {[...footnoteDefs.values()].map(d => (
            <div key={d.id} data-testid={`fn-def-${d.id}`} style={{ display: 'flex', gap: 10, padding: '10px 12px', marginTop: 8, border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard }}>
              <span style={{ fontFamily: mono, fontSize: 10, color: C.textMuted, minWidth: 24, flexShrink: 0, paddingTop: 2 }}>{fnDisplay(d.id)}</span>
              <div style={{ minWidth: 0, flex: 1 }}>
                <FootnoteBody def={d} onOpenNote={onWikilink} />
              </div>
            </div>
          ))}
        </section>
      )}
      {hover && createPortal(
        <div style={{
          position: 'fixed', left: hover.x, top: hover.y, zIndex: Z.modal,
          width: 380, maxHeight: 280, overflow: 'hidden', pointerEvents: 'none',
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: '12px 14px',
        }}>
          <div style={{ fontFamily: serif, fontSize: 15, fontWeight: 700, color: C.textHeading, marginBottom: 6 }}>
            {hover.data.title}
          </div>
          <MarkdownViewer
            content={(() => { const b = stripFrontmatter(hover.data.content).body; return b.length > 900 ? b.slice(0, 900) + '…' : b; })()}
            onWikilink={() => {}} embedDepth={1} embedSource={embedSource}
          />
        </div>,
        document.body,
      )}
      {/* Поповер сноски: на десктопе — карточка у маркера, на мобиле — шторка с затемнением
          (тап вне закрывает, Esc тоже). Обе панели — role=dialog: содержимое интерактивно
          (кнопка чата/задачи/ссылка заметки), tooltip для этого не годится. Панели
          фокусируемы (tabIndex=-1): при открытии кликом/Enter фокус переносится внутрь,
          Esc закрывает и возвращает его маркеру; на модальной шторке ещё focus-trap — Tab
          не уходит за её пределы. aria-describedby маркера — id этой панели (useId). */}
      {fnOpen && footnoteDefs.has(fnOpen.id) && createPortal(
        touchMode ? (
          <>
            <div data-testid="fn-scrim" onClick={() => closeFootnote(false)} style={{ position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal }} />
            <div ref={fnPanelRef} id={fnId} data-testid="fn-sheet" role="dialog" aria-modal="true" aria-label={`Сноска ${fnDisplay(fnOpen.id)}`} tabIndex={-1} onKeyDown={onPanelKey} style={{ position: 'fixed', left: 0, right: 0, bottom: 0, zIndex: Z.modal, background: C.bgWhite, borderRadius: `${R.xl}px ${R.xl}px 0 0`, boxShadow: SHADOW.sheet, padding: '8px 16px 20px' }}>
              <div style={{ width: 36, height: 4, borderRadius: 999, background: C.borderLight, margin: '0 auto 12px' }} aria-hidden />
              <div style={{ fontFamily: mono, fontSize: 10, color: C.textMuted, marginBottom: 8 }}>Сноска {fnDisplay(fnOpen.id)}</div>
              <FootnoteBody def={footnoteDefs.get(fnOpen.id)!} onOpenNote={onWikilink} />
            </div>
          </>
        ) : (
          <div ref={fnPanelRef} id={fnId} data-testid="fn-popover" role="dialog" aria-label={`Сноска ${fnDisplay(fnOpen.id)}`} tabIndex={-1} onMouseEnter={clearFnTimer} onMouseLeave={scheduleFnClose} onKeyDown={onPanelKey} style={{ position: 'fixed', left: `${fnOpen.x}px`, top: `${fnOpen.y}px`, zIndex: Z.modal, width: 320, maxHeight: 340, overflow: 'auto', background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl, boxShadow: SHADOW.dropdown, padding: '12px 14px' }}>
            <FootnoteBody def={footnoteDefs.get(fnOpen.id)!} onOpenNote={onWikilink} />
          </div>
        ),
        document.body,
      )}
    </div>
  );
}
