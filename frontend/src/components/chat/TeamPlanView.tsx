import { useContext, useEffect, useMemo, useRef, useState } from 'react';
import { Users, Play, Zap, ChevronDown, RotateCcw, AlertTriangle, Check } from 'lucide-react';
import type { ChatItem, Persona, TeamPlan, TeamPlanSubtask } from '../../types';
import { C, FS, FONT, R, SHADOW, SP } from '../../lib/design';
import { relPath } from '../../lib/paths';
import { useIsMobile } from '../../lib/breakpoints';
import { ensurePersonasLoaded, getPersonaById, usePersonas, usePersonasVersion } from '../../lib/personas';
import { agentDotColor } from '../AgentSelector';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { teamPlanRunLabel } from '../../lib/teamImplement';
import { Button } from '../ui/Button';
import { Dot } from '../ui/Dot';
import { Menu } from '../ui/Menu';
import { ChatProjectContext, TeamPlanContext } from './contexts';

// Склонение числительного (ру): 1 под-задача, 2 под-задачи, 5 под-задач
function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

// Подзаголовок карточки: «5 под-задач · 2 волны · 3 исполнителя».
// Счётчики волн и исполнителей считает бэкенд (waveCount/executorCount).
function planSummaryLine(plan: TeamPlan): string {
  const t = plan.subtasks.length;
  const parts = [`${t} ${plural(t, 'под-задача', 'под-задачи', 'под-задач')}`];
  if (plan.waveCount > 0) parts.push(`${plan.waveCount} ${plural(plan.waveCount, 'волна', 'волны', 'волн')}`);
  if (plan.executorCount > 0)
    parts.push(`${plan.executorCount} ${plural(plan.executorCount, 'исполнитель', 'исполнителя', 'исполнителей')}`);
  return parts.join(' · ');
}

// Версия плана (Э8): «План v1» — всегда; «· обновлён после уточнений» — только у v2+
function planVersionLabel(plan: TeamPlan): string {
  const version = plan.version > 0 ? plan.version : 1;
  return version > 1 ? `План v${version} · обновлён после уточнений` : `План v${version}`;
}

// Полный подзаголовок: версия плана + счётчики под-задач/волн/исполнителей
function planSubtitle(plan: TeamPlan): string {
  return [planVersionLabel(plan), planSummaryLine(plan)].join(' · ');
}

// Блок «Что изменилось» / «Допущения» (Э8): пунктирная рамка, метка заглавными,
// строки с точкой-маркером. Скрывается целиком, когда список пуст.
// note — мелкая сноска под списком (у «Допущений» — пометка о моменте записи)
function AnnotationBlock({ label, items, note }: { label: string; items: string[]; note?: string }) {
  if (items.length === 0) return null;
  return (
    <div style={{ border: `1px dashed ${C.dashed}`, borderRadius: R.lg, padding: '8px 10px 6px', margin: '10px 0' }}>
      <div style={{
        fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.08em', textTransform: 'uppercase',
        color: C.textMuted, padding: '0 2px 4px',
      }}>
        {label}
      </div>
      {items.map((text, i) => (
        <div key={i} style={{
          display: 'flex', alignItems: 'flex-start', gap: 8, padding: '3px 2px',
          fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45,
        }}>
          <span style={{ width: 4, height: 4, borderRadius: '50%', background: C.textMuted, flexShrink: 0, marginTop: 7 }} />
          <span style={{ minWidth: 0, wordBreak: 'break-word' }}>{text}</span>
        </div>
      ))}
      {note && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `${SP.xs}px ${SP.xxs}px ${SP.xxs}px`, lineHeight: 1.4 }}>
          {note}
        </div>
      )}
    </div>
  );
}

// Подпись группы волны: первая идёт параллельно, следующие ждут предыдущую
function waveHint(wave: number): string {
  if (wave <= 1) return 'параллельно';
  if (wave === 2) return 'после первой';
  return `после ${wave - 1}-й`;
}

// Пометки бэка о ненадёжном подборе («…проверьте выбор», «…не обосновал выбор — проверьте»)
// показываем заметно: это сигнал проверить назначение руками до запуска.
function isCheckMe(rationale: string): boolean {
  return /провер/i.test(rationale);
}

// Под-задачи по волнам, в порядке возрастания номера волны
function groupByWave(subtasks: TeamPlanSubtask[]): { wave: number; items: TeamPlanSubtask[] }[] {
  const map = new Map<number, TeamPlanSubtask[]>();
  for (const s of subtasks) {
    const arr = map.get(s.wave) ?? [];
    arr.push(s);
    map.set(s.wave, arr);
  }
  return [...map.entries()].sort((a, b) => a[0] - b[0]).map(([wave, items]) => ({ wave, items }));
}

// Файлы во владении под-задачи: первые два пути + «+N», полный список — в title
function filesLine(files: string[], rootPath?: string | null): { text: string; title: string } | null {
  if (files.length === 0) return null;
  const shown = files.map(f => relPath(f, rootPath));
  const head = shown.slice(0, 2).join(' · ');
  return {
    text: shown.length > 2 ? `${head} +${shown.length - 2}` : head,
    title: shown.join(' · '),
  };
}

// Кликабельный чип исполнителя: точка цвета персоны + имя + chevron.
// Единственное редактируемое место карточки — выбор меняет исполнителя на месте (reassign).
function ExecutorChip({ personaId, candidates, onPick, disabled }: {
  personaId?: string | null;
  candidates: Persona[];
  onPick: (personaId: string) => void;
  disabled: boolean;
}) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const persona = personaId ? getPersonaById(personaId) : undefined;
  const label = persona?.name ?? 'не назначен';
  const color = persona ? agentDotColor(persona.avatar?.color) : C.textMuted;

  return (
    <span style={{ position: 'relative', flexShrink: 0 }}>
      <button
        // rect снимаем СРАЗУ: внутри апдейтера состояния currentTarget уже null
        onClick={e => {
          const rect = e.currentTarget.getBoundingClientRect();
          setAnchor(a => (a ? null : rect));
        }}
        disabled={disabled}
        title={disabled ? label : 'Сменить исполнителя'}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, height: 22, padding: '0 7px',
          borderRadius: R.max, border: `1px solid ${C.border}`, background: C.bgCard,
          cursor: disabled ? 'default' : 'pointer', fontSize: FS.xs, fontWeight: 600,
          color: C.textHeading, fontFamily: FONT.sans, maxWidth: 150,
        }}
      >
        <Dot color={color} />
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
        {!disabled && <ChevronDown size={9} strokeWidth={2.6} color={C.textMuted} style={{ flexShrink: 0 }} />}
      </button>
      {anchor && (
        <Menu onClose={() => setAnchor(null)} anchor={anchor} minWidth={252} maxHeight={280}>
          {candidates.length === 0 ? (
            <div style={{ padding: '9px 10px', fontSize: FS.sm, color: C.textMuted }}>
              Некого выбрать — в команде проекта нет персон
            </div>
          ) : candidates.map(p => {
            const current = p.id === personaId;
            return (
              <button
                key={p.id}
                onClick={() => { setAnchor(null); if (!current) onPick(p.id); }}
                style={{
                  display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left',
                  border: 'none', borderRadius: R.md, padding: '7px 9px', cursor: 'pointer',
                  background: current ? C.accentLight : 'none', fontFamily: FONT.sans,
                  fontSize: FS.base, color: C.textHeading,
                }}
              >
                <Dot color={agentDotColor(p.avatar?.color)} size={9} />
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{p.name}</span>
                {p.role && (
                  <span style={{
                    marginLeft: 'auto', flexShrink: 0, fontSize: FS.xs,
                    color: current ? C.accent : C.textMuted,
                  }}>
                    {p.role}
                  </span>
                )}
              </button>
            );
          })}
        </Menu>
      )}
    </span>
  );
}

// Строка под-задачи: название · чип исполнителя · файлы · обоснование серым
function SubtaskRow({ subtask, candidates, onReassign, readOnly, isMobile, rootPath }: {
  subtask: TeamPlanSubtask;
  candidates: Persona[];
  onReassign: (subtaskId: string, personaId: string) => void;
  readOnly: boolean;
  isMobile: boolean;
  rootPath?: string | null;
}) {
  const files = filesLine(subtask.files, rootPath);
  const warn = isCheckMe(subtask.executorRationale);
  return (
    <div style={{ padding: '7px 2px', borderTop: `1px solid ${C.borderLight}` }}>
      <div style={{
        display: 'flex', gap: SP.sm, minWidth: 0,
        alignItems: isMobile ? 'flex-start' : 'center',
        flexDirection: isMobile ? 'column' : 'row',
      }}>
        <span
          title={subtask.title}
          style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading, flex: 1, minWidth: 0, maxWidth: '100%',
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}
        >
          {subtask.title}
        </span>
        <ExecutorChip personaId={subtask.executorPersonaId} candidates={candidates}
          disabled={readOnly} onPick={id => onReassign(subtask.id, id)} />
      </div>
      {files && (
        <div title={files.title} style={{
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: 3,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {files.text}
        </div>
      )}
      {subtask.executorRationale && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: 5, marginTop: 3,
          fontSize: FS.xs, lineHeight: 1.4,
          color: warn ? C.warningText : C.textMuted,
          fontWeight: warn ? 600 : 400,
        }}>
          {warn && <AlertTriangle size={11} strokeWidth={2.2} style={{ flexShrink: 0, marginTop: 1 }} />}
          <span>{subtask.executorRationale}</span>
        </div>
      )}
    </div>
  );
}

// Тело плана: под-задачи по волнам со скроллом и fade снизу (как тело plan_review)
function PlanBody({ plan, candidates, onReassign, readOnly, isMobile, rootPath, maxHeight = 330 }: {
  plan: TeamPlan;
  candidates: Persona[];
  onReassign: (subtaskId: string, personaId: string) => void;
  readOnly: boolean;
  isMobile: boolean;
  rootPath?: string | null;
  maxHeight?: number;
}) {
  const bodyRef = useRef<HTMLDivElement>(null);
  const [overflowing, setOverflowing] = useState(false);
  useEffect(() => {
    const el = bodyRef.current;
    if (!el) return;
    setOverflowing(el.scrollHeight - el.clientHeight > 8);
  }, [plan]);
  const waves = useMemo(() => groupByWave(plan.subtasks), [plan.subtasks]);

  return (
    <div style={{ position: 'relative', margin: '12px 0' }}>
      <div ref={bodyRef} style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
        padding: '6px 10px', maxHeight, overflow: 'auto',
      }}>
        {waves.map(({ wave, items }) => (
          <div key={wave}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 6, padding: '8px 2px 4px',
              fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.08em',
              textTransform: 'uppercase', color: C.textMuted,
            }}>
              Волна {wave} · {waveHint(wave)}
              <span style={{ flex: 1, height: 1, background: C.borderLight }} />
            </div>
            {items.map(s => (
              <SubtaskRow key={s.id} subtask={s} candidates={candidates} onReassign={onReassign}
                readOnly={readOnly} isMobile={isMobile} rootPath={rootPath} />
            ))}
          </div>
        ))}
      </div>
      {overflowing && (
        // Градиентный fade снизу — подсказка, что план длиннее видимой области
        <div style={{
          position: 'absolute', left: 1, right: 1, bottom: 1, height: 40,
          borderRadius: `0 0 ${R.lg}px ${R.lg}px`,
          background: `linear-gradient(to bottom, transparent, ${C.bgCard})`,
          pointerEvents: 'none',
        }} />
      )}
    </div>
  );
}

// Свёрнутое тело решённой карточки — план остаётся в ленте референсом
function CollapsedPlan({ plan, isMobile, rootPath }: {
  plan: TeamPlan; isMobile: boolean; rootPath?: string | null;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, background: 'none', border: 'none',
          cursor: 'pointer', padding: 0, fontSize: FS.sm, fontWeight: 600, color: C.textSecondary,
          fontFamily: FONT.sans,
        }}
      >
        <span style={{
          display: 'inline-block', transition: 'transform 0.2s',
          transform: open ? 'rotate(180deg)' : 'rotate(0deg)',
        }}>▾</span>
        {open ? 'Скрыть план' : 'Показать план'}
      </button>
      {open && (
        <PlanBody plan={plan} candidates={[]} onReassign={() => {}} readOnly
          isMobile={isMobile} rootPath={rootPath} maxHeight={280} />
      )}
    </div>
  );
}

// Карточка плана «Командной реализации» (Э2): структурный план в ленте штаба.
// Конструкция — как у plan_review, но на accent-оси: две карточки в одной ленте
// не спутаешь. Единственная правка до запуска — исполнитель под-задачи (reassign).
export function TeamPlanView({ item, online }: {
  item: Extract<ChatItem, { kind: 'team_plan' }>;
  online: boolean;
}) {
  const ctx = useContext(TeamPlanContext);
  const project = useContext(ChatProjectContext);
  const isMobile = useIsMobile();
  const personas = usePersonas();
  usePersonasVersion();
  const [editing, setEditing] = useState(false);
  const [feedback, setFeedback] = useState('');
  // В не-персон-чате стор персон мог быть не загружен — резолвим имена исполнителей
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  const plan = item.plan;
  const rootPath = project?.rootPath;

  // Автор карточки (Э8): планировщик ИЗ ПЛАНА, не из текущего состояния режима — авторство
  // истории не должно меняться при смене координатора/планировщика. null у штаба без персоны —
  // тогда шапка остаётся прежним обезличенным вариантом
  const author = plan.plannerPersonaId ? getPersonaById(plan.plannerPersonaId) : undefined;
  const AuthorByline = author ? (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, minWidth: 0 }}>
      <span style={{
        fontSize: FS.sm, fontWeight: 700, color: C.textHeading,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {author.name}
      </span>
      <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>· планировщик</span>
    </div>
  ) : null;

  // Кандидаты для смены исполнителя: явно выбранный состав либо вся команда контекста
  // (глобальные персоны + персоны этого проекта) — зеркало TeamPlanningService.ResolveCandidates
  const candidates = useMemo(() => {
    const chosen = ctx?.executorPersonaIds ?? [];
    if (chosen.length > 0) return chosen.map(id => personas.find(p => p.id === id)).filter((p): p is Persona => !!p);
    return personas.filter(p => p.scope === 'global' || (p.scope === 'project' && p.projectId === project?.id));
  }, [ctx?.executorPersonaIds, personas, project?.id]);

  // === Решённое состояние: план запущен ===
  if (item.resolved && item.approved) {
    // «Идёт волна» — только у текущего плана и только пока волна реально идёт:
    // после завершения итерации (idle, waveNumber=0) и у старой версии плана —
    // честное «План запущен» (см. teamPlanRunLabel)
    const waveLine = teamPlanRunLabel(plan, ctx);
    return (
      <div style={{
        border: `1px solid ${C.successBg}`, borderLeft: `3px solid ${C.success}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.successBg,
        display: 'flex', flexDirection: 'column', gap: 6,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.base, fontWeight: 600, color: C.successText }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none" style={{ flexShrink: 0 }}>
            <circle cx="8" cy="8" r="8" fill={C.success} />
            <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke={C.onAccent} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {waveLine}
        </div>
        {/* Мини-подпись автора (Э8): сохраняется и в свёрнутой карточке — после смены
            координатора видно, чей это был план */}
        {author && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.xs, color: C.textMuted }}>
            <PersonaAvatar persona={author} size={16} />
            {author.name} · планировщик
          </div>
        )}
        <CollapsedPlan plan={plan} isMobile={isMobile} rootPath={rootPath} />
      </div>
    );
  }

  // === Решённое состояние: план отменён ===
  if (item.resolved && item.approved === false) {
    return (
      <div style={{
        border: `1px solid ${C.border}`, borderLeft: `3px solid ${C.textMuted}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.bgWhite,
        display: 'flex', flexDirection: 'column', gap: 6,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.base, fontWeight: 600, color: C.textSecondary }}>
          <RotateCcw size={15} color={C.textMuted} strokeWidth={2} style={{ flexShrink: 0 }} />
          План отменён
        </div>
        {author && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: FS.xs, color: C.textMuted }}>
            <PersonaAvatar persona={author} size={16} />
            {author.name} · планировщик
          </div>
        )}
        <CollapsedPlan plan={plan} isMobile={isMobile} rootPath={rootPath} />
      </div>
    );
  }

  // === На подтверждении ===
  const canAct = online && !!ctx;
  const respond = (decision: 'run' | 'cancel') => ctx?.onRespond(item.planId, decision);
  const reassign = (subtaskId: string, personaId: string) =>
    ctx?.onRespond(item.planId, 'reassign', subtaskId, personaId);
  const sendEdit = () => {
    const text = feedback.trim();
    if (!text || !ctx) return;
    ctx.onSendMessage(text);
    setEditing(false);
    setFeedback('');
  };

  return (
    <div style={{
      border: `1px solid ${C.accentMuted}`, borderLeft: `4px solid ${C.accent}`,
      borderRadius: R.xl, padding: '14px 16px', background: C.bgCard, boxShadow: SHADOW.card,
    }}>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10, marginBottom: 4 }}>
        {author ? (
          <PersonaAvatar persona={author} size={28} />
        ) : (
          <span style={{
            width: 28, height: 28, borderRadius: R.md, background: C.accent, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            <Users size={15} color={C.onAccent} strokeWidth={2.2} />
          </span>
        )}
        <div style={{ flex: 1, minWidth: 0 }}>
          {AuthorByline}
          <div style={{
            fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading, lineHeight: 1.2,
            marginTop: author ? 1 : 0,
          }}>
            План командной реализации
          </div>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>
            {planSubtitle(plan)}
          </div>
        </div>
        {/* Kind-иконка уходит на правый край, когда шапку занял аватар персоны — тот же
            паттерн «единой шапки штаба», что и у карточек эскалации */}
        {author && (
          <span style={{ flexShrink: 0, marginTop: 1, color: C.accent }}>
            <Users size={16} strokeWidth={2.2} />
          </span>
        )}
        <span style={{
          flexShrink: 0, background: C.accentLight, color: C.accent, borderRadius: R.sm,
          padding: '2px 8px', fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap',
        }}>
          на подтверждении
        </span>
      </div>

      {plan.summary && (
        <div style={{ fontSize: FS.base, color: C.textSecondary, lineHeight: 1.45 }}>{plan.summary}</div>
      )}

      {/* «Что изменилось» (Э8, только у v2+) — над телом плана: человек уже читал v1,
          подтверждает именно дельту. Подпись — дословно из спеки */}
      <AnnotationBlock label="Что изменилось" items={plan.changes} />

      <PlanBody plan={plan} candidates={candidates} onReassign={reassign}
        readOnly={!canAct} isMobile={isMobile} rootPath={rootPath} />

      {/* «Допущения» (Э8) — под телом, над кнопками: путь «вопросов нет» — главный
          носитель, декларируются вместе с планом. Текст фиксируется при составлении:
          после смены исполнителя имена в допущениях могут отставать от назначения —
          честно помечаем момент записи, а не переписываем чужой текст */}
      <AnnotationBlock label="Допущения" items={plan.assumptions}
        note="Записаны при составлении плана — имена в тексте не меняются при смене исполнителя" />

      {!online ? (
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>Недоступно офлайн</div>
      ) : editing ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
            Что поправить в плане? Сообщение уйдёт координатору — он пересоберёт план
          </div>
          <textarea
            value={feedback}
            onChange={e => setFeedback(e.target.value)}
            autoFocus
            rows={3}
            placeholder="Например: тесты отдать Вере, ревью убрать из плана"
            style={{
              width: '100%', boxSizing: 'border-box', borderRadius: R.lg,
              border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 10px',
              fontSize: FS.base, color: C.textHeading, fontFamily: FONT.sans, resize: 'none', outline: 'none',
            }}
          />
          <div style={{ display: 'flex', gap: SP.sm }}>
            <Button size="sm" leftIcon={<Check size={14} strokeWidth={2.6} />}
              disabled={!feedback.trim()} onClick={sendEdit} style={{ flex: 1 }}>
              Отправить
            </Button>
            <Button size="sm" variant="ghost" onClick={() => { setEditing(false); setFeedback(''); }}>
              Назад
            </Button>
          </div>
        </div>
      ) : (
        <>
          <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
            <Button size="sm" leftIcon={<Play size={14} fill="currentColor" stroke="none" />}
              disabled={!canAct} onClick={() => respond('run')} style={{ flex: 1, minWidth: 130 }}>
              Запустить
            </Button>
            <Button size="sm" variant="ghostFilled" disabled={!canAct} onClick={() => setEditing(true)}>
              Изменить план
            </Button>
            <Button size="sm" variant="ghost" disabled={!canAct} onClick={() => respond('cancel')}>
              Отменить
            </Button>
          </div>
          {ctx?.autoWaves && (
            <div style={{
              display: 'flex', alignItems: 'flex-start', gap: 6, marginTop: SP.sm,
              fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45,
            }}>
              <Zap size={11} fill="currentColor" stroke="none" style={{ flexShrink: 0, marginTop: 2 }} />
              <span>После запуска волны пойдут одна за другой — вмешаемся только при проблеме или исчерпании бюджета</span>
            </div>
          )}
        </>
      )}
    </div>
  );
}
