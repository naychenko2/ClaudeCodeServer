import { memo, useEffect, useMemo, useState } from 'react';
import type { ChatItem, Persona } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { usePersonas, ensurePersonasLoaded, personaLabel } from '../../lib/personas';
import { splitAgentResultTail, formatTailTokens, formatTailDuration } from '../../lib/agentTail';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { AGENT_COLORS } from '../AgentSelector';
import { MarkdownContent } from './MarkdownContent';
import { ToolUseView, toolWord } from './ToolUseView';

type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Вызов сабагента (встроенный Task/Agent) — сравнение без регистра, как AGENT_TOOLS
// в useSessionArtifacts
export function isAgentToolUse(name: string): boolean {
  const n = name.toLowerCase();
  return n === 'task' || n === 'agent';
}

// Персона по типу сабагента (handle): общий резолв и для Task-вызовов чата
// (subagent_type), и для агентов Workflow (agentType из meta.json)
export function findPersonaByAgentType(agentType: string | undefined, personas: Persona[]): Persona | null {
  const handle = agentType?.trim();
  if (!handle) return null;
  return personas.find(p => p.handle?.toLowerCase() === handle.toLowerCase()) ?? null;
}

// Персона, с которой консультируется этот Task-вызов (по input.subagent_type == handle);
// null — обычный сабагент (Explore/general-purpose/кастомный). Используется и здесь
// (фолбэк на ToolUseView), и в ChatPanel (передать вложенную активность внутрь карточки).
export function findConsultedPersona(item: ToolUseItem, personas: Persona[]): Persona | null {
  if (!isAgentToolUse(item.name)) return null;
  const inp = (item.input ?? {}) as { subagent_type?: unknown; agentType?: unknown };
  const handle = typeof inp.subagent_type === 'string' ? inp.subagent_type
    : typeof inp.agentType === 'string' ? inp.agentType : '';
  return findPersonaByAgentType(handle, personas);
}

// Ответ длиннее порога сворачиваем, чтобы карточка не раздувала ленту (как PersonaAskView)
const LONG_ANSWER_CHARS = 1200;

// Презентационная карточка «консультация персоны»: идентичность (аватар + «Роль (Имя)» +
// @handle + цвет), вопрос, слот активности (children) и ответ. Используется и для
// Task-вызовов чата (PersonaTaskView), и для агентов Workflow (WorkflowBlockView).
// Системный хвост CLI в ответе (agentId + <usage>) вырезается и рендерится
// аккуратной строкой метрик «токены · действия · время».
export function PersonaConsultCard({ persona, question, summary, running, isError, answer, children }: {
  persona: Persona;
  question: string;           // полный вопрос (раскрывается кликом)
  summary?: string;           // короткое описание вопроса (description вызова)
  running: boolean;
  isError: boolean;
  answer: string;
  children?: React.ReactNode; // секция активности между вопросом и ответом
}) {
  const { body: answerBody, tail } = useMemo(() => splitAgentResultTail(answer), [answer]);

  // Длинный вопрос — свёрнут до двух строк, клик раскрывает
  const questionLong = question.length > 140;
  const [questionOpen, setQuestionOpen] = useState(false);
  const answerLong = !isError && answerBody.length > LONG_ANSWER_CHARS;
  const [answerOpen, setAnswerOpen] = useState(false);
  const answerCollapsed = answerLong && !answerOpen;

  const accent = AGENT_COLORS[persona.avatar?.color ?? ''] ?? C.accent;
  const title = personaLabel(persona);
  const hasTailMetrics = !!tail && (tail.tokens != null || tail.toolUses != null || tail.durationMs != null);

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
        <PersonaAvatar persona={persona} size={30} />
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'baseline', gap: 7, flexWrap: 'wrap' }}>
          <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '100%' }}>
            {title}
          </span>
          <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>@{persona.handle}</span>
          {/* Отличие от one-shot persona_ask: работает с инструментами (файлы/заметки/память) */}
          <span style={{
            fontSize: 10, fontWeight: 700, padding: '1px 7px', borderRadius: 999,
            background: `${accent}22`, color: C.textSecondary,
            textTransform: 'uppercase', letterSpacing: '0.05em',
          }}>
            консультант
          </span>
        </div>
        {running && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
            <div className="tool-spinner" />
            <span style={{ fontSize: 11, color: C.textMuted }}>Консультируется…</span>
          </span>
        )}
        {!running && isError && (
          <span style={{ fontSize: 11, color: C.dangerText, flexShrink: 0 }}>ошибка</span>
        )}
      </div>

      {/* Вопрос: короткое описание + раскрываемый полный prompt */}
      {(summary || question) && (
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
          {questionOpen ? question || summary : summary || question}
        </div>
      )}

      {/* Слот активности консультанта (инструменты, файлы) */}
      {children}

      {/* Тело: ответ / ошибка / ожидание */}
      {running ? (
        <div style={{ padding: '10px 12px', fontSize: 12.5, color: C.textMuted, fontStyle: 'italic' }}>
          {title} изучает материалы и готовит ответ…
        </div>
      ) : isError ? (
        <div style={{
          margin: 10, padding: '8px 11px', borderRadius: 8,
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          fontSize: 12.5, color: C.dangerText, whiteSpace: 'pre-wrap', wordBreak: 'break-word',
          maxHeight: 200, overflow: 'auto',
        }}>
          {answerBody.trim() || 'Не удалось получить ответ персоны'}
        </div>
      ) : (
        <>
          <div style={{
            position: 'relative', padding: '10px 12px 4px',
            fontSize: 14, color: C.textHeading, wordBreak: 'break-word',
            ...(answerCollapsed ? { maxHeight: 260, overflow: 'hidden' } : {}),
          }}>
            {answerBody.trim()
              ? <MarkdownContent text={answerBody} />
              : <span style={{ fontSize: 12.5, color: C.textMuted, fontStyle: 'italic' }}>Ответ передан без текста</span>}
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
          {/* Метрики консультации из системного хвоста — вместо сырого текста CLI */}
          {hasTailMetrics && (
            <div style={{
              display: 'flex', alignItems: 'center', gap: 5, flexWrap: 'wrap',
              padding: '5px 12px 7px', borderTop: `1px solid ${C.divider}`,
              fontFamily: FONT.sans, fontSize: 11, color: C.textMuted,
            }}>
              {tail!.tokens != null && <span>{formatTailTokens(tail!.tokens)} токенов</span>}
              {tail!.tokens != null && (tail!.toolUses != null || tail!.durationMs != null) && <span>·</span>}
              {tail!.toolUses != null && <span>{tail!.toolUses} {toolWord(tail!.toolUses)}</span>}
              {tail!.toolUses != null && tail!.durationMs != null && <span>·</span>}
              {tail!.durationMs != null && <span>{formatTailDuration(tail!.durationMs)}</span>}
            </div>
          )}
        </>
      )}
    </div>
  );
}

// Карточка «консультация персоны-сабагента»: Task tool_use, чей subagent_type совпал
// с handle персоны владельца. Идентичность (аватар + «Роль (Имя)» + @handle + цвет)
// вместо безликой строки инструмента; не совпал (Explore/general-purpose/кастомные
// агенты) — обычный ToolUseView (фолбэк здесь, а не в ChatItemView: usePersonas —
// хук, звать его условно нельзя).
export const PersonaTaskView = memo(function PersonaTaskView({ item, online, onOpenFile, activity, renderChild, idxMap }: {
  item: ToolUseItem;
  online: boolean;
  onOpenFile?: (path: string) => void;
  // Вложенная активность консультанта (дочерние tool_use по parentToolUseId) —
  // рендерится раскрывающейся секцией внутри карточки (передаёт ChatPanel)
  activity?: ToolUseItem[];
  // Универсальный рендер элемента ленты (renderItem из ChatPanel): активность рисуется
  // ТЕМИ ЖЕ карточками, что и обычный чат (сворачивание тулов, TodoWrite-чек-лист и т.д.)
  renderChild?: (item: ChatItem, idx: number) => React.ReactNode;
  idxMap?: Map<string, number>;
}) {
  // В чатах без персоны стор мог быть ещё не загружен — подтягиваем список
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const personas = usePersonas();

  const inp = (item.input ?? {}) as { prompt?: unknown; description?: unknown };
  const persona = findConsultedPersona(item, personas) ?? undefined;

  const question = typeof inp.prompt === 'string' ? inp.prompt : '';
  const summary = typeof inp.description === 'string' ? inp.description : '';

  const running = item.result === undefined;

  // Обычный сабагент (не персона) — стандартная карточка инструмента
  if (!persona) return <ToolUseView item={item} online={online} onOpenFile={onOpenFile} />;

  const accent = AGENT_COLORS[persona.avatar?.color ?? ''] ?? C.accent;

  return (
    <PersonaConsultCard
      persona={persona}
      question={question}
      summary={summary}
      running={running}
      isError={!!item.isError}
      answer={item.result ?? ''}
    >
      {/* Активность консультанта: какие инструменты дёргает (чтение файлов, поиск, память).
          Пока идёт работа — раскрыта (виден живой прогресс), по завершении сворачивается */}
      {activity && activity.length > 0 && (
        <ActivitySection activity={activity} running={running} accent={accent}
          online={online} onOpenFile={onOpenFile} renderChild={renderChild} idxMap={idxMap} />
      )}
    </PersonaConsultCard>
  );
});

// Раскрывающаяся секция активности консультанта внутри карточки. Автораскрытие,
// пока идёт работа (живой прогресс без клика); по завершении сворачивается — ручной
// клик пользователя приоритетнее автоповедения до следующей смены running.
// Элементы рендерятся универсальным renderChild (renderItem из ChatPanel) — те же
// карточки и возможности, что в обычной ленте; фолбэк без него — компактный ToolUseView.
function ActivitySection({ activity, running, accent, online, onOpenFile, renderChild, idxMap }: {
  activity: ToolUseItem[];
  running: boolean;
  accent: string;
  online: boolean;
  onOpenFile?: (path: string) => void;
  renderChild?: (item: ChatItem, idx: number) => React.ReactNode;
  idxMap?: Map<string, number>;
}) {
  const [userOpen, setUserOpen] = useState<boolean | null>(null);
  // Смена фазы running сбрасывает ручной выбор — секция снова следует автоповедению
  useEffect(() => { setUserOpen(null); }, [running]);
  const open = userOpen ?? running;
  const label = activity.length === 1 ? '1 действие' : `${activity.length} действий`;

  return (
    <div style={{ borderBottom: `1px solid ${C.divider}` }}>
      <button
        onClick={() => setUserOpen(o => !(o ?? running))}
        style={{
          display: 'flex', alignItems: 'center', gap: 6, width: '100%',
          padding: '6px 12px', border: 'none', background: `${accent}0d`,
          cursor: 'pointer', textAlign: 'left',
          fontFamily: FONT.sans, fontSize: 11.5, color: C.textSecondary,
        }}
      >
        <span style={{
          display: 'inline-block', fontSize: 10, color: C.textMuted,
          transform: open ? 'rotate(0deg)' : 'rotate(-90deg)', transition: 'transform 0.15s',
        }}>▾</span>
        <span style={{ fontWeight: 600 }}>Активность</span>
        <span style={{ color: C.textMuted }}>· {label}</span>
        {running && <div className="tool-spinner" style={{ width: 10, height: 10, marginLeft: 4 }} />}
      </button>
      {open && (
        <div style={{ padding: '2px 10px 8px', maxHeight: 360, overflowY: 'auto' }}>
          {activity.map((child, ci) => (
            <div key={child.id} style={ci === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>
              {renderChild
                ? renderChild(child, idxMap?.get(child.id) ?? 0)
                : <ToolUseView item={child} online={online} onOpenFile={onOpenFile} />}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
