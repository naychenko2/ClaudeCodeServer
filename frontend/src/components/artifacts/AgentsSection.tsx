// Секция «Агенты»: сводка + одиночные агенты, workflow-группы и свёрнутые
// завершённые. Перенесена из ArtifactsPanel verbatim.
import { useState, type CSSProperties, type ReactNode } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, FONT, R } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { MarkdownViewer } from '../MarkdownViewer';
import type { AgentArtifact, AgentToolCall, WorkflowGroup } from '../../hooks/useSessionArtifacts';
import { callName } from './shared';

// Статус-иконка агента — в палитре TodoRow, чтобы прогресс задач и агентов читался одинаково
function AgentStatusIcon({ status }: { status: AgentArtifact['status'] }) {
  if (status === 'done') return (
    <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="8" r="8" fill={C.success} />
      <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
  if (status === 'error') return (
    <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="8" r="8" fill={C.danger} />
      <path d="M5.5 5.5l5 5M10.5 5.5l-5 5" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" />
    </svg>
  );
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="8" r="6.5" stroke={C.accentMuted} strokeWidth="2" />
      <path d="M8 1.5a6.5 6.5 0 0 1 6.5 6.5" stroke={C.accent} strokeWidth="2" strokeLinecap="round">
        <animateTransform attributeName="transform" type="rotate" from="0 8 8" to="360 8 8" dur="0.9s" repeatCount="indefinite" />
      </path>
    </svg>
  );
}

// Строка дочернего вызова в мини-ленте раскрытого агента
function AgentCallRow({ call }: { call: AgentToolCall }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: 7, minWidth: 0 }}>
      <span style={{
        flexShrink: 0, fontFamily: FONT.mono, fontSize: 11, fontWeight: 600,
        color: call.isError ? C.dangerText : call.running ? C.accent : C.textSecondary,
      }}>
        {callName(call.name)}
      </span>
      <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={call.arg}>
        {call.arg ?? ''}
      </span>
      {call.isError && (
        <span style={{ flexShrink: 0, fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, color: C.dangerText }}>ошибка</span>
      )}
    </div>
  );
}

// Свёрнутый блок в раскрытой карточке («Промпт», «Ответ агента»)
function AgentSection({ title, children }: { title: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <button
        onClick={() => setOpen(v => !v)}
        style={{
          display: 'flex', alignItems: 'center', gap: 5, border: 'none', background: 'transparent',
          cursor: 'pointer', padding: '2px 0', fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textSecondary,
        }}
      >
        <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }} />
        {title}
      </button>
      {open && (
        <div style={{
          marginTop: 3, maxHeight: 220, overflowY: 'auto', padding: '8px 10px',
          background: C.bgInset, border: `1px solid ${C.border}`, borderRadius: R.md,
        }}>
          {children}
        </div>
      )}
    </div>
  );
}

// Карточка агента: шапка со статусом (в работе / готов / ошибка) и деталями,
// по клику раскрывается «внутрянка»: промпт, мини-лента дочерних вызовов
// (для workflow — сводка инструментов и файлы) и финальный ответ.
function AgentRow({ agent }: { agent: AgentArtifact }) {
  const [open, setOpen] = useState(false);
  const [hover, setHover] = useState(false);
  const details: string[] = [];
  details.push(agent.kind === 'workflow' ? 'workflow' : agent.type ?? 'субагент');
  if (agent.toolCount > 0) details.push(`${agent.toolCount} инстр.`);
  if (agent.status === 'running' && agent.lastTool) details.push(`сейчас: ${callName(agent.lastTool)}`);
  const expandable = !!(agent.prompt || agent.resultText || agent.calls?.length || agent.tools?.length || agent.files?.length);

  return (
    <div>
      <button
        onClick={() => expandable && setOpen(v => !v)}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        style={{
          width: '100%', display: 'flex', alignItems: 'flex-start', gap: 9, padding: '6px 14px 6px 8px',
          border: 'none', textAlign: 'left', cursor: expandable ? 'pointer' : 'default',
          background: hover && expandable ? C.bgSelected : 'transparent',
        }}
      >
        <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={expandable ? C.textMuted : 'transparent'}
          style={{ flexShrink: 0, marginTop: 4, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }} />
        <span style={{ flexShrink: 0, marginTop: 2, display: 'flex' }}>
          <AgentStatusIcon status={agent.status} />
        </span>
        <span style={{ minWidth: 0, flex: 1, display: 'flex', flexDirection: 'column' }}>
          <span style={{
            fontFamily: FONT.sans, fontSize: 13, lineHeight: 1.4,
            color: agent.status === 'running' ? C.textHeading : C.textSecondary,
            fontWeight: agent.status === 'running' ? 600 : 400,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }} title={agent.label}>
            {agent.label}
          </span>
          <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {details.join(' · ')}
          </span>
        </span>
        {agent.background && (
          <span style={{
            flexShrink: 0, marginTop: 2, fontFamily: FONT.sans, fontSize: 10, fontWeight: 700,
            padding: '2px 7px', borderRadius: R.sm, color: C.textSecondary, background: C.bgInset, whiteSpace: 'nowrap',
          }}>
            фон
          </span>
        )}
      </button>

      {open && (
        <div style={{ padding: '0 14px 8px 44px', display: 'flex', flexDirection: 'column', gap: 5 }}>
          {agent.prompt && (
            <AgentSection title="Промпт">
              <div style={{ fontFamily: FONT.mono, fontSize: 11, lineHeight: 1.5, color: C.textSecondary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                {agent.prompt}
              </div>
            </AgentSection>
          )}

          {/* Мини-лента действий субагента (живая: новые вызовы дописываются снизу) */}
          {agent.calls && agent.calls.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 3, padding: '2px 0' }}>
              {agent.calls.map(c => <AgentCallRow key={c.id} call={c} />)}
            </div>
          )}

          {/* Сводка workflow-агента: чипы инструментов + затронутые файлы */}
          {agent.tools && agent.tools.length > 0 && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {agent.tools.map(t => (
                <span key={t.name} style={{
                  fontFamily: FONT.mono, fontSize: 10.5, padding: '2px 7px', borderRadius: R.sm,
                  color: C.textSecondary, background: C.bgInset, border: `1px solid ${C.border}`,
                }}>
                  {callName(t.name)} ×{t.count}
                </span>
              ))}
            </div>
          )}
          {agent.files && agent.files.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              {agent.files.map(f => (
                <span key={f} style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={f}>
                  {f}
                </span>
              ))}
            </div>
          )}

          {agent.resultText && (
            <AgentSection title="Ответ агента">
              <MarkdownViewer content={agent.resultText} />
            </AgentSection>
          )}
        </div>
      )}
    </div>
  );
}

// Сворачиваемая секция списка на вкладке «Агенты». Два вида заголовка:
//  - 'caption' (рубрики «Завершённые» / «Фоновые») — мелкий caps-ярлык;
//  - 'title' (группа workflow) — обычный заголовок с иконкой и осмысленным
//    названием нормальным кейсом (в caps описание workflow превращается в кашу).
// tail всегда прижат вправо, title занимает остаток и обрезается с многоточием.
function CollapseGroup({ title, tail, defaultOpen, variant = 'caption', icon, children }: {
  title: string;
  tail?: ReactNode;
  defaultOpen: boolean;
  variant?: 'caption' | 'title';
  icon?: ReactNode;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const isTitle = variant === 'title';
  const titleStyle: CSSProperties = isTitle
    ? { fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.textHeading }
    : { fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.04em' };
  return (
    <div>
      <button
        onClick={() => setOpen(v => !v)}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 6,
          padding: isTitle ? '8px 14px 8px 10px' : '9px 14px 3px 10px',
          border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
        }}
      >
        <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted}
          style={{ flexShrink: 0, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }} />
        {icon}
        <span style={{ ...titleStyle, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={title}>
          {title}
        </span>
        {tail}
      </button>
      {open && children}
    </div>
  );
}

// Иконка workflow (граф из двух связанных нод) — для заголовка группы
const workflowIcon = (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="2"
    strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
    <rect x="3" y="3" width="7" height="7" rx="1.5" /><rect x="14" y="14" width="7" height="7" rx="1.5" />
    <path d="M10 6.5h3a2 2 0 0 1 2 2V14" />
  </svg>
);

// Группа агентов одного workflow: заголовок с названием и прогрессом N/M,
// раскрыта пока workflow идёт, завершённая — свёрнута.
function WorkflowGroupView({ group }: { group: WorkflowGroup }) {
  const total = group.agents.length;
  return (
    <CollapseGroup
      variant="title"
      icon={workflowIcon}
      title={group.name}
      defaultOpen={!group.settled}
      tail={
        <span style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 5, marginLeft: 8 }}>
          <span style={{ fontFamily: FONT.mono, fontSize: 11, fontWeight: 600, color: C.textMuted }}>
            {group.doneCount}/{total}
          </span>
          {group.settled ? (
            <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
              <circle cx="8" cy="8" r="8" fill={C.success} />
              <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          ) : (
            <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
              <circle cx="8" cy="8" r="6.5" stroke={C.accentMuted} strokeWidth="2.4" />
              <path d="M8 1.5a6.5 6.5 0 0 1 6.5 6.5" stroke={C.accent} strokeWidth="2.4" strokeLinecap="round">
                <animateTransform attributeName="transform" type="rotate" from="0 8 8" to="360 8 8" dur="0.9s" repeatCount="indefinite" />
              </path>
            </svg>
          )}
        </span>
      }
    >
      {total > 0 ? (
        group.agents.map(a => <AgentRow key={a.id} agent={a} />)
      ) : (
        <span style={{ display: 'block', padding: '4px 14px 6px 31px', fontFamily: FONT.sans, fontSize: 12, color: C.textMuted }}>
          Запуск агентов…
        </span>
      )}
    </CollapseGroup>
  );
}

// Сводная плашка вверху вкладки «Агенты»: словами — сколько в работе,
// завершено и (если есть) с ошибкой. Иконки в палитре статусов AgentRow.
function AgentsSummary({ running, done, errors }: { running: number; done: number; errors: number }) {
  const item = (icon: ReactNode, text: string, color: string) => (
    <span style={{ display: 'flex', alignItems: 'center', gap: 5, fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color }}>
      {icon}{text}
    </span>
  );
  return (
    <div style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: 14, flexWrap: 'wrap',
      padding: '9px 14px', borderBottom: `1px solid ${C.border}`,
    }}>
      {running > 0 && item(
        <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
          <circle cx="8" cy="8" r="6.5" stroke={C.accentMuted} strokeWidth="2.4" />
          <path d="M8 1.5a6.5 6.5 0 0 1 6.5 6.5" stroke={C.accent} strokeWidth="2.4" strokeLinecap="round">
            <animateTransform attributeName="transform" type="rotate" from="0 8 8" to="360 8 8" dur="0.9s" repeatCount="indefinite" />
          </path>
        </svg>,
        `${running} в работе`, C.textHeading,
      )}
      {done > 0 && item(
        <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
          <circle cx="8" cy="8" r="8" fill={C.success} />
          <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>,
        `${done} завершено`, C.textSecondary,
      )}
      {errors > 0 && item(
        <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
          <circle cx="8" cy="8" r="8" fill={C.danger} />
          <path d="M5.5 5.5l5 5M10.5 5.5l-5 5" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" />
        </svg>,
        `${errors} с ошибкой`, C.dangerText,
      )}
    </div>
  );
}

export function AgentsSection({ agents, workflows }: { agents: AgentArtifact[]; workflows: WorkflowGroup[] }) {
  // Все агенты сессии (одиночные + внутри workflow) для сводки
  const allAgents = [...agents, ...workflows.flatMap(w => w.agents)];
  const agentsRunning = allAgents.filter(a => a.status === 'running').length;
  const agentsDone = allAgents.filter(a => a.status === 'done').length;
  const agentsErrors = allAgents.filter(a => a.status === 'error').length;
  // Активные всегда на виду, отработанные — в свёрнутую секцию (не скрываем:
  // из панели ничего не должно «исчезать», но и захламлять список незачем)
  const activeAgents = agents.filter(a => a.status === 'running');
  const finishedAgents = agents.filter(a => a.status !== 'running');
  return (
    <>
      <AgentsSummary running={agentsRunning} done={agentsDone} errors={agentsErrors} />
      <div style={{ flex: 1, overflowY: 'auto', padding: '4px 0 8px' }}>
        {activeAgents.map(a => <AgentRow key={a.id} agent={a} />)}

        {workflows.map(g => <WorkflowGroupView key={g.id} group={g} />)}

        {finishedAgents.length > 0 && (
          <CollapseGroup
            title="Завершённые"
            defaultOpen={false}
            tail={
              <span style={{ flexShrink: 0, fontFamily: FONT.mono, fontSize: 10.5, fontWeight: 600, color: C.textMuted }}>
                {finishedAgents.length}
              </span>
            }
          >
            {finishedAgents.map(a => <AgentRow key={a.id} agent={a} />)}
          </CollapseGroup>
        )}
      </div>
    </>
  );
}
