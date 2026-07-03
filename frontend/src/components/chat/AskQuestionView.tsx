import { useState } from 'react';
import type { ChatItem } from '../../types';
import { C, FONT } from '../../lib/design';

// Уточняющий вопрос Claude (AskUserQuestion) — интерактивная карточка выбора
interface QuestionDef { question: string; header?: string; multiSelect?: boolean; options: Array<{ label: string; description?: string }> }

// Маркер выбора: single → точка-радио, multi → чекбокс
function ChoiceMarker({ multi, selected }: { multi: boolean; selected: boolean }) {
  if (multi) {
    return selected ? (
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="1" y="1" width="14" height="14" rx="4" fill="#D97757" /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
    ) : (
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="1.5" y="1.5" width="13" height="13" rx="4" stroke="#9A8F7E" strokeWidth="1.5" /></svg>
    );
  }
  return selected ? (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="7" fill="#D97757" /><circle cx="8" cy="8" r="2.6" fill="#FBF1EA" /></svg>
  ) : (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="6.5" stroke="#9A8F7E" strokeWidth="1.5" /></svg>
  );
}

export function AskQuestionView({ item, online, onAnswer, onInterrupt }: {
  item: Extract<ChatItem, { kind: 'ask_question' }>;
  online: boolean;
  onAnswer: (toolUseId: string, answerText: string) => void;
  onInterrupt?: () => void;
}) {
  const questions = (() => {
    const q = (item.input as { questions?: unknown } | null)?.questions;
    return Array.isArray(q) ? (q as QuestionDef[]) : [];
  })();
  const [selected, setSelected] = useState<Record<number, string[]>>({});
  const [customText, setCustomText] = useState<Record<number, string>>({});
  const [customOpen, setCustomOpen] = useState<Record<number, boolean>>({});
  const [activeTab, setActiveTab] = useState(0);
  if (questions.length === 0) return null;

  const disabled = item.resolved || !online;
  const multiQ = questions.length > 1;

  // Отвеченный вопрос — компактная зелёная плашка «принято» со сводкой выбора по всем вопросам
  if (item.resolved) {
    return (
      <div style={{ border: '1px solid #CADFC4', borderLeft: '3px solid #5E8B4E', borderRadius: 12, padding: '13px 14px', background: '#EEF4EA' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 10, fontSize: 13, fontWeight: 600, color: '#3F6B33' }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="8" fill="#5E8B4E" /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#fff" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
          Ответ передан Claude
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {questions.map((q, qi) => {
            const stored = item.answers?.[q.question];
            const chosen = Array.isArray(stored) ? stored : stored ? [stored] : (selected[qi] ?? []);
            if (chosen.length === 0) return null;
            return (
              <div key={qi}>
                <div style={{ fontSize: 12, color: C.textSecondary, marginBottom: 4 }}>{q.header || q.question}</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {chosen.map((label, li) => (
                    <span key={li} style={{ display: 'inline-flex', alignItems: 'center', gap: 5, fontSize: 12, fontWeight: 600, color: '#3F6B33', background: C.bgWhite, border: '1px solid #CADFC4', borderRadius: 7, padding: '3px 9px' }}>
                      <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="#5E8B4E" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
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

  const renderQuestion = (q: QuestionDef, qi: number) => (
    <div>
      <div style={{ fontSize: 13, color: C.textHeading, fontWeight: 600, marginBottom: 9 }}>
        {q.question}
        {q.multiSelect && <span style={{ fontWeight: 400, color: C.textMuted, fontSize: 11 }}> · можно несколько</span>}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {q.options.map(opt => {
          const isSel = (selected[qi] ?? []).includes(opt.label);
          return (
            <button key={opt.label} disabled={disabled} onClick={() => toggleOption(qi, opt.label, !!q.multiSelect)}
              style={{
                textAlign: 'left', padding: '9px 12px', borderRadius: 9, minHeight: 44, boxSizing: 'border-box',
                cursor: disabled ? 'default' : 'pointer',
                border: isSel ? `1.5px solid ${C.accent}` : `1px solid ${C.border}`,
                background: isSel ? C.accentLight : C.bgWhite,
                display: 'flex', alignItems: 'flex-start', gap: 9,
              }}
            >
              {!q.multiSelect && <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}><ChoiceMarker multi={false} selected={isSel} /></span>}
              <span style={{ flex: 1 }}>
                <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>{opt.label}</span>
                {opt.description && <span style={{ display: 'block', fontSize: 12, color: C.textSecondary, marginTop: 2, lineHeight: 1.4 }}>{opt.description}</span>}
              </span>
              {q.multiSelect && <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}><ChoiceMarker multi selected={isSel} /></span>}
            </button>
          );
        })}
        {/* Свой вариант (free-text) */}
        {(() => {
          const open = !!customOpen[qi];
          const filled = open && (customText[qi]?.trim().length ?? 0) > 0;
          return (
            <div style={{ borderRadius: 9, overflow: 'hidden', border: open ? `1.5px solid ${C.accent}` : '1px dashed #C9A98F', background: open ? C.accentLight : 'transparent' }}>
              <div onClick={() => !disabled && toggleCustom(qi, !!q.multiSelect)}
                style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '9px 12px', minHeight: 44, boxSizing: 'border-box', cursor: disabled ? 'default' : 'pointer' }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#9A8F7E" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></svg>
                <span style={{ flex: 1, fontSize: 13, fontWeight: 600, color: open ? C.textHeading : C.textMuted }}>Свой вариант{open ? '' : '…'}</span>
                {q.multiSelect && <span style={{ flexShrink: 0, display: 'flex' }}><ChoiceMarker multi selected={filled} /></span>}
              </div>
              {open && (
                <div style={{ padding: '0 10px 10px' }}>
                  <textarea
                    value={customText[qi] ?? ''}
                    onChange={e => setCustomText(p => ({ ...p, [qi]: e.target.value }))}
                    onClick={e => e.stopPropagation()}
                    disabled={disabled}
                    placeholder="Введите свой ответ…"
                    rows={2}
                    style={{ width: '100%', boxSizing: 'border-box', borderRadius: 8, border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 10px', fontSize: 13, color: C.textHeading, fontFamily: 'inherit', resize: 'none', minHeight: 44, outline: 'none' }}
                  />
                </div>
              )}
            </div>
          );
        })()}
      </div>
    </div>
  );

  const secBtn = (label: string, onClick: () => void): React.ReactNode => (
    <button onClick={onClick} style={{ flex: 1, minHeight: 44, background: C.bgWhite, border: `1px solid ${C.border}`, color: C.textHeading, borderRadius: 9, padding: '9px 16px', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>{label}</button>
  );
  const interruptBtn = (): React.ReactNode => onInterrupt ? (
    <button onClick={onInterrupt}
      style={{ minHeight: 44, background: 'none', border: `1px solid ${C.border}`, color: C.textMuted, borderRadius: 9, padding: '9px 14px', cursor: 'pointer', fontSize: 13, fontWeight: 600, flexShrink: 0 }}>
      Прервать
    </button>
  ) : null;
  const answerBtn = (full: boolean): React.ReactNode => (
    <button onClick={submit} disabled={!allAnswered}
      style={{ flex: full ? undefined : 1, width: full ? '100%' : undefined, minHeight: 44, background: C.accent, color: C.onAccent, borderRadius: 9, padding: '9px 16px', border: 'none', cursor: allAnswered ? 'pointer' : 'default', fontSize: 13, fontWeight: 600, opacity: allAnswered ? 1 : 0.5 }}>Ответить</button>
  );

  return (
    <div style={{ border: '1px solid #E6C9B8', borderLeft: `3px solid ${C.accent}`, borderRadius: 12, padding: '13px 14px', background: '#FBF1EA' }}>
      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 11 }}>
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', gap: 7, fontSize: 13, fontWeight: 600, color: C.textHeading }}>
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#D97757" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" />
          </svg>
          Claude уточняет
        </div>
        {multiQ && <span style={{ fontSize: 12, fontWeight: 600, color: C.textMuted, fontFamily: FONT.mono }}>{activeTab + 1} / {questions.length}</span>}
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
                  borderRadius: 14, cursor: disabled ? 'default' : 'pointer', fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap', lineHeight: 1,
                  border: active ? `1.5px solid ${C.accent}` : `1px solid ${C.border}`,
                  background: active ? C.accentLight : C.bgWhite,
                  color: active || ans ? C.textHeading : C.textSecondary,
                }}
              >
                {ans
                  ? <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="#D97757" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
                  : <span style={{ width: 6, height: 6, borderRadius: '50%', background: active ? C.accent : '#C9BEAD', flexShrink: 0 }} />}
                {q.header || `Q${qi + 1}`}
              </button>
            );
          })}
        </div>
      )}

      <div style={{ marginBottom: 11 }}>
        {renderQuestion(questions[multiQ ? activeTab : 0], multiQ ? activeTab : 0)}
      </div>

      {!online ? (
        <div style={{ fontSize: 12, color: C.textMuted }}>Недоступно офлайн</div>
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
