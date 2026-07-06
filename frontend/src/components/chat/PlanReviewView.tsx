import { useState, useEffect, useRef, useContext } from 'react';
import type { ChatItem } from '../../types';
import { type Mode, MODE_META, ModeIcon } from '../../lib/modes';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { stripRoot } from '../../lib/paths';
import { ChatProjectContext, useAssistantName } from './contexts';
import { MarkdownContent } from './MarkdownContent';

// Иконка режима «План» — прямоугольник с линиями (как ModeIcon plan в Composer)
function PlanIcon({ size = 13, color = 'currentColor', strokeWidth = 2 }: { size?: number; color?: string; strokeWidth?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={strokeWidth} strokeLinecap="round" strokeLinejoin="round">
      <rect x="9" y="3" width="6" height="4" rx="1" />
      <path d="M9 5H7a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-2" />
      <path d="M9 12h6M9 16h4" />
    </svg>
  );
}

// Свёрнутый блок исходного плана (disclosure) — для решённых состояний карточки
function CollapsedPlanBody({ plan }: { plan: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ marginTop: 8 }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, background: 'none', border: 'none',
          cursor: 'pointer', padding: 0, fontSize: 12, fontWeight: 600, color: C.textSecondary, fontFamily: 'inherit',
        }}
      >
        <span style={{ display: 'inline-block', transform: open ? 'rotate(180deg)' : 'rotate(0deg)', transition: 'transform 0.2s' }}>▾</span>
        {open ? 'Скрыть план' : 'Показать план'}
      </button>
      {open && (
        <div style={{
          marginTop: 8, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
          padding: '10px 12px', maxHeight: 320, overflow: 'auto', fontSize: 13, color: C.textHeading, wordBreak: 'break-word',
        }}>
          <MarkdownContent text={plan || '_(пустой план)_'} />
        </div>
      )}
    </div>
  );
}

// Карточка согласования плана (ExitPlanMode в режиме «План»):
// показывает план и кнопки «Одобрить и выполнить» / «Отклонить» (с комментарием).
export function PlanReviewView({ item, online, onRespond, version, showBadge, showSwitch, onSwitchMode }: {
  item: Extract<ChatItem, { kind: 'plan_review' }>;
  online: boolean;
  onRespond: (requestId: string, approve: boolean, feedback?: string) => void;
  version?: number;
  showBadge?: boolean;
  showSwitch?: boolean;
  onSwitchMode?: (mode: Mode) => void;
}) {
  const [rejecting, setRejecting] = useState(false);
  const [feedback, setFeedback] = useState('');
  const asstName = useAssistantName();
  const project = useContext(ChatProjectContext);
  // В тексте плана пути показываем относительно корня проекта
  const plan = stripRoot(item.plan, project?.rootPath);
  const planBodyRef = useRef<HTMLDivElement>(null);
  // fade-оверлей снизу появляется только если контент плана не помещается в maxHeight
  const [overflowing, setOverflowing] = useState(false);
  useEffect(() => {
    const el = planBodyRef.current;
    if (!el) return;
    setOverflowing(el.scrollHeight - el.clientHeight > 8);
  }, [plan, rejecting]);

  // === Решённое состояние: одобрено → компактная шапка выполнения ===
  if (item.resolved && item.approved) {
    return (
      <div style={{
        border: `1px solid ${C.successBg}`, borderLeft: `3px solid ${C.success}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.successBg,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, fontWeight: 600, color: C.successText }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="8" fill={C.success} /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
          План одобрен — выполняется
        </div>
        <CollapsedPlanBody plan={plan} />
        {/* Выход из режима «План» — только у актуального (последнего) одобренного плана.
            Предлагаем выбрать режим исполнения, как в нативном approval Claude Code. */}
        {showSwitch && onSwitchMode && (
          <div style={{ marginTop: 9, paddingTop: 9, borderTop: '1px solid #CFE3CA', fontSize: 12, color: C.textSecondary }}>
            <div style={{ marginBottom: 7 }}>Чат остаётся в режиме «План» — следующие задачи тоже будут согласованы. Выйти и выполнять в:</div>
            <div style={{ display: 'flex', gap: 7 }}>
              {(['acceptEdits', 'auto'] as Mode[]).map(m => (
                <button key={m} onClick={() => onSwitchMode(m)}
                  style={{ display: 'flex', alignItems: 'center', gap: 6, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, cursor: 'pointer', fontSize: 12, fontWeight: 600, color: C.textHeading, padding: '5px 10px' }}>
                  <span style={{ display: 'flex', color: C.accent }}><ModeIcon mode={m} /></span>
                  {MODE_META[m].label}
                </button>
              ))}
            </div>
          </div>
        )}
      </div>
    );
  }

  // === Решённое состояние: отклонено → компактная строка + комментарий ===
  if (item.resolved && item.approved === false) {
    return (
      <div style={{
        border: `1px solid ${C.border}`, borderLeft: `3px solid ${C.textMuted}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.bgWhite,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, fontWeight: 600, color: C.textSecondary }}>
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M3 2v6h6" /><path d="M3 8a9 9 0 1 0 3-6.7L3 4" /></svg>
          План{version ? ` v${version}` : ''} — отклонён
        </div>
        {item.feedback?.trim() && (
          <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 7, whiteSpace: 'pre-wrap' }}>
            Комментарий: {item.feedback}
          </div>
        )}
        <CollapsedPlanBody plan={plan} />
      </div>
    );
  }

  // === На согласовании ===
  return (
    <div style={{
      border: `1px solid ${C.planBorder}`, borderLeft: `4px solid ${C.plan}`,
      borderRadius: R.xl, padding: '14px 16px', background: C.bgCard, boxShadow: SHADOW.card,
    }}>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10, marginBottom: 4 }}>
        <span style={{
          width: 28, height: 28, borderRadius: R.md, background: C.plan, flexShrink: 0,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <PlanIcon size={15} color="#FFF" />
        </span>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 700, color: C.textHeading, lineHeight: 1.2 }}>
            План готов
          </div>
          <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 2 }}>
            {asstName} предлагает план. Файлы пока не изменялись.
          </div>
        </div>
        {showBadge && version && (
          <span style={{
            flexShrink: 0, background: C.planLight, color: C.planText, borderRadius: R.sm,
            padding: '2px 8px', fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap',
          }}>
            v{version} · на согласовании
          </span>
        )}
      </div>

      <div style={{ position: 'relative', margin: '12px 0' }}>
        <div ref={planBodyRef} style={{
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
          padding: '10px 12px', maxHeight: 360, overflow: 'auto',
          fontSize: 13.5, color: C.textHeading, wordBreak: 'break-word',
        }}>
          <MarkdownContent text={plan || '_(пустой план)_'} />
        </div>
        {overflowing && (
          // Градиентный fade снизу — подсказка, что план длиннее видимой области
          <div style={{
            position: 'absolute', left: 1, right: 1, bottom: 1, height: 40, borderRadius: `0 0 ${R.lg}px ${R.lg}px`,
            background: `linear-gradient(to bottom, rgba(255,255,255,0), ${C.bgCard})`,
            pointerEvents: 'none',
          }} />
        )}
      </div>

      {!online ? (
        <div style={{ fontSize: 12, color: C.textMuted }}>Недоступно офлайн</div>
      ) : rejecting ? (
        <div>
          <div style={{ fontSize: 12, color: C.textSecondary, marginBottom: 7 }}>
            {asstName} учтёт это и предложит новый план
          </div>
          <textarea
            value={feedback}
            onChange={e => setFeedback(e.target.value)}
            autoFocus
            placeholder="Что поправить в плане? (необязательно)"
            rows={3}
            style={{ width: '100%', boxSizing: 'border-box', borderRadius: R.lg, border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 10px', fontSize: 13, color: C.textHeading, fontFamily: 'inherit', resize: 'none', outline: 'none', marginBottom: 8 }}
          />
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={() => onRespond(item.requestId, false, feedback.trim() || undefined)}
              style={{ flex: 1, minHeight: 40, background: C.plan, color: '#FFF', borderRadius: R.lg, padding: 9, border: 'none', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>
              Переработать план
            </button>
            <button onClick={() => { setRejecting(false); setFeedback(''); }}
              style={{ flex: 'none', minHeight: 40, background: C.bgWhite, border: `1px solid ${C.border}`, color: C.textSecondary, borderRadius: R.lg, padding: '9px 16px', cursor: 'pointer', fontSize: 13 }}>
              Назад
            </button>
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', gap: 8 }}>
          <button onClick={() => onRespond(item.requestId, true)}
            style={{
              flex: 1, minHeight: 42, background: C.plan, color: '#FFF', borderRadius: R.lg,
              padding: 9, border: 'none', cursor: 'pointer', fontSize: 13.5, fontWeight: 700,
              boxShadow: '0 4px 14px rgba(108,92,176,0.30)',
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
            }}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#FFF" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
            Одобрить и выполнить
          </button>
          <button onClick={() => setRejecting(true)}
            style={{ flex: 'none', minHeight: 42, background: 'transparent', border: `1px solid ${C.planBorder}`, color: C.planText, borderRadius: R.lg, padding: '9px 16px', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>
            Отклонить
          </button>
        </div>
      )}
    </div>
  );
}
