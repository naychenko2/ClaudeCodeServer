import { useCallback, useEffect, useState } from 'react';
import { Menu as MenuIcon, MessageCircle, Plus } from 'lucide-react';
import type { AuthState, Session, SkillInfo } from '../types';
import { api } from '../lib/api';
import { joinUser, onMessage } from '../lib/signalr';
import { navPush, navReplace, getNav, type NavSnapshot } from '../lib/nav';
import { showToast } from '../lib/toast';
import { C, FONT } from '../lib/design';
import { useSidebarDrag } from '../lib/sidebarWidth';
import { useIsMobile } from '../lib/breakpoints';
import { Button, IconButton, IslandScaffold } from '../components/ui';
import { ICON_SIZE } from '../components/ui/icons';
import type { HubTabValue } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { ChatList } from '../components/ChatList';
import { ChatPanel } from '../components/ChatPanel';
import { RightPanelStack } from './workspace/RightPanelStack';
import { chatPanelStack } from './workspace/panelStackState';
import { ensurePersonasLoaded } from '../lib/personas';
import { ensureTasksLoaded } from '../lib/tasks';

const OPEN_CHAT_KEY = 'cc_open_chat';

interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
}

export function ChatsPage({ auth, onLogout, onHubTab }: Props) {
  const [chats, setChats] = useState<Session[]>([]);
  const [activeId, setActiveId] = useState<string | null>(() => {
    const nav = getNav();
    return nav?.chatId ?? localStorage.getItem(OPEN_CHAT_KEY);
  });
  const [creating, setCreating] = useState(false);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const isMobile = useIsMobile();

  // Глобальные скиллы для кнопки «/» в композере. Чаты здесь вне проекта,
  // поэтому берём глобальные скиллы (~/.claude/skills), без агентов (они per-project).
  const [skills, setSkills] = useState<SkillInfo[]>([]);
  useEffect(() => {
    api.skills.listGlobal().then(setSkills).catch(() => {});
  }, []);

  // Стор персон — чтобы ChatList показал аватар/имя персоны у её чатов
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  // Стор задач — для резолва контекста «в рамках какой задачи» (ChatOriginBadge/артефакты)
  useEffect(() => { void ensureTasksLoaded(); }, []);

  // Вложения относятся к конкретному чату — сбрасываем при смене активного
  useEffect(() => { setAttachedFiles([]); }, [activeId]);

  // Режим сайдбара чатов: pinned (в потоке) | collapsed (свёрнут). Сворачивание —
  // кнопкой на сплиттере, разворот — гамбургером обратно в поток.
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed'>(() =>
    localStorage.getItem('cc_chats_sidebar_mode') === 'collapsed' ? 'collapsed' : 'pinned'
  );
  useEffect(() => {
    localStorage.setItem('cc_chats_sidebar_mode', sidebarMode);
  }, [sidebarMode]);

  // Ширина сайдбара — общая для всех разделов (единый хук: та же ширина и клампы,
  // что в «Проектах», «Заметках», воркспейсе).
  const { width: sidebarWidth, dragging: draggingSplitter, startDrag: handleSidebarSplitterMouseDown } = useSidebarDrag();

  // Артефакты сессии живут в правой рельсе (RightPanelStack в режиме sessionOnly):
  // открытие панелей и их ширина хранятся в собственном инстансе стора (cc_chat_panels_*),
  // поэтому отдельного состояния здесь больше нет.

  // Активный workflow текущего чата — для плашки «WF» на карточке в списке
  const [workflowRunningFor, setWorkflowRunningFor] = useState<string | null>(null);
  const handleWorkflowRunning = useCallback((active: boolean, sessionId: string) => {
    setWorkflowRunningFor(prev => (active ? sessionId : prev === sessionId ? null : prev));
  }, []);

  const refresh = () => api.chats.list().then(setChats).catch(() => {});

  // Первичная загрузка + поллинг + realtime-обновление статусов через user-группу
  useEffect(() => {
    refresh();
    const poll = setInterval(refresh, 5000);
    if (auth.id) joinUser(auth.id).catch(() => {});
    const off = onMessage(msg => {
      if (msg.type === 'status_changed') refresh();
      // Чат удалён на сервере (в т.ч. авто-удаление временного) — убираем из списка,
      // открытый чат закрываем. Side-эффекты в апдейтере идемпотентны.
      if (msg.type === 'chat_deleted') {
        setChats(prev => prev.filter(c => c.id !== msg.sessionId));
        setActiveId(prev => {
          if (prev !== msg.sessionId) return prev;
          localStorage.removeItem(OPEN_CHAT_KEY);
          if (getNav()?.chatId) navReplace({ screen: 'chats' });
          return null;
        });
      }
      // Авто-заголовок уточнён локальной моделью — обновляем имя в списке на лету
      if (msg.type === 'chat_renamed') {
        setChats(prev => prev.map(c => c.id === msg.sessionId ? { ...c, name: msg.name } : c));
      }
    });
    return () => {
      clearInterval(poll);
      off();
      // Группу user_{id} НЕ покидаем: она сессионная и общая (её держат
      // NotificationToasts и стор задач). Ранний LeaveUser выкидывал всё
      // соединение из группы → task_changed переставал доходить в проектном чате.
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auth.id]);

  // Back/forward браузера внутри вкладки «Чаты» — синхронизируем активный чат из истории
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen === 'chats') {
        setActiveId(s.chatId ?? null);
        if (s.chatId) localStorage.setItem(OPEN_CHAT_KEY, s.chatId);
        else localStorage.removeItem(OPEN_CHAT_KEY);
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  // Форк «Сменить персону» из открытого чата: App переключает сюда, а мы открываем
  // новый чат по id (список перечитываем, чтобы плашка нового чата появилась сразу).
  useEffect(() => {
    const open = (e: Event) => {
      const chatId = (e as CustomEvent<{ chatId?: string }>).detail?.chatId;
      if (!chatId) return;
      setActiveId(chatId);
      localStorage.setItem(OPEN_CHAT_KEY, chatId);
      navPush({ screen: 'chats', chatId });
      refresh();
    };
    window.addEventListener('cc-open-chat', open);
    return () => window.removeEventListener('cc-open-chat', open);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const selectChat = (chat: Session) => {
    setActiveId(chat.id);
    localStorage.setItem(OPEN_CHAT_KEY, chat.id);
    navPush({ screen: 'chats', chatId: chat.id });
  };

  // Возврат из открытого чата к списку (мобилка). Детерминированно, не полагаясь на history.back():
  // снимаем активный чат и откатываем запись истории с chatId к списку.
  const backToList = () => {
    setActiveId(null);
    localStorage.removeItem(OPEN_CHAT_KEY);
    if (getNav()?.chatId) navReplace({ screen: 'chats' });
  };

  const newChat = async () => {
    if (creating) return;
    setCreating(true);
    try {
      const chat = await api.chats.create();
      setChats(prev => [chat, ...prev]);
      selectChat(chat);
    } catch (e) {
      showToast('Чат', e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setCreating(false);
    }
  };

  const activeChat = chats.find(c => c.id === activeId) ?? null;

  // Чат отредактирован/закреплён — обновить в списке
  const handleChatEdited = (updated: Session) =>
    setChats(prev => prev.map(c => c.id === updated.id ? { ...c, ...updated } : c));

  // Чат удалён — убрать из списка; если был активным — вернуться к списку
  const handleChatDeleted = (id: string) => {
    setChats(prev => prev.filter(c => c.id !== id));
    if (activeId === id) backToList();
  };

  // Развернуть свёрнутый сайдбар в поток — проброс в шапку ChatPanel
  const openSidebar = sidebarMode !== 'pinned' ? () => setSidebarMode('pinned') : undefined;

  // Внутренность сайдбара-острова: список чатов (управление сворачиванием — на
  // сплиттере). Паддинг здесь, а не на обёртке: IslandScaffold отступы не добавляет.
  const sidebarInner = (
    <div style={{ flex: 1, minHeight: 0, padding: '8px 10px 14px', display: 'flex', flexDirection: 'column' }}>
      <div style={{ flex: 1, minHeight: 0 }}>
        <ChatList chats={chats} activeId={activeId} onSelect={selectChat} onNew={newChat} creating={creating} onEdited={handleChatEdited} onDeleted={handleChatDeleted} workflowRunningFor={workflowRunningFor ?? undefined} />
      </div>
    </div>
  );

  // === Мобильная раскладка: список ИЛИ полноэкранный чат (не две панели) ===
  if (isMobile) {
    return (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden', position: 'relative' }}>
        {activeChat ? (
          // Чат + сессионная рельса в ОДНОЙ строке: рельса — flex-сосед справа
          // (сам пейн колоночный, без row-обёртки рельса встала бы под чатом)
          <div style={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden', position: 'relative' }}>
            <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
              <ChatPanel
                key={activeChat.id}
                session={activeChat}
                isMobile
                onBack={backToList}
                skills={skills}
                attachedFiles={attachedFiles}
                onAttachedFilesChange={setAttachedFiles}
                onSessionUpdated={updated => setChats(prev => prev.map(c => c.id === updated.id ? updated : c))}
                onWorkflowRunning={handleWorkflowRunning}
              />
            </div>
            <RightPanelStack
              sessionOnly
              isMobile
              panelStack={chatPanelStack}
              session={activeChat}
            />
          </div>
        ) : (
          <>
            <HubHeader value="chats" onTab={onHubTab} auth={auth} onLogout={onLogout} />
            <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', padding: '12px 16px 14px' }}>
              <ChatList chats={chats} activeId={activeId} onSelect={selectChat} onNew={newChat} creating={creating} onEdited={handleChatEdited} onDeleted={handleChatDeleted} isMobile workflowRunningFor={workflowRunningFor ?? undefined} />
            </div>
          </>
        )}
      </div>
    );
  }

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="chats" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      {/* Тело: остров-сайдбар + центральный остров (+ остров артефактов) на холсте */}
      <div style={{ flex: 1, minHeight: 0 }}>
        <IslandScaffold
          sidebarOpen={sidebarMode === 'pinned'}
          sidebar={sidebarInner}
          sidebarWidth={sidebarWidth}
          sidebarDragging={draggingSplitter}
          onSidebarDrag={handleSidebarSplitterMouseDown}
          onSidebarCollapse={() => setSidebarMode('collapsed')}
          centerBare
          center={activeChat ? (
            <ChatPanel
              key={activeChat.id}
              session={activeChat}
              headerIsland
              skills={skills}
              attachedFiles={attachedFiles}
              onAttachedFilesChange={setAttachedFiles}
              onOpenSidebar={openSidebar}
              onSessionUpdated={updated => setChats(prev => prev.map(c => c.id === updated.id ? updated : c))}
              onWorkflowRunning={handleWorkflowRunning}
            />
          ) : (
            <>
              {sidebarMode === 'collapsed' && (
                <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', padding: '0 8px', height: 52, borderBottom: `1px solid ${C.divider}` }}>
                  <IconButton onClick={() => setSidebarMode('pinned')} title="Открыть панель" size="md" variant="soft">
                    <MenuIcon size={ICON_SIZE.sm} strokeWidth={2} />
                  </IconButton>
                </div>
              )}
              <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
                <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 400, gap: 10 }}>
                  {/* Иконка раздела */}
                  <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: 4 }}>
                    <MessageCircle size={ICON_SIZE.xl} strokeWidth={2} />
                  </div>
                  <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 22, color: C.textHeading, letterSpacing: '-0.01em' }}>
                    О чём поговорим?
                  </div>
                  <div style={{ fontSize: 13.5, color: C.textSecondary, lineHeight: 1.55, maxWidth: 360 }}>
                    Обсуждайте любые темы, ищите нужную информацию, генерируйте тексты и изображения — просто начните разговор.
                  </div>
                  <Button
                    variant="primary" size="md" glow loading={creating}
                    onClick={newChat} style={{ marginTop: 10 }}
                    leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}
                  >
                    Новый чат
                  </Button>
                  <div style={{ fontSize: 12, color: C.textMuted, marginTop: 2 }}>или выберите чат слева</div>
                </div>
              </div>
            </>
          )}
          // Сессионная рельса (План/Агенты/Персона) — постоянная, как в проектах:
          // сама приносит сплиттер ширины, панели-острова и полосу иконок
          right={activeChat ? (
            <RightPanelStack sessionOnly panelStack={chatPanelStack} session={activeChat} />
          ) : undefined}
        />
      </div>
    </div>
  );
}
