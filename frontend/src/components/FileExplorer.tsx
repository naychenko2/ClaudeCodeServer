import { useEffect, useLayoutEffect, useState, useCallback, useMemo, useRef, type ReactNode } from 'react';
import { X, Folder, FolderPlus, ChevronRight, SquarePen, Trash2, ArrowRight, Paperclip, BookOpen, Search, Plus, Check, Copy, Upload, Monitor, Server, GitBranch, History, SlidersHorizontal } from 'lucide-react';
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
import { bumpNotes, getNotesSnapshot } from '../lib/notes';
import { IconNotes } from '../features/notes/shared';
import { NewNoteDialog } from '../features/notes/NewNoteDialog';
import { onFilesChanged } from '../lib/signalr';
import { showToast } from '../lib/toast';
import { beginAiBusy, endAiBusy } from '../lib/ai/busy';

// Форматы, которые markitdown умеет превращать в Markdown (для пункта «Трансформировать в Markdown»)
const MD_CONVERTIBLE = new Set(['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'ods', 'odp', 'epub', 'csv', 'rtf', 'html', 'htm', 'msg']);
const isMdConvertible = (name: string) => MD_CONVERTIBLE.has((name.split('.').pop() ?? '').toLowerCase());
import { copyMarkdown } from '../lib/selectionScope';
import { useGitState, ensureGit, gitInit, loadGitRemote } from '../lib/git';
import { GitChangesPanel, GitHistoryPanel } from './GitPanel';
import { useOnline } from '../hooks/useOnline';
import { EmptyState } from './EmptyState';
import { C, R, FONT, MODAL_W, TB, SHADOW } from '../lib/design';
import { useThemeMode, getEffectiveTheme } from '../lib/themeMode';
import { Modal, ModalActions, TextField, IconButton, Button, Menu, MenuItem } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

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
  // Открыть diff файла из панели «Изменения» (staged — дифф индекса);
  // не передан — фолбэк на onOpenFile
  onOpenGitDiff?: (path: string, staged: boolean) => void;
  onOpenCommit?: (sha: string) => void;
}

// Режим сайдбара «Файлы»: дерево / git-изменения / git-история
export type GitView = 'files' | 'changes' | 'history';

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
  gitView?: GitView;         // активный сегмент пилюли (файлы/изменения/история)
  onlyChanged?: boolean;     // фильтр «Только изменённые» в дереве
}
const _explorerStore = new Map<string, ExplorerState>();

/** Возвращает текущую папку для создания файлов в проводнике (используется из ChatPanel). */
export function getExplorerCreateInDir(projectId: string): string {
  return _explorerStore.get(projectId)?.createInDir ?? '';
}

/** Переключает git-сегмент пилюли извне (пилюля в «Знаниях»): применится при монтировании проводника. */
export function setExplorerGitView(projectId: string, view: GitView): void {
  const st = _explorerStore.get(projectId);
  if (st) { st.gitView = view; return; }
  _explorerStore.set(projectId, {
    dirCache: new Map(), expanded: new Set(), mobileDir: '', search: '',
    searchResults: null, createInDir: '', scrollTop: 0, gitView: view,
  });
}

const normPath = (p?: string | null) => (p ?? '').replace(/\\/g, '/');

// Режим сортировки дерева — глобальная настройка, живёт в localStorage
type FileSortMode = 'name' | 'date-desc' | 'date-asc';
const SORT_MODE_KEY = 'cc_files_sort';
const loadSortMode = (): FileSortMode => {
  const v = localStorage.getItem(SORT_MODE_KEY);
  if (v === 'date' || v === 'date-desc') return 'date-desc'; // 'date' — старое значение до появления направлений
  if (v === 'date-asc') return 'date-asc';
  return 'name';
};

// Папка заметок (vault проекта) в корне дерева — закреплена первой
const isNotesRoot = (e: FileEntry) => e.isDirectory && normPath(e.path) === 'notes';

// Путь внутри vault заметок — файловые операции с ним должны обновить раздел «Заметки»
const inNotesVault = (p?: string | null) => {
  const n = normPath(p);
  return n === 'notes' || n.startsWith('notes/');
};
const touchNotesStore = (...paths: (string | undefined | null)[]) => {
  if (paths.some(inNotesVault)) bumpNotes();
};

// Путь папки заметок → относительный путь внутри vault (для NewNoteDialog.folder)
const noteFolderOf = (p: string) => {
  const n = normPath(p);
  return n === 'notes' ? '' : n.slice('notes/'.length);
};

// Отображаемый путь: vault заметок показываем как «Заметки», а не «notes»
const notesDisplayPath = (p: string) => {
  const n = normPath(p);
  if (n === 'notes') return 'Заметки';
  return n.startsWith('notes/') ? 'Заметки/' + n.slice('notes/'.length) : p;
};

// В vault заметок допустимы только .md и изображения (вложения ![[img]])
const NOTES_OK_EXT = /\.(md|png|jpe?g|gif|svg|webp)$/i;
const allowedInNotes = (name: string) => NOTES_OK_EXT.test(name);

// Запрет перемещения по правилам vault заметок (dnd и диалог «Переместить»):
//  • папку заметок нельзя вынести из vault, обычную — внести в vault
//  • в vault-папку можно класть только .md/картинки (внешние файлы)
const notesMoveBlocked = (fromPath: string, fromIsDir: boolean, toPath: string): boolean => {
  const fromIn = inNotesVault(fromPath);
  const toIn = inNotesVault(toPath);
  if (fromIsDir) {
    if (fromIn && !toIn) return true;
    if (!fromIn && toIn) return true;
    return false;
  }
  const name = normPath(fromPath).split('/').pop() ?? '';
  return toIn && !fromIn && !allowedInNotes(name);
};

// Единая сортировка записей: «Заметки» первой, папки сверху, затем по имени
// (без учёта регистра) или по дате изменения (в выбранном направлении)
const sortEntries = (entries: FileEntry[], mode: FileSortMode): FileEntry[] =>
  [...entries].sort((a, b) => {
    if (isNotesRoot(a) !== isNotesRoot(b)) return isNotesRoot(a) ? -1 : 1;
    if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
    if (mode !== 'name') {
      const d = Date.parse(a.modified) - Date.parse(b.modified);
      if (d) return mode === 'date-asc' ? d : -d;
    }
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

// Светлый hex → полупрозрачный rgba (для тёмного тонированного фона плитки)
function hexToRgba(hex: string, a: number): string {
  const h = hex.replace('#', '');
  const r = parseInt(h.slice(0, 2), 16), g = parseInt(h.slice(2, 4), 16), b = parseInt(h.slice(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

function getExtMeta(name: string) {
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  const m = EXT_META[ext] ?? { bg: '#EFEAE0', fg: '#9A8F7E', label: ext.slice(0, 3) || '•' };
  // В тёмной теме светлый пастельный фон плитки заменяем на тёмный тонированный
  // того же оттенка (rgba от fg поверх тёмного фона), буква остаётся цветной
  if (getEffectiveTheme() === 'dark') return { ...m, bg: hexToRgba(m.fg, 0.18) };
  return m;
}

function FolderIcon() {
  return <Folder size={ICON_SIZE.md} strokeWidth={ICON_STROKE} color={C.accent} />;
}

// Иконка папки «Заметки» (vault проекта) — единая IconNotes в accent-цвете
function NotesFolderIcon() {
  return <span style={{ color: C.accent, display: 'flex' }}><IconNotes size={17} /></span>;
}

function CloudIcon({ variant }: { variant: 'direct' | 'inherited' | 'idle' }) {
  const color = variant === 'direct' ? C.accent : variant === 'inherited' ? C.accentSoft : C.textMuted;
  const fill = variant === 'direct' ? C.accent : variant === 'inherited' ? C.accentMuted : 'none';
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill={fill} stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z" />
    </svg>
  );
}

function SyncSpinner() {
  return (
    <span style={{ display: 'flex', width: 16, height: 16, alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
      <span style={{ width: 14, height: 14, borderRadius: '50%', border: `2.5px solid ${C.track}`, borderTopColor: C.accent, animation: 'spin 0.6s linear infinite' }} />
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
          <Folder size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />
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
            leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={onCreateFile}
          >
            Создать первый файл
          </Button>
        )}
      </div>

      <div style={{ width: '100%', display: 'flex', flexDirection: 'column', gap: 10, borderTop: `1px solid ${C.border}`, paddingTop: 16 }}>
        <FilesTip
          icon={
            <Monitor size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          }
          title="Файлы и структура проекта"
          text="Создавайте и загружайте файлы, организуйте их по папкам. Ассистент видит всю структуру проекта при работе над задачами."
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
            <Server size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          }
          title="Удалённый доступ к папке"
          text="Подключите как сетевой диск — все файлы будут доступны прямо в проводнике."
          extra={
            <div style={{ marginTop: 6, display: 'flex', alignItems: 'center', gap: 6, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '5px 8px' }}>
              <span style={{ flex: 1, fontFamily: FONT.mono, fontSize: 11, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {webdavUrl}
              </span>
              <button onClick={handleCopyWebdav} title="Скопировать URL" style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 0, display: 'flex', alignItems: 'center', color: copied ? C.successText : C.textMuted, flexShrink: 0 }}>
                {copied
                  ? <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  : <Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
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
  return <SquarePen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;
}

// Иконка корзины для удаления
function TrashIcon() {
  return <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;
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
  return <Paperclip size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}
function MI_Copy() {
  return <Copy size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
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
  return <SquarePen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}
function MI_Move() {
  return <ArrowRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}
function MI_Trash() {
  return <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}
// Иконка «Новая заметка» в контекстном меню: лист с плюсом
function MI_NotePlus() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h5"/>
      <path d="M14 3v5h5"/>
      <path d="M17 14v6M14 17h6"/>
    </svg>
  );
}

// Иконка папки с плюсом
function FolderPlusIcon() {
  return <FolderPlus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}

interface ContextMenuState {
  x: number;
  y: number;
  entry: FileEntry;
}

export function FileExplorer({ project, onOpenFile, activeFilePath, isMobile = false, alwaysShowIcons = false, onAddToKnowledge, onAddFolderToKnowledge, onRemoveFromKnowledge, indexedFileNames, indexingFiles, indexingFolders, onAttachToChat, onOpenKnowledge, onOpenGitDiff, onOpenCommit }: Props) {
  const online = useOnline();
  useThemeMode();  // перерисовка дерева при смене темы (плитки типов файлов)
  const marks = useSyncMarks(project.id);
  const initial = _explorerStore.get(project.id);
  const [dirCache, setDirCache] = useState<Map<string, FileEntry[]>>(() => initial?.dirCache ?? new Map());
  const [expanded, setExpanded] = useState<Set<string>>(() => initial?.expanded ?? new Set());
  const [mobileDir, setMobileDir] = useState<string>(() => initial?.mobileDir ?? '');
  const [loadingDirs, setLoadingDirs] = useState<Set<string>>(new Set());
  const inFlight = useRef(new Set<string>());
  const dirCacheRef = useRef(dirCache);
  dirCacheRef.current = dirCache;

  const [sortMode, setSortMode] = useState<FileSortMode>(loadSortMode);
  const [showSortMenu, setShowSortMenu] = useState(false);
  // Активированный поиск занимает весь сайдбар (сортировка и пилюля схлопываются);
  // остаётся развёрнутым, пока в поле есть текст (searchExpanded считается ниже по search)
  const [searchFocused, setSearchFocused] = useState(false);
  const changeSortMode = (m: FileSortMode) => {
    setSortMode(m);
    localStorage.setItem(SORT_MODE_KEY, m);
    setShowSortMenu(false);
  };

  // === Git: режим сайдбара + статус репозитория + фильтр «Только изменённые» ===
  const [gitView, setGitView] = useState<GitView>(() => initial?.gitView ?? 'files');
  const [onlyChanged, setOnlyChanged] = useState<boolean>(() => initial?.onlyChanged ?? false);
  const gitState = useGitState(project.id);
  useEffect(() => { ensureGit(project.id); }, [project.id]);
  const isRepo = gitState.status?.isRepo ?? false;
  // Документный режим (авто-ведение истории): работа с индексом скрыта — сегмент
  // «Изменения» недоступен, вся жизнь в «Истории» (плашка + «Сохранить сейчас»)
  useEffect(() => { if (isRepo) void loadGitRemote(project.id); }, [project.id, isRepo]);
  const docMode = isRepo && gitState.remote?.autoCommit === true;
  // Не репо — git-сегменты недоступны; в документном режиме «Изменения» → «История»
  const view: GitView = !isRepo ? 'files' : (docMode && gitView === 'changes' ? 'history' : gitView);

  // Подключение git к проекту без репозитория (git init + remote на Forgejo)
  const [gitInitOpen, setGitInitOpen] = useState(false);
  const [gitInitBusy, setGitInitBusy] = useState(false);
  const [gitInitError, setGitInitError] = useState<string | null>(null);
  const handleGitInit = async () => {
    if (gitInitBusy) return;
    setGitInitBusy(true);
    setGitInitError(null);
    const r = await gitInit(project.id);
    setGitInitBusy(false);
    if (r.ok) setGitInitOpen(false);   // статус в сторе уже свежий → появятся сегменты пилюли
    else setGitInitError(r.error ?? 'Не удалось создать git-репозиторий');
  };

  // Наборы изменённых путей из git-статуса: файлы + папки на пути к ним (для фильтра дерева)
  const changedSets = useMemo(() => {
    const s = gitState.status;
    if (!s?.isRepo) return null;
    const files = new Set<string>();
    const dirs = new Set<string>();
    for (const list of [s.staged, s.unstaged, s.untracked]) {
      for (const c of list) {
        const p = normPath(c.path).replace(/\/+$/, '');
        files.add(p);
        let i = p.lastIndexOf('/');
        let d = i < 0 ? '' : p.slice(0, i);
        while (d) {
          dirs.add(d);
          const j = d.lastIndexOf('/');
          d = j < 0 ? '' : d.slice(0, j);
        }
      }
    }
    return { files, dirs };
  }, [gitState.status]);

  // Фильтр активен только в git-репо: изменённый файл / папка на пути к изменению.
  // Пути untracked-папок git отдаёт как «dir/» — такие покрывает префикс-проверка.
  const passesChangedFilter = useCallback((entry: FileEntry): boolean => {
    if (entry.isModified || entry.isNew) return true;
    if (!changedSets) return false;
    const p = normPath(entry.path);
    if (entry.isDirectory) {
      if (changedSets.dirs.has(p)) return true;
      for (const f of changedSets.files) if (f.startsWith(p + '/')) return true;
      return false;
    }
    return changedSets.files.has(p);
  }, [changedSets]);

  const [search, setSearch] = useState(() => initial?.search ?? '');
  const searchExpanded = searchFocused || search.trim().length > 0;
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
  // Диалог «Новая заметка» из раздела файлов (folder — путь внутри vault)
  const [noteDialog, setNoteDialog] = useState<{ folder: string } | null>(null);

  // === Drag & drop state ===
  const [dragPath, setDragPath] = useState<string | null>(null);
  const [dragIsDir, setDragIsDir] = useState(false);   // тащим папку или файл (для правил vault)
  const [dropTarget, setDropTarget] = useState<string | null>(null);

  // === Long press для мобилы ===
  const longPressTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [pressingPath, setPressingPath] = useState<string | null>(null);

  // === Flash-highlight для новосозданного файла ===
  const [newlyCreatedPath, setNewlyCreatedPath] = useState<string | null>(null);

  // === Move modal state ===
  const [showMoveModal, setShowMoveModal] = useState(false);
  const [movingEntry, setMovingEntry] = useState<FileEntry | null>(null);

  // === Трансформация в Markdown (markitdown) — выбор папки назначения ===
  const [mdEntry, setMdEntry] = useState<FileEntry | null>(null);
  const [mdEnhance, setMdEnhance] = useState(false);
  const doTransformMd = async (entry: FileEntry, targetDir: string | null) => {
    const enhance = mdEnhance;
    setMdEntry(null);
    beginAiBusy();
    try {
      const r = await api.files.toMarkdown(project.id, entry.path, targetDir, enhance);
      showToast('Трансформировано в Markdown', r.savedPath);
      const [dir] = splitPath(r.savedPath);
      await invalidateDir(dir);
    } catch {
      showToast('Ошибка', 'Не удалось трансформировать файл', 'info');
    } finally {
      endAiBusy();
    }
  };

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
      setGitView(st.gitView ?? 'files');
      setOnlyChanged(st.onlyChanged ?? false);
      loadDir('');
      if (st.mobileDir) loadDir(st.mobileDir);
    } else {
      setDirCache(new Map());
      setExpanded(new Set());
      setMobileDir('');
      setCreateInDir('');
      setSearch('');
      setSearchResults(null);
      setGitView('files');
      setOnlyChanged(false);
      loadDir('');
    }
  }, [project.id, loadDir]);

  useEffect(() => {
    _explorerStore.set(project.id, {
      dirCache, expanded, mobileDir, search, searchResults, createInDir, gitView, onlyChanged,
      scrollTop: scrollRef.current?.scrollTop ?? _explorerStore.get(project.id)?.scrollTop ?? 0,
    });
  }, [project.id, dirCache, expanded, mobileDir, search, searchResults, createInDir, gitView, onlyChanged]);

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
    touchNotesStore(path);
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
    touchNotesStore(path);
    setShowCreateDir(false);
    setNewDirName('');
    await invalidateDir(createInDir);
    if (createInDir) setExpanded(prev => new Set(prev).add(createInDir));
  };

  const handleUploadFiles = async (fileList: FileList | null) => {
    if (!fileList || fileList.length === 0) return;
    const dir = isMobile ? mobileDir : createInDir;
    // В vault заметок грузим только .md/картинки — прочее отсеиваем с сообщением
    let files = Array.from(fileList);
    if (inNotesVault(dir)) {
      const rejected = files.filter(f => !allowedInNotes(f.name));
      files = files.filter(f => allowedInNotes(f.name));
      if (rejected.length) {
        setUploadError(`В заметки можно загружать только .md и изображения: ${rejected.map(f => f.name).join(', ')}`);
        if (files.length === 0) { if (uploadInputRef.current) uploadInputRef.current.value = ''; return; }
      }
    }
    setUploading(true);
    if (!inNotesVault(dir)) setUploadError(null);
    try {
      await Promise.all(files.map(f => api.files.upload(project.id, f, dir)));
      touchNotesStore(dir);
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
    // Переименование заметки notes/*.md → через notes-API, чтобы обновились
    // входящие [[ссылки]] (файловый rename их не трогает). Смена расширения на
    // не-.md / только регистра / отсутствие заметки в сторе → обычный rename.
    const isNotesMd = inNotesVault(renamingPath) && /\.md$/i.test(oldName);
    const newIsMd = /\.md$/i.test(newName);
    if (isNotesMd && newIsMd && newName.toLowerCase() !== oldName.toLowerCase()) {
      const rel = normPath(renamingPath).slice('notes/'.length);
      // Стор заметок в разделе файлов может быть не загружен → фолбэк на свежий список
      let note = getNotesSnapshot().find(n => n.source === project.id && normPath(n.path) === rel);
      if (!note) {
        try { note = (await api.notes.list()).find(n => n.source === project.id && normPath(n.path) === rel); } catch { /* оффлайн — обычный rename ниже */ }
      }
      if (note) {
        try {
          await api.notes.update(note.id, { title: newName.replace(/\.md$/i, '') });
          bumpNotes();
          setRenamingPath(null);
          setRenameValue('');
          await invalidateDir(parentDir);
          return;
        } catch { /* коллизия имени и т.п. — оставляем режим правки */ return; }
      }
    }
    try {
      await api.files.rename(project.id, renamingPath, newPath);
      touchNotesStore(renamingPath, newPath);
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

  // «Копировать Markdown» из контекстного меню — содержимое файла в буфер без открытия
  const copyMdFromTree = async (path: string) => {
    try {
      const r = await api.files.getContent(project.id, path);
      if (r.isBinary || r.content == null) { showToast('Не скопировано', 'Файл не текстовый'); return; }
      if (await copyMarkdown(r.content)) showToast('Скопировано', path.split('/').pop() ?? path);
    } catch {
      showToast('Не скопировано', 'Не удалось прочитать файл');
    }
  };

  // === Delete handler ===
  const handleDelete = useCallback(async (entry: FileEntry) => {
    const [parentDir] = splitPath(entry.path);
    try {
      await api.files.delete(project.id, entry.path);
      touchNotesStore(entry.path);
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
    // правила vault заметок (тип файла / папки заметок)
    if (notesMoveBlocked(from, dragIsDir, to)) return true;
    return false;
  }, [dragIsDir]);

  const handleDragStart = useCallback((e: React.DragEvent, entry: FileEntry) => {
    setDragPath(entry.path);
    setDragIsDir(entry.isDirectory);
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
      touchNotesStore(dp, targetPath);
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
    setDragIsDir(false);
    setDropTarget(null);
  }, []);

  // === Move handler ===
  const handleMove = useCallback(async (entry: FileEntry, targetDir: string) => {
    const targetPath = targetDir ? `${normPath(targetDir)}/${entry.name}` : entry.name;
    const [sourceParent] = splitPath(entry.path);
    try {
      await api.files.rename(project.id, entry.path, targetPath);
      touchNotesStore(entry.path, targetPath);
      setShowMoveModal(false);
      setMovingEntry(null);
      await Promise.all([invalidateDir(sourceParent), invalidateDir(targetDir)]);
    } catch {
      // тихо игнорируем
    }
  }, [project.id, invalidateDir]);

  // Все загруженные папки (для диалога перемещения); notes показываем как «Заметки»
  const allDirs = useMemo(() => {
    const label = (p: string) => {
      const n = normPath(p);
      if (n === 'notes') return 'Заметки';
      return n.startsWith('notes/') ? 'Заметки/' + p.slice('notes/'.length) : p;
    };
    const dirs: Array<{ path: string; label: string }> = [{ path: '', label: '/ (корень проекта)' }];
    for (const [, entries] of dirCache) {
      for (const e of entries) {
        if (e.isDirectory) dirs.push({ path: e.path, label: label(e.path) });
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
    const filterChanged = onlyChanged && isRepo;
    const walk = (path: string, depth: number): TreeNode[] => {
      const entries = sortEntries(dirCache.get(path) ?? [], sortMode);
      const result: TreeNode[] = [];
      for (const entry of entries) {
        if (filterChanged && !passesChangedFilter(entry)) continue;
        result.push({ entry, depth });
        if (entry.isDirectory && expanded.has(entry.path)) {
          result.push(...walk(entry.path, depth + 1));
        }
      }
      return result;
    };
    return walk('', 0);
  }, [dirCache, expanded, sortMode, onlyChanged, isRepo, passesChangedFilter]);

  const rootLoading = !dirCache.has('') && loadingDirs.has('');

  const mobileEntries = useMemo((): FileEntry[] => {
    const entries = dirCache.get(mobileDir);
    if (!entries) return [];
    const sorted = sortEntries(entries, sortMode);
    return onlyChanged && isRepo ? sorted.filter(passesChangedFilter) : sorted;
  }, [dirCache, mobileDir, sortMode, onlyChanged, isRepo, passesChangedFilter]);

  const breadcrumbs = useMemo(() => {
    const crumbs: { label: string; path: string }[] = [{ label: 'Файлы', path: '' }];
    if (mobileDir) {
      const parts = mobileDir.split('/').filter(Boolean);
      let acc = '';
      for (const part of parts) {
        acc = acc ? `${acc}/${part}` : part;
        // Корень vault заметок в UI называется «Заметки», а не «notes»
        const label = normPath(acc) === 'notes' ? 'Заметки' : part;
        crumbs.push({ label, path: acc });
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
    // «Заметки» (vault проекта) — выделенная строка с русским именем и своей иконкой
    const notesRoot = isNotesRoot(entry);
    const isActive = !entry.isDirectory && activeNorm !== '' && normPath(entry.path) === activeNorm;
    const sstate = computeSyncState(marks, entry.path);
    const pending = !entry.isDirectory && !!sstate && !isDownloaded(project.id, entry.path);
    const folderSyncing = entry.isDirectory && isSyncing(project.id, entry.path);
    const isDropTgt = dropTarget === entry.path;
    const isDragging = dragPath === entry.path;
    const isRenaming = renamingPath === entry.path;

    const rowBg = isDropTgt
      ? C.accentMuted
      : isActive ? C.accentMuted
      : hoveredPath === entry.path ? C.bgSelected
      : normPath(entry.path) === newlyCreatedPath ? C.accentLight
      : (sstate || folderSyncing) ? C.accentLight
      : notesRoot ? C.accentLight
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
        draggable={!isMobile && !alwaysShowIcons && !isRenaming && !notesRoot}
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
            : isActive ? `inset 2px 0 0 ${C.accent}`
            : 'none',
          transition: 'background 0.1s, box-shadow 0.1s, opacity 0.1s, transform 0.1s',
        }}
      >
        {/* toggle-стрелка дерева — только десктоп/планшет */}
        {!mobileNav && (
          <span style={{ width: 12, flexShrink: 0, textAlign: 'center', userSelect: 'none', color: C.textMuted, fontSize: 9, lineHeight: 1 }}>
            {entry.isDirectory ? (isLoading ? '·' : (isExpanded ? '▾' : '▸')) : ''}
          </span>
        )}
        {entry.isDirectory ? (
          <span style={{ flexShrink: 0, display: 'flex', color: C.accent }}>
            {notesRoot ? <NotesFolderIcon /> : <FolderIcon />}
          </span>
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
                color: C.textHeading,
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
            <span title={notesRoot ? 'Заметки (папка notes)' : entry.name} style={{
              fontFamily: notesRoot ? undefined : "'JetBrains Mono', monospace",
              fontSize: 13,
              fontWeight: entry.isDirectory ? 700 : 500,
              color: notesRoot ? C.accent
                : (!entry.isDirectory && indexedFileNames?.has(entry.path))
                ? C.successText
                : C.textHeading,
              ...(isMobile
                ? { whiteSpace: 'normal', wordBreak: 'break-all', lineHeight: 1.35 }
                : { whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }),
            }}>{notesRoot ? 'Заметки' : entry.name}</span>
          )}
          {parentDir && (
            <span title={normPath(entry.path)} style={{
              fontFamily: "'JetBrains Mono', monospace", fontSize: 10.5, color: C.textMuted,
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
          <span style={{ fontSize: 9, fontWeight: 700, color: C.accent, background: C.accentLight, width: 16, height: 16, borderRadius: 4, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>M</span>
        )}
        {entry.isNew && (
          <span style={{ fontSize: 9, fontWeight: 700, color: C.successText, background: C.successBg, width: 16, height: 16, borderRadius: 4, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>+</span>
        )}
        {/* Hover-иконки: переименовать + удалить — только десктоп при hover.
            Корень «Заметки» (vault) не переименовываем/не удаляем. */}
        {online && !isRenaming && !isMobile && hoveredPath === entry.path && !isNotesRoot(entry) && (
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
              color={C.danger}
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
              <Paperclip size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          ) : null
        )}
        {/* Иконка знаний: спиннер при индексации; при hover — добавить или удалить.
            Заметки (notes/*.md) в файловые «Знания» не индексируем — свой семантический индекс. */}
        {!entry.isDirectory && !inNotesVault(entry.path) && (
          indexingFiles?.has(entry.path) ? (
            <span style={{ padding: 2, display: 'flex', alignItems: 'center', flexShrink: 0, color: C.successText }}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <style>{`@keyframes kb-spin{to{transform:rotate(360deg)}} .kb-spin{transform-origin:center;animation:kb-spin 0.8s linear infinite}`}</style>
                <circle className="kb-spin" cx="12" cy="12" r="9" strokeDasharray="40 20" />
              </svg>
            </span>
          ) : indexedFileNames?.has(entry.path) ? (
            !isMobile && !alwaysShowIcons && hoveredPath === entry.path && onRemoveFromKnowledge ? (
              <IconButton
                size="xs"
                tone="danger"
                color={C.danger}
                onClick={e => { e.stopPropagation(); onRemoveFromKnowledge(entry.path); }}
                title="Удалить из знаний"
              >
                <BookMinusIcon />
              </IconButton>
            ) : (
              <span style={{ padding: 2, display: 'flex', alignItems: 'center', flexShrink: 0, color: C.successText }}>
                <BookOpen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </span>
            )
          ) : (
            // Не в знаниях — показать «добавить» при hover (только десктоп)
            onAddToKnowledge && isKnowledgeIndexable(entry.name) && !isMobile && !alwaysShowIcons && hoveredPath === entry.path ? (
              <IconButton
                size="xs"
                color={C.successText}
                onClick={e => { e.stopPropagation(); onAddToKnowledge(entry.path); }}
                title="Добавить в базу знаний"
              >
                <BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
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
        {mobileNav && entry.isDirectory && <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />}
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
        color: danger ? C.danger : C.textPrimary,
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

  // Пилюля режимов сайдбара: Файлы / [Изменения / История — только git-репо] / Знания
  const pillBtn = (key: string, title: string, active: boolean, onClick: (() => void) | undefined, icon: ReactNode) => (
    <button
      key={key}
      onClick={onClick}
      title={title}
      style={{
        width: 28, height: 28, border: 'none', borderRadius: 6,
        cursor: active ? 'default' : 'pointer',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        background: active ? C.bgMain : 'transparent',
        color: active ? C.accent : C.textMuted,
        boxShadow: active ? TB.pillThumbShadow : 'none',
      }}
    >
      {icon}
    </button>
  );
  const showPill = !!onOpenKnowledge || isRepo;
  const pill = showPill ? (
    <div style={{ display: 'flex', alignItems: 'center', gap: 2, background: TB.pillTrack, borderRadius: 8, padding: 2, flexShrink: 0 }}>
      {pillBtn('files', 'Файлы', view === 'files', () => setGitView('files'), <Folder size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />)}
      {isRepo && !docMode && pillBtn('changes', 'Изменения', view === 'changes', () => setGitView('changes'), <GitBranch size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />)}
      {isRepo && pillBtn('history', 'История', view === 'history', () => setGitView('history'), <History size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />)}
      {onOpenKnowledge && pillBtn('knowledge', 'Знания', false, onOpenKnowledge, <BookOpen size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />)}
    </div>
  ) : null;

  // === Git-режимы: содержимое сайдбара вместо дерева ===
  if (view !== 'files') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
        <div style={{ padding: '4px 12px 10px', display: 'flex', alignItems: 'center', gap: 6 }}>
          <div style={{ flex: 1, minWidth: 0, fontSize: 13.5, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>
            {view === 'changes' ? 'Изменения' : 'История'}
          </div>
          {pill}
        </div>
        {view === 'changes' ? (
          <GitChangesPanel
            project={project}
            onOpenDiff={(p, staged) => onOpenGitDiff ? onOpenGitDiff(p, staged) : onOpenFile(p)}
            onOpenFile={onOpenFile}
          />
        ) : (
          <GitHistoryPanel project={project} onOpenCommit={onOpenCommit} docMode={docMode} />
        )}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Search */}
      <div style={{ padding: '4px 12px 10px' }}>
        {/* position:relative на всей строке: меню сортировки позиционируется от её левого
            края (200px гарантированно внутри сайдбара), а не от кнопки 28px */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, position: 'relative' }}>
          <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, padding: '0 11px', height: 36 }}>
            <span style={{ color: C.textMuted, marginRight: 8, display: 'flex', flexShrink: 0 }}>
              <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </span>
            <input
              placeholder="Поиск…"
              value={search}
              onChange={e => handleSearch(e.target.value)}
              onFocus={() => setSearchFocused(true)}
              onBlur={() => setSearchFocused(false)}
              style={{ flex: 1, border: 'none', background: 'none', fontSize: 13, fontFamily: FONT.mono, color: C.textHeading, outline: 'none' }}
            />
            {search && (
              <button onClick={() => handleSearch('')} style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.textMuted, padding: 0, display: 'flex', alignItems: 'center' }}><X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></button>
            )}
          </div>
          {/* Активный поиск разворачивается на весь сайдбар: сортировка и пилюля схлопываются */}
          <div style={{
            display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
            maxWidth: searchExpanded ? 0 : 220,
            opacity: searchExpanded ? 0 : 1,
            overflow: 'hidden',
            transition: 'max-width 0.18s ease, opacity 0.15s ease',
            pointerEvents: searchExpanded ? 'none' : 'auto',
          }}>
          {/* Сортировка дерева + фильтр «Только изменённые» */}
          <span style={{ position: 'relative', flexShrink: 0, display: 'inline-flex' }}>
            <IconButton
              size="md"
              active={showSortMenu}
              color={onlyChanged && isRepo ? C.accent : undefined}
              style={onlyChanged && isRepo && !showSortMenu ? { background: C.accentLight } : undefined}
              onClick={() => setShowSortMenu(v => !v)}
              title={
                (sortMode === 'name' ? 'Сортировка: по имени'
                : sortMode === 'date-desc' ? 'Сортировка: сначала новые'
                : 'Сортировка: сначала старые')
                + (onlyChanged && isRepo ? ' · Только изменённые' : '')
              }
            >
              <SlidersHorizontal size={15} strokeWidth={ICON_STROKE} />
            </IconButton>
            {/* Точка-индикатор активного фильтра */}
            {onlyChanged && isRepo && (
              <span style={{ position: 'absolute', top: 3, right: 3, width: 6, height: 6, borderRadius: '50%', background: C.accent, pointerEvents: 'none' }} />
            )}
          </span>
          {pill}
          </div>
          {showSortMenu && (
              // align="left": кнопка у левого края сайдбара — правое выравнивание уводило меню за экран
              <Menu onClose={() => setShowSortMenu(false)} align="left" top={34} minWidth={200}>
                <MenuItem
                  icon={sortMode === 'name' ? <Check size={15} strokeWidth={2} /> : <></>}
                  label="По имени"
                  onClick={() => changeSortMode('name')}
                />
                <MenuItem
                  icon={sortMode === 'date-desc' ? <Check size={15} strokeWidth={2} /> : <></>}
                  label="Сначала новые"
                  onClick={() => changeSortMode('date-desc')}
                />
                <MenuItem
                  icon={sortMode === 'date-asc' ? <Check size={15} strokeWidth={2} /> : <></>}
                  label="Сначала старые"
                  onClick={() => changeSortMode('date-asc')}
                />
                {isRepo ? (
                  <>
                    <div style={{ height: 1, background: C.border, margin: '4px 6px' }} />
                    <MenuItem
                      icon={onlyChanged ? <Check size={15} strokeWidth={2} /> : <GitBranch size={15} strokeWidth={ICON_STROKE} />}
                      label="Только изменённые"
                      onClick={() => { setOnlyChanged(v => !v); setShowSortMenu(false); }}
                    />
                  </>
                ) : gitState.statusLoaded && online && (
                  <>
                    <div style={{ height: 1, background: C.border, margin: '4px 6px' }} />
                    <MenuItem
                      icon={<GitBranch size={15} strokeWidth={ICON_STROKE} />}
                      label="Подключить git…"
                      onClick={() => { setShowSortMenu(false); setGitInitOpen(true); }}
                    />
                  </>
                )}
              </Menu>
            )}
        </div>

        {online && (
          <div style={{ marginTop: 8, display: 'flex', gap: 6 }}>
            {/* Новый файл — в контексте vault заметок превращается в «Новая заметка» */}
            {inNotesVault(isMobile ? mobileDir : createInDir) ? (
              <Button
                variant="dashed"
                size="md"
                leftIcon={<IconNotes size={15} />}
                onClick={() => setNoteDialog({ folder: noteFolderOf(isMobile ? mobileDir : createInDir) })}
                style={{ flex: 1 }}
              >
                Новая заметка
              </Button>
            ) : (
              <Button
                variant="dashed"
                size="md"
                leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                onClick={() => {
                  if (isMobile) setCreateInDir(mobileDir);
                  setShowCreateFile(true);
                }}
                style={{ flex: 1 }}
              >
                Новый файл
              </Button>
            )}
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
                <span style={{ width: 14, height: 14, borderRadius: '50%', border: `2px solid ${C.track}`, borderTopColor: C.accent, animation: 'spin 0.6s linear infinite', display: 'inline-block' }} />
              ) : (
                <Upload size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              )}
            </label>
          </div>
        )}
        {uploadError && (
          <div style={{ marginTop: 6, fontSize: 12, color: C.dangerText, fontFamily: FONT.sans, paddingLeft: 2 }}>{uploadError}</div>
        )}
        {/* Хинт целевой папки — только десктоп */}
        {online && !isMobile && (
          <div style={{ marginTop: 5, fontSize: 11.5, color: C.textMuted, fontFamily: FONT.mono, paddingLeft: 2, display: 'flex', alignItems: 'center', gap: 4 }}>
            <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            {createInDir ? <span title={createInDir}>{notesDisplayPath(createInDir)}</span> : <span style={{ fontStyle: 'italic' }}>корень проекта</span>}
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
              icon={<Search size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
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
            onlyChanged && isRepo ? (
              <EmptyState
                icon={<GitBranch size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
                title="Нет изменённых файлов"
                subtitle="Активен фильтр «Только изменённые»"
              />
            ) : mobileDir === '' ? <FilesRootEmptyState onCreateFile={online ? () => { setCreateInDir(mobileDir); setShowCreateFile(true); } : undefined} /> : (
              <EmptyState
                icon={<Folder size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
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
          onlyChanged && isRepo ? (
            <EmptyState
              icon={<GitBranch size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
              title="Нет изменённых файлов"
              subtitle="Активен фильтр «Только изменённые»"
            />
          ) : (
            <FilesRootEmptyState onCreateFile={online ? () => { setCreateInDir(''); setShowCreateFile(true); } : undefined} />
          )
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
                color: C.textHeading,
                background: C.bgWhite,
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
      {/* Подключение git к проекту (git init + при настроенном Forgejo удалённый репозиторий) */}
      {gitInitOpen && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => { if (!gitInitBusy) { setGitInitOpen(false); setGitInitError(null); } }}
          title="Подключить git"
          footer={
            <ModalActions
              confirmLabel={gitInitBusy ? 'Подключаю…' : 'Подключить'}
              confirmDisabled={gitInitBusy}
              onConfirm={handleGitInit}
              onCancel={() => { setGitInitOpen(false); setGitInitError(null); }}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            В папке проекта будет создан git-репозиторий — появятся панели «Изменения» и «История».
            Если настроен сервер Forgejo, также будет создан удалённый репозиторий.
          </div>
          {gitInitError && (
            <div style={{ marginTop: 10, fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans, lineHeight: 1.45 }}>
              {gitInitError}
            </div>
          )}
        </Modal>
      )}

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
              {deleteConfirm.isDirectory && <span style={{ color: C.danger }}> со всем содержимым</span>}
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
          // правила vault заметок: тип файла / папки заметок ↔ обычные
          if (notesMoveBlocked(movingEntry.path, movingEntry.isDirectory, d.path)) return false;
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

      {/* === Трансформация в Markdown: выбор папки назначения === */}
      {mdEntry && (() => {
        const [mdParent] = splitPath(mdEntry.path);
        const folderBtn: React.CSSProperties = {
          display: 'flex', alignItems: 'center', gap: 8, padding: '9px 8px', background: 'none',
          border: 'none', cursor: 'pointer', borderRadius: R.md, textAlign: 'left',
          fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary, width: '100%',
        };
        return (
          <Modal
            width={MODAL_W.form}
            onClose={() => setMdEntry(null)}
            title={`В Markdown: ${mdEntry.name}`}
            subtitle="Куда сохранить .md — рядом с файлом или в другую папку"
          >
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 4px 10px', cursor: 'pointer', fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary }}>
              <input type="checkbox" checked={mdEnhance} onChange={e => setMdEnhance(e.target.checked)} />
              Восстановить разметку (ИИ): заголовки, списки — полезно для PDF
            </label>
            <div style={{ maxHeight: 340, overflow: 'auto', display: 'flex', flexDirection: 'column', gap: 2, margin: '0 -4px' }}>
              <button
                onPointerDown={e => { e.stopPropagation(); void doTransformMd(mdEntry, null); }}
                style={{ ...folderBtn, background: C.bgInset, fontFamily: FONT.sans, fontWeight: 600, fontSize: 13 }}
              >
                <span style={{ flexShrink: 0, display: 'flex' }}><FolderIcon /></span>
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  Рядом с файлом{mdParent ? ` · ${mdParent}` : ''}
                </span>
              </button>
              {allDirs.map(d => (
                <button
                  key={d.path}
                  onPointerDown={e => { e.stopPropagation(); void doTransformMd(mdEntry, d.path); }}
                  style={folderBtn}
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
                style={{ position: 'fixed', inset: 0, background: C.overlay, zIndex: 1000 }}
              />
              <div style={{
                position: 'fixed', bottom: 0, left: 0, right: 0,
                background: C.bgPanel,
                borderRadius: '16px 16px 0 0',
                paddingBottom: 24,
                zIndex: 1001,
                boxShadow: SHADOW.sheet,
              }}>
                {/* Ручка */}
                <div style={{ display: 'flex', justifyContent: 'center', padding: '10px 0 4px' }}>
                  <div style={{ width: 36, height: 4, borderRadius: 2, background: C.border }} />
                </div>
                <div style={{ padding: '4px 20px 12px', fontFamily: FONT.mono, fontSize: 13, fontWeight: 700, color: C.textPrimary, borderBottom: `1px solid ${C.border}` }}>
                  {entry.name}
                </div>
                {entry.isDirectory && inNotesVault(entry.path) && menuItem(<MI_NotePlus />, 'Новая заметка', () => { setContextMenu(null); setNoteDialog({ folder: noteFolderOf(entry.path) }); })}
                {!entry.isDirectory && onAttachToChat && menuItem(<MI_Attach />, 'Прикрепить к чату', () => { setContextMenu(null); onAttachToChat(entry.path); })}
                {!entry.isDirectory && /\.(md|mdx)$/i.test(entry.name) && menuItem(<MI_Copy />, 'Копировать Markdown', () => { setContextMenu(null); void copyMdFromTree(entry.path); })}
                {!entry.isDirectory && online && isMdConvertible(entry.name) && menuItem(<MI_Copy />, 'Трансформировать в Markdown…', () => { setContextMenu(null); setMdEntry(entry); })}
                {!entry.isDirectory && !inNotesVault(entry.path) && onAddToKnowledge && !indexedFileNames?.has(entry.path) && isKnowledgeIndexable(entry.name) && menuItem(<MI_BookPlus />, 'Добавить в знания', () => { setContextMenu(null); onAddToKnowledge(entry.path); })}
                {!entry.isDirectory && onRemoveFromKnowledge && indexedFileNames?.has(entry.path) && menuItem(<MI_BookMinus />, 'Удалить из знаний', () => { setContextMenu(null); onRemoveFromKnowledge(entry.path); })}
                {entry.isDirectory && !inNotesVault(entry.path) && onAddFolderToKnowledge && !indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Добавить папку в знания', () => { setContextMenu(null); onAddFolderToKnowledge(entry.path); })}
                {entry.isDirectory && !inNotesVault(entry.path) && indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Индексирование…', () => {})}
                {canToggleOffline && menuItem(<MI_Cloud />, offlineLabel, doToggleOffline)}
                {/* «Заметки» (vault) не переименовываем/не удаляем — сломается база знаний */}
                {!isNotesRoot(entry) && <>
                  <div style={{ height: 1, background: C.border, margin: '4px 20px' }} />
                  {online && menuItem(<MI_Rename />, 'Переименовать', () => startRename(entry))}
                  {online && menuItem(<MI_Move />, 'Переместить в...', () => { setContextMenu(null); setMovingEntry(entry); setShowMoveModal(true); })}
                  {online && <div style={{ height: 1, background: C.border, margin: '4px 20px' }} />}
                  {online && menuItem(<MI_Trash />, 'Удалить', () => { setContextMenu(null); setDeleteConfirm(entry); }, true)}
                </>}
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
              boxShadow: SHADOW.dropdown,
              padding: 4,
              minWidth: 190,
            }}
          >
            {entry.isDirectory && inNotesVault(entry.path) && menuItem(<MI_NotePlus />, 'Новая заметка', () => { setContextMenu(null); setNoteDialog({ folder: noteFolderOf(entry.path) }); })}
            {!entry.isDirectory && onAttachToChat && menuItem(<MI_Attach />, 'Прикрепить к чату', () => { setContextMenu(null); onAttachToChat(entry.path); })}
            {!entry.isDirectory && /\.(md|mdx)$/i.test(entry.name) && menuItem(<MI_Copy />, 'Копировать Markdown', () => { setContextMenu(null); void copyMdFromTree(entry.path); })}
                {!entry.isDirectory && online && isMdConvertible(entry.name) && menuItem(<MI_Copy />, 'Трансформировать в Markdown…', () => { setContextMenu(null); setMdEntry(entry); })}
            {!entry.isDirectory && !inNotesVault(entry.path) && onAddToKnowledge && !indexedFileNames?.has(entry.path) && isKnowledgeIndexable(entry.name) && menuItem(<MI_BookPlus />, 'Добавить в знания', () => { setContextMenu(null); onAddToKnowledge(entry.path); })}
            {!entry.isDirectory && onRemoveFromKnowledge && indexedFileNames?.has(entry.path) && menuItem(<MI_BookMinus />, 'Удалить из знаний', () => { setContextMenu(null); onRemoveFromKnowledge(entry.path); })}
            {entry.isDirectory && !inNotesVault(entry.path) && onAddFolderToKnowledge && !indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Добавить папку в знания', () => { setContextMenu(null); onAddFolderToKnowledge(entry.path); })}
            {entry.isDirectory && !inNotesVault(entry.path) && indexingFolders?.has(entry.path) && menuItem(<MI_BookPlus />, 'Индексирование…', () => {})}
            {canToggleOffline && menuItem(<MI_Cloud />, sstate === 'direct' ? 'Убрать из офлайна' : 'Сохранить офлайн', doToggleOffline)}
            {/* «Заметки» (vault) не переименовываем/не удаляем — сломается база знаний */}
            {!isNotesRoot(entry) && <>
              <div style={{ height: 1, background: C.border, margin: '4px 0' }} />
              {online && menuItem(<MI_Rename />, 'Переименовать', () => startRename(entry))}
              {online && menuItem(<MI_Move />, 'Переместить в...', () => { setContextMenu(null); setMovingEntry(entry); setShowMoveModal(true); })}
              <div style={{ height: 1, background: C.border, margin: '4px 0' }} />
              {online && menuItem(<MI_Trash />, 'Удалить', () => { setContextMenu(null); setDeleteConfirm(entry); }, true)}
            </>}
          </div>
        );
      })()}

      {/* Диалог «Новая заметка» из раздела файлов (папка vault → source=проект) */}
      {noteDialog && (
        <NewNoteDialog
          defaults={{ source: project.id, folder: noteDialog.folder }}
          onClose={() => setNoteDialog(null)}
          onCreated={() => {
            setNoteDialog(null);
            bumpNotes();
            const dir = noteDialog.folder ? `notes/${noteDialog.folder}` : 'notes';
            void invalidateDir(dir);
            setExpanded(prev => new Set(prev).add(dir));
          }}
        />
      )}
    </div>
  );
}
