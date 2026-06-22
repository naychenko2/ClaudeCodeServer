import { useState, useEffect, useRef } from 'react';
import type { Project, Session } from '../types';
import { SessionList } from '../components/SessionList';
import { FileExplorer } from '../components/FileExplorer';
import { ChatPanel } from '../components/ChatPanel';
import { FileViewer } from '../components/FileViewer';
import { ConnectionStatus } from '../components/ConnectionStatus';
import { joinProject, leaveProject, onReconnected } from '../lib/signalr';
import { loadWorkspaceState, saveWorkspaceState } from '../lib/workspaceState';

interface Props {
  project: Project;
  onBack: () => void;
}

type LeftTab = 'sessions' | 'files';

function useWindowWidth() {
  const [width, setWidth] = useState(window.innerWidth);
  useEffect(() => {
    const handler = () => setWidth(window.innerWidth);
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  }, []);
  return width;
}

export function WorkspacePage({ project, onBack }: Props) {
  // Восстанавливаем состояние окна для этого проекта (компонент перемонтируется при входе в проект)
  const [leftTab, setLeftTab] = useState<LeftTab>(() => loadWorkspaceState(project.id)?.leftTab ?? 'sessions');
  const [activeSession, setActiveSession] = useState<Session | null>(() => loadWorkspaceState(project.id)?.activeSession ?? null);
  const [pendingMessage, setPendingMessage] = useState<string | undefined>();
  const [openFile, setOpenFile] = useState<string | null>(() => loadWorkspaceState(project.id)?.openFile ?? null);
  const [fileFullscreen, setFileFullscreen] = useState(() => loadWorkspaceState(project.id)?.fileFullscreen ?? false);
  const [chatDockExpanded, setChatDockExpanded] = useState(() => loadWorkspaceState(project.id)?.chatDockExpanded ?? true);
  const [chatFlex, setChatFlex] = useState(1); // 1:1 = 50/50 по умолчанию
  const [chatHeight, setChatHeight] = useState(280);
  const splitContainerRef = useRef<HTMLDivElement>(null);
  // мобайл: показываем либо sidebar, либо chat
  const [mobileView, setMobileView] = useState<'sidebar' | 'chat'>('sidebar');

  const windowWidth = useWindowWidth();
  const isMobile = windowWidth < 768;
  const isTablet = windowWidth >= 768 && windowWidth < 1100;
  // Ширина сайдбара — перетаскиваемая, сохраняется между сессиями
  const [sidebarWidth, setSidebarWidth] = useState(() => {
    const v = localStorage.getItem('cc_sidebar_width');
    return v ? Math.max(220, Math.min(520, Number(v))) : 300;
  });
  useEffect(() => { localStorage.setItem('cc_sidebar_width', String(sidebarWidth)); }, [sidebarWidth]);

  const handleSidebarSplitterMouseDown = (e: React.MouseEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = sidebarWidth;
    const onMove = (ev: MouseEvent) => {
      setSidebarWidth(Math.max(220, Math.min(520, startW + (ev.clientX - startX))));
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  };

  const handleSelectSession = (session: Session, firstMessage?: string, autoSelect?: boolean) => {
    setActiveSession(session);
    setPendingMessage(firstMessage);
    if (isMobile && !autoSelect) setMobileView('chat');
    if (!autoSelect) {
      // явный выбор — закрываем файл, показываем чат во весь экран
      setOpenFile(null);
      setFileFullscreen(false);
    }
  };

  const handleSessionUpdated = (updated: Session) => {
    setActiveSession(prev => (prev?.id === updated.id ? updated : prev));
  };

  // Запоминаем состояние окна (активный чат/файл, панели) для проекта
  useEffect(() => {
    saveWorkspaceState(project.id, { activeSession, openFile, fileFullscreen, leftTab, chatDockExpanded });
  }, [project.id, activeSession, openFile, fileFullscreen, leftTab, chatDockExpanded]);

  // Членство в project-группе на всё время открытия проекта (для статусов и watcher'а файлов).
  // Владелец — WorkspacePage (не SessionList, который размонтируется при переходе на «Файлы»).
  useEffect(() => {
    joinProject(project.id).catch(() => {});
    onReconnected(() => joinProject(project.id).catch(() => {}));
    return () => { leaveProject(project.id).catch(() => {}); };
  }, [project.id]);

  // из дерева файлов → всегда fullscreen
  const handleOpenFileFromTree = (filePath: string) => {
    setOpenFile(filePath);
    setFileFullscreen(true);
    if (!openFile) setChatDockExpanded(true);  // при первом открытии — раскрыть; при переключении — сохранить
  };

  // из чата → split-режим (на планшете/мобайле — fullscreen)
  const handleOpenFileFromChat = (filePath: string) => {
    setOpenFile(filePath);
    if (isTablet) {
      setFileFullscreen(true);
      setChatDockExpanded(true);
    } else {
      setFileFullscreen(false);
    }
  };

  const handleCloseFile = () => {
    setOpenFile(null);
    setFileFullscreen(false);
  };

  const handleEnterFullscreen = () => {
    setFileFullscreen(true);
    setChatDockExpanded(true);
  };

  const handleSplitterMouseDown = (e: React.MouseEvent) => {
    e.preventDefault();
    const container = splitContainerRef.current;
    if (!container) return;
    const rect = container.getBoundingClientRect();
    const SPLITTER = 5;

    const onMove = (ev: MouseEvent) => {
      const available = rect.width - SPLITTER;
      const chatW = Math.max(200, Math.min(available - 200, ev.clientX - rect.left));
      const fileW = available - chatW;
      if (fileW > 0) setChatFlex(chatW / fileW);
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  };

  const handleVerticalSplitterMouseDown = (e: React.MouseEvent) => {
    e.preventDefault();
    const startY = e.clientY;
    const startHeight = chatHeight;
    const onMove = (ev: MouseEvent) => {
      const delta = startY - ev.clientY;
      setChatHeight(Math.max(120, Math.min(window.innerHeight - 200, startHeight + delta)));
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'row-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  };

  const handleMobileBack = () => {
    if (openFile) {
      setOpenFile(null);
      if (leftTab === 'files') setMobileView('sidebar');
      return;
    }
    setMobileView('sidebar');
  };

  const handleTabSwitch = (tab: LeftTab) => {
    setLeftTab(tab);
    if (isMobile) setMobileView('sidebar');
  };

  const TabSwitcher = (
    <div style={{ display: 'flex', gap: 3, background: '#E1D9CA', borderRadius: 10, padding: 3 }}>
      {(['sessions', 'files'] as LeftTab[]).map(tab => (
        <button key={tab}
          onClick={() => handleTabSwitch(tab)}
          style={{
            flex: 1, textAlign: 'center', fontSize: 13, fontWeight: 600,
            padding: 8, cursor: 'pointer', border: 'none',
            borderRadius: 7, transition: 'all 0.15s',
            background: leftTab === tab ? '#FFFFFF' : 'transparent',
            color: leftTab === tab ? '#2A251F' : '#756B5E',
            boxShadow: leftTab === tab ? '0 1px 2px rgba(0,0,0,0.06)' : 'none',
          }}
        >
          {tab === 'sessions' ? 'Чаты' : 'Файлы'}
        </button>
      ))}
    </div>
  );

  const Sidebar = (
    <div style={{ width: isMobile ? '100%' : sidebarWidth, borderRight: isMobile ? 'none' : '1px solid #DDD4C4', display: 'flex', flexDirection: 'column', background: '#EDE7DC', flexShrink: 0, height: '100%' }}>
      {/* Планшет/десктоп: логотип + tabs в одном header блоке */}
      {!isMobile && (
        <div style={{ padding: '18px 16px 14px', flexShrink: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 15, padding: '0 2px' }}>
            <div style={{ width: 28, height: 28, borderRadius: 8, background: '#D97757', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#F4F0E8" strokeWidth="2.2" strokeLinecap="round">
                <path d="M12 3v18M3 12h18M5.6 5.6l12.8 12.8M18.4 5.6L5.6 18.4"/>
              </svg>
            </div>
            <span style={{ fontFamily: "'PT Serif', serif", fontSize: 18, fontWeight: 500, color: '#2A251F' }}>Claude Code Server</span>
          </div>
          {TabSwitcher}
        </div>
      )}
      <div style={{ flex: 1, overflow: 'hidden' }}>
        {leftTab === 'sessions' ? (
          <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} />
        ) : (
          <FileExplorer project={project} activeFilePath={openFile} onOpenFile={(f) => { handleOpenFileFromTree(f); if (isMobile) setMobileView('chat'); }} />
        )}
      </div>
      {/* Project footer — клик возвращает к списку проектов */}
      <div
        onClick={onBack}
        style={{ padding: '11px 14px', borderTop: '1px solid #DDD4C4', display: 'flex', alignItems: 'center', gap: 11, background: '#E7E0D2', cursor: 'pointer', flexShrink: 0 }}
      >
        <ConnectionStatus variant="footer" title={project.name} subtitle={project.rootPath} />
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#9A8F7E" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M7 10l5-5 5 5M7 14l5 5 5-5" />
        </svg>
      </div>
    </div>
  );

  if (isMobile) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100vh', background: '#EDE7DC', fontFamily: "'Hanken Grotesk', sans-serif", overflow: 'hidden' }}>
        {/* Единая шапка: скрыта только при просмотре файла (у FileViewer своя шапка) */}
        {!openFile && (
          <div style={{ padding: '10px 14px', borderBottom: '1px solid #DDD4C4', background: '#EDE7DC', display: 'flex', alignItems: 'center', gap: 10, flexShrink: 0 }}>
            <button
              onClick={mobileView === 'sidebar' ? onBack : handleMobileBack}
              style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', background: 'none', border: 'none', cursor: 'pointer', flex: 1, minWidth: 0, padding: 0, gap: 2 }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 5 }}>
                <span style={{ fontSize: 20, color: '#8A8070', lineHeight: 1, flexShrink: 0 }}>‹</span>
                <span style={{ fontWeight: 700, fontSize: 15, color: '#2A251F', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{project.name}</span>
              </div>
              {activeSession && mobileView === 'chat' && (
                <span style={{ fontSize: 12, color: '#9A8F7E', paddingLeft: 22, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '100%' }}>{activeSession.name}</span>
              )}
            </button>
            <div style={{ display: 'flex', gap: 2, background: '#E1D9CA', borderRadius: 10, padding: 3, flexShrink: 0 }}>
              {(['sessions', 'files'] as LeftTab[]).map(tab => (
                <button key={tab}
                  onClick={() => handleTabSwitch(tab)}
                  style={{
                    fontSize: 13, fontWeight: 600, padding: '6px 10px', cursor: 'pointer', border: 'none',
                    borderRadius: 7, transition: 'all 0.15s', whiteSpace: 'nowrap',
                    background: leftTab === tab ? '#FFFFFF' : 'transparent',
                    color: leftTab === tab ? '#2A251F' : '#756B5E',
                    boxShadow: leftTab === tab ? '0 1px 2px rgba(0,0,0,0.06)' : 'none',
                  }}
                >
                  {tab === 'sessions' ? 'Чаты' : 'Файлы'}
                </button>
              ))}
            </div>
          </div>
        )}
        {/* Sidebar — ВСЕГДА в DOM: FileExplorer не теряет текущий путь при смене вида */}
        <div style={{ flex: 1, display: !openFile && mobileView === 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          <div style={{ flex: 1, overflow: 'hidden' }}>
            {leftTab === 'sessions'
              ? <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} />
              : <FileExplorer project={project} activeFilePath={openFile} onOpenFile={handleOpenFileFromTree} />
            }
          </div>
          <div
            onClick={onBack}
            style={{ padding: '11px 14px', borderTop: '1px solid #DDD4C4', display: 'flex', alignItems: 'center', gap: 11, background: '#E7E0D2', cursor: 'pointer', flexShrink: 0 }}
          >
            <ConnectionStatus variant="footer" title={project.name} subtitle={project.rootPath} />
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#9A8F7E" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M7 10l5-5 5 5M7 14l5 5 5-5" />
            </svg>
          </div>
        </div>
        {/* Чат — ВСЕГДА в DOM */}
        <div style={{ flex: 1, display: !openFile && mobileView !== 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          {activeSession
            ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} />
            : <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: '#8A8070', fontSize: 14 }}>Выберите или создайте чат</div>
          }
        </div>
        {/* Просмотр файла — FileViewer имеет свою шапку */}
        {openFile && (
          <div style={{ flex: 1, overflow: 'hidden' }}>
            <FileViewer project={project} filePath={openFile} onClose={() => {
              setOpenFile(null);
              if (leftTab === 'files') setMobileView('sidebar');
            }} />
          </div>
        )}
      </div>
    );
  }

  const NoSession = (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: '#8A8070', fontSize: 14 }}>
      Выберите или создайте чат
    </div>
  );

  return (
    <div style={{ display: 'flex', height: '100vh', background: '#F4F0E8', fontFamily: "'Hanken Grotesk', sans-serif", overflow: 'hidden' }}>
      {Sidebar}

      {/* Сплиттер между сайдбаром и контентом */}
      <div
        onMouseDown={handleSidebarSplitterMouseDown}
        style={{ flex: '0 0 5px', background: '#D4CFC4', cursor: 'col-resize', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}
        onMouseEnter={e => (e.currentTarget.style.background = '#BDB7AC')}
        onMouseLeave={e => (e.currentTarget.style.background = '#D4CFC4')}
      >
        <div style={{ width: 1, height: 32, background: '#A8A09A', borderRadius: 1, pointerEvents: 'none' }} />
      </div>

      {/* Нет открытого файла — только чат */}
      {!openFile && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {activeSession
            ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} />
            : NoSession}
        </div>
      )}

      {/* Файл открыт, split только на десктопе: [Chat] | [splitter] | [FileViewer] */}
      {openFile && !fileFullscreen && !isTablet && (
        <div ref={splitContainerRef} style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0 }}>
          <div style={{ flex: chatFlex, overflow: 'hidden', minWidth: 200 }}>
            {activeSession
              ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} />
              : NoSession}
          </div>
          {/* Сплиттер */}
          <div
            onMouseDown={handleSplitterMouseDown}
            style={{ flex: '0 0 5px', background: '#D4CFC4', cursor: 'col-resize', flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
            onMouseEnter={e => (e.currentTarget.style.background = '#BDB7AC')}
            onMouseLeave={e => (e.currentTarget.style.background = '#D4CFC4')}
          >
            <div style={{ width: 1, height: 32, background: '#A8A09A', borderRadius: 1, pointerEvents: 'none' }} />
          </div>
          <div style={{ flex: 1, overflow: 'hidden', minWidth: 200 }}>
            <FileViewer project={project} filePath={openFile} onClose={handleCloseFile} onToggleFullscreen={handleEnterFullscreen} />
          </div>
        </div>
      )}

      {/* Fullscreen: на десктопе — по флагу, на планшете — всегда */}
      {openFile && (fileFullscreen || isTablet) && (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <div style={{ flex: 1, overflow: 'hidden', minHeight: 100 }}>
            <FileViewer project={project} filePath={openFile} onClose={handleCloseFile} isFullscreen onToggleFullscreen={isTablet ? undefined : () => setFileFullscreen(false)} />
          </div>
          {/* Горизонтальный сплиттер — только когда чат развёрнут */}
          {chatDockExpanded && (
            <div
              onMouseDown={handleVerticalSplitterMouseDown}
              style={{
                flexShrink: 0, height: 5, background: '#D4CFC4',
                cursor: 'row-resize',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
              onMouseEnter={e => (e.currentTarget.style.background = '#BDB7AC')}
              onMouseLeave={e => (e.currentTarget.style.background = '#D4CFC4')}
            >
              <div style={{ width: 32, height: 1, background: '#A8A09A', borderRadius: 1, pointerEvents: 'none' }} />
            </div>
          )}
          {activeSession ? (
            <div style={{ flexShrink: 0, height: chatDockExpanded ? chatHeight : 56, overflow: 'hidden', transition: chatDockExpanded ? 'none' : 'height 0.2s ease' }}>
              <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} dockMode={chatDockExpanded ? 'expanded' : 'collapsed'} onToggleDock={() => setChatDockExpanded(p => !p)} />
            </div>
          ) : (
            <div style={{ flexShrink: 0, height: 56, display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#8A8070', fontSize: 13, background: '#EDE7DC' }}>
              Выберите или создайте чат
            </div>
          )}
        </div>
      )}
    </div>
  );
}
