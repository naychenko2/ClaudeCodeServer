import { memo, useContext, useEffect, useMemo, useState } from 'react';
import { Bot } from 'lucide-react';
import type { ChatItem, Persona } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { usePersonas, ensurePersonasLoaded, personaLabel } from '../../lib/personas';
import { splitAgentResultTail, formatTailTokens, formatTailDuration, isAsyncLaunchAck } from '../../lib/agentTail';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { AGENT_COLORS } from '../AgentSelector';
import { MarkdownContent } from './MarkdownContent';
import { ToolUseView, toolWord } from './ToolUseView';
import { AgentTextBlock, AgentThinkingBlock, NEUTRAL_AGENT_ACCENT } from './AgentContentBlocks';
import { itemKey, type ActivityEntry } from './timeline';
import { ChatProjectContext } from './contexts';

type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Вызов сабагента (встроенный Task/Agent) — сравнение без регистра, как AGENT_TOOLS
// в useSessionArtifacts
export function isAgentToolUse(name: string): boolean {
  const n = name.toLowerCase();
  return n === 'task' || n === 'agent';
}

// Персона по типу сабагента (handle): общий резолв и для Task-вызовов чата
// (subagent_type), и для агентов Workflow (agentType из meta.json)
// projectId — контекст чата: handle уникален только в контексте (глобальные + персоны
// одного проекта), тёзки из ЧУЖИХ проектов не матчатся (зеркало PersonaManager.GetByHandle).
// Страховка на случай остаточных дублей: проектная приоритетнее глобальной.
export function findPersonaByAgentType(agentType: string | undefined, personas: Persona[], projectId: string | null): Persona | null {
  const handle = agentType?.trim().toLowerCase();
  if (!handle) return null;
  const inContext = personas.filter(p => p.handle?.toLowerCase() === handle
    && (p.scope === 'global' || (p.scope === 'project' && p.projectId === projectId)));
  return inContext.find(p => p.scope === 'project') ?? inContext[0] ?? null;
}

// Персона, с которой консультируется этот Task-вызов (по input.subagent_type == handle);
// null — обычный сабагент (Explore/general-purpose/кастомный). Используется и здесь
// (фолбэк на ToolUseView), и в ChatPanel (передать вложенную активность внутрь карточки).
export function findConsultedPersona(item: ToolUseItem, personas: Persona[], projectId: string | null): Persona | null {
  if (!isAgentToolUse(item.name)) return null;
  const inp = (item.input ?? {}) as { subagent_type?: unknown; agentType?: unknown };
  const handle = typeof inp.subagent_type === 'string' ? inp.subagent_type
    : typeof inp.agentType === 'string' ? inp.agentType : '';
  return findPersonaByAgentType(handle, personas, projectId);
}

// Ответ длиннее порога сворачиваем, чтобы карточка не раздувала ленту (как PersonaAskView)
const LONG_ANSWER_CHARS = 1200;

// Презентационная карточка «консультация персоны»: идентичность (аватар + «Роль (Имя)» +
// @handle + цвет), вопрос, слот активности (children) и ответ. Используется и для
// Task-вызовов чата (PersonaTaskView), и для агентов Workflow (WorkflowBlockView).
// Без persona — карточка «просто агента»: нейтральная серая шапка «Агент» + роль вызова
// (agentRole), остальная структура идентична.
// Системный хвост CLI в ответе (agentId + <usage>) вырезается и рендерится
// аккуратной строкой метрик «токены · действия · время».
export function PersonaConsultCard({ persona, agentRole, question, summary, running, isError, answer, children, badge = 'консультант' }: {
  persona?: Persona | null;
  agentRole?: string;         // тип/роль обычного агента (не-персоны), если информативна
  question: string;           // полный вопрос (раскрывается кликом)
  summary?: string;           // короткое описание вопроса (description вызова)
  running: boolean;
  isError: boolean;
  answer: string;
  children?: React.ReactNode; // секция активности между вопросом и ответом
  // Чип роли вызова в шапке: 'консультант' у Task-консультаций чата; null — скрыть
  // (в Workflow персона — полноценный агент, «консультант» там неверен)
  badge?: string | null;
}) {
  const { body: answerBody, tail } = useMemo(() => splitAgentResultTail(answer), [answer]);

  // Длинный вопрос — свёрнут до двух строк, клик раскрывает
  const questionLong = question.length > 140;
  const [questionOpen, setQuestionOpen] = useState(false);
  const answerLong = !isError && answerBody.length > LONG_ANSWER_CHARS;
  const [answerOpen, setAnswerOpen] = useState(false);
  const answerCollapsed = answerLong && !answerOpen;

  const accent = persona
    ? (AGENT_COLORS[persona.avatar?.color ?? ''] ?? NEUTRAL_AGENT_ACCENT)
    : NEUTRAL_AGENT_ACCENT;
  const title = persona ? personaLabel(persona) : 'Агент';
  const hasTailMetrics = !!tail && (tail.tokens != null || tail.toolUses != null || tail.durationMs != null);

  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${accent}`,
      borderRadius: 12, background: C.bgWhite, overflow: 'hidden',
      boxShadow: SHADOW.card, maxWidth: '100%',
    }}>
      {/* Шапка идентичности — лёгкая подложка цвета персоны (у обычного агента — серая) */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 9, padding: '9px 12px',
        background: `${accent}17`, borderBottom: `1px solid ${C.divider}`,
      }}>
        {persona
          ? <PersonaAvatar persona={persona} size={30} />
          : (
            <div style={{
              width: 30, height: 30, borderRadius: '50%', flexShrink: 0,
              background: `${accent}22`, display: 'flex', alignItems: 'center', justifyContent: 'center',
            }}>
              <Bot size={17} color={C.textMuted} strokeWidth={2} />
            </div>
          )}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'baseline', gap: 7, flexWrap: 'wrap' }}>
          <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '100%' }}>
            {title}
          </span>
          {persona
            ? <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>@{persona.handle}</span>
            : agentRole
              ? <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>{agentRole}</span>
              : null}
          {/* Отличие от one-shot persona_ask: работает с инструментами (файлы/заметки/память) */}
          {persona && badge && (
            <span style={{
              fontSize: 10, fontWeight: 700, padding: '1px 7px', borderRadius: 999,
              background: `${accent}22`, color: C.textSecondary,
              textTransform: 'uppercase', letterSpacing: '0.05em',
            }}>
              {badge}
            </span>
          )}
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
export const PersonaTaskView = memo(function PersonaTaskView({ item, online, onOpenFile, activity, renderChild, badge }: {
  item: ToolUseItem;
  online: boolean;
  onOpenFile?: (path: string) => void;
  // Вложенная активность консультанта (дочерние tool_use/text/thinking по parentToolUseId
  // с их глобальными индексами) — рендерится раскрывающейся секцией внутри карточки
  activity?: ActivityEntry[];
  // Универсальный рендер элемента ленты (renderItem из ChatPanel): инструменты рисуются
  // ТЕМИ ЖЕ карточками, что и обычный чат (сворачивание тулов, TodoWrite-чек-лист и т.д.)
  renderChild?: (item: ChatItem, idx: number) => React.ReactNode;
  // Чип роли в шапке карточки (см. PersonaConsultCard.badge); undefined — дефолт «консультант»
  badge?: string | null;
}) {
  // В чатах без персоны стор мог быть ещё не загружен — подтягиваем список
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const personas = usePersonas();
  const project = useContext(ChatProjectContext);

  const inp = (item.input ?? {}) as { prompt?: unknown; description?: unknown };
  const persona = findConsultedPersona(item, personas, project?.id ?? null) ?? undefined;

  const question = typeof inp.prompt === 'string' ? inp.prompt : '';
  const summary = typeof inp.description === 'string' ? inp.description : '';

  // Фоновый запуск (run_in_background): tool_result — служебная квитанция CLI («Async
  // agent launched successfully… agentId… output_file…»), НЕ ответ — её не показываем.
  // Ответом делаем последний текст из потока агента (транскрипт доезжает по мере работы),
  // из активности его при этом убираем, чтобы не дублировался.
  const asyncAck = item.result !== undefined && isAsyncLaunchAck(item.result);
  const lastTextIdx = asyncAck && activity
    ? activity.reduce((last, e, i) => (e.item.kind === 'text' ? i : last), -1)
    : -1;
  const shownActivity = lastTextIdx >= 0 ? activity!.filter((_, i) => i !== lastTextIdx) : activity;
  const answer = asyncAck
    ? (lastTextIdx >= 0 ? (activity![lastTextIdx].item as { text: string }).text : '')
    : (item.result ?? '');
  // Фоновый агент без единого текста — ещё работает (спиннер до первой реплики)
  const running = item.result === undefined || (asyncAck && lastTextIdx < 0);

  // Обычный сабагент (не персона) — стандартная карточка инструмента
  if (!persona) return <ToolUseView item={item} online={online} onOpenFile={onOpenFile} />;

  const accent = AGENT_COLORS[persona.avatar?.color ?? ''] ?? NEUTRAL_AGENT_ACCENT;

  return (
    <PersonaConsultCard
      persona={persona}
      question={question}
      summary={summary}
      running={running}
      isError={!!item.isError}
      answer={answer}
      badge={badge}
    >
      {/* Активность консультанта: весь поток сабагента — инструменты, текст, размышления.
          Пока идёт работа — раскрыта (виден живой прогресс), по завершении сворачивается */}
      {shownActivity && shownActivity.length > 0 && (
        <ActivitySection activity={shownActivity} running={running} accent={accent}
          online={online} onOpenFile={onOpenFile} renderChild={renderChild} />
      )}
    </PersonaConsultCard>
  );
});

// Раскрывающаяся секция активности консультанта внутри карточки: ВЕСЬ поток сабагента.
// Инструменты рендерятся универсальным renderChild (renderItem из ChatPanel) — те же
// карточки и возможности, что в обычной ленте (фолбэк — компактный ToolUseView); текст —
// AgentTextBlock (markdown как в чате, в расцветке персоны), thinking — AgentThinkingBlock.
// Автораскрытие, пока идёт работа (живой прогресс без клика); по завершении сворачивается —
// ручной клик пользователя приоритетнее автоповедения до следующей смены running.
function ActivitySection({ activity, running, accent, online, onOpenFile, renderChild }: {
  activity: ActivityEntry[];
  running: boolean;
  accent: string;
  online: boolean;
  onOpenFile?: (path: string) => void;
  renderChild?: (item: ChatItem, idx: number) => React.ReactNode;
}) {
  const [userOpen, setUserOpen] = useState<boolean | null>(null);
  // Смена фазы running сбрасывает ручной выбор — секция снова следует автоповедению
  useEffect(() => { setUserOpen(null); }, [running]);
  const open = userOpen ?? running;
  // «Действия» — только вызовы инструментов; текст/thinking считать действиями странно
  const toolCount = activity.filter(e => e.item.kind === 'tool_use').length;
  const label = toolCount === 1 ? '1 действие' : `${toolCount} действий`;

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
        {toolCount > 0 && <span style={{ color: C.textMuted }}>· {label}</span>}
        {running && <div className="tool-spinner" style={{ width: 10, height: 10, marginLeft: 4 }} />}
      </button>
      {open && (
        <div style={{ padding: '2px 10px 8px', maxHeight: 360, overflowY: 'auto' }}>
          {activity.map((e, ci) => (
            <div key={itemKey(e.item, e.idx)} style={ci === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>
              {e.item.kind === 'text'
                ? <AgentTextBlock text={e.item.text} accent={accent} />
                : e.item.kind === 'thinking'
                  ? <AgentThinkingBlock text={e.item.text} />
                  : renderChild
                    ? renderChild(e.item, e.idx)
                    : <ToolUseView item={e.item as ToolUseItem} online={online} onOpenFile={onOpenFile} />}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
