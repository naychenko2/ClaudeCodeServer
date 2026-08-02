import { useState, useRef, useEffect } from 'react';
import { AlertCircle, CheckCircle2, Clock, Columns3, MoreVertical, Pencil, Pin, Tags, Trash2, Users, Wrench } from 'lucide-react';
import type { Session } from '../types';
import { C, R, SHADOW, FONT } from '../lib/design';
import { IconButton, Menu, MenuItem } from './ui';
import { ICON_STROKE } from './ui/icons';
import { StatusIndicator } from './StatusIndicator';
import { ExpiryBadge } from './ExpiryBadge';
import { ChatOriginBadge } from './ChatOriginBadge';
import { TagChip } from './TagChip';
import { describeTaskChat, resolveChatOrigin, type TaskChatInfo, type TaskChatStatusKind } from '../lib/chatOrigin';
import { getPersonaById, personaLabel } from '../lib/personas';
import { useTasks } from '../lib/tasks';
import { agentDotColor } from './AgentSelector';
import { PersonaBackdrop } from '../features/personas/PersonaFace';
import { TeamMechanicBadge } from '../features/team/TeamMechanicBadge';
import { teamTurnPreview } from '../features/team/teamMechanics';
import { getLastMechanic } from '../lib/lastMechanic';
import { teamImplementTone, teamImplementStageShort, teamImplementBadgeText } from '../lib/teamImplement';

// Ширина правой зоны под лицо собеседника; на её левой кромке стоит столбик действий
const COMPANION_W = 84;

// Кнопка меню действий (IconButton size="xs") и её отступ от правого края; по
// вертикали она стоит по центру карточки. Место одно на всех карточках — и с
// собеседником, и без, чтобы кнопка не прыгала при переходе между чатами
const ACTION_BOX = 24;
const ACTIONS_RIGHT = 4;

// Минимальная высота карточки: не меньше двух текстовых строк (название + превью,
// чтобы чат без последнего сообщения не схлопывался) и не меньше кнопки действий
const TWO_LINES = 42;

// Умеет ли устройство наводить курсор. На тач-экранах hover не наступает никогда,
// поэтому кнопки действий там показываем постоянно (приём как в MarkdownViewer)
const CAN_HOVER = typeof window !== 'undefined' && !window.matchMedia('(hover: none)').matches;

// Подложки под кнопкой действий нет: в покое видна только иконка, фон появляется
// под курсором — его рисует сам IconButton

// Собеседник в правом углу карточки — общий PersonaBackdrop (вынесен в PersonaFace.tsx,
// его же использует hero-шапка открытого чата); ширина полосы = COMPANION_W.

// Цвет и иконка строки статуса выполнения задачи (вариант A)
const TASK_STATUS_COLOR: Record<TaskChatStatusKind, string> = {
  run: C.accent, wait: C.warningText, done: C.successText,
  todo: C.textMuted, error: C.danger, deleted: C.textMuted,
};
const TASK_STATUS_ICON: Partial<Record<TaskChatStatusKind, typeof Clock>> = {
  wait: Clock, done: CheckCircle2, error: AlertCircle,
};

// Строка статуса чата-задачи вместо шумного превью промпта: маркер + подпись
// статуса + прогресс подзадач + срок. Заменяет и превью, и плашку происхождения.
function TaskStatusLine({ info }: { info: TaskChatInfo }) {
  const { status, subDone, subTotal, dueText, dueUrgent } = info;
  const color = TASK_STATUS_COLOR[status.kind];
  const Icon = TASK_STATUS_ICON[status.kind];
  const meta: React.CSSProperties = { fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, flexShrink: 0 };
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 5, minWidth: 0, marginTop: 1,
      fontSize: 11.5, color,
    }}>
      {status.spinner
        ? <div className="tool-spinner" style={{ width: 11, height: 11 }} />
        : Icon
          ? <Icon size={11} strokeWidth={2.2} style={{ flexShrink: 0 }} />
          : <span style={{ width: 6, height: 6, borderRadius: '50%', background: 'currentColor', flexShrink: 0 }} />}
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{status.label}</span>
      {subTotal > 0 && status.kind !== 'deleted' && <span style={meta}>{subDone}/{subTotal}</span>}
      {dueText && status.kind !== 'done' && status.kind !== 'deleted' && (
        <span style={{ ...meta, color: dueUrgent ? C.danger : C.textMuted }}>{dueText}</span>
      )}
    </div>
  );
}

// Маркер режима «Командная реализация» в строке названия (макет team-implement-mode,
// секция 2): плашка 17px по образцу WF-бейджа, иконка Users + короткая форма стадии,
// тон — по тому, кто должен действовать; тултип — полная строка бейджа
function TeamImplementMarker({ session }: { session: Session }) {
  const ti = session.teamImplement;
  if (!ti) return null;
  const tone = teamImplementTone(ti.stage);
  const toneStyle = tone === 'work'
    ? { background: C.accentLight, color: C.accent, border: `1px solid ${C.accentMuted}` }
    : tone === 'wait'
      ? { background: C.warningBg, color: C.warningText, border: `1px solid ${C.warning}` }
      : { background: C.bgSelected, color: C.textMuted, border: '1px solid transparent' };
  return (
    <span
      title={teamImplementBadgeText(ti.stage, ti.waveNumber, ti.plannedWaves)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4, height: 17, padding: '0 6px',
        borderRadius: R.max, fontSize: 10, fontWeight: 600, lineHeight: 1,
        flexShrink: 0, whiteSpace: 'nowrap', ...toneStyle,
      }}
    >
      <Users size={10} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      {teamImplementStageShort(ti.stage, ti.waveNumber, ti.plannedWaves)}
    </span>
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
  onDelete: () => void;
  // Не задан — чат без закрепления (списки проекта)
  onTogglePin?: () => void;
  // Общие теги чата (имя + цвет из реестра) — строка чипов под названием
  tags?: { name: string; color?: string }[];
  // Снять тег с чата (hover-крестик на чипе; на тач удаление — через меню маркировки)
  onRemoveTag?: (name: string) => void;
  // Открыть меню маркировки тегами (кнопка в действиях; якорь — rect кнопки для fixed-позиции)
  onAssignTags?: (anchor: DOMRect) => void;
  // Переименование чата прямо в карточке (пункт меню «Переименовать»). Не задан —
  // пункта нет. Отклонённый промис оставляет карточку в режиме правки с набранным
  // текстом: имя не сохранилось, и молча выкидывать пользователя из ввода нельзя
  onRename?: (name: string) => Promise<unknown>;
  // «На стену» (фича wall, воркспейс): добавить чат в набор стены. Не задан — пункта нет
  onAddToWall?: () => void;
}

/**
 * Карточка чата в боковых списках (глобальном ChatList и проектном SessionList).
 * Раскладка: строка «статус + собеседник + название + время», под ней — бейджи
 * и превью последнего сообщения во всю ширину. Высота — не меньше двух текстовых
 * строк, поэтому карточки в списке стоят единой сеткой. Действия всплывают по
 * наведению вертикальным столбиком на стыке текста и лица собеседника.
 */
export function ChatCard({
  session: s, isActive, isMobile, fallbackName, online, hovered, workflowRunning,
  onSelect, onHover, onDelete, onTogglePin, tags, onRemoveTag, onAssignTags, onRename, onAddToWall,
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
  // Чат-исполнитель задачи: компактная раскладка без тройного повтора заголовка
  // (имя без «Задача:», статус выполнения вместо промпта, без плашки-дубля).
  // Подписка на стор задач обязательна: без неё строка статуса оживала бы только
  // при следующем событии сессии, а у свежего чата исполнения залипала бы на
  // «Загрузка…» до конца загрузки стора
  useTasks();
  const taskChat = describeTaskChat(s);
  const displayName = (taskChat ? taskChat.title : s.name) || fallbackName;
  // Последняя запущенная в чате механика команды — компактный бейдж
  const mechanic = getLastMechanic(s.id);
  // Открытое меню действий: rect кнопки-триггера (null — закрыто)
  const [menu, setMenu] = useState<DOMRect | null>(null);
  // Правка названия прямо в карточке: пункт меню превращает заголовок в поле ввода —
  // ради одного имени открывать форму настроек чата не надо. У чата-исполнителя задачи
  // переименования нет: там в заголовке стоит имя ЗАДАЧИ (taskChat.title), и правка
  // s.name не изменила бы ни строчки на экране
  const canRename = !!onRename && !taskChat;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [saving, setSaving] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  // Отмена по Esc: снятый с DOM input может успеть отдать blur, а тот сохраняет —
  // флаг гасит ровно этот случай, не трогая обычный уход фокуса
  const cancelledRef = useRef(false);
  useEffect(() => {
    if (!editing) return;
    inputRef.current?.focus();
    inputRef.current?.select();
  }, [editing]);

  // Имя на момент входа в правку. Пока поле открыто, название может приехать со
  // стороны: авто-заголовок нового чата (событие chat_renamed) или действие «Обновить
  // название» из AI-хаба. Ввод при этом НЕ трогаем — набранное важнее, а сохранение
  // всё равно победит (бэкенд на явном имени ставит NameLocked). Но молчать нельзя:
  // разошлись — подсвечиваем поле и объясняем в тултипе, что именно перезапишем
  const startNameRef = useRef<string | null>(null);
  const externalName = editing && (s.name ?? '') !== (startNameRef.current ?? '') ? (s.name ?? '') : null;

  const startRename = () => { startNameRef.current = s.name ?? ''; setDraft(s.name ?? ''); setEditing(true); };
  const cancelRename = () => { setEditing(false); setSaving(false); };
  const commitRename = async () => {
    if (!onRename || !editing || saving) return;
    const next = draft.trim();
    // Пустое поле или имя без изменений — просто выходим из правки, запрос не шлём
    if (!next || next === (s.name ?? '')) { cancelRename(); return; }
    setSaving(true);
    try {
      await onRename(next);
      setEditing(false);
    } catch { /* сохранить не вышло — остаёмся в правке, набранный текст на месте */ }
    setSaving(false);
  };
  // Действия: с мышью — по наведению, на тач-устройствах — у выбранного чата.
  // Показывать их на тач всегда нельзя: они висели бы поверх лица собеседника на
  // каждой карточке. Тап по чату и открывает его, и раскрывает кнопки.
  // Проверяем возможность hover, а не ширину: на планшете в широкой раскладке
  // isMobile=false, но навести всё равно нечем
  // Во время правки названия действий нет: кнопка «⋮» стоит вплотную к полю ввода,
  // и её меню (закрепить/теги/удалить) применялось бы к чату, имя которого ещё не
  // сохранено. Уходит вся кнопка, а не только меню — раскладку она не двигает (absolute)
  const showActions = online && !editing && (CAN_HOVER ? hovered : isActive);
  const cardBg = isActive ? C.accentLight : C.bgWhite;
  // Лицо для подложки: у группы — ведущая (первая в составе)
  const backdropPersona = group.length > 1 ? group[0] : persona;
  const padV = isMobile ? 14 : 11;
  const minHeight = Math.max(padV * 2 + TWO_LINES, ACTION_BOX + 8);
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
        paddingTop: padV,
        paddingBottom: padV,
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
        justifyContent: 'center',
        gap: 3,
        // единая высота карточек в списке: короткий чат не выше длинного
        minHeight,
        boxSizing: 'border-box',
      }}
    >
      {/* Собеседник — в правом углу; в группе лицо даёт ведущая.
          Рисуется до акцентной полосы, иначе накрыла бы её собой */}
      {backdropPersona && <PersonaBackdrop persona={backdropPersona} width={COMPANION_W} />}

      {/* Акцентная полоса слева — явный маркер текущего чата (у чатов персоны — её цветом) */}
      {isActive && (
        <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 4, background: accent }} />
      )}

      {/* Текст карточки. Когда есть собеседник, справа под него отведена полоса
          (лицо + кнопки поверх) — заголовок и превью обрываются на её границе.
          Без персоны резерв не держим: текст идёт во всю ширину, а кнопки действий
          (при наведении, с непрозрачной подложкой cardBg) перекрывают его хвост */}
      <div style={{
        position: 'relative', display: 'flex', flexDirection: 'column', gap: 3, minWidth: 0,
        paddingRight: backdropPersona ? COMPANION_W - (isMobile ? 16 : 12) : 0,
      }}>
        {/* Строка 1: статус точкой, признак задачи, название, метки срока и закрепления */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
          <StatusIndicator status={s.status} title={companionTitle} />
          {/* Тихий ключ-признак задачи: «Задача» уходит в иконку, весь текст — в тултип */}
          {taskChat && (
            <span title={taskChat.fullLabel} aria-label={taskChat.fullLabel} style={{ display: 'flex', flexShrink: 0, color: C.textMuted }}>
              <Wrench size={12} strokeWidth={2.2} />
            </span>
          )}
          {editing ? (
            // Поле правки стоит ровно на месте заголовка: строка карточки не
            // перестраивается, соседние метки не прыгают. Клики гасим — иначе
            // попытка поставить курсор открывала бы чат (onClick всей карточки)
            <input
              ref={inputRef}
              value={draft}
              disabled={saving}
              placeholder={fallbackName}
              aria-label="Название чата"
              title={externalName
                ? `Пока вы правите, чат переименовали в «${externalName}». Сохранение перезапишет это название.`
                : undefined}
              onChange={e => setDraft(e.target.value)}
              onClick={e => e.stopPropagation()}
              onMouseDown={e => e.stopPropagation()}
              onKeyDown={e => {
                e.stopPropagation();
                if (e.key === 'Enter') { e.preventDefault(); void commitRename(); }
                if (e.key === 'Escape') { e.preventDefault(); cancelledRef.current = true; cancelRename(); }
              }}
              onBlur={() => {
                if (cancelledRef.current) { cancelledRef.current = false; return; }
                void commitRename();
              }}
              style={{
                // Высота с border-box держится вровень со строкой заголовка (13.5px
                // текста ≈ 18px строки) — иначе появление поля толкало строку вниз.
                // Тон приглушённый: поле правки в списке — не акцент, оранжевая рамка
                // здесь кричала. Внимание к нему привлекают курсор и выделенный текст
                flex: '1 1 auto', minWidth: 0, boxSizing: 'border-box',
                height: 19, padding: '0 5px', lineHeight: 1,
                fontSize: 13.5, fontWeight: 600, fontFamily: 'inherit', color: C.textHeading,
                background: C.bgSelected, borderRadius: R.sm, outline: 'none',
                // Жёлтая рамка — единственный сигнал, что имя увели из-под правки
                border: `1px solid ${externalName ? C.warning : C.border}`,
                opacity: saving ? 0.6 : 1,
              }}
            />
          ) : (
            <span title={displayName} style={{
              fontSize: 13.5, fontWeight: isActive ? 700 : 600, color: C.textHeading,
              flex: '0 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {displayName}
            </span>
          )}
          <TeamImplementMarker session={s} />
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

        {/* Строка общих тегов (макет chat-tags-switch): чипы под названием. Крестик
            снятия — только там, где есть hover; на тач снятие идёт через меню маркировки */}
        {tags && tags.length > 0 && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 4, flexWrap: 'wrap', minWidth: 0 }}>
            {tags.map(t => (
              <TagChip key={t.name} name={t.name} color={t.color}
                onRemove={onRemoveTag && CAN_HOVER ? () => onRemoveTag(t.name) : undefined} />
            ))}
          </div>
        )}

        {/* Чат-задача: одна строка статуса выполнения вместо превью-промпта и
            плашки-дубля. Обычный чат — превью + плашка происхождения как раньше */}
        {taskChat ? (
          <TaskStatusLine info={taskChat} />
        ) : (
          <>
            {/* Строка 2: превью последнего сообщения */}
            {s.lastMessage && (
              <div style={{
                minWidth: 0, fontSize: 12, color: C.textMuted, lineHeight: 1.4,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>
                {teamTurnPreview(s.lastMessage) ?? s.lastMessage}
              </div>
            )}

            {/* Под описанием: происхождение и механика — иконка с подписью, прижаты
                влево (собеседник ушёл в подложку) */}
            {(origin || mechanic) && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 5, minWidth: 0, marginTop: 1 }}>
                {origin && <ChatOriginBadge origin={origin} style={{ flexShrink: 0 }} />}
                {mechanic && <TeamMechanicBadge id={mechanic} size="sm" />}
              </div>
            )}
          </>
        )}
      </div>

      {/* Действия — одной кнопкой «⋮» у правого края по центру высоты, место одно и
          то же при любом составе карточки. Само меню открывается порталом по rect
          кнопки (anchor-режим Menu): список чатов скроллится, и absolute-меню
          обрезалось бы его overflow */}
      {showActions && (
        <div style={{
          position: 'absolute', top: '50%', transform: 'translateY(-50%)',
          right: ACTIONS_RIGHT, zIndex: 1, display: 'flex',
        }}>
          <IconButton
            onClick={e => {
              e.stopPropagation();
              const r = e.currentTarget.getBoundingClientRect();
              setMenu(prev => (prev ? null : r));
            }}
            title="Действия с чатом"
            size="xs"
            active={!!menu}
          >
            <MoreVertical size={14} strokeWidth={2} />
          </IconButton>
        </div>
      )}

      {menu && !editing && (
        <Menu anchor={menu} onClose={() => setMenu(null)} minWidth={158}
          // Высота меню решает, куда его раскрыть (вверх/вниз) — считаем по составу
          maxHeight={(1 + (canRename ? 1 : 0) + (onTogglePin ? 1 : 0) + (onAssignTags ? 1 : 0) + (onAddToWall ? 1 : 0)) * 34 + 10}
          gap={4}>
          {canRename && (
            <MenuItem
              icon={<Pencil size={15} strokeWidth={2} />}
              label="Переименовать"
              onClick={e => { e.stopPropagation(); setMenu(null); startRename(); }}
            />
          )}
          {onTogglePin && (
            <MenuItem
              icon={<Pin size={15} strokeWidth={2} fill={s.isPinned ? 'currentColor' : 'none'} />}
              label={s.isPinned ? 'Открепить' : 'Закрепить'}
              onClick={e => { e.stopPropagation(); setMenu(null); onTogglePin(); }}
            />
          )}
          {onAssignTags && (
            <MenuItem
              icon={<Tags size={15} strokeWidth={2} />}
              label="Теги"
              // Меню маркировки открывается по тому же якорю: кнопка «⋮» уже
              // исчезнет вместе с этим меню, и её rect брать будет неоткуда
              onClick={e => { e.stopPropagation(); const anchor = menu; setMenu(null); onAssignTags(anchor); }}
            />
          )}
          {onAddToWall && (
            <MenuItem
              icon={<Columns3 size={15} strokeWidth={2} />}
              label="На стену"
              onClick={e => { e.stopPropagation(); setMenu(null); onAddToWall(); }}
            />
          )}
          <MenuItem
            icon={<Trash2 size={15} strokeWidth={2} />}
            label="Удалить"
            danger
            onClick={e => { e.stopPropagation(); setMenu(null); onDelete(); }}
          />
        </Menu>
      )}
    </div>
  );
}
