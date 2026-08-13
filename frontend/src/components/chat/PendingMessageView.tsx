// === Сообщения, ждущие конца текущего хода ===
// Агент прислал сообщение (chats_send) в занятый чат: сервер принял его в очередь и
// доставит после хода. Показываем строкой-конвертом в конце ленты: лицо, имя, начало
// текста; клик раскрывает целиком, крестик отменяет доставку. Компактность важнее
// полноты: очередь бывает длинной, а стопка полноразмерных карточек читается как сбой.
//
// Пунктир C.dashed + утопленный фон — принятый в проекте язык «ещё не вещь» (кнопки
// «создать», зона дропа). Глухой прозрачности нет: приглушение несут рамка и фон,
// а opacity поверх C.textMuted роняла бы контраст подписей.
import { useEffect, useRef, useState } from 'react';
import { ChevronDown, CloudOff, Inbox, User } from 'lucide-react';
import { C, FONT, R, SP, FS } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { IconButton } from '../ui/IconButton';
import { Button } from '../ui/Button';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { MarkdownContent } from './MarkdownContent';
import { MessageOriginChip } from '../MessageOriginChip';
import { getPersonaById, ensurePersonasLoaded, personaLabel } from '../../lib/personas';
import type { PendingChatMessage } from '../../lib/chatReducer';

// Длительность ухода строки после доставки/отмены — столько же держим её в DOM
const LEAVE_MS = 150;
// Высота раскрытого тела до кнопки «Показать целиком». Ограничиваем по пикселям, а не
// по строкам: в тексте бывают блоки кода и списки, и line-clamp резал бы их посередине
const BODY_MAX_H = 220;

// Превью в свёрнутой строке — plain: разметка в одну строку читается как мусор
// («## Итог», «**готово**», ```` ```ts ````). Снимаем самый частый синтаксис, текст оставляем.
function previewOf(text: string): string {
  return text
    .replace(/```[\s\S]*?```/g, '⟨код⟩')      // блоки кода — одним словом
    .replace(/`([^`]+)`/g, '$1')               // инлайн-код
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1') // ссылки и картинки — только текст
    .replace(/^\s{0,3}#{1,6}\s+/gm, '')        // заголовки
    .replace(/^\s{0,3}>\s?/gm, '')             // цитаты
    .replace(/^\s*[-*+]\s+/gm, '')             // маркеры списка
    .replace(/(\*\*|__|\*|_|~~)/g, '')         // выделения
    .replace(/\s+/g, ' ')
    .trim();
}

// Компактное «ждёт N» — не «N минут назад»: речь о длительности ожидания, а не о
// моменте в прошлом, и место в строке ограничено
function waitedFor(iso: string): string {
  const sec = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (sec < 60) return `ждёт ${Math.floor(sec)} сек`;
  if (sec < 3600) return `ждёт ${Math.floor(sec / 60)} мин`;
  return `ждёт ${Math.floor(sec / 3600)} ч`;
}

interface RowProps {
  item: PendingChatMessage;
  // Отмена доставки; undefined — нет связи, вместо крестика показываем причину
  onCancel?: (id: string) => void;
  // Прервать идущий ход и доставить это сейчас; undefined — нет связи или ход уже кончился
  onPreempt?: () => void;
  isMobile?: boolean;
  // Строка уходит (доставлена или отменена) — гасим её перед снятием с DOM
  leaving?: boolean;
}

function PendingMessageRow({ item, onCancel, onPreempt, isMobile, leaving }: RowProps) {
  // Лицо отправителя: в не-персон-чате стор мог быть не загружен
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const [open, setOpen] = useState(false);
  const [full, setFull] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  // Пересчитываем «ждёт N» раз в 20с, пока строка висит
  const [, tick] = useState(0);
  useEffect(() => {
    const t = setInterval(() => tick(n => n + 1), 20_000);
    return () => clearInterval(t);
  }, []);

  const sender = item.senderPersonaId ? getPersonaById(item.senderPersonaId) : null;
  // kind=user — своё сообщение, ждущее в «честной очереди»: подпись «Вы», без персоны и
  // чипа-источника (это не чужое входящее). Агентские строки (chats_send) — прежняя логика.
  const isUser = item.kind === 'user';
  // Персона → её имя; иначе имя чата-отправителя; иначе нейтральная подпись
  const title = isUser ? 'Вы' : (sender ? personaLabel(sender) : (item.senderChatName || 'Входящее сообщение'));
  const preview = previewOf(item.text);

  const cancel = () => {
    if (!onCancel || cancelling) return;
    setCancelling(true);
    onCancel(item.id);
  };

  return (
    <div style={{
      border: `1px dashed ${C.dashed}`, borderRadius: R.lg, background: C.bgInset,
      // Уход и отмена — одинаково приглушают строку, чтобы подмена не «моргала»
      opacity: leaving ? 0 : cancelling ? 0.55 : 1,
      transform: leaving ? 'translateY(-2px)' : 'none',
      transition: `opacity ${LEAVE_MS}ms ease, transform ${LEAVE_MS}ms ease`,
    }}>
      <div
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm,
          padding: `${SP.xs}px ${SP.xs}px ${SP.xs}px ${SP.sm}px`,
          cursor: 'pointer', minHeight: isMobile ? 44 : undefined,
        }}
      >
        {isUser
          ? <User size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
              style={{ color: C.textMuted, flexShrink: 0 }} />
          : sender
            ? <PersonaAvatar persona={sender} size={isMobile ? 24 : 20} />
            : <Inbox size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                style={{ color: C.textMuted, flexShrink: 0 }} />}

        <span style={{
          fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, flex: '0 1 auto',
          maxWidth: '38%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {title}
        </span>

        {!isUser && item.senderOrigin && <MessageOriginChip origin={item.senderOrigin} style={{ flex: '0 0 auto' }} />}

        {/* Превью в одну строку — при раскрытии уступает место полному тексту */}
        {!open && (
          <span style={{
            flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textMuted,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {preview}
          </span>
        )}
        {open && <span style={{ flex: 1 }} />}

        {!isMobile && (
          <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}>
            {cancelling ? 'отменяем…' : waitedFor(item.enqueuedAt)}
          </span>
        )}

        <ChevronDown
          size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden
          style={{
            color: C.textMuted, flexShrink: 0,
            transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s',
          }}
        />

        {onCancel ? (
          <span onClick={e => e.stopPropagation()} style={{ display: 'inline-flex', flexShrink: 0 }}>
            <IconButton
              onClick={cancel}
              disabled={cancelling}
              title="Не доставлять это сообщение"
              size={isMobile ? 'lg' : 'xs'}
              tone="danger"
            >
              <svg width={ICON_SIZE.xs} height={ICON_SIZE.xs} viewBox="0 0 24 24" fill="none"
                stroke="currentColor" strokeWidth={ICON_STROKE} strokeLinecap="round">
                <path d="M18 6 6 18" /><path d="m6 6 12 12" />
              </svg>
            </IconButton>
          </span>
        ) : (
          // Пропавший крестик читался бы как баг — показываем причину
          <CloudOff
            size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            aria-label="Отмена недоступна: нет связи"
            style={{ color: C.textMuted, flexShrink: 0, marginRight: SP.xs }}
          />
        )}
      </div>

      {/* Тело — тот же Markdown, что и у доставленного сообщения: агенты шлют сюда код,
          списки и заголовки, а сырым текстом это читалось как мусор из звёздочек. Заодно
          при доставке карточка не «прыгает» — разметка уже отрисована так же. */}
      {open && (
        <div
          onClick={e => e.stopPropagation()}
          style={{
            padding: `0 ${SP.md}px ${SP.sm}px ${isMobile ? SP.md : 38}px`,
            fontSize: FS.base, color: C.textSecondary, wordBreak: 'break-word',
            ...(full ? {} : { maxHeight: BODY_MAX_H, overflow: 'hidden' }),
          }}
        >
          <MarkdownContent text={item.text} />
        </div>
      )}
      {open && isUser && (
        // Свой ход в очереди: объясняем, почему карточка стоит, а не ушла в работу, и даём
        // явный перебой. Отправка сама ход не прерывает (иначе сделанная им работа и токены
        // выбрасываются) — «не жди, начинай сейчас» это отдельное осознанное действие.
        <div style={{
          padding: `0 ${SP.md}px ${SP.sm}px ${isMobile ? SP.md : 38}px`,
          display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
          fontSize: FS.xs, color: C.textMuted,
        }}>
          <span>Уйдёт в работу, когда Claude закончит текущий шаг</span>
          {onPreempt && (
            <Button
              variant="ghost"
              size="xs"
              onClick={e => { e.stopPropagation(); onPreempt(); }}
              title="Оборвать текущий ход, не дожидаясь его конца, и отправить это сообщение сейчас"
            >
              Прервать и отправить
            </Button>
          )}
        </div>
      )}
      {open && !full && item.text.length > 220 && (
        <div style={{ padding: `0 ${SP.md}px ${SP.sm}px ${isMobile ? SP.md : 38}px` }}>
          <button
            onClick={e => { e.stopPropagation(); setFull(true); }}
            style={{
              border: 'none', background: 'none', padding: 0, cursor: 'pointer',
              fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary,
            }}
          >
            Показать целиком
          </button>
        </div>
      )}
    </div>
  );
}

interface Props {
  items: PendingChatMessage[];
  onCancel?: (id: string) => void;
  // Прервать ход ради очереди; undefined — нет связи или ход уже не идёт
  onPreempt?: () => void;
  isMobile?: boolean;
}

// Держит ушедшие строки лишние 150мс, чтобы доставка/отмена не выглядела мгновенной
// подменой. Сервер шлёт очередь полным снимком, поэтому «ушедшие» вычисляются здесь.
export function PendingMessageList({ items, onCancel, onPreempt, isMobile }: Props) {
  const [leavingIds, setLeavingIds] = useState<string[]>([]);
  const [shown, setShown] = useState(items);
  const prevRef = useRef(items);

  useEffect(() => {
    const prev = prevRef.current;
    prevRef.current = items;
    const gone = prev.filter(p => !items.some(i => i.id === p.id));
    if (gone.length === 0) { setShown(items); return; }

    // Показываем ушедшие ещё один кадр — с ними отработает transition
    setShown([...items, ...gone]);
    setLeavingIds(gone.map(g => g.id));
    const t = setTimeout(() => {
      setShown(items);
      setLeavingIds([]);
    }, LEAVE_MS);
    return () => clearTimeout(t);
  }, [items]);

  if (shown.length === 0) return null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, marginTop: SP.sm }}>
      {shown.map((p, i) => (
        <PendingMessageRow
          key={p.id}
          item={p}
          isMobile={isMobile}
          leaving={leavingIds.includes(p.id)}
          onCancel={onCancel}
          // Перебой доставляет ГОЛОВУ очереди (DrainNextPendingAsync), а не ту строку, что
          // раскрыл пользователь — поэтому кнопка только у первой. Иначе «отправить это
          // сейчас» на второй реплике отправляло бы первую.
          onPreempt={i === 0 ? onPreempt : undefined}
        />
      ))}
    </div>
  );
}
