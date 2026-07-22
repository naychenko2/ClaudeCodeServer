import { Pin, SquarePen, Trash2 } from 'lucide-react';
import type { Session } from '../types';
import { C, R, SHADOW, FONT } from '../lib/design';
import { IconButton } from './ui';
import { StatusIndicator } from './StatusIndicator';
import { ExpiryBadge } from './ExpiryBadge';
import { ChatOriginBadge } from './ChatOriginBadge';
import { resolveChatOrigin } from '../lib/chatOrigin';
import { getPersonaById, personaLabel } from '../lib/personas';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import { agentDotColor } from './AgentSelector';
import { TeamMechanicBadge } from '../features/team/TeamMechanicBadge';
import { teamTurnPreview } from '../features/team/teamMechanics';
import { getLastMechanic } from '../lib/lastMechanic';

// Время создания чата: сегодня — часы:минуты, иначе — дата (группы и так разбиты по дням)
function chatTime(iso: string): string {
  const d = new Date(iso);
  const now = new Date();
  if (d.toDateString() === now.toDateString())
    return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
}

interface Props {
  session: Session;
  isActive: boolean;
  isMobile: boolean;
  // Имя-заглушка, если чат не назван («Новый чат» / «Чат #3»)
  fallbackName: string;
  // Действия доступны только онлайн (мутации)
  online: boolean;
  hovered: boolean;
  workflowRunning: boolean;
  onSelect: () => void;
  onHover: (hovered: boolean) => void;
  onEdit: () => void;
  onDelete: () => void;
  // Не задан — чат без закрепления (списки проекта)
  onTogglePin?: () => void;
}

/**
 * Карточка чата в боковых списках (глобальном ChatList и проектном SessionList).
 * Раскладка: строка «статус + собеседник + название + время», под ней — бейджи
 * и превью последнего сообщения во всю ширину. Действия всплывают поверх времени
 * по наведению, поэтому текст под ними места не теряет.
 */
export function ChatCard({
  session: s, isActive, isMobile, fallbackName, online, hovered, workflowRunning,
  onSelect, onHover, onEdit, onDelete, onTogglePin,
}: Props) {
  // Чат от лица персоны: мини-аватар в строке названия и акцент её цвета
  const persona = s.personaId ? getPersonaById(s.personaId) : undefined;
  // Групповой чат: стек мини-аватаров участников вместо одиночного + подпись «Групповой»
  const group = (s.participants?.length ?? 0) > 1
    ? s.participants!.map(id => getPersonaById(id)).filter(p => p !== undefined)
    : [];
  const accent = persona ? agentDotColor(persona.avatar?.color) : C.accent;
  // Происхождение чата (задача/автоматизация) — контекст на плашке
  const origin = resolveChatOrigin(s);
  // Последняя запущенная в чате механика команды — компактный бейдж
  const mechanic = getLastMechanic(s.id);
  // Действия: по наведению, у активной карточки — всегда; на мобиле hover'а нет
  const showActions = online && (isMobile || isActive || hovered);
  const cardBg = isActive ? C.accentLight : C.bgWhite;
  const subtitle = group.length > 1
    ? `Групповой · ${group.length} участника`
    : persona ? personaLabel(persona) : null;

  return (
    <div
      onClick={onSelect}
      onMouseEnter={() => onHover(true)}
      onMouseLeave={() => onHover(false)}
      style={{
        position: 'relative',
        // отдельные longhand-свойства: со shorthand + undefined React обнуляет padding-left
        paddingTop: isMobile ? 14 : 11,
        paddingBottom: isMobile ? 14 : 11,
        paddingRight: isMobile ? 16 : 12,
        // у активной карточки добавляем слева место под акцентную полосу
        paddingLeft: (isMobile ? 16 : 12) + (isActive ? 6 : 0),
        borderRadius: isMobile ? 16 : R.xl,
        marginBottom: 5,
        cursor: 'pointer',
        overflow: 'hidden',
        background: cardBg,
        border: '1px solid ' + (isActive ? accent : C.borderLight),
        boxShadow: isActive ? SHADOW.button : SHADOW.card,
        display: 'flex',
        flexDirection: 'column',
        gap: 3,
      }}
    >
      {/* Акцентная полоса слева — явный маркер текущего чата (у чатов персоны — её цветом) */}
      {isActive && (
        <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: accent }} />
      )}

      {/* Строка 1: статус, собеседник, название, время.
          Есть собеседник — статус показывает кольцо вокруг его аватара, отдельной точки нет */}
      <div style={{ display: 'flex', alignItems: 'center', gap: group.length > 1 || persona ? 9 : 6, minWidth: 0 }}>
        {group.length > 1 ? (
          // Горизонтальный стек внахлёст: важно количество участников, а не лица.
          // Кольцо статуса — на ведущей (первой): она поверх остальных по z-index
          <div style={{ display: 'flex', flexShrink: 0 }}>
            {group.map((p, i) => (
              <div key={p!.id} style={{
                marginLeft: i === 0 ? 0 : -7, position: 'relative', zIndex: group.length - i,
                borderRadius: '50%', border: `1.5px solid ${cardBg}`, display: 'flex',
              }}>
                {i === 0 ? (
                  <StatusIndicator status={s.status}><PersonaAvatar persona={p!} size={18} /></StatusIndicator>
                ) : (
                  <PersonaAvatar persona={p!} size={18} />
                )}
              </div>
            ))}
          </div>
        ) : persona ? (
          <StatusIndicator status={s.status}><PersonaAvatar persona={persona} size={22} /></StatusIndicator>
        ) : (
          <StatusIndicator status={s.status} />
        )}
        <span style={{
          fontSize: 13.5, fontWeight: isActive ? 700 : 600, color: C.textHeading,
          flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {s.name ?? fallbackName}
        </span>
        {workflowRunning && (
          <div title="Выполняется Workflow" style={{
            display: 'flex', alignItems: 'center', gap: 3, padding: '1px 5px',
            background: C.accentLight, border: `1px solid ${C.accentMuted}`, borderRadius: 4, flexShrink: 0,
          }}>
            <div className="tool-spinner" style={{ width: 8, height: 8 }} />
            <span style={{ fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, color: C.accent, lineHeight: 1 }}>WF</span>
          </div>
        )}
        <ExpiryBadge session={s} />
        {/* Закреплённый чат: пока кнопки скрыты, признак держит статичная иконка */}
        {s.isPinned && !showActions && (
          <Pin size={11} strokeWidth={2} fill="currentColor" style={{ color: C.textMuted, flexShrink: 0 }} />
        )}
        <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, lineHeight: 1, whiteSpace: 'nowrap', flexShrink: 0 }}>
          {chatTime(s.createdAt)}
        </span>
      </div>

      {/* Строка 2: происхождение, механика, подпись собеседника — под аватаром, во всю ширину */}
      {(subtitle || origin || mechanic) && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 5, minWidth: 0 }}>
          {origin && <ChatOriginBadge origin={origin} style={{ flexShrink: 0 }} />}
          {mechanic && <TeamMechanicBadge id={mechanic} size="sm" />}
          {subtitle && (
            <span style={{ fontSize: 11.5, fontWeight: 600, color: accent, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {subtitle}
            </span>
          )}
        </div>
      )}

      {/* Строка 3: превью последнего сообщения — во всю ширину карточки */}
      {s.lastMessage && (
        <div style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {teamTurnPreview(s.lastMessage) ?? s.lastMessage}
        </div>
      )}

      {/* Действия — поверх времени: так текст карточки не отдаёт им ширину */}
      {showActions && (
        <div style={{
          position: 'absolute', top: isMobile ? 8 : 5, right: isMobile ? 10 : 6,
          display: 'flex', background: cardBg, borderRadius: R.md, paddingLeft: 4,
        }}>
          {onTogglePin && (
            <IconButton
              onClick={e => { e.stopPropagation(); onTogglePin(); }}
              title={s.isPinned ? 'Открепить' : 'Закрепить'}
              size="xs" active={s.isPinned}
            >
              <Pin size={14} strokeWidth={2} fill={s.isPinned ? 'currentColor' : 'none'} />
            </IconButton>
          )}
          <IconButton onClick={e => { e.stopPropagation(); onEdit(); }} title="Настройки чата" size="xs">
            <SquarePen size={14} strokeWidth={2} />
          </IconButton>
          <IconButton onClick={e => { e.stopPropagation(); onDelete(); }} title="Удалить чат" size="xs" tone="danger">
            <Trash2 size={14} strokeWidth={2} />
          </IconButton>
        </div>
      )}
    </div>
  );
}
