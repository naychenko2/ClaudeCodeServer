// Единый пикер исполнителя задачи: Я | Claude | персона (с аватаром).
// Заменяет пару «карточки Я/Claude + отдельный select персоны». Выбор персоны
// под капотом означает assignee='claude' + personaId — задачу выполнит Claude
// от лица персоны. Список персон — доступные в контексте задачи (глобальные +
// её проекта), гейт по флагу personas.
// В общем проекте (S2b-F1) между «Основными» и персонами стоит группа «Участники
// проекта»: человек-исполнитель — assignee='me' + assigneeUserId (id участника,
// контракт бэка S2b-2a); выбор «Я»/Claude/персоны снимает человека (''). Глобальные
// персоны в общем проекте недоступны ни у кого (п.17 карты, та же граница, что в
// CompanionSelector).

import { useEffect, useRef, useState } from 'react';
import { Check as CheckIcon, ChevronDown } from 'lucide-react';
import type { Persona, TaskAssignee } from '../../types';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { UserAvatar } from '../../components/ui/UserAvatar';
import { api } from '../../lib/api';
import { personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import { ClaudeBadge, MeBadge } from './bits';
import { isGlobalPersona, visibleIn } from '../../lib/personaZone';
import { useSphereOf } from '../../lib/useSphereOf';

export interface ExecutorValue {
  assignee: TaskAssignee;
  // id персоны-исполнителя (только при assignee='claude'); null — обычный Claude
  personaId: string | null;
  // Человек-исполнитель общего проекта (S2b-F1): id участника (всегда с assignee='me');
  // '' — снять. В личном проекте поля нет (прежнее поведение)
  assigneeUserId?: string;
}

// Пункт группы «Участники проекта». Список уже отфильтрован вызывающим: без читателей
// (бэк: reader_not_assignable) и без текущего пользователя — он в пикере «Я»;
// владелец, если это не он, — обычный участник (правило дорожек доски)
export interface ExecutorMemberOption { userId: string; displayName: string }

// Подпись исполнителя, которого нет в присланном списке (ушёл из проекта, список
// ещё не догрузился) — та же запасная подпись, что в TaskUserBadge/memberOptionName
const UNKNOWN_MEMBER_NAME = 'Участник';

interface Props {
  assignee: TaskAssignee;
  personaId: string | null;
  // Контекст для списка персон: null/undefined — личная задача (только глобальные)
  projectId?: string | null;
  // Участники общего проекта; undefined — личный/локальный проект: группы нет и
  // поведение прежнее. Пустой список — общий проект без участников: группа скрыта,
  // «Глобальных» всё равно нет
  members?: ExecutorMemberOption[];
  // Назначенный сейчас человек-исполнитель (пусто/undefined — никого)
  assigneeUserId?: string | null;
  onChange: (v: ExecutorValue) => void;
  disabled?: boolean;
}

export function ExecutorPicker({ assignee, personaId, projectId, members, assigneeUserId, onChange, disabled }: Props) {
  const [personas, setPersonas] = useState<Persona[]>([]);
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const sphereOf = useSphereOf();

  // Персоны, доступные в контексте задачи (глобальные + её проекта)
  useEffect(() => {
    let alive = true;
    api.personas.list({ scope: 'context', projectId: projectId ?? undefined })
      .then(list => { if (alive) setPersonas(list); })
      .catch(() => { if (alive) setPersonas([]); });
    return () => { alive = false; };
  }, [projectId]);

  // Закрытие по клику вне контрола
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  // Выбранная персона (если исполнитель — персона и она ещё доступна)
  const selectedPersona = personaId ? personas.find(p => p.id === personaId) ?? null : null;

  // Общий проект: сигнал — сам присланный список (TaskEditForm даёт его только для
  // общего проекта). В нём глобальные персоны недоступны ни у кого (п.17 карты)
  const sharedProject = members !== undefined;

  // Выбранный человек-исполнитель: имя из списка; нет в списке (ушёл/список ещё
  // не догрузился) — запасная подпись, как в TaskUserBadge
  const selectedMember = assignee === 'me' && assigneeUserId
    ? { userId: assigneeUserId, displayName: members?.find(m => m.userId === assigneeUserId)?.displayName ?? UNKNOWN_MEMBER_NAME }
    : null;

  // Группы персон: команда проекта / глобальные
  const projectPersonas = personas.filter(p => !isGlobalPersona(p) && visibleIn(p, projectId ?? undefined, sphereOf));
  const globalPersonas = personas.filter(isGlobalPersona);

  const pick = (v: ExecutorValue) => { onChange(v); setOpen(false); };

  return (
    <div ref={wrapRef} style={{ position: 'relative' }}>
      <button
        type="button"
        onClick={() => !disabled && setOpen(o => !o)}
        disabled={disabled}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 11,
          padding: '11px 14px', boxSizing: 'border-box', textAlign: 'left',
          border: `1px solid ${open ? C.accent : C.border}`, borderRadius: R.xl,
          background: C.bgWhite, cursor: disabled ? 'default' : 'pointer',
          transition: 'border-color 0.12s',
        }}
      >
        {/* Текущий исполнитель: аватар + подпись */}
        {selectedPersona
          ? <PersonaAvatar persona={selectedPersona} size={34} />
          : selectedMember
            ? <UserAvatar userId={selectedMember.userId} displayName={selectedMember.displayName} size={34} />
            : assignee === 'claude'
              ? <ClaudeBadge size={34} />
              : <MeBadge size={34} />}
        <span style={{ flex: 1, minWidth: 0 }}>
          <span style={{ display: 'block', fontFamily: FONT.sans, fontSize: 14, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {selectedPersona ? personaLabel(selectedPersona) : selectedMember ? selectedMember.displayName : assignee === 'claude' ? 'AI' : 'Я'}
          </span>
          <span style={{ display: 'block', fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, marginTop: 1 }}>
            {selectedPersona ? 'Выполнит от своего лица' : selectedMember ? 'Задача на нём' : assignee === 'claude' ? 'Выполнит AI' : 'Задача на вас'}
          </span>
        </span>
        <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
      </button>

      {open && (
        <div
          style={{
            position: 'absolute', top: 'calc(100% + 6px)', left: 0, right: 0,
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
            boxShadow: SHADOW.dropdown, padding: 4, zIndex: Z.dropdown,
            maxHeight: 340, overflowY: 'auto',
          }}
        >
          <GroupHeader text="Основные" />
          <BasicItem
            icon={<MeBadge size={28} />} label="Я" testid="opt-me"
            active={assignee === 'me' && !assigneeUserId}
            onClick={() => pick({ assignee: 'me', personaId: null, assigneeUserId: '' })}
          />
          <BasicItem
            icon={<ClaudeBadge size={28} />} label="AI" testid="opt-ai"
            active={assignee === 'claude' && !personaId}
            onClick={() => pick({ assignee: 'claude', personaId: null, assigneeUserId: '' })}
          />

          {/* Участники общего проекта (S2b-F1): между «Основными» и персонами.
              Пустого состояния нет — нет участников, группа скрыта */}
          {members && members.length > 0 && (
            <>
              <GroupHeader text="Участники проекта" />
              {members.map(m => (
                <BasicItem
                  key={m.userId}
                  icon={<UserAvatar userId={m.userId} displayName={m.displayName} size={28} />} label={m.displayName}
                  testid={`member-${m.userId}`}
                  active={assignee === 'me' && assigneeUserId === m.userId}
                  onClick={() => pick({ assignee: 'me', personaId: null, assigneeUserId: m.userId })}
                />
              ))}
            </>
          )}

          {projectPersonas.length > 0 && (
            <>
              <GroupHeader text="Команда проекта" />
              {projectPersonas.map(p => (
                <PersonaItem key={p.id} persona={p} active={p.id === personaId}
                  onClick={() => pick({ assignee: 'claude', personaId: p.id, assigneeUserId: '' })} />
              ))}
            </>
          )}
          {/* Глобальные — только вне общего проекта (п.17 карты) */}
          {!sharedProject && globalPersonas.length > 0 && (
            <>
              <GroupHeader text="Глобальные" />
              {globalPersonas.map(p => (
                <PersonaItem key={p.id} persona={p} active={p.id === personaId}
                  onClick={() => pick({ assignee: 'claude', personaId: p.id, assigneeUserId: '' })} />
              ))}
            </>
          )}
        </div>
      )}
    </div>
  );
}

// Заголовок группы в списке
function GroupHeader({ text }: { text: string }) {
  return (
    <div style={{
      padding: '7px 10px 3px', fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700,
      color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.06em',
    }}>
      {text}
    </div>
  );
}

// Пункт «Я» / «Claude» / участник
function BasicItem({ icon, label, active, onClick, testid }: { icon: React.ReactNode; label: string; active: boolean; onClick: () => void; testid?: string }) {
  return (
    <button type="button" data-testid={testid} onClick={onClick}
      onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
      onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 9, padding: '8px 10px',
        borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
        cursor: 'pointer', textAlign: 'left',
      }}
    >
      {icon}
      <span style={{ flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading }}>{label}</span>
      {active && <Check />}
    </button>
  );
}

// Пункт персоны
function PersonaItem({ persona, active, onClick }: { persona: Persona; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
      onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 9, padding: '8px 10px',
        borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
        cursor: 'pointer', textAlign: 'left',
      }}
    >
      <PersonaAvatar persona={persona} size={28} />
      <span style={{ flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {personaLabel(persona)}
      </span>
      {active && <Check />}
    </button>
  );
}

function Check() {
  return <CheckIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0 }} />;
}
