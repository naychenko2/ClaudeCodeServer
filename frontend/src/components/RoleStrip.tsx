import { useEffect, useState } from 'react';
import type { Role, Session } from '../types';
import { api } from '../lib/api';
import { C } from '../lib/design';
import { RoleAvatar } from './RoleAvatar';
import { useFeature, FLAGS } from '../lib/featureFlags';

interface Props {
  chats: Session[];                       // текущий список чатов — для поиска существующего разговора
  onSelect: (chat: Session) => void;      // открыть существующий чат с сотрудником
  onCreated: (chat: Session) => void;     // создан новый чат с сотрудником
}

// Полоса сотрудников над списком чатов (как в мессенджерах): аватар + имя + должность.
// Тык = продолжить существующий внепроектный разговор с сотрудником или начать первый.
// Видна только при включённом флаге roles и непустом пуле.
export function RoleStrip({ chats, onSelect, onCreated }: Props) {
  const rolesEnabled = useFeature(FLAGS.roles);
  const [roles, setRoles] = useState<Role[]>([]);
  const [starting, setStarting] = useState<string | null>(null);

  useEffect(() => {
    if (!rolesEnabled) { setRoles([]); return; }
    api.team.list().then(setRoles).catch(() => {});
  }, [rolesEnabled]);

  if (!rolesEnabled || roles.length === 0) return null;

  const startChat = async (role: Role) => {
    if (starting) return;
    const existing = chats.find(c => c.roleId === role.id);   // список отсортирован по свежести
    if (existing) {
      onSelect(existing);
      return;
    }
    setStarting(role.id);
    try {
      onCreated(await api.chats.create('auto', undefined, role.name, undefined, undefined, role.id));
    } catch { /* офлайн/сбой — ничего не меняем */ }
    finally { setStarting(null); }
  };

  return (
    <div style={{
      display: 'flex', gap: 10, overflowX: 'auto', padding: '2px 2px 10px',
      flexShrink: 0, scrollbarWidth: 'thin',
    }}>
      {roles.map(role => (
        <button
          key={role.id}
          type="button"
          onClick={() => startChat(role)}
          title={`${role.name}${role.title ? ' · ' + role.title : ''}`}
          style={{
            display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4,
            width: 62, flexShrink: 0, padding: 0, border: 'none', background: 'none',
            cursor: 'pointer', opacity: starting === role.id ? 0.6 : 1,
          }}
        >
          <RoleAvatar name={role.name} avatar={role.avatar} color={role.color} size={42} />
          <span style={{
            fontSize: 10.5, fontWeight: 600, color: C.textHeading, lineHeight: 1.15,
            maxWidth: 62, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {role.name || 'Без имени'}
          </span>
          {role.title && (
            <span style={{
              fontSize: 9, color: C.textMuted, lineHeight: 1.1, marginTop: -2,
              maxWidth: 62, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {role.title}
            </span>
          )}
        </button>
      ))}
    </div>
  );
}
