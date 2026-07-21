import { R, FONT } from '../../lib/design';
import { agentDotColor } from '../../components/AgentSelector';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import { getPersonaById, usePersonasVersion } from '../../lib/personas';
import { KIND_META } from './kindMeta';
import type { NotificationKind } from '../../types';

// Инициалы из имени: две первые буквы (по словам, иначе первые две буквы слова)
function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

// Аватар уведомления. Форма несёт различие «кто написал»:
//  • персона → КРУГ (лицо): живая персона из стора рендерится полноценным PersonaAvatar
//    (фото/инициалы); при её отсутствии (удалена) — инициалы по снапшоту уведомления.
//  • система/напоминание → скруглённый КВАДРАТ-плитка с kind-иконкой (как раньше).
export function NotificationAvatar({ personaId, personaName, personaColor, kind, size = 40 }: {
  personaId?: string;
  personaName?: string;
  personaColor?: string;
  kind: string;
  size?: number;
}) {
  usePersonasVersion();   // перерисоваться, когда персона догрузится/изменится в сторе
  const persona = personaId ? getPersonaById(personaId) : undefined;

  if (persona) return <PersonaAvatar persona={persona} size={size} />;

  // Снапшот-фолбэк: персоны нет в сторе (удалена), но уведомление помнит имя/цвет
  if (personaName) {
    return (
      <div
        aria-hidden
        style={{
          width: size, height: size, borderRadius: R.full, flexShrink: 0, userSelect: 'none',
          background: agentDotColor(personaColor), color: '#fff',
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.4), lineHeight: 1,
        }}
      >
        {initials(personaName)}
      </div>
    );
  }

  // Система/напоминание — плитка вида
  const meta = KIND_META[kind as NotificationKind] ?? KIND_META.info;
  return (
    <div
      aria-hidden
      style={{
        width: size, height: size, borderRadius: R.md, flexShrink: 0,
        background: meta.bg, color: meta.color,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontSize: Math.round(size * 0.42), lineHeight: 1,
      }}
    >
      {meta.icon}
    </div>
  );
}

// Есть ли у уведомления персона-атрибуция (для выбора формы/строки идентичности)
export function hasPersona(n: { personaId?: string; personaName?: string }): boolean {
  return !!n.personaId && !!n.personaName;
}

// Строка идентичности «Роль (Имя)» по снапшоту уведомления (без обращения к стору —
// имя/роль хранятся в самом уведомлении, переживают удаление персоны)
export function notifPersonaLabel(n: { personaName?: string; personaRole?: string }): string {
  if (!n.personaName) return '';
  return n.personaRole && n.personaRole.trim()
    ? `${n.personaRole.trim()} (${n.personaName})`
    : n.personaName;
}

// Токен цвета акцента уведомления: персона → её цвет, иначе цвет вида
export function notifAccentColor(n: { personaColor?: string; personaId?: string; personaName?: string }, kind: string): string {
  if (hasPersona(n)) return agentDotColor(n.personaColor);
  return (KIND_META[kind as NotificationKind] ?? KIND_META.info).color;
}
