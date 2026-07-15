import { memo, useState, useEffect, useRef } from 'react';
import { Check } from 'lucide-react';
import { api, type WorkflowAgentInfo, type WorkflowAgentBlock } from '../../lib/api';
import { parseWorkflowMeta } from '../../lib/workflowMeta';
import { usePersonas, ensurePersonasLoaded } from '../../lib/personas';
import { C, FONT, R } from '../../lib/design';
import { ToolUseView, toolWord, type ToolUseItem } from './ToolUseView';
import { splitAgentResultTail } from '../../lib/agentTail';
import { PersonaConsultCard, PersonaTaskView, findConsultedPersona, findPersonaByAgentType } from './PersonaTaskView';
import { AgentTextBlock, AgentThinkingBlock, AgentStructuredBlock, NEUTRAL_AGENT_ACCENT } from './AgentContentBlocks';
import { AGENT_COLORS } from '../AgentSelector';
import { type ActivityEntry } from './timeline';

function parseTranscriptDir(result: string | undefined): string | null {
  if (!result) return null;
  const m = result.match(/Transcript dir:\s*(.+)/);
  return m ? m[1].trim() : null;
}

// React.memo: пропсы (workflow-элемент, массивы агентов) пересоздаются только при
// изменении items — карточка не перерендеривается на каждый чужой рендер ленты
export const WorkflowBlockView = memo(function WorkflowBlockView({ workflow, agents, childrenByParentId, onOpenFile }: {
  workflow: ToolUseItem;
  agents: ToolUseItem[];
  // Все дочерние элементы по родителю (инструменты + текст/thinking сабагентов)
  childrenByParentId: Map<string, ActivityEntry[]>;
  onOpenFile?: (path: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const [expandedAgents, setExpandedAgents] = useState<Set<string>>(new Set());

  // Персоны владельца: агенты workflow с agentType == handle персоны рендерятся
  // её карточкой-консультацией (как Task-вызовы персон в обычной ленте)
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const personas = usePersonas();

  // Локальный фоллбэк — используется только для старых сессий без серверного ватчера
  const [localAgents, setLocalAgents] = useState<WorkflowAgentInfo[] | null>(null);
  const [localLoading, setLocalLoading] = useState(false);

  // Авто-раскрытие при завершении: только если workflow завершился в этой сессии
  const wasSettledRef = useRef(false);

  const isDone = workflow.result !== undefined;

  // Серверные агенты (реалтайм через SignalR — приходят от WorkflowWatcher)
  const serverAgents = workflow.workflowAgents;
  const serverDone = workflow.workflowDone;

  // Итоговые значения: сервер приоритетнее фоллбэка
  const transcriptAgents = serverAgents ?? localAgents;
  const transcriptLoading = serverAgents !== undefined ? false : localLoading;
  const hasTranscriptDir = isDone && !!parseTranscriptDir(workflow.result as string | undefined);
  // isSettled: result получен И workflow окончательно завершён
  // isDone=false → спиннер (workflow tool ещё не вернул result)
  const isSettled = isDone && (
    serverDone === true ||
    !hasTranscriptDir ||
    // Все агенты от сервера завершены → считаем settled (без ожидания IsDone=true)
    (serverAgents !== undefined && serverAgents.length > 0 && serverAgents.every(a => a.isDone === true)) ||
    // Фоллбэк для старых сессий (нет живого watcher'а) — только если ВСЕ агенты завершены
    (serverAgents === undefined && localAgents !== null && localAgents.length > 0 && localAgents.every(a => a.isDone === true))
  );

  const meta = parseWorkflowMeta(workflow.input);
  const phases = meta?.phases ?? [];
  const doneCount = agents.filter(a => a.result !== undefined).length;
  const totalCount = agents.length;
  const progress = totalCount > 0 ? doneCount / totalCount : isSettled ? 1 : 0;

  // Прогресс по фазам: сколько фаз завершено (оцениваем по transcript агентам)
  const transcriptDone = transcriptAgents?.filter(a => a.isDone === true).length ?? 0;
  const transcriptTotal = transcriptAgents?.length ?? 0;
  const completedPhaseCount = isSettled
    ? phases.length
    : transcriptTotal > 0 && phases.length > 0
    ? Math.min(
        Math.floor((transcriptDone / transcriptTotal) * phases.length),
        phases.length - 1  // не помечать все фазы done пока workflow не завершён
      )
    : 0;

  // Авто-раскрытие карточки при завершении workflow в текущей сессии
  useEffect(() => {
    if (isSettled && !wasSettledRef.current) setExpanded(true);
    wasSettledRef.current = isSettled;
  }, [isSettled]);

  // Фоллбэк-загрузка для старых сессий (где серверный ватчер не работал)
  useEffect(() => {
    if (serverAgents !== undefined) return; // сервер уже обрабатывает
    if (!isDone || localAgents !== null) return;
    const dir = parseTranscriptDir(workflow.result as string | undefined);
    if (!dir) return;
    setLocalLoading(true);
    api.workflow.getAgents(dir)
      .then(r => setLocalAgents(r.agents))
      .catch(() => setLocalAgents([]))
      .finally(() => setLocalLoading(false));
  }, [isDone, localAgents, workflow.result, serverAgents]);

  const toggleAgent = (id: string) => setExpandedAgents(prev => {
    const next = new Set(prev);
    if (next.has(id)) next.delete(id); else next.add(id);
    return next;
  });

  const DoneIcon = () => (
    <Check size={14} color={C.success} strokeWidth={2.5} style={{ flexShrink: 0 }} />
  );

  return (
    <div style={{ border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden', background: C.bgPanel }}>
      {/* Шапка */}
      <div
        onClick={() => setExpanded(e => !e)}
        style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '10px 14px', cursor: 'pointer', userSelect: 'none' as const }}
      >
        <span style={{ flexShrink: 0, display: 'flex', alignItems: 'center', marginTop: meta?.description ? -1 : 0 }}>
          {isSettled
            ? <DoneIcon />
            : <div className="tool-spinner" />}
        </span>
        {/* Название + описание: одна колонка, description под заголовком */}
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textPrimary, flexShrink: 0 }}>Workflow</span>
            {/* Дотики фаз — когда есть phases (независимо от description) */}
            {phases.length > 0 && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 3, flexShrink: 0 }}>
                {phases.map((_, idx) => {
                  const done = idx < completedPhaseCount;
                  const active = !isSettled && idx === completedPhaseCount;
                  return (
                    <div key={idx} style={{
                      width: 6, height: 6, borderRadius: '50%', flexShrink: 0,
                      background: done || isSettled ? C.success : active ? C.accent : C.borderLight,
                      transition: 'background 0.25s',
                    }} />
                  );
                })}
              </div>
            )}
            {phases.length > 0 && (
              <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0, whiteSpace: 'nowrap' }}>
                {completedPhaseCount}/{phases.length}
              </span>
            )}
            {/* Когда фаз нет — счётчик агентов + бар */}
            {phases.length === 0 && !meta?.description && totalCount > 0 && (
              <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>
                {doneCount}/{totalCount} агентов
              </span>
            )}
            {phases.length === 0 && !meta?.description && totalCount > 0 && (
              <div style={{ flex: 1, height: 3, background: C.borderLight, borderRadius: 2, overflow: 'hidden', minWidth: 40, maxWidth: 80 }}>
                <div style={{ width: `${Math.round(progress * 100)}%`, height: '100%', background: isSettled ? C.success : C.accent, borderRadius: 2, transition: 'width 0.3s ease' }} />
              </div>
            )}
          </div>
          {meta?.description && (
            <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {meta.description}
            </div>
          )}
        </div>
        <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>{expanded ? '▴ скрыть' : '▾ детали'}</span>
      </div>

      {/* Тело */}
      {expanded && (
        <div style={{ borderTop: `1px solid ${C.border}` }}>
          {/* Фазы из meta.phases */}
          {phases.length > 0 && (
            <div style={{ padding: '10px 14px 8px', display: 'flex', flexDirection: 'column', gap: 1 }}>
              {phases.map((phase, idx) => {
                const phaseDone = idx < completedPhaseCount;
                const phaseActive = !isSettled && idx === completedPhaseCount;
                return (
                <div key={idx} style={{ display: 'flex', alignItems: 'flex-start', gap: 8, padding: '5px 0', borderBottom: idx < phases.length - 1 ? `1px solid ${C.borderLight}` : undefined }}>
                  <span style={{ flexShrink: 0, width: 16, display: 'flex', alignItems: 'center', justifyContent: 'center', marginTop: 2 }}>
                    {phaseDone
                      ? <Check size={13} color={C.success} strokeWidth={2.5} style={{ flexShrink: 0 }} />
                      : phaseActive
                      ? <div className="tool-spinner" style={{ width: 10, height: 10 }} />
                      : <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.border }} />}
                  </span>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 500, color: C.textPrimary, lineHeight: 1.4 }}>{phase.title}</div>
                    {phase.detail && (
                      <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textSecondary, lineHeight: 1.4, marginTop: 1 }}>{phase.detail}</div>
                    )}
                  </div>
                </div>
                );
              })}
            </div>
          )}
          {/* Субагенты из потока (если есть) */}
          {agents.length > 0 && (
            <div style={{ borderTop: phases.length > 0 ? `1px solid ${C.border}` : undefined }}>
              {agents.map((agent, idx) => {
                // Финальный текст сабагента дублирует тело его ответа (tool_result) —
                // после завершения в активности не показываем (ответ рендерит карточка)
                const agentAnswer = typeof agent.result === 'string'
                  ? splitAgentResultTail(agent.result).body.trim() : null;
                const children = (childrenByParentId.get(agent.id) ?? []).filter(e =>
                  !(agentAnswer !== null && e.item.kind === 'text' && e.item.text.trim() === agentAnswer));
                const toolCount = children.filter(e => e.item.kind === 'tool_use').length;
                // Стрим-агент консультируется с персоной → её карточка с активностью внутри
                if (findConsultedPersona(agent, personas)) {
                  return (
                    <div key={agent.id} style={{ padding: '8px 14px', borderTop: idx > 0 ? `1px solid ${C.bgInset}` : undefined }}>
                      <PersonaTaskView item={agent} online activity={children.length > 0 ? children : undefined} onOpenFile={onOpenFile} />
                    </div>
                  );
                }
                const isAgentExpanded = expandedAgents.has(agent.id);
                const inp = (agent.input ?? {}) as Record<string, unknown>;
                const rawLabel =
                  (typeof inp.description === 'string' ? inp.description : null) ??
                  (typeof inp.label === 'string' ? inp.label : null) ??
                  (typeof inp.prompt === 'string' ? inp.prompt : null) ??
                  (typeof inp.task === 'string' ? inp.task : null) ?? '';
                const label = rawLabel.split('\n')[0].slice(0, 100);
                const agentDone = agent.result !== undefined;
                return (
                  <div key={agent.id} style={{ borderTop: idx > 0 ? `1px solid ${C.bgInset}` : undefined }}>
                    <div
                      onClick={children.length > 0 ? () => toggleAgent(agent.id) : undefined}
                      style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 14px', cursor: children.length > 0 ? 'pointer' : 'default' }}
                    >
                      <span style={{ flexShrink: 0, width: 14, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                        {agentDone
                          ? <Check size={13} color={C.success} strokeWidth={2.5} style={{ flexShrink: 0 }} />
                          : <div className="tool-spinner" style={{ width: 11, height: 11 }} />}
                      </span>
                      <span style={{ flex: 1, fontFamily: FONT.sans, fontSize: 12.5, color: label ? C.textPrimary : C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {label || `Агент ${idx + 1}`}
                      </span>
                      {toolCount > 0 && (
                        <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>{toolCount} {toolWord(toolCount)}</span>
                      )}
                      {children.length > 0 && (
                        <span style={{ color: C.textMuted, fontSize: 11, flexShrink: 0, display: 'inline-block', transform: isAgentExpanded ? 'rotate(180deg)' : 'none', transition: 'transform 0.2s' }}>▾</span>
                      )}
                    </div>
                    {isAgentExpanded && children.length > 0 && (
                      <div style={{ paddingLeft: 22, paddingRight: 14, paddingBottom: 4, borderTop: `1px solid ${C.bgInset}` }}>
                        {children.map((e, ti) => (
                          <div key={e.item.kind === 'tool_use' ? e.item.id : `b-${e.idx}`} style={ti > 0 ? { borderTop: `1px solid ${C.bgInset}` } : undefined}>
                            {e.item.kind === 'text'
                              ? <AgentTextBlock text={e.item.text} accent={NEUTRAL_AGENT_ACCENT} />
                              : e.item.kind === 'thinking'
                                ? <AgentThinkingBlock text={e.item.text} />
                                : <ToolUseView item={e.item as ToolUseItem} onOpenFile={onOpenFile} />}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}
          {/* Агенты из transcript-файлов (показываем как только приходят через SignalR, не ждём isDone) */}
          {(transcriptLoading || (transcriptAgents && transcriptAgents.length > 0)) && (
            <div style={{ borderTop: `1px solid ${C.border}` }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '7px 14px 6px', background: C.bgInset, borderBottom: `1px solid ${C.borderLight}` }}>
                <span style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase' as const, letterSpacing: '0.04em' }}>Агенты</span>
                {!transcriptLoading && transcriptAgents && (
                  <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, background: C.borderLight, borderRadius: R.sm, padding: '1px 5px', fontWeight: 600, lineHeight: 1.5 }}>
                    {transcriptAgents.length}
                  </span>
                )}
              </div>
              {transcriptLoading && (
                <div style={{ padding: '6px 0' }}>
                  {[80, 65, 90].map((w, i) => (
                    <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '7px 14px', borderTop: i > 0 ? `1px solid ${C.bgInset}` : undefined }}>
                      <div style={{ width: 13, height: 13, borderRadius: '50%', background: C.borderLight, flexShrink: 0 }} />
                      <div style={{ height: 11, width: `${w}%`, maxWidth: 280, borderRadius: 4, background: C.borderLight }} />
                    </div>
                  ))}
                </div>
              )}
              {!transcriptLoading && transcriptAgents && transcriptAgents.length > 0 && (
                <div style={{ padding: '4px 0' }}>
                  {transcriptAgents.map((agent, idx) => {
                    const transcriptDir = parseTranscriptDir(workflow.result as string | undefined);
                    // Персона (agentType == handle) → её карточка; обычный агент — та же
                    // карточка с нейтральной серой шапкой «Агент» + роль вызова (agentType,
                    // если информативен — дефолтный workflow-subagent не показываем)
                    const persona = findPersonaByAgentType(agent.agentType, personas);
                    const role = !persona && agent.agentType && agent.agentType !== 'workflow-subagent'
                      ? agent.agentType : undefined;
                    const accent = persona
                      ? (AGENT_COLORS[persona.avatar?.color ?? ''] ?? NEUTRAL_AGENT_ACCENT)
                      : NEUTRAL_AGENT_ACCENT;
                    return (
                      <div key={agent.id} style={{ padding: '6px 14px', borderTop: idx > 0 ? `1px solid ${C.bgInset}` : undefined }}>
                        <PersonaConsultCard
                          persona={persona}
                          agentRole={role}
                          question={agent.prompt}
                          running={agent.isDone !== true}
                          isError={false}
                          answer={agent.summary ?? ''}
                        >
                          {transcriptDir && (
                            <TranscriptAgentTimeline dir={transcriptDir} agentId={agent.id}
                              accent={accent} running={agent.isDone !== true} onOpenFile={onOpenFile} />
                          )}
                        </PersonaConsultCard>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          )}
          {/* Ничего нет */}
          {agents.length === 0 && phases.length === 0 && !transcriptLoading && !transcriptAgents?.length && (
            <div style={{ padding: '10px 14px', fontFamily: FONT.sans, fontSize: 12, color: C.textMuted }}>
              {isDone ? 'Детали недоступны' : 'Запуск субагентов…'}
            </div>
          )}
        </div>
      )}
    </div>
  );
});

// Ленивый таймлайн workflow-агента: полный поток (текст/thinking/инструменты) из его
// транскрипта, подгружается по REST при раскрытии. Пока агент работает (running) —
// рефетч раз в 5с; по завершении — финальный снапшот. Рендер — как в обычном чате:
// текст — AgentTextBlock (MarkdownContent в расцветке персоны/нейтральной), thinking —
// AgentThinkingBlock, инструмент — полноценный ToolUseView (input + раскрываемый результат).
function TranscriptAgentTimeline({ dir, agentId, accent, running, edge = 'bottom', onOpenFile }: {
  dir: string;
  agentId: string;
  accent: string;
  running: boolean;
  // Сторона разделителя: bottom — слот карточки персоны, top — низ панели деталей агента
  edge?: 'top' | 'bottom';
  onOpenFile?: (path: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const [blocks, setBlocks] = useState<WorkflowAgentBlock[] | null>(null);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const load = () => {
      api.workflow.getTimeline(dir, agentId)
        .then(r => { if (!cancelled) setBlocks(r.blocks); })
        .catch(() => { if (!cancelled) setBlocks(prev => prev ?? []); });
    };
    load();
    if (!running) return () => { cancelled = true; };
    const t = setInterval(load, 5000);
    return () => { cancelled = true; clearInterval(t); };
  }, [open, running, dir, agentId]);

  return (
    <div style={edge === 'bottom' ? { borderBottom: `1px solid ${C.divider}` } : { borderTop: `1px solid ${C.borderLight}` }}>
      <button
        onClick={() => setOpen(o => !o)}
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
        <span style={{ fontWeight: 600 }}>Ход работы</span>
        {running && open && <div className="tool-spinner" style={{ width: 10, height: 10, marginLeft: 4 }} />}
      </button>
      {open && (
        <div style={{ padding: '2px 10px 8px', maxHeight: 360, overflowY: 'auto' }}>
          {blocks === null ? (
            <div style={{ padding: '6px 0' }}>
              {[85, 60].map((w, i) => (
                <div key={i} style={{ height: 10, width: `${w}%`, borderRadius: 4, background: C.borderLight, marginTop: i > 0 ? 6 : 0 }} />
              ))}
            </div>
          ) : blocks.length === 0 ? (
            <div style={{ padding: '6px 2px', fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, fontStyle: 'italic' }}>
              {running ? 'Агент ещё не начал писать…' : 'Поток недоступен'}
            </div>
          ) : (
            // Ключ по индексу безопасен: транскрипт append-only, блоки не переставляются
            blocks.map((b, i) => b.kind === 'text'
              ? <AgentTextBlock key={i} text={b.text ?? ''} accent={accent} />
              : b.kind === 'thinking'
                ? <AgentThinkingBlock key={i} text={b.text ?? ''} />
                : b.kind === 'structured'
                ? <AgentStructuredBlock key={i} json={b.text ?? ''} accent={accent} />
                : (
                  // Тот же ToolUseView, что и в обычном чате: иконка, статус,
                  // раскрываемые аргументы и результат, diff у правок
                  <div key={b.toolId ?? i} style={i > 0 ? { borderTop: `1px solid ${C.bgInset}` } : undefined}>
                    <ToolUseView
                      item={{
                        kind: 'tool_use',
                        id: b.toolId ?? `tl-${agentId}-${i}`,
                        name: b.toolName ?? '',
                        input: b.toolInput,
                        result: b.toolResult,
                        isError: b.isError,
                      }}
                      onOpenFile={onOpenFile}
                    />
                  </div>
                ))
          )}
        </div>
      )}
    </div>
  );
}

