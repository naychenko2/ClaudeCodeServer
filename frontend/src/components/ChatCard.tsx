import { Pin, SquarePen, Trash2 } from 'lucide-react';
import type { Persona, Session } from '../types';
import { C, R, SHADOW, FONT } from '../lib/design';
import { IconButton } from './ui';
import { StatusIndicator } from './StatusIndicator';
import { ExpiryBadge } from './ExpiryBadge';
import { ChatOriginBadge } from './ChatOriginBadge';
import { resolveChatOrigin } from '../lib/chatOrigin';
import { getPersonaById, personaLabel } from '../lib/personas';
import { agentDotColor } from './AgentSelector';
import { PersonaFace } from '../features/personas/PersonaFace';
import { TeamMechanicBadge } from '../features/team/TeamMechanicBadge';
import { teamTurnPreview } from '../features/team/teamMechanics';
import { getLastMechanic } from '../lib/lastMechanic';

// Ширина правой зоны: под ней ровно помещаются три кнопки действий (по 24) с их
// отступом. Лицо собеседника занимает эту же полосу, кнопки всплывают поверх него
const COMPANION_W = 84;
// Лицо плотное у правого края и тает влево; хвост доводит до левого края цветовая вуаль
const BACKDROP_FADE = 'linear-gradient(to left, #000 40%, transparent)';

// Стоп цветовой вуали. Цвета персон — hex, но фолбэк палитры это CSS-переменная,
// к которой альфу не приклеить, поэтому для неё считаем прозрачность через color-mix
function veilStop(color: string, alpha: number, pos: number): string {
  const c = /^#[0-9a-f]{6}$/i.test(color)
    ? color + Math.round(alpha * 255).toString(16).padStart(2, '0')
    : `color-mix(in srgb, ${color} ${Math.round(alpha * 100)}%, transparent)`;
  return `${c} ${pos}%`;
}

// Умеет ли устройство наводить курсор. На тач-экранах hover не наступает никогда,
// поэтому кнопки действий там показываем постоянно (приём как в MarkdownViewer)
const CAN_HOVER = typeof window !== 'undefined' && !window.matchMedia('(hover: none)').matches;

// Стекло под кнопками действий: они лежат поверх лица собеседника, глухая
// подложка вырезала бы в нём прямоугольник. Текст в зону лица не заходит вовсе,
// поэтому под надписями никаких облачек нет
const GLASS: React.CSSProperties = {
  background: C.glass,
  backdropFilter: 'blur(6px)',
  WebkitBackdropFilter: 'blur(6px)',
  borderRadius: R.md,
  padding: '2px 7px',
};

/**
 * Собеседник в правом углу карточки: лицо персоны почти в полную силу плюс вуаль
 * её цветом, уводящая изображение влево. Ширина полосы — как у блока кнопок,
 * которые лежат поверх. Прозрачность у фото и инициалов разная: буквы визуально
 * легче фотографии и при равной прозрачности выглядели бы бледнее
 */
function PersonaBackdrop({ persona }: { persona: Persona }) {
  const color = agentDotColor(persona.avatar?.color);
  const hasPhoto = persona.avatar?.kind === 'image';

  return (
    <>
      {/* Вуаль цветом персоны: подхватывает лицо у его края и длинной мягкой
          растяжкой уводит цвет влево — стык картинки с фоном карточки не читается.
          Ступени по альфе, а не один линейный переход: так спад плавнее */}
      <div aria-hidden style={{
        position: 'absolute', inset: 0, pointerEvents: 'none', opacity: 0.22,
        background: 'linear-gradient(to left, '
          + [
            veilStop(color, 1, 0),
            veilStop(color, 0.82, 16),
            veilStop(color, 0.5, 38),
            veilStop(color, 0.22, 62),
            veilStop(color, 0.06, 82),
            veilStop(color, 0, 100),
          ].join(', ') + ')',
      }} />
      <PersonaFace
        persona={persona} align="right" fontSize={38}
        style={{
          position: 'absolute', top: 0, right: 0, bottom: 0, width: COMPANION_W,
          pointerEvents: 'none', userSelect: 'none',
          WebkitMaskImage: BACKDROP_FADE, maskImage: BACKDROP_FADE,
          opacity: hasPhoto ? 0.92 : 0.85,
          paddingRight: hasPhoto ? undefined : 10,
        }}
      />
    </>
  );
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
  // Действия: с мышью — по наведению, на тач-устройствах — у выбранного чата.
  // Показывать их на тач всегда нельзя: они висели бы поверх лица собеседника на
  // каждой карточке. Тап по чату и открывает его, и раскрывает кнопки.
  // Проверяем возможность hover, а не ширину: на планшете в широкой раскладке
  // isMobile=false, но навести всё равно нечем
  const showActions = online && (CAN_HOVER ? hovered : isActive);
  const cardBg = isActive ? C.accentLight : C.bgWhite;
  // Лицо для подложки: у группы — ведущая (первая в составе)
  const backdropPersona = group.length > 1 ? group[0] : persona;
  // Стекло — только когда под кнопками есть лицо; на чистом фоне глухая подложка
  const glass = backdropPersona ? GLASS : { background: cardBg, borderRadius: R.md, padding: '2px 4px' };
  // Собеседник назван словами только в тултипе точки статуса — в самой карточке
  // его показывает подложка, строку под текст он не занимает
  const companionTitle = group.length > 1 ? (
    <>
      Групповой · {group.length} участника
      <span style={{ display: 'block', fontWeight: 400, color: C.textMuted, marginTop: 2 }}>
        {group.map(p => personaLabel(p!)).join(' · ')}
      </span>
    </>
  ) : persona ? personaLabel(persona) : undefined;

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
      {/* Собеседник — в правом углу; в группе лицо даёт ведущая.
          Рисуется до акцентной полосы, иначе накрыла бы её собой */}
      {backdropPersona && <PersonaBackdrop persona={backdropPersona} />}

      {/* Акцентная полоса слева — явный маркер текущего чата (у чатов персоны — её цветом) */}
      {isActive && (
        <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: accent }} />
      )}

      {/* Текст карточки. Правая полоса не его: там лицо собеседника и кнопки действий,
          поэтому заголовок и превью обрываются на её границе, а не наезжают.
          Резерв держим и без персоны — иначе кнопки накрыли бы хвост превью */}
      <div style={{
        position: 'relative', display: 'flex', flexDirection: 'column', gap: 3, minWidth: 0,
        paddingRight: COMPANION_W - (isMobile ? 16 : 12),
      }}>
        {/* Строка 1: статус точкой, название, метки срока и закрепления */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
          <StatusIndicator status={s.status} title={companionTitle} />
          <span style={{
            fontSize: 13.5, fontWeight: isActive ? 700 : 600, color: C.textHeading,
            flex: '0 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {s.name ?? fallbackName}
          </span>
          <ExpiryBadge session={s} />
          {/* Закрепление: иконка-признак, сама кнопка живёт в блоке действий */}
          {s.isPinned && (
            <Pin size={11} strokeWidth={2} fill="currentColor" style={{ color: C.textMuted, flexShrink: 0 }} />
          )}
          {workflowRunning && (
            <div title="Выполняется Workflow" style={{
              display: 'flex', alignItems: 'center', gap: 3, padding: '1px 5px',
              background: C.accentLight, border: `1px solid ${C.accentMuted}`, borderRadius: 4, flexShrink: 0,
            }}>
              <div className="tool-spinner" style={{ width: 8, height: 8 }} />
              <span style={{ fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, color: C.accent, lineHeight: 1 }}>WF</span>
            </div>
          )}
        </div>

        {/* Строка 2: происхождение и механика. Собеседник ушёл в подложку */}
        {(origin || mechanic) && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 5, minWidth: 0 }}>
            {origin && <ChatOriginBadge origin={origin} style={{ flexShrink: 0 }} />}
            {mechanic && <TeamMechanicBadge id={mechanic} size="sm" />}
          </div>
        )}

        {/* Строка 3: превью последнего сообщения */}
        {s.lastMessage && (
          <div style={{
            minWidth: 0, fontSize: 12, color: C.textMuted, lineHeight: 1.4,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {teamTurnPreview(s.lastMessage) ?? s.lastMessage}
          </div>
        )}
      </div>

      {/* Действия — в правой полосе поверх лица, прижаты к низу карточки */}
      {showActions && (
        <div style={{
          ...glass, position: 'absolute', bottom: isMobile ? 8 : 6, right: isMobile ? 12 : 8, zIndex: 1,
          display: 'flex', alignItems: 'center',
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
