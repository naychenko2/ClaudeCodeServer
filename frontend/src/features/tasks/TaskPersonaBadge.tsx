// Мини-плашка персоны-исполнителя задачи: аватар + роль.
// Данные — из глобального стора персон (lib/personas); если персона ещё не
// загрузилась или удалена — ничего не рендерим (мягкая деградация).

import { useEffect } from 'react';
import { C, FONT } from '../../lib/design';
import { ensurePersonasLoaded, getPersonaById, personaTitleLines, usePersonasVersion } from '../../lib/personas';
import { PersonaAvatar } from '../personas/PersonaAvatar';

export function TaskPersonaBadge({ personaId, size = 16 }: { personaId: string; size?: number }) {
  usePersonasVersion(); // реактивность на изменения стора персон
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  const persona = getPersonaById(personaId);
  if (!persona) return null;

  return (
    <span
      title={personaTitleLines(persona).secondary
        ? `${personaTitleLines(persona).primary} (${personaTitleLines(persona).secondary})`
        : personaTitleLines(persona).primary}
      style={{ display: 'inline-flex', alignItems: 'center', gap: 5, minWidth: 0 }}
    >
      <PersonaAvatar persona={persona} size={size} />
      <span style={{
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, color: C.textSecondary,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {personaTitleLines(persona).primary}
      </span>
    </span>
  );
}
