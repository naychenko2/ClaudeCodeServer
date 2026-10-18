import { memo, useContext, useEffect, useMemo, useState } from 'react';
import { SquareCheck, ArrowRight, Route } from 'lucide-react';
import type { ChatItem, Task } from '../../types';
import { C, FONT, SHADOW, TB } from '../../lib/design';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { dueLabel, isDueUrgent, openTaskInSection, PRIORITY_LABEL } from '../../lib/tasks';
import { PriorityFlag, MeBadge, ClaudeBadge, LabelChip, SubtaskCheck, CalendarIcon } from '../../features/tasks/bits';
import { usePersonas, ensurePersonasLoaded, personaLabel } from '../../lib/personas';
import { markdownToPlain } from '../../lib/markdownPlain';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { useIsMobile } from '../../lib/breakpoints';
import { focusFeatureStep, getHasPlanCard, openPillMenu } from '../../lib/featureRuns';
import { laneOfChain } from './feature/PipelineFlow';
import { Button } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { ToolUseView } from './ToolUseView';
import { ChatProjectContext, ChatSessionContext } from './contexts';

type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Вызов tasks_create (mcp__tasks__tasks_create) — сравнение по суффиксу, без регистра
export function isTasksCreate(name: string): boolean {
  return name.toLowerCase().endsWith('__tasks_create');
}

// Защитный разбор ответа tasks-server: полный Task JSON или null (фолбэк на ToolUseView)
function parseTask(result: string): Task | null {
  try {
    const t = JSON.parse(result) as { id?: unknown; title?: unknown };
    if (t && typeof t === 'object' && typeof t.id === 'string' && typeof t.title === 'string')
      return t as unknown as Task;
  } catch { /* не JSON — деградация в обычный блок инструмента */ }
  return null;
}

// Геометрия чипа «Шаг N фичи» (макет v1 .featchip): значения вне шкалы SP (gap 6,
// padding 3/9, margin-top 11, minHeight 28) — константы; minHeight мобилы — TB.iconHitMobile (40).
const CHIP_GAP = 6;
const CHIP_PAD_Y = 3;
const CHIP_PAD_X = 9;
const CHIP_MARGIN_TOP = 11;
const CHIP_MIN_H = 28;

// Подпись ячейки сетки деталей
function CellLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: 10, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, marginBottom: 4,
    }}>
      {children}
    </div>
  );
}

const cellValueStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6,
  fontSize: 12.5, fontWeight: 600, color: C.textPrimary, minWidth: 0,
};

// Карточка «Задача создана» — вызов MCP tasks_create в ленте чата (макет варианта C).
// Вместо технического блока: тонированная шапка с техподписью, название, описание,
// сетка деталей (исполнитель/срок/приоритет/подзадачи), метки, футер-ссылка.
// Вся карточка кликабельна → задача в её разделе (проектная — «Задачи», личная — «Календарь»).
export const TaskCreatedView = memo(function TaskCreatedView({ item, online, onOpenFile }: {
  item: ToolUseItem;
  online: boolean;
  onOpenFile?: (path: string) => void;
}) {
  // Резолв персоны-исполнителя: в чатах без персоны стор мог быть не загружен
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const personas = usePersonas();
  const project = useContext(ChatProjectContext);
  // Id сессии чата: чип «Шаг N фичи» (F9) шлёт по ней сигнал фокуса в стор Ф1.
  const sessionId = useContext(ChatSessionContext);
  const isMobile = useIsMobile();
  const [hovered, setHovered] = useState(false);
  // Чип «Шаг N фичи» — за гейтом фич-флага: разметка (featureId/stepRef) уже в проде,
  // но чип показываем только когда режим «Исполнение фичи» включён.
  const featureMode = useFeature(FLAGS.turnPlanFeatureMode);
  // F9: есть ли в ленте живая карточка плана (пишет ChatPanel): есть — сигнал фокуса
  // потребляется в ней, нет — клик по чипу открывает меню пилюли. Флаг читаем императивно
  // (getHasPlanCard) на следующем кадре ПОСЛЕ клика, а не из stale-значения хука (Глеб:
  // гонка ремоунта ChatPanel — при клике эффект ещё не написал флаг).
  // Hover/фокус чипа: hover — подложка C.bgSelected + текст C.textPrimary, фокус — кольцо SHADOW.focus.
  const [chipHot, setChipHot] = useState(false);
  const [chipFocused, setChipFocused] = useState(false);

  const running = item.result === undefined;
  const task = useMemo(
    () => (running || item.isError ? null : parseTask(item.result!)),
    [running, item.isError, item.result],
  );

  // Ошибка создания или непарсибельный ответ — стандартный блок инструмента
  if (!running && !task) return <ToolUseView item={item} online={online} onOpenFile={onOpenFile} />;

  const inp = (item.input ?? {}) as { title?: unknown };
  const title = task?.title ?? (typeof inp.title === 'string' ? inp.title : '');

  // В ответе личной задачи projectId нет — для hash-URL берём проект чата (личный чат → null)
  const openTask = () => {
    if (task) openTaskInSection(task.projectId ? task : { ...task, projectId: project?.id });
  };

  const persona = task?.personaId ? personas.find(p => p.id === task.personaId) : undefined;
  const subtasks = task?.subtasks ?? [];
  const subtasksDone = subtasks.filter(s => s.isDone).length;
  const labels = task?.labels ?? [];
  // Описание клампится в 2 строки — блочный markdown ломает кламп, показываем плоский текст
  const description = markdownToPlain(task?.description ?? '');
  const dueUrgent = task ? isDueUrgent(task) : false;
  // F9: подпись чипа. Линия — из task.lane с той же нормализацией, что в конвейере
  // (back/бэк → бэк, front/фронт → фронт); линии нет или она другая — суффикса нет.
  const chipLane = task?.lane ? laneOfChain(task.lane) : 'other';
  const chipLabel = task?.stepRef
    ? `Шаг ${task.stepRef} фичи · Производство${chipLane === 'back' ? ', бэк' : chipLane === 'front' ? ', фронт' : ''}`
    : '';

  return (
    <div
      onClick={task ? openTask : undefined}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      title={task ? 'Открыть задачу' : undefined}
      style={{
        border: `1px solid ${hovered && task ? C.accentMuted : C.borderLight}`,
        borderLeft: `3px solid ${C.accent}`,
        borderRadius: 12, background: C.bgWhite, overflow: 'hidden',
        boxShadow: SHADOW.card, maxWidth: '100%',
        cursor: task ? 'pointer' : 'default',
        transition: 'border-color 0.15s',
      }}
    >
      {/* Шапка: значок + «Задача создана» + техподпись инструмента */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 9, padding: '9px 12px',
        background: C.accentLight, borderBottom: `1px solid ${C.divider}`,
      }}>
        <div style={{
          width: 26, height: 26, borderRadius: '50%', flexShrink: 0,
          background: C.accentMuted, color: C.accent,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <SquareCheck size={14} strokeWidth={2} />
        </div>
        <span style={{ fontFamily: FONT.serif, fontSize: 14, fontWeight: 700, color: C.textHeading }}>
          Задача создана
        </span>
        {running ? (
          <span style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
            <div className="tool-spinner" />
            <span style={{ fontSize: 11, color: C.textMuted }}>Создаю задачу…</span>
          </span>
        ) : (
          <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>
            tasks · create
          </span>
        )}
      </div>

      {/* Тело: название, описание, сетка деталей, метки. Пока input стримится и
          заголовка ещё нет — не рендерим пустой блок с паддингами */}
      {(title || task) && (
      <div style={{ padding: '11px 14px 12px' }}>
        {title && (
          <div style={{ fontSize: 15, fontWeight: 700, lineHeight: 1.35, color: C.textHeading }}>
            {title}
          </div>
        )}
        {description && (
          <div style={{
            marginTop: 5, fontSize: 12.5, lineHeight: 1.5, color: C.textSecondary,
            display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' as const,
            overflow: 'hidden',
          }}>
            {description}
          </div>
        )}
        {task && (
          <div style={{
            marginTop: 12, display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: '10px 16px',
          }}>
            {(persona || task.assignee) && (
              <div>
                <CellLabel>Исполнитель</CellLabel>
                <div style={cellValueStyle}>
                  {persona ? (
                    <>
                      <PersonaAvatar persona={persona} size={20} />
                      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {personaLabel(persona)}
                      </span>
                    </>
                  ) : task.assignee === 'claude' ? (
                    <><ClaudeBadge size={18} />AI</>
                  ) : (
                    <><MeBadge size={20} />Я</>
                  )}
                </div>
              </div>
            )}
            {task.dueDate && (
              <div>
                <CellLabel>Срок</CellLabel>
                <div style={{ ...cellValueStyle, color: dueUrgent ? C.danger : C.textPrimary }}>
                  <CalendarIcon size={12} />
                  {dueLabel(task.dueDate)}{task.dueTime ? ` · ${task.dueTime}` : ''}
                </div>
              </div>
            )}
            <div>
              <CellLabel>Приоритет</CellLabel>
              <div style={cellValueStyle}>
                <PriorityFlag priority={task.priority} size={12} />
                {PRIORITY_LABEL[task.priority] ?? task.priority}
              </div>
            </div>
            {subtasks.length > 0 && (
              <div>
                <CellLabel>Подзадачи</CellLabel>
                <div style={cellValueStyle}>
                  <SubtaskCheck done={subtasksDone === subtasks.length} size={13} />
                  {subtasksDone} из {subtasks.length}
                </div>
              </div>
            )}
          </div>
        )}
        {labels.length > 0 && (
          <div style={{ marginTop: 11, display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            {labels.map(l => <LabelChip key={l} label={l} />)}
          </div>
        )}
        {/* Чип «Шаг N фичи» (макет v1 п. 5): кнопка, открывающая план на этом шаге.
            Только когда режим «Исполнение фичи» включён И в ответе tasks_create есть
            featureId И stepRef (Major 3 Глеба: без них чипа нет, чтобы не врать про
            неразмеченные задачи). Клик — stopPropagation: вся карточка — ссылка на задачу,
            а чип открывает план (сигнал фокуса в сторе Ф1; без живой карточки плана —
            меню пилюли). */}
        {featureMode && task?.featureId && task?.stepRef && (
          <div style={{ marginTop: CHIP_MARGIN_TOP }}>
            <span
              onMouseEnter={() => setChipHot(true)}
              onMouseLeave={() => setChipHot(false)}
              onFocus={() => setChipFocused(true)}
              onBlur={() => setChipFocused(false)}
            >
              <Button
                size="xs"
                variant="ghost"
                title="Открыть план фичи на этом шаге"
                leftIcon={<Route size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.accent} />}
                onClick={e => {
                  e.stopPropagation();
                  if (task && sessionId) {
                    focusFeatureStep(sessionId, task.featureId!, task.id);
                    // Гонка (Глеб): при ремоунте ChatPanel флаг «живая карточка плана» может быть
                    // ещё false в момент клика (эффект не отработал) → чип откроет меню пилюли,
                    // а карточка в ленте тоже раскроется. Читаем флаг на следующем кадре (после
                    // клика, когда эффект уже отработал), а не из stale-значения при клике.
                    requestAnimationFrame(() => {
                      if (!getHasPlanCard(sessionId)) openPillMenu(sessionId);
                    });
                  }
                }}
                style={{
                  display: 'inline-flex',
                  gap: CHIP_GAP,
                  padding: `${CHIP_PAD_Y}px ${CHIP_PAD_X}px`,
                  minHeight: isMobile ? TB.iconHitMobile : CHIP_MIN_H,
                  height: 'auto',
                  background: chipHot ? C.bgSelected : C.bgCard,
                  color: chipHot ? C.textPrimary : C.textSecondary,
                  border: `1px solid ${C.borderLight}`,
                  boxShadow: chipFocused ? SHADOW.focus : 'none',
                  fontWeight: 400,
                }}
              >
                {chipLabel}
              </Button>
            </span>
          </div>
        )}
      </div>
      )}

      {/* Футер-ссылка — самая явная аффорданса перехода */}
      {task && (
        <div style={{
          display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 5,
          padding: '8px 14px', borderTop: `1px solid ${C.divider}`,
          fontSize: 12, fontWeight: 600, color: C.accent,
        }}>
          Открыть задачу
          <span style={{
            display: 'flex', transform: hovered ? 'translateX(3px)' : 'none',
            transition: 'transform 0.15s',
          }}>
            <ArrowRight size={13} strokeWidth={2} />
          </span>
        </div>
      )}
    </div>
  );
});
