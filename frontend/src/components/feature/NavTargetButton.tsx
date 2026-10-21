import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ListChecks, MessageCircle } from 'lucide-react';
import { C, R, FS, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Цель навигации «чат / задача» (волна 3, макет features-overview-3-final §9а,
// «Навигация в один клик»): ОДНА пара кнопок на всю страницу фичи — где назван чат
// или задача, цели стоят рядом, у каждой своя иконка (облачко / список), подпись и
// подсказка «Открыть чат «…»». Наведение/фокус — как у кнопок кита (accentLight /
// bgSelected, SHADOW.focus); на 360 тач-цель ≥ 40. Обе темы — через токены C.*/FS/R.
//
// Состояния (единая точка правды — этот компонент):
//   chat:  chatId == null  → «чата нет» (неактивный текст, НЕ кнопка);
//          chatArchived    → пунктирная рамка + «· архив» (переход возможен, чат
//                            открывается для чтения с полосой «Чат в архиве…»).
//   task:  taskId == null  → кнопки задачи нет;
//          taskClosed       → пунктирная рамка + «закрыта» (клик ведёт, пометка вторичная).
//
// Навигацию НЕ дублируем: вызывающий передаёт onOpen, переиспользуя openChatById
// (чат) / openTaskInSection (задача) — те же пути, что плашка FeatureBanner и
// карточка «Шаг N фичи».

export interface NavTargetButtonProps {
  // chat — облачко, task — список.
  kind: 'chat' | 'task';
  // Переход (вызывающий переиспользует openChatById / openTaskInSection).
  onOpen: () => void;
  // Видимая подпись (по умолчанию 'чат'/'задача').
  label?: string;
  // Подсказка («Открыть чат «…»», «Открыть задачу…»).
  title?: string;
  // Тач-цель ≥ 40 на 360.
  isMobile?: boolean;
  // === Состояния ===
  // chat: null → «чата нет» (неактивный текст, не кнопка); undefined/строка — есть.
  chatId?: string | null;
  // chat: чат в архиве → пунктирная рамка + «· архив».
  chatArchived?: boolean;
  // task: null → кнопки задачи нет; undefined/строка — есть.
  taskId?: string | null;
  // task: задача закрыта → пунктирная рамка + «закрыта» (клик ведёт).
  taskClosed?: boolean;
}

// Есть ли цель: null — «цели нет» (явно: «чата нет» / нет кнопки задачи);
// undefined — вызывающий не передал (в NavTargetPair отфильтровано до вызова;
// standalone-вызов с undefined — «не передали» → не рисовать, minor 7).
// На практике standalone-кнопки всегда через NavTargetPair, который рендерит
// NavTargetButton только когда id != null, поэтому undefined здесь недостижим.
function hasTarget(id: string | null | undefined): boolean {
  return id != null; // null → нет цели; undefined → «не передали» (не рисовать)
}

// Кнопка-цель. Возвращает null, когда цели нет (task); «чата нет» — для chat.
export function NavTargetButton({
  kind, onOpen, label, title, isMobile = false,
  chatId, chatArchived, taskId, taskClosed,
}: NavTargetButtonProps) {
  const [hover, setHover] = useState(false);
  const isChat = kind === 'chat';
  const Icon = isChat ? MessageCircle : ListChecks;

  // Состояние «нет цели»: chat → «чата нет» (текст, не кнопка); task → нет кнопки.
  const id = isChat ? chatId : taskId;
  if (!hasTarget(id)) {
    if (isChat) {
      return (
        <span
          data-testid="nav-target-no-chat"
          style={{
            fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans,
            whiteSpace: 'nowrap', alignSelf: 'center',
          }}
        >
          чата нет
        </span>
      );
    }
    return null;
  }

  // Пометка вторичного состояния: архивный чат → «· архив»; закрытая задача → «закрыта».
  // Пунктирная рамка (макет: «у цели пунктирная рамка»).
  const marked = isChat ? !!chatArchived : !!taskClosed;
  const mark = isChat ? '· архив' : 'закрыта';
  const labelText = label ?? (isChat ? 'чат' : 'задача');
  // aria-label: подпись + (пометка) — скринридер слышит состояние.
  const aria = title ?? (isChat ? `Открыть чат${marked ? ' (в архиве)' : ''}` : `Открыть задачу${marked ? ' (закрыта)' : ''}`);

  const style: CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0,
    height: isMobile ? 40 : 24, minHeight: isMobile ? 40 : undefined,
    padding: '0 7px',
    border: `1px ${marked ? 'dashed' : 'solid'} ${hover ? (isChat ? C.accentMuted : C.borderLight) : (marked ? C.dashed : C.borderLight)}`,
    background: isChat ? (hover ? C.accentLight : C.bgWhite) : (hover ? C.bgSelected : C.bgWhite),
    borderRadius: R.sm,
    fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
    color: isChat ? C.accent : (hover ? C.textHeading : C.textSecondary),
    cursor: 'pointer',
  };
  // На 360 в плотных строках — только иконка (макет §9а, minor 8): подпись в aria-label,
  // видимый текст не рисуем (иначе «чат»/«задача» не влезает в 40×40).
  const showText = !isMobile;
  return (
    <button
      type="button"
      data-testid={`nav-target-${kind}`}
      onClick={onOpen}
      title={title}
      aria-label={aria}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      onFocus={e => { e.currentTarget.style.boxShadow = SHADOW.focus; }}
      onBlur={e => { e.currentTarget.style.boxShadow = 'none'; }}
      style={style}
    >
      <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      {showText && labelText}
      {showText && marked && (
        <span style={{ color: C.textMuted, fontWeight: 500 }}>{mark}</span>
      )}
    </button>
  );
}

// === Пара «чат · задача» (макет §9а) ===
//
// Участник (исполнитель этапа, шаг, виток, событие, тревога) — пара: чат и задача
// рядом, у каждого своя цель. Слоты:
//   chat:  chatId != null → кнопка (архив → «· архив»); chatId == null → «чата нет»
//          (текст, не кнопка) — если showNoChat; иначе слот пуст.
//   task:  taskId != null → кнопка (закрыта → «закрыта»); taskId == null → нет кнопки.
// Навигацию (openChatById / openTaskInSection) и точку возврата задаёт вызывающий.
export interface NavTargetPairProps {
  // Чат участника; null — «чата нет» (или пустой слот, если showNoChat=false).
  chatId?: string | null;
  // Чат в архиве → пометка «· архив».
  chatArchived?: boolean;
  // Переход в чат (openChatById).
  onOpenChat?: () => void;
  // Подсказка чата («Открыть чат «…»»).
  chatTitle?: string;
  // Рисуем ли «чата нет» (текст), когда chatId == null; false — слот пуст.
  showNoChat?: boolean;
  // Задача участника; null — кнопки задачи нет.
  taskId?: string | null;
  // Задача закрыта → пометка «закрыта».
  taskClosed?: boolean;
  // Переход в задачу (openTaskInSection).
  onOpenTask?: () => void;
  // Подсказка задачи («Открыть задачу…»).
  taskTitle?: string;
  // Тач-цель ≥ 40 на 360.
  isMobile?: boolean;
}

export function NavTargetPair({
  chatId, chatArchived, onOpenChat, chatTitle, showNoChat = true,
  taskId, taskClosed, onOpenTask, taskTitle, isMobile = false,
}: NavTargetPairProps) {
  // Чат: есть (chatId != null) → кнопка; нет → «чата нет» (если showNoChat) или пусто.
  const hasChat = chatId != null;
  // Задача: есть (taskId != null) → кнопка; нет → нет кнопки.
  const hasTask = taskId != null;
  if (!hasChat && !hasTask && !showNoChat) return null;
  return (
    <span data-testid="nav-target-pair" style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
      {(hasChat || (showNoChat && !hasChat)) && (
        <NavTargetButton
          kind="chat"
          chatId={chatId}
          chatArchived={chatArchived}
          title={chatTitle}
          isMobile={isMobile}
          onOpen={onOpenChat ?? (() => {})}
        />
      )}
      {hasTask && (
        <NavTargetButton
          kind="task"
          taskId={taskId}
          taskClosed={taskClosed}
          title={taskTitle}
          isMobile={isMobile}
          onOpen={onOpenTask ?? (() => {})}
        />
      )}
    </span>
  );
}
