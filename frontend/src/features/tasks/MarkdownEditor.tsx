// Markdown-редактор описания задачи: тулбар форматирования + textarea.
// Кнопки оборачивают выделение синтаксисом или добавляют префикс к строкам.

import { useRef, useState } from 'react';
import { C, FONT, R, SHADOW } from '../../lib/design';

interface Props {
  value: string;
  onChange: (v: string) => void;
  minHeight?: number;
  placeholder?: string;
}

type Action =
  | { kind: 'wrap'; before: string; after: string; sample: string }
  | { kind: 'linePrefix'; prefix: string }
  | { kind: 'block'; before: string; after: string; sample: string };

const TOOLBAR: { title: string; label: React.ReactNode; action: Action }[] = [
  { title: 'Заголовок', label: <b style={{ fontFamily: 'inherit' }}>H</b>, action: { kind: 'linePrefix', prefix: '### ' } },
  { title: 'Жирный', label: <b>Ж</b>, action: { kind: 'wrap', before: '**', after: '**', sample: 'текст' } },
  { title: 'Курсив', label: <i>К</i>, action: { kind: 'wrap', before: '*', after: '*', sample: 'текст' } },
  { title: 'Зачёркнутый', label: <s>З</s>, action: { kind: 'wrap', before: '~~', after: '~~', sample: 'текст' } },
  { title: 'Список', label: '•', action: { kind: 'linePrefix', prefix: '- ' } },
  { title: 'Чек-лист', label: '☑', action: { kind: 'linePrefix', prefix: '- [ ] ' } },
  { title: 'Цитата', label: '❝', action: { kind: 'linePrefix', prefix: '> ' } },
  { title: 'Код', label: <span style={{ fontFamily: FONT.mono, fontSize: 11 }}>{'</>'}</span>, action: { kind: 'wrap', before: '`', after: '`', sample: 'код' } },
  { title: 'Ссылка', label: '🔗', action: { kind: 'wrap', before: '[', after: '](url)', sample: 'текст' } },
];

export function MarkdownEditor({ value, onChange, minHeight = 160, placeholder }: Props) {
  const ref = useRef<HTMLTextAreaElement>(null);
  const [focused, setFocused] = useState(false);

  const apply = (action: Action) => {
    const ta = ref.current;
    if (!ta) return;
    const start = ta.selectionStart;
    const end = ta.selectionEnd;
    const selected = value.slice(start, end);

    let next: string;
    let selStart: number;
    let selEnd: number;

    if (action.kind === 'wrap' || action.kind === 'block') {
      const inner = selected || action.sample;
      next = value.slice(0, start) + action.before + inner + action.after + value.slice(end);
      selStart = start + action.before.length;
      selEnd = selStart + inner.length;
    } else {
      // Префикс каждой строке выделения (или текущей строке)
      const lineStart = value.lastIndexOf('\n', start - 1) + 1;
      const lineEndIdx = value.indexOf('\n', end);
      const lineEnd = lineEndIdx === -1 ? value.length : lineEndIdx;
      const block = value.slice(lineStart, lineEnd);
      const prefixed = block.split('\n')
        .map(l => l.startsWith(action.prefix) ? l.slice(action.prefix.length) : action.prefix + l)
        .join('\n');
      next = value.slice(0, lineStart) + prefixed + value.slice(lineEnd);
      selStart = lineStart;
      selEnd = lineStart + prefixed.length;
    }

    onChange(next);
    requestAnimationFrame(() => {
      ta.focus();
      ta.setSelectionRange(selStart, selEnd);
    });
  };

  return (
    <div style={{
      border: `1px solid ${focused ? C.accent : C.border}`,
      borderRadius: R.xl, background: C.bgWhite, overflow: 'hidden',
      boxShadow: focused ? SHADOW.focus : 'none',
      transition: 'border-color 0.15s, box-shadow 0.15s',
    }}>
      {/* Тулбар форматирования */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap',
        padding: '6px 8px', borderBottom: `1px solid ${C.borderLight}`, background: C.bgCard,
      }}>
        {TOOLBAR.map(btn => (
          <button
            key={btn.title}
            title={btn.title}
            // mousedown, чтобы textarea не теряла фокус/выделение до применения
            onMouseDown={e => { e.preventDefault(); apply(btn.action); }}
            style={{
              minWidth: 28, height: 26, padding: '0 6px', cursor: 'pointer',
              border: 'none', borderRadius: R.sm, background: 'transparent',
              color: C.textSecondary, fontFamily: FONT.sans, fontSize: 13, lineHeight: 1,
              display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
            }}
            onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textPrimary; }}
            onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textSecondary; }}
          >
            {btn.label}
          </button>
        ))}
        <div style={{ flex: 1 }} />
        <span style={{ fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, paddingRight: 4 }}>markdown</span>
      </div>

      <textarea
        ref={ref}
        value={value}
        onChange={e => onChange(e.target.value)}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        placeholder={placeholder}
        style={{
          display: 'block', width: '100%', minHeight, boxSizing: 'border-box',
          border: 'none', outline: 'none', resize: 'vertical',
          padding: '11px 13px', background: 'transparent',
          fontFamily: FONT.mono, fontSize: 13, lineHeight: 1.6, color: C.textPrimary,
        }}
      />
    </div>
  );
}
