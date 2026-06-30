import { useState, useEffect, useRef, useCallback } from 'react';
import type { Project, Session, AgentInfo, SkillsData } from '../types';
import { SessionList } from '../components/SessionList';
import { RolesPanel } from '../components/RolesPanel';
import { FileExplorer } from '../components/FileExplorer';
import { ChatPanel } from '../components/ChatPanel';
import { FileViewer } from '../components/FileViewer';
import { ArtifactsPanel } from '../components/ArtifactsPanel';
import { KnowledgePanel } from '../components/KnowledgePanel';
import { SkillsPanel } from '../components/SkillsPanel';
import { UsageScreen } from '../components/UsageScreen';
import { joinProject, leaveProject, onMessage, onReconnected } from '../lib/signalr';
import { loadWorkspaceState, saveWorkspaceState } from '../lib/workspaceState';
import { api } from '../lib/api';
import { useFeature, FLAGS } from '../lib/featureFlags';
import { C, FONT } from '../lib/design';
import { PillSwitch } from '../components/Toolbar';
import { BackButton } from '../components/ui';
import { navPush, type NavSnapshot } from '../lib/nav';
import { EditDialog } from '../features/projects/dialogs/EditDialog';

interface Props {
  project: Project;
  onGoToProjects: () => void;
}

type LeftTab = 'sessions' | 'files' | 'roles';
type FileSubTab = 'files' | 'knowledge';

function useWindowWidth() {
  const [width, setWidth] = useState(window.innerWidth);
  useEffect(() => {
    const handler = () => setWidth(window.innerWidth);
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  }, []);
  return width;
}

// Высота ВИДИМОЙ области (над клавиатурой). При открытии мобильной клавиатуры
// visualViewport ужимается — привязав к нему высоту контейнера, прижимаем композер
// к низу видимой части и убираем прокрутку поля ввода за экран.
function useViewportHeight() {
  const [h, setH] = useState(() => window.visualViewport?.height ?? window.innerHeight);
  useEffect(() => {
    const vv = window.visualViewport;
    const update = () => setH(vv?.height ?? window.innerHeight);
    update();
    vv?.addEventListener('resize', update);
    vv?.addEventListener('scroll', update);
    window.addEventListener('resize', update);
    return () => {
      vv?.removeEventListener('resize', update);
      vv?.removeEventListener('scroll', update);
      window.removeEventListener('resize', update);
    };
  }, []);
  return h;
}

// Современный ресайз-сплиттер: в покое — тонкая 1px-линия (как граница панели),
// на hover/drag — accent-линия с точечным grip; широкая невидимая hit-зона ±6px
function Splitter({ orientation, active, onMouseDown }: {
  orientation: 'v' | 'h';
  active: boolean;
  onMouseDown: (e: React.MouseEvent) => void;
}) {
  const vertical = orientation === 'v';
  return (
    <div
      onMouseDown={onMouseDown}
      style={{
        position: 'relative', flexShrink: 0, cursor: vertical ? 'col-resize' : 'row-resize',
        background: active ? C.accent : C.border, transition: 'background 0.15s ease',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        ...(vertical ? { flex: '0 0 1px', width: 1, alignSelf: 'stretch' } : { height: 1, width: '100%' }),
      }}
      onMouseEnter={e => { if (!active) (e.currentTarget.firstElementChild as HTMLElement).style.opacity = '1'; }}
      onMouseLeave={e => { if (!active) (e.currentTarget.firstElementChild as HTMLElement).style.opacity = '0'; }}
    >
      <div style={{
        position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)',
        borderRadius: 3, background: C.accent, opacity: active ? 1 : 0,
        transition: 'opacity 0.15s ease', pointerEvents: 'none',
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 3,
        ...(vertical ? { width: 4, height: 34, flexDirection: 'column' } : { width: 34, height: 4, flexDirection: 'row' }),
      }}>
        {[0, 1, 2].map(i => <span key={i} style={{ width: 2, height: 2, borderRadius: '50%', background: C.onAccent }} />)}
      </div>
      <div style={vertical
        ? { position: 'absolute', top: 0, bottom: 0, left: -6, right: -6, cursor: 'col-resize' }
        : { position: 'absolute', left: 0, right: 0, top: -6, bottom: -6, cursor: 'row-resize' }} />
    </div>
  );
}

export function WorkspacePage({ project, onGoToProjects }: Props) {
  // Восстанавливаем состояние окна для этого проекта (компонент перемонтируется при входе в проект)
  const [leftTab, setLeftTab] = useState<LeftTab>(() => {
    const saved = loadWorkspaceState(project.id)?.leftTab;
    return saved === 'sessions' || saved === 'files' || saved === 'roles' ? saved : 'sessions';
  });
  // Фича «Команда» (роли-собеседники) за фич-флагом — dark launch
  const rolesEnabled = useFeature(FLAGS.roles);
  // Если флаг выключен, а сохранён таб «Команда» — откатываемся на «Чаты»
  useEffect(() => {
    if (!rolesEnabled && leftTab === 'roles') setLeftTab('sessions');
  }, [rolesEnabled, leftTab]);
  // Опции левого переключателя: «Команда» появляется только при включённом флаге
  const leftTabOptions: { value: LeftTab; label: string }[] = [
    { value: 'sessions', label: 'Чаты' },
    ...(rolesEnabled ? [{ value: 'roles' as LeftTab, label: 'Команда' }] : []),
    { value: 'files', label: 'Файлы' },
  ];
  const [fileSubTab, setFileSubTab] = useState<FileSubTab>(() => loadWorkspaceState(project.id)?.fileSubTab ?? 'files');
  const [activeSession, setActiveSession] = useState<Session | null>(() => loadWorkspaceState(project.id)?.activeSession ?? null);
  const [pendingMessage, setPendingMessage] = useState<string | undefined>();
  const [openFile, setOpenFile] = useState<string | null>(() => loadWorkspaceState(project.id)?.openFile ?? null);
  const [fileFullscreen, setFileFullscreen] = useState(() => loadWorkspaceState(project.id)?.fileFullscreen ?? false);
  const [chatFlex, setChatFlex] = useState(1); // 1:1 = 50/50 по умолчанию
  const [workflowRunningFor, setWorkflowRunningFor] = useState<string | null>(null);
  const [showSkillsModal, setShowSkillsModal] = useState(false);
  const [showUsage, setShowUsage] = useState(false);
  // Ссылка «Подробная статистика» в pop-up бейджа fal.ai открывает единый экран «Использование»
  useEffect(() => {
    const open = () => setShowUsage(true);
    window.addEventListener('open-fal-stats', open);
    return () => window.removeEventListener('open-fal-stats', open);
  }, []);
  const [editProjectOpen, setEditProjectOpen] = useState(false);
  const [projectForEdit, setProjectForEdit] = useState(project);
  const activeSessionRef = useRef<Session | null>(null);
  activeSessionRef.current = activeSession;

  const handleWorkflowRunning = useCallback((active: boolean, sessionId: string) => {
    setWorkflowRunningFor(prev => {
      if (active) return sessionId;
      return prev === sessionId ? null : prev;
    });
  }, []);
  const splitContainerRef = useRef<HTMLDivElement>(null);
  const [indexedFileNames, setIndexedFileNames] = useState<Set<string>>(new Set());
  const [knowledgeDocMap, setKnowledgeDocMap] = useState<Map<string, string>>(new Map()); // filename → docId
  const [indexingFiles, setIndexingFiles] = useState<Set<string>>(new Set());
  const [indexingFolders, setIndexingFolders] = useState<Set<string>>(new Set());
  const [skillsData, setSkillsData] = useState<SkillsData | null>(null);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  const handleAttachToChat = useCallback((path: string) => {
    setAttachedFiles(prev => prev.includes(path) ? prev : [...prev, path]);
  }, []);
  const [selectedAgent, setSelectedAgent] = useState<AgentInfo | null>(() => {
    try {
      const raw = localStorage.getItem(`cc_agent_${project.id}`);
      return raw ? JSON.parse(raw) : null;
    } catch { return null; }
  });

  const handleAgentChange = (agent: AgentInfo | null) => {
    setSelectedAgent(agent);
    if (agent) localStorage.setItem(`cc_agent_${project.id}`, JSON.stringify(agent));
    else localStorage.removeItem(`cc_agent_${project.id}`);
  };

  useEffect(() => {
    api.knowledge.getStatus(project.id).then(s => {
      const names = new Set<string>();
      const docMap = new Map<string, string>();
      for (const d of s.documents) {
        const fname = d.name.split('/').pop() ?? d.name;
        names.add(fname);
        docMap.set(fname, d.id);
      }
      setIndexedFileNames(names);
      setKnowledgeDocMap(docMap);
    }).catch(() => {});
  }, [project.id]);

  useEffect(() => {
    api.skills.list(project.id).then(setSkillsData).catch(() => {});
  }, [project.id]);
  // мобайл: показываем либо sidebar, либо chat
  const [mobileView, setMobileView] = useState<'sidebar' | 'chat'>('sidebar');

const windowWidth = useWindowWidth();
  const viewportH = useViewportHeight();
  const isMobile = windowWidth < 768;
  const isTablet = windowWidth >= 768 && windowWidth < 1200;
  // Ширина сайдбара — перетаскиваемая, сохраняется между сессиями
  const [sidebarWidth, setSidebarWidth] = useState(() => {
    const v = localStorage.getItem('cc_sidebar_width');
    return v ? Math.max(220, Math.min(520, Number(v))) : 300;
  });
  useEffect(() => { localStorage.setItem('cc_sidebar_width', String(sidebarWidth)); }, [sidebarWidth]);

  // Режим сайдбара: pinned (в потоке) | collapsed (свёрнут) | open (drawer поверх контента)
  // Персистируется только 'pinned'/'collapsed'; 'open' — временное состояние
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed' | 'open'>(() => {
    const v = localStorage.getItem('cc_sidebar_mode');
    return v === 'collapsed' ? 'collapsed' : 'pinned';
  });
  useEffect(() => {
    if (sidebarMode !== 'open') {
      localStorage.setItem('cc_sidebar_mode', sidebarMode);
    }
  }, [sidebarMode]);

  // Панель «Артефакты сессии» (за фич-флагом): открыта/закрыта + ширина, персист в localStorage
  const artifactsEnabled = useFeature(FLAGS.sessionArtifacts);
  const [artifactsOpen, setArtifactsOpen] = useState(() => localStorage.getItem('cc_artifacts_open') === '1');
  useEffect(() => { localStorage.setItem('cc_artifacts_open', artifactsOpen ? '1' : '0'); }, [artifactsOpen]);
  const [artifactsWidth, setArtifactsWidth] = useState(() => {
    const v = localStorage.getItem('cc_artifacts_width');
    return v ? Math.max(240, Math.min(480, Number(v))) : 300;
  });
  useEffect(() => { localStorage.setItem('cc_artifacts_width', String(artifactsWidth)); }, [artifactsWidth]);
  const toggleArtifacts = useCallback(() => setArtifactsOpen(v => !v), []);

  // Какой сплиттер сейчас тащим — для подсветки на всём протяжении drag (даже если курсор соскользнул)
  const [draggingSplitter, setDraggingSplitter] = useState<null | 'sidebar' | 'split' | 'artifacts'>(null);
  useEffect(() => {
    if (!draggingSplitter) return;
    const up = () => setDraggingSplitter(null);
    window.addEventListener('mouseup', up);
    return () => window.removeEventListener('mouseup', up);
  }, [draggingSplitter]);

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

  const handleArtifactsSplitterMouseDown = (e: React.MouseEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startW = artifactsWidth;
    const onMove = (ev: MouseEvent) => {
      // Панель справа: тянем влево (clientX уменьшается) → ширина растёт
      setArtifactsWidth(Math.max(240, Math.min(480, startW - (ev.clientX - startX))));
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
    // На мобиле явный выбор чата — переход «вглубь»: пишем запись истории (для кнопки «назад»)
    if (isMobile && !autoSelect) {
      setMobileView('chat');
      navPush({ screen: 'project', project, view: 'chat', file: null });
    }
    if (!autoSelect) {
      // явный выбор — закрываем файл, показываем чат во весь экран
      setOpenFile(null);
    }
  };

  const handleSessionUpdated = (updated: Session) => {
    setActiveSession(prev => (prev?.id === updated.id ? updated : prev));
  };

  const handleResume = useCallback(async (message?: string) => {
    if (!activeSession || activeSession.status !== 'orphaned') return;
    try {
      const s = await api.sessions.create(project.id, activeSession.mode, activeSession.claudeSessionId ?? undefined, undefined, activeSession.model ?? undefined, activeSession.agentName ?? undefined);
      await api.sessions.delete(project.id, activeSession.id);
      handleSelectSession(s, message);
    } catch { /* офлайн или сбой — ничего не меняем */ }
  }, [activeSession, project.id]);

  // Запоминаем состояние окна (активный чат/файл, панели) для проекта
  useEffect(() => {
    saveWorkspaceState(project.id, { activeSession, openFile, fileFullscreen, leftTab, fileSubTab });
  }, [project.id, activeSession, openFile, fileFullscreen, leftTab, fileSubTab]);

  // Членство в project-группе на всё время открытия проекта (для статусов и watcher'а файлов).
  // Владелец — WorkspacePage (не SessionList, который размонтируется при переходе на «Файлы»).
  useEffect(() => {
    joinProject(project.id).catch(() => {});
    // onReconnected возвращает cleanup — иначе при смене проекта старый callback остаётся
    // навсегда и продолжает джойнить уже закрытый проект при каждом реконнекте
    const unsub = onReconnected(async () => {
      joinProject(project.id).catch(() => {});
      // Сервер не шлёт status_changed при рестарте — рефетчим статус активной сессии
      // чтобы session.status в ChatPanel не застрял в 'working' после убийства процесса
      const sess = activeSessionRef.current;
      if (!sess) return;
      try {
        const sessions = await api.sessions.list(sess.projectId);
        const fresh = sessions.find(s => s.id === sess.id);
        if (fresh && fresh.status !== sess.status) {
          setActiveSession(prev => prev?.id === fresh.id ? { ...prev, status: fresh.status } : prev);
        }
      } catch { /* офлайн — оставляем как есть */ }
    });
    return () => { leaveProject(project.id).catch(() => {}); unsub(); };
  }, [project.id]);

  // Обновляем статус activeSession при status_changed — иначе session.status в ChatPanel frozen
  useEffect(() => {
    return onMessage(msg => {
      if (msg.type !== 'status_changed') return;
      setActiveSession(prev =>
        prev?.id === msg.sessionId
          ? { ...prev, status: msg.status as Session['status'] }
          : prev
      );
    });
  }, []);

  // Кнопки «назад/вперёд» браузера внутри проекта: восстанавливаем вид (sidebar/chat)
  // и открытый файл из снимка истории. Уровень проекта обрабатывает App из того же popstate.
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen !== 'project') return; // выход из проекта — обработает App
      setMobileView(s.view ?? 'sidebar');
      const f = s.file ?? null;
      setOpenFile(f);
      if (f === null) setFileFullscreen(false);
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  // из дерева файлов → всегда полноэкранный режим
  const handleOpenFileFromTree = (filePath: string) => {
    setOpenFile(filePath);
    setFileFullscreen(true);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
  };

  // из чата → split на десктопе, fullscreen на планшете/мобайле
  const handleOpenFileFromChat = (filePath: string) => {
    setOpenFile(filePath);
    setFileFullscreen(isMobile || isTablet);
    navPush({ screen: 'project', project, view: mobileView, file: filePath });
  };

  const handleEnterFullscreen = () => setFileFullscreen(true);

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

  const handleTabSwitch = (tab: LeftTab) => {
    setLeftTab(tab);
    if (isMobile) setMobileView('sidebar');
  };

  const handleAddToKnowledge = useCallback(async (relativePath: string) => {
    setIndexingFiles(prev => new Set([...prev, relativePath]));
    try {
      const result = await api.knowledge.indexFile(project.id, relativePath);
      const fileName = relativePath.split('/').pop() ?? relativePath;
      setIndexedFileNames(prev => new Set([...prev, fileName]));
      setKnowledgeDocMap(prev => new Map(prev).set(fileName, result.document.id));
    } catch {
      // KnowledgePanel сразу показывает актуальный статус
    } finally {
      setIndexingFiles(prev => { const next = new Set(prev); next.delete(relativePath); return next; });
    }
  }, [project.id]);

  const handleAddFolderToKnowledge = useCallback(async (relativePath: string) => {
    setIndexingFolders(prev => new Set([...prev, relativePath]));
    try {
      const result = await api.knowledge.indexFolder(project.id, relativePath);
      setIndexedFileNames(prev => {
        const next = new Set(prev);
        for (const doc of result.documents) {
          const fname = (doc as { name: string }).name.split('/').pop() ?? (doc as { name: string }).name;
          next.add(fname);
        }
        return next;
      });
      setKnowledgeDocMap(prev => {
        const next = new Map(prev);
        for (const doc of result.documents) {
          const d = doc as { id: string; name: string };
          const fname = d.name.split('/').pop() ?? d.name;
          next.set(fname, d.id);
        }
        return next;
      });
    } catch {
      // ignore — Dify может быть не настроен
    } finally {
      setIndexingFolders(prev => { const next = new Set(prev); next.delete(relativePath); return next; });
    }
  }, [project.id]);

  const handleRemoveFromKnowledge = useCallback(async (relativePath: string) => {
    const fileName = relativePath.split('/').pop() ?? relativePath;
    const docId = knowledgeDocMap.get(fileName);
    if (!docId) return;
    try {
      await api.knowledge.deleteDocument(project.id, docId);
      setIndexedFileNames(prev => { const n = new Set(prev); n.delete(fileName); return n; });
      setKnowledgeDocMap(prev => { const n = new Map(prev); n.delete(fileName); return n; });
    } catch {
      // игнорируем
    }
  }, [project.id, knowledgeDocMap]);



  const Sidebar = (
    <div style={{ width: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel, flexShrink: 0, height: '100%' }}>
      {/* Планшет/десктоп: логотип + tabs в одном header блоке */}
      {!isMobile && (
        <div style={{ padding: '16px 16px 14px', flexShrink: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8, padding: '0 2px' }}>
            <div onClick={onGoToProjects} style={{ display: 'flex', alignItems: 'center', gap: 10, flex: 1, minWidth: 0, cursor: 'pointer' }}>
              <div style={{ width: 28, height: 28, borderRadius: 8, background: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
                <svg width="16" height="16" viewBox="0 0 512 512" fill="none">
                  <g stroke="#FFFFFF" strokeWidth="52" strokeLinecap="round" fill="none">
                    <line x1="256" y1="130" x2="256" y2="382"/>
                    <line x1="130" y1="256" x2="382" y2="256"/>
                    <line x1="160" y1="160" x2="352" y2="352"/>
                    <line x1="352" y1="160" x2="160" y2="352"/>
                  </g>
                </svg>
              </div>
              <span style={{ fontFamily: FONT.serif, fontSize: 18, fontWeight: 500, color: C.textHeading, flex: 1, minWidth: 0 }}>Claude Home Server</span>
            </div>
            {/* В режиме open — кнопка «закрепить» (📌) */}
            {sidebarMode === 'open' && (
              <button
                onClick={() => setSidebarMode('pinned')}
                title="Закрепить панель"
                style={{ width: 28, height: 28, border: 'none', borderRadius: 8, background: 'transparent', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
              >
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                  <line x1="12" y1="17" x2="12" y2="22"/><path d="M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4h1v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24Z"/>
                </svg>
              </button>
            )}
            {/* Кнопка свернуть (◀) — в обоих режимах */}
            <button
              onClick={() => setSidebarMode('collapsed')}
              title="Свернуть панель"
              style={{ width: 28, height: 28, border: 'none', borderRadius: 8, background: 'transparent', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M15 6l-6 6 6 6"/>
              </svg>
            </button>
          </div>
          {/* Строка проекта: имя + кнопка настроек */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 13, padding: '0 2px' }}>
            <div style={{ width: 8, height: 8, borderRadius: '50%', background: C.accent, flexShrink: 0 }} />
            <span style={{ fontSize: 13, fontWeight: 500, color: C.textPrimary, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {projectForEdit.name}
            </span>
            <button
              onClick={() => setShowUsage(true)}
              title="Использование (Claude + fal.ai)"
              style={{ width: 22, height: 22, border: 'none', borderRadius: 6, background: 'transparent', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
            >
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M22 12h-4l-3 9L9 3l-3 9H2"/>
              </svg>
            </button>
            <button
              onClick={() => setEditProjectOpen(true)}
              title="Настройки проекта"
              style={{ width: 22, height: 22, border: 'none', borderRadius: 6, background: 'transparent', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
            >
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <circle cx="12" cy="12" r="3"/>
                <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
              </svg>
            </button>
            <button
              onClick={() => setShowSkillsModal(true)}
              title="Скиллы и агенты"
              style={{ width: 22, height: 22, border: 'none', borderRadius: 6, background: 'transparent', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
            >
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="m21.64 3.64-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2.36 18.64a1.21 1.21 0 0 0 0 1.72l1.28 1.28a1.2 1.2 0 0 0 1.72 0L21.64 5.36a1.2 1.2 0 0 0 0-1.72Z"/>
                <path d="m14 7 3 3"/>
                <path d="M5 6v4"/>
                <path d="M19 14v4"/>
                <path d="M10 2v2"/>
                <path d="M7 8H3"/>
                <path d="M21 16h-4"/>
                <path d="M11 3H9"/>
              </svg>
            </button>
          </div>
          <PillSwitch<LeftTab>
            value={leftTab}
            options={leftTabOptions}
            onChange={handleTabSwitch}
            fill
          />
        </div>
      )}
      <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {leftTab === 'sessions' ? (
          <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} workflowRunningFor={workflowRunningFor ?? undefined} selectedAgent={selectedAgent} />
        ) : leftTab === 'roles' ? (
          <RolesPanel project={project} onStartChat={s => handleSelectSession(s)} isMobile={isMobile} />
        ) : (
          <div style={{ flex: 1, overflow: 'hidden' }}>
            {fileSubTab === 'files'
              ? <FileExplorer project={project} activeFilePath={openFile} isMobile={isMobile} alwaysShowIcons={isTablet} onOpenFile={(f) => { handleOpenFileFromTree(f); if (isMobile) setMobileView('chat'); }} onAddToKnowledge={handleAddToKnowledge} onAddFolderToKnowledge={handleAddFolderToKnowledge} onRemoveFromKnowledge={handleRemoveFromKnowledge} indexedFileNames={indexedFileNames} indexingFiles={indexingFiles} indexingFolders={indexingFolders} onAttachToChat={activeSession && !fileFullscreen ? handleAttachToChat : undefined} onOpenKnowledge={() => setFileSubTab('knowledge')} />
              : <KnowledgePanel project={project} isMobile={isMobile} alwaysShowIcons={isTablet} onDocumentsChanged={setIndexedFileNames} onBack={() => setFileSubTab('files')} />
            }
          </div>
        )}
      </div>
    </div>
  );

  if (isMobile) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: viewportH, background: C.bgPanel, fontFamily: FONT.sans, overflow: 'hidden', position: 'relative' }}>
        {/* Верхняя шапка — только в режиме списка (sidebar). В режиме чата своя
            самодостаточная шапка ChatHeaderBar с кнопкой «назад»; у файла — шапка FileViewer */}
        {!openFile && mobileView === 'sidebar' && (
          <div style={{ padding: '10px 14px', borderBottom: `1px solid ${C.border}`, background: C.bgPanel, display: 'flex', alignItems: 'center', gap: 10, flexShrink: 0 }}>
            <BackButton onClick={onGoToProjects} title={project.name} style={{ flex: 1, minHeight: 40 }}>
              <span style={{ fontWeight: 700, fontSize: 15, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{project.name}</span>
            </BackButton>
            <PillSwitch<LeftTab>
              value={leftTab}
              options={leftTabOptions}
              onChange={handleTabSwitch}
              isMobile
            />
            <button
              onClick={() => setShowSkillsModal(true)}
              title="Скиллы и агенты"
              style={{ width: 34, height: 34, border: 'none', borderRadius: 9, background: 'transparent', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, flexShrink: 0 }}
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="m21.64 3.64-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2.36 18.64a1.21 1.21 0 0 0 0 1.72l1.28 1.28a1.2 1.2 0 0 0 1.72 0L21.64 5.36a1.2 1.2 0 0 0 0-1.72Z"/>
                <path d="m14 7 3 3"/>
                <path d="M5 6v4"/>
                <path d="M19 14v4"/>
                <path d="M10 2v2"/>
                <path d="M7 8H3"/>
                <path d="M21 16h-4"/>
                <path d="M11 3H9"/>
              </svg>
            </button>
          </div>
        )}
        {/* Sidebar — ВСЕГДА в DOM: FileExplorer не теряет текущий путь при смене вида */}
        <div style={{ flex: 1, display: !openFile && mobileView === 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
            {leftTab === 'sessions'
              ? <SessionList project={project} activeSession={activeSession} onSelect={handleSelectSession} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} workflowRunningFor={workflowRunningFor ?? undefined} selectedAgent={selectedAgent} />
              : leftTab === 'roles'
              ? <RolesPanel project={project} onStartChat={s => { handleSelectSession(s); setMobileView('chat'); }} isMobile={isMobile} />
              : (
                <div style={{ flex: 1, overflow: 'hidden' }}>
                  {fileSubTab === 'files'
                    ? <FileExplorer project={project} activeFilePath={openFile} isMobile={isMobile} alwaysShowIcons={isTablet} onOpenFile={handleOpenFileFromTree} onAddToKnowledge={handleAddToKnowledge} onAddFolderToKnowledge={handleAddFolderToKnowledge} onRemoveFromKnowledge={handleRemoveFromKnowledge} indexedFileNames={indexedFileNames} indexingFiles={indexingFiles} indexingFolders={indexingFolders} onAttachToChat={activeSession && !fileFullscreen ? handleAttachToChat : undefined} onOpenKnowledge={() => setFileSubTab('knowledge')} />
                    : <KnowledgePanel project={project} isMobile={isMobile} alwaysShowIcons={isTablet} onDocumentsChanged={setIndexedFileNames} onBack={() => setFileSubTab('files')} />
                  }
                </div>
              )
            }
          </div>
        </div>
        {/* Чат — ВСЕГДА в DOM */}
        <div style={{ flex: 1, display: !openFile && mobileView !== 'sidebar' ? 'flex' : 'none', flexDirection: 'column', overflow: 'hidden' }}>
          {activeSession
            ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} onBack={() => window.history.back()} onWorkflowRunning={handleWorkflowRunning} skills={skillsData?.skills} agents={skillsData?.agents} selectedAgent={selectedAgent} onAgentChange={handleAgentChange} attachedFiles={attachedFiles} onAttachedFilesChange={setAttachedFiles} onResume={handleResume} artifactsOpen={artifactsOpen} onToggleArtifacts={artifactsEnabled ? toggleArtifacts : undefined} />
            : <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: '#8A8070', fontSize: 14 }}>Выберите или создайте чат</div>
          }
        </div>
        {/* Просмотр файла — FileViewer имеет свою шапку */}
        {openFile && (
          <div style={{ flex: 1, overflow: 'hidden' }}>
            <FileViewer project={project} filePath={openFile} isMobile onClose={() => window.history.back()} />
          </div>
        )}
        {/* Панель «Артефакты сессии» — мобайл: drawer поверх чата */}
        {artifactsEnabled && artifactsOpen && activeSession && !openFile && (
          <>
            <div onClick={() => setArtifactsOpen(false)}
              style={{ position: 'absolute', inset: 0, zIndex: 900, background: C.overlay }} />
            <div style={{ position: 'absolute', top: 0, right: 0, bottom: 0, zIndex: 901, width: 'min(92vw, 380px)', boxShadow: '-4px 0 20px rgba(20,16,10,0.18)' }}>
              <ArtifactsPanel sessionId={activeSession.id} projectId={project.id} rootPath={project.rootPath} isMobile
                onOpenFile={(f) => { setArtifactsOpen(false); handleOpenFileFromChat(f); }} onClose={() => setArtifactsOpen(false)} />
            </div>
          </>
        )}
        {/* Модальное окно скиллов/агентов */}
        {showSkillsModal && (
          <div
            style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', zIndex: 1000, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16 }}
            onClick={e => { if (e.target === e.currentTarget) setShowSkillsModal(false); }}
          >
            <div style={{ width: '100%', maxWidth: 600, height: 'min(70vh, 600px)', background: '#FFFFFF', borderRadius: 20, boxShadow: '0 24px 60px rgba(23,19,15,0.40)', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
              <div style={{ padding: '14px 20px', borderBottom: `1px solid ${C.border}`, display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0 }}>
                <span style={{ fontSize: 15, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>Скиллы и агенты</span>
                <button onClick={() => setShowSkillsModal(false)} style={{ border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 18, padding: '0 4px', borderRadius: 6 }}>✕</button>
              </div>
              <div style={{ flex: 1, overflow: 'hidden' }}>
                <SkillsPanel projectId={project.id} />
              </div>
            </div>
          </div>
        )}
        {editProjectOpen && (
          <EditDialog
            project={projectForEdit}
            onSuccess={updated => { setProjectForEdit(updated); setEditProjectOpen(false); }}
            onClose={() => setEditProjectOpen(false)}
          />
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
    <div style={{ display: 'flex', height: '100vh', background: C.bgMain, fontFamily: FONT.sans, overflow: 'hidden', position: 'relative' }}>

      {/* === Pinned: sidebar в flex-потоке, толкает контент === */}
      {sidebarMode === 'pinned' && (
        <>
          <div style={{ width: sidebarWidth, flexShrink: 0, height: '100%' }}>
            {Sidebar}
          </div>
          <Splitter orientation="v" active={draggingSplitter === 'sidebar'}
            onMouseDown={e => { setDraggingSplitter('sidebar'); handleSidebarSplitterMouseDown(e); }} />
        </>
      )}

      {/* === Collapsed / Open: sidebar absolute drawer === */}
      {sidebarMode !== 'pinned' && (
        <div style={{
          position: 'absolute', top: 0, left: 0, bottom: 0, zIndex: 10,
          width: 320,
          transform: sidebarMode === 'open' ? 'translateX(0)' : 'translateX(-320px)',
          transition: 'transform 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
          boxShadow: sidebarMode === 'open' ? '4px 0 20px rgba(20,16,10,0.15)' : 'none',
        }}>
          {Sidebar}
        </div>
      )}

      {/* Overlay — только когда drawer открыт */}
      {sidebarMode === 'open' && (
        <div
          onClick={() => setSidebarMode('collapsed')}
          style={{ position: 'absolute', inset: 0, zIndex: 9, background: C.overlay }}
        />
      )}

      {/* Проп для ChatPanel — открывает drawer когда sidebar не pinned */}
      {(() => {
        const openSidebar = sidebarMode !== 'pinned' ? () => setSidebarMode('open') : undefined;

        // NoSession с топбаром для ☰ (когда нет активного чата)
        const NoSessionWithBar = (
          <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
            {sidebarMode === 'collapsed' && (
              <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center', padding: '0 8px', height: 52, borderBottom: `1px solid ${C.divider}`, background: C.bgMain }}>
                <button
                  onClick={() => setSidebarMode('open')}
                  title="Открыть панель"
                  style={{ width: 34, height: 34, border: 'none', borderRadius: 9, background: C.bgPanel, cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}
                >
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                    <path d="M2 4h12M2 8h12M2 12h12" stroke={C.textMuted} strokeWidth="1.8" strokeLinecap="round"/>
                  </svg>
                </button>
              </div>
            )}
            {NoSession}
          </div>
        );

        return (
          <>
            {/* Нет открытого файла — только чат */}
            {!openFile && (
              <div style={{ flex: 1, overflow: 'hidden' }}>
                {activeSession
                  ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} onWorkflowRunning={handleWorkflowRunning} onOpenSidebar={openSidebar} skills={skillsData?.skills} agents={skillsData?.agents} selectedAgent={selectedAgent} onAgentChange={handleAgentChange} attachedFiles={attachedFiles} onAttachedFilesChange={setAttachedFiles} onResume={handleResume} artifactsOpen={artifactsOpen} onToggleArtifacts={artifactsEnabled ? toggleArtifacts : undefined} />
                  : NoSessionWithBar}
              </div>
            )}

            {/* Split: файл из чата, только на десктопе */}
            {openFile && !fileFullscreen && !isTablet && (
              <div ref={splitContainerRef} style={{ flex: 1, display: 'flex', overflow: 'hidden', minWidth: 0 }}>
                <div style={{ flex: chatFlex, overflow: 'hidden', minWidth: 200 }}>
                  {activeSession
                    ? <ChatPanel session={activeSession} project={project} onOpenFile={handleOpenFileFromChat} pendingMessage={pendingMessage} onPendingMessageSent={() => setPendingMessage(undefined)} onSessionUpdated={handleSessionUpdated} isMobile={isMobile} onWorkflowRunning={handleWorkflowRunning} onOpenSidebar={openSidebar} skills={skillsData?.skills} agents={skillsData?.agents} selectedAgent={selectedAgent} onAgentChange={handleAgentChange} attachedFiles={attachedFiles} onAttachedFilesChange={setAttachedFiles} onResume={handleResume} artifactsOpen={artifactsOpen} onToggleArtifacts={artifactsEnabled ? toggleArtifacts : undefined} />
                    : NoSessionWithBar}
                </div>
                <Splitter orientation="v" active={draggingSplitter === 'split'}
                  onMouseDown={e => { setDraggingSplitter('split'); handleSplitterMouseDown(e); }} />
                <div style={{ flex: 1, overflow: 'hidden', minWidth: 200 }}>
                  <FileViewer project={project} filePath={openFile} onClose={() => window.history.back()} onToggleFullscreen={handleEnterFullscreen} onOpenSidebar={openSidebar} />
                </div>
              </div>
            )}

            {/* Fullscreen: файл из дерева или планшет */}
            {openFile && (fileFullscreen || isTablet) && (
              <div style={{ flex: 1, overflow: 'hidden' }}>
                <FileViewer project={project} filePath={openFile} onClose={() => window.history.back()} onOpenSidebar={openSidebar} />
              </div>
            )}
          </>
        );
      })()}

      {/* Панель «Артефакты сессии» — десктоп: боковая колонка в потоке (никогда поверх) */}
      {artifactsEnabled && artifactsOpen && activeSession && !isTablet && (
        <>
          <Splitter orientation="v" active={draggingSplitter === 'artifacts'}
            onMouseDown={e => { setDraggingSplitter('artifacts'); handleArtifactsSplitterMouseDown(e); }} />
          <div style={{ width: artifactsWidth, flexShrink: 0, height: '100%' }}>
            <ArtifactsPanel sessionId={activeSession.id} projectId={project.id} rootPath={project.rootPath}
              onOpenFile={handleOpenFileFromChat} onClose={() => setArtifactsOpen(false)} />
          </div>
        </>
      )}

      {/* Панель «Артефакты сессии» — планшет: drawer поверх контента (узкий экран) */}
      {artifactsEnabled && artifactsOpen && activeSession && isTablet && (
        <>
          <div onClick={() => setArtifactsOpen(false)}
            style={{ position: 'absolute', inset: 0, zIndex: 19, background: C.overlay }} />
          <div style={{ position: 'absolute', top: 0, right: 0, bottom: 0, zIndex: 20, width: 'min(85vw, 360px)', boxShadow: '-4px 0 20px rgba(20,16,10,0.15)' }}>
            <ArtifactsPanel sessionId={activeSession.id} projectId={project.id} rootPath={project.rootPath}
              onOpenFile={(f) => { handleOpenFileFromChat(f); setArtifactsOpen(false); }} onClose={() => setArtifactsOpen(false)} />
          </div>
        </>
      )}

      {/* Модальное окно скиллов/агентов */}
      {showSkillsModal && (
        <div
          style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', zIndex: 1000, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16 }}
          onClick={e => { if (e.target === e.currentTarget) setShowSkillsModal(false); }}
        >
          <div style={{ width: '100%', maxWidth: 600, height: 'min(70vh, 600px)', background: '#FFFFFF', borderRadius: 20, boxShadow: '0 24px 60px rgba(23,19,15,0.40)', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <div style={{ padding: '14px 20px', borderBottom: `1px solid ${C.border}`, display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0 }}>
              <span style={{ fontSize: 15, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>Скиллы и агенты</span>
              <button onClick={() => setShowSkillsModal(false)} style={{ border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 18, padding: '0 4px', borderRadius: 6 }}>✕</button>
            </div>
            <div style={{ flex: 1, overflow: 'hidden' }}>
              <SkillsPanel projectId={project.id} />
            </div>
          </div>
        </div>
      )}
      {showUsage && <UsageScreen onClose={() => setShowUsage(false)} />}
      {editProjectOpen && (
        <EditDialog
          project={projectForEdit}
          onSuccess={updated => { setProjectForEdit(updated); setEditProjectOpen(false); }}
          onClose={() => setEditProjectOpen(false)}
        />
      )}
    </div>
  );
}
