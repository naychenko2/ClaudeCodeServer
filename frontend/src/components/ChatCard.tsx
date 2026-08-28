import { useState, useRef, useEffect, type CSSProperties, type ReactNode } from 'react';
import { AlertCircle, Archive, ArchiveRestore, Bell, BellOff, Bot, CheckCircle2, Clock, Columns3, GitCommitVertical, History, Hourglass, Eye, EyeOff, MoreVertical, Pencil, Pin, Tags, Terminal, Trash2, Users, Wrench } from 'lucide-react';
import type { Session } from '../types';
import { C, R, SHADOW, FONT } from '../lib/design';
import { ChatTopicBackdrop, ChatTopicIcon, IconButton, Menu, MenuItem } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { STATUS_CONFIG, STATUS_GLOW, type VisualStatus } from './StatusIndicator';
import { useAgentsRunning, useBgCommandRunning } from '../lib/agentsPresence';
import { useActionVisibility } from '../hooks/useActionVisibility';
import { CHAT_ACTION_ORDER, CARD_ACTIONS_HIDDEN_BY_DEFAULT, type ChatActionKey } from '../lib/chatActions';
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

// Геометрия строки списка. Скругление мелкое (R.md) — крупный радиус имеет смысл
// у обведённой плитки, а под заливкой наведения он размывает левую кромку столбца.
// Зазор — минимальный: воздух между чатами даёт их собственный padding, а щель
// нужна лишь чтобы заливки соседних строк не слипались в сплошную полосу.
// Одно значение на оба слоя свайп-бутерброда — см. комментарий у обёртки.
// Экспортируются: дерево чатов (ChatTreeRow) рисует по этой же геометрии рамку
// цели перетаскивания и считает от зазора вертикальный центр строки — со своей
// копией чисел контур двоился бы
export const ROW_R = R.md;
export const ROW_GAP = 2;

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
  // Переопределение «в чате работают фоновые агенты». Не задано — берём из стора
  // agentsPresence; проп нужен витрине UI-кита, где стора нет
  agentsRunning?: boolean;
  // То же для фоновой команды (Bash в фоне) — тихий значок терминала
  bgCommandRunning?: boolean;
  // Незафиксированные в git правки (dirtySessionIds из lib/git.ts) — тихий значок в
  // строке названия. 'own' — правил сам чат; 'descendants' — сам не правил, но правки
  // есть у вложенных чатов (признак наследуется вверх по ветке, чтобы свёрнутая или
  // длинная ветка не прятала работу). Оба сразу — 'own', свои правки важнее.
  // Не задан — значка нет: признак неизвестен или не применим (чаты вне проектов,
  // витрина UI-кита, недоступный git-статус)
  uncommitted?: 'own' | 'descendants';
  onSelect: () => void;
  onHover: (hovered: boolean) => void;
  onDelete: () => void;
  // Не задан — чат без закрепления (списки проекта)
  onTogglePin?: () => void;
  // Общие теги чата (имя + цвет из реестра) — строка чипов под названием
  tags?: { name: string; color?: string }[];
  // Снять тег с чата (hover-крестик на чипе; на тач удаление — через меню маркировки)
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
  // === Свайп-действия (мобильная раскладка) ===
  // Эта карточка раскрыта свайпом (список держит ОДНУ раскрытую — открытие другой
  // закрывает предыдущую). Не задан/undefined — свайп-механика выключена
  swipeOpen?: boolean;
  // Смена раскрытия: true — карточку раскрыли жестом, false — закрыли (тап мимо
  // кнопок, открытие другой карточки, скролл списка)
  onSwipeToggle?: (open: boolean) => void;
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
  agentsRunning: agentsRunningProp, bgCommandRunning: bgCommandRunningProp, uncommitted,
  onSelect, onHover, onDelete, onTogglePin, tags, onAssignTags, onRename, onAddToWall,
  onEdited, leadingInset = 0, swipeOpen, onSwipeToggle,
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

  // Быстрые действия карточки объявлены ниже — им нужны canRename/canMute/canEditChat

  // Long-press на мобиле открывает меню действий карточки — аналог кнопки «⋮»,
  // который на тач-экране виден только у активного чата. Долгий тап работает на
  // любой карточке, не выбирая её: флаг longPressFired гасит клик, чтобы menu
  // не закрывалось открытием чата
  const lpTimer = useRef<number | null>(null);
  const lpFired = useRef(false);
  const lpStart = useRef<{ x: number; y: number } | null>(null);

  // === Свайп-действия (мобильная раскладка) ===
  // Свайп влево тянет карточку вслед за пальцем, под ней открываются три кнопки
  // (закрепить/теги/удалить). Ось фиксируется на первых SWIPE_AXIS px: горизонталь
  // забирает свайп (long-press гасится немедленно), вертикаль отдаётся скроллу
  // списка — preventDefault по вертикали не зовём никогда.
  // Текущий сдвиг: 0 = закрыто, отрицательное = влево. Стейт живёт только на время
  // жеста; «раскрытое» состояние держит родитель (swipeOpen), мы лишь анимируем к нему
  const SWIPE_AXIS = 10;        // px от старта, на которых решается ось жеста
  // Ширина зоны кнопок = число видимых быстрых действий × 44px (тач-цель); она же
  // вылет раскрытия. Считается по факту: пользователь сам решает, сколько их
  const SWIPE_BTN_W = 44;
  // editing объявлен ниже; его состояние важно только в момент жеста, поэтому в
  // гейт здесь не включаем (beginLongPress уже проверяет editing на месте)
  const swipeCanWork = isMobile && online && !!onSwipeToggle;
  const [swipeDx, setSwipeDx] = useState(0);
  const swipeActive = useRef(false);   // ось зафиксирована как горизонталь
  const swipeStartX = useRef(0);
  // Жест работает в обе стороны: из закрытого состояния тянем влево (раскрыть),
  // из раскрытого — вправо (закрыть). Тап по карточке тоже закрывает (см. onClick),
  // но обратный свайп — то, что палец делает сам собой, и без него раскрытие
  // выглядит залипшим.
  // База — трансформ на старте жеста: 0 у закрытой, -swipeOpenW у раскрытой
  const swipeBase = useRef(0);
  const swipeMoved = useRef(false);    // был ли горизонтальный сдвиг — глушит клик

  const beginLongPress = (e: React.TouchEvent) => {
    if (!isMobile || !online || editing) return;   // editing здесь уже объявлен ниже по коду, но вызов идёт по событию — безопасно
    const t = e.touches[0];
    lpStart.current = { x: t.clientX, y: t.clientY };
    lpFired.current = false;
    swipeActive.current = false;
    swipeMoved.current = false;
    swipeStartX.current = t.clientX;
    if (swipeCanWork) swipeBase.current = swipeOpen ? -swipeOpenW : 0;
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
    const dx = t.clientX - lpStart.current.x;
    const dy = t.clientY - lpStart.current.y;
    // Ось ещё не решена: гасим long-press при любом движении >10px (как раньше),
    // а для свайпа дополнительно требуем преобладание горизонтали
    if (!swipeActive.current) {
      if (Math.abs(dx) > SWIPE_AXIS || Math.abs(dy) > SWIPE_AXIS) {
        killLongPress();
        // Вправо жест берём только у раскрытой карточки: у закрытой тянуть некуда,
        // и перехват отобрал бы у страницы её собственные горизонтальные жесты.
        // Сравниваем модули: с сырым dy диагональ вверх-влево (dy < 0) проходила
        // проверку всегда и жест перехватывался у вертикальной прокрутки
        if (swipeCanWork && Math.abs(dx) > Math.abs(dy) && (dx < 0 || swipeOpen)) {
          // Горизонтальный свайп: перехват жеста у скролла
          swipeActive.current = true;
          swipeMoved.current = true;
        }
      }
      return;
    }
    // Горизонталь зафиксирована: карточка следует за пальцем в пределах вылета —
    // от -swipeOpenW (раскрыто) до 0 (закрыто), считая от базы
    const next = Math.max(-swipeOpenW, Math.min(0, swipeBase.current + dx));
    setSwipeDx(next);
  };
  const onSwipeEnd = () => {
    killLongPress();
    if (swipeActive.current) {
      // Дальше половины вылета — считаем открытым, ближе — закрытым. Порог один на
      // оба направления, поэтому обратный свайп закрывает ровно там же, где прямой
      // открывает: протянул больше половины назад — закрылось, меньше — вернулось
      const opened = swipeDx <= -swipeOpenW / 2;
      setSwipeDx(0);
      // Итог сообщаем всегда, а не только при открытии: иначе закрывающий жест
      // отпускал бы карточку обратно в раскрытое состояние (swipeOpen не менялся)
      onSwipeToggle?.(opened);
      swipeActive.current = false;
    }
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
  // Чат в архиве — действие и подпись у кнопки зеркалятся («В архив» ⇄ «Вернуть из архива»)
  const archived = !!s.archivedAt;
  const expiryAt = expiresAt(s);
  // Правка названия прямо в карточке: пункт меню превращает заголовок в поле ввода —
  // ради одного имени открывать форму настроек чата не надо. У чата-исполнителя задачи
  // переименования нет: там в заголовке стоит имя ЗАДАЧИ (taskChat.title), и правка
  // s.name не изменила бы ни строчки на экране
  const canRename = !!onRename && !taskChat;

  // Быстрые действия карточки (hover-кластер на desktop, кнопки свайпа на мобиле).
  // Набор — общий каталог действий чата (тот же, что в шапке открытого чата); какие
  // из них стоят кнопками рядом с «⋮», решает пользователь глазиком в меню.
  // Скрыто всё — кластера нет вовсе, но каждое действие доступно из самого меню
  const cardVis = useActionVisibility('chat-card', CARD_ACTIONS_HIDDEN_BY_DEFAULT);
  // Доступность действия в ЭТОМ списке: нет колбэка (или контекста) — действия нет
  // ни кнопкой, ни строкой меню. Порядок — канонический из каталога
  const cardActionAvailable: Record<ChatActionKey, boolean> = {
    rename: canRename,
    pin: !!onTogglePin,
    tags: !!onAssignTags,
    wall: !!onAddToWall,
    notify: canMute,
    dossier: canEditChat && !!s.projectId,
    expiry: canEditChat,
    archive: canEditChat,
    delete: true,
  };
  const cardActions = CHAT_ACTION_ORDER.filter(k => cardActionAvailable[k]);
  // Вылет свайпа = сколько кнопок реально показано (кнопки по 44px — тач-цель)
  // Потолок — три кнопки, ОБЩИЙ для свайпа и hover-кластера: при восьми зона
  // заняла бы 352px, а нижний ориентир
  // экрана 360 CSS (Flip 8) — карточка уезжала бы целиком, оставляя под пальцем
  // ряд одинаковых иконок без имени и превью. Остальное достаётся из меню
  const MAX_QUICK_BTNS = 3;
  // Кандидаты ряда — ВСЕ видимые действия, включая «В архив»: глазик управляет им на
  // общих основаниях. Исключение archive здесь (была такая правка) ломало инвариант
  // «глазик показывает "видна" — кнопка есть»: действие исчезало из ряда у обычного
  // чата при включённом глазике, хотя хранилище говорило обратное
  const quickActions = cardActions.filter(k => cardVis.isVisible(k));
  // Видимых сверх потолка — добираем по каноническому порядку и молча уводим в
  // меню. Так честно и с сохранённой настройкой на 5+ кнопок (снял один — на
  // её место встает следующий по порядку, а не «дырка» в середине)
  const overLimitKeys = quickActions.slice(MAX_QUICK_BTNS).join(',');
  const shownActions = quickActions.slice(0, MAX_QUICK_BTNS);
  // Возврат из архива — единственное действие вне настройки видимости: у архивного чата
  // оно стоит кнопкой ВСЕГДА, на своём каноническом месте — прямо перед «Удалить».
  // Иначе человек, спрятавший «В архив» в меню (а оно спрятано по умолчанию), в архивном
  // списке искал бы выход из архива по одному чату через «⋮».
  // Добавляется СВЕРХ отрезанного потолка (кандидаты без неё + она сама), а не вместо
  // одной из трёх: вытеснять ради неё «Удалить» (соседа по смыслу) нельзя, а архивный
  // список — редкий режим, четвёртая кнопка там ряд не ломает. Дубля нет: если archive
  // уже в shownActions по глазику, фильтр отдаст её один раз
  const quickButtons = archived && cardActionAvailable.archive
    ? CHAT_ACTION_ORDER.filter(k => k === 'archive' || shownActions.includes(k))
    : shownActions;
  useEffect(() => { if (overLimitKeys) cardVis.hide(overLimitKeys.split(',')); }, [overLimitKeys]);
  const swipeOpenW = quickButtons.length * SWIPE_BTN_W;
  // Глазик-спутник строки меню: показывает, стоит ли действие быстрой кнопкой
  // (hover-кластер на десктопе, свайп на мобиле), и переключает это по клику.
  // Меню при этом не закрывается — набор выставляется одним заходом. Пока ряд
  // полон (3 кнопки), включить четвёртую нельзя — глазик гаснет с подсказкой:
  // место освобождают, убрав соседнюю кнопку
  const visAction = (key: ChatActionKey) => {
    // Возврат из архива спрятать нельзя — глазика у строки нет вовсе (иначе он
    // предлагал бы действие, которое ничего не изменит)
    if (key === 'archive' && archived) return undefined;
    const shown = cardVis.isVisible(key);
    const blocked = !shown && shownActions.length >= MAX_QUICK_BTNS;
    return {
      icon: shown
        ? <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        : <EyeOff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
      title: shown
        ? 'Убрать в меню'
        : blocked
          ? 'Ряд полон — сначала уберите другую кнопку'
          : 'Показывать кнопкой в ряду',
      disabled: blocked,
      onClick: () => cardVis.toggle(key),
    };
  };
  // Описание быстрой кнопки по ключу — одна таблица на hover-кластер и свайп-зону,
  // чтобы жест и наведение всегда делали ровно одно и то же. Клики гасят всплытие:
  // иначе любое действие заодно открывало бы чат (onClick карточки)
  const quickButton = (key: ChatActionKey): {
    icon: ReactNode; title: string; danger?: boolean; active?: boolean;
    onClick: (e: React.MouseEvent<Element>) => void;
  } => {
    switch (key) {
      case 'rename': return {
        icon: <Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, title: 'Переименовать',
        onClick: e => { e.stopPropagation(); startRename(); },
      };
      case 'pin': return {
        icon: <Pin size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} fill={s.isPinned ? 'currentColor' : 'none'} />,
        title: s.isPinned ? 'Открепить' : 'Закрепить',
        onClick: e => { e.stopPropagation(); onTogglePin?.(); },
      };
      case 'tags': return {
        icon: <Tags size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, title: 'Теги',
        onClick: e => { e.stopPropagation(); onAssignTags?.((e.currentTarget as HTMLElement).getBoundingClientRect()); },
      };
      case 'wall': return {
        icon: <Columns3 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, title: 'На стену',
        onClick: e => { e.stopPropagation(); onAddToWall?.(); },
      };
      case 'notify': return {
        icon: notifyOn ? <Bell size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : <BellOff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
        title: notifyOn ? 'Заглушить' : 'Включить уведомления',
        onClick: e => { e.stopPropagation(); void toggleNotify(); },
      };
      case 'dossier': return {
        icon: <History size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
        title: s.excludeFromDossiers ? 'Решения не сохраняются' : 'Решения сохраняются',
        onClick: e => { e.stopPropagation(); void toggleDossier(); },
      };
      case 'expiry': return {
        icon: <Hourglass size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
        title: s.expiresAfterMinutes ? `Хранить: ${expiryOptionLabel(s.expiresAfterMinutes)}` : 'Срок хранения',
        active: s.expiresAfterMinutes != null,
        onClick: e => { e.stopPropagation(); setExpiryMenu((e.currentTarget as HTMLElement).getBoundingClientRect()); },
      };
      case 'archive': return {
        icon: archived
          ? <ArchiveRestore size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          : <Archive size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
        title: archived ? 'Вернуть из архива' : 'В архив',
        onClick: e => { e.stopPropagation(); void toggleArchive(); },
      };
      case 'delete': return {
        icon: <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, title: 'Удалить', danger: true,
        onClick: e => { e.stopPropagation(); onDelete(); },
      };
    }
  };

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
  // Opt-out «не сохранять решения из этого чата» (ADR-004 §6) — тот же тумблер, что
  // в шапке открытого чата. Только у проектных чатов: у чата вне проекта досье нет
  const toggleDossier = async () => {
    try {
      onEdited?.(await updateChatFields(s, { excludeFromDossiers: !s.excludeFromDossiers }));
    } catch {
      showToast('История решений', 'Не удалось изменить настройку чата', 'info');
    }
  };

  // Убрать чат в архив / вернуть обратно. Ответ бэкенда отдаём владельцу списка: карточка
  // сама уйдёт из основного вида (архивные там отфильтрованы) либо вернётся в него
  const toggleArchive = async () => {
    try {
      onEdited?.(await updateChatFields(s, { archived: !archived }));
      showToast('Архив', archived ? 'Чат вернулся в список' : 'Чат убран в архив', 'info');
    } catch {
      showToast('Архив', archived ? 'Не удалось вернуть чат' : 'Не удалось убрать чат в архив', 'info');
    }
  };

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

  // Строка общих тегов (макет chat-tags-switch): чипы идут ТРЕТЬЕЙ строкой — под
  // превью или статусом задачи, а не сразу под названием, чтобы метки не разрывали
  // связку «имя чата → о чём он». Снять тег отсюда нельзя — только через меню
  // маркировки: крестик ловил клики по плитке
  const tagsRow = tags && tags.length > 0 ? (
    <div style={{ display: 'flex', alignItems: 'center', gap: 4, flexWrap: 'wrap', minWidth: 0 }}>
      {tags.map(t => (
        <TagChip key={t.name} name={t.name} color={t.color} />
      ))}
    </div>
  ) : null;

  // Статус несёт перелив ФОНА карточки (.cc-tint): по фону слева направо идёт
  // еле заметная волна статусного цвета. Ауры вокруг и цветного бордюра нет.
  // У живых (breath) волна движется, у error — ровная подкраска. Цвет и силу
  // подмешивания отдаём в CSS переменных --cc-status-c / --cc-tint-a
  //
  // Когда практика «Командная реализация» стоит и ждёт человека (tone='wait':
  // стадии interview/confirming/awaitingDecision), CLI-сессия может быть и
  // working, и finished — но для человека это один смысл: «ждут меня». Оставлять
  // оранжевую дышащую волну working на фоне жёлтого маркера «решение» — два
  // взаимоисключающих сообщения об одном чате (большая волна перебивает маркер).
  // Переключаем визуал статуса на 'waiting' (медовый, slow) — он усиливает жёлтый
  // маркер, а не спорит с ним. Сам s.status не трогаем: это факт CLI, а не визуал
  const teamWait = !!s.teamImplement && teamImplementTone(s.teamImplement.stage) === 'wait';
  // Фоновая работа доживает уже после конца хода: статус сессии при этом Active, у него
  // нулевое свечение — карточка выглядела остывшей, хотя работа идёт. Приоритет ниже
  // teamWait (там ждут ЧЕЛОВЕКА — это важнее) и выше собственного статуса сессии:
  // перебиваем только спокойные состояния, живой working подменять незачем
  const agentsRunningLive = useAgentsRunning(s.id);
  const agentsRunning = agentsRunningProp ?? agentsRunningLive;
  // Фоновая команда (дев-сервер, watch) светится наравне с агентами: чат с живым процессом
  // не должен выглядеть остывшим, а какая именно работа идёт — говорит значок в строке имени.
  // Своё значение visualStatus, а не 'agents': подпись «агенты работают» тут была бы враньём
  const bgCommandRunningLive = useBgCommandRunning(s.id);
  const bgCommandRunning = bgCommandRunningProp ?? bgCommandRunningLive;
  const visualStatus: VisualStatus = teamWait ? 'waiting'
    : STATUS_GLOW[s.status].breath ? s.status
      : agentsRunning ? 'agents'
        : bgCommandRunning ? 'command'
          : s.status;
  const glow = STATUS_GLOW[visualStatus];
  const hasGlow = glow.alpha > 0;
  const glowClass = !hasGlow ? ''
    : glow.breath
      ? ' cc-tint cc-tint--flow' + (glow.slow ? ' cc-tint--slow' : '')
      : ' cc-tint cc-tint--static';

  // Непрочитанный чат мягко мерцает фоном нейтральным серым — но только если
  // статусного перелива нет: приоритет у него, два движения на карточке дрались
  // бы. Слой у обоих один и тот же (::after), так что классы взаимоисключающие.
  // Хук, а не голая функция: по подписке метка гаснет сразу при открытии чата
  const unread = useHasUnread(s.updatedAt, s.id, s.lastReadAt);
  const unreadClass = !hasGlow && unread ? ' cc-unread' : '';
  const statusVars = {
    '--cc-status-c': STATUS_CONFIG[visualStatus].color,
    // Сила подмешивания в фон: alpha из STATUS_GLOW (45..72) задумана под
    // свечение — для заливки её ужимаем втрое и добавляем 10 п.п. Даёт 25..34%:
    // ниже ~10% подкраска на кремовом фоне уже неразличима
    '--cc-tint-a': `${Math.round(glow.alpha / 3) + 10}%`,
  } as CSSProperties;

  // Чат — СТРОКА списка, а не отдельная плитка: рамки и тени у неё нет, состояние
  // несёт заливка. В покое строка залита фоном списка (bgWhite) — на нём она и
  // стоит, поэтому ступеньки тона не видно, а между соседями не остаётся линий.
  // Под курсором — bgSelected, у выбранного — accentMuted.
  //
  // Именно accentMuted, а не accentLight: последний (#F4ECE1) СВЕТЛЕЕ серого
  // bgSelected (#E8E1D4), и выбранный чат оказывался бледнее и наведённого, и
  // непрочитанного — тонул в списке вместо того, чтобы выделяться. Тем же
  // accentMuted показан открытый раздел в ListDateDivider, так что выделение
  // текущего элемента по продукту одно
  //
  // Фон именно НЕПРОЗРАЧНЫЙ, а не transparent (выглядело бы так же): карточка
  // накрывает собой кнопки свайпа, которые на мобиле висят под ней всегда, и
  // подложку кластера быстрых кнопок, лежащую поверх хвоста названия. С прозрачным
  // фоном и то и другое просвечивает.
  //
  // Если по карточке идёт статусный перелив, состояние фоном не показываем: волна
  // статусного цвета поверх оранжевой заливки читается грязно, цвет статуса
  // перестаёт быть собой. Что чат выбран, видно по акцентной полосе слева
  const cardBg = hasGlow ? C.bgWhite
    : isActive ? C.accentMuted
      : hovered ? C.bgSelected
        : C.bgWhite;
  // Лицо для подложки: у группы — ведущая (первая в составе)
  const backdropPersona = group.length > 1 ? group[0] : persona;
  const padV = isMobile ? 14 : 11;
  const minHeight = Math.max(padV * 2 + TWO_LINES, ACTION_BOX + 8);

  return (
    <div
      onClick={() => {
        // Свайп-жест прошёл (даже без открытия) — клик не открывает чат
        if (swipeMoved.current) { swipeMoved.current = false; return; }
        if (lpFired.current) { lpFired.current = false; return; }
        // Раскрытая свайпом карточка: тап закрывает раскрытие, а не открывает чат
        if (swipeOpen) { onSwipeToggle?.(false); return; }
        onSelect();
      }}
      onMouseEnter={() => onHover(true)}
      onMouseLeave={() => onHover(false)}
      // Правый клик — меню действий прямо у курсора (desktop): тот же состав, что у
      // «⋮» и long-press, без прицеливания в мелкую кнопку. Якорь — точка курсора
      // (zero-size rect), ui/Menu в anchor-режиме раскроется под ней
      onContextMenu={online && !editing ? e => {
        e.preventDefault();
        e.stopPropagation();
        setMenu(new DOMRect(e.clientX, e.clientY, 0, 0));
      } : undefined}
      onTouchStart={beginLongPress}
      onTouchMove={onTpMove}
      onTouchEnd={onSwipeEnd}
      onTouchCancel={onSwipeEnd}
      // Обёртка свайп-бутерброда: держит отступ списка, срезает уголки кнопок под
      // карточкой её же скруглением. Вся визуальная карточка (фон/glow) — на
      // внутреннем слое, который и уезжает влево при свайпе.
      // Радиус ОБЯЗАН совпадать с радиусом внутреннего слоя: этой обёрткой
      // обрезается и слой статусного перелива (.cc-tint::after)
      style={{
        position: 'relative',
        marginBottom: ROW_GAP,
        borderRadius: ROW_R,
        overflow: 'hidden',
        cursor: 'pointer',
      }}
    >
      {/* Кнопки свайпа (мобила): под карточкой у правого края, видны, когда карточка
          уехала. Высота — вся карточка, ширина — вылет раскрытия.
          Слой висит постоянно, а прячет его непрозрачный фон карточки — поэтому фон
          строки в покое обязан оставаться непрозрачным (см. cardBg): с прозрачным
          кнопки просвечивают сквозь чат, и список на мобиле выглядит так, будто
          свайп раскрыт на каждой строке */}
      {swipeCanWork && quickButtons.length > 0 && (
        <div style={{
          position: 'absolute', top: 0, bottom: 0, right: 0, width: swipeOpenW,
          display: 'flex', zIndex: 0,
        }}>
          {/* Осознанное отклонение от ui/IconButton: здесь нужны ячейки во всю высоту
              карточки, а примитив — квадрат фиксированного размера, и растягивать его
              пропом ради одного места хуже, чем собрать кнопку тут. Что у примитива
              берём обязательно: имя (aria-label — на таче title не показывается вовсе)
              и видимое кольцо фокуса (класс cc-iconbtn) */}
          {quickButtons.map((k, i) => {
            const b = quickButton(k);
            return (
              <button
                key={k}
                type="button"
                className="cc-iconbtn"
                // Раскрытие закрываем ПЕРЕД действием: пикер тегов/срока встаёт по rect
                // кнопки, а сама кнопка уедет вместе с закрытием — rect берём заранее
                onClick={e => { onSwipeToggle?.(false); b.onClick(e); }}
                title={b.title}
                aria-label={b.title}
                style={{
                  flex: 1, border: 'none', cursor: 'pointer', display: 'grid', placeItems: 'center',
                  borderLeft: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
                  // Подложка — bgInset, а не bgWhite: у карточки тот же тон, и слой
                  // «под» ней не читался бы ни в светлой теме, ни в тёмной
                  background: b.danger ? C.dangerBg : C.bgInset,
                  color: b.danger ? C.danger : C.textSecondary,
                }}
              >
                {b.icon}
              </button>
            );
          })}
        </div>
      )}

      {/* Внутренний слой — сама карточка: уезжает влево при свайпе (раскрытие
          держит swipeOpen родителя; во время жеста — swipeDx) */}
      <div
        // cc-card-press — прожатие под нажатием (index.css). В режиме правки названия
        // его нет: там внутри поле ввода, и вдавливать карточку при постановке
        // курсора незачем
        className={'cc-card-shadow' + (editing ? '' : ' cc-card-press') + glowClass + unreadClass}
        style={{
          position: 'relative', zIndex: 1,
          // Пока палец ведёт (swipeActive) — за ним, иначе по состоянию раскрытия.
          // Порядок именно такой: с приоритетом у swipeOpen раскрытая карточка стояла
          // бы прибитой к -swipeOpenW и обратный жест не двигал бы её вовсе
          transform: swipeActive.current ? `translateX(${swipeDx}px)`
            : swipeOpen ? `translateX(${-swipeOpenW}px)`
              : swipeDx ? `translateX(${swipeDx}px)` : undefined,
          // Пружинка на отпускании/раскрытии; во время слежения за пальцем — без неё
          transition: swipeActive.current ? 'none' : 'transform 0.18s ease',
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
          borderRadius: ROW_R,
          cursor: 'pointer',
          background: cardBg,
          // Рамки у строки нет — ни в покое, ни у выбранной: обведённые чаты
          // выстраивались в частокол линий, а разделяет их теперь заливка и воздух.
          // Прозрачная рамка остаётся ради габарита: с ней высота строки не зависит
          // от состояния (внутри есть поле правки названия со своей рамкой)
          border: '1px solid transparent',
          // Тени в покое тоже нет — она рисовала вторую обводку поверх рамки.
          // Класс cc-card-shadow остаётся: на нём же висит прожатие (cc-card-press),
          // подменяющее тень на внутреннюю в момент нажатия
          '--cc-card-shadow': 'none',
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
          // чтобы текстовая область оставалась чистым фоном. Фото/инициалы остаются.
          // У выбранного чата — по той же причине: вуаль ложится поверх акцентной
          // заливки, и в списке, где у половины чатов свои персоны, текущий переставал
          // отличаться от соседей
          neutral={hasGlow || unread || isActive}
        />
      )}

      {/* Тема чата — крупный полупрозрачный водяной знак на месте персоны, когда
          собеседника нет (правый угол свободен). У чата с персоной значка темы тут
          не нужно: идентификатор правого угла — лицо. Низ уходит за срез карточки и
          обрезается её overflow:hidden — метафора «значок вырастает из нижней панели» */}
      {!backdropPersona && (
        <ChatTopicBackdrop topic={s.topic} align="right" />
      )}

      {/* Акцентная полоса слева — главный маркер текущего чата (у чатов персоны — её
          цветом). Во всю высоту и в 4px: заливка сама по себе теряется на пёстром
          фоне (у чатов с персоной справа лежит цветная вуаль её цвета), а сплошная
          вертикаль ловится боковым зрением независимо от того, что под ней */}
      {isActive && (
        <div style={{
          position: 'absolute', left: 0, top: 0, bottom: 0, width: 4,
          background: accent,
        }} />
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
              autoComplete="off"
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
            // Название текущего чата — цветом акцента: третий признак выбора рядом с
            // полосой и заливкой. Текст лежит поверх любых подложек, поэтому работает
            // и там, где заливку глушит вуаль персоны или водяной знак темы
            <span title={displayName} style={{
              fontSize: 13.5, fontWeight: isActive ? 700 : 600,
              color: isActive ? accent : C.textHeading,
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
          {/* Правки чата не зафиксированы в git. Значок-СОСТОЯНИЕ, поэтому глиф взят из
              git-семейства и намеренно не FileDiff — тот в продукте занят кнопкой
              «показать дифф» (GitChangesRail), и одинаковый глиф читался бы как действие.
              Формулировка тултипа про ПРАВКИ ЧАТА, а не про состояние репозитория:
              множество берётся из атрибуции файлов чату, а она врёт в известных случаях
              (коммит при погашенном сервере, чужая правка «его» файла) — обещать
              «в репозитории есть незакоммиченное» значок не вправе. На мобиле тултип по
              тапу не всплывает — значок остаётся без пояснения осознанно, прятать его
              там хуже, чем показать молча.
              Унаследованный от потомков значок тем же глифом и цветом: два оттенка серого
              на иконке 13px не различить, поэтому разводим их ТЕКСТОМ подсказки, а не
              видом — иначе родитель молча врал бы, что правил файлы сам */}
          {uncommitted && (
            <span
              title={uncommitted === 'own'
                ? 'Правки этого чата не зафиксированы в git'
                : 'Не зафиксированы правки во вложенных чатах'}
              aria-label={uncommitted === 'own'
                ? 'Правки этого чата не зафиксированы в git'
                : 'Не зафиксированы правки во вложенных чатах'}
              style={{ display: 'flex', flexShrink: 0, color: C.textMuted }}>
              <GitCommitVertical size={13} strokeWidth={2.2} />
            </span>
          )}
          {/* Работают фоновые агенты. Это единственное, чем такой чат отличим от чата
              с идущим ходом: волна у них одна и та же (работа и там, и там реальная).
              При чипе WF значок не дублируем — workflow и есть фоновая задача */}
          {agentsRunning && !workflowRunning && (
            <span title="Работают агенты" aria-label="Работают агенты"
              style={{ display: 'flex', flexShrink: 0, color: C.accent }}>
              <Bot size={13} strokeWidth={2.2} />
            </span>
          )}
          {/* Фоновая команда (дев-сервер, watch): волна по плитке у неё та же, что у агентов
              (чат с живым процессом не выглядит остывшим), а вид работы называет этот значок.
              Цвет акцентный — под цвет волны: серый значок на акцентной подсветке читался бы
              как рассинхрон. При работающих агентах не показываем — свечение уже объясняет,
              почему чат жив, а два значка подряд сливаются в шум */}
          {bgCommandRunning && !agentsRunning && !workflowRunning && (
            <span title="В фоне выполняется команда" aria-label="В фоне выполняется команда"
              style={{ display: 'flex', flexShrink: 0, color: C.accent }}>
              <Terminal size={13} strokeWidth={2.2} />
            </span>
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

        {/* Чат-задача: одна строка статуса выполнения вместо превью-промпта и
            плашки-дубля. Обычный чат — превью + плашка происхождения как раньше */}
        {taskChat ? (
          <>
            <TaskStatusLine info={taskChat} />
            {tagsRow}
          </>
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

            {tagsRow}

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

      {/* Действия по наведению (desktop): кластер быстрых кнопок ПОВЕРХ контента
          у правого края — места в layout не занимают, до «⋮» можно не тянуться.
          Удаление — тот же onDelete, что в меню (с подтверждением списка).
          Ghost-класса здесь НЕТ намеренно: кластер и так появляется только по
          наведению (showActions), и приглушать уже проявленные кнопки — значит
          показывать их выключенными */}
      {showActions && !isMobile && quickButtons.length > 0 && (
        <div style={{
          position: 'absolute', top: '50%', transform: 'translateY(-50%)',
          right: ACTIONS_RIGHT, zIndex: 2, display: 'flex', alignItems: 'center',
          background: cardBg, borderRadius: R.lg, boxShadow: SHADOW.card,
        }}>
          {quickButtons.map(k => {
            const b = quickButton(k);
            return (
              <IconButton
                key={k}
                onClick={b.onClick}
                title={b.title}
                size="xs"
                tone={b.danger ? 'danger' : undefined}
                active={b.active}
              >
                {b.icon}
              </IconButton>
            );
          })}
        </div>
      )}
      {/* Кнопка «⋮» — постоянное место действий чата (полный состав: переименовать,
          на стену, мьют, срок + тумблеры «что показывать в кластере»). На мобиле
          рисуется как раньше (у активного чата), на desktop — при наведении.
          Отступ вправо считаем по ФАКТУ видимых кнопок кластера */}
      {showActions && (
        <div style={{
          position: 'absolute', top: '50%', transform: 'translateY(-50%)',
          right: isMobile ? ACTIONS_RIGHT : ACTIONS_RIGHT + quickButtons.length * ACTION_BOX,
          zIndex: 1, display: 'flex',
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
            <MoreVertical size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </div>
      )}
      {/* --- конец внутреннего слоя (уезжающего при свайпе); меню ниже живут в
          обёртке, чтобы не смещаться вместе с карточкой --- */}
      </div>

      {menu && !editing && (
        <Menu anchor={menu} onClose={() => setMenu(null)} minWidth={158}
          // Высота меню решает, куда его раскрыть (вверх/вниз) — считаем по составу
          // Высота меню решает, куда его раскрыть; в счёт входят и строки-тумблеры
          // видимости быстрых кнопок (cardActions) с разделителем перед ними
          // Меню = ровно доступные действия каталога, по строке на каждое
          maxHeight={cardActions.length * 34 + 20}
          gap={4}>
          {canRename && (
            <MenuItem
              icon={<Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label="Переименовать"
              isMobile={isMobile}
            action={visAction('rename')}
              onClick={e => { e.stopPropagation(); setMenu(null); startRename(); }}
            />
          )}
          {onTogglePin && (
            <MenuItem
              icon={<Pin size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} fill={s.isPinned ? 'currentColor' : 'none'} />}
              label={s.isPinned ? 'Открепить' : 'Закрепить'}
              isMobile={isMobile}
            action={visAction('pin')}
              onClick={e => { e.stopPropagation(); setMenu(null); onTogglePin(); }}
            />
          )}
          {onAssignTags && (
            <MenuItem
              icon={<Tags size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label="Теги"
              isMobile={isMobile}
            action={visAction('tags')}
              // Меню маркировки открывается по тому же якорю: кнопка «⋮» уже
              // исчезнет вместе с этим меню, и её rect брать будет неоткуда
              onClick={e => { e.stopPropagation(); const anchor = menu; setMenu(null); onAssignTags(anchor); }}
            />
          )}
          {onAddToWall && (
            <MenuItem
              icon={<Columns3 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label="На стену"
              isMobile={isMobile}
            action={visAction('wall')}
              onClick={e => { e.stopPropagation(); setMenu(null); onAddToWall(); }}
            />
          )}
          {/* Настройки чата, раньше доступные только из шапки открытого чата: мьют
              уведомлений и срок хранения. Ни то, ни другое не двигает чат в списке —
              бэкенд намеренно не обновляет UpdatedAt на этих правках */}
          {canMute && (
            <MenuItem
              icon={notifyOn ? <Bell size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : <BellOff size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label={notifyOn ? 'Заглушить' : 'Уведомления'}
              isMobile={isMobile}
            action={visAction('notify')}
              onClick={e => { e.stopPropagation(); setMenu(null); void toggleNotify(); }}
            />
          )}
          {/* Досье решений (ADR-004 §6) — тот же тумблер, что в шапке открытого чата;
              только у проектных чатов: у чата вне проекта досье не ведётся */}
          {canEditChat && !!s.projectId && (
            <MenuItem
              icon={<History size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label={s.excludeFromDossiers ? 'Досье: не сохраняются' : 'Досье: сохраняются'}
              isMobile={isMobile}
            action={visAction('dossier')}
              onClick={e => { e.stopPropagation(); setMenu(null); void toggleDossier(); }}
            />
          )}
          {canEditChat && (
            <MenuItem
              icon={<Hourglass size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label={s.expiresAfterMinutes ? `Хранить: ${expiryOptionLabel(s.expiresAfterMinutes)}` : 'Срок хранения'}
              isMobile={isMobile}
            action={visAction('expiry')}
              // Пикер сроков открывается по тому же якорю, что и это меню: кнопка «⋮»
              // исчезнет вместе с ним, и её rect брать будет неоткуда (приём пункта «Теги»)
              onClick={e => { e.stopPropagation(); const anchor = menu; setMenu(null); setExpiryMenu(anchor); }}
            />
          )}
          {canEditChat && (
            <MenuItem
              icon={archived
                ? <ArchiveRestore size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                : <Archive size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label={archived ? 'Вернуть из архива' : 'В архив'}
              isMobile={isMobile}
              action={visAction('archive')}
              onClick={e => { e.stopPropagation(); setMenu(null); void toggleArchive(); }}
            />
          )}
          <MenuItem
            icon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Удалить"
            danger
            isMobile={isMobile}
            action={visAction('delete')}
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
