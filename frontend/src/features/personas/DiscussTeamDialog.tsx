import { useState } from 'react';
import type { Persona } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Modal, ModalActions, TextArea } from '../../components/ui';
import { personaTitleLines } from '../../lib/personas';
import { PersonaAvatar } from './PersonaAvatar';

// Мультиперсонная дискуссия (MVP без оркестратора): пользователь выбирает 1-2 участников
// и вопрос; диалог собирает промпт-обвязку — ведущая персона сама опрашивает участников
// через mcp__personas__persona_ask и сводит итог. Ни бэкенд, ни протокол не меняются.
export function DiscussTeamDialog({ candidates, onSend, onClose }: {
  candidates: Persona[];
  // Отправить собранное сообщение в чат (обычный send)
  onSend: (text: string) => void;
  onClose: () => void;
}) {
  const [selected, setSelected] = useState<string[]>([]);
  const [question, setQuestion] = useState('');
  const MAX = 2;

  const toggle = (id: string) =>
    setSelected(prev => prev.includes(id)
      ? prev.filter(x => x !== id)
      : prev.length >= MAX ? prev : [...prev, id]);

  const canSend = selected.length > 0 && question.trim().length > 0;

  const submit = () => {
    if (!canSend) return;
    const picked = candidates.filter(p => selected.includes(p.id));
    const mentions = picked.map(p => `@${p.handle}`).join(' и ');
    const text =
      `Обсуди со мной и командой вопрос: ${question.trim()}\n\n` +
      `Спроси мнение ${mentions} через persona_ask (вопрос формулируй самодостаточно, ` +
      `с нужным контекстом). Собери позиции, при разногласиях возрази несогласным (один раунд) ` +
      `и сведи итог: кто что предлагал и к чему пришли. Заверши своим взвешенным выводом.`;
    onSend(text);
    onClose();
  };

  return (
    <Modal width={460} title="Обсудить с командой"
      subtitle="Выбери до двух участников — ведущая персона соберёт их мнения и сведёт итог"
      onClose={onClose}
      footer={<ModalActions confirmLabel="Начать обсуждение" onConfirm={submit}
        confirmDisabled={!canSend} onCancel={onClose} />}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {candidates.map(p => {
            const active = selected.includes(p.id);
            const disabled = !active && selected.length >= MAX;
            return (
              <button
                key={p.id}
                type="button"
                onClick={() => toggle(p.id)}
                disabled={disabled}
                style={{
                  display: 'flex', alignItems: 'center', gap: 10, textAlign: 'left',
                  padding: '8px 10px', borderRadius: R.lg, cursor: disabled ? 'default' : 'pointer',
                  border: `1.5px solid ${active ? C.accent : C.border}`,
                  background: active ? C.accentLight : C.bgWhite,
                  opacity: disabled ? 0.5 : 1, fontFamily: FONT.sans,
                }}
              >
                <PersonaAvatar persona={p} size={30} />
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
                    {personaTitleLines(p).primary}
                  </span>
                  {p.description && (
                    <span style={{
                      display: 'block', fontSize: 11.5, color: C.textMuted,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}>
                      {p.description}
                    </span>
                  )}
                </span>
                {active && (
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.accent}
                    strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                    <path d="M20 6L9 17l-5-5" />
                  </svg>
                )}
              </button>
            );
          })}
        </div>
        <div>
          <div style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, marginBottom: 6 }}>
            Вопрос для обсуждения
          </div>
          <TextArea value={question} onChange={setQuestion} minHeight={72} autoGrow
            placeholder="Например: как лучше организовать онбординг новых пользователей?" />
        </div>
      </div>
    </Modal>
  );
}
