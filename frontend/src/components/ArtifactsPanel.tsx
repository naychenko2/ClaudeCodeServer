import { useState, useRef, useEffect, type CSSProperties, type ReactNode } from 'react';
import { C, FONT, R, SHADOW } from '../lib/design';
import { PillSwitch } from './Toolbar';
import { MarkdownViewer } from './MarkdownViewer';
import { useSessionArtifacts, type AgentArtifact, type AgentToolCall, type ArtifactFile, type ArtifactLink, type PlanStatus, type TodoItem, type WorkflowGroup } from '../hooks/useSessionArtifacts';
import { IconNotes } from '../features/notes/shared';
import { saveChatNote, openNoteById } from '../features/notes/saveToNote';
import { PersonaContextTab } from './PersonaContextTab';

interface Props {
  sessionId: string | null;
  // В чат-режиме проекта нет: projectId/rootPath/onOpenFile отсутствуют, вкладка «Файлы» скрывается.
  projectId?: string;
  rootPath?: string;
  onOpenFile?: (path: string) => void;
  onClose: () => void;
  isMobile?: boolean;
  // Собеседник-персона текущего чата — показывает вкладку «Контекст персоны» (①-L2a)
  personaId?: string | null;
}

type TabKey = 'plan' | 'todos' | 'agents' | 'files' | 'links' | 'context';

function basename(p: string): string {
  const norm = p.replace(/\\/g, '/');
  return norm.slice(norm.lastIndexOf('/') + 1);
}

function dirname(p: string): string {
  const norm = p.replace(/\\/g, '/');
  const i = norm.lastIndexOf('/');
  return i > 0 ? norm.slice(0, i) : '';
}

// Единый стиль кнопок-чипов в навигаторе плана («последний», «оглавление») —
// утопленный фон (не белый), одинаковые размеры/типографика.
const navChip: CSSProperties = {
  height: 28, padding: '0 10px', borderRadius: R.md, cursor: 'pointer',
  display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
  fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap',
  border: `1px solid ${C.border}`, background: C.bgInset, color: C.textSecondary,
};

// Заголовок оглавления = реальный <h*> узел из отрендеренного плана.
// Единый источник (DOM), чтобы список TOC и цель скролла были тем же узлом —
// иначе строковый парсер разъезжается с рендером remark (Setext, blockquote и пр.).
interface Heading { level: number; text: string; el: HTMLElement }

// Чип «в заметку» в навигаторе плана — сохраняет текущий план в базу заметок
function SavePlanChip({ plan, projectId }: { plan: string; projectId?: string }) {
  const [savedId, setSavedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const save = () => {
    if (busy) return;
    if (savedId) { openNoteById(savedId); return; }
    setBusy(true);
    saveChatNote({ text: plan, projectId, titlePrefix: 'План: ' })
      .then(n => { setSavedId(n.id); setTimeout(() => setSavedId(null), 6000); })
      .catch(() => {})
      .finally(() => setBusy(false));
  };
  return (
    <button onClick={save} title={savedId ? 'Сохранено — открыть заметку' : 'Сохранить план в заметку'}
      style={savedId
        ? { ...navChip, background: C.successBg, border: `1px solid ${C.successBg}`, color: C.successText }
        : { ...navChip, opacity: busy ? 0.6 : 1 }}>
      <IconNotes size={13} />
      {savedId ? 'открыть' : 'в заметку'}
    </button>
  );
}

const STATUS_META: Record<PlanStatus, { label: string; fg: string; bg: string }> = {
  approved: { label: 'одобрен', fg: C.successText, bg: C.successBg },
  rejected: { label: 'отклонён', fg: C.dangerText, bg: C.dangerBg },
  pending:  { label: 'ожидает', fg: C.textSecondary, bg: C.bgInset },
};

function FileRow({ file, onOpen }: { file: ArtifactFile; onOpen: () => void }) {
  const [hover, setHover] = useState(false);
  const [copied, setCopied] = useState(false);
  const dir = dirname(file.path);
  const showDelta = file.changed && file.hasDelta && (file.added > 0 || file.removed > 0);

  const handleClick = () => {
    if (file.external) {
      // На Windows копируем с обратными слэшами (как ждёт проводник/cmd).
      // Optional chaining до .then включительно — буфер может быть недоступен (http-контекст).
      const toCopy = /^[A-Za-z]:\//.test(file.path) ? file.path.replace(/\//g, '\\') : file.path;
      navigator.clipboard?.writeText(toCopy)?.then(() => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      })?.catch(() => { /* буфер недоступен — молча */ });
    } else {
      onOpen();
    }
  };

  return (
    <button
      onClick={handleClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={file.external ? `${file.path} — скопировать путь` : file.path}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 9,
        padding: '7px 14px', border: 'none', cursor: 'pointer', textAlign: 'left',
        background: hover ? C.bgSelected : 'transparent',
      }}
    >
      {file.external ? (
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2"
          strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
          <rect x="9" y="9" width="13" height="13" rx="2" /><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
        </svg>
      ) : (
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2"
          strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
          <path d="M14 2v6h6" />
        </svg>
      )}
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {basename(file.path)}
        </div>
        {dir && (
          <div style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {dir}
          </div>
        )}
      </div>
      {showDelta ? (
        <div style={{ display: 'flex', gap: 6, flexShrink: 0, fontFamily: FONT.mono, fontSize: 11, fontWeight: 600 }}>
          {file.added > 0 && <span style={{ color: C.diffAddText }}>+{file.added}</span>}
          {file.removed > 0 && <span style={{ color: C.diffRemText }}>−{file.removed}</span>}
        </div>
      ) : (
        <span style={{ flexShrink: 0, fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, color: copied ? C.successText : C.textMuted, whiteSpace: 'nowrap' }}>
          {copied ? 'скопировано' : file.external ? 'вне проекта' : !file.changed ? 'упомянут' : ''}
        </span>
      )}
    </button>
  );
}

// Пункт todo-списка — те же иконки статусов, что у TodoPlanView в чате,
// чтобы прогресс в панели и в ленте выглядел одинаково.
function TodoRow({ todo }: { todo: TodoItem }) {
  const isDone = todo.status === 'completed';
  const isActive = todo.status === 'in_progress';
  const label = isActive && todo.activeForm ? todo.activeForm : todo.content;
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 9, padding: '5px 14px' }}>
      <span style={{ flexShrink: 0, marginTop: 2, display: 'flex' }}>
        {isDone ? (
          <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
            <circle cx="8" cy="8" r="8" fill={C.success} />
            <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : isActive ? (
          <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
            <circle cx="8" cy="8" r="7" fill={C.accent} />
            <circle cx="8" cy="8" r="2.6" fill={C.accentLight} />
          </svg>
        ) : (
          <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
            <circle cx="8" cy="8" r="6.5" stroke={C.dashed} strokeWidth="1.5" />
          </svg>
        )}
      </span>
      <span style={{
        fontFamily: FONT.sans, fontSize: 13, lineHeight: 1.4,
        color: isDone ? C.textMuted : isActive ? C.textHeading : C.textSecondary,
        textDecoration: isDone ? 'line-through' : 'none',
        fontWeight: isActive ? 600 : 400,
      }}>
        {label}
      </span>
    </div>
  );
}

// Имя инструмента для мини-ленты: MCP → «server · tool», остальные — как есть
function callName(name: string): string {
  return name.startsWith('mcp__') ? name.slice(5).replace(/__/g, ' · ') : name;
}

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
        <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6"
          strokeLinecap="round" strokeLinejoin="round"
          style={{ transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }}>
          <path d="M9 6l6 6-6 6" />
        </svg>
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
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke={expandable ? C.textMuted : 'transparent'}
          strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"
          style={{ flexShrink: 0, marginTop: 4, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }}>
          <path d="M9 6l6 6-6 6" />
        </svg>
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
        <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2.6"
          strokeLinecap="round" strokeLinejoin="round"
          style={{ flexShrink: 0, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.12s' }}>
          <path d="M9 6l6 6-6 6" />
        </svg>
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

function LinkRow({ link }: { link: ArtifactLink }) {
  const [hover, setHover] = useState(false);
  return (
    <a
      href={link.url}
      target="_blank"
      rel="noopener noreferrer"
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={link.url}
      style={{
        display: 'flex', flexDirection: 'column', gap: 1,
        padding: '7px 14px', textDecoration: 'none',
        background: hover ? C.bgSelected : 'transparent',
      }}
    >
      <span style={{ fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {link.domain}
      </span>
      <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {link.url}
      </span>
    </a>
  );
}

// Иконка-кнопка навигатора планов (стрелка ‹ / ›)
function NavArrow({ dir, disabled, onClick }: { dir: 'prev' | 'next'; disabled: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      title={dir === 'prev' ? 'Предыдущий план' : 'Следующий план'}
      style={{
        width: 24, height: 24, border: 'none', borderRadius: R.sm, background: 'transparent',
        cursor: disabled ? 'default' : 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
        color: disabled ? C.border : C.textSecondary, flexShrink: 0,
      }}
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
        {dir === 'prev' ? <path d="M15 6l-6 6 6 6" /> : <path d="M9 6l6 6-6 6" />}
      </svg>
    </button>
  );
}

export function ArtifactsPanel({ sessionId, projectId, rootPath, onOpenFile, onClose, isMobile, personaId }: Props) {
  const { files, plans, todos, links, agents, workflows } = useSessionArtifacts(sessionId, projectId, rootPath);
  // Чат-режим (без проекта): файлы открывать некуда — вкладку «Файлы» не показываем.
  const isChat = !projectId;

  // Вкладки — только непустые, в порядке: План → Задачи → Агенты → Файлы → Ссылки
  const todosDone = todos.filter(t => t.status === 'completed').length;
  // Все агенты сессии (одиночные + внутри workflow) для счётчиков и сводки
  const allAgents = [...agents, ...workflows.flatMap(w => w.agents)];
  const agentsRunning = allAgents.filter(a => a.status === 'running').length;
  const agentsDone = allAgents.filter(a => a.status === 'done').length;
  const agentsErrors = allAgents.filter(a => a.status === 'error').length;
  const agentsTotal = allAgents.length + workflows.filter(w => !w.agents.length).length;
  const tabs: { value: TabKey; label: string }[] = [];
  if (plans.length) tabs.push({ value: 'plan', label: 'План' });
  if (todos.length) tabs.push({ value: 'todos', label: `Задачи · ${todosDone}/${todos.length}` });
  if (agentsTotal > 0) {
    // Прогресс завершённых/всего — единый формат с вкладкой «Задачи».
    // «Завершено» = всё, что перестало работать (done + error), чтобы счётчик
    // доходил до N/N, когда никто не пашет; активные — в сводке словами внутри.
    tabs.push({ value: 'agents', label: `Агенты · ${agentsTotal - agentsRunning}/${agentsTotal}` });
  }
  if (files.length && !isChat) tabs.push({ value: 'files', label: `Файлы · ${files.length}` });
  if (links.length) tabs.push({ value: 'links', label: `Ссылки · ${links.length}` });
  // Контекст персоны-собеседника (①-L2a): память/знания/задачи рядом с чатом
  if (personaId) tabs.push({ value: 'context', label: 'Контекст' });

  const [active, setActive] = useState<TabKey>('plan');
  const activeKey: TabKey | undefined = tabs.some(t => t.value === active) ? active : tabs[0]?.value;
  const isEmpty = tabs.length === 0;

  // Навигация по планам: null = «не выбирал» → показываем последний
  const [planIdx, setPlanIdx] = useState<number | null>(null);
  const effIdx = planIdx == null ? plans.length - 1 : Math.min(Math.max(planIdx, 0), plans.length - 1);
  const curPlan = plans[effIdx];

  // Оглавление текущего плана + поповер
  const [tocOpen, setTocOpen] = useState(false);
  const [headings, setHeadings] = useState<Heading[]>([]);
  const planContentRef = useRef<HTMLDivElement>(null);

  // Заголовки берём из реального DOM плана (после рендера MarkdownViewer) — один источник,
  // никакого рассинхрона со строковым парсером. Пересбор при смене текста плана/вкладки.
  const planText = activeKey === 'plan' ? curPlan?.plan : undefined;
  useEffect(() => {
    const root = planContentRef.current;
    if (!root) { setHeadings([]); return; }
    const list: Heading[] = [];
    root.querySelectorAll('h1,h2,h3,h4,h5,h6').forEach(n => {
      const el = n as HTMLElement;
      const text = (el.textContent ?? '').trim();
      if (text) list.push({ level: Number(el.tagName[1]), text, el });
    });
    setHeadings(list);
  }, [planText]);

  const scrollToHeading = (h: Heading) => {
    h.el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    setTocOpen(false);
  };

  return (
    <div style={{ width: '100%', height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel, overflow: 'hidden' }}>
      {/* Шапка */}
      <div style={{
        flexShrink: 0, height: 52, display: 'flex', alignItems: 'center', gap: 8,
        padding: '0 10px 0 14px', borderBottom: `1px solid ${C.border}`,
      }}>
        <span style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, flex: 1 }}>
          Артефакты сессии
        </span>
        <button
          onClick={onClose}
          title="Скрыть панель"
          style={{ width: 30, height: 30, border: 'none', borderRadius: R.md, background: 'transparent', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
            {isMobile ? <path d="M6 9l6 6 6-6" /> : <path d="M9 6l6 6-6 6" />}
          </svg>
        </button>
      </div>

      {isEmpty ? (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 10, padding: 24, textAlign: 'center' }}>
          <svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" style={{ opacity: 0.6 }}>
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
            <path d="M14 2v6h6M9 13h6M9 17h3" />
          </svg>
          <span style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, lineHeight: 1.5 }}>
            Пока ничего не менялось.<br />Здесь появятся план, задачи, агенты, файлы и ссылки.
          </span>
        </div>
      ) : (
        <>
          {/* Переключатель вкладок (только непустые) */}
          <div style={{ flexShrink: 0, padding: '10px 12px', borderBottom: `1px solid ${C.border}` }}>
            <PillSwitch<TabKey> value={activeKey!} options={tabs} onChange={setActive} fill />
          </div>

          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            {activeKey === 'plan' && curPlan && (
              <>
                {/* Навигатор планов + статус + оглавление */}
                <div style={{
                  flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', gap: 6,
                  padding: '8px 10px 8px 12px', borderBottom: `1px solid ${C.border}`,
                }}>
                  {plans.length > 1 && (
                    <NavArrow dir="prev" disabled={effIdx === 0} onClick={() => setPlanIdx(effIdx - 1)} />
                  )}
                  <span style={{ fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>
                    {plans.length > 1 ? `План ${effIdx + 1} / ${plans.length}` : 'План'}
                  </span>
                  {plans.length > 1 && (
                    <NavArrow dir="next" disabled={effIdx === plans.length - 1} onClick={() => setPlanIdx(effIdx + 1)} />
                  )}
                  <span style={{
                    fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700, padding: '2px 7px', borderRadius: R.sm,
                    color: STATUS_META[curPlan.status].fg, background: STATUS_META[curPlan.status].bg, whiteSpace: 'nowrap',
                  }}>
                    {STATUS_META[curPlan.status].label}
                  </span>
                  <div style={{ flex: 1 }} />
                  <SavePlanChip plan={curPlan.plan} projectId={projectId} />
                  {plans.length > 1 && effIdx !== plans.length - 1 && (
                    <button
                      onClick={() => setPlanIdx(null)}
                      title="К последнему плану"
                      style={navChip}
                    >
                      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M13 17l5-5-5-5" /><path d="M6 17l5-5-5-5" />
                      </svg>
                      последний
                    </button>
                  )}
                  {headings.length > 0 && (
                    <button
                      onClick={() => setTocOpen(v => !v)}
                      title="Оглавление"
                      style={tocOpen
                        ? { ...navChip, background: C.accentMuted, border: `1px solid ${C.accentMuted}`, color: C.accent }
                        : navChip}
                    >
                      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <line x1="8" y1="6" x2="21" y2="6" /><line x1="8" y1="12" x2="21" y2="12" /><line x1="8" y1="18" x2="21" y2="18" />
                        <line x1="3" y1="6" x2="3.01" y2="6" /><line x1="3" y1="12" x2="3.01" y2="12" /><line x1="3" y1="18" x2="3.01" y2="18" />
                      </svg>
                      оглавление
                    </button>
                  )}

                  {/* Поповер оглавления */}
                  {tocOpen && headings.length > 0 && (
                    <>
                      <div onClick={() => setTocOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
                      <div style={{
                        position: 'absolute', top: '100%', right: 8, marginTop: 4, zIndex: 41,
                        width: 'min(280px, calc(100% - 16px))', maxHeight: 320, overflowY: 'auto',
                        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
                        boxShadow: SHADOW.dropdown, padding: '6px 0',
                      }}>
                        {headings.map((h, i) => (
                          <button
                            key={i}
                            onClick={() => scrollToHeading(h)}
                            style={{
                              width: '100%', textAlign: 'left', border: 'none', background: 'transparent', cursor: 'pointer',
                              padding: '5px 12px', paddingLeft: 12 + (h.level - 1) * 12,
                              fontFamily: FONT.sans, fontSize: 12.5, color: h.level <= 2 ? C.textHeading : C.textSecondary,
                              fontWeight: h.level <= 2 ? 600 : 400,
                              whiteSpace: 'normal', overflowWrap: 'anywhere', lineHeight: 1.35,
                            }}
                            onMouseEnter={e => (e.currentTarget.style.background = C.bgSelected)}
                            onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
                          >
                            {h.text}
                          </button>
                        ))}
                      </div>
                    </>
                  )}
                </div>

                {/* Текст плана (скроллится) */}
                <div ref={planContentRef} style={{ flex: 1, overflowY: 'auto', padding: '14px 16px' }}>
                  <MarkdownViewer content={curPlan.plan} />
                </div>
              </>
            )}

            {activeKey === 'todos' && (
              <div style={{ flex: 1, overflowY: 'auto', padding: '8px 0' }}>
                {todos.map((t, i) => <TodoRow key={i} todo={t} />)}
              </div>
            )}

            {activeKey === 'agents' && (() => {
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
            })()}

            {activeKey === 'files' && (
              <div style={{ flex: 1, overflowY: 'auto', padding: '6px 0' }}>
                {files.map(f => <FileRow key={f.path} file={f} onOpen={() => onOpenFile?.(f.path)} />)}
              </div>
            )}

            {activeKey === 'links' && (
              <div style={{ flex: 1, overflowY: 'auto', padding: '6px 0' }}>
                {links.map(l => <LinkRow key={l.url} link={l} />)}
              </div>
            )}

            {activeKey === 'context' && personaId && (
              <div style={{ flex: 1, overflowY: 'auto', padding: '6px 0' }}>
                <PersonaContextTab personaId={personaId} sessionId={sessionId} />
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}
