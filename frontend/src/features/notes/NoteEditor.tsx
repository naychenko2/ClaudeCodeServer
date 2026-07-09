import { useEffect, useMemo, useRef, useState } from 'react';
import { EditorView, keymap, drawSelection, dropCursor, placeholder as cmPlaceholder,
  Decoration, ViewPlugin, WidgetType, type DecorationSet, type ViewUpdate } from '@codemirror/view';
import { EditorState, RangeSetBuilder } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands';
import { syntaxHighlighting, HighlightStyle, syntaxTree, indentOnInput } from '@codemirror/language';
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap,
  type CompletionContext, type CompletionResult } from '@codemirror/autocomplete';
import { markdown } from '@codemirror/lang-markdown';
import { tags as t } from '@lezer/highlight';
import { C, FONT } from '../../lib/design';
import { getEffectiveTheme, subscribeThemeMode } from '../../lib/themeMode';
import { useNotes } from '../../lib/notes';

// Редактор заметок на CodeMirror 6 с Obsidian-подобным Live Preview:
// - маркеры **жирного**/*курсива*/`кода`/# заголовков скрываются, когда каретка
//   не на их строке; текст при этом стилизован
// - [[wikilinks]] подсвечены accent (скобки прячутся), Ctrl/Cmd+клик — переход
// - #теги — чипы; чекбоксы «- [ ]» интерактивные
// - автокомплит [[ по заголовкам заметок и # по существующим тегам

interface Props {
  value: string;
  onChange: (v: string) => void;
  minHeight?: number;
  placeholder?: string;
  onWikilink?: (target: string) => void;
}

// --- Подсветка markdown-синтаксиса (сам текст, не маркеры) ---

function makeHighlight(dark: boolean): HighlightStyle {
  const heading = { fontFamily: FONT.serif, fontWeight: 'bold', color: C.textHeading } as const;
  return HighlightStyle.define([
    { tag: t.heading1, ...heading, fontSize: '1.6em' },
    { tag: t.heading2, ...heading, fontSize: '1.35em' },
    { tag: t.heading3, ...heading, fontSize: '1.18em' },
    { tag: t.heading4, ...heading, fontSize: '1.05em' },
    { tag: t.heading5, ...heading },
    { tag: t.heading6, ...heading },
    { tag: t.strong, fontWeight: 'bold' },
    { tag: t.emphasis, fontStyle: 'italic' },
    { tag: t.strikethrough, textDecoration: 'line-through' },
    { tag: t.monospace, fontFamily: FONT.mono, fontSize: '0.9em' },
    { tag: t.link, color: C.info },
    { tag: t.url, color: dark ? '#8A8072' : '#9A8F7E' },
    { tag: t.quote, color: C.textSecondary, fontStyle: 'italic' },
    { tag: t.meta, color: dark ? '#8A8072' : '#9A8F7E' },
  ]);
}

// --- Live Preview: скрытие маркеров + wikilinks + теги + чекбоксы ---

const WIKI_RE = /(!?)\[\[([^\][]+)\]\]/g;
const TAG_RE = /(?<=^|\s)#([\p{L}\p{N}_][\p{L}\p{N}_/-]*)/gu;

class CheckboxWidget extends WidgetType {
  private readonly checked: boolean;
  private readonly pos: number;
  constructor(checked: boolean, pos: number) { super(); this.checked = checked; this.pos = pos; }
  override eq(other: CheckboxWidget) { return other.checked === this.checked && other.pos === this.pos; }
  toDOM(view: EditorView): HTMLElement {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = this.checked;
    box.className = 'cm-note-checkbox';
    box.onmousedown = e => e.preventDefault();
    box.onclick = e => {
      e.preventDefault();
      view.dispatch({
        changes: { from: this.pos, to: this.pos + 3, insert: this.checked ? '[ ]' : '[x]' },
      });
    };
    return box;
  }
  override ignoreEvent() { return false; }
}

// Пересечение диапазона со строками, где стоит каретка/выделение
function selectionTouches(state: EditorState, from: number, to: number): boolean {
  for (const r of state.selection.ranges) {
    const a = state.doc.lineAt(r.from).from;
    const b = state.doc.lineAt(r.to).to;
    if (to >= a && from <= b) return true;
  }
  return false;
}

function buildLivePreview(view: EditorView): DecorationSet {
  const { state } = view;
  const decos: { from: number; to: number; deco: Decoration }[] = [];

  // 1) Маркеры markdown из синтакс-дерева — прячем вне активной строки
  for (const { from, to } of view.visibleRanges) {
    syntaxTree(state).iterate({
      from, to,
      enter: node => {
        const name = node.name;
        if (name === 'HeaderMark' || name === 'EmphasisMark' || name === 'CodeMark' || name === 'StrikethroughMark') {
          if (selectionTouches(state, node.from, node.to)) return;
          // у заголовка съедаем и пробел после решёток
          const ext = name === 'HeaderMark' && state.sliceDoc(node.to, node.to + 1) === ' ' ? 1 : 0;
          decos.push({ from: node.from, to: node.to + ext, deco: Decoration.replace({}) });
        } else if (name === 'TaskMarker') {
          // «- [ ]» → интерактивный чекбокс (вне активной строки)
          if (selectionTouches(state, node.from, node.to)) return;
          const checked = state.sliceDoc(node.from, node.to).toLowerCase().includes('x');
          decos.push({ from: node.from, to: node.to, deco: Decoration.replace({ widget: new CheckboxWidget(checked, node.from) }) });
        }
      },
    });

    // 2) Wikilinks и теги — regex по видимому тексту (lezer их не знает)
    const text = state.sliceDoc(from, to);
    for (const m of text.matchAll(WIKI_RE)) {
      const start = from + m.index!;
      const end = start + m[0].length;
      const active = selectionTouches(state, start, end);
      const openLen = m[1] ? 3 : 2;   // "![[" или "[["
      if (!active) {
        decos.push({ from: start, to: start + openLen, deco: Decoration.replace({}) });
        decos.push({ from: end - 2, to: end, deco: Decoration.replace({}) });
      }
      decos.push({
        from: start + (active ? 0 : openLen),
        to: active ? end : end - 2,
        deco: Decoration.mark({ class: 'cm-wikilink', attributes: { 'data-target': m[2] } }),
      });
    }
    for (const m of text.matchAll(TAG_RE)) {
      const start = from + m.index!;
      decos.push({ from: start, to: start + m[0].length, deco: Decoration.mark({ class: 'cm-notetag' }) });
    }
  }

  decos.sort((a, b) => a.from - b.from || a.to - b.to);
  const builder = new RangeSetBuilder<Decoration>();
  let last = -1;
  for (const d of decos) {
    if (d.from < last) continue;   // пересечения отбрасываем (регресс лучше падения)
    builder.add(d.from, d.to, d.deco);
    if (d.to > last) last = d.from === d.to ? d.to : d.to;
  }
  return builder.finish();
}

const livePreview = ViewPlugin.fromClass(class {
  decorations: DecorationSet;
  constructor(view: EditorView) { this.decorations = buildLivePreview(view); }
  update(u: ViewUpdate) {
    if (u.docChanged || u.selectionSet || u.viewportChanged)
      this.decorations = buildLivePreview(u.view);
  }
}, { decorations: v => v.decorations });

// --- Тема редактора (токены дизайн-системы, обе темы) ---

function makeTheme(dark: boolean) {
  return EditorView.theme({
    '&': { fontSize: '14px', backgroundColor: 'transparent' },
    '.cm-scroller': { fontFamily: FONT.sans, lineHeight: 1.7 },
    '.cm-content': { caretColor: C.accent, padding: '10px 2px' },
    '&.cm-focused': { outline: 'none' },
    '.cm-cursor': { borderLeft: `2px solid ${C.accent}` },
    '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': {
      backgroundColor: dark ? 'rgba(227,138,106,0.28)' : 'rgba(217,119,87,0.20)',
    },
    '.cm-activeLine': { backgroundColor: dark ? 'rgba(227,138,106,0.05)' : 'rgba(217,119,87,0.05)' },
    '.cm-wikilink': { color: C.accent, fontWeight: '500', cursor: 'pointer' },
    '.cm-notetag': {
      color: C.accent, backgroundColor: C.accentLight,
      borderRadius: '4px', padding: '0 4px', fontSize: '0.88em', fontWeight: '500',
    },
    '.cm-note-checkbox': { accentColor: C.accent, margin: '0 2px', cursor: 'pointer' },
    '.cm-tooltip-autocomplete': {
      backgroundColor: C.bgCard, border: `1px solid ${C.border}`, borderRadius: '10px',
      fontFamily: FONT.sans,
    },
    '.cm-tooltip-autocomplete > ul > li[aria-selected]': {
      backgroundColor: C.accent, color: C.onAccent,
    },
  }, { dark });
}

// --- Автокомплит [[заметка]] и #тег ---

function wikiCompletion(titles: () => string[]) {
  return (ctx: CompletionContext): CompletionResult | null => {
    const m = ctx.matchBefore(/\[\[[^\][]*$/);
    if (!m) return null;
    const after = ctx.state.sliceDoc(ctx.pos, ctx.pos + 2);
    const closing = after === ']]' ? '' : ']]';
    return {
      from: m.from + 2,
      options: titles().map(title => ({
        label: title,
        type: 'text',
        apply: title + closing,
      })),
      validFor: /^[^\][]*$/,
    };
  };
}

function tagCompletion(tags: () => string[]) {
  return (ctx: CompletionContext): CompletionResult | null => {
    const m = ctx.matchBefore(/(?:^|\s)#[\p{L}\p{N}_/-]*$/u);
    if (!m) return null;
    const hashAt = ctx.state.sliceDoc(m.from, m.from + 1) === '#' ? m.from : m.from + 1;
    return {
      from: hashAt + 1,
      options: tags().map(tag => ({ label: tag, type: 'keyword' })),
      validFor: /^[\p{L}\p{N}_/-]*$/u,
    };
  };
}

// Автопара [[ → ]]. Учитываем ]-скобку, которую closeBrackets уже вставил за первой [:
//   '[|]' + '[' → '[[|]]' (дописываем одну ]), '[|' + '[' → '[[|]]', '[[|]]' — не дублируем.
const wikiAutoClose = EditorView.inputHandler.of((view, from, _to, text) => {
  if (text !== '[') return false;
  if (view.state.sliceDoc(from - 1, from) !== '[') return false;
  const next2 = view.state.sliceDoc(from, from + 2);
  if (next2 === ']]') return false;                       // пара уже полная
  const insert = next2.startsWith(']') ? '[]' : '[]]';    // ] от closeBrackets достраиваем до ]]
  view.dispatch({
    changes: { from, insert },
    selection: { anchor: from + 1 },
  });
  return true;
});

// --- Тулбар (те же действия, что у MarkdownEditor задач, через dispatch) ---

type TbAction =
  | { label: string; hint: string; wrap: [string, string]; sample: string }
  | { label: string; hint: string; linePrefix: string };

const TOOLBAR: TbAction[] = [
  { label: 'H', hint: 'Заголовок', linePrefix: '### ' },
  { label: 'Ж', hint: 'Жирный', wrap: ['**', '**'], sample: 'текст' },
  { label: 'К', hint: 'Курсив', wrap: ['*', '*'], sample: 'текст' },
  { label: '•', hint: 'Список', linePrefix: '- ' },
  { label: '☑', hint: 'Чек-лист', linePrefix: '- [ ] ' },
  { label: '❝', hint: 'Цитата', linePrefix: '> ' },
  { label: '</>', hint: 'Код', wrap: ['`', '`'], sample: 'код' },
  { label: '[[', hint: 'Связь с заметкой', wrap: ['[[', ']]'], sample: 'Заметка' },
];

function applyToolbar(view: EditorView, action: TbAction) {
  const { from, to } = view.state.selection.main;
  if ('wrap' in action) {
    const selected = view.state.sliceDoc(from, to) || action.sample;
    const [b, a] = action.wrap;
    view.dispatch({
      changes: { from, to, insert: b + selected + a },
      selection: { anchor: from + b.length, head: from + b.length + selected.length },
    });
  } else {
    const startLine = view.state.doc.lineAt(from);
    const endLine = view.state.doc.lineAt(to);
    const changes = [];
    for (let n = startLine.number; n <= endLine.number; n++) {
      const line = view.state.doc.line(n);
      changes.push(line.text.startsWith(action.linePrefix)
        ? { from: line.from, to: line.from + action.linePrefix.length, insert: '' }
        : { from: line.from, insert: action.linePrefix });
    }
    view.dispatch({ changes });
  }
  view.focus();
}

// --- Компонент ---

export function NoteEditor({ value, onChange, minHeight = 280, placeholder, onWikilink }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<EditorView | null>(null);
  const [theme, setTheme] = useState<'light' | 'dark'>(() => getEffectiveTheme());
  useEffect(() => subscribeThemeMode(() => setTheme(getEffectiveTheme())), []);
  const [focused, setFocused] = useState(false);

  const notes = useNotes();
  // Свежие данные автокомплита без пересоздания редактора
  const titlesRef = useRef<string[]>([]);
  const tagsRef = useRef<string[]>([]);
  useMemo(() => {
    titlesRef.current = notes.map(n => n.title);
    tagsRef.current = [...new Set(notes.flatMap(n => n.tags))].sort();
  }, [notes]);

  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  const onWikilinkRef = useRef(onWikilink);
  onWikilinkRef.current = onWikilink;

  useEffect(() => {
    if (!containerRef.current) return;
    const dark = theme === 'dark';

    const state = EditorState.create({
      doc: viewRef.current ? viewRef.current.state.doc.toString() : value,
      extensions: [
        history(),
        drawSelection(),
        dropCursor(),
        indentOnInput(),
        EditorView.lineWrapping,
        markdown(),
        syntaxHighlighting(makeHighlight(dark)),
        livePreview,
        wikiAutoClose,
        closeBrackets(),
        autocompletion({ override: [wikiCompletion(() => titlesRef.current), tagCompletion(() => tagsRef.current)] }),
        keymap.of([...closeBracketsKeymap, ...completionKeymap, ...defaultKeymap, ...historyKeymap]),
        ...(placeholder ? [cmPlaceholder(placeholder)] : []),
        makeTheme(dark),
        EditorView.updateListener.of(u => {
          if (u.docChanged) onChangeRef.current(u.state.doc.toString());
          if (u.focusChanged) setFocused(u.view.hasFocus);
        }),
        // Ctrl/Cmd+клик по вики-ссылке — переход к заметке
        EditorView.domEventHandlers({
          mousedown: (e, _view) => {
            if (!(e.ctrlKey || e.metaKey)) return false;
            const el = (e.target as HTMLElement).closest?.('.cm-wikilink') as HTMLElement | null;
            if (!el?.dataset.target) return false;
            e.preventDefault();
            onWikilinkRef.current?.(el.dataset.target.split('|')[0].trim());
            return true;
          },
        }),
      ],
    });

    const view = new EditorView({ state, parent: containerRef.current });
    viewRef.current = view;
    return () => { view.destroy(); viewRef.current = null; };
    // Пересоздание только при смене темы (как CodeEditor); контент переносится
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [theme]);

  return (
    <div style={{
      border: `1px solid ${focused ? C.accent : C.border}`,
      borderRadius: 10, background: C.bgWhite, overflow: 'hidden',
      boxShadow: focused ? `0 0 0 3px ${C.accentLight}` : 'none',
      transition: 'border-color .12s, box-shadow .12s',
    }}>
      <div style={{
        display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap',
        padding: '5px 8px', borderBottom: `1px solid ${C.borderLight}`, background: C.bgPanel,
      }}>
        {TOOLBAR.map(a => (
          <button key={a.hint} title={a.hint}
            onMouseDown={e => { e.preventDefault(); if (viewRef.current) applyToolbar(viewRef.current, a); }}
            style={{
              width: 28, height: 26, border: 'none', background: 'transparent', cursor: 'pointer',
              borderRadius: 6, color: C.textSecondary, fontSize: 12.5, fontWeight: 600, fontFamily: FONT.sans,
            }}>{a.label}</button>
        ))}
        <span style={{ marginLeft: 'auto', fontSize: 10, color: C.textMuted, fontFamily: FONT.mono }}>
          live preview · Ctrl+клик по [[ссылке]] — переход
        </span>
      </div>
      <div ref={containerRef} style={{ minHeight, maxHeight: '60vh', overflowY: 'auto', padding: '0 10px' }} />
    </div>
  );
}
