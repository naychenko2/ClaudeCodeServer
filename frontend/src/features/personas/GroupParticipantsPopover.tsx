import { useEffect, useState } from 'react';
import type { Persona, Session } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { personaTitleLines } from '../../lib/personas';
import { showToast } from '../../lib/toast';
import { PersonaAvatar } from './PersonaAvatar';

// Управление участниками группового чата: поповер под стеком аватаров в шапке.
// Список текущих (ведущая/отвечает, удаление при >2) + добавление доступных персон (до 4).
export function GroupParticipantsPopover({ session, participants, onUpdated, onClose }: {
  session: Session;
  participants: Persona[];
  // Обновлённая сессия после смены состава — родитель подхватывает participants/спикера
  onUpdated: (s: Session) => void;
  onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  // Кандидаты на добавление: персоны контекста чата, не входящие в группу
  const [available, setAvailable] = useState<Persona[] | null>(null);

  useEffect(() => {
    let alive = true;
    api.personas.list({ scope: 'context', projectId: session.projectId })
      .then(list => { if (alive) setAvailable(list.filter(p => !session.participants?.includes(p.id))); })
      .catch(() => { if (alive) setAvailable([]); });
    return () => { alive = false; };
  }, [session.id, session.participants, session.projectId]);

  const apply = async (personaIds: string[]) => {
    setBusy(true);
    try {
      const updated = await api.chats.setParticipants(session.id, personaIds);
      onUpdated(updated);
    } catch (e) {
      showToast('Участники', e instanceof Error ? e.message : 'Не удалось изменить состав', 'info');
    } finally {
      setBusy(false);
    }
  };

  const ids = session.participants ?? [];
  const remove = (id: string) => apply(ids.filter(x => x !== id));
  const add = (id: string) => apply([...ids, id]);

  const row = (p: Persona, extra: React.ReactNode) => (
    <div key={p.id} style={{ display: 'flex', alignItems: 'center', gap: 9, padding: '6px 8px', borderRadius: R.lg }}>
      <PersonaAvatar persona={p} size={26} />
      <span style={{ flex: 1, minWidth: 0, fontSize: 12.5, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {personaTitleLines(p).primary}
        {personaTitleLines(p).secondary && (
          <span style={{ fontWeight: 400, color: C.textMuted }}> · {personaTitleLines(p).secondary}</span>
        )}
      </span>
      {extra}
    </div>
  );

  const pill = (text: string) => (
    <span style={{
      flexShrink: 0, fontSize: 9.5, fontWeight: 700, letterSpacing: 0.3, textTransform: 'uppercase',
      padding: '2px 7px', borderRadius: R.pill, background: C.accentLight, color: C.accent,
    }}>
      {text}
    </span>
  );

  return (
    <>
      <div onClick={onClose} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
      <div style={{
        position: 'absolute', top: '100%', left: 0, marginTop: 8, zIndex: 41,
        width: 300, background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, boxShadow: SHADOW.card, padding: 8,
        fontFamily: FONT.sans, opacity: busy ? 0.6 : 1, pointerEvents: busy ? 'none' : 'auto',
      }}>
        <div style={{ fontSize: 11, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4, padding: '4px 8px 6px' }}>
          Участники · {participants.length}
        </div>
        {participants.map((p, i) => row(p, (
          <>
            {i === 0 && pill('ведущий')}
            {p.id === session.personaId && i !== 0 && pill('отвечает')}
            {participants.length > 2 && (
              <button
                type="button"
                onClick={() => remove(p.id)}
                title="Убрать из чата"
                style={{
                  flexShrink: 0, border: 'none', background: 'none', cursor: 'pointer',
                  color: C.textMuted, padding: 2, display: 'flex', borderRadius: 4,
                }}
              >
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round">
                  <path d="M18 6 6 18M6 6l12 12" />
                </svg>
              </button>
            )}
          </>
        )))}

        {participants.length < 4 && available !== null && available.length > 0 && (
          <>
            <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '6px 0' }} />
            <div style={{ fontSize: 11, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4, padding: '4px 8px 6px' }}>
              Добавить в чат
            </div>
            {available.map(p => (
              <button
                key={p.id}
                type="button"
                onClick={() => add(p.id)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
                  padding: '6px 8px', borderRadius: R.lg, border: 'none', background: 'none',
                  cursor: 'pointer', fontFamily: FONT.sans,
                }}
              >
                <PersonaAvatar persona={p} size={26} />
                <span style={{ flex: 1, minWidth: 0, fontSize: 12.5, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {personaTitleLines(p).primary}
                </span>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="2.4" strokeLinecap="round" style={{ flexShrink: 0 }}>
                  <path d="M12 5v14M5 12h14" />
                </svg>
              </button>
            ))}
          </>
        )}
        {participants.length >= 4 && (
          <div style={{ fontSize: 11, color: C.textMuted, padding: '4px 8px 6px' }}>
            Максимум 4 участника.
          </div>
        )}
      </div>
    </>
  );
}
