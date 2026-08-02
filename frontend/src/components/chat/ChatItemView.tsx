import { memo, useState, useContext, useEffect } from 'react';
import { SquareCheck, SquarePen, Check, Copy, AlertCircle, RotateCcw, AlertTriangle, X, Brain, Clock, ScrollText } from 'lucide-react';
import type { ChatItem, Persona, ProviderFallbackOption } from '../../types';
import { splitFallbackOptions, formatSubscriptionMeta } from '../../lib/providerLimit';
import type { TodoItem } from '../../hooks/useSessionArtifacts';
import type { Mode } from '../../lib/modes';
import { C, FONT, SHADOW, R } from '../../lib/design';
import { useModelLabel } from '../../lib/models';
import { formatPostTime, formatPostTimeFull } from '../../lib/postTime';
import { relPathTree, stripRootTree } from '../../lib/paths';
import { hasUltraworkKeyword } from '../../lib/ultrawork';
import { detectTeamMechanic, describeTeamTurn } from '../../features/team/teamMechanics';
import { TeamTurnRequest } from '../../features/team/TeamTurnCard';
import { ChatProjectContext, ChatTreePathContext, ChatSessionContext, PersonaContext, useAssistantName } from './contexts';
import { PromptSnapshotDialog } from '../../features/chat/PromptSnapshotDialog';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { AGENT_COLORS } from '../AgentSelector';
import { MessageOriginChip } from '../MessageOriginChip';
import { getPersonaById, usePersonasVersion, personaLabel, ensurePersonasLoaded } from '../../lib/personas';
import { IconNotes } from '../../features/notes/shared';
import { saveChatNote, openNoteById } from '../../features/notes/saveToNote';
import { MarkdownContent } from './MarkdownContent';
import { CollapsibleMarkdownBody } from './AgentContentBlocks';
import { parseDelegationReport } from '../../lib/delegationReport';
import { ToolUseView } from './ToolUseView';
import { PersonaAskView, isPersonaAsk } from './PersonaAskView';
import { PersonaTaskView, isAgentToolUse } from './PersonaTaskView';
import { WidgetView, isWidgetShow } from './WidgetView';
import { TaskCreatedView, isTasksCreate } from './TaskCreatedView';
import type { ActivityEntry } from './timeline';
import { AskQuestionView } from './AskQuestionView';
import { PlanReviewView } from './PlanReviewView';
import { TeamPlanView } from './TeamPlanView';
import { TeamEscalationView } from './TeamEscalationView';

// Разбор input инструмента TodoWrite → пункты чек-листа (каждый вызов несет полный список)
function parseTodoWriteInput(input: unknown): TodoItem[] {
  const t = (input as { todos?: unknown } | null)?.todos;
  return Array.isArray(t) ? (t as TodoItem[]) : [];
}

// Карточка плана задач — закрепленный чек-лист с прогрессом. Источник списка:
// input TodoWrite либо агрегат TaskCreate/TaskUpdate (computeTodos)
function TodoPlanView({ todos }: { todos: TodoItem[] }) {
  if (todos.length === 0) return null;
  const done = todos.filter(t => t.status === 'completed').length;

  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderRadius: 12, background: C.bgWhite,
      overflow: 'hidden', boxShadow: SHADOW.card,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '10px 13px', borderBottom: `1px solid ${C.divider}` }}>
        <SquareCheck size={15} color={C.accent} strokeWidth={2} style={{ flexShrink: 0 }} />
        <span style={{ fontFamily: FONT.serif, fontSize: 14, fontWeight: 700, color: C.textHeading }}>План</span>
        <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>
          {done}/{todos.length}
        </span>
      </div>
      <div style={{ padding: '7px 13px 10px', display: 'flex', flexDirection: 'column', gap: 1 }}>
        {todos.map((t, i) => {
          const isDone = t.status === 'completed';
          const isActive = t.status === 'in_progress';
          const label = isActive && t.activeForm ? t.activeForm : t.content;
          return (
            <div key={i} style={{ display: 'flex', alignItems: 'flex-start', gap: 9, padding: '4px 0' }}>
              <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}>
                {isDone ? (
                  <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
                    <circle cx="8" cy="8" r="8" fill={C.success} />
                    <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke={C.onAccent} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
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
                fontSize: 13, lineHeight: 1.4,
                color: isDone ? C.textMuted : isActive ? C.textHeading : C.textSecondary,
                textDecoration: isDone ? 'line-through' : 'none',
                fontWeight: isActive ? 600 : 400,
              }}>
                {label}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// Карточка запроса разрешения. Пока решения нет — акцентная карточка с кнопками;
// после решения сворачивается в компактную строку с вердиктом, содержимое
// (команда/путь) раскрывается по клику.
function PermissionRequestView({ item, online, onAllow, onDeny, onAllowAlways }: {
  item: Extract<ChatItem, { kind: 'permission_request' }>;
  online: boolean;
  onAllow: (requestId: string) => void;
  onDeny: (requestId: string) => void;
  onAllowAlways: (requestId: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const project = useContext(ChatProjectContext);
  const treePath = useContext(ChatTreePathContext);
  const asstName = useAssistantName();

  // Что именно собирается выполнить Claude — команда/путь/аргументы
  const detail = (() => {
    const inp = item.toolInput as Record<string, unknown> | null;
    if (!inp) return '';
    if (typeof inp.command === 'string') return stripRootTree(inp.command, project?.rootPath, treePath);
    if (typeof inp.file_path === 'string') return relPathTree(inp.file_path, project?.rootPath, treePath);
    if (typeof inp.path === 'string') return relPathTree(inp.path, project?.rootPath, treePath);
    try { const s = JSON.stringify(inp, null, 2); return s === '{}' ? '' : s; } catch { return ''; }
  })();
  // Консольная команда (Bash/shell) → тёмный «терминал»; прочее (путь файла и т.п.) → светлая панель
  const pn = item.toolName.toLowerCase();
  const isConsoleReq = pn.startsWith('bash') || pn.includes('shell');
  const detailBlock = (
    <div style={{
      background: isConsoleReq ? C.termBg : C.outputBg,
      border: isConsoleReq ? 'none' : `1px solid ${C.outputBorder}`,
      borderRadius: 7, padding: '8px 11px',
      color: isConsoleReq ? C.termText : C.textPrimary, fontFamily: FONT.mono,
      fontSize: 12, lineHeight: 1.5,
      whiteSpace: 'pre-wrap', wordBreak: 'break-word', maxHeight: 180, overflow: 'auto',
    }}>
      {detail || item.toolName}
    </div>
  );

  if (item.resolved) {
    const denied = item.decision === 'denied';
    const verdict = item.decision === 'allowed' ? 'разрешено'
      : item.decision === 'always' ? 'разрешено всегда'
      : denied ? 'отклонено' : 'решение принято';
    return (
      <div style={{ border: `1px solid ${C.borderLight}`, borderRadius: 12, background: C.bgWhite }}>
        <div
          onClick={() => setOpen(o => !o)}
          style={{ display: 'flex', alignItems: 'center', gap: 7, padding: '7px 12px', cursor: 'pointer', userSelect: 'none' as const }}
        >
          {denied
            ? <X size={13} color={C.danger} strokeWidth={2.5} style={{ flexShrink: 0 }} />
            : <Check size={13} color={item.decision ? C.success : C.textMuted} strokeWidth={2.5} style={{ flexShrink: 0 }} />}
          <span style={{ flex: 1, fontSize: 12, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            Разрешение: <span style={{ fontWeight: 600, color: C.textPrimary }}>{item.toolName}</span>
            {' — '}{verdict}
          </span>
          <span style={{
            display: 'inline-block', fontSize: 10, color: C.textMuted, flexShrink: 0,
            transform: open ? 'rotate(0deg)' : 'rotate(-90deg)', transition: 'transform 0.15s',
          }}>▾</span>
        </div>
        {open && <div style={{ padding: '0 12px 10px' }}>{detailBlock}</div>}
      </div>
    );
  }

  return (
    <div style={{
      border: `1px solid ${C.accentMuted}`, borderLeft: `3px solid ${C.accent}`,
      borderRadius: 12, padding: '13px 14px', background: C.accentLight,
    }}>
      <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4, color: C.textHeading }}>
        Запрос разрешения
      </div>
      <div style={{ fontSize: 12, color: C.textSecondary, marginBottom: 10 }}>
        {asstName} хочет выполнить <span style={{ fontWeight: 600 }}>{item.toolName}</span>:
      </div>
      <div style={{ marginBottom: 12 }}>{detailBlock}</div>
      {online ? (
        <>
          <div style={{ display: 'flex', gap: 8 }}>
            <button
              onClick={() => onAllow(item.requestId)}
              style={{
                flex: 1, background: C.accent, color: C.onAccent,
                borderRadius: 9, padding: 9, border: 'none',
                cursor: 'pointer', fontSize: 13, fontWeight: 600,
              }}
            >
              Разрешить
            </button>
            <button
              onClick={() => onDeny(item.requestId)}
              style={{
                flex: 1, background: C.bgWhite, border: `1px solid ${C.border}`,
                color: C.textSecondary, borderRadius: 9, padding: 9,
                cursor: 'pointer', fontSize: 13,
              }}
            >
              Отклонить
            </button>
          </div>
          <button
            onClick={() => onAllowAlways(item.requestId)}
            style={{
              marginTop: 8, width: '100%', background: 'none', border: 'none',
              cursor: 'pointer', fontSize: 12, color: C.accent, padding: '4px 0',
            }}
          >
            Всегда разрешать «{item.toolName}» в этом чате
          </button>
        </>
      ) : (
        <div style={{ fontSize: 12, color: C.textMuted }}>Недоступно офлайн</div>
      )}
    </div>
  );
}

// Компактная строка изменённого файла — для использования внутри общего контура
// блока действий (рядом с карточками инструментов). Один ритм со строкой ToolUseView.
export const FileChangedRow = memo(function FileChangedRow({ item, online, onOpenFile, onRevert }: {
  item: Extract<ChatItem, { kind: 'file_changed' }>;
  online: boolean;
  onOpenFile?: (path: string) => void;
  onRevert?: (path: string) => void;
}) {
  const project = useContext(ChatProjectContext);
  const treePath = useContext(ChatTreePathContext);
  const relativePath = relPathTree(item.path, project?.rootPath, treePath);
  return (
    <div style={{ padding: '3px 0', display: 'flex', alignItems: 'center', gap: 10 }}>
      <span style={{ display: 'flex', alignItems: 'center', gap: 5, flexShrink: 0, color: C.accent }}>
        <SquarePen size={13} strokeWidth={2} style={{ flexShrink: 0 }} />
      </span>
      <span onClick={() => onOpenFile?.(item.path)}
        style={{ fontFamily: FONT.mono, fontSize: 12.5, flex: 1, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', cursor: onOpenFile ? 'pointer' : 'default', direction: 'rtl', textAlign: 'left' }}>
        {relativePath}
      </span>
      <span style={{ fontSize: 11.5, color: C.diffAddText, fontFamily: FONT.mono, flexShrink: 0 }}>+{item.added}</span>
      <span style={{ fontSize: 11.5, color: C.diffRemText, fontFamily: FONT.mono, flexShrink: 0 }}>-{item.removed}</span>
      {online && onRevert && (
        <button onClick={() => onRevert(item.path)}
          style={{ fontSize: 11, padding: '2px 8px', borderRadius: 6, border: `1px solid ${C.border}`, background: C.bgWhite, cursor: 'pointer', color: C.dangerText, flexShrink: 0 }}>
          Откатить
        </button>
      )}
    </div>
  );
});

// Насколько строка действий опущена под пузырь. Тем же числом посты с этой строкой
// раздвигаются по вертикали (POST_BAR_GAP), иначе всплывающая строка ложилась бы
// на следующий пост — лента идёт с gap 2px.
const POST_BAR_H = 20;
// Зазор чуть меньше высоты строки: она заезжает в поле соседнего поста на пару
// пикселей, но всплывает поверх (zIndex) и читаться это не мешает
const POST_BAR_GAP = POST_BAR_H - 5;

// Строка под постом: сначала действия, потом модель, потом время. Ни фона, ни рамки —
// это подпись к посту, а не элемент управления: заметной она становится только под
// курсором. Держится оверлеем (`position:absolute` ПОД пузырём), поэтому места в ленте
// не занимает и посты не раздвигает.
// align — с какой стороны прижата: ответы ассистента идут слева, пузыри человека справа.
function PostActionBar({ align = 'left', children }: {
  align?: 'left' | 'right'; children?: React.ReactNode;
}) {
  return (
    <div className="cc-actions" style={{
      position: 'absolute', bottom: -POST_BAR_H, [align]: 4, zIndex: 2,
      display: 'flex', alignItems: 'center', gap: 7,
      // Ширина по содержимому, а не по пузырю: панель — оверлей, места в ленте не
      // занимает, а «100%» резало подпись у коротких сообщений («16 мин наз…»).
      // Прижата к своему краю, поэтому растёт внутрь ленты, а не за её пределы.
      width: 'max-content', maxWidth: '92vw', whiteSpace: 'nowrap',
    }}>
      {children}
    </div>
  );
}

// Мета поста — модель и время простым текстом. Оба слагаемых необязательны: старая
// история их не несёт, и тогда под постом остаются одни действия.
// Имя модели — вход в шторку «что она получила»: отдельная иконка в панели действий
// была лишней, а связка «модель → её контекст» читается сама собой. Без имени модели
// (старая история) вместо него показывается иконка.
function PostMeta({ model, ts, promptSnapshotId, turnContextTokens, turnCache }: {
  model?: string; ts?: number; promptSnapshotId?: string;
  turnContextTokens?: number | null;
  turnCache?: { read: number; creation: number } | null;
}) {
  const label = useModelLabel(model);
  const time = formatPostTime(ts);
  const timeFull = formatPostTimeFull(ts);
  const sessionId = useContext(ChatSessionContext);
  const [snapshotOpen, setSnapshotOpen] = useState(false);
  const canOpen = !!(promptSnapshotId && sessionId);
  if (!model && !time && !canOpen) return null;
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 6, minWidth: 0,
      fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap',
    }}>
      {model && (canOpen ? (
        <button onClick={() => setSnapshotOpen(true)}
          title={`${label} — показать, что она получила`}
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 3, minWidth: 0,
            background: 'none', border: 'none', padding: 0, cursor: 'pointer',
            fontFamily: 'inherit', fontSize: 'inherit', color: 'inherit',
          }}
          {...postIconHover}>
          <Brain size={12} strokeWidth={2} style={{ flexShrink: 0 }} />
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{label}</span>
        </button>
      ) : (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 3, minWidth: 0 }}
          title={`Модель этого ответа: ${label}`}>
          <Brain size={12} strokeWidth={2} style={{ flexShrink: 0 }} />
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{label}</span>
        </span>
      ))}
      {/* Модель неизвестна (история до появления поля) — вход остаётся иконкой */}
      {!model && canOpen && (
        <button onClick={() => setSnapshotOpen(true)} style={postIconBtn}
          title="Что получила модель" aria-label="Что получила модель" {...postIconHover}>
          <ScrollText size={13} strokeWidth={2} style={{ flexShrink: 0 }} />
        </button>
      )}
      {snapshotOpen && sessionId && promptSnapshotId && (
        <PromptSnapshotDialog sessionId={sessionId} snapshotId={promptSnapshotId}
          contextTokens={turnContextTokens} turnCache={turnCache}
          onClose={() => setSnapshotOpen(false)} />
      )}
      {time && (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 3 }}
          title={timeFull ?? undefined}>
          <Clock size={12} strokeWidth={2} style={{ flexShrink: 0 }} />
          {time}
        </span>
      )}
    </span>
  );
}

// Кнопка в строке действий поста — общая для ответа ассистента и пузыря человека.
// Без заливки и рамки: подсветка только цветом иконки, чтобы строка читалась как
// подпись, а не как ряд кнопок.
const postIconBtn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
  width: 20, height: 20, borderRadius: 5, border: 'none', background: 'none',
  color: C.textMuted, cursor: 'pointer', fontFamily: 'inherit', padding: 0,
};

// Наведение подсвечивает саму иконку (заливки нет)
const postIconHover = {
  onMouseEnter: (e: React.MouseEvent<HTMLButtonElement>) => { e.currentTarget.style.color = C.textHeading; },
  onMouseLeave: (e: React.MouseEvent<HTMLButtonElement>) => { e.currentTarget.style.color = C.textMuted; },
};

// Тап по посту на мобиле раскрывает панель действий (на десктопе — hover, это CSS).
// Тап по ссылке/кнопке/коду и тап, завершающий выделение текста, не считаем — иначе
// панель дёргалась бы при обычном чтении и копировании.
function usePostTap() {
  const [tapped, setTapped] = useState(false);
  const handleTap = (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest('a, button, input, textarea, pre, code')) return;
    if (window.getSelection()?.toString()) return;
    setTapped(t => !t);
  };
  return { tapped, handleTap };
}

// Кнопка «Скопировать» с отметкой об успехе — одинаково нужна обоим видам постов
function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false);
  const copy = () => {
    navigator.clipboard?.writeText(text)
      .then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); })
      .catch(() => {});
  };
  return (
    <button onClick={copy} style={postIconBtn} title={copied ? 'Скопировано' : label} aria-label={label}
      {...postIconHover}>
      {copied
        ? <Check size={14} color={C.success} strokeWidth={3} style={{ flexShrink: 0 }} />
        : <Copy size={13} strokeWidth={2} style={{ flexShrink: 0 }} />}
    </button>
  );
}

// Пузырь сообщения человека: та же панель по hover, но короче — копирование и время
// (модель к своему сообщению отношения не имеет). Вынесен из switch, потому что
// копирование и тап держат состояние, а хуки внутри case недопустимы.
// Кнопки «что получила модель» тут нет намеренно: она описывает ход целиком и живёт
// под ответом — там вопрос «на основании чего это написано» и возникает
function UserMessageBubble({ text, ts, children }: {
  text: string; ts?: number;
  children: React.ReactNode;
}) {
  const { tapped, handleTap } = usePostTap();
  return (
    <div className={`cc-msg${tapped ? ' cc-msg--tapped' : ''}`} onClick={handleTap}
      style={{
        position: 'relative', maxWidth: '100%', marginBottom: POST_BAR_GAP,
        background: C.bgWhite, color: C.textPrimary,
        border: `1px solid ${C.borderLight}`, boxShadow: SHADOW.card,
        borderRadius: '18px 18px 4px 18px', padding: '12px 17px', fontSize: 14,
      }}>
      {children}
      {/* Прижата вправо — по стороне, с которой стоит сам пузырь человека */}
      <PostActionBar align="right">
        <CopyButton text={text} label="Скопировать сообщение" />
        <PostMeta ts={ts} />
      </PostActionBar>
    </div>
  );
}

// Ответ ассистента. Панель «мета + Копировать/В заметку/Повторить» — оверлеем у нижней
// кромки: десктоп — fade-in по hover на сообщении, мобайл (тач) — по тапу.
function TextMessageView({ text, online, onRetry, streaming, model, ts, promptSnapshotId, turnContextTokens, turnCache }: {
  text: string; online: boolean; onRetry: () => void; streaming?: boolean;
  model?: string; ts?: number; promptSnapshotId?: string; turnContextTokens?: number | null;
  turnCache?: { read: number; creation: number } | null;
}) {
  // «В заметку»: сохранение ответа в базу заметок (проект → notes/, чат → personal)
  const project = useContext(ChatProjectContext);
  const [savedNoteId, setSavedNoteId] = useState<string | null>(null);
  const [savingNote, setSavingNote] = useState(false);
  const [noteError, setNoteError] = useState(false);
  const saveNote = () => {
    if (savingNote || savedNoteId) return;
    setSavingNote(true);
    setNoteError(false);
    saveChatNote({ text, projectId: project?.id })
      .then(n => { setSavedNoteId(n.id); setTimeout(() => setSavedNoteId(null), 6000); })
      .catch(() => { setNoteError(true); setTimeout(() => setNoteError(false), 3000); })
      .finally(() => setSavingNote(false));
  };
  const iconBtn = postIconBtn;
  const { tapped, handleTap } = usePostTap();
  return (
    // Обёртка нужна, чтобы строка действий висела ПОД пузырём: у самого пузыря
    // overflow:hidden (обрезает контент), и вылезающую наружу строку он бы срезал
    <div className={`cc-msg${tapped ? ' cc-msg--tapped' : ''}`} onClick={handleTap}
      style={{ position: 'relative', maxWidth: '100%', marginBottom: streaming ? 0 : POST_BAR_GAP }}>
      <div style={{
        display: 'flex', flexDirection: 'column', gap: 6, maxWidth: '100%', overflow: 'hidden',
        // Полупрозрачный «лист» под ответом: лента лежит на фоне с дудл-паттерном,
        // и без подложки рисунок лез бы прямо под текст
        background: C.msgBg, borderRadius: R.xl, padding: '9px 12px',
      }}>
        {/* data-selection-doc: Ctrl+A в чате выделяет последний ответ ассистента (см. selectionScope) */}
        <div data-selection-doc="" style={{ fontSize: 14, color: C.textHeading, wordBreak: 'break-word' }}>
          <MarkdownContent text={text} />
          {/* Мигающая каретка стриминга (B2) */}
          {streaming && <span style={{ display: 'inline-block', width: 7, height: 15, marginTop: 3, borderRadius: 1, background: C.accent, animation: 'blink 1s step-start infinite', verticalAlign: 'text-bottom' }} />}
        </div>
      </div>
      {/* Строка под постом: действия, затем модель и время. Оверлей, места не занимает */}
      {!streaming && (
        <PostActionBar>
          <CopyButton text={text} label="Скопировать ответ" />
          {online && (
            <>
              {savedNoteId && (
                <button onClick={() => openNoteById(savedNoteId)}
                  style={{ ...iconBtn, width: 'auto', padding: '0 8px', fontSize: 11, fontWeight: 600, color: C.successText }}
                  title="Открыть созданную заметку">
                  Открыть
                </button>
              )}
              <button onClick={saveNote} disabled={savingNote} style={{ ...iconBtn, opacity: savingNote ? 0.5 : 1 }}
                title={noteError ? 'Не удалось сохранить' : savedNoteId ? 'Сохранено в заметки' : 'Сохранить в заметку'}
                aria-label="Сохранить в заметку"
                {...postIconHover}>
                {savedNoteId
                  ? <Check size={14} color={C.success} strokeWidth={3} style={{ flexShrink: 0 }} />
                  : noteError
                    ? <AlertCircle size={13} color={C.dangerText} strokeWidth={2} style={{ flexShrink: 0 }} />
                    : <IconNotes size={13} />}
              </button>
            </>
          )}
          {online && (
            <button onClick={onRetry} style={iconBtn} title="Повторить последний запрос" aria-label="Повторить последний запрос"
              {...postIconHover}>
              <RotateCcw size={13} strokeWidth={2} style={{ flexShrink: 0 }} />
            </button>
          )}
          {/* Модель и время — после действий, как подпись к посту */}
          <PostMeta model={model} ts={ts} promptSnapshotId={promptSnapshotId}
            turnContextTokens={turnContextTokens} turnCache={turnCache} />
        </PostActionBar>
      )}
    </div>
  );
}

// Бейдж гостевой реплики-доклада (модель Z): в чате с собеседником A появляется реплика
// от лица персоны-исполнителя B — маркер, чтобы чужое лицо не спуталось с ответом A
// (ADR-001, инвариант-риск гостевой реплики).
function DelegationReportBadge({ title }: { title: string }) {
  return (
    <div style={{
      display: 'inline-flex', alignItems: 'center', gap: 6, alignSelf: 'flex-start',
      maxWidth: '100%', background: C.accentLight, border: `1px solid ${C.accentMuted}`,
      borderRadius: R.max, padding: '3px 11px', fontSize: 11, fontWeight: 600, color: C.accent,
    }}>
      <span style={{ flexShrink: 0 }}>↩</span>
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        Отчёт по делегированной задаче: <span style={{ color: C.textHeading }}>{title}</span>
      </span>
    </div>
  );
}

// Нейтральный акцент для агента без персоны (как в PersonaAskView)
// eslint-disable-next-line design/no-raw-color -- значение клеится с альфой (`${accent}17`)
const AGENT_NEUTRAL = '#8A8070';

// Сообщение с источником (не от человека): входящее от персоны через chats_send, либо
// авто-публикация — совещание/конвейер/задача. Карточка с лицом автора (персона или
// стандартный значок) и телом в Markdown — вместо безликого пузыря пользователя.
function AgentMessageView({ text, persona, neutralTitle = 'Агент', note, origin }: {
  text: string; persona: Persona | null; neutralTitle?: string; note?: string;
  // Откуда прилетело, если из другого места (чужой проект / вне проектов) — чип в шапке.
  // Считает сервер: у сообщения из того же контекста поля нет и чип не рисуется.
  origin?: string;
}) {
  // В не-персон-чате стор мог быть не загружен — подтягиваем, чтобы резолвить лицо автора
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const accent = persona ? (AGENT_COLORS[persona.avatar?.color ?? ''] ?? AGENT_NEUTRAL) : AGENT_NEUTRAL;
  const title = persona ? personaLabel(persona) : neutralTitle;
  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${accent}`,
      borderRadius: 12, background: C.bgWhite, overflow: 'hidden',
      boxShadow: SHADOW.card, maxWidth: '100%',
    }}>
      {/* Шапка идентичности — лёгкая подложка цвета отправителя */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 9, padding: '9px 12px',
        background: `${accent}17`, borderBottom: `1px solid ${C.divider}`,
      }}>
        {persona ? (
          <PersonaAvatar persona={persona} size={28} />
        ) : (
          <div aria-hidden style={{
            width: 28, height: 28, borderRadius: '50%', flexShrink: 0,
            background: AGENT_NEUTRAL, color: C.onDark,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="3" y="11" width="18" height="10" rx="2" /><circle cx="12" cy="5" r="2" /><path d="M12 7v4" /><path d="M8 16h.01M16 16h.01" />
            </svg>
          </div>
        )}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'baseline', gap: 7, flexWrap: 'wrap' }}>
          <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '100%' }}>
            {title}
          </span>
          {persona?.handle && (
            <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>@{persona.handle}</span>
          )}
          {/* Чип-источник: сообщение пришло из другого проекта либо вне проектов */}
          {origin && <MessageOriginChip origin={origin} />}
        </div>
        {note && <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0 }}>{note}</span>}
      </div>
      {/* Тело — Markdown; длинная постановка задачи сворачивается тем же механизмом,
          что и ответ консультации, иначе карточка хоронит ленту под инструкциями */}
      <CollapsibleMarkdownBody text={text} accent={accent} padding="10px 12px" />
    </div>
  );
}

interface ItemProps {
  item: ChatItem;
  index: number;
  online: boolean;
  streaming?: boolean;
  isLastResult?: boolean;
  onToggleThinking: (i: number) => void;
  onAllowPermission: (id: string) => void;
  onDenyPermission: (id: string) => void;
  onAllowAlways: (id: string) => void;
  onAnswerQuestion: (toolUseId: string, answerText: string) => void;
  onRespondPlan: (requestId: string, approve: boolean, feedback?: string) => void;
  planVersion?: number;
  planShowBadge?: boolean;
  planShowSwitch?: boolean;
  onSwitchMode: (mode: Mode) => void;
  onOpenFile?: (path: string) => void;
  onRevert?: (path: string) => void;
  onRetry: () => void;
  onInterrupt: () => void;
  // Миграция чата на другого провайдера (карточка «Продолжить на …» при исчерпании лимита)
  onMigrateProvider?: (model: string, subscriptionKey?: string) => Promise<void>;
  // Агрегированный чек-лист TaskCreate/TaskUpdate — приходит только на последний task-вызов ленты
  taskPlan?: TodoItem[];
  // Снимок промпта хода, к которому относится этот пост, и размер контекста его запроса —
  // оба считает ChatPanel скользящим окном по ленте (у поста ассистента своего id нет).
  // undefined — история до появления снимков либо пост сабагента: кнопки не будет
  promptSnapshotId?: string;
  turnContextTokens?: number | null;
  turnCache?: { read: number; creation: number } | null;
  // Вложенная активность сабагента-персоны (дочерние tool_use/text/thinking с индексами) —
  // рендерится секцией внутри карточки консультации (передаёт ChatPanel только для
  // персона-вызовов Task)
  agentActivity?: ActivityEntry[];
  // Универсальный рендер элементов активности (renderItem из ChatPanel) —
  // чтобы внутри секции работали ВСЕ возможности обычной ленты
  agentRenderChild?: (item: ChatItem, idx: number) => React.ReactNode;
  // Для kind='session_started': считает ChatPanel (sessionStartedBoundaries) — 'entered'
  // рисует разделитель «ход в дереве агента», 'returned' — «ход вернулся в проект»;
  // undefined — этот session_started прозрачен, рендерится в null (как сегодня)
  turnBoundaryKind?: 'entered' | 'returned';
}

// React.memo: переключатель по kind — самый массовый компонент ленты. Элементы ChatItem
// иммутабельны по ссылке (обновление элемента = новый объект), пропсы-функции стабильны
// (useCallback в ChatPanel) — при дописывании ленты старые элементы не перерендериваются.
// Карточка «Лимит исчерпан — продолжить чат»: две секции опций — сначала здоровые
// аккаунты того же пула подписок (kind='subscription', та же модель, своя предоплата),
// затем сторонние провайдеры (модель сменится, оплата с их баланса). Клик мигрирует чат
// (транскрипт переезжает, контекст сохраняется); у опции пула передаём её key как
// subscriptionKey — явный выбор вместо автовыбора пулом. После миграции карточка
// гаснет по provider_switched (resolved в chatReducer).
// export — для dev-витрины UiKitPage (демо обеих секций без живого лимита)
export function ProviderLimitCard({ item, online, onMigrate }: {
  item: Extract<ChatItem, { kind: 'provider_limit' }>;
  online: boolean;
  onMigrate?: (model: string, subscriptionKey?: string) => Promise<void>;
}) {
  // Busy по ключу опции, не по модели: у аккаунтов пула модель одна и та же
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  if (item.resolved || item.providers.length === 0) return null;

  const { subscriptions, providers } = splitFallbackOptions(item.providers);

  let when = '';
  if (item.resetsAt) {
    const dt = new Date(item.resetsAt);
    if (!isNaN(dt.getTime())) {
      const sameDay = dt.toDateString() === new Date().toDateString();
      const hhmm = dt.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
      when = sameDay
        ? `сбросится в ${hhmm}`
        : `сбросится ${dt.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })} в ${hhmm}`;
    }
  }

  const migrate = async (option: ProviderFallbackOption) => {
    if (!onMigrate || busyKey) return;
    setBusyKey(option.key);
    setError(null);
    try {
      await onMigrate(option.model, option.kind === 'subscription' ? option.key : undefined);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось переключить чат');
    } finally {
      setBusyKey(null);
    }
  };

  const optionButtonStyle = (key: string): React.CSSProperties => ({
    padding: '5px 12px', borderRadius: 8, border: `1px solid ${C.warning}`,
    background: C.bgWhite, color: C.textHeading, fontSize: 12.5, fontWeight: 600,
    cursor: !online || busyKey ? 'default' : 'pointer',
    opacity: !online || (busyKey !== null && busyKey !== key) ? 0.55 : 1,
    fontFamily: 'inherit',
  });

  return (
    <div style={{
      alignSelf: 'center', maxWidth: '100%',
      background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: 10,
      padding: '10px 14px', fontSize: 12.5, color: C.warningText,
      display: 'flex', flexDirection: 'column', gap: 8,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span>⏳</span>
        <span>
          Лимит подписки исчерпан{when ? <span style={{ opacity: 0.75 }}> · {when}</span> : null}.
          {subscriptions.length > 0
            ? ' Можно продолжить этот чат на другом аккаунте пула или стороннем провайдере — контекст сохранится.'
            : ' Можно продолжить этот чат на другом провайдере — контекст сохранится.'}
        </span>
      </div>
      {subscriptions.length > 0 && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          {subscriptions.map(p => {
            const meta = formatSubscriptionMeta(p);
            return (
              <button
                key={p.key}
                onClick={() => void migrate(p)}
                disabled={!online || !onMigrate || busyKey !== null}
                style={{ ...optionButtonStyle(p.key), display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: 1 }}
              >
                {busyKey === p.key ? 'Переключаю…' : (
                  <>
                    <span>Продолжить на «{p.displayName}»</span>
                    {meta && <span style={{ fontSize: 11, fontWeight: 400, opacity: 0.7 }}>{meta}</span>}
                  </>
                )}
              </button>
            );
          })}
          <span style={{ fontSize: 11.5, color: C.textMuted }}>
            Та же модель — расходуется выбранный аккаунт
          </span>
        </div>
      )}
      {providers.length > 0 && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          {providers.map(p => (
            <button
              key={p.key}
              onClick={() => void migrate(p)}
              disabled={!online || !onMigrate || busyKey !== null}
              style={optionButtonStyle(p.key)}
            >
              {busyKey === p.key ? 'Переключаю…' : `Продолжить на ${p.displayName}`}
            </button>
          ))}
          <span style={{ fontSize: 11.5, color: C.textMuted }}>
            Оплата — с баланса провайдера, модель сменится
          </span>
        </div>
      )}
      {error && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, color: C.dangerText, fontSize: 12 }}>
          <AlertCircle size={13} style={{ flexShrink: 0 }} />
          {error}
        </div>
      )}
    </div>
  );
}

export const ChatItemView = memo(function ChatItemView({ item, index, online, streaming, isLastResult, onToggleThinking, onAllowPermission, onDenyPermission, onAllowAlways, onAnswerQuestion, onRespondPlan, planVersion, planShowBadge, planShowSwitch, onSwitchMode, onOpenFile, onRevert, onRetry, onInterrupt, onMigrateProvider, taskPlan, agentActivity, agentRenderChild, turnBoundaryKind, promptSnapshotId, turnContextTokens, turnCache }: ItemProps) {
  const project = useContext(ChatProjectContext);
  const treePath = useContext(ChatTreePathContext);
  const persona = useContext(PersonaContext);
  const asstName = useAssistantName();
  // Подписка на стор персон: авторские аватары реплик (personaId) обновятся после загрузки стора
  usePersonasVersion();
  switch (item.kind) {
    case 'user_message': {
      // Служебный ход механики штаба (ответ на карточку, возврат в интервью, сводка волны) —
      // компактная плашка-разделитель вместо пузыря «Автоматически» с сырым текстом директивы
      if (item.staffNote) {
        return (
          <div style={{ alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 8, maxWidth: '100%' }}>
            <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
            <span style={{
              fontSize: 12, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden',
              textOverflow: 'ellipsis', padding: '3px 10px', borderRadius: 999,
              background: C.bgSelected, border: `1px solid ${C.border}`,
            }}>
              ⚑ {item.staffNote}
            </span>
            <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
          </div>
        );
      }
      // Служебная директива цикла «до готово» (continuation/verification) — компактная
      // плашка-разделитель вместо сырого текста в пузыре пользователя
      if (item.systemDirective) {
        const mm = /(\d+)\s*\/\s*(\d+)/.exec(item.text);
        const label = /ВЕРИФИКАЦ/i.test(item.text)
          ? 'Цикл: финальная верификация'
          : mm ? `Цикл: продолжение ${mm[1]}/${mm[2]}` : 'Цикл: системная директива';
        return (
          <div style={{ alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 8, maxWidth: '100%' }}>
            <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
            <span style={{
              fontSize: 12, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden',
              textOverflow: 'ellipsis', padding: '3px 10px', borderRadius: 999,
              background: C.bgSelected, border: `1px solid ${C.border}`,
            }}>
              ⟳ {label}
            </span>
            <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
          </div>
        );
      }
      // Командный ход из раскрывашки «Обсудить с командой»: текст — скилл-команда или
      // промпт-обвязка, поэтому даже auto-отправленный рендерим пузырём пользователя
      // с бейджем механики (пользователь сам его инициировал темой из композера)
      const teamMech = detectTeamMechanic(item.text);
      const teamInfo = teamMech ? describeTeamTurn(item.text) : null;
      // Сообщение не от человека — показываем источник карточкой с лицом автора:
      //  • viaAgent — прислано персоной/агентом из другого чата (chats_send)
      //  • auto — авто-публикация (задача, автоматизация)
      // Персона резолвится по senderPersonaId; иначе — стандартный значок.
      if (item.viaAgent || (item.auto && !teamMech)) {
        const sender = item.senderPersonaId ? (getPersonaById(item.senderPersonaId) ?? null) : null;
        // Без персоны заголовком идёт имя чата-отправителя («Задача: починить билд») —
        // безликие «Агент»/«Автоматически» не отвечали на вопрос «кто пишет»
        return item.viaAgent
          ? <AgentMessageView text={item.text} persona={sender} note="прислал(а) в чат"
              origin={item.senderOrigin} neutralTitle={item.senderChatName ?? 'Агент'} />
          : <AgentMessageView text={item.text} persona={sender}
              origin={item.senderOrigin} neutralTitle={item.senderChatName ?? 'Автоматически'} />;
      }
      return (
        <div style={{ alignSelf: 'flex-end', maxWidth: '80%', display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 3 }}>
          <UserMessageBubble text={item.text} ts={item.ts}
          >
            {teamInfo ? (
              /* Командный ход механики: вместо сырой слэш-команды/JSON — карточка
                 запроса (механика + тема + чипы параметров). Сырой текст остаётся
                 в истории для модели, здесь только слой отображения. */
              <TeamTurnRequest info={teamInfo} ultra={hasUltraworkKeyword(item.text)} />
            ) : (
              <>
                {hasUltraworkKeyword(item.text) && (
                  <div style={{ marginBottom: 5, display: 'flex', gap: 5, flexWrap: 'wrap' }}>
                    <span style={{
                      display: 'inline-flex', alignItems: 'center', gap: 3,
                      background: C.accent, color: C.onAccent, borderRadius: R.pill,
                      padding: '2px 8px', fontSize: 9.5, fontWeight: 700,
                      letterSpacing: 0.6, textTransform: 'uppercase',
                    }}>
                      ⚡ ультра
                    </span>
                  </div>
                )}
                {/* Markdown и в пузыре пользователя: авто-публикуемые сообщения (задачи)
                    приходят в MD — форматируем их; обычный текст рендерится идентично */}
                <div className="cc-user-md">
                  <MarkdownContent text={item.text} />
                </div>
              </>
            )}
            {item.attachedPaths && item.attachedPaths.length > 0 && (
              <div style={{ marginTop: 6, display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                {item.attachedPaths.map(p => (
                  <span key={p} style={{
                    background: C.bgPanel, color: C.textSecondary, borderRadius: 5,
                    padding: '1px 6px', fontSize: 11,
                  }}>
                    {/* В проекте — путь относительно корня; в чате без проекта — только имя файла */}
                    {project ? relPathTree(p, project.rootPath, treePath) : (p.replace(/\\/g, '/').split('/').pop() ?? p)}
                  </span>
                ))}
              </div>
            )}
          </UserMessageBubble>
        </div>
      );
    }

    case 'session_started': {
      // Старт чата обычно не показываем — тех-инфа (модель/режим/cwd/MCP) дублирует шапку
      // и раздувает ленту. Исключение — «граница» дерева хода (turnBoundaryKind, считает
      // ChatPanel): агент внутри хода ушёл в свой git worktree мимо Session.worktreePath,
      // и это стоит отметить в ленте, как и возврат обратно. По образцу compact_boundary
      if (!turnBoundaryKind) return null;
      const entered = turnBoundaryKind === 'entered';
      // Нейтральное время: разделитель — отметка о прошедшем событии истории, а не
      // индикатор текущего состояния (тем занят git-бар, см. ProjectGitBar.turnTree)
      const title = entered
        ? `Дерево агента: ${item.turnWorktree!.path}`
        : (project ? `Корень проекта: ${project.rootPath}` : 'Ход вернулся в корень проекта');
      return (
        <div style={{ alignSelf: 'stretch', display: 'flex', alignItems: 'center', gap: 10, color: C.textMuted, fontSize: 11, margin: '2px 0' }} title={title}>
          <div style={{ flex: 1, height: 1, background: C.border }} />
          <span style={{ display: 'flex', alignItems: 'center', gap: 5, whiteSpace: 'nowrap' }}>
            <span style={{ color: C.textMuted }}>⎇</span>
            {entered
              ? <>ход в дереве агента · <span style={{ fontFamily: FONT.mono }}>{item.turnWorktree!.name}</span></>
              : 'ход вернулся в корень проекта'}
          </span>
          <div style={{ flex: 1, height: 1, background: C.border }} />
        </div>
      );
    }

    case 'text': {
      // Доклад делегированной задачи (модель Z) — гостевая реплика несёт маркер первой
      // строкой; распознаём его и рендерим бейджем, а в тело идёт только сам доклад
      const report = parseDelegationReport(item.text);
      // Пост сабагента кнопки «какой промпт ушёл» не получает: его промпт собирал CLI,
      // а наш снимок описывает ход основного агента
      const msg = (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {report && <DelegationReportBadge title={report.title} />}
          <TextMessageView text={report ? report.body : item.text} online={online} onRetry={onRetry} streaming={streaming}
            model={item.model} ts={item.ts}
            promptSnapshotId={item.parentToolUseId ? undefined : promptSnapshotId}
            turnContextTokens={turnContextTokens} turnCache={turnCache} />
        </div>
      );
      // В персон-чате слева от реплики ассистента — её аватар (главный сигнал «говорит она»).
      // Авторство реплики (personaId из истории) главнее текущей персоны чата: после
      // смены собеседника старые реплики сохраняют аватар прежней персоны.
      const author = item.personaId ? (getPersonaById(item.personaId) ?? persona) : persona;
      if (author) {
        return (
          <div style={{ display: 'flex', gap: 9, alignItems: 'flex-start' }}>
            <div style={{ flexShrink: 0, marginTop: 1 }}><PersonaAvatar persona={author} size={28} /></div>
            <div style={{ flex: 1, minWidth: 0 }}>{msg}</div>
          </div>
        );
      }
      return msg;
    }

    case 'thinking': {
      const hasText = item.text.trim().length > 0;

      // Завершён без текста — не рендерить
      if (!streaming && !hasText) return null;

      // Стриминг, текст ещё не накоплен — тихий индикатор «клод думает»
      if (streaming && !hasText) {
        return (
          <div style={{ display: 'flex', alignItems: 'center', gap: 5, height: 24, paddingLeft: 2 }}>
            {[0, 1, 2].map(i => (
              <span
                key={i}
                style={{
                  display: 'inline-block', width: 5, height: 5, borderRadius: '50%',
                  background: C.textMuted,
                  animation: `thinkingDot 1.2s ease-in-out ${i * 0.2}s infinite`,
                }}
              />
            ))}
          </div>
        );
      }

      // Есть текст (стриминг или завершён) — компактная collapsible строка
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
          {/* Триггер-строка — одна строчка, без фона/рамки */}
          <div
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 5,
              cursor: 'pointer', userSelect: 'none',
              padding: '2px 0',
              width: 'fit-content',
            }}
            onClick={() => onToggleThinking(index)}
          >
            <span style={{ color: C.textMuted, display: 'flex', alignItems: 'center', flexShrink: 0 }}>
              <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 3a6 6 0 0 0-4 10.5V17h8v-3.5A6 6 0 0 0 12 3z" />
                <path d="M9 20h6M10 22h4" />
              </svg>
            </span>
            <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>
              Размышление
            </span>
            {streaming && (
              <span style={{
                width: 5, height: 5, borderRadius: '50%', background: C.textMuted,
                animation: 'thinkingDot 1.2s ease-in-out infinite', flexShrink: 0,
              }} />
            )}
            {!streaming && (
              <span title="приблизительно, по объёму текста" style={{ fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, opacity: 0.7 }}>
                ~{Math.max(1, Math.round(item.text.length / 4))} ток.
              </span>
            )}
            <span style={{
              color: C.textMuted, fontSize: 10, opacity: 0.7,
              transform: item.expanded ? 'rotate(180deg)' : 'rotate(0deg)',
              transition: 'transform 0.2s',
              display: 'inline-block',
            }}>▾</span>
          </div>
          {/* Раскрытое содержимое — левая полоска как у цитаты */}
          {item.expanded && (
            <div style={{
              marginTop: 4,
              paddingLeft: 10,
              borderLeft: `2px solid ${C.borderLight}`,
              fontSize: 11.5, fontStyle: 'italic', lineHeight: 1.65,
              color: C.textMuted,
              whiteSpace: 'pre-wrap',
              fontFamily: FONT.sans,
            }}>
              {item.text}
            </div>
          )}
        </div>
      );
    }

    case 'tool_use':
      // План задач рисуем отдельной карточкой-чек-листом: TodoWrite несет полный список
      // в своем input, для инкрементальных TaskCreate/TaskUpdate агрегат (taskPlan)
      // прокидывает ChatPanel — только на последний task-вызов ленты. Линию-коннектор для
      // дочерних вызовов субагента (parentToolUseId) рисует renderItems — единой полосой.
      if (item.name === 'TodoWrite') return <TodoPlanView todos={parseTodoWriteInput(item.input)} />;
      if (taskPlan) return <TodoPlanView todos={taskPlan} />;
      // Вопрос другой персоне (persona_ask) — фирменная карточка с идентичностью персоны
      if (isPersonaAsk(item.name)) return <PersonaAskView item={item} />;
      // HTML-виджет (widget_show) — рендер в sandbox-iframe
      if (isWidgetShow(item.name)) return <WidgetView item={item} />;
      // Создание задачи (tasks_create) — карточка «Задача создана» с переходом к задаче
      // (ошибка/непарсибельный ответ — фолбэк на ToolUseView внутри)
      if (isTasksCreate(item.name)) return <TaskCreatedView item={item} online={online} onOpenFile={onOpenFile} />;
      // Сабагент Task/Agent: subagent_type = handle персоны → карточка консультации
      // (несовпадение — обычный ToolUseView, фолбэк внутри PersonaTaskView)
      if (isAgentToolUse(item.name))
        return <PersonaTaskView item={item} online={online} onOpenFile={onOpenFile}
          activity={agentActivity} renderChild={agentRenderChild} />;
      return <ToolUseView item={item} online={online} onOpenFile={onOpenFile} />;

    case 'ask_question':
      return <AskQuestionView item={item} online={online} onAnswer={onAnswerQuestion} onInterrupt={onInterrupt} />;

    case 'plan_review':
      return <PlanReviewView item={item} online={online} onRespond={onRespondPlan} version={planVersion} showBadge={planShowBadge} showSwitch={planShowSwitch} onSwitchMode={onSwitchMode} />;

    case 'team_plan':
      // Действия и состояние режима карточка берёт из TeamPlanContext (провайдит ChatPanel)
      return <TeamPlanView item={item} online={online} />;

    case 'team_escalation':
      // Карточка остановки: решение уходит через TeamEscalationContext (провайдит ChatPanel)
      return <TeamEscalationView item={item} online={online} />;

    case 'permission_request':
      return <PermissionRequestView item={item} online={online}
        onAllow={onAllowPermission} onDeny={onDenyPermission} onAllowAlways={onAllowAlways} />;

    case 'git_turn_commit':
      // Документный режим: ход сохранён авто-коммитом — компактная плашка со ссылкой
      return (
        <div
          onClick={() => window.dispatchEvent(new CustomEvent('cc-open-commit', { detail: { projectId: item.projectId, sha: item.sha } }))}
          title="Открыть изменения этого хода"
          style={{
            display: 'flex', alignItems: 'center', gap: 8, padding: '6px 11px',
            borderRadius: 10, cursor: 'pointer', alignSelf: 'flex-start',
            background: C.successBg, color: C.successText,
            fontSize: 12.5, fontFamily: FONT.sans,
          }}
        >
          <span style={{ fontWeight: 600 }}>✓ Изменения сохранены в историю</span>
          <span style={{ fontFamily: FONT.mono, fontSize: 11, opacity: 0.85 }}>{item.sha.slice(0, 7)}</span>
          <span style={{ textDecoration: 'underline' }}>посмотреть</span>
        </div>
      );

    case 'file_changed': {
      const fileName = relPathTree(item.path, project?.rootPath, treePath);
      // Заметка (notes/*.md): подпись «Заметка · …», клик ведёт в раздел «Заметки»
      const isNote = /(^|\/)notes\/[^/]*\.md$/i.test(item.path);
      const noteTitle = item.path.split(/[\\/]/).pop()!.replace(/\.md$/i, '');
      const openNote = () => { sessionStorage.setItem('cc_pending_note_title', noteTitle); window.dispatchEvent(new Event('cc-open-note')); };
      const openAction = isNote ? openNote : (onOpenFile ? () => onOpenFile(item.path) : undefined);
      return (
        <div style={{
          border: `1px solid ${C.borderLight}`, borderRadius: 14, overflow: 'hidden',
          background: C.bgWhite, boxShadow: SHADOW.card,
        }}>
          <div style={{
            display: 'flex', alignItems: 'center', gap: 11,
            padding: '12px 13px', cursor: openAction ? 'pointer' : 'default',
            borderBottom: `1px solid ${C.divider}`,
          }}
            onClick={openAction}
          >
            <div style={{
              width: 28, height: 28, borderRadius: 8,
              background: C.accentLight, color: C.accent,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              flexShrink: 0,
            }}>
              {isNote
                ? <IconNotes size={14} />
                : <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                    <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                  </svg>}
            </div>
            <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {isNote ? `Заметка · ${noteTitle}` : fileName}
            </span>
            <span style={{ fontSize: 11.5, color: C.diffAddText, fontFamily: FONT.mono }}>
              +{item.added}
            </span>
            <span style={{ fontSize: 11.5, color: C.diffRemText, fontFamily: FONT.mono }}>
              -{item.removed}
            </span>
          </div>
          <div style={{ padding: '8px 13px', display: 'flex', gap: 6 }}>
            {openAction && (
              <button
                onClick={openAction}
                style={{
                  fontSize: 12, padding: '4px 10px', borderRadius: 6,
                  border: `1px solid ${C.borderLight}`, background: C.bgWhite, cursor: 'pointer', color: C.textPrimary,
                }}
              >
                {isNote ? 'Открыть заметку' : 'Открыть'}
              </button>
            )}
            {online && onRevert && (
              <button
                onClick={() => onRevert(item.path)}
                style={{
                  fontSize: 12, padding: '4px 10px', borderRadius: 6,
                  border: `1px solid ${C.border}`, background: C.bgWhite,
                  cursor: 'pointer', color: C.dangerText,
                }}
              >
                Откатить
              </button>
            )}
          </div>
        </div>
      );
    }

    case 'result': {
      const ok = item.subtype === 'success';
      // Склонение числительного: 1 шаг, 2 шага, 5 шагов
      const stepWord = (n: number) => {
        const m10 = n % 10, m100 = n % 100;
        if (m10 === 1 && m100 !== 11) return 'шаг';
        if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'шага';
        return 'шагов';
      };
      const fmtTok = (n: number) => n >= 1000 ? (n / 1000).toFixed(n >= 10000 ? 0 : 1) + 'k' : String(n);
      const fmtCost = (c: number) => '$' + (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));
      const u = item.usage;
      const sep = <span style={{ opacity: 0.45 }}>·</span>;

      // Ошибочный итог хода — показываем причину и предлагаем повторить
      if (!ok) {
        const REASONS: Record<string, string> = {
          error_max_turns: 'достигнут лимит ходов',
          error_during_execution: 'сбой во время выполнения',
          error_max_budget_usd: 'исчерпан бюджет',
          error_max_structured_output_retries: 'не удалось получить структурированный ответ',
        };
        // Конкретная причина по api_error_status имеет приоритет над общим subtype
        const apiReason = (status?: string): string | null => {
          if (!status) return null;
          const s = status.toLowerCase();
          if (s.includes('overload')) return 'серверы Anthropic перегружены';
          if (s.includes('rate') || s.includes('429')) return 'превышен лимит запросов к API';
          if (s.includes('credit') || s.includes('billing') || s.includes('payment') || s.includes('402')) return 'проблема с биллингом или кредитами';
          if (s.includes('401') || s.includes('authentication')) return 'ошибка авторизации — проверьте API-ключ';
          if (s.includes('403') || s.includes('permission')) return 'доступ запрещён (403)';
          if (s.includes('404') || s.includes('not_found')) return 'ресурс не найден (404)';
          if (s.includes('529')) return 'сервис временно перегружен (529)';
          if (s.includes('500') || s.includes('internal')) return 'внутренняя ошибка сервера';
          if (s.includes('timeout')) return 'таймаут запроса к API';
          return `ошибка API: ${status}`;
        };
        const reason = apiReason(item.apiErrorStatus) ?? REASONS[item.subtype] ?? `ход завершился с ошибкой (${item.subtype})`;
        return (
          <div style={{
            alignSelf: 'center', maxWidth: '100%',
            background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: 8,
            padding: '8px 12px', fontSize: 12.5, color: C.dangerText,
            display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 9, flexWrap: 'wrap',
          }}>
            <span style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
              <span style={{ fontWeight: 700 }}>✗</span>
              <span>{reason}</span>
              <span style={{ opacity: 0.65, fontFamily: FONT.mono, fontSize: 11 }}>
                · {item.numTurns} {stepWord(item.numTurns)} · {(item.durationMs / 1000).toFixed(1)}с
              </span>
            </span>
            {online && (
              <button
                onClick={onRetry}
                style={{
                  fontSize: 12, padding: '4px 10px', borderRadius: 6,
                  border: `1px solid ${C.dangerBorder}`, background: C.bgWhite, cursor: 'pointer',
                  color: C.dangerText, whiteSpace: 'nowrap', flexShrink: 0,
                }}
              >
                Повторить
              </button>
            )}
          </div>
        );
      }

      // Плашка токенов/времени — только у последнего хода (экономия места); у прошлых скрываем
      if (!isLastResult) return null;

      return (
        <div style={{
          fontSize: 11, color: C.textMuted, alignSelf: 'center',
          background: C.bgSelected, borderRadius: 8, padding: '4px 11px',
          fontFamily: FONT.mono,
          display: 'flex', alignItems: 'center', gap: 7, flexWrap: 'wrap', justifyContent: 'center',
        }}>
          <span style={{ color: ok ? C.success : C.dangerText, fontWeight: 700 }}>{ok ? '✓' : '✗'}</span>
          <span>{item.numTurns} {stepWord(item.numTurns)}</span>
          {sep}
          <span>{(item.durationMs / 1000).toFixed(1)}с</span>
          {u && (u.inputTokens > 0 || u.outputTokens > 0) && (
            <>
              {sep}
              <span title="входные · выходные токены">↑{fmtTok(u.inputTokens)} ↓{fmtTok(u.outputTokens)}</span>
            </>
          )}
          {typeof item.totalCostUsd === 'number' && item.totalCostUsd > 0 && (
            <>
              {sep}
              <span style={{ color: C.accent, fontWeight: 700 }}>{fmtCost(item.totalCostUsd)}</span>
            </>
          )}
          {item.permissionDenials && item.permissionDenials.length > 0 && (
            <>
              {sep}
              <span title={`Запрещено: ${item.permissionDenials.join(', ')}`} style={{ color: C.dangerText, fontWeight: 700 }}>
                ⊘ {item.permissionDenials.length} {item.permissionDenials.length === 1 ? 'запрет' : 'запрета(ов)'}
              </span>
            </>
          )}
        </div>
      );
    }

    case 'rate_limit': {
      // Мягкий лимит API: ход не упал, claude ждёт сброса окна — янтарный информационный баннер
      const TYPES: Record<string, string> = {
        five_hour: '5-часовой лимит',
        seven_day: 'недельный лимит',
        weekly: 'недельный лимит',
      };
      const label = TYPES[item.limitType] ?? (item.limitType ? `лимит (${item.limitType})` : 'лимит запросов');
      // "rejected" — лимит реально достигнут; всё прочее (allowed_warning) — приближение
      const reached = !item.status || item.status === 'rejected';
      const verb = reached ? 'Достигнут' : 'Приближается';
      let when = '';
      if (item.resetsAt) {
        const dt = new Date(item.resetsAt);
        if (!isNaN(dt.getTime())) {
          const sameDay = dt.toDateString() === new Date().toDateString();
          const hhmm = dt.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
          when = sameDay
            ? `сбросится в ${hhmm}`
            : `сбросится ${dt.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })} в ${hhmm}`;
        }
      }
      return (
        <div style={{
          alignSelf: 'center', maxWidth: '100%',
          background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: 8,
          padding: '7px 12px', fontSize: 12.5, color: C.warningText,
          display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', justifyContent: 'center',
        }}>
          <span>⏳</span>
          <span>{verb} {label}{when ? <span style={{ opacity: 0.75 }}> · {when}</span> : null}</span>
        </div>
      );
    }

    case 'compact_boundary': {
      const fmtTok = (nn: number) => nn >= 1000 ? (nn / 1000).toFixed(nn >= 10000 ? 0 : 1) + 'k' : String(nn);
      return (
        <div style={{ alignSelf: 'stretch', display: 'flex', alignItems: 'center', gap: 10, color: C.textMuted, fontSize: 11, margin: '2px 0' }}>
          <div style={{ flex: 1, height: 1, background: C.border }} />
          <span style={{ display: 'flex', alignItems: 'center', gap: 5, whiteSpace: 'nowrap' }}>
            <span style={{ color: C.textMuted }}>✦</span>
            контекст сжат{item.trigger === 'manual' ? ' вручную' : ''}
            {typeof item.preTokens === 'number' && item.preTokens > 0 && (
              <span style={{ opacity: 0.7 }}>
                · {typeof item.postTokens === 'number' && item.postTokens > 0
                  ? `${fmtTok(item.preTokens)} → ${fmtTok(item.postTokens)} токенов`
                  : `было ${fmtTok(item.preTokens)} токенов`}
              </span>
            )}
          </span>
          <div style={{ flex: 1, height: 1, background: C.border }} />
        </div>
      );
    }

    case 'resumed':
      // Разделитель «продолжение чата» убран — декоративный, без полезной нагрузки
      return null;

    case 'companion_switched': {
      // Разделитель смены собеседника/спикера. label задан явно (ручная смена,
      // speaker_changed) либо резолвится по personaId (derived из истории группового чата)
      const switchedTo = item.label
        || (item.personaId ? (() => { const p = getPersonaById(item.personaId!); return p ? personaLabel(p) : null; })() : null)
        || 'другой собеседник';
      return (
        <div style={{ alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 8, maxWidth: '100%' }}>
          <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
          <span style={{
            fontSize: 12, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden',
            textOverflow: 'ellipsis', padding: '3px 10px', borderRadius: 999,
            background: C.bgSelected, border: `1px solid ${C.border}`,
          }}>
            Теперь отвечает: {switchedTo}
          </span>
          <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
        </div>
      );
    }

    case 'provider_switched':
      // Разделитель «Продолжено на …» — явная миграция чата на другого провайдера
      return (
        <div style={{ alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 8, maxWidth: '100%' }}>
          <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
          <span style={{
            fontSize: 12, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden',
            textOverflow: 'ellipsis', padding: '3px 10px', borderRadius: 999,
            background: C.bgSelected, border: `1px solid ${C.border}`,
          }}>
            {item.label}
          </span>
          <div style={{ flex: 1, minWidth: 24, height: 1, background: C.border }} />
        </div>
      );

    case 'provider_limit':
      return <ProviderLimitCard item={item} online={online} onMigrate={onMigrateProvider} />;

    case 'interrupted':
      return (
        <div style={{
          alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'wrap', justifyContent: 'center',
          background: C.bgSelected, border: `1px solid ${C.border}`, borderRadius: 8, padding: '6px 12px', fontSize: 12, color: C.textSecondary,
        }}>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <svg width="11" height="11" viewBox="0 0 24 24" fill={C.textMuted}><rect x="5" y="5" width="14" height="14" rx="2" /></svg>
            Ход остановлен пользователем
          </span>
          {online && (
            <button onClick={onRetry} style={{ fontSize: 12, padding: '3px 10px', borderRadius: 6, border: `1px solid ${C.border}`, background: C.bgWhite, cursor: 'pointer', color: C.textSecondary, whiteSpace: 'nowrap' }}>Повторить</button>
          )}
        </div>
      );

    case 'truncated':
      return (
        <div style={{
          alignSelf: 'center', maxWidth: '100%', display: 'flex', alignItems: 'center', gap: 8,
          background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: 8, padding: '6px 12px',
          fontSize: 12.5, color: C.warningText,
        }}>
          <span>✂</span>
          <span>Ответ обрезан — достигнут лимит токенов в ответе</span>
        </div>
      );

    case 'redacted_thinking':
      return (
        <div style={{
          display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px',
          background: C.bgSelected, border: `1px solid ${C.border}`, borderRadius: 10,
          fontSize: 12.5, fontStyle: 'italic', color: C.textMuted,
        }}>
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" /></svg>
          Размышление скрыто (зашифровано провайдером)
        </div>
      );

    case 'session_ended':
      return (
        <div style={{
          alignSelf: 'center', maxWidth: '100%', display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'wrap', justifyContent: 'center',
          background: C.dangerBg, border: `1px solid ${C.dangerBorder}`, borderRadius: 8, padding: '7px 12px', fontSize: 12.5, color: C.dangerText,
        }}>
          <AlertTriangle size={13} strokeWidth={2} style={{ flexShrink: 0 }} /><span>Сессия прервана — {asstName} завершился неожиданно</span>
          {online && (
            <button onClick={onRetry} style={{ fontSize: 12, padding: '3px 10px', borderRadius: 6, border: `1px solid ${C.dangerBorder}`, background: C.bgWhite, cursor: 'pointer', color: C.dangerText, whiteSpace: 'nowrap' }}>Повторить</button>
          )}
        </div>
      );

    case 'error':
      return (
        <div style={{
          background: C.dangerBg, borderRadius: 8, padding: '8px 12px',
          fontSize: 13, color: C.dangerText, border: `1px solid ${C.dangerBorder}`,
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10,
        }}>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}><AlertTriangle size={13} strokeWidth={2} style={{ flexShrink: 0 }} />{item.text}</span>
          {item.canRetry && online && (
            <button
              onClick={onRetry}
              style={{
                fontSize: 12, padding: '4px 10px', borderRadius: 6,
                border: `1px solid ${C.dangerBorder}`, background: C.bgWhite,
                cursor: 'pointer', color: C.dangerText, whiteSpace: 'nowrap', flexShrink: 0,
              }}
            >
              Повторить
            </button>
          )}
        </div>
      );

    default:
      return null;
  }
});
