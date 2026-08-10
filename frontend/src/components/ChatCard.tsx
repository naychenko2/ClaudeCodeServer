import { useState, useRef, useEffect, type CSSProperties } from 'react';
import { AlertCircle, Bell, BellOff, CheckCircle2, Clock, Columns3, Hourglass, MoreVertical, Pencil, Pin, Tags, Trash2, Users, Wrench } from 'lucide-react';
import type { Session } from '../types';
import { C, R, SHADOW, FONT } from '../lib/design';
import { ChatTopicBackdrop, ChatTopicIcon, IconButton, Menu, MenuItem } from './ui';
import { ICON_STROKE } from './ui/icons';
import { STATUS_CONFIG, STATUS_GLOW } from './StatusIndicator';
import { ExpiryBadge } from './ExpiryBadge';
import { ExpiryPicker } from './chat/ExpiryPicker';
import { expiresAt, expiryOptionLabel, formatExpiryDate } from '../lib/expiry';
import { updateChatFields } from '../lib/chatUpdate';
import { isNotifySupported, setChatNotifyEnabled, useChatNotifyOn } from '../lib/notify';
import { showToast } from '../lib/toast';
import { ChatOriginBadge } from './ChatOriginBadge';
import { TagChip } from './TagChip';
import { describeTaskChat, resolveChatOrigin, type TaskChatInfo, type TaskChatStatusKind } from '../lib/chatOrigin';
import { useHasUnread } from '../lib/chatReadState';
import { getPersonaById } from '../lib/personas';
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
  // «На стену» (воркспейс): добавить чат в набор стены. Не задан — пункта нет
  onAddToWall?: () => void;
  // Изменение чата из меню карточки (мьют уведомлений, срок хранения) — обновлённую
  // сессию возвращает бэкенд, список обновляет ею своё состояние. Не задан — пунктов нет
  onEdited?: (s: Session) => void;
  // Доп. отступ содержимого слева (px). В дереве чатов контрол ветки садится в шов
  // на левый край карточки — под ним нужно освободить место, иначе он ляжет на
  // первые буквы названия. Кромка состояния и лицо собеседника позиционированы
  // абсолютно и на месте остаются.
  leadingInset?: number;
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
  onEdited, leadingInset = 0,
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

  // Long-press на мобиле открывает меню действий карточки — аналог кнопки «⋮»,
  // который на тач-экране виден только у активного чата. Долгий тап работает на
  // любой карточке, не выбирая её: флаг longPressFired гасит клик, чтобы menu
  // не закрывалось открытием чата
  const lpTimer = useRef<number | null>(null);
  const lpFired = useRef(false);
  const lpStart = useRef<{ x: number; y: number } | null>(null);
  const beginLongPress = (e: React.TouchEvent) => {
    if (!isMobile || !online || editing) return;
    const t = e.touches[0];
    lpStart.current = { x: t.clientX, y: t.clientY };
    lpFired.current = false;
    lpTimer.current = window.setTimeout(() => {
      lpFired.current = true;
      // Якорь — правый край карточки, там же где «⋮»: меню встанет как от кнопки
      const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
      setMenu(new DOMRect(r.right - ACTIONS_RIGHT - ACTION_BOX, r.top + (r.height - ACTION_BOX) / 2, ACTION_BOX, ACTION_BOX));
    }, 500);
  };
  const killLongPress = () => {
    if (lpTimer.current != null) { clearTimeout(lpTimer.current); lpTimer.current = null; }
  };
  const onTpMove = (e: React.TouchEvent) => {
    if (!lpStart.current) return;
    const t = e.touches[0];
    if (Math.abs(t.clientX - lpStart.current.x) > 10 || Math.abs(t.clientY - lpStart.current.y) > 10) killLongPress();
  };
  // Выбор срока хранения — второе меню по тому же якорю (приём пункта «Теги»):
  // пикер сроков не пункт списка, поэтому живёт в своём поповере
  const [expiryMenu, setExpiryMenu] = useState<DOMRect | null>(null);
  // Срок хранения правится только при живом колбэке обновления сессии (online + onEdited)
  const canEditChat = !!onEdited && online;
  // Мьют чата: тумблер пишет notificationsMuted. Пункт прячем в браузерах без
  // Notification API — глушить там нечего
  const notifyOn = useChatNotifyOn(s);
  const canMute = canEditChat && isNotifySupported();
  const expiryAt = expiresAt(s);
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
  const [startName, setStartName] = useState<string | null>(null);
  const externalName = editing && (s.name ?? '') !== (startName ?? '') ? (s.name ?? '') : null;

  const startRename = () => { setStartName(s.name ?? ''); setDraft(s.name ?? ''); setEditing(true); };
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

  // Заглушить/включить уведомления по чату. Включение может дёрнуть запрос разрешения
  // браузера — поэтому идёт прямо из обработчика клика, без промежуточных эффектов
  const toggleNotify = async () => {
    try {
      const res = await setChatNotifyEnabled(s, !notifyOn);
      if (res.session) onEdited?.(res.session);
      // Просили включить, а разрешение не выдано — иначе пункт выглядит нерабочим
      if (!notifyOn && !res.enabled) showToast('Уведомления', 'Браузер не дал разрешение на уведомления', 'info');
    } catch {
      showToast('Уведомления', 'Не удалось изменить уведомления чата', 'info');
    }
  };

  // Срок хранения из меню карточки — тот же набор пресетов, что у часов в шапке чата
  const pickExpiry = async (minutes: number | null) => {
    setExpiryMenu(null);
    if (minutes === (s.expiresAfterMinutes ?? null)) return;
    try {
      onEdited?.(await updateChatFields(s, { expiresAfterMinutes: minutes }));
    } catch {
      showToast('Время жизни', 'Не удалось изменить срок жизни чата', 'info');
    }
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

  // Статус несёт перелив ФОНА карточки (.cc-tint): по фону слева направо идёт
  // еле заметная волна статусного цвета. Ауры вокруг и цветного бордюра нет.
  // У живых (breath) волна движется, у error — ровная подкраска. Цвет и силу
  // подмешивания отдаём в CSS переменных --cc-status-c / --cc-tint-a
  const glow = STATUS_GLOW[s.status];
  const hasGlow = glow.alpha > 0;
  const glowClass = !hasGlow ? ''
    : glow.breath
      ? ' cc-tint cc-tint--flow' + (glow.slow ? ' cc-tint--slow' : '')
      : ' cc-tint cc-tint--static';

  // Непрочитанный чат мягко мерцает фоном нейтральным серым — но только если
  // статусного перелива нет: приоритет у него, два движения на карточке дрались
  // бы. Слой у обоих один и тот же (::after), так что классы взаимоисключающие.
  // Хук, а не голая функция: по подписке метка гаснет сразу при открытии чата
  const unread = useHasUnread(s.updatedAt, s.id);
  const unreadClass = !hasGlow && unread ? ' cc-unread' : '';
  const statusVars = {
    '--cc-status-c': STATUS_CONFIG[s.status].color,
    // Сила подмешивания в фон: alpha из STATUS_GLOW (45..72) задумана под
    // свечение — для заливки её ужимаем втрое и добавляем 10 п.п. Даёт 25..34%:
    // ниже ~10% подкраска на кремовом фоне уже неразличима
    '--cc-tint-a': `${Math.round(glow.alpha / 3) + 10}%`,
  } as CSSProperties;

  // У выбранного чата фон обычно подкрашен accentLight, но если по карточке идёт
  // статусный перелив — оставляем белый: волна статусного цвета поверх оранжевой
  // заливки читается грязно, цвет статуса перестаёт быть собой. Что чат выбран,
  // и без заливки видно по акцентной полосе слева и рамке
  const cardBg = isActive && !hasGlow ? C.accentLight : C.bgWhite;
  // Лицо для подложки: у группы — ведущая (первая в составе)
  const backdropPersona = group.length > 1 ? group[0] : persona;
  const padV = isMobile ? 14 : 11;
  const minHeight = Math.max(padV * 2 + TWO_LINES, ACTION_BOX + 8);

  return (
    <div
      onClick={() => { if (lpFired.current) { lpFired.current = false; return; } onSelect(); }}
      onMouseEnter={() => onHover(true)}
      onMouseLeave={() => onHover(false)}
      onTouchStart={beginLongPress}
      onTouchMove={onTpMove}
      onTouchEnd={killLongPress}
      onTouchCancel={killLongPress}
      // cc-card-press — прожатие под нажатием (index.css). В режиме правки названия
      // его нет: там внутри поле ввода, и вдавливать карточку при постановке
      // курсора незачем
      className={'cc-card-shadow' + (editing ? '' : ' cc-card-press') + glowClass + unreadClass}
      style={{
        position: 'relative',
        // отдельные longhand-свойства: со shorthand + undefined React обнуляет padding-left
        paddingTop: padV,
        paddingBottom: padV,
        paddingRight: isMobile ? 16 : 12,
        // Отступ слева одинаковый в любом состоянии: акцентная полоса активного чата
        // лежит absolute поверх карточки и всего 4px шириной, а текст начинается с
        // 12/16px — наехать она не может. Прежняя прибавка «под полосу» только толкала
        // название и превью вправо в момент выделения.
        // leadingInset — отступ под контрол ветки в дереве чатов
        paddingLeft: (isMobile ? 16 : 12) + leadingInset,
        borderRadius: isMobile ? 16 : R.xl,
        marginBottom: 5,
        cursor: 'pointer',
        overflow: 'hidden',
        background: cardBg,
        border: '1px solid ' + (isActive ? accent : C.borderLight),
        // box-shadow задаётся классом cc-card-shadow; цвет и сила ауры — переменными
        // (statusVars), их читают и steady box-shadow, и слой-аура в обёртке
        '--cc-card-shadow': isActive ? SHADOW.button : SHADOW.card,
        ...statusVars,
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'center',
        gap: 3,
        // единая высота карточек в списке: короткий чат не выше длинного
        minHeight,
        boxSizing: 'border-box',
      } as CSSProperties}
    >
      {/* Собеседник — в правом углу; в группе лицо даёт ведущая.
          Рисуется до акцентной полосы, иначе накрыла бы её собой */}
      {backdropPersona && (
        <PersonaBackdrop
          persona={backdropPersona}
          width={COMPANION_W}
          // Цветная вуаль персоны мешает статусному переливу/пульсации — гасим её,
          // чтобы текстовая область оставалась чистым фоном. Фото/инициалы остаются
          neutral={hasGlow || unread}
        />
      )}

      {/* Тема чата — крупный полупрозрачный водяной знак на месте персоны, когда
          собеседника нет (правый угол свободен). У чата с персоной значка темы тут
          не нужно: идентификатор правого угла — лицо. Низ уходит за срез карточки и
          обрезается её overflow:hidden — метафора «значок вырастает из нижней панели» */}
      {!backdropPersona && (
        <ChatTopicBackdrop topic={s.topic} align="right" />
      )}

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
        {/* Строка 1: признак задачи, название, метки срока и закрепления. Состояние
            чата больше не точкой здесь — его несёт внешний glow-ореол карточки, а
            подпись всплывает тултипом по наведению на правую часть (hotspot ниже) */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
          {/* Тихий ключ-признак задачи: «Задача» уходит в иконку, весь текст — в тултип */}
          {taskChat && (
            <span title={taskChat.fullLabel} aria-label={taskChat.fullLabel} style={{ display: 'flex', flexShrink: 0, color: C.textMuted }}>
              <Wrench size={12} strokeWidth={2.2} />
            </span>
          )}
          {/* Тема чата — значок перед именем: быстрый ориентир в строке, дополняет
              водяной знак в правом углу. У чатов-задач тоже рисуется: имя там своё,
              но тема разговора остаётся полезной приметой */}
          <ChatTopicIcon topic={s.topic} size={14} />
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
          maxHeight={(1 + (canRename ? 1 : 0) + (onTogglePin ? 1 : 0) + (onAssignTags ? 1 : 0)
            + (onAddToWall ? 1 : 0) + (canEditChat ? (canMute ? 2 : 1) : 0)) * 34 + 10}
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
          {/* Настройки чата, раньше доступные только из шапки открытого чата: мьют
              уведомлений и срок хранения. Ни то, ни другое не двигает чат в списке —
              бэкенд намеренно не обновляет UpdatedAt на этих правках */}
          {canMute && (
            <MenuItem
              icon={notifyOn ? <Bell size={15} strokeWidth={2} /> : <BellOff size={15} strokeWidth={2} />}
              label={notifyOn ? 'Заглушить' : 'Уведомления'}
              onClick={e => { e.stopPropagation(); setMenu(null); void toggleNotify(); }}
            />
          )}
          {canEditChat && (
            <MenuItem
              icon={<Hourglass size={15} strokeWidth={2} />}
              label={s.expiresAfterMinutes ? `Хранить: ${expiryOptionLabel(s.expiresAfterMinutes)}` : 'Срок хранения'}
              // Пикер сроков открывается по тому же якорю, что и это меню: кнопка «⋮»
              // исчезнет вместе с ним, и её rect брать будет неоткуда (приём пункта «Теги»)
              onClick={e => { e.stopPropagation(); const anchor = menu; setMenu(null); setExpiryMenu(anchor); }}
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

      {expiryMenu && !editing && (
        <Menu anchor={expiryMenu} onClose={() => setExpiryMenu(null)}
          minWidth={isMobile ? 260 : 300} maxHeight={190} gap={4}>
          {/* Клики гасим на обёртке: иначе выбор срока открывал бы чат (onClick карточки) */}
          <div style={{ padding: '6px 8px 8px' }} onClick={e => e.stopPropagation()}>
            <ExpiryPicker value={s.expiresAfterMinutes} onChange={pickExpiry} columns={isMobile ? 2 : 3} />
            {expiryAt && (
              <p style={{ margin: '8px 0 0', fontSize: 11.5, color: C.textMuted, lineHeight: 1.4 }}>
                Удалится ~{formatExpiryDate(expiryAt)}, если не будет активности.
              </p>
            )}
          </div>
        </Menu>
      )}
    </div>
  );
}
