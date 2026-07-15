import { useState } from 'react';
import { C, FONT } from '../../lib/design';
import { MarkdownContent } from './MarkdownContent';

// Блоки контента сабагента внутри секции «Активность» (PersonaTaskView, AgentActionsBlock,
// таймлайн Workflow): текст — тем же markdown-рендером, что и основной чат, но компактнее
// и в расцветке агента (accent персоны или нейтральный C.accent); thinking — сворачиваемое
// «Размышление» как в основной ленте, но с локальным состоянием (блоки приходят целиком,
// глобальный toggle по индексу не нужен).

// Порог сворачивания длинного текста — как LONG_ANSWER_CHARS у ответа в PersonaConsultCard
const LONG_TEXT_CHARS = 1200;

export function AgentTextBlock({ text, accent }: { text: string; accent: string }) {
  const long = text.length > LONG_TEXT_CHARS;
  const [open, setOpen] = useState(false);
  const collapsed = long && !open;
  if (!text.trim()) return null;

  return (
    <div style={{
      margin: '6px 0', borderLeft: `2px solid ${accent}`,
      background: `${accent}08`, borderRadius: '0 8px 8px 0', overflow: 'hidden',
    }}>
      <div style={{
        position: 'relative', padding: '6px 10px',
        fontSize: 13, color: C.textHeading, wordBreak: 'break-word',
        ...(collapsed ? { maxHeight: 240, overflow: 'hidden' } : {}),
      }}>
        <MarkdownContent text={text} />
        {collapsed && (
          <div style={{
            position: 'absolute', left: 0, right: 0, bottom: 0, height: 44,
            background: `linear-gradient(transparent, ${C.bgWhite})`, pointerEvents: 'none',
          }} />
        )}
      </div>
      {long && (
        <button
          onClick={() => setOpen(o => !o)}
          style={{
            display: 'block', width: '100%', padding: '4px 10px',
            border: 'none', background: 'none', cursor: 'pointer', textAlign: 'left',
            fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 600, color: accent,
          }}
        >
          {open ? 'Свернуть ▴' : 'Показать полностью ▾'}
        </button>
      )}
    </div>
  );
}

// Структурированный итог агента (StructuredOutput при agent() со schema):
// по умолчанию свёрнут — раскрывается кликом в json-блок с подсветкой
export function AgentStructuredBlock({ json, accent }: { json: string; accent: string }) {
  const [open, setOpen] = useState(false);
  if (!json.trim()) return null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', padding: '2px 0' }}>
      <div
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5,
          cursor: 'pointer', userSelect: 'none', padding: '2px 0', width: 'fit-content',
        }}
      >
        <span style={{ fontFamily: FONT.mono, fontSize: 10, fontWeight: 700, color: accent, flexShrink: 0 }}>
          {'{ }'}
        </span>
        <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>
          Структурированный итог
        </span>
        <span style={{
          color: C.textMuted, fontSize: 10, opacity: 0.7,
          transform: open ? 'rotate(180deg)' : 'rotate(0deg)',
          transition: 'transform 0.2s', display: 'inline-block',
        }}>▾</span>
      </div>
      {open && (
        <div style={{
          marginTop: 4, borderLeft: `2px solid ${accent}`,
          background: `${accent}08`, borderRadius: '0 8px 8px 0',
          padding: '2px 10px', fontSize: 12.5,
          maxHeight: 320, overflowY: 'auto',
        }}>
          <MarkdownContent text={'```json\n' + json + '\n```'} />
        </div>
      )}
    </div>
  );
}

export function AgentThinkingBlock({ text }: { text: string }) {
  const [expanded, setExpanded] = useState(false);
  if (!text.trim()) return null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', padding: '2px 0' }}>
      <div
        onClick={() => setExpanded(e => !e)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5,
          cursor: 'pointer', userSelect: 'none', padding: '2px 0', width: 'fit-content',
        }}
      >
        <span style={{ color: C.textMuted, display: 'flex', alignItems: 'center', flexShrink: 0 }}>
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 3a6 6 0 0 0-4 10.5V17h8v-3.5A6 6 0 0 0 12 3z" />
            <path d="M9 20h6M10 22h4" />
          </svg>
        </span>
        <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>Размышление</span>
        <span title="приблизительно, по объёму текста" style={{ fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, opacity: 0.7 }}>
          ~{Math.max(1, Math.round(text.length / 4))} ток.
        </span>
        <span style={{
          color: C.textMuted, fontSize: 10, opacity: 0.7,
          transform: expanded ? 'rotate(180deg)' : 'rotate(0deg)',
          transition: 'transform 0.2s', display: 'inline-block',
        }}>▾</span>
      </div>
      {expanded && (
        <div style={{
          marginTop: 4, paddingLeft: 10, borderLeft: `2px solid ${C.borderLight}`,
          fontSize: 11.5, fontStyle: 'italic', lineHeight: 1.65, color: C.textMuted,
          whiteSpace: 'pre-wrap', fontFamily: FONT.sans,
          maxHeight: 300, overflowY: 'auto',
        }}>
          {text}
        </div>
      )}
    </div>
  );
}
