import { useState, useContext, useEffect, useRef } from 'react';
import { Check, SquarePen, MessageCircle, X } from 'lucide-react';
import type { ChatItem } from '../../types';
import { C, FONT, FS, R } from '../../lib/design';
import { useAssistantName, PersonaContext } from './contexts';
import { personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { markdownToPlain } from '../../lib/markdownPlain';
import { useIsMobile } from '../../lib/breakpoints';
import { VoiceMicButton } from './VoiceMicButton';

// Задержки для 12 столбиков «волны» (как в композере, чтобы анимация выглядела
// естественно и не в фазу)
const WAVE_DELAYS = [0.0, 0.12, 0.28, 0.45, 0.6, 0.32, 0.15, 0.5, 0.05, 0.36, 0.18, 0.42];
// mm:ss с ведущими нулями — как у секундомера в композере
function fmtRecTime(s: number): string {
  const mm = Math.floor(s / 60);
  const ss = s % 60;
  return `${mm}:${ss < 10 ? '0' : ''}${ss}`;
}

// Уточняющий вопрос Claude (AskUserQuestion) — интерактивная карточка выбора
interface QuestionDef { question: string; header?: string; multiSelect?: boolean; options: Array<{ label: string; description?: string }> }

// Маркер выбора: single → точка-радио, multi → чекбокс
function ChoiceMarker({ multi, selected }: { multi: boolean; selected: boolean }) {
  if (multi) {
    return selected ? (
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="1" y="1" width="14" height="14" rx="4" fill={C.accent} /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke={C.onAccent} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
    ) : (
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="1.5" y="1.5" width="13" height="13" rx="4" stroke={C.textMuted} strokeWidth="1.5" /></svg>
    );
  }
  return selected ? (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="7" fill={C.accent} /><circle cx="8" cy="8" r="2.6" fill={C.onAccent} /></svg>
  ) : (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="6.5" stroke={C.textMuted} strokeWidth="1.5" /></svg>
  );
}

export function AskQuestionView({ item, online, onAnswer, onInterrupt }: {
  item: Extract<ChatItem, { kind: 'ask_question' }>;
  online: boolean;
  onAnswer: (toolUseId: string, answerText: string) => void;
  onInterrupt?: () => void;
}) {
  const asstName = useAssistantName();
  const isMobile = useIsMobile();
  const persona = useContext(PersonaContext);
  const questions = (() => {
    const q = (item.input as { questions?: unknown } | null)?.questions;
    return Array.isArray(q) ? (q as QuestionDef[]) : [];
  })();
  const [selected, setSelected] = useState<Record<number, string[]>>({});
  const [customText, setCustomText] = useState<Record<number, string>>({});
  const customTextRefs = useRef<(HTMLTextAreaElement | null)[]>([]);
  // Идёт запись голоса в кастом-ответе «Другое» — индекс вопроса или -1, если не записываем
  const [recordingFor, setRecordingFor] = useState(-1);
  const [recSeconds, setRecSeconds] = useState(0);
  const [customOpen, setCustomOpen] = useState<Record<number, boolean>>({});
  const [activeTab, setActiveTab] = useState(0);
  // Тик таймера записи — отдельный эффект, чтобы ререндер формы не прыгал каждую секунду
  useEffect(() => {
    if (recordingFor < 0) return;
    const t = setInterval(() => setRecSeconds(s => s + 1), 1000);
    return () => clearInterval(t);
  }, [recordingFor]);
  // При входе в режим записи сбрасываем таймер
  useEffect(() => {
    if (recordingFor >= 0) setRecSeconds(0);
  }, [recordingFor]);
  if (questions.length === 0) return null;

  const disabled = item.resolved || !online;
  const multiQ = questions.length > 1;

  // Отвеченный вопрос — компактная зелёная плашка «принято» со сводкой выбора по всем вопросам
  if (item.resolved) {
    return (
      <div style={{ border: `1px solid ${C.success}`, borderLeft: `3px solid ${C.success}`, borderRadius: R.xl, padding: '13px 14px', background: C.successBg }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 10, fontSize: FS.base, fontWeight: 600, color: C.successText }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="8" fill={C.success} /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke={C.onAccent} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
          Ответ передан {persona ? personaLabel(persona) : asstName}
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {questions.map((q, qi) => {
            const stored = item.answers?.[q.question];
            const chosen = Array.isArray(stored) ? stored : stored ? [stored] : (selected[qi] ?? []);
            if (chosen.length === 0) return null;
            return (
              <div key={qi}>
                <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: 4 }}>{markdownToPlain(q.header || q.question)}</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {chosen.map((label, li) => (
                    <span key={li} style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: FS.sm, fontWeight: 600, color: C.successText, background: C.bgWhite, border: `1px solid ${C.success}`, borderRadius: R.sm, padding: '3px 9px' }}>
                      <Check size={11} color={C.success} strokeWidth={3.5} style={{ flexShrink: 0 }} />
                      {label}
                    </span>
                  ))}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    );
  }

  const isAnswered = (qi: number) =>
    (selected[qi]?.length ?? 0) > 0 || (!!customOpen[qi] && (customText[qi]?.trim().length ?? 0) > 0);
  const allAnswered = questions.every((_, qi) => isAnswered(qi));

  const toggleOption = (qi: number, label: string, multi: boolean) => {
    setSelected(prev => {
      const cur = prev[qi] ?? [];
      if (multi) return { ...prev, [qi]: cur.includes(label) ? cur.filter(l => l !== label) : [...cur, label] };
      return { ...prev, [qi]: [label] };
    });
    // single: выбор готовой опции сворачивает «свой вариант»
    if (!multi) {
      setCustomOpen(p => ({ ...p, [qi]: false }));
      setCustomText(p => ({ ...p, [qi]: '' }));
    }
  };
  const toggleCustom = (qi: number, multi: boolean) => {
    const willOpen = !customOpen[qi];
    setCustomOpen(p => ({ ...p, [qi]: willOpen }));
    if (willOpen && !multi) setSelected(p => ({ ...p, [qi]: [] })); // single: «свой вариант» снимает опции
    if (!willOpen) setCustomText(p => ({ ...p, [qi]: '' }));
  };

  const submit = () => {
    // updatedInput как в SDK: исходные questions + answers (вопрос → label/массив/свой текст)
    const answers: Record<string, string | string[]> = {};
    questions.forEach((q, qi) => {
      const labels = selected[qi] ?? [];
      const custom = customOpen[qi] ? (customText[qi]?.trim() ?? '') : '';
      if (q.multiSelect) {
        answers[q.question] = custom ? [...labels, custom] : [...labels];
      } else {
        answers[q.question] = custom || labels[0] || '';
      }
    });
    onAnswer(item.toolUseId, JSON.stringify({ questions, answers }));
  };

  // Enter в поле «свой ответ» подтверждает (как кнопка внизу), Shift+Enter — перенос строки:
  // иначе набор своего варианта заканчивался переводом строки, а не ответом
  const onCustomKeyDown = (qi: number) => (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    // на мобиле Enter переносит строку, подтверждение — кнопкой (как в композере)
    if (e.key !== 'Enter' || e.shiftKey || isMobile || e.nativeEvent.isComposing) return;
    e.preventDefault();
    if (disabled || !isAnswered(qi)) return;
    if (allAnswered) { submit(); return; }
    // остались неотвеченные вопросы — уводим на ближайший
    const next = questions.findIndex((_, i) => i !== qi && !isAnswered(i));
    if (next >= 0) setActiveTab(next);
  };

  const renderQuestion = (q: QuestionDef, qi: number) => (
    <div>
      {/* Текст вопроса и подписи опций — от модели: строчный контекст, markdown снимаем */}
      <div style={{ fontSize: FS.base, color: C.textHeading, fontWeight: 600, marginBottom: 9 }}>
        {markdownToPlain(q.question)}
        {q.multiSelect && <span style={{ fontWeight: 400, color: C.textMuted, fontSize: FS.xs }}> · можно несколько</span>}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {q.options.map(opt => {
          const isSel = (selected[qi] ?? []).includes(opt.label);
          return (
            <button key={opt.label} disabled={disabled} onClick={() => toggleOption(qi, opt.label, !!q.multiSelect)}
              style={{
                textAlign: 'left', padding: '9px 12px', borderRadius: R.lg, minHeight: 44, boxSizing: 'border-box',
                cursor: disabled ? 'default' : 'pointer',
                border: isSel ? `1.5px solid ${C.accent}` : `1px solid ${C.border}`,
                background: isSel ? C.accentLight : C.bgWhite,
                display: 'flex', alignItems: 'flex-start', gap: 9,
              }}
            >
              {!q.multiSelect && <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}><ChoiceMarker multi={false} selected={isSel} /></span>}
              <span style={{ flex: 1 }}>
                <span style={{ display: 'block', fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{opt.label}</span>
                {opt.description && <span style={{ display: 'block', fontSize: FS.sm, color: C.textSecondary, marginTop: 2, lineHeight: 1.4 }}>{markdownToPlain(opt.description)}</span>}
              </span>
              {q.multiSelect && <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}><ChoiceMarker multi selected={isSel} /></span>}
            </button>
          );
        })}
        {/* Другое (free-text) */}
        {(() => {
          const open = !!customOpen[qi];
          const filled = open && (customText[qi]?.trim().length ?? 0) > 0;
          return (
            <div style={{ borderRadius: R.lg, overflow: 'hidden', border: open ? `1.5px solid ${C.accent}` : `1px dashed ${C.dashed}`, background: open ? C.accentLight : 'transparent' }}>
              <div onClick={() => !disabled && toggleCustom(qi, !!q.multiSelect)}
                style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '9px 12px', minHeight: 44, boxSizing: 'border-box', cursor: disabled ? 'default' : 'pointer' }}>
                <SquarePen size={14} color={C.textMuted} strokeWidth={2} style={{ flexShrink: 0 }} />
                <span style={{ flex: 1, fontSize: FS.base, fontWeight: 600, color: open ? C.textHeading : C.textMuted }}>Другое{open ? '' : '…'}</span>
                {q.multiSelect && <span style={{ flexShrink: 0, display: 'flex' }}><ChoiceMarker multi selected={filled} /></span>}
              </div>
              {open && (
                <div style={{ padding: '0 10px 10px' }}>
                  {recordingFor === qi ? (
                    // === Режим записи голоса: textarea прячется, на её месте
                    // ряд из [dot, mm:ss, Waveform, ✕] — ровно как в композере ===
                    <div style={{
                      display: 'flex', alignItems: 'center', gap: 10,
                      minHeight: 44, padding: '8px 10px',
                      borderRadius: R.md, border: `1px solid ${C.border}`,
                      background: C.bgWhite,
                    }}>
                      <span style={{
                        width: 9, height: 9, borderRadius: '50%',
                        background: C.danger,
                        animation: 'pulsedot 1s ease-in-out infinite',
                        flexShrink: 0,
                      }} />
                      <span style={{
                        fontSize: 13, color: C.dangerText, fontWeight: 600,
                        fontFamily: FONT.mono, flexShrink: 0, minWidth: 34,
                      }}>
                        {fmtRecTime(recSeconds)}
                      </span>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 3, flex: 1, height: 22, overflow: 'hidden' }}>
                        {WAVE_DELAYS.map((d, i) => (
                          <span key={i} className="cc-wave-bar" style={{ height: 22, animationDelay: `${d}s` }} />
                        ))}
                      </div>
                      <button
                        type="button"
                        onClick={() => setRecordingFor(-1)}
                        title="Остановить запись"
                        style={{
                          width: 28, height: 28, borderRadius: '50%',
                          border: 'none', background: C.dangerBg, color: C.danger,
                          cursor: 'pointer', display: 'flex',
                          alignItems: 'center', justifyContent: 'center',
                          flexShrink: 0,
                        }}
                      >
                        <X size={14} strokeWidth={2.4} />
                      </button>
                    </div>
                  ) : (
                    <div style={{ position: 'relative' }}>
                      <textarea
                        autoComplete="off"
                        autoFocus
                        value={customText[qi] ?? ''}
                        onChange={e => setCustomText(p => ({ ...p, [qi]: e.target.value }))}
                        onKeyDown={onCustomKeyDown(qi)}
                        onClick={e => e.stopPropagation()}
                        disabled={disabled}
                        placeholder="Введите свой ответ…"
                        rows={2}
                        ref={el => { customTextRefs.current[qi] = el; }}
                        style={{ width: '100%', boxSizing: 'border-box', borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 36px 8px 10px', fontSize: FS.base, color: C.textHeading, fontFamily: 'inherit', resize: 'none', minHeight: 44, outline: 'none' }}
                      />
                      <VoiceMicButton
                        inputGetter={() => customTextRefs.current[qi]}
                        variant="suffix"
                        onListeningChange={listening => {
                          if (listening) setRecordingFor(qi);
                          else if (recordingFor === qi) setRecordingFor(-1);
                        }}
                      />
                    </div>
                  )}
                  {!isMobile && recordingFor !== qi && (
                    <div style={{ marginTop: 4, fontSize: FS.xs, color: C.textMuted }}>
                      Enter — {!multiQ || allAnswered ? 'ответить' : 'к следующему вопросу'}, Shift+Enter — перенос строки
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })()}
      </div>
    </div>
  );

  const secBtn = (label: string, onClick: () => void): React.ReactNode => (
    <button onClick={onClick} style={{ flex: 1, minHeight: 44, background: C.bgWhite, border: `1px solid ${C.border}`, color: C.textHeading, borderRadius: R.lg, padding: '9px 16px', cursor: 'pointer', fontSize: FS.base, fontWeight: 600 }}>{label}</button>
  );
  const interruptBtn = (): React.ReactNode => onInterrupt ? (
    <button onClick={onInterrupt}
      style={{ minHeight: 44, background: 'none', border: `1px solid ${C.border}`, color: C.textMuted, borderRadius: R.lg, padding: '9px 14px', cursor: 'pointer', fontSize: FS.base, fontWeight: 600, flexShrink: 0 }}>
      Прервать
    </button>
  ) : null;
  const answerBtn = (full: boolean): React.ReactNode => (
    <button onClick={submit} disabled={!allAnswered}
      style={{ flex: full ? undefined : 1, width: full ? '100%' : undefined, minHeight: 44, background: C.accent, color: C.onAccent, borderRadius: R.lg, padding: '9px 16px', border: 'none', cursor: allAnswered ? 'pointer' : 'default', fontSize: FS.base, fontWeight: 600, opacity: allAnswered ? 1 : 0.5 }}>Ответить</button>
  );

  return (
    <div style={{ border: `1px solid ${C.accentMuted}`, borderLeft: `3px solid ${C.accent}`, borderRadius: R.xl, padding: '13px 14px', background: C.accentLight }}>
      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 11 }}>
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', gap: 7, fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>
          {/* Карточки штаба идут от лица персоны (Э8): аватар, как у плана и эскалаций.
              Без персоны чата — прежний обезличенный вариант с иконкой */}
          {persona
            ? <PersonaAvatar persona={persona} size={20} />
            : <MessageCircle size={15} color={C.accent} strokeWidth={2} style={{ flexShrink: 0 }} />}
          {persona ? personaLabel(persona) : asstName} уточняет
        </div>
        {multiQ && <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textMuted, fontFamily: FONT.mono }}>{activeTab + 1} / {questions.length}</span>}
      </div>

      {multiQ && (
        <div style={{ display: 'flex', gap: 6, overflowX: 'auto', marginBottom: 12, paddingBottom: 2, scrollbarWidth: 'none' }}>
          {questions.map((q, qi) => {
            const ans = isAnswered(qi);
            const active = qi === activeTab;
            return (
              <button key={qi} disabled={disabled} onClick={() => setActiveTab(qi)}
                style={{
                  flexShrink: 0, display: 'flex', alignItems: 'center', gap: 6, padding: '0 11px', height: 28, boxSizing: 'border-box',
                  borderRadius: R.xxl, cursor: disabled ? 'default' : 'pointer', fontSize: FS.sm, fontWeight: 600, whiteSpace: 'nowrap', lineHeight: 1,
                  border: active ? `1.5px solid ${C.accent}` : `1px solid ${C.border}`,
                  background: active ? C.accentLight : C.bgWhite,
                  color: active || ans ? C.textHeading : C.textSecondary,
                }}
              >
                {ans
                  ? <Check size={11} color={C.accent} strokeWidth={3.5} style={{ flexShrink: 0 }} />
                  : <span style={{ width: 6, height: 6, borderRadius: '50%', background: active ? C.accent : C.textMuted, flexShrink: 0 }} />}
                {markdownToPlain(q.header ?? '') || `Q${qi + 1}`}
              </button>
            );
          })}
        </div>
      )}

      <div style={{ marginBottom: 11 }}>
        {renderQuestion(questions[multiQ ? activeTab : 0], multiQ ? activeTab : 0)}
      </div>

      {!online ? (
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>Недоступно офлайн</div>
      ) : multiQ ? (
        <div style={{ display: 'flex', gap: 8 }}>
          {activeTab > 0 && secBtn('‹ Назад', () => setActiveTab(t => t - 1))}
          {allAnswered
            ? answerBtn(false)
            : activeTab < questions.length - 1
              ? secBtn('Далее ›', () => setActiveTab(t => t + 1))
              : answerBtn(false)}
          {interruptBtn()}
        </div>
      ) : (
        <div style={{ display: 'flex', gap: 8 }}>
          <div style={{ flex: 1 }}>{answerBtn(true)}</div>
          {interruptBtn()}
        </div>
      )}
    </div>
  );
}
