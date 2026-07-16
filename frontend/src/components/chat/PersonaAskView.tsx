import { memo, useContext, useEffect, useState } from 'react';
import type { ChatItem } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { usePersonas, ensurePersonasLoaded, personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { AGENT_COLORS } from '../AgentSelector';
import { MarkdownContent } from './MarkdownContent';
import { findPersonaByAgentType } from './PersonaTaskView';
import { ChatProjectContext } from './contexts';

// Вызов persona_ask (mcp__personas__persona_ask) — сравнение по суффиксу, без регистра
export function isPersonaAsk(name: string): boolean {
  return name.toLowerCase().endsWith('__persona_ask');
}

// Нейтральный акцент для удалённой/неизвестной персоны — hex, чтобы работала
// альфа-подложка `${accent}17` (CSS-переменные с суффиксом альфы не работают)
const NEUTRAL_ACCENT = '#8A8070';

// Ответ длиннее порога сворачиваем, чтобы карточка не раздувала ленту
const LONG_ANSWER_CHARS = 1200;

// Фолбэк-аватар, если персону удалили: круг с первой буквой handle нейтрального цвета
function FallbackAvatar({ handle, size }: { handle: string; size: number }) {
  return (
    <div
      aria-hidden
      style={{
        width: size, height: size, borderRadius: '50%', flexShrink: 0, userSelect: 'none',
        background: NEUTRAL_ACCENT, color: '#fff',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.4), lineHeight: 1,
      }}
    >
      {(handle[0] ?? '?').toUpperCase()}
    </div>
  );
}

// Карточка «ответ персоны» — вызов инструмента persona_ask в ленте чата.
// Идентичность (аватар + «Роль (Имя)» + @handle + цвет персоны) вместо
// безликой строки инструмента: видно, КТО отвечает и что у него спросили.
export const PersonaAskView = memo(function PersonaAskView({ item }: { item: Extract<ChatItem, { kind: 'tool_use' }> }) {
  // В чатах без персоны стор мог быть ещё не загружен — подтягиваем список
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const personas = usePersonas();
  const project = useContext(ChatProjectContext);

  const inp = (item.input ?? {}) as { handle?: unknown; question?: unknown };
  const handle = String(inp.handle ?? '').replace(/^@+/, '').trim();
  const question = typeof inp.question === 'string' ? inp.question : '';

  // Контекстный резолв: handle уникален лишь в границах проекта — тёзку из чужого не берём
  const persona = findPersonaByAgentType(handle, personas, project?.id ?? null);
  const accent = persona ? (AGENT_COLORS[persona.avatar?.color ?? ''] ?? NEUTRAL_ACCENT) : NEUTRAL_ACCENT;
  const title = persona ? personaLabel(persona) : handle || 'Персона';

  const running = item.result === undefined;
  const isError = !!item.isError;
  const answer = item.result ?? '';

  // Длинный вопрос — свёрнут до двух строк, клик раскрывает
  const questionLong = question.length > 140;
  const [questionOpen, setQuestionOpen] = useState(false);

  // Длинный ответ — свёрнут до фиксированной высоты с fade и кнопкой
  const answerLong = !isError && answer.length > LONG_ANSWER_CHARS;
  const [answerOpen, setAnswerOpen] = useState(false);
  const answerCollapsed = answerLong && !answerOpen;

  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${accent}`,
      borderRadius: 12, background: C.bgWhite, overflow: 'hidden',
      boxShadow: SHADOW.card, maxWidth: '100%',
    }}>
      {/* Шапка идентичности — лёгкая подложка цвета персоны */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 9, padding: '9px 12px',
        background: `${accent}17`, borderBottom: `1px solid ${C.divider}`,
      }}>
        {persona
          ? <PersonaAvatar persona={persona} size={30} />
          : <FallbackAvatar handle={handle} size={30} />}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'baseline', gap: 7, flexWrap: 'wrap' }}>
          <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '100%' }}>
            {title}
          </span>
          {handle && (
            <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>@{handle}</span>
          )}
        </div>
        {/* Статус справа: выполняется / ошибка */}
        {running && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
            <div className="tool-spinner" />
            <span style={{ fontSize: 11, color: C.textMuted }}>Спрашиваю…</span>
          </span>
        )}
        {!running && isError && (
          <span style={{ fontSize: 11, color: C.dangerText, flexShrink: 0 }}>ошибка</span>
        )}
      </div>

      {/* Вопрос — приглушённо, свёрнут до двух строк */}
      {question && (
        <div
          onClick={questionLong ? () => setQuestionOpen(o => !o) : undefined}
          title={questionLong && !questionOpen ? 'Показать вопрос целиком' : undefined}
          style={{
            padding: '8px 12px', fontSize: 12.5, lineHeight: 1.5, color: C.textSecondary,
            borderBottom: `1px solid ${C.divider}`,
            cursor: questionLong ? 'pointer' : 'default',
            ...(questionOpen ? {} : {
              display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' as const,
              overflow: 'hidden',
            }),
          }}
        >
          <span style={{ fontWeight: 600, color: C.textMuted }}>Вопрос: </span>
          {question}
        </div>
      )}

      {/* Тело: ответ / ошибка / ожидание */}
      {running ? (
        <div style={{ padding: '10px 12px', fontSize: 12.5, color: C.textMuted, fontStyle: 'italic' }}>
          {title} готовит ответ…
        </div>
      ) : isError ? (
        <div style={{
          margin: 10, padding: '8px 11px', borderRadius: 8,
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          fontSize: 12.5, color: C.dangerText, whiteSpace: 'pre-wrap', wordBreak: 'break-word',
          maxHeight: 200, overflow: 'auto',
        }}>
          {answer.trim() || 'Не удалось получить ответ персоны'}
        </div>
      ) : (
        <>
          <div style={{
            position: 'relative', padding: '10px 12px 4px',
            fontSize: 14, color: C.textHeading, wordBreak: 'break-word',
            ...(answerCollapsed ? { maxHeight: 260, overflow: 'hidden' } : {}),
          }}>
            <MarkdownContent text={answer} />
            {/* Fade-градиент внизу свёрнутого ответа */}
            {answerCollapsed && (
              <div style={{
                position: 'absolute', left: 0, right: 0, bottom: 0, height: 56,
                background: `linear-gradient(transparent, ${C.bgWhite})`, pointerEvents: 'none',
              }} />
            )}
          </div>
          {answerLong && (
            <button
              onClick={() => setAnswerOpen(o => !o)}
              style={{
                display: 'block', width: '100%', padding: '7px 12px',
                border: 'none', borderTop: `1px solid ${C.divider}`,
                background: 'none', cursor: 'pointer', textAlign: 'center',
                fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: accent,
              }}
            >
              {answerOpen ? 'Свернуть ▴' : 'Показать полностью ▾'}
            </button>
          )}
        </>
      )}
    </div>
  );
});
