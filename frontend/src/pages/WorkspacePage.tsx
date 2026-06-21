import { useState, useEffect } from 'react';
import type { Project, Session } from '../types';
import { SessionList } from '../components/SessionList';
import { FileExplorer } from '../components/FileExplorer';
import { ChatPanel } from '../components/ChatPanel';
import { FileViewer } from '../components/FileViewer';

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
  const [leftTab, setLeftTab] = useState<LeftTab>('sessions');
  const [activeSession, setActiveSession] = useState<Session | null>(null);
  const [pendingMessage, setPendingMessage] = useState<string | undefined>();
  const [openFile, setOpenFile] = useState<string | null>(null);
  // мобайл: показываем либо sidebar, либо chat
  const [mobileView, setMobileView] = useState<'sidebar' | 'chat'>('sidebar');

  const windowWidth = useWindowWidth();
  const isMobile = windowWidth < 768;
  const isTablet = windowWidth >= 768 && windowWidth < 1100;
  const sidebarWidth = isTablet ? 288 : 300;

  const handleSelectSession = (session: Session, firstMessage?: string) => {
    setActiveSession(session);
    setPendingMessage(firstMessage);
    if (isMobile) setMobileView('chat');
  };

  const handleMobileBack = () => {
    if (openFile) {
      setOpenFile(null);
      if (leftTab === 'files') setMobileView('sidebar');
      return;
    }
    setMobileView('sidebar');
  };

  const TabSwitcher = (
    <div style={{ display: 'flex', gap: 3, background: '#E1D9CA', borderRadius: 10, padding: 3 }}>
      {(['sessions', 'files'] as LeftTab[]).map(tab => (
        <button key={tab}
          onClick={() => setLeftTab(tab)}
          style={{
            flex: 1, textAlign: 'center', fontSize: 13, fontWeight: 600,
            padding: 8, cursor: 'pointer', border: 'none',
            borderRadius: 7, transition: 'all 0.15s',
            background: leftTab === tab ? '#FFFFFF' : 'transparent',
            color: leftTab === tab ? '#2A251F' : '#756B5E',
            boxShadow: leftTab === tab ? '0 1px 2px rgba(0,0,0,0.06)' : 'none',
          }}
        >
          {tab === 'sessions' ? 'Сессии' : 'Файлы'}
        </button>
      ))}
    </div>
  );

  const Sidebar = (
    <div style={{ width: isMobile ? '100%' : sidebarWidth, borderRight: isMobile ? 'none' : '1px solid #DDD4C4', display: 'flex', flexDirection: 'column', background: '#EDE7DC', flexShrink: 0, height: '100%' }}>
      {/* Мобайл: кнопка назад + название проекта + tabs */}
      {isMobile && (
        <>
          <div style={{ padding: '12px 16px', borderBottom: '1px solid #DDD4C4', display: 'flex', alignItems: 'center', gap: 8 }}>
            <button onClick={onBack} style={{ background: 'none', border: 'none', cursor: 'pointer', fontSize: 18, padding: 2, color: '#8A8070' }}>‹</button>
            <span style={{ fontWeight: 700, fontSize: 15, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: '#2A251F' }}>{project.name}</span>
          </div>
          <div style={{ padding: '10px 12px', borderBottom: '1px solid #DDD4C4' }}>{TabSwitcher}</div>
        </>
      )}
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
          <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} isMobile={isMobile} />
        ) : (
          <FileExplorer project={project} onOpenFile={(f) => { setOpenFile(f); if (isMobile) setMobileView('chat'); }} />
        )}
      </div>
      {/* Project footer — клик возвращает к списку проектов */}
      <div
        onClick={onBack}
        style={{ padding: '11px 14px', borderTop: '1px solid #DDD4C4', display: 'flex', alignItems: 'center', gap: 11, background: '#E7E0D2', cursor: 'pointer', flexShrink: 0 }}
      >
        <div style={{ width: 32, height: 32, borderRadius: 9, background: '#FFFFFF', border: '1px solid #E0D7C8', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
          <div style={{ width: 9, height: 9, borderRadius: '50%', background: '#5E8B4E' }} />
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: 13, fontWeight: 600, color: '#2A251F', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{project.name}</div>
          <div style={{ fontSize: 11, color: '#9A8F7E', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: "'JetBrains Mono', monospace" }}>{project.rootPath}</div>
        </div>
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#9A8F7E" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M7 10l5-5 5 5M7 14l5 5 5-5" />
        </svg>
      </div>
    </div>
  );

  const ChatArea = (
    <div style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0 }}>
      {/* Мобайл: кнопка назад над чатом — только когда открыт чат (не файл) */}
      {isMobile && !openFile && (
        <div style={{ position: 'absolute', top: 0, left: 0, right: 0, zIndex: 10, display: 'flex', alignItems: 'center', gap: 8, padding: '10px 16px', background: '#EDE7DC', borderBottom: '1px solid #DDD4C4' }}>
          <button onClick={handleMobileBack} style={{ background: 'none', border: 'none', cursor: 'pointer', fontSize: 18, padding: 2, color: '#8A8070' }}>‹</button>
          <span style={{ fontWeight: 700, fontSize: 15, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: '#2A251F' }}>
            {activeSession?.name ?? project.name}
          </span>
        </div>
      )}
      {openFile && !isMobile && (
        <div style={{ flex: 1, borderRight: '1px solid #D4CFC4', overflow: 'hidden' }}>
          <FileViewer project={project} filePath={openFile} onClose={() => setOpenFile(null)} />
        </div>
      )}
      {openFile && isMobile && (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          <FileViewer project={project} filePath={openFile} onClose={() => {
            setOpenFile(null);
            if (leftTab === 'files') setMobileView('sidebar');
          }} />
        </div>
      )}
      {!openFile && (
        <div style={{ flex: 1, overflow: 'hidden', ...(isMobile ? { paddingTop: 52 } : {}) }}>
          {activeSession ? (
            <ChatPanel session={activeSession} project={project} onOpenFile={(f) => setOpenFile(f)} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} />
          ) : (
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: '#8A8070', fontSize: 14 }}>
              Выберите или создайте сессию
            </div>
          )}
        </div>
      )}
    </div>
  );

  if (isMobile) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100vh', background: '#F4F0E8', fontFamily: "'Hanken Grotesk', sans-serif", overflow: 'hidden', position: 'relative' }}>
        {mobileView === 'sidebar' ? Sidebar : ChatArea}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', height: '100vh', background: '#F4F0E8', fontFamily: "'Hanken Grotesk', sans-serif", overflow: 'hidden' }}>
      {Sidebar}
      {openFile ? (
        <>
          <div style={{ flex: 1, borderRight: '1px solid #D4CFC4', overflow: 'hidden' }}>
            <FileViewer project={project} filePath={openFile} onClose={() => setOpenFile(null)} />
          </div>
          <div style={{ flex: 'none', width: 420, overflow: 'hidden' }}>
            {activeSession ? (
              <ChatPanel session={activeSession} project={project} onOpenFile={setOpenFile} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} />
            ) : (
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: '#8A8070', fontSize: 14 }}>
                Выберите или создайте сессию
              </div>
            )}
          </div>
        </>
      ) : (
        <div style={{ flex: 1, overflow: 'hidden' }}>
          {activeSession ? (
            <ChatPanel session={activeSession} project={project} onOpenFile={setOpenFile} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} />
          ) : (
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: '#8A8070', fontSize: 14 }}>
              Выберите или создайте сессию
            </div>
          )}
        </div>
      )}
    </div>
  );
}
