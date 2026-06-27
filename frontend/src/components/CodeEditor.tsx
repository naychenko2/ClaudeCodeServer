import { useEffect, useRef } from 'react';
import { EditorView, keymap, lineNumbers, highlightActiveLineGutter,
  highlightSpecialChars, drawSelection, dropCursor,
  rectangularSelection, crosshairCursor, highlightActiveLine } from '@codemirror/view';
import { EditorState } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { indentOnInput, syntaxHighlighting, defaultHighlightStyle,
  bracketMatching, foldGutter, foldKeymap, StreamLanguage } from '@codemirror/language';
import { closeBrackets, autocompletion,
  closeBracketsKeymap, completionKeymap } from '@codemirror/autocomplete';
import { search, searchKeymap, highlightSelectionMatches } from '@codemirror/search';
import { javascript } from '@codemirror/lang-javascript';
import { css } from '@codemirror/lang-css';
import { json } from '@codemirror/lang-json';
import { python } from '@codemirror/lang-python';
import { rust } from '@codemirror/lang-rust';
import { java } from '@codemirror/lang-java';
import { sql } from '@codemirror/lang-sql';
import { html } from '@codemirror/lang-html';
import { markdown } from '@codemirror/lang-markdown';
import { cpp } from '@codemirror/lang-cpp';
import { yaml } from '@codemirror/lang-yaml';
import { go } from '@codemirror/legacy-modes/mode/go';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { shell } from '@codemirror/legacy-modes/mode/shell';
import type { Extension } from '@codemirror/state';
import { FONT, C } from '../lib/design';

function getEditorLanguage(filePath: string): Extension | null {
  const ext = filePath.split('.').pop()?.toLowerCase() ?? '';
  switch (ext) {
    case 'js':    return javascript();
    case 'jsx':   return javascript({ jsx: true });
    case 'ts':    return javascript({ typescript: true });
    case 'tsx':   return javascript({ jsx: true, typescript: true });
    case 'css':
    case 'scss':
    case 'sass':  return css();
    case 'json':
    case 'jsonc': return json();
    case 'py':    return python();
    case 'rs':    return rust();
    case 'java':  return java();
    case 'sql':   return sql();
    case 'html':
    case 'htm':   return html();
    case 'md':
    case 'mdx':   return markdown();
    case 'cpp':
    case 'cxx':
    case 'cc':
    case 'c':
    case 'h':
    case 'hpp':   return cpp();
    case 'yaml':
    case 'yml':   return yaml();
    case 'go':    return StreamLanguage.define(go);
    case 'cs':    return StreamLanguage.define(csharp);
    case 'sh':
    case 'bash':
    case 'zsh':   return StreamLanguage.define(shell);
    default:      return null;
  }
}

const editorTheme = EditorView.theme({
  '&': { height: '100%' },
  '.cm-scroller': {
    overflow: 'auto',
    fontFamily: FONT.mono,
    fontSize: '13px',
    lineHeight: '1.6',
  },
  '.cm-content': { padding: '8px 0', caretColor: C.accent },
  '.cm-line': { padding: '0 16px 0 0' },
  '.cm-gutters': {
    background: C.bgMain,
    borderRight: `1px solid ${C.border}`,
    color: '#C4BBA9',
    userSelect: 'none',
  },
  '.cm-lineNumbers .cm-gutterElement': {
    paddingLeft: '6px',
    paddingRight: '10px',
    minWidth: '2.6em',
    textAlign: 'right',
  },
  '.cm-foldGutter .cm-gutterElement': { paddingLeft: '4px' },
  '.cm-activeLineGutter': { background: C.bgPanel },
  '.cm-activeLine': { background: 'rgba(217,119,87,0.04)' },
  '&.cm-focused .cm-selectionBackground, .cm-selectionBackground': {
    background: 'rgba(217,119,87,0.20) !important',
  },
  '.cm-cursor, .cm-dropCursor': {
    borderLeftColor: C.accent,
    borderLeftWidth: '2px',
  },
  '.cm-searchMatch': {
    background: 'rgba(217,119,87,0.22)',
    outline: '1px solid rgba(217,119,87,0.4)',
    borderRadius: '2px',
  },
  '.cm-searchMatch.cm-searchMatch-selected': {
    background: 'rgba(217,119,87,0.45)',
  },
  '.cm-foldPlaceholder': {
    background: C.bgPanel,
    border: `1px solid ${C.border}`,
    color: C.textMuted,
    borderRadius: '3px',
    padding: '0 6px',
  },
  '.cm-panels': { background: C.bgPanel },
  '.cm-panels-top': { borderBottom: `1px solid ${C.border}` },
  '.cm-search': { padding: '6px 10px', display: 'flex', gap: '4px', alignItems: 'center', flexWrap: 'wrap' },
  '.cm-textfield': {
    background: C.bgCard,
    border: `1px solid ${C.border}`,
    borderRadius: '4px',
    color: C.textPrimary,
    fontFamily: FONT.mono,
    fontSize: '12px',
    padding: '3px 8px',
    outline: 'none',
  },
  '.cm-textfield:focus': { borderColor: C.accent },
  '.cm-button': {
    background: C.bgPanel,
    border: `1px solid ${C.border}`,
    borderRadius: '4px',
    color: C.textPrimary,
    fontSize: '12px',
    padding: '3px 8px',
    cursor: 'pointer',
  },
  '.cm-button:hover': { background: C.border },
  '.cm-tooltip': {
    border: `1px solid ${C.border}`,
    background: C.bgCard,
    borderRadius: '6px',
    boxShadow: '0 4px 12px rgba(60,50,35,0.10)',
    fontSize: '12px',
  },
  '.cm-tooltip-autocomplete > ul > li': {
    padding: '3px 10px',
    fontFamily: FONT.mono,
  },
  '.cm-tooltip-autocomplete > ul > li[aria-selected]': {
    background: C.accent,
    color: '#fff',
  },
}, { dark: false });

interface Props {
  value: string;
  onChange: (value: string) => void;
  filePath: string;
}

export function CodeEditor({ value, onChange, filePath }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<EditorView | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const lang = getEditorLanguage(filePath);

    const state = EditorState.create({
      doc: value,
      extensions: [
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightSpecialChars(),
        history(),
        foldGutter(),
        drawSelection(),
        dropCursor(),
        EditorState.allowMultipleSelections.of(true),
        indentOnInput(),
        syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
        bracketMatching(),
        closeBrackets(),
        autocompletion(),
        rectangularSelection(),
        crosshairCursor(),
        highlightActiveLine(),
        highlightSelectionMatches(),
        keymap.of([
          ...closeBracketsKeymap,
          ...defaultKeymap,
          ...searchKeymap,
          ...historyKeymap,
          ...foldKeymap,
          ...completionKeymap,
          indentWithTab,
        ]),
        search({ top: true }),
        EditorView.lineWrapping,
        ...(lang ? [lang] : []),
        editorTheme,
        EditorView.updateListener.of(update => {
          if (update.docChanged) onChange(update.state.doc.toString());
        }),
      ],
    });

    const view = new EditorView({ state, parent: containerRef.current });
    viewRef.current = view;
    view.focus();

    return () => {
      view.destroy();
      viewRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []); // смена файла — через key на уровне FileViewer

  return <div ref={containerRef} style={{ height: '100%' }} />;
}
