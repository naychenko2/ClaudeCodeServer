import { useState } from 'react';
import { ChevronDown, Users, X } from 'lucide-react';
import type { ChatItem, MeetingEntryItem, MeetingPhaseKey, Persona } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { getPersonaById, personaLabel, usePersonasVersion } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { MarkdownContent } from './MarkdownContent';

type MeetingItem = Extract<ChatItem, { kind: 'meeting' }>;

const PHASE_TITLES: Record<MeetingPhaseKey, string> = {
  independent: 'Независимые позиции',
  attack: 'Перекрёстная критика',
  synthesis: 'Итог',
};

// Спиннер построчного прогресса (стиль tool-spinner из глобальных стилей)
function Spinner() {
  return <div className="tool-spinner" style={{ width: 11, height: 11, flexShrink: 0 }} />;
}

// Строка-аккордеон реплики участника: аватар + «Роль (Имя)», текст раскрывается по клику
function EntryRow({ entry, defaultOpen }: { entry: MeetingEntryItem; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(!!defaultOpen);
  const persona = getPersonaById(entry.personaId);
  const label = persona ? personaLabel(persona) : 'Персона';
  return (
    <div style={{ borderTop: `1px solid ${C.divider}` }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '7px 12px',
          border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
        }}
      >
        {persona
          ? <PersonaAvatar persona={persona as Persona} size={22} />
          : <span style={{ width: 22, height: 22, borderRadius: '50%', background: C.bgSelected, flexShrink: 0 }} />}
        <span style={{ flex: 1, minWidth: 0, fontSize: 12.5, fontWeight: 600, color: entry.isError ? C.dangerText : C.textHeading, fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {label}{entry.isError ? ' — не ответил' : ''}
        </span>
        <ChevronDown size={11} color={C.textMuted} strokeWidth={2}
          style={{ flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
      </button>
      {open && (
        <div style={{ padding: '0 12px 10px 42px', fontSize: 13 }}>
          {entry.isError
            ? <span style={{ color: C.dangerText, fontSize: 12.5 }}>{entry.text}</span>
            : <MarkdownContent text={entry.text} />}
        </div>
      )}
    </div>
  );
}

// Секция фазы: заголовок + реплики участников (аккордеон) + спиннеры live-прогресса
function PhaseSection({ phase, entries, running }: {
  phase: MeetingPhaseKey;
  entries?: MeetingEntryItem[];
  running?: Record<string, 'running' | 'done' | 'error'>;
}) {
  if (!entries && !running) return null;
  return (
    <div>
      <div style={{
        padding: '8px 12px 4px', fontSize: 10.5, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
      }}>
        {PHASE_TITLES[phase]}
      </div>
      {entries?.map(e => <EntryRow key={`${phase}-${e.personaId}`} entry={e} />)}
      {/* Фаза ещё идёт — построчные статусы вместо реплик */}
      {!entries && running && Object.entries(running).map(([pid, status]) => {
        const persona = getPersonaById(pid);
        return (
          <div key={pid} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 12px', borderTop: `1px solid ${C.divider}` }}>
            {persona
              ? <PersonaAvatar persona={persona} size={22} />
              : <span style={{ width: 22, height: 22, borderRadius: '50%', background: C.bgSelected, flexShrink: 0 }} />}
            <span style={{ flex: 1, fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>
              {persona ? personaLabel(persona) : 'Персона'}
            </span>
            {status === 'running' && <Spinner />}
            {status === 'done' && <span style={{ color: C.success, fontSize: 13 }}>✓</span>}
            {status === 'error' && <X size={13} strokeWidth={2} style={{ color: C.dangerText, flexShrink: 0 }} />}
          </div>
        );
      })}
    </div>
  );
}

// Карточка совещания персон (P7) в ленте чата: вопрос, три секции-фазы с live-прогрессом,
// «Итог» крупно от ведущей, «Продолжить обсуждение» — мост итога в транскрипт CLI.
export function MeetingView({ item, onContinue, onCancel }: {
  item: MeetingItem;
  // Отправить в чат обычное сообщение с кратким итогом совещания
  onContinue?: (text: string) => void;
  // Отменить идущее совещание (POST /chats/{id}/meeting/cancel)
  onCancel?: () => void;
}) {
  usePersonasVersion(); // аватары/подписи обновятся после загрузки стора персон
  const synthesis = item.phases.synthesis?.find(e => !e.isError) ?? null;
  const leader = synthesis ? getPersonaById(synthesis.personaId) : null;
  const isRunning = item.status !== 'done' && item.status !== 'error';

  const continueText = () => {
    if (!synthesis) return '';
    const text = synthesis.text.length > 2000 ? synthesis.text.slice(0, 2000) + '…' : synthesis.text;
    return `Мы провели совещание по вопросу «${item.question}». Итог совещания:\n\n${text}\n\n` +
      'Продолжим обсуждение — учитывай этот итог.';
  };

  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderRadius: 12, background: C.bgWhite,
      overflow: 'hidden', boxShadow: SHADOW.card,
    }}>
      {/* Шапка: иконка + «Совещание» + вопрос */}
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8, padding: '10px 13px', borderBottom: `1px solid ${C.divider}` }}>
        <Users size={16} color={C.accent} strokeWidth={2} style={{ flexShrink: 0, marginTop: 2 }} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <span style={{ fontFamily: FONT.serif, fontSize: 14, fontWeight: 700, color: C.textHeading }}>Совещание</span>
          {item.question && (
            <div style={{ fontSize: 12.5, color: C.textSecondary, marginTop: 2, lineHeight: 1.4 }}>{item.question}</div>
          )}
        </div>
        {isRunning && <Spinner />}
        {isRunning && onCancel && (
          <button onClick={onCancel} style={{
            flexShrink: 0, fontSize: 11.5, padding: '3px 9px', borderRadius: 6,
            border: `1px solid ${C.border}`, background: C.bgWhite, cursor: 'pointer',
            color: C.textSecondary, fontFamily: FONT.sans,
          }}>
            Отменить
          </button>
        )}
      </div>

      <PhaseSection phase="independent" entries={item.phases.independent}
        running={item.running?.phase === 'independent' ? item.running.persona : undefined} />
      <PhaseSection phase="attack" entries={item.phases.attack}
        running={item.running?.phase === 'attack' ? item.running.persona : undefined} />

      {/* Итог — крупно, от ведущей, без аккордеона */}
      {(item.phases.synthesis || item.running?.phase === 'synthesis') && (
        <div style={{ borderTop: `1px solid ${C.divider}`, background: C.accentLight }}>
          <div style={{
            padding: '8px 12px 4px', fontSize: 10.5, fontWeight: 700, color: C.accent,
            textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
            display: 'flex', alignItems: 'center', gap: 8,
          }}>
            Итог{leader ? ` · ${personaLabel(leader)}` : ''}
            {!item.phases.synthesis && <Spinner />}
          </div>
          {synthesis && (
            <div style={{ display: 'flex', gap: 9, padding: '4px 12px 12px', alignItems: 'flex-start' }}>
              {leader && <div style={{ flexShrink: 0, marginTop: 2 }}><PersonaAvatar persona={leader} size={26} /></div>}
              <div style={{ flex: 1, minWidth: 0, fontSize: 13.5 }}>
                <MarkdownContent text={synthesis.text} />
              </div>
            </div>
          )}
          {item.phases.synthesis && !synthesis && (
            <div style={{ padding: '4px 12px 12px', fontSize: 12.5, color: C.dangerText }}>
              Ведущий не смог свести итог: {item.phases.synthesis[0]?.text}
            </div>
          )}
        </div>
      )}

      {/* Совещание прервано */}
      {item.status === 'error' && (
        <div style={{
          padding: '8px 12px', borderTop: `1px solid ${C.divider}`,
          background: C.dangerBg, color: C.dangerText, fontSize: 12.5,
        }}>
          {item.error ?? 'Совещание прервано'}
        </div>
      )}

      {/* Мост в транскрипт: краткий итог уходит обычным сообщением */}
      {synthesis && onContinue && (
        <div style={{ padding: '8px 12px', borderTop: `1px solid ${C.divider}` }}>
          <button
            onClick={() => onContinue(continueText())}
            style={{
              fontSize: 12.5, fontWeight: 600, padding: '6px 14px', borderRadius: 8,
              border: `1px solid ${C.border}`, background: C.bgWhite, cursor: 'pointer',
              color: C.textHeading, fontFamily: FONT.sans,
            }}
          >
            Продолжить обсуждение
          </button>
        </div>
      )}
    </div>
  );
}
