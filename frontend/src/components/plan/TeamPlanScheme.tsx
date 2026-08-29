// Разворот схемой КОМАНДНОГО плана (флаг visual-plan). В отличие от схемы обычного
// плана (PlanScheme + PlanMap с бэка) модель здесь НЕ участвует: схема собирается
// детерминированно из структуры TeamPlan — волны → под-задачи с исполнителями и
// файлами. Каркас (крошки, уровни «Суть»/«Карта», токены, inline-стили) повторяет
// PlanScheme; чистая логика (волны, счётчики, сигналы внимания) — в teamSchemeLogic.ts.
//
// Отличия контракта от PlanScheme, осознанные:
//  • нет уровня «Блок» и якорей useHeadings — у командного плана нет markdown-тела,
//    источник истины структура; вместо экрана блока детали под-задачи (goal,
//    doneCriteria, executorRationale) раскрываются НА МЕСТЕ кликом по строке;
//  • чип исполнителя здесь не редактируется — смена исполнителя остаётся у текстовой
//    карточки (TeamPlanView), схема только читает назначение;
//  • пункт внимания («Требует вашего внимания») уводит в «Карту» и раскрывает первую
//    затронутую под-задачу — отдельного адресата (раздела плана) у него нет.
//
// initialView/initialExpandedId существуют только для статик-тестов: vitest гоняет
// окружение node без jsdom, клики в renderToStaticMarkup не воспроизвести. Живой
// рендер — из TeamPlanView (карточка «Командной реализации»), без этих пропсов:
// состояние всегда стартует с «Сути».

import { useEffect, useState, type ReactNode } from 'react';
import { AlertTriangle, ArrowRight, ChevronDown, ChevronRight } from 'lucide-react';
import type { PlanMapNumber, TeamPlan, TeamPlanSubtask } from '../../types';
import { C, FONT, R, SP, FS } from '../../lib/design';
import { relPath } from '../../lib/paths';
import { markdownToPlain } from '../../lib/markdownPlain';
import { ensurePersonasLoaded, getPersonaById, usePersonasVersion } from '../../lib/personas';
import { agentDotColor } from '../AgentSelector';
import { MarkdownContent } from '../chat/MarkdownContent';
import { Dot } from '../ui/Dot';
import { buildTeamScheme, countNumbers, type TeamSchemeAttention, type TeamSchemeWave } from './teamSchemeLogic';

interface Props {
  // Готовый план командной реализации — источник схемы, модель не зовётся
  plan: TeamPlan;
  // Корень проекта: файлы под-задач показываются относительно него (relPath)
  rootPath?: string | null;
  // Стартовый экран и раскрытая под-задача — только для статик-тестов (см. эпиграф)
  initialView?: 'essence' | 'map';
  initialExpandedId?: string | null;
}

type View = 'essence' | 'map';

// Подпись сигнала внимания — пилюля в строке блока «Требует вашего внимания»
const ATTENTION_LABEL: Record<TeamSchemeAttention['kind'], string> = {
  'file-conflict': 'общий файл',
  'no-executor': 'нет исполнителя',
};

// Файлы под-задачи: первые два пути + «+N», полный список — в title.
// Копия filesLine из TeamPlanView — карточка и схема обязаны показывать файлы
// одинаково, но кодом компонент карточки не делим (TeamPlanView не трогаем).
function filesLine(files: string[], rootPath?: string | null): { text: string; title: string } | null {
  if (files.length === 0) return null;
  const shown = files.map(f => relPath(f, rootPath));
  const head = shown.slice(0, 2).join(' · ');
  return {
    text: shown.length > 2 ? `${head} +${shown.length - 2}` : head,
    title: shown.join(' · '),
  };
}

export function TeamPlanScheme({ plan, rootPath, initialView = 'essence', initialExpandedId = null }: Props) {
  const [view, setView] = useState<View>(initialView);
  const [expandedId, setExpandedId] = useState<string | null>(initialExpandedId);
  // Имена исполнителей резолвятся из стора персон; getPersonaById — нереактивный
  // снимок, поэтому подписываемся на версию стора: без неё загрузка персон не
  // перерисовала бы чипы, и они навсегда остались бы «не назначен».
  usePersonasVersion();
  // В чате без персон стор мог не загружаться — ensurePersonasLoaded идемпотентен,
  // повторный вызов рядом с TeamPlanView лишней работы не делает
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  // Дешёвые производные считаем прямо в рендере без useMemo: план в карточке
  // меняется только с версией, а накопление вещания проекта (ревью-комментарий
  // 24-03-06) обходится дороже пересчёта
  const scheme = buildTeamScheme(plan.subtasks);
  const numbers = countNumbers(scheme.counts);

  // Пункт внимания → «Карта» с раскрытой первой затронутой под-задачей (см. эпиграф)
  function openInMap(subtaskId: string) {
    setView('map');
    setExpandedId(subtaskId);
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <Breadcrumbs view={view} onEssence={() => setView('essence')} />
      {view === 'essence' && (
        <Essence
          plan={plan}
          subtasks={plan.subtasks}
          numbers={numbers}
          attention={scheme.attention}
          rootPath={rootPath}
          onOpenInMap={openInMap}
        />
      )}
      {view === 'map' && (
        <MapView
          waves={scheme.waves}
          rootPath={rootPath}
          expandedId={expandedId}
          onToggle={id => setExpandedId(cur => (cur === id ? null : id))}
        />
      )}
    </div>
  );
}

// === Крошки ===
// Два уровня — «Суть» и «Карта»; последний элемент не кликабелен, мы в нём.
function Breadcrumbs({ view, onEssence }: { view: View; onEssence: () => void }) {
  const items: Array<{ label: string; onClick?: () => void }> = [
    { label: 'Суть', onClick: view !== 'essence' ? onEssence : undefined },
    ...(view === 'map' ? [{ label: 'Карта' }] : []),
  ];
  return (
    <div style={{
      display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 4,
      fontFamily: FONT.sans, fontSize: FS.sm,
    }}>
      {items.map((it, i) => (
        <span key={it.label} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          {i > 0 && <ChevronRight size={12} style={{ color: C.textMuted, flexShrink: 0 }} />}
          {it.onClick ? (
            <button onClick={it.onClick} style={{
              border: 'none', background: 'transparent', cursor: 'pointer',
              padding: '2px 4px', borderRadius: R.sm,
              fontFamily: FONT.sans, fontSize: FS.sm,
              color: C.textSecondary, fontWeight: 600,
            }}>{it.label}</button>
          ) : (
            <span style={{
              padding: '2px 4px',
              color: C.textHeading, fontWeight: 600,
            }}>{it.label}</span>
          )}
        </span>
      ))}
    </div>
  );
}

// === Экран «Суть» ===
function Essence({ plan, subtasks, numbers, attention, rootPath, onOpenInMap }: {
  plan: TeamPlan;
  subtasks: TeamPlanSubtask[];
  numbers: PlanMapNumber[];
  attention: TeamSchemeAttention[];
  rootPath?: string | null;
  onOpenInMap: (subtaskId: string) => void;
}) {
  // Заголовки под-задач для строк внимания: сигнал несёт только id, текст резолвим сами
  const byId = new Map(subtasks.map(s => [s.id, s]));
  const titleOf = (id: string) => markdownToPlain(byId.get(id)?.title ?? '');
  const intent = plan.intent?.trim();

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {/* Жанр + суть: summary — сводка планировщика одной фразой; пустая сводка
          (старая история) — показываем исходный запрос, «пустого заголовка» не рисуем */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <span style={{
          alignSelf: 'flex-start',
          padding: '3px 9px', borderRadius: R.max,
          background: C.bgInset, color: C.textSecondary,
          fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
          textTransform: 'lowercase',
        }}>
          командная реализация
        </span>
        <div style={{
          fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700,
          color: C.textHeading, lineHeight: 1.3,
        }}>{plan.summary?.trim() || plan.request}</div>
      </div>

      {/* Замысел планировщика — markdown, как в текстовой карточке, но без ограничения
          высоты: «Суть» и так короче полного плана */}
      {intent && (
        <div style={{
          background: C.bgInset, border: `1px solid ${C.border}`,
          borderRadius: R.lg, padding: `${SP.sm}px ${SP.md}px`,
          display: 'flex', flexDirection: 'column', gap: SP.xs,
        }}>
          <div style={{
            fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.08em',
            textTransform: 'uppercase', color: C.textMuted,
          }}>Замысел</div>
          <div style={{ fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5 }}>
            {/* Нижний отступ последнего абзаца гасим — иначе блок разъедется с
                остальными отступами «Сути» (тот же приём, что IntentBlock) */}
            <div style={{ marginBottom: -8 }}><MarkdownContent text={intent} /></div>
          </div>
        </div>
      )}

      {/* Числа: «3 под-задачи · 2 волны · …» — считаются по фактическим subtasks */}
      {numbers.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
          {numbers.map((n, i) => (
            <span key={i} style={{
              display: 'inline-flex', alignItems: 'baseline', gap: 4,
              padding: '4px 10px', borderRadius: R.pill,
              border: `1px solid ${C.border}`, background: C.bgWhite,
              fontFamily: FONT.sans, fontSize: FS.sm,
            }}>
              <strong style={{ color: C.textHeading, fontWeight: 700 }}>{n.value}</strong>
              <span style={{ color: C.textSecondary }}>{n.label}</span>
            </span>
          ))}
        </div>
      )}

      {/* Блок внимания — детерминированные сигналы teamSchemeLogic: общий файл
          (две руки в одном файле) и под-задача без исполнителя */}
      {attention.length > 0 && (
        <div style={{
          border: `1px solid ${C.border}`,
          background: C.warningBg, borderRadius: R.lg,
          padding: `${SP.sm}px ${SP.md}px`,
          display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textHeading, fontWeight: 700 }}>
            <AlertTriangle size={14} style={{ color: C.warning, flexShrink: 0 }} />
            Требует вашего внимания · {attention.length}
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            {attention.map(a => a.kind === 'file-conflict' ? (
              <AttentionRow key={`file:${a.file}`} pill={ATTENTION_LABEL[a.kind]} onOpen={() => onOpenInMap(a.subtaskIds[0])}>
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
                  <span title={relPath(a.file, rootPath)} style={{
                    fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>{relPath(a.file, rootPath)}</span>
                  <span style={{
                    fontSize: FS.sm, color: C.textSecondary,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>{a.subtaskIds.map(titleOf).join(' · ')}</span>
                </div>
              </AttentionRow>
            ) : (
              <AttentionRow key={`exec:${a.subtaskId}`} pill={ATTENTION_LABEL[a.kind]} onOpen={() => onOpenInMap(a.subtaskId)}>
                <span style={{
                  flex: 1, minWidth: 0, fontSize: FS.base, color: C.textHeading, fontWeight: 600,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>{titleOf(a.subtaskId)}</span>
              </AttentionRow>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// Строка блока внимания: пилюля сигнала + произвольное тело + стрелка. Клик уводит
// в «Карту» к первой затронутой под-задаче. hover/active — как у строк PlanScheme.
function AttentionRow({ pill, onOpen, children }: {
  pill: string;
  onOpen: () => void;
  children: ReactNode;
}) {
  return (
    <button onClick={onOpen} style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: '8px 10px', borderRadius: R.md,
      background: C.bgWhite, border: `1px solid ${C.border}`,
      cursor: 'pointer', fontFamily: FONT.sans,
      textAlign: 'left', width: '100%',
      transition: 'background 0.12s',
    }}
    onMouseEnter={e => (e.currentTarget.style.background = C.bgSelected)}
    onMouseLeave={e => (e.currentTarget.style.background = C.bgWhite)}
    >
      <span style={{
        padding: '2px 7px', borderRadius: R.max,
        background: C.bgInset, color: C.textSecondary,
        fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
      }}>{pill}</span>
      {children}
      <ArrowRight size={14} style={{ color: C.textMuted, flexShrink: 0 }} />
    </button>
  );
}

// === Экран «Карта» ===
// Волны по строкам: заголовок «Волна N · подпись» + строки под-задач. Внутри волны
// порядок исходного массива (его задаёт планировщик) — схема не переупорядочивает.
function MapView({ waves, rootPath, expandedId, onToggle }: {
  waves: TeamSchemeWave[];
  rootPath?: string | null;
  expandedId: string | null;
  onToggle: (id: string) => void;
}) {
  if (waves.length === 0) {
    return (
      <div style={{
        border: `1px dashed ${C.dashed}`, borderRadius: R.lg,
        padding: `${SP.md}px`, textAlign: 'center',
        fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted,
      }}>Под-задач нет</div>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {waves.map(({ wave, hint, items }) => (
        <div key={wave} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <div style={{
            display: 'flex', alignItems: 'center', gap: 6, padding: '8px 2px 4px',
            fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.08em',
            textTransform: 'uppercase', color: C.textMuted,
          }}>
            {`Волна ${wave} · ${hint}`}
            <span style={{ flex: 1, height: 1, background: C.borderLight }} />
          </div>
          {items.map((s, i) => (
            <SubtaskCard
              key={s.id}
              subtask={s}
              index={i}
              expanded={expandedId === s.id}
              onToggle={onToggle}
              rootPath={rootPath}
            />
          ))}
        </div>
      ))}
    </div>
  );
}

// Чип исполнителя в схеме — только чтение: точка цвета персоны + имя; «не назначен»
// (в том числе неразрешённый id — стор персон пуст) — серым. Копия ExecutorChip из
// TeamPlanView без меню выбора: смена исполнителя остаётся у текстовой карточки.
function ExecutorChip({ personaId }: { personaId?: string | null }) {
  const persona = personaId ? getPersonaById(personaId) : undefined;
  const label = persona?.name ?? 'не назначен';
  const color = persona ? agentDotColor(persona.avatar?.color) : C.textMuted;
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 5, height: 22, padding: '0 7px',
      borderRadius: R.max, border: `1px solid ${C.border}`, background: C.bgCard,
      fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans, maxWidth: 150, flexShrink: 0,
      color: persona ? C.textHeading : C.textMuted,
    }}>
      <Dot color={color} size={9} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
    </span>
  );
}

// Строка под-задачи: номер в волне · название · чип исполнителя · файлы. Клик
// раскрывает детали (goal/doneCriteria/executorRationale) на месте; шеврон
// переворачивается. Нет деталей — строка не кликабельна и шеврона нет.
function SubtaskCard({ subtask, index, expanded, onToggle, rootPath }: {
  subtask: TeamPlanSubtask;
  index: number;
  expanded: boolean;
  onToggle: (id: string) => void;
  rootPath?: string | null;
}) {
  const files = filesLine(subtask.files, rootPath);
  // Название от модели приходит markdown-ом, а живёт в однострочном контексте —
  // чистим до плоского (тот же приём, что SubtaskRow в TeamPlanView)
  const title = markdownToPlain(subtask.title);
  const goal = subtask.goal.trim();
  const done = subtask.doneCriteria.trim();
  const rationale = subtask.executorRationale.trim();
  const hasDetails = !!(goal || done || rationale);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
      <button
        onClick={hasDetails ? () => onToggle(subtask.id) : undefined}
        disabled={!hasDetails}
        title={hasDetails ? (expanded ? 'Свернуть детали' : 'Показать детали') : undefined}
        style={{
          display: 'flex', alignItems: 'flex-start', gap: SP.sm,
          padding: '10px 12px', borderRadius: R.lg,
          background: C.bgWhite, border: `1px solid ${C.border}`,
          cursor: hasDetails ? 'pointer' : 'default',
          fontFamily: FONT.sans, textAlign: 'left', width: '100%',
          opacity: hasDetails ? 1 : 0.55,
          transition: 'background 0.12s',
        }}
        onMouseEnter={e => {
          if (hasDetails) e.currentTarget.style.background = C.bgSelected;
        }}
        onMouseLeave={e => {
          e.currentTarget.style.background = C.bgWhite;
        }}
      >
        <span style={{
          flexShrink: 0, width: 22, height: 22, borderRadius: R.full,
          background: C.bgInset, color: C.textSecondary,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          fontSize: FS.xs, fontWeight: 700,
        }}>{index + 1}</span>
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
            <span style={{
              fontSize: FS.md, fontWeight: 700, color: C.textHeading,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{title}</span>
            <ExecutorChip personaId={subtask.executorPersonaId} />
          </div>
          {files && (
            <div title={files.title} style={{
              fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{files.text}</div>
          )}
        </div>
        {hasDetails && (
          <ChevronDown size={14} style={{
            color: C.textMuted, flexShrink: 0, alignSelf: 'center',
            transition: 'transform 0.15s',
            transform: expanded ? 'rotate(180deg)' : 'rotate(0deg)',
          }} />
        )}
      </button>
      {expanded && hasDetails && (
        <div style={{
          background: C.bgInset, border: `1px solid ${C.border}`,
          borderRadius: R.lg, padding: `${SP.sm}px ${SP.md}px`,
          display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          {goal && <Detail label="Цель" text={goal} />}
          {done && <Detail label="Готово, когда" text={done} />}
          {rationale && <Detail label="Почему этот исполнитель" text={rationale} />}
        </div>
      )}
    </div>
  );
}

// Подробность раскрытой под-задачи: метка заглавными + markdown-текст (поля
// заполняет модель — блочная разметка здесь уместна, контекст не однострочный)
function Detail({ label, text }: { label: string; text: string }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
      <div style={{
        fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.08em',
        textTransform: 'uppercase', color: C.textMuted,
      }}>{label}</div>
      <div style={{ fontSize: FS.base, color: C.textHeading, lineHeight: 1.5 }}>
        <div style={{ marginBottom: -8 }}><MarkdownContent text={text} /></div>
      </div>
    </div>
  );
}
