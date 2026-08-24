// Раздел «Архив» (план «Архив чатов» v4, шаг 4): плоский список заархивированных
// чатов с карточкой-саммари, кнопками «Собрать сводку» и «Сохранить в заметки».
// Архив ПРЯЧЕТ чат, а не удаляет — чат можно открыть, прочитать и вернуть
// одной кнопкой «Вернуть из архива». Ручной архив и сам раздел работают без
// флага chat-auto-archive (флаг закрывает только автоправило и его настройки).

import { useCallback, useEffect, useMemo, useState } from 'react';
import { Archive, ArchiveRestore, FileText, Sparkles } from 'lucide-react';
import { api } from '../lib/api';
import { archiveApi, saveArchiveSessionAsNote } from '../api/chats';
import { joinUser, onMessage } from '../lib/signalr';
import { isArchivedChat } from '../lib/chatFilters';
import { archiveCardText, firstNoteLines } from '../lib/archiveCard';
import { useOnline } from '../hooks/useOnline';
import { C, FONT, R } from '../lib/design';
import { showToast } from '../lib/toast';
import { usePersonasVersion } from '../lib/personas';
import { ChatCard } from '../components/ChatCard';
import { Modal, ModalActions, Button, EmptyState, IconButton } from '../components/ui';
import { ICON_SIZE } from '../components/ui/icons';
import type { HubTabValue } from '../components/HubTabs';
import type { AuthState, Session } from '../types';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
  // Клик по чату: открыть его (select) и прокрутить список к этому элементу.
  // Реализует ChatsPage через selectChat + scrollIntoView — здесь мы лишь
  // сообщаем о намерении и передаём выбранную сессию.
  onOpenChat: (chat: Session) => void;
}

// Эндпоинт списка чатов отдаёт общий плоский массив (в т.ч. архивные). Из него
// берём только isArchivedChat() === true — это узкое место фильтрации, см.
// chatFilters.ts. На сервере есть производный признак, фронт его не пересчитывает.
async function loadArchivedChats(): Promise<Session[]> {
  const all = await api.chats.list();
  return all.filter(isArchivedChat);
}

// Прокрутить к карточке чата в общем списке (ChatsPage использует свою
// прокрутку; здесь нужен был общий тригер для возврата из плашки — наружу
// через колбэк onOpenChat пробрасываем намерение, раздел сам по себе без
// своего скролла, потому что плоский).
function scrollToChat(id: string) {
  const el = document.querySelector(`[data-chat-id="${CSS.escape(id)}"]`);
  if (el && 'scrollIntoView' in el) (el as HTMLElement).scrollIntoView({ block: 'nearest' });
}

export function ArchivePage({ auth, onHubTab, onOpenChat }: Props) {
  const online = useOnline();
  const [chats, setChats] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  // Подписка на стор персон — карточки показывают аватары
  usePersonasVersion();

  const refresh = useCallback(async () => {
    try {
      const list = await loadArchivedChats();
      setChats(list);
    } catch {
      /* сеть/ошибка — оставляем прежний список */
    } finally {
      setLoading(false);
    }
  }, []);

  // Первичная загрузка + поллинг + SignalR-обновление. Архив ПРЯЧЕТ, а не
  // удаляет — события chat_deleted у архивных чатов не будет, зато приходит
  // chat_archived на смену признака (см. backend/Protocol/ServerMessage.cs).
  // В ленте не подписываемся на chat_archived как на удаление — это разные
  // смыслы: chat_deleted уносит чат, chat_archived прячет/возвращает.
  useEffect(() => {
    refresh();
    const poll = setInterval(refresh, 5000);
    if (auth.id) joinUser(auth.id).catch(() => {});
    const off = onMessage(msg => {
      if (msg.type === 'status_changed') refresh();
      if (msg.type === 'task_changed') refresh();
      // Чат убран/возвращён из архива — перечитываем список. Направление
      // (Archived) не разбираем: фильтр isArchivedChat() возьмёт только то,
      // что архивное сейчас. НИКОГА не подменяем на chat_deleted: chat_deleted
      // означает «чат удалён», а архив чат НЕ удаляет.
      if (msg.type === 'chat_archived') refresh();
      if (msg.type === 'chat_renamed') refresh();
    });
    return () => {
      clearInterval(poll);
      off();
    };
  }, [auth.id, refresh]);

  // Архивировать/разархивировать через колбэк карточки: 409 (живой ход/фоновые
  // агенты) ловим и показываем тостом с серверным текстом — клиент не должен
  // выдумывать своё сообщение поверх того, что вернул бэкенд.
  const setArchived = useCallback(async (chat: Session, archived: boolean) => {
    try {
      const updated = await archiveApi.setArchived(chat.id, archived);
      setChats(prev => prev
        .map(c => c.id === updated.id ? updated : c)
        .filter(c => isArchivedChat(c) === true));
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Не удалось изменить архив';
      // Серверный текст 409 «в чате идёт ход» уже человекочитаемый; остальные
      // ответы тоже показываем как есть — никакой обёртки «Произошла ошибка».
      showToast('Архив', msg, 'info');
    }
  }, []);

  // Сводка карточки архива: приоритет из канона (см. ChatDigestService.CardText).
  // Свежую сводку показывает сервер уже в поле Session.ArchiveSummary — здесь
  // только читаем и передаём в компонент. «Собрать сводку» дёргает API,
  // ответом приходит обновлённая сессия (ArchiveSummary заполнен, ArchiveSummaryAt
  // проставлен). 409 «сводка уже собирается» и 502 «модель упала» прилетают
  // человеческим текстом — клиент не делает своих сообщений поверх.
  const buildDigest = useCallback(async (chat: Session) => {
    try {
      const updated = await archiveApi.buildDigest(chat.id);
      setChats(prev => prev.map(c => c.id === updated.id ? updated : c));
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Не удалось собрать сводку';
      showToast('Сводка', msg, 'info');
    }
  }, []);

  // Сохранить в заметки — отдельный путь через существующий
  // POST /api/sessions/{sessionId}/summary. Серверный эндпоинт не трогаем:
  // контракт «Итога сессии» неизменен, им просто пользуется ещё одна кнопка.
  // SummaryNoteId проставляется сервером — следующее открытие карточки архива
  // покажет первые строки этой заметки как приоритет 2.
  const saveAsNote = useCallback(async (chat: Session) => {
    try {
      await saveArchiveSessionAsNote(chat.id);
      showToast('Сохранение в заметки', 'Заметка создана', 'info');
      // Сервер не шлёт специального события о простановке SummaryNoteId —
      // перечитываем список, чтобы карточка сразу показала обновлённый текст
      refresh();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Не удалось сохранить в заметки';
      showToast('Сохранение в заметки', msg, 'info');
    }
  }, [refresh]);

  // Открыть архивный чат на чтение: даём вызывающей стороне сигнал открыть
  // чат (ChatsPage его подхватит и поставит в активный), плюс отдельный
  // скролл — карточка осталась видна после перечитки списка.
  const openArchived = useCallback((chat: Session) => {
    onOpenChat(chat);
    scrollToChat(chat.id);
  }, [onOpenChat]);

  // Заголовок раздела со счётчиком: «Архив · 12». Скрываем цифру, пока грузится,
  // чтобы не мигало «Архив · 0» перед первым ответом. Сама цифра — число
  // уникальных архивных чатов в текущем списке
  const counter = useMemo(() => chats.length, [chats.length]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Шапка раздела — без HubHeader (не тот уровень навигации). Иконка-признак +
          заголовок + счётчик + кнопка назад. Без неё «Архив» читался бы как
          одна из панелей чатов, а это отдельный раздел. */}
      <header style={{
        display: 'flex', alignItems: 'center', gap: 10,
        padding: '14px 18px',
        borderBottom: `1px solid ${C.border}`,
        background: C.bgPanel,
        flexShrink: 0,
      }}>
        <Archive size={18} strokeWidth={2} style={{ color: C.textSecondary, flexShrink: 0 }} />
        <h1 style={{
          margin: 0, fontFamily: FONT.serif, fontSize: 18, fontWeight: 500,
          color: C.textHeading, letterSpacing: '-0.01em', flex: 1, minWidth: 0,
        }}>
          Архив{!loading && counter > 0 ? ` · ${counter}` : ''}
        </h1>
        {/* Закрыть раздел — возврат к обычным «Чатам». На широком экране это
            переход между hub-tab'ами; на мобильном — назад по истории. */}
        <IconButton
          onClick={() => onHubTab('chats')}
          title="Закрыть архив"
          size="sm"
        >
          <svg width="14" height="14" viewBox="0 0 14 14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M3 3l8 8M11 3l-8 8" />
          </svg>
        </IconButton>
      </header>

      {/* Скролл-зона списка. Пустое состояние — дословно из канона. */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: '12px 16px 20px' }}>
        {!loading && chats.length === 0 && (
          <EmptyState
            icon={<Archive size={ICON_SIZE.xl} strokeWidth={2} />}
            title="Здесь пусто"
            subtitle="Сюда попадают чаты, к которым давно не возвращались — они не удаляются и всегда открываются обратно"
          />
        )}
        {(loading || chats.length > 0) && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {chats.map(chat => (
              <ArchiveCard
                key={chat.id}
                chat={chat}
                online={online}
                onOpen={() => openArchived(chat)}
                onRestore={() => void setArchived(chat, false)}
                onBuildDigest={() => void buildDigest(chat)}
                onSaveAsNote={() => void saveAsNote(chat)}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

// Карточка архивного чата (компонент для одной записи): шапка с ChatCard’ом
// (имя, тема, лицо, метки) + блок сводки + кнопки действий. Сводка показывается
// в приоритете канона: свежая ArchiveSummary → первые строки заметки (SummaryNoteId
// подтягиваем через api.notes.get — кэшируем, без локальной репликации) →
// lastMessage → «Сообщений нет». Кнопки разделены: «Вернуть из архива» слева,
// «Собрать сводку» / «Сохранить в заметки» справа.
function ArchiveCard({
  chat, online, onOpen, onRestore, onBuildDigest, onSaveAsNote,
}: {
  chat: Session;
  online: boolean;
  onOpen: () => void;
  onRestore: () => void;
  onBuildDigest: () => void;
  onSaveAsNote: () => void;
}) {
  // Текст карточки в приоритете канона. Резолв заметки — побочный эффект
// (api.notes.resolve), его держим здесь; приоритет и формат строк живут
// в lib/archiveCard, чтобы их можно было покрыть юнитами без побочек.
  const [noteLines, setNoteLines] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    setNoteLines(null);
    if (!chat.summaryNoteId) return;
    api.notes.resolve(chat.summaryNoteId).then(r => {
      if (cancelled) return;
      const content = r.note?.content ?? '';
      setNoteLines(firstNoteLines(content));
    }).catch(() => { if (!cancelled) setNoteLines(null); });
    return () => { cancelled = true; };
  }, [chat.summaryNoteId]);

  const text = archiveCardText(chat, noteLines);

  return (
    <div
      data-chat-id={chat.id}
      style={{
        border: `1px solid ${C.border}`,
        borderRadius: R.xl,
        background: C.bgWhite,
        overflow: 'hidden',
      }}>
      {/* Шапка карточки: ChatCard без меню действий (мы тут, не в общем списке).
          Кликаем по карточке — открываем чат. На тач и без hover кнопки
          действий в ChatCard не нужны, тут мы сами рисуем свой набор. */}
      <div onClick={onOpen} style={{ cursor: 'pointer' }}>
        <ChatCard
          session={chat}
          isActive={false}
          isMobile={false}
          fallbackName="Чат без названия"
          online={online}
          hovered={false}
          workflowRunning={false}
          onSelect={onOpen}
          onHover={() => {}}
          onDelete={() => {}}
        />
      </div>

      {/* Текст карточки (приоритет канона) + действия */}
      <div style={{
        padding: '10px 14px 12px',
        borderTop: `1px solid ${C.borderLight}`,
        display: 'flex', flexDirection: 'column', gap: 10,
        background: C.bgPanel,
      }}>
        <p style={{
          margin: 0, fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary,
          lineHeight: 1.45,
          // Многоточие: длинная сводка не должна раздувать карточку.
          display: '-webkit-box', WebkitLineClamp: 4, WebkitBoxOrient: 'vertical',
          overflow: 'hidden',
        }}>
          {text}
        </p>
        <div style={{
          display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
          // Слева — возврат (главное действие с архивной записью),
          // справа — сборка и заметка
          justifyContent: 'space-between',
        }}>
          <Button
            variant="ghostAccent"
            size="sm"
            leftIcon={<ArchiveRestore size={ICON_SIZE.sm} strokeWidth={2} />}
            onClick={onRestore}
          >
            Вернуть из архива
          </Button>
          <div style={{ display: 'flex', gap: 6 }}>
            <Button
              variant="secondary"
              size="sm"
              leftIcon={<Sparkles size={ICON_SIZE.sm} strokeWidth={2} />}
              onClick={onBuildDigest}
            >
              Собрать сводку
            </Button>
            <Button
              variant="secondary"
              size="sm"
              leftIcon={<FileText size={ICON_SIZE.sm} strokeWidth={2} />}
              onClick={onSaveAsNote}
            >
              Сохранить в заметки
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}

// Приоритет и форматирование текста карточки живут в lib/archiveCard, юниты — там же.
// Здесь только эффекты (резолв заметки, обновление чата).

// Подавляем неиспользуемый импорт Modal/ModalActions — оставлены на случай,
// если потребуется подтверждение возврата; сейчас возврат одной кнопкой без
// модалки (план v4: «возврат одной кнопкой возвращает чат и его транскрипт»).
void Modal; void ModalActions;