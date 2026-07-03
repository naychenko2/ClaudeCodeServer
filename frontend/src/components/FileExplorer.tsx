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
  if (ext === '' || ext === filename.toLowerCase()) return true;
  return KB_TEXT_EXT.has(ext) || KB_FILE_EXT.has(ext);
}
import { toggleSyncMark, useSyncMarks, computeSyncState, isSyncing, isDownloaded, loadSyncMarks, loadDownloadedSet } from '../lib/sync';
import { onFilesChanged } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { EmptyState } from './EmptyState';
import { C, R, FONT, MODAL_W, TB } from '../lib/design';
import { Modal, ModalActions, TextField, IconButton, Button } from './ui';

interface Props {
  project: Project;
  onOpenFile: (path: string) => void;
  activeFilePath?: string | null;
  isMobile?: boolean;
  alwaysShowIcons?: boolean;
  onAddToKnowledge?: (relativePath: string) => void;
  onAddFolderToKnowledge?: (relativePath: string) => void;
  indexedFileNames?: Set<string>;
  indexingFiles?: Set<string>;
  indexingFolders?: Set<string>;
  onAttachToChat?: (path: string) => void;
  onRemoveFromKnowledge?: (relativePath: string) => void;
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

// Единая сортировка записей: папки сверху, затем по имени без учёта регистра
const sortEntries = (entries: FileEntry[]): FileEntry[] =>
  [...entries].sort((a, b) => {
    if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
    return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
  });

// Возвращает [parentDir, name] из пути
const splitPath = (p: string): [string, string] => {
  const norm = normPath(p);
  const i = norm.lastIndexOf('/');
  return i < 0 ? ['', norm] : [norm.slice(0, i), norm.slice(i + 1)];
};

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

function ChevronRight() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
      <path d="m9 18 6-6-6-6" />
    </svg>
  );
}

function CloudIcon({ variant }: { variant: 'direct' | 'inherited' | 'idle' }) {
  const color = variant === 'direct' ? '#D97757' : variant === 'inherited' ? '#D7A78D' : '#B0A697';
  const fill = variant === 'direct' ? '#D97757' : variant === 'inherited' ? '#EAC6B2' : 'none';
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill={fill} stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z" />
    </svg>
  );
}

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

function FilesTip({ icon, title, text, extra }: { icon: ReactNode; title: string; text: string; extra?: ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
      <div style={{
        width: 28, height: 28, borderRadius: R.md, background: C.bgInset,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        flexShrink: 0, color: C.textSecondary,
      }}>
        {icon}
      </div>
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, marginBottom: 2 }}>{title}</div>
        <div style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.55 }}>{text}</div>
        {extra}
      </div>
    </div>
  );
}

function FilesRootEmptyState({ onCreateFile }: { onCreateFile?: () => void }) {
  const [copied, setCopied] = useState(false);
  const webdavUrl = `${window.location.origin}/projects/`;
  const handleCopyWebdav = () => {
    navigator.clipboard.writeText(webdavUrl);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };
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
        {onCreateFile && (
          <Button
            variant="primary"
            size="md"
            glow
            leftIcon={<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round"><path d="M12 5v14M5 12h14"/></svg>}
            onClick={onCreateFile}
          >
            Создать первый файл
          </Button>
        )}
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
        <FilesTip
          icon={
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/>
              <line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/>
            </svg>
          }
          title="Удалённый доступ к папке"
          text="Подключите как сетевой диск — все файлы будут доступны прямо в проводнике."
          extra={
            <div style={{ marginTop: 6, display: 'flex', alignItems: 'center', gap: 6, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '5px 8px' }}>
              <span style={{ flex: 1, fontFamily: FONT.mono, fontSize: 11, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {webdavUrl}
              </span>
              <button onClick={handleCopyWebdav} title="Скопировать URL" style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', color: copied ? '#3F7A4F' : C.textMuted, flexShrink: 0 }}>
                {copied
                  ? <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12"/></svg>
                  : <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>
                }
              </button>
            </div>
          }
        />
      </div>
    </div>
  );
}

// Иконка карандаша для переименования
function RenameIcon() {
  return (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
      <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
    </svg>
  );
}

// Иконка корзины для удаления
function TrashIcon() {
  return (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="3 6 5 6 21 6"/>
      <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>
      <path d="M10 11v6M14 11v6"/>
      <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>
    </svg>
  );
}

// Иконка книги с минусом — «удалить из знаний» (в строке файла, 15×15)
function BookMinusIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/>
      <path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>
      <line x1="14" y1="10" x2="22" y2="10"/>
    </svg>
  );
}

// Иконки для контекстного меню — 16×16, currentColor, Lucide-style
function MI_Attach() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/>
    </svg>
  );
}
function MI_BookPlus() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/>
      <path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>
      <line x1="18" y1="7" x2="18" y2="13"/>
      <line x1="15" y1="10" x2="21" y2="10"/>
    </svg>
  );
}
function MI_BookMinus() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/>
      <path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>
      <line x1="14" y1="10" x2="22" y2="10"/>
    </svg>
  );
}
function MI_Cloud() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z"/>
    </svg>
  );
}
function MI_Rename() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
      <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
    </svg>
  );
}
function MI_Move() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M5 12h14M13 6l6 6-6 6"/>
    </svg>
  );
}
function MI_Trash() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="3 6 5 6 21 6"/>
      <path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>
      <path d="M10 11v6M14 11v6"/>
      <path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>
    </svg>
  );
}

// Иконка папки с плюсом
function FolderPlusIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
      <line x1="12" y1="11" x2="12" y2="17"/>
      <line x1="9" y1="14" x2="15" y2="14"/>
    </svg>
  );
}

interface ContextMenuState {
  x: number;
  y: number;
  entry: FileEntry;
}

export function FileExplorer({ project, onOpenFile, activeFilePath, isMobile = false, alwaysShowIcons = false, onAddToKnowledge, onAddFolderToKnowledge, onRemoveFromKnowledge, indexedFileNames, indexingFiles, indexingFolders, onAttachToChat, onOpenKnowledge }: Props) {
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

  // === Rename state ===
  const [renamingPath, setRenamingPath] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState('');
  const renameCancelledRef = useRef(false);
  const renameInputRef = useRef<HTMLInputElement>(null);
  // Модальный диалог переименования — для мобилы/планшета (клавиатура не сбивает blur)
  const [showRenameModal, setShowRenameModal] = useState(false);
  const renameModalInputRef = useRef<HTMLInputElement>(null);

  // === Context menu state ===
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null);

  // === Delete confirm state ===
  const [deleteConfirm, setDeleteConfirm] = useState<FileEntry | null>(null);

  // === Create directory state ===
  const [showCreateDir, setShowCreateDir] = useState(false);
  const [newDirName, setNewDirName] = useState('');

  // === Drag & drop state ===
  const [dragPath, setDragPath] = useState<string | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null);

  // === Long press для мобилы ===
  const longPressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [pressingPath, setPressingPath] = useState<string | null>(null);

  // === Flash-highlight для новосозданного файла ===
  const [newlyCreatedPath, setNewlyCreatedPath] = useState<string | null>(null);

  // === Move modal state ===
  const [showMoveModal, setShowMoveModal] = useState(false);
  const [movingEntry, setMovingEntry] = useState<FileEntry | null>(null);

  const loadDir = useCallback(async (path: string) => {
    if (inFlight.current.has(path)) return;
    inFlight.current.add(path);
    setLoadingDirs(prev => new Set(prev).add(path));
    try {
      const entries = await api.files.list(project.id, path).catch(() => null);
      if (entries) setDirCache(prev => new Map(prev).set(path, entries));
    } finally {
      inFlight.current.delete(path);
      setLoadingDirs(prev => { const n = new Set(prev); n.delete(path); return n; });
    }
  }, [project.id]);

  useEffect(() => { loadSyncMarks(project.id); loadDownloadedSet(project.id); }, [project.id]);

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
      loadDir('');
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

  useEffect(() => {
    _explorerStore.set(project.id, {
      dirCache, expanded, mobileDir, search, searchResults, createInDir,
      scrollTop: scrollRef.current?.scrollTop ?? _explorerStore.get(project.id)?.scrollTop ?? 0,
    });
  }, [project.id, dirCache, expanded, mobileDir, search, searchResults, createInDir]);

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
      setSearchResults([]);
      setSearchError(e instanceof OfflineError ? 'Поиск недоступен офлайн' : 'Не удалось выполнить поиск');
    }
  };

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
    setNewlyCreatedPath(normPath(path));
    setTimeout(() => setNewlyCreatedPath(null), 1500);
  };

  const handleCreateDir = async () => {
    if (!newDirName.trim()) return;
    const path = createInDir ? `${createInDir}/${newDirName.trim()}` : newDirName.trim();
    await api.files.mkdir(project.id, path);
    setShowCreateDir(false);
    setNewDirName('');
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

  // === Rename handlers ===

  // Фокус и выделение имени без расширения для inline-редактирования (десктоп)
  useEffect(() => {
    if (!renamingPath || showRenameModal) return;
    const raf = requestAnimationFrame(() => {
      const input = renameInputRef.current;
      if (!input) return;
      input.focus();
      const dotIdx = renameValue.lastIndexOf('.');
      const end = dotIdx > 0 ? dotIdx : renameValue.length;
      input.setSelectionRange(0, end);
    });
    return () => cancelAnimationFrame(raf);
  }, [renamingPath]); // eslint-disable-line react-hooks/exhaustive-deps

  // Фокус и выделение имени без расширения для модального диалога (мобила/планшет).
  // Задержка 280мс на тач-устройствах — клавиатура выезжает ПОСЛЕ того, как модал
  // уже отрисован, иначе viewport-сдвиг от клавиатуры вызывает мигание.
  useEffect(() => {
    if (!showRenameModal) return;
    const delay = (isMobile || alwaysShowIcons) ? 280 : 0;
    let raf = 0;
    const timer = setTimeout(() => {
      raf = requestAnimationFrame(() => {
        const input = renameModalInputRef.current;
        if (!input) return;
        input.focus();
        const dotIdx = renameValue.lastIndexOf('.');
        const end = dotIdx > 0 ? dotIdx : renameValue.length;
        input.setSelectionRange(0, end);
      });
    }, delay);
    return () => { clearTimeout(timer); cancelAnimationFrame(raf); };
  }, [showRenameModal]); // eslint-disable-line react-hooks/exhaustive-deps

  const startRename = useCallback((entry: FileEntry) => {
    setContextMenu(null);
    setRenamingPath(entry.path);
    setRenameValue(entry.name);
    // На мобиле и планшете — модальный диалог, т.к. виртуальная клавиатура
    // вызывает blur на inline-input и сразу прерывает редактирование
    if (isMobile || alwaysShowIcons) setShowRenameModal(true);
  }, [isMobile, alwaysShowIcons]);

  const commitRename = useCallback(async () => {
    if (!renamingPath || !renameValue.trim()) {
      setRenamingPath(null);
      setRenameValue('');
      return;
    }
    const [parentDir, oldName] = splitPath(renamingPath);
    const newName = renameValue.trim();
    if (newName === oldName) {
      setRenamingPath(null);
      setRenameValue('');
      return;
    }
    const newPath = parentDir ? `${parentDir}/${newName}` : newName;
    try {
      await api.files.rename(project.id, renamingPath, newPath);
      setRenamingPath(null);
      setRenameValue('');
      await invalidateDir(parentDir);
    } catch {
      // оставляем режим редактирования при ошибке
    }
  }, [renamingPath, renameValue, project.id, invalidateDir]);

  const cancelRename = useCallback(() => {
    renameCancelledRef.current = true;
    setShowRenameModal(false);
    setRenamingPath(null);
    setRenameValue('');
  }, []);

  // Для модального диалога: закрываем модал до async-части commitRename
  const commitRenameModal = useCallback(async () => {
    setShowRenameModal(false);
    await commitRename();
  }, [commitRename]);

  // === Context menu handlers ===
  useEffect(() => {
    if (!contextMenu) return;
    const close = () => setContextMenu(null);
    // небольшая задержка — иначе правый клик, открывающий меню, сразу его закроет
    const timer = setTimeout(() => document.addEventListener('mousedown', close), 0);
    return () => { clearTimeout(timer); document.removeEventListener('mousedown', close); };
  }, [contextMenu]);

  const handleContextMenu = useCallback((e: React.MouseEvent, entry: FileEntry) => {
    e.preventDefault();
    e.stopPropagation();
    setContextMenu({ x: e.clientX, y: e.clientY, entry });
  }, []);

  // === Delete handler ===
  const handleDelete = useCallback(async (entry: FileEntry) => {
    const [parentDir] = splitPath(entry.path);
    try {
      await api.files.delete(project.id, entry.path);
      setDeleteConfirm(null);
      await invalidateDir(parentDir);
    } catch {
      setDeleteConfirm(null);
    }
  }, [project.id, invalidateDir]);

  // === Drag & Drop handlers ===
  const isDropForbidden = useCallback((from: string, to: string) => {
    if (from === to) return true;
    if (normPath(to).startsWith(normPath(from) + '/')) return true;
    // нельзя бросить в ту же папку где уже лежит
    const [sourceParent] = splitPath(from);
    if (sourceParent === normPath(to)) return true;
    return false;
  }, []);

  const handleDragStart = useCallback((e: React.DragEvent, entry: FileEntry) => {
    setDragPath(entry.path);
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', entry.path);
  }, []);

  const handleDragOver = useCallback((e: React.DragEvent, entry: FileEntry) => {
    if (!entry.isDirectory || !dragPath) return;
    if (isDropForbidden(dragPath, entry.path)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    setDropTarget(entry.path);
  }, [dragPath, isDropForbidden]);

  const handleDragLeave = useCallback((e: React.DragEvent, entry: FileEntry) => {
    if (entry.path !== dropTarget) return;
    if (!e.currentTarget.contains(e.relatedTarget as Node)) {
      setDropTarget(null);
    }
  }, [dropTarget]);

  const handleDrop = useCallback(async (e: React.DragEvent, targetEntry: FileEntry) => {
    e.preventDefault();
    setDropTarget(null);
    const dp = dragPath;
    setDragPath(null);
    if (!dp || !targetEntry.isDirectory) return;
    if (isDropForbidden(dp, targetEntry.path)) return;

    const [sourceParent, sourceName] = splitPath(dp);
    const targetPath = `${normPath(targetEntry.path)}/${sourceName}`;

    try {
      await api.files.rename(project.id, dp, targetPath);
      await Promise.all([
        invalidateDir(sourceParent),
        invalidateDir(targetEntry.path),
      ]);
    } catch {
      // тихо игнорируем ошибку перемещения
    }
  }, [dragPath, project.id, invalidateDir, isDropForbidden]);

  const handleDragEnd = useCallback(() => {
    setDragPath(null);
    setDropTarget(null);
  }, []);

  // === Move handler ===
  const handleMove = useCallback(async (entry: FileEntry, targetDir: string) => {
    const targetPath = targetDir ? `${normPath(targetDir)}/${entry.name}` : entry.name;
    const [sourceParent] = splitPath(entry.path);
    try {
      await api.files.rename(project.id, entry.path, targetPath);
      setShowMoveModal(false);
      setMovingEntry(null);
      await Promise.all([invalidateDir(sourceParent), invalidateDir(targetDir)]);
    } catch {
      // тихо игнорируем
    }
  }, [project.id, invalidateDir]);

  // Все загруженные папки (для диалога перемещения)
  const allDirs = useMemo(() => {
    const dirs: Array<{ path: string; label: string }> = [{ path: '', label: '/ (корень проекта)' }];
    for (const [, entries] of dirCache) {
      for (const e of entries) {
        if (e.isDirectory) dirs.push({ path: e.path, label: e.path });
      }
    }
    return dirs.sort((a, b) => a.path.localeCompare(b.path));
  }, [dirCache]);

  // === Long press (mobile) ===
  const handleTouchStart = useCallback((entry: FileEntry) => {
    if (longPressTimer.current) clearTimeout(longPressTimer.current);
    setPressingPath(entry.path);
    longPressTimer.current = setTimeout(() => {
      longPressTimer.current = null;
      if (navigator.vibrate) navigator.vibrate(10);
      setPressingPath(null);
      setContextMenu({ x: 0, y: 0, entry });
    }, 500);
  }, []);

  const handleTouchCancel = useCallback(() => {
    if (longPressTimer.current) {
      clearTimeout(longPressTimer.current);
      longPressTimer.current = null;
    }
    setPressingPath(null);
  }, []);

  const flatTree = useMemo((): TreeNode[] => {
    const walk = (path: string, depth: number): TreeNode[] => {
      const entries = sortEntries(dirCache.get(path) ?? []);
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

  const mobileEntries = useMemo((): FileEntry[] => {
    const entries = dirCache.get(mobileDir);
    if (!entries) return [];
    return sortEntries(entries);
  }, [dirCache, mobileDir]);

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

  const renderFileRow = (entry: FileEntry, depth: number, showPath = false, mobileNav = false) => {
    const parentDir = showPath ? normPath(entry.path).split('/').slice(0, -1).join('/') : '';
    const isExpanded = expanded.has(entry.path);
    const isLoading = loadingDirs.has(entry.path);
    const em = entry.isDirectory ? null : getExtMeta(entry.name);
    const isActive = !entry.isDirectory && activeNorm !== '' && normPath(entry.path) === activeNorm;
    const sstate = computeSyncState(marks, entry.path);
    const pending = !entry.isDirectory && !!sstate && !isDownloaded(project.id, entry.path);
    const folderSyncing = entry.isDirectory && isSyncing(project.id, entry.path);
    const isDropTgt = dropTarget === entry.path;
    const isDragging = dragPath === entry.path;
    const isRenaming = renamingPath === entry.path;

    const rowBg = isDropTgt
      ? '#F1DDD1'
      : isActive ? '#F1DDD1'
      : hoveredPath === entry.path ? '#E8E1D4'
      : normPath(entry.path) === newlyCreatedPath ? 'rgba(217,119,87,0.13)'
      : (sstate || folderSyncing) ? '#F4ECE3'
      : 'transparent';
    // Десктоп: кластер иконок липнет к правому краю видимой области при горизонтальном скролле
    const stickyIcons = !isMobile;

    const handleRowClick = () => {
      if (isRenaming) return;
      entry.isDirectory
        ? (mobileNav ? enterMobileDir(entry.path) : handleToggleDir(entry))
        : onOpenFile(entry.path);
    };

    return (
      <div
        key={entry.path}
        draggable={!isMobile && !alwaysShowIcons && !isRenaming}
        onClick={handleRowClick}
        onDoubleClick={!isMobile && !entry.isDirectory ? e => { e.stopPropagation(); startRename(entry); } : undefined}
        onContextMenu={e => handleContextMenu(e, entry)}
        onMouseEnter={() => setHoveredPath(entry.path)}
        onMouseLeave={() => setHoveredPath(null)}
        onDragStart={!isMobile && !alwaysShowIcons ? e => handleDragStart(e, entry) : undefined}
        onDragOver={!isMobile && !alwaysShowIcons && entry.isDirectory ? e => handleDragOver(e, entry) : undefined}
        onDragLeave={!isMobile && !alwaysShowIcons && entry.isDirectory ? e => handleDragLeave(e, entry) : undefined}
        onDrop={!isMobile && !alwaysShowIcons && entry.isDirectory ? e => handleDrop(e, entry) : undefined}
        onDragEnd={!isMobile && !alwaysShowIcons ? handleDragEnd : undefined}
        onTouchStart={isMobile || alwaysShowIcons ? () => handleTouchStart(entry) : undefined}
        onTouchEnd={isMobile || alwaysShowIcons ? (e) => {
          if (!longPressTimer.current) e.preventDefault();
          handleTouchCancel();
        } : undefined}
        onTouchMove={isMobile || alwaysShowIcons ? handleTouchCancel : undefined}
        onTouchCancel={isMobile || alwaysShowIcons ? handleTouchCancel : undefined}
        style={{
          display: 'flex', alignItems: isMobile ? 'flex-start' : 'center', gap: 6,
          paddingLeft: 8 + depth * 16, paddingRight: stickyIcons ? 0 : 8,
          paddingTop: isMobile || alwaysShowIcons ? 10 : 6,
          paddingBottom: isMobile || alwaysShowIcons ? 10 : 6,
          // фикс высоты: hover-иконки (24) чуть выше контента строки — держим 36, чтобы строка не «прыгала»
          minHeight: isMobile || alwaysShowIcons ? 44 : 36,
          borderRadius: 8, cursor: isDragging ? 'grabbing' : 'pointer',
          width: '100%', boxSizing: 'border-box',
          opacity: isDragging ? 0.4 : pressingPath === entry.path ? 0.6 : 1,
          transform: pressingPath === entry.path ? 'scale(0.98)' : 'none',
          background: rowBg,
          boxShadow: isDropTgt
            ? `inset 0 0 0 2px ${C.accent}`
            : isActive ? 'inset 2px 0 0 #D97757'
            : 'none',
          transition: 'background 0.1s, box-shadow 0.1s, opacity 0.1s, transform 0.1s',
        }}
      >
        {/* toggle-стрелка дерева — только десктоп/планшет */}
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
          {/* Inline-редактирование только на десктопе; мобила/планшет → Modal */}
          {isRenaming && !isMobile && !alwaysShowIcons ? (
            <input
              ref={renameInputRef}
              value={renameValue}
              onChange={e => setRenameValue(e.target.value)}
              onKeyDown={e => {
                if (e.key === 'Enter') { e.stopPropagation(); commitRename(); }
                if (e.key === 'Escape') { e.stopPropagation(); cancelRename(); }
              }}
              onBlur={() => {
                if (renameCancelledRef.current) { renameCancelledRef.current = false; return; }
                commitRename();
              }}
              onClick={e => e.stopPropagation()}
              style={{
                fontFamily: "'JetBrains Mono', monospace",
                fontSize: 13,
                fontWeight: entry.isDirectory ? 700 : 500,
                color: '#39332B',
                background: C.bgWhite,
                border: `1.5px solid ${C.accent}`,
                borderRadius: 4,
                padding: '1px 4px',
                outline: 'none',
                width: '100%',
                boxSizing: 'border-box',
              }}
            />
          ) : (
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
          )}
          {parentDir && (
            <span title={normPath(entry.path)} style={{
              fontFamily: "'JetBrains Mono', monospace", fontSize: 10.5, color: '#9A8F7E',
              ...(isMobile ? { whiteSpace: 'normal', wordBreak: 'break-all' } : { whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }),
            }}>{parentDir}</span>
          )}
        </span>
        {/* Кластер правых иконок: на десктопе — sticky, не уезжает при горизонтальном скролле */}
        <span style={{
          display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
          ...(stickyIcons ? {
            position: 'sticky' as const, right: 0,
            alignSelf: 'stretch',
            paddingLeft: 4, paddingRight: 8,
            background: rowBg === 'transparent' ? C.bgPanel : rowBg,
            borderRadius: '0 8px 8px 0',
          } : {}),
        }}>
        {entry.isModified && (
          <span style={{ fontSize: 9, fontWeight: 700, color: '#C2693B', background: '#FBEBE0', width: 16, height: 16, borderRadius: 4, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>M</span>
        )}
        {entry.isNew && (
          <span style={{ fontSize: 9, fontWeight: 700, color: '#3F7A4F', background: '#E2F0E6', width: 16, height: 16, borderRadius: 4, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>+</span>
        )}
        {/* Hover-иконки: переименовать + удалить — только десктоп при hover */}
        {online && !isRenaming && !isMobile && hoveredPath === entry.path && (
          <>
            <IconButton
              size="xs"
              onClick={e => { e.stopPropagation(); startRename(entry); }}
              title="Переименовать (F2)"
            >
              <RenameIcon />
            </IconButton>
            <IconButton
              size="xs"
              tone="danger"
              color="#C85A3F"
              onClick={e => { e.stopPropagation(); setDeleteConfirm(entry); }}
              title="Удалить"
            >
              <TrashIcon />
            </IconButton>
          </>
        )}
        {/* Кнопка «добавить в чат» — десктоп hover; мобила/планшет — через long-press меню */}
        {!entry.isDirectory && onAttachToChat && !isMobile && !alwaysShowIcons && (
          hoveredPath === entry.path ? (
            <IconButton
              size="xs"
              tone="accent"
              onClick={e => { e.stopPropagation(); onAttachToChat(entry.path); }}
              title="Добавить в чат"
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/>
              </svg>
            </IconButton>
          ) : null
        )}
        {/* Иконка знаний: спиннер при индексации; при hover — добавить или удалить */}
        {!entry.isDirectory && (
          indexingFiles?.has(entry.path) ? (
            <span style={{ padding: 2, display: 'flex', alignItems: 'center', flexShrink: 0, color: '#3F7A4F' }}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <style>{`@keyframes kb-spin{to{transform:rotate(360deg)}} .kb-spin{transform-origin:center;animation:kb-spin 0.8s linear infinite}`}</style>
                <circle className="kb-spin" cx="12" cy="12" r="9" strokeDasharray="40 20" />
              </svg>
            </span>
          ) : indexedFileNames?.has(entry.name) ? (
            !isMobile && !alwaysShowIcons && hoveredPath === entry.path && onRemoveFromKnowledge ? (
              <IconButton
                size="xs"
                tone="danger"
                color="#C85A3F"
                onClick={e => { e.stopPropagation(); onRemoveFromKnowledge(entry.path); }}
                title="Удалить из знаний"
              >
                <BookMinusIcon />
              </IconButton>
            ) : (
              <span style={{ padding: 2, display: 'flex', alignItems: 'center', flexShrink: 0, color: '#3F7A4F' }}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/>
                  <path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>
                </svg>
              </span>
            )
          ) : (
            // Не в знаниях — показать «добавить» при hover (только десктоп)
            onAddToKnowledge && isKnowledgeIndexable(entry.name) && !isMobile && !alwaysShowIcons && hoveredPath === entry.path ? (
              <IconButton
                size="xs"
                color="#3F7A4F"
                onClick={e => { e.stopPropagation(); onAddToKnowledge(entry.path); }}
                title="Добавить в базу знаний"
              >
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M2 3h6a4 4 0 0 1 4 4v14a3 3 0 0 0-3-3H2z"/>
                  <path d="M22 3h-6a4 4 0 0 0-4 4v14a3 3 0 0 1 3-3h7z"/>
                </svg>
              </IconButton>
            ) : null
          )
        )}
        {/* Маркер/тоггл синхронизации */}
        {(() => {
          const btnStyle: React.CSSProperties = { background: 'none', border: 'none', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', flexShrink: 0 };
          if (pending || folderSyncing) {
            if (online && sstate === 'direct') {
              return <button onClick={e => handleToggleSync(entry, e)} title="Отменить синхронизацию" style={btnStyle}><SyncSpinner /></button>;
            }
            return <span style={{ ...btnStyle, cursor: 'default' }} title="Загружается…"><SyncSpinner /></span>;
          }
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
        {/* Намёк «войти в папку» — только мобильная навигация */}
        {mobileNav && entry.isDirectory && <ChevronRight />}
        </span>
      </div>
    );
  };

  // Пункт контекстного меню — единый стиль.
  // onPointerDown + stopPropagation: предотвращает всплытие mousedown до document-listener
  // (который закрыл бы меню до срабатывания click).
  const menuItem = (icon: ReactNode, label: string, action: () => void, danger = false) => (
    <button
      key={label}
      onPointerDown={e => { e.stopPropagation(); action(); }}
      style={{
        display: 'flex', alignItems: 'center', width: '100%',
        padding: isMobile || alwaysShowIcons ? '14px 20px' : '8px 12px',
        background: 'none', border: 'none', cursor: 'pointer', textAlign: 'left',
        fontFamily: FONT.sans, fontSize: isMobile || alwaysShowIcons ? 15 : 13,
        color: danger ? '#C85A3F' : C.textPrimary,
        borderRadius: isMobile || alwaysShowIcons ? 0 : 6,
        gap: 10,
      }}
      onMouseEnter={e => { if (!isMobile && !alwaysShowIcons) (e.currentTarget as HTMLButtonElement).style.background = C.bgInset; }}
      onMouseLeave={e => { if (!isMobile && !alwaysShowIcons) (e.currentTarget as HTMLButtonElement).style.background = 'none'; }}
    >
      <span style={{ display: 'flex', alignItems: 'center', flexShrink: 0, opacity: 0.8 }}>{icon}</span>
      {label}
    </button>
  );

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
              <button title="Файлы" style={{ width: 28, height: 28, border: 'none', borderRadius: 6, cursor: 'default', display: 'flex', alignItems: 'center', justifyContent: 'center', background: C.bgMain, color: C.accent, boxShadow: TB.pillThumbShadow }}>
                <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                </svg>
              </button>
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
            {/* Новый файл */}
            <Button
              variant="dashed"
              size="md"
              leftIcon={<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M12 5v14M5 12h14"/></svg>}
              onClick={() => {
                if (isMobile) setCreateInDir(mobileDir);
                setShowCreateFile(true);
              }}
              style={{ flex: 1 }}
            >
              Новый файл
            </Button>
            {/* Новая папка */}
            <div
              onClick={() => {
                if (isMobile) setCreateInDir(mobileDir);
                setShowCreateDir(true);
              }}
              title="Новая папка"
              style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: 40, height: 40, border: `1.5px dashed ${C.dashed}`, borderRadius: R.lg, color: C.accent, cursor: 'pointer', flexShrink: 0 }}
            >
              <FolderPlusIcon />
            </div>
            {/* Загрузить */}
            <label
              title="Загрузить файлы"
              style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: 40, height: 40, border: `1.5px dashed ${C.dashed}`, borderRadius: R.lg, color: uploading ? C.textMuted : C.accent, cursor: uploading ? 'default' : 'pointer', opacity: uploading ? 0.6 : 1, flexShrink: 0 }}
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
            </label>
          </div>
        )}
        {uploadError && (
          <div style={{ marginTop: 6, fontSize: 12, color: '#B4452F', fontFamily: FONT.sans, paddingLeft: 2 }}>{uploadError}</div>
        )}
        {/* Хинт целевой папки — только десктоп */}
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

      {/* Tree / список папки / результаты поиска */}
      <div ref={scrollRef} onScroll={handleScroll} style={{ flex: 1, overflowX: isMobile ? 'hidden' : 'auto', overflowY: 'auto', padding: '0 4px 12px' }}>
        {searchResults !== null ? (
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
          mobileDirLoading ? (
            <div style={{ padding: '24px 12px', color: C.textMuted, fontSize: 13, textAlign: 'center', fontFamily: FONT.mono }}>Загрузка…</div>
          ) : mobileEntries.length === 0 ? (
            mobileDir === '' ? <FilesRootEmptyState onCreateFile={online ? () => { setCreateInDir(mobileDir); setShowCreateFile(true); } : undefined} /> : (
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
          <FilesRootEmptyState onCreateFile={online ? () => { setCreateInDir(''); setShowCreateFile(true); } : undefined} />
        ) : (
          // width: max-content — строки растягиваются под самое длинное имя,
          // контейнер даёт горизонтальный скролл вместо обрезания
          <div style={{ minWidth: '100%', width: 'max-content' }}>
            {flatTree.map(({ entry, depth }) => renderFileRow(entry, depth))}
          </div>
        )}
      </div>

      {/* === Модальный диалог переименования (мобила/планшет) === */}
      {showRenameModal && renamingPath && (() => {
        const [, oldName] = splitPath(renamingPath);
        const isDir = dirCache.get(splitPath(renamingPath)[0])?.find(e => e.path === renamingPath)?.isDirectory ?? false;
        return (
          <Modal
            width={MODAL_W.form}
            onClose={cancelRename}
            title={isDir ? 'Переименовать папку' : 'Переименовать файл'}
            subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{oldName}</span>}
            footer={
              <ModalActions
                confirmLabel="Переименовать"
                onConfirm={commitRenameModal}
                confirmDisabled={!renameValue.trim() || renameValue.trim() === oldName}
                onCancel={cancelRename}
              />
            }
          >
            <input
              ref={renameModalInputRef}
              value={renameValue}
              onChange={e => setRenameValue(e.target.value)}
              onKeyDown={e => {
                if (e.key === 'Enter') commitRenameModal();
                if (e.key === 'Escape') cancelRename();
              }}
              style={{
                fontFamily: "'JetBrains Mono', monospace",
                fontSize: 14,
                color: '#39332B',
                background: '#FFFFFF',
                border: `1.5px solid ${C.accent}`,
                borderRadius: 6,
                padding: '10px 12px',
                outline: 'none',
                width: '100%',
                boxSizing: 'border-box',
              }}
            />
          </Modal>
        );
      })()}

      {/* === Диалог создания файла === */}
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

      {/* === Диалог создания папки === */}
      {showCreateDir && (
        <Modal
          width={MODAL_W.form}
          onClose={() => setShowCreateDir(false)}
          title="Новая папка"
          subtitle={
            createInDir
              ? <>В папке <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{createInDir}/</span></>
              : 'В корне проекта'
          }
          footer={
            <ModalActions
              confirmLabel="Создать"
              onConfirm={handleCreateDir}
              confirmDisabled={!newDirName.trim()}
              onCancel={() => { setShowCreateDir(false); setNewDirName(''); }}
            />
          }
        >
          <TextField
            value={newDirName}
            onChange={setNewDirName}
            placeholder="my-folder"
            mono
            autoFocus
            onEnter={handleCreateDir}
          />
        </Modal>
      )}

      {/* === Диалог подтверждения удаления === */}
      {deleteConfirm && (
        <Modal
          width={MODAL_W.form}
          onClose={() => setDeleteConfirm(null)}
          title={deleteConfirm.isDirectory ? 'Удалить папку' : 'Удалить файл'}
          subtitle={
            <>
              <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{deleteConfirm.name}</span>
              {deleteConfirm.isDirectory && <span style={{ color: '#C85A3F' }}> со всем содержимым</span>}
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={() => handleDelete(deleteConfirm)}
              onCancel={() => setDeleteConfirm(null)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans }}>
            Это действие необратимо.
          </div>
        </Modal>
      )}

      {/* === Диалог перемещения === */}
      {showMoveModal && movingEntry && (() => {
        const [movingParent] = splitPath(movingEntry.path);
        const available = allDirs.filter(d => {
          if (d.path === movingParent) return false; // уже здесь
          if (movingEntry.isDirectory && (d.path === movingEntry.path || normPath(d.path).startsWith(normPath(movingEntry.path) + '/'))) return false;
          return true;
        });
        return (
          <Modal
            width={MODAL_W.form}
            onClose={() => { setShowMoveModal(false); setMovingEntry(null); }}
            title={`Переместить: ${movingEntry.name}`}
            subtitle={available.length === 0 ? 'Нет доступных папок — откройте нужные папки в дереве' : 'Выберите целевую папку'}
          >
            <div style={{ maxHeight: 320, overflow: 'auto', display: 'flex', flexDirection: 'column', gap: 2, margin: '0 -4px' }}>
              {available.length === 0 ? (
                <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans, padding: '8px 4px' }}>
                  Раскройте папки в проводнике, чтобы они появились здесь.
                </div>
              ) : available.map(d => (
                <button
                  key={d.path}
                  onPointerDown={e => { e.stopPropagation(); handleMove(movingEntry, d.path); }}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 8,
                    padding: '9px 8px', background: 'none', border: 'none',
                    cursor: 'pointer', borderRadius: R.md, textAlign: 'left',
                    fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary,
                    width: '100%',
                  }}
                  onMouseEnter={e => { (e.currentTarget as HTMLButtonElement).style.background = C.bgInset; }}
                  onMouseLeave={e => { (e.currentTarget as HTMLButtonElement).style.background = 'none'; }}
                >
                  <span style={{ flexShrink: 0, display: 'flex' }}><FolderIcon /></span>
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{d.label}</span>
                </button>
              ))}
            </div>
          </Modal>
        );
      })()}

      {/* === Контекстное меню === */}
      {contextMenu && (() => {
        const { entry } = contextMenu;
        const sstate = computeSyncState(marks, entry.path);
        const offlineLabel = sstate === 'direct' ? 'Убрать из офлайна' : 'Сохранить офлайн';
        const canToggleOffline = online && sstate !== 'inherited';
        const doToggleOffline = () => { setContextMenu(null); toggleSyncMark(project.id, entry); };

        // Мобила — bottom sheet
        if (isMobile || alwaysShowIcons) {
          return (
            <>
              <div
                onPointerDown={e => { e.stopPropagation(); setContextMenu(null); }}
                style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.35)', zIndex: 1000 }}
              />
              <div style={{
                position: 'fixed', bottom: 0, left: 0, right: 0,
                background: C.bgPanel,
                borderRadius: '16px 16px 0 0',
                paddingBottom: 24,
                zIndex: 1001,
                boxShadow: '0 -4px 32px rgba(0,0,0,0.12)',
              }}>
                {/* Ручка */}
                <div style={{ display: 'flex', justifyContent: 'center', padding: '10px 0 4px' }}>
                  <div style={{ width: 36, height: 4, borderRadius: 2, background: C.border }} />
                </div>
                <div style={{ padding: '4px 20px 12px', fontFamily: FONT.mono, fontSize: 13, fontWeight: 700, color: C.textPrimary, borderBottom: `1px solid ${C.border}` }}>
                  {entry.name}
                </div>
                {!entry.isDirectory && onAttachToChat && menuItem(<MI_Attach />, 'Прикрепить к чату', () => { setContextMenu(null); onAttachToChat(entry.path); })}
                {!entry.isDirectory && onAddToKnowledge && !indexedFileNames?.has(entry.name) && isKnowledgeIndexable(entry.name) && menuItem(<MI_BookPlus />, 'Добавить в знания', () => { setContextMenu(null); onAddToKnowledge(entry.path); })}
                {!entry.isDirectory && onRemoveFromKnowledge && indexedFileNames?.has(entry.name) && menuItem(<MI_BookMinus />, 'Удалить из знаний', () => { setContextMenu(null); onRemoveFromKnowledge(entry.path); })}
                {entry.isDirectory && onAddFolderToKnowledge && !indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Добавить папку в знания', () => { setContextMenu(null); onAddFolderToKnowledge(entry.path); })}
                {entry.isDirectory && indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Индексирование…', () => {})}
                {canToggleOffline && menuItem(<MI_Cloud />, offlineLabel, doToggleOffline)}
                <div style={{ height: 1, background: C.border, margin: '4px 20px' }} />
                {online && menuItem(<MI_Rename />, 'Переименовать', () => startRename(entry))}
                {online && menuItem(<MI_Move />, 'Переместить в...', () => { setContextMenu(null); setMovingEntry(entry); setShowMoveModal(true); })}
                {online && <div style={{ height: 1, background: C.border, margin: '4px 20px' }} />}
                {online && menuItem(<MI_Trash />, 'Удалить', () => { setContextMenu(null); setDeleteConfirm(entry); }, true)}
              </div>
            </>
          );
        }

        // Десктоп — popup
        return (
          <div
            style={{
              position: 'fixed',
              top: contextMenu.y,
              left: contextMenu.x,
              zIndex: 1000,
              background: C.bgWhite,
              border: `1px solid ${C.border}`,
              borderRadius: R.lg,
              boxShadow: '0 4px 16px rgba(0,0,0,0.12)',
              padding: 4,
              minWidth: 190,
            }}
          >
            {!entry.isDirectory && onAttachToChat && menuItem(<MI_Attach />, 'Прикрепить к чату', () => { setContextMenu(null); onAttachToChat(entry.path); })}
            {!entry.isDirectory && onAddToKnowledge && !indexedFileNames?.has(entry.name) && isKnowledgeIndexable(entry.name) && menuItem(<MI_BookPlus />, 'Добавить в знания', () => { setContextMenu(null); onAddToKnowledge(entry.path); })}
            {!entry.isDirectory && onRemoveFromKnowledge && indexedFileNames?.has(entry.name) && menuItem(<MI_BookMinus />, 'Удалить из знаний', () => { setContextMenu(null); onRemoveFromKnowledge(entry.path); })}
            {entry.isDirectory && onAddFolderToKnowledge && !indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Добавить папку в знания', () => { setContextMenu(null); onAddFolderToKnowledge(entry.path); })}
            {entry.isDirectory && indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Индексирование…', () => {})}
            {canToggleOffline && menuItem(<MI_Cloud />, sstate === 'direct' ? 'Убрать из офлайна' : 'Сохранить офлайн', doToggleOffline)}
            <div style={{ height: 1, background: C.border, margin: '4px 0' }} />
            {online && menuItem(<MI_Rename />, 'Переименовать', () => startRename(entry))}
            {online && menuItem(<MI_Move />, 'Переместить в...', () => { setContextMenu(null); setMovingEntry(entry); setShowMoveModal(true); })}
            <div style={{ height: 1, background: C.border, margin: '4px 0' }} />
            {online && menuItem(<MI_Trash />, 'Удалить', () => { setContextMenu(null); setDeleteConfirm(entry); }, true)}
          </div>
        );
      })()}
    </div>
  );
}
