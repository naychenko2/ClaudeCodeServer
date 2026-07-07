import { useCallback, useEffect, useState } from 'react';
import type { MouseEvent as ReactMouseEvent } from 'react';
import type { AuthState, Session, SkillInfo } from '../types';
import { api } from '../lib/api';
import { joinUser, onMessage } from '../lib/signalr';
import { navPush, navReplace, getNav, type NavSnapshot } from '../lib/nav';
import { C, FONT } from '../lib/design';
import { useSidebarWidth } from '../lib/sidebarWidth';
import { Button, IconButton, Splitter } from '../components/ui';
import type { HubTab } from '../components/HubTabs';
import { HubHeader } from '../components/HubHeader';
import { ChatList } from '../components/ChatList';
import { ChatPanel } from '../components/ChatPanel';
import { ArtifactsPanel } from '../components/ArtifactsPanel';
import { useFeature, FLAGS } from '../lib/featureFlags';

const OPEN_CHAT_KEY = 'cc_open_chat';

function useWindowWidth() {
  const [width, setWidth] = useState(window.innerWidth);
  useEffect(() => {
    const handler = () => setWidth(window.innerWidth);
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  }, []);
  return width;
}


interface Props {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}

export function ChatsPage({ auth, onLogout, onHubTab }: Props) {
  const [chats, setChats] = useState<Session[]>([]);
  const [activeId, setActiveId] = useState<string | null>(() => {
    const nav = getNav();
    return nav?.chatId ?? localStorage.getItem(OPEN_CHAT_KEY);
  });
  const [creating, setCreating] = useState(false);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const isMobile = useWindowWidth() < 768;

  // Глобальные скиллы для кнопки «/» в композере. Чаты здесь вне проекта,
  // поэтому берём глобальные скиллы (~/.claude/skills), без агентов (они per-project).
  const [skills, setSkills] = useState<SkillInfo[]>([]);
  useEffect(() => {
    api.skills.listGlobal().then(setSkills).catch(() => {});
  }, []);

  // Вложения относятся к конкретному чату — сбрасываем при смене активного
  useEffect(() => { setAttachedFiles([]); }, [activeId]);

  // Режим сайдбара чатов: pinned (в потоке) | collapsed (свёрнут) | open (drawer поверх).
  // Персистируем только pinned/collapsed; open — временное состояние.
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed' | 'open'>(() =>
    localStorage.getItem('cc_chats_sidebar_mode') === 'collapsed' ? 'collapsed' : 'pinned'
  );
  useEffect(() => {
    if (sidebarMode !== 'open') localStorage.setItem('cc_chats_sidebar_mode', sidebarMode);
  }, [sidebarMode]);

  // Ширина сайдбара — общая для всех областей (перетаскиваемая, персистится)
  const [sidebarWidth, setSidebarWidth] = useSidebarWidth();
  const [draggingSplitter, setDraggingSplitter] = useState(false);

  const handleSidebarSplitterMouseDown = (e: ReactMouseEvent) => {
    e.preventDefault();
    setDraggingSplitter(true);
    const startX = e.clientX;
    const startW = sidebarWidth;
    const onMove = (ev: globalThis.MouseEvent) =>
      setSidebarWidth(Math.max(220, Math.min(480, startW + (ev.clientX - startX))));
    const onUp = () => {
      setDraggingSplitter(false);
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  };

  // Панель «Артефакты сессии» (за фич-флагом): открыта/закрыта + ширина, персист в localStorage.
  // Ключи отдельные от проектов (cc_chat_artifacts_*), чтобы состояния не пересекались.
  const artifactsEnabled = useFeature(FLAGS.sessionArtifacts);
  const [artifactsOpen, setArtifactsOpen] = useState(() => localStorage.getItem('cc_chat_artifacts_open') === '1');
  useEffect(() => { localStorage.setItem('cc_chat_artifacts_open', artifactsOpen ? '1' : '0'); }, [artifactsOpen]);
  const [artifactsWidth, setArtifactsWidth] = useState(() => {
    const v = localStorage.getItem('cc_chat_artifacts_width');
    return v ? Math.max(240, Math.min(480, Number(v))) : 300;
  });
  useEffect(() => { localStorage.setItem('cc_chat_artifacts_width', String(artifactsWidth)); }, [artifactsWidth]);
  const toggleArtifacts = useCallback(() => setArtifactsOpen(v => !v), []);
  const [draggingArtifacts, setDraggingArtifacts] = useState(false);

  const handleArtifactsSplitterMouseDown = (e: ReactMouseEvent) => {
    e.preventDefault();
    setDraggingArtifacts(true);
    const startX = e.clientX;
    const startW = artifactsWidth;
    // Панель справа: тянем влево (clientX уменьшается) → ширина растёт
    const onMove = (ev: globalThis.MouseEvent) =>
      setArtifactsWidth(Math.max(240, Math.min(480, startW - (ev.clientX - startX))));
    const onUp = () => {
      setDraggingArtifacts(false);
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  };

  const refresh = () => api.chats.list().then(setChats).catch(() => {});

  // Первичная загрузка + поллинг + realtime-обновление статусов через user-группу
  useEffect(() => {
    refresh();
    const poll = setInterval(refresh, 5000);
    if (auth.id) joinUser(auth.id).catch(() => {});
    const off = onMessage(msg => {
      if (msg.type === 'status_changed') refresh();
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

  const selectChat = (chat: Session) => {
    setActiveId(chat.id);
    localStorage.setItem(OPEN_CHAT_KEY, chat.id);
    navPush({ screen: 'chats', chatId: chat.id });
    // В режиме drawer после выбора — закрываем оверлей
    setSidebarMode(m => m === 'open' ? 'collapsed' : m);
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
      alert(e instanceof Error ? e.message : 'Не удалось создать чат');
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

  // Открыть drawer (когда сайдбар не закреплён) — проброс в шапку ChatPanel
  const openSidebar = sidebarMode !== 'pinned' ? () => setSidebarMode('open') : undefined;

  // Внутренность сайдбара: строка управления (закрепить/свернуть) + список чатов
  const sidebarInner = (
    <>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 8, minHeight: 28 }}>
        {/* Свернуть панель (◀) */}
        <IconButton onClick={() => setSidebarMode('collapsed')} title="Свернуть панель" size="sm" style={{ marginLeft: -2 }}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M15 6l-6 6 6 6" />
          </svg>
        </IconButton>
        <span style={{ flex: 1, minWidth: 0, fontSize: 14, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>Чаты</span>
        {/* В режиме open — закрепить (📌) */}
        {sidebarMode === 'open' && (
          <IconButton onClick={() => setSidebarMode('pinned')} title="Закрепить панель" size="sm">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
              <line x1="12" y1="17" x2="12" y2="22" /><path d="M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4h1v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24Z" />
            </svg>
          </IconButton>
        )}
      </div>
      <div style={{ flex: 1, minHeight: 0 }}>
        <ChatList chats={chats} activeId={activeId} onSelect={selectChat} onNew={newChat} creating={creating} onEdited={handleChatEdited} onDeleted={handleChatDeleted} />
      </div>
    </>
  );

  // === Мобильная раскладка: список ИЛИ полноэкранный чат (не две панели) ===
  if (isMobile) {
    return (
      <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden', position: 'relative' }}>
        {activeChat ? (
          <>
            <ChatPanel
              key={activeChat.id}
              session={activeChat}
              isMobile
              onBack={backToList}
              skills={skills}
              attachedFiles={attachedFiles}
              onAttachedFilesChange={setAttachedFiles}
              onSessionUpdated={updated => setChats(prev => prev.map(c => c.id === updated.id ? updated : c))}
              artifactsOpen={artifactsEnabled ? artifactsOpen : undefined}
              onToggleArtifacts={artifactsEnabled ? toggleArtifacts : undefined}
            />
            {artifactsEnabled && artifactsOpen && (
              <>
                <div onClick={() => setArtifactsOpen(false)}
                  style={{ position: 'absolute', inset: 0, zIndex: 900, background: C.overlay }} />
                <div style={{ position: 'absolute', top: 0, right: 0, bottom: 0, zIndex: 901, width: 'min(92vw, 380px)', boxShadow: '-4px 0 20px rgba(20,16,10,0.18)' }}>
                  <ArtifactsPanel sessionId={activeChat.id} isMobile onClose={() => setArtifactsOpen(false)} />
                </div>
              </>
            )}
          </>
        ) : (
          <>
            <HubHeader value="chats" onTab={onHubTab} auth={auth} onLogout={onLogout} />
            <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', padding: '12px 16px 14px' }}>
              <ChatList chats={chats} activeId={activeId} onSelect={selectChat} onNew={newChat} creating={creating} onEdited={handleChatEdited} onDeleted={handleChatDeleted} isMobile />
            </div>
          </>
        )}
      </div>
    );
  }

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="chats" onTab={onHubTab} auth={auth} onLogout={onLogout} />

      {/* Тело: сайдбар списка (pinned/collapsed/open) + центр */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', position: 'relative' }}>

        {/* === Pinned: сайдбар в потоке (перетаскиваемая ширина) === */}
        {sidebarMode === 'pinned' && (
          <>
            <div style={{ width: sidebarWidth, flexShrink: 0, background: C.bgPanel, padding: '12px 14px 14px', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
              {sidebarInner}
            </div>
            <Splitter active={draggingSplitter} onMouseDown={handleSidebarSplitterMouseDown} />
          </>
        )}

        {/* === Collapsed / Open: drawer поверх === */}
        {sidebarMode !== 'pinned' && (
          <div style={{
            position: 'absolute', top: 0, left: 0, bottom: 0, zIndex: 10, width: 288,
            background: C.bgPanel, borderRight: `1px solid ${C.border}`, padding: '12px 14px 14px',
            display: 'flex', flexDirection: 'column',
            transform: sidebarMode === 'open' ? 'translateX(0)' : 'translateX(-300px)',
            transition: 'transform 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
            boxShadow: sidebarMode === 'open' ? '4px 0 20px rgba(20,16,10,0.15)' : 'none',
          }}>
            {sidebarInner}
          </div>
        )}

        {/* Backdrop — только когда drawer открыт */}
        {sidebarMode === 'open' && (
          <div onClick={() => setSidebarMode('collapsed')} style={{ position: 'absolute', inset: 0, zIndex: 9, background: C.overlay }} />
        )}

        {/* Центр */}
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', background: C.bgMain }}>
          {activeChat ? (
            <ChatPanel
              key={activeChat.id}
              session={activeChat}
              skills={skills}
              attachedFiles={attachedFiles}
              onAttachedFilesChange={setAttachedFiles}
              onOpenSidebar={openSidebar}
              onSessionUpdated={updated => setChats(prev => prev.map(c => c.id === updated.id ? updated : c))}
              artifactsOpen={artifactsEnabled ? artifactsOpen : undefined}
              onToggleArtifacts={artifactsEnabled ? toggleArtifacts : undefined}
            />
          ) : (
            <>
              {sidebarMode === 'collapsed' && (
                <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', padding: '0 8px', height: 52, borderBottom: `1px solid ${C.divider}` }}>
                  <IconButton onClick={() => setSidebarMode('open')} title="Открыть панель" size="md" variant="soft">
                    <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
                      <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
                    </svg>
                  </IconButton>
                </div>
              )}
              <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
                <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', maxWidth: 400, gap: 10 }}>
                  {/* Иконка раздела */}
                  <div style={{ width: 56, height: 56, borderRadius: 16, background: C.bgPanel, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: 4 }}>
                    <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                      <path d="M21 11.5a8.5 8.5 0 0 1-12 7.7L3 21l1.8-6A8.5 8.5 0 1 1 21 11.5z" />
                    </svg>
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
                    leftIcon={
                      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
                        <path d="M12 5v14M5 12h14" />
                      </svg>
                    }
                  >
                    Новый чат
                  </Button>
                  <div style={{ fontSize: 12, color: C.textMuted, marginTop: 2 }}>или выберите чат слева</div>
                </div>
              </div>
            </>
          )}
        </div>

        {/* Панель артефактов сессии (за фич-флагом) — колонка справа от чата */}
        {artifactsEnabled && artifactsOpen && activeChat && (
          <>
            <Splitter active={draggingArtifacts} onMouseDown={handleArtifactsSplitterMouseDown} />
            <div style={{ width: artifactsWidth, flexShrink: 0, height: '100%' }}>
              <ArtifactsPanel sessionId={activeChat.id} onClose={() => setArtifactsOpen(false)} />
            </div>
          </>
        )}
      </div>
    </div>
  );
}
