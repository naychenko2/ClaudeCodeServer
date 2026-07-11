import { useState } from 'react';
import type { Persona } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Modal, ModalActions, TextArea } from '../../components/ui';
import { personaTitleLines } from '../../lib/personas';
import { api } from '../../lib/api';
import { showToast } from '../../lib/toast';
import { PersonaAvatar } from './PersonaAvatar';

// Режимы командного обсуждения:
//  - discuss — ведущая персона сама опрашивает участников через persona_ask и сводит итог
//    (промпт-обвязка обычного сообщения, бэкенд не участвует);
//  - meeting — совещание P7 (флаг persona-group-chats): независимые позиции →
//    перекрёстная критика → синтез; оркестрирует бэкенд (POST /chats/{id}/meeting).
type DiscussMode = 'discuss' | 'meeting';

export function DiscussTeamDialog({ candidates, chatPersona, sessionId, meetingEnabled, onSend, onClose }: {
  candidates: Persona[];
  // Персона самого чата — ведущая совещания (первая в списке участников)
  chatPersona?: Persona | null;
  // Чат, в котором запускается совещание (для POST /chats/{id}/meeting)
  sessionId?: string;
  // Доступен ли режим «Совещание» (флаг persona-group-chats)
  meetingEnabled?: boolean;
  // Отправить собранное сообщение в чат (обычный send) — режим «Обсуждение»
  onSend: (text: string) => void;
  onClose: () => void;
}) {
  const [mode, setMode] = useState<DiscussMode>('discuss');
  // Единственный кандидат выбран сразу — выбор без альтернатив не должен требовать клика
  const [selected, setSelected] = useState<string[]>(
    candidates.length === 1 ? [candidates[0].id] : []
  );
  const [question, setQuestion] = useState('');
  const [starting, setStarting] = useState(false);
  // Обсуждение — до 2 собеседников; совещание — до 3 (плюс ведущая = максимум 4)
  const max = mode === 'meeting' ? 3 : 2;

  const toggle = (id: string) =>
    setSelected(prev => prev.includes(id)
      ? prev.filter(x => x !== id)
      : prev.length >= max ? prev : [...prev, id]);

  const canSend = selected.length > 0 && question.trim().length > 0 && !starting;
  // Участники совещания: ведущая (персона чата) + выбранные
  const meetingCount = selected.length + (chatPersona ? 1 : 0);

  const submit = async () => {
    if (!canSend) return;
    const picked = candidates.filter(p => selected.includes(p.id));

    if (mode === 'meeting' && sessionId) {
      try {
        setStarting(true);
        const ids = [...(chatPersona ? [chatPersona.id] : []), ...picked.map(p => p.id)];
        await api.chats.startMeeting(sessionId, question.trim(), ids);
        onClose();
      } catch (e) {
        showToast('Совещание', e instanceof Error ? e.message : 'Не удалось запустить совещание', 'info');
      } finally {
        setStarting(false);
      }
      return;
    }

    const mentions = picked.map(p => `@${p.handle}`).join(' и ');
    const text =
      `Обсуди со мной и командой вопрос: ${question.trim()}\n\n` +
      `Спроси мнение ${mentions} через persona_ask (вопрос формулируй самодостаточно, ` +
      `с нужным контекстом). Собери позиции, при разногласиях возрази несогласным (один раунд) ` +
      `и сведи итог: кто что предлагал и к чему пришли. Заверши своим взвешенным выводом.`;
    onSend(text);
    onClose();
  };

  const modeCard = (m: DiscussMode, title: string, desc: string) => {
    const active = mode === m;
    return (
      <button
        key={m}
        type="button"
        onClick={() => { setMode(m); setSelected(prev => prev.slice(0, m === 'meeting' ? 3 : 2)); }}
        style={{
          flex: 1, textAlign: 'left', padding: '8px 10px', borderRadius: R.lg,
          border: `1.5px solid ${active ? C.accent : C.border}`,
          background: active ? C.accentLight : C.bgWhite, cursor: 'pointer', fontFamily: FONT.sans,
        }}
      >
        <span style={{ display: 'block', fontSize: 12.5, fontWeight: 700, color: active ? C.accent : C.textHeading }}>
          {title}
        </span>
        <span style={{ display: 'block', fontSize: 11, color: C.textMuted, marginTop: 2, lineHeight: 1.35 }}>
          {desc}
        </span>
      </button>
    );
  };

  return (
    <Modal width={460} title="Обсудить с командой"
      subtitle={mode === 'meeting'
        ? 'Участники независимо выскажутся, раскритикуют позиции друг друга, ведущий сведёт итог'
        : 'Выбери до двух участников — ведущий соберёт их мнения и сведёт итог'}
      onClose={onClose}
      footer={<ModalActions
        confirmLabel={mode === 'meeting' ? 'Созвать совещание' : 'Начать обсуждение'}
        onConfirm={submit} confirmDisabled={!canSend} loading={starting} onCancel={onClose} />}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {/* Переключатель режима — только когда совещания доступны */}
        {meetingEnabled && sessionId && (
          <div style={{ display: 'flex', gap: 8 }}>
            {modeCard('discuss', 'Обсуждение', 'Ведущий опрашивает участников и сводит итог. Быстро.')}
            {modeCard('meeting', 'Совещание', 'Независимые позиции + перекрёстная критика. Глубже, но дольше.')}
          </div>
        )}

        {/* Ведущая — персона этого чата: она собирает мнения и сводит итог, участия не требует */}
        {chatPersona && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '8px 10px',
            borderRadius: R.lg, background: C.bgPanel, fontFamily: FONT.sans }}>
            <PersonaAvatar persona={chatPersona} size={30} />
            <span style={{ flex: 1, minWidth: 0 }}>
              <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
                {personaTitleLines(chatPersona).primary}
              </span>
              <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted }}>
                {mode === 'meeting' ? 'Ведущий — выскажется и сведёт итог' : 'Ведущий — опросит участников и сведёт итог'}
              </span>
            </span>
            <span style={{
              flexShrink: 0, fontSize: 10, fontWeight: 700, letterSpacing: 0.3, textTransform: 'uppercase',
              padding: '2px 8px', borderRadius: R.pill, background: C.accentLight, color: C.accent,
            }}>
              ведущий
            </span>
          </div>
        )}

        <div>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', marginBottom: 6 }}>
            <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>
              Кого спросить
            </span>
            <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>
              выбрано {selected.length} из {max}
            </span>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {candidates.map(p => {
            const active = selected.includes(p.id);
            const disabled = !active && selected.length >= max;
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
                {/* Явный чекбокс: карточка — это выбор участника, а не информационная плашка */}
                <span style={{
                  flexShrink: 0, width: 18, height: 18, borderRadius: 5,
                  border: `2px solid ${active ? C.accent : C.border}`,
                  background: active ? C.accent : C.bgWhite,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                }}>
                  {active && (
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke={C.onAccent}
                      strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round">
                      <path d="M20 6L9 17l-5-5" />
                    </svg>
                  )}
                </span>
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
              </button>
            );
          })}
          </div>
          {selected.length === 0 && (
            <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, marginTop: 6 }}>
              Отметь хотя бы одного участника — без этого обсуждение не начать.
            </div>
          )}
        </div>

        <div>
          <div style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, marginBottom: 6 }}>
            Вопрос для обсуждения
          </div>
          <TextArea value={question} onChange={setQuestion} minHeight={72} autoGrow
            placeholder="Например: как лучше организовать онбординг новых пользователей?" />
          {mode === 'meeting' && meetingCount >= 2 && (
            <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, marginTop: 6, lineHeight: 1.4 }}>
              Участников: {meetingCount} (ведущий — {chatPersona ? personaTitleLines(chatPersona).primary : 'персона чата'}).
              Стоимость ≈ {2 * meetingCount + 1} вызовов модели — заметно дольше обычного ответа.
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
}
