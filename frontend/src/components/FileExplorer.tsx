import { useEffect, useLayoutEffect, useState, useCallback, useMemo, useRef, type ReactNode } from 'react';
import type { Project, FileEntry } from '../types';
import { api } from '../lib/api';
import { OfflineError } from '../lib/offline';

const KB_TEXT_EXT = new Set([
  '.txt', '.md', '.markdown', '.cs', '.ts', '.tsx', '.js', '.jsx',
  '.py', '.json', '.yaml', '.yml', '.xml', '.html', '.htm',
  '.css', '.scss', '.toml', '.ini', '.sh', '.bash', '.ps1',
  '.go', '.rs', '.java', '.kt', '.rb', '.php', '.swift',
  '.tf', '.hcl', '.sql', '.graphql', '.proto',
]);
const KB_FILE_EXT = new Set(['.pdf', '.docx', '.xlsx', '.xls', '.pptx', '.csv', '.epub']);

function isKnowledgeIndexable(filename: string): boolean {
  const dot = filename.lastIndexOf('.');
  const ext = dot > 0 ? filename.slice(dot).toLowerCase() : '';
  // нет расширения (Makefile, LICENSE) или дотфайл (.gitignore)
  if (ext === '' || ext === filename.toLowerCase()) return true;
  return KB_TEXT_EXT.has(ext) || KB_FILE_EXT.has(ext);
}
import { toggleSyncMark, useSyncMarks, computeSyncState, isSyncing, isDownloaded, loadSyncMarks, loadDownloadedSet } from '../lib/sync';
import { onFilesChanged } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { EmptyState } from './EmptyState';
import { C, R, FONT, MODAL_W, TB } from '../lib/design';
import { Modal, ModalActions, TextField } from './ui';

interface Props {
  project: Project;
  onOpenFile: (path: string) => void;
  activeFilePath?: string | null;
  isMobile?: boolean;
  alwaysShowIcons?: boolean;
  onAddToKnowledge?: (relativePath: string) => void;
  indexedFileNames?: Set<string>;
  indexingFiles?: Set<string>;
  onAttachToChat?: (path: string) => void;
  onOpenKnowledge?: () => void;
}

// Персистентное состояние дерева на уровне модуля — переживает размонтирование
// при переключении вкладок «Чаты»/«Файлы». Ключ — projectId.
interface ExplorerState {
  dirCache: Map<string, FileEntry[]>;
  expanded: Set<string>;       // только десктоп/планшет (дерево)
  mobileDir: string;           // текущая папка в мобильной навигации ('' = корень)
  search: string;
  searchResults: FileEntry[] | null;
  createInDir: string;
  scrollTop: number;
}
const _explorerStore = new Map<string, ExplorerState>();

/** Возвращает текущую папку для создания файлов в проводнике (используется из ChatPanel). */
export function getExplorerCreateInDir(projectId: string): string {
  return _explorerStore.get(projectId)?.createInDir ?? '';
}

const normPath = (p?: string | null) => (p ?? '').replace(/\\/g, '/');

const EXT_META: Record<string, { bg: string; fg: string; label: string }> = {
  ts:   { bg: '#E6EEF5', fg: '#3E7CA6', label: 'ts' },
  tsx:  { bg: '#E6EEF5', fg: '#3E7CA6', label: 'tsx' },
  js:   { bg: '#FBF3D5', fg: '#B5830A', label: 'js' },
  jsx:  { bg: '#FBF3D5', fg: '#B5830A', label: 'jsx' },
  cs:   { bg: '#F0E6F5', fg: '#8E4A82', label: 'cs' },
  py:   { bg: '#E7EFF5', fg: '#3E7CA6', label: 'py' },
  json: { bg: '#FBEBE0', fg: '#C2693B', label: 'json' },
  md:   { bg: '#EFEAE0', fg: '#8A8072', label: 'md' },
  txt:  { bg: '#EFEAE0', fg: '#9A8F7E', label: 'txt' },
  html: { bg: '#FBEBE0', fg: '#C2693B', label: 'html' },
  css:  { bg: '#E6EEF5', fg: '#3E7CA6', label: 'css' },
  png:  { bg: '#F2E6F0', fg: '#8E4A82', label: 'img' },
  jpg:  { bg: '#F2E6F0', fg: '#8E4A82', label: 'img' },
  svg:  { bg: '#F2E6F0', fg: '#8E4A82', label: 'svg' },
};

function getExtMeta(name: string) {
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  return EXT_META[ext] ?? { bg: '#EFEAE0', fg: '#9A8F7E', label: ext.slice(0, 3) || '•' };
}

function FolderIcon() {
  return (
    <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="#C2693B" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>
    </svg>
  );
}

// Шеврон-вправо — намёк «войти в папку» (мобильная навигация).
function ChevronRight() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
      <path d="m9 18 6-6-6-6" />
    </svg>
  );
}

// Иконка облака — маркер/тоггл синхронизации.
// direct — залит акцентом (помечен сам); inherited — залит светлым акцентом (через папку/проект);
// idle — контур (можно включить).
function CloudIcon({ variant }: { variant: 'direct' | 'inherited' | 'idle' }) {
  const color = variant === 'direct' ? '#D97757' : variant === 'inherited' ? '#D7A78D' : '#B0A697';
  const fill = variant === 'direct' ? '#D97757' : variant === 'inherited' ? '#EAC6B2' : 'none';
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill={fill} stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z" />
    </svg>
  );
}

// Спиннер — состояние «синхронизируется» (идёт докачка содержимого). Контрастный, чтобы был заметен.
function SyncSpinner() {
  return (
    <span style={{ display: 'flex', width: 16, height: 16, alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
      <span style={{ width: 14, height: 14, borderRadius: '50%', border: '2.5px solid #DACDB9', borderTopColor: '#C2532E', animation: 'spin 0.6s linear infinite' }} />
    </span>
  );
}

interface TreeNode {
  entry: FileEntry;
  depth: number;
}

function FilesTip({ icon, title, text }: { icon: ReactNode; title: string; text: string }) {
  return (
    <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
      <div style={{
        width: 28, height: 28, borderRadius: R.md, background: C.bgInset,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        flexShrink: 0, color: C.textSecondary,
      }}>
        {icon}
      </div>
      <div>
        <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, marginBottom: 2 }}>{title}</div>
        <div style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.55 }}>{text}</div>
      </div>
    </div>
  );
}

function FilesRootEmptyState() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', padding: '32px 16px 20px', gap: 16 }}>
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10 }}>
        <div style={{ width: 48, height: 48, borderRadius: 14, background: C.bgInset, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
          </svg>
        </div>
        <div style={{ textAlign: 'center' }}>
          <div style={{ fontFamily: FONT.serif, fontWeight: 500, fontSize: 18, color: C.textPrimary, letterSpacing: '-0.01em', marginBottom: 4 }}>Проект пуст</div>
          <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>Здесь пока нет файлов</div>
        </div>
      </div>

      <div style={{ width: '100%', display: 'flex', flexDirection: 'column', gap: 10, borderTop: `1px solid ${C.border}`, paddingTop: 16 }}>
        <FilesTip
          icon={
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/>
            </svg>
          }
          title="Файлы и структура проекта"
          text="Создавайте и загружайте файлы, организуйте их по папкам. Claude видит всю структуру проекта при работе над задачами."
        />
        <FilesTip
          icon={
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z"/>
            </svg>
          }
          title="Офлайн-доступ"
          text="Нажмите иконку облачка рядом с файлом или папкой — они сохранятся для просмотра без интернета. Помеченные файлы доступны в приложении даже при отсутствии соединения."
        />
      </div>
    </div>
  );
}

export function FileExplorer({ project, onOpenFile, activeFilePath, isMobile = false, alwaysShowIcons = false, onAddToKnowledge, indexedFileNames, indexingFiles, onAttachToChat, onOpenKnowledge }: Props) {
  const online = useOnline();
  const marks = useSyncMarks(project.id);
  const initial = _explorerStore.get(project.id);
  const [dirCache, setDirCache] = useState<Map<string, FileEntry[]>>(() => initial?.dirCache ?? new Map());
  const [expanded, setExpanded] = useState<Set<string>>(() => initial?.expanded ?? new Set());
  const [mobileDir, setMobileDir] = useState<string>(() => initial?.mobileDir ?? '');
  const [loadingDirs, setLoadingDirs] = useState<Set<string>>(new Set());
  const inFlight = useRef(new Set<string>());
  const dirCacheRef = useRef(dirCache);
  dirCacheRef.current = dirCache;

  const [search, setSearch] = useState(() => initial?.search ?? '');
  const [searchResults, setSearchResults] = useState<FileEntry[] | null>(() => initial?.searchResults ?? null);
  // Поиск идёт только по живому серверу (не кэшируется): офлайн/сбой → сообщение вместо краша
  const [searchError, setSearchError] = useState<string | null>(null);
  const [hoveredPath, setHoveredPath] = useState<string | null>(null);
  const [showCreateFile, setShowCreateFile] = useState(false);
  const [newFileName, setNewFileName] = useState('');
  const [createInDir, setCreateInDir] = useState(() => initial?.createInDir ?? '');
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const uploadInputRef = useRef<HTMLInputElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const activeNorm = normPath(activeFilePath);

  const loadDir = useCallback(async (path: string) => {
    if (inFlight.current.has(path)) return;
    inFlight.current.add(path);
    setLoadingDirs(prev => new Set(prev).add(path));
    try {
      // Офлайн-промах кэша не должен ронять загрузку — папка просто не раскроется
      const entries = await api.files.list(project.id, path).catch(() => null);
      if (entries) setDirCache(prev => new Map(prev).set(path, entries));
    } finally {
      inFlight.current.delete(path);
      setLoadingDirs(prev => { const n = new Set(prev); n.delete(path); return n; });
    }
  }, [project.id]);

  // Метки синхронизации + набор уже скачанных файлов — в общий стор (для маркеров/спиннеров)
  useEffect(() => { loadSyncMarks(project.id); loadDownloadedSet(project.id); }, [project.id]);

  // Watcher: при изменении файлов проекта перезагружаем затронутые ЗАГРУЖЕННЫЕ папки (дерево живое)
  useEffect(() => {
    return onFilesChanged(({ projectId, paths }) => {
      if (projectId !== project.id) return;
      const dirs = new Set<string>();
      for (const raw of paths) {
        const p = raw.replace(/\\/g, '/');
        const i = p.lastIndexOf('/');
        dirs.add(i < 0 ? '' : p.slice(0, i));
      }
      for (const d of dirs) if (dirCacheRef.current.has(d)) loadDir(d);
    });
  }, [project.id, loadDir]);

  // Переключение проекта: восстанавливаем сохранённое состояние либо грузим корень заново
  const mountedProjectRef = useRef<string | null>(null);
  useEffect(() => {
    if (mountedProjectRef.current === project.id) return;
    mountedProjectRef.current = project.id;
    inFlight.current.clear();
    const st = _explorerStore.get(project.id);
    if (st) {
      setDirCache(st.dirCache);
      setExpanded(st.expanded);
      setMobileDir(st.mobileDir);
      setCreateInDir(st.createInDir);
      setSearch(st.search);
      setSearchResults(st.searchResults);
      // Всегда перезагружаем корень — кэш мог устареть пока дерево не было смонтировано
      loadDir('');
      // на мобиле могли вернуться во вложенную папку — догрузим её содержимое
      if (st.mobileDir) loadDir(st.mobileDir);
    } else {
      setDirCache(new Map());
      setExpanded(new Set());
      setMobileDir('');
      setCreateInDir('');
      setSearch('');
      setSearchResults(null);
      loadDir('');
    }
  }, [project.id, loadDir]);

  // Сохраняем состояние дерева в модульный стор при любом изменении
  useEffect(() => {
    _explorerStore.set(project.id, {
      dirCache, expanded, mobileDir, search, searchResults, createInDir,
      scrollTop: scrollRef.current?.scrollTop ?? _explorerStore.get(project.id)?.scrollTop ?? 0,
    });
  }, [project.id, dirCache, expanded, mobileDir, search, searchResults, createInDir]);

  // Восстанавливаем позицию прокрутки после монтирования (дерево уже отрисовано из кэша)
  useLayoutEffect(() => {
    const st = _explorerStore.get(project.id);
    if (scrollRef.current && st) scrollRef.current.scrollTop = st.scrollTop;
  }, [project.id]);

  const handleScroll = () => {
    const st = _explorerStore.get(project.id);
    if (st && scrollRef.current) st.scrollTop = scrollRef.current.scrollTop;
  };

  const invalidateDir = useCallback(async (path: string) => {
    inFlight.current.delete(path);
    setDirCache(prev => { const n = new Map(prev); n.delete(path); return n; });
    await loadDir(path);
  }, [loadDir]);

  const handleToggleDir = async (entry: FileEntry) => {
    const { path } = entry;
    setCreateInDir(path);
    if (expanded.has(path)) {
      setExpanded(prev => { const n = new Set(prev); n.delete(path); return n; });
    } else {
      if (!dirCache.has(path)) await loadDir(path);
      setExpanded(prev => new Set(prev).add(path));
    }
  };

  // Мобильная навигация: вход в папку (содержимое уже в кэше — иначе грузим)
  const enterMobileDir = async (path: string) => {
    setMobileDir(path);
    setCreateInDir(path);
    if (!dirCache.has(path)) await loadDir(path);
  };

  const handleSearch = async (q: string) => {
    setSearch(q);
    if (!q.trim()) { setSearchResults(null); setSearchError(null); return; }
    try {
      const results = await api.files.search(project.id, q);
      setSearchResults(results);
      setSearchError(null);
    } catch (e) {
      // Поиск по живому серверу: офлайн/сбой не должен ронять промис в консоль
      setSearchResults([]);
      setSearchError(e instanceof OfflineError ? 'Поиск недоступен офлайн' : 'Не удалось выполнить поиск');
    }
  };

  // Переключение синхронизации файла/папки. Маркеры (direct + inherited у вложенных)
  // обновляются мгновенно через общий стор; содержимое докачивается фоном.
  const handleToggleSync = useCallback((entry: FileEntry, e: React.MouseEvent) => {
    e.stopPropagation();
    if (!online) return;
    toggleSyncMark(project.id, entry);
  }, [project.id, online]);

  const handleCreateFile = async () => {
    const path = createInDir ? `${createInDir}/${newFileName}` : newFileName;
    await api.files.createFile(project.id, path);
    setShowCreateFile(false);
    setNewFileName('');
    await invalidateDir(createInDir);
    if (createInDir) setExpanded(prev => new Set(prev).add(createInDir));
  };

  const handleUploadFiles = async (fileList: FileList | null) => {
    if (!fileList || fileList.length === 0) return;
    const dir = isMobile ? mobileDir : createInDir;
    setUploading(true);
    setUploadError(null);
    try {
      await Promise.all(Array.from(fileList).map(f => api.files.upload(project.id, f, dir)));
      await invalidateDir(dir);
      if (dir && !isMobile) setExpanded(prev => new Set(prev).add(dir));
    } catch (e) {
      setUploadError(e instanceof Error ? e.message : 'Ошибка загрузки');
    } finally {
      setUploading(false);
      if (uploadInputRef.current) uploadInputRef.current.value = '';
    }
  };

  const flatTree = useMemo((): TreeNode[] => {
    const walk = (path: string, depth: number): TreeNode[] => {
      const entries = dirCache.get(path) ?? [];
      const result: TreeNode[] = [];
      for (const entry of entries) {
        result.push({ entry, depth });
        if (entry.isDirectory && expanded.has(entry.path)) {
          result.push(...walk(entry.path, depth + 1));
        }
      }
      return result;
    };
    return walk('', 0);
  }, [dirCache, expanded]);

  const rootLoading = !dirCache.has('') && loadingDirs.has('');

  // === Мобильная навигация «по папкам» ===
  // Содержимое текущей папки: сначала директории, затем файлы (по имени)
  const mobileEntries = useMemo((): FileEntry[] => {
    const entries = dirCache.get(mobileDir);
    if (!entries) return [];
    return [...entries].sort((a, b) => {
      if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [dirCache, mobileDir]);

  // Сегменты хлебных крошек: [{ label, path }] от корня до текущей папки
  const breadcrumbs = useMemo(() => {
    const crumbs: { label: string; path: string }[] = [{ label: 'Файлы', path: '' }];
    if (mobileDir) {
      const parts = mobileDir.split('/').filter(Boolean);
      let acc = '';
      for (const part of parts) {
        acc = acc ? `${acc}/${part}` : part;
        crumbs.push({ label: part, path: acc });
      }
    }
    return crumbs;
  }, [mobileDir]);

  const mobileDirLoading = !dirCache.has(mobileDir) && loadingDirs.has(mobileDir);

  // mobileNav: папочная навигация (вход в папку вместо toggle, шеврон-вправо, без отступа-depth)
  const renderFileRow = (entry: FileEntry, depth: number, showPath = false, mobileNav = false) => {
    // Папка-родитель — показываем в результатах поиска, чтобы различать одноимённые файлы (MA5)
    const parentDir = showPath ? normPath(entry.path).split('/').slice(0, -1).join('/') : '';
    const isExpanded = expanded.has(entry.path);
    const isLoading = loadingDirs.has(entry.path);
    const em = entry.isDirectory ? null : getExtMeta(entry.name);
    const isActive = !entry.isDirectory && activeNorm !== '' && normPath(entry.path) === activeNorm;
    // Состояние синхронизации — из общего стора (не из устаревшего entry.synced листинга)
    const sstate = computeSyncState(marks, entry.path);
    // Файл помечен, но содержимое ещё не скачано → спиннер; папка в активной докачке → спиннер
    const pending = !entry.isDirectory && !!sstate && !isDownloaded(project.id, entry.path);
    const folderSyncing = entry.isDirectory && isSyncing(project.id, entry.path);
    return (
      <div
        key={entry.path}
        onClick={() => entry.isDirectory ? (mobileNav ? enterMobileDir(entry.path) : handleToggleDir(entry)) : onOpenFile(entry.path)}
        onMouseEnter={() => setHoveredPath(entry.path)}
        onMouseLeave={() => setHoveredPath(null)}
        style={{
          display: 'flex', alignItems: isMobile ? 'flex-start' : 'center', gap: 6,
          paddingLeft: 8 + depth * 16, paddingRight: 8,
          paddingTop: 6, paddingBottom: 6,
          borderRadius: 8, cursor: 'pointer',
          // во всю ширину панели: длинное имя усекается ellipsis, маркеры (M/облако) всегда видны
          width: '100%', boxSizing: 'border-box',
          background: isActive ? '#F1DDD1' : hoveredPath === entry.path ? '#E8E1D4' : (sstate || folderSyncing) ? '#F4ECE3' : 'transparent',
          boxShadow: isActive ? 'inset 2px 0 0 #D97757' : 'none',
          transition: 'background 0.1s',
        }}
      >
        {/* toggle-стрелка дерева — только десктоп/планшет; в мобильной навигации её нет */}
        {!mobileNav && (
          <span style={{ width: 12, flexShrink: 0, textAlign: 'center', userSelect: 'none', color: '#9A8F7E', fontSize: 9, lineHeight: 1 }}>
            {entry.isDirectory ? (isLoading ? '·' : (isExpanded ? '▾' : '▸')) : ''}
          </span>
        )}
        {entry.isDirectory ? (
          <span style={{ flexShrink: 0, display: 'flex' }}><FolderIcon /></span>
        ) : (
          <span style={{
            width: 23, height: 23, borderRadius: 6,
            background: em!.bg, color: em!.fg,
            fontFamily: "'JetBrains Mono', monospace",
            fontSize: 8.5, fontWeight: 700,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            flexShrink: 0, letterSpacing: '-0.02em',
          }}>{em!.label}</span>
        )}
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1, paddingTop: isMobile ? 3 : 0 }}>
          <span title={entry.name} style={{
            fontFamily: "'JetBrains Mono', monospace",
            fontSize: 13,
            fontWeight: entry.isDirectory ? 700 : 500,
            color: (!entry.isDirectory && indexedFileNames?.has(entry.name))
              ? '#3F7A4F'
              : '#39332B',
            ...(isMobile
              ? { whiteSpace: 'normal', wordBreak: 'break-all', lineHeight: 1.35 }
              : { whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }),
          }}>{entry.name}</span>
          {parentDir && (
            <span title={normPath(entry.path)} style={{
              fontFamily: "'JetBrains Mono', monospace", fontSize: 10.5, color: '#9A8F7E',
              ...(isMobile ? { whiteSpace: 'normal', wordBreak: 'break-all' } : { whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }),
            }}>{parentDir}</span>
          )}
        </span>
        {entry.isModified && (
          <span style={{ fontSize: 9, fontWeight: 700, color: '#C2693B', background: '#FBEBE0', width: 16, height: 16, borderRadius: 4, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>M</span>
        )}
        {/* Кнопка «добавить в чат» — только для файлов.
            Десктоп: появляется при hover. Мобила: всегда видна. */}
        {!entry.isDirectory && onAttachToChat && (
          hoveredPath === entry.path ? (
            <button
              onClick={e => { e.stopPropagation(); onAttachToChat(entry.path); }}
              title="Добавить в чат"
              style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 2, display: 'flex', alignItems: 'center', flexShrink: 0, color: C.accent }}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/>
              </svg>
            </button>
          ) : null
        )}
        {/* Кнопка «добавить в БЗ» — только для файлов не в БЗ.
            Десктоп: появляется при hover. Мобила: всегда видна (hover недоступен).
            Пока файл индексируется — спиннер вместо кнопки.
            Неподдерживаемый тип — иконка с крестиком (disabled). */}
        {!entry.isDirectory && onAddToKnowledge && !indexedFileNames?.has(entry.name) && (
          indexingFiles?.has(entry.path) ? (
            <span style={{ padding: 2, display: 'flex', alignItems: 'center', flexShrink: 0, color: '#3F7A4F' }}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <style>{`@keyframes kb-spin{to{transform:rotate(360deg)}} .kb-spin{transform-origin:center;animation:kb-spin 0.8s linear infinite}`}</style>
                <circle className="kb-spin" cx="12" cy="12" r="9" strokeDasharray="40 20" />
              </svg>
            </span>
          ) : !isKnowledgeIndexable(entry.name) ? null
          : isMobile || alwaysShowIcons || hoveredPath === entry.path ? (
            <button
              onClick={e => { e.stopPropagation(); onAddToKnowledge(entry.path); }}
              title="Добавить в базу знаний"
              style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 2, display: 'flex', alignItems: 'center', flexShrink: 0, color: '#3F7A4F' }}
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/>
                <path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>
              </svg>
            </button>
          ) : null
        )}
        {/* Маркер/тоггл синхронизации */}
        {(() => {
          const btnStyle: React.CSSProperties = { background: 'none', border: 'none', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', flexShrink: 0 };
          // Идёт докачка: файл ещё не скачан или папка активно синхронизируется → спиннер.
          // direct (можно управлять) — кликом отменяем; inherited — статичный индикатор.
          if (pending || folderSyncing) {
            if (online && sstate === 'direct') {
              return <button onClick={e => handleToggleSync(entry, e)} title="Отменить синхронизацию" style={btnStyle}><SyncSpinner /></button>;
            }
            return <span style={{ ...btnStyle, cursor: 'default' }} title="Загружается…"><SyncSpinner /></span>;
          }
          // Синхронизирован (содержимое скачано)
          if (sstate === 'inherited') {
            return <span style={{ ...btnStyle, cursor: 'default' }} title="Синхронизируется (через папку/проект)"><CloudIcon variant="inherited" /></span>;
          }
          if (online) {
            if (sstate === 'direct') {
              return <button onClick={e => handleToggleSync(entry, e)} title="Отключить синхронизацию" style={btnStyle}><CloudIcon variant="direct" /></button>;
            }
            if (isMobile || alwaysShowIcons || hoveredPath === entry.path) {
              return <button onClick={e => handleToggleSync(entry, e)} title="Синхронизировать для офлайна" style={btnStyle}><CloudIcon variant="idle" /></button>;
            }
            return null;
          }
          if (sstate === 'direct') {
            return <span style={{ ...btnStyle, cursor: 'default' }} title="Синхронизирован"><CloudIcon variant="direct" /></span>;
          }
          return null;
        })()}
        {/* Намёк «войти в папку» — только в мобильной навигации */}
        {mobileNav && entry.isDirectory && <ChevronRight />}
      </div>
    );
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Search */}
      <div style={{ padding: '4px 12px 10px' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '0 11px', height: 36 }}>
            <span style={{ color: C.textMuted, marginRight: 8, display: 'flex', flexShrink: 0 }}>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>
            </span>
            <input
              placeholder="Поиск…"
              value={search}
              onChange={e => handleSearch(e.target.value)}
              style={{ flex: 1, border: 'none', background: 'none', fontSize: 13, fontFamily: FONT.mono, color: C.textHeading, outline: 'none' }}
            />
            {search && (
              <button onClick={() => handleSearch('')} style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 13, padding: 0 }}>✕</button>
            )}
          </div>
          {onOpenKnowledge && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 2, background: TB.pillTrack, borderRadius: 8, padding: 2, flexShrink: 0 }}>
              {/* Файлы — активна */}
              <button title="Файлы" style={{ width: 28, height: 28, border: 'none', borderRadius: 6, cursor: 'default', display: 'flex', alignItems: 'center', justifyContent: 'center', background: C.bgMain, color: C.accent, boxShadow: TB.pillThumbShadow }}>
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                </svg>
              </button>
              {/* Знания — неактивна */}
              <button onClick={onOpenKnowledge} title="Знания" style={{ width: 28, height: 28, border: 'none', borderRadius: 6, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'transparent', color: C.textMuted }}>
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/>
                  <path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>
                </svg>
              </button>
            </div>
          )}
        </div>

        {online && (
          <div style={{ marginTop: 8, display: 'flex', gap: 6 }}>
            <div
              onClick={() => {
                if (isMobile) setCreateInDir(mobileDir);
                setShowCreateFile(true);
              }}
              style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7, height: 36, border: `1.5px dashed ${C.dashed}`, borderRadius: R.lg, color: C.accent, fontSize: 12.5, fontWeight: 600, cursor: 'pointer' }}
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M12 5v14M5 12h14"/></svg>
              Новый файл
            </div>
            <label
              title="Загрузить файлы"
              style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6, height: 36, paddingInline: 12, border: `1.5px dashed ${C.dashed}`, borderRadius: R.lg, color: uploading ? C.textMuted : C.accent, fontSize: 12.5, fontWeight: 600, cursor: uploading ? 'default' : 'pointer', opacity: uploading ? 0.6 : 1, flexShrink: 0 }}
            >
              <input
                ref={uploadInputRef}
                type="file"
                multiple
                disabled={uploading}
                style={{ display: 'none' }}
                onChange={e => handleUploadFiles(e.target.files)}
              />
              {uploading ? (
                <span style={{ width: 14, height: 14, borderRadius: '50%', border: '2px solid #DACDB9', borderTopColor: C.accent, animation: 'spin 0.6s linear infinite', display: 'inline-block' }} />
              ) : (
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>
              )}
              Загрузить
            </label>
          </div>
        )}
        {uploadError && (
          <div style={{ marginTop: 6, fontSize: 12, color: '#B4452F', fontFamily: FONT.sans, paddingLeft: 2 }}>{uploadError}</div>
        )}
        {/* Хинт целевой папки — только десктоп, когда есть куда */}
        {online && !isMobile && (
          <div style={{ marginTop: 5, fontSize: 11.5, color: C.textMuted, fontFamily: FONT.mono, paddingLeft: 2, display: 'flex', alignItems: 'center', gap: 4 }}>
            <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>
            {createInDir ? <span title={createInDir}>{createInDir}</span> : <span style={{ fontStyle: 'italic' }}>корень проекта</span>}
          </div>
        )}
      </div>

      {/* Хлебные крошки — только мобила, когда поиск неактивен */}
      {isMobile && searchResults === null && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: 2,
          padding: '0 10px 8px',
          overflowX: 'auto', whiteSpace: 'nowrap',
          // прячем горизонтальный скроллбар, прокрутка остаётся (тач)
          scrollbarWidth: 'none',
        }}>
          {breadcrumbs.map((crumb, i) => {
            const isLast = i === breadcrumbs.length - 1;
            return (
              <span key={crumb.path} style={{ display: 'flex', alignItems: 'center', flexShrink: 0 }}>
                {i > 0 && (
                  <span style={{ color: C.textMuted, fontSize: 13, padding: '0 2px', userSelect: 'none' }}>›</span>
                )}
                <span
                  onClick={isLast ? undefined : () => enterMobileDir(crumb.path)}
                  style={{
                    fontFamily: i === 0 ? FONT.sans : FONT.mono,
                    fontSize: 12.5,
                    fontWeight: isLast ? 700 : 600,
                    color: isLast ? C.accent : C.textSecondary,
                    background: 'none', border: 'none',
                    padding: '6px 6px', borderRadius: R.md,
                    cursor: isLast ? 'default' : 'pointer',
                    minHeight: 32, display: 'flex', alignItems: 'center',
                  }}
                >{crumb.label}</span>
              </span>
            );
          })}
        </div>
      )}

      {/* Tree (десктоп) / список папки (мобила) / результаты поиска */}
      <div ref={scrollRef} onScroll={handleScroll} style={{ flex: 1, overflow: 'auto', padding: '0 4px 12px' }}>
        {searchResults !== null ? (
          // Поиск (общий режим для всех форм-факторов)
          searchResults.length === 0 ? (
            <EmptyState
              icon={<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>}
              title={searchError ?? 'Ничего не найдено'}
              subtitle={searchError ? 'Подключитесь к серверу, чтобы искать файлы' : `Нет файлов по запросу «${search}»`}
            />
          ) : (
            searchResults.map(entry => renderFileRow(entry, 0, true))
          )
        ) : isMobile ? (
          // Мобильная навигация «по папкам»: содержимое текущей папки
          mobileDirLoading ? (
            <div style={{ padding: '24px 12px', color: C.textMuted, fontSize: 13, textAlign: 'center', fontFamily: FONT.mono }}>Загрузка…</div>
          ) : mobileEntries.length === 0 ? (
            mobileDir === '' ? <FilesRootEmptyState /> : (
              <EmptyState
                icon={<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>}
                title="Папка пуста"
                subtitle="Здесь пока нет файлов"
              />
            )
          ) : (
            mobileEntries.map(entry => renderFileRow(entry, 0, false, true))
          )
        ) : rootLoading ? (
          <div style={{ padding: '24px 12px', color: C.textMuted, fontSize: 13, textAlign: 'center', fontFamily: FONT.mono }}>Загрузка…</div>
        ) : flatTree.length === 0 ? (
          <FilesRootEmptyState />
        ) : (
          flatTree.map(({ entry, depth }) => renderFileRow(entry, depth))
        )}
      </div>

      {/* Диалог создания файла */}
      {showCreateFile && (
        <Modal
          width={MODAL_W.form}
          onClose={() => setShowCreateFile(false)}
          title="Новый файл"
          subtitle={
            createInDir
              ? <>В папке <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{createInDir}/</span></>
              : 'В корне проекта'
          }
          footer={
            <ModalActions
              confirmLabel="Создать"
              onConfirm={handleCreateFile}
              confirmDisabled={!newFileName.trim()}
              onCancel={() => setShowCreateFile(false)}
            />
          }
        >
          <TextField
            value={newFileName}
            onChange={setNewFileName}
            placeholder="name.py"
            mono
            autoFocus
            onEnter={handleCreateFile}
          />
        </Modal>
      )}
    </div>
  );
}
