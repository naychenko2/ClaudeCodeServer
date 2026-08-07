import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AlertTriangle, X, File, Trash2, Maximize2, Columns2, RotateCcw, Save, Download, Music, Menu, SquarePen, Eye, Code, Copy, Check, FileDiff, History, Users, MessageCircle, ChevronLeft, ChevronRight, TableOfContents } from 'lucide-react';
import SyntaxHighlighter from 'react-syntax-highlighter/dist/esm/prism-light';
import { oneLight, oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism';
import tsx from 'react-syntax-highlighter/dist/esm/languages/prism/tsx';
import typescript from 'react-syntax-highlighter/dist/esm/languages/prism/typescript';
import javascript from 'react-syntax-highlighter/dist/esm/languages/prism/javascript';
import jsx from 'react-syntax-highlighter/dist/esm/languages/prism/jsx';
import csharp from 'react-syntax-highlighter/dist/esm/languages/prism/csharp';
import json from 'react-syntax-highlighter/dist/esm/languages/prism/json';
import markdown from 'react-syntax-highlighter/dist/esm/languages/prism/markdown';
import css from 'react-syntax-highlighter/dist/esm/languages/prism/css';
import scss from 'react-syntax-highlighter/dist/esm/languages/prism/scss';
import python from 'react-syntax-highlighter/dist/esm/languages/prism/python';
import go from 'react-syntax-highlighter/dist/esm/languages/prism/go';
import rust from 'react-syntax-highlighter/dist/esm/languages/prism/rust';
import java from 'react-syntax-highlighter/dist/esm/languages/prism/java';
import bash from 'react-syntax-highlighter/dist/esm/languages/prism/bash';
import yaml from 'react-syntax-highlighter/dist/esm/languages/prism/yaml';
import sql from 'react-syntax-highlighter/dist/esm/languages/prism/sql';
import markup from 'react-syntax-highlighter/dist/esm/languages/prism/markup';
import type { Project, GitBlameLine, GitLogEntry } from '../types';
import { api } from '../lib/api';
import { basename } from '../lib/paths';
import { resolveDocImage, resolveDocLink, sliceSection, slugify } from '../lib/docsLinks';
import { OfflineError } from '../lib/offline';
import { useGitState, ensureGit, gitRestoreFile, loadGitRemote } from '../lib/git';
import { parseDiffToHunks, buildHunkPatch, buildLinesPatch } from '../lib/gitPatch';
import { relTime } from '../lib/gitFormat';
import { toggleSyncMark, useSyncMarks, computeSyncState, isDownloaded, loadSyncMarks, loadDownloadedSet } from '../lib/sync';
import { onFilesChanged } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { useHeadings, useHeadingSpy, scrollToHeading, type DocToc, type Heading } from '../hooks/useHeadings';
import { EmptyState } from './EmptyState';
import { getLanguage } from '../lib/getLanguage';
import { MarkdownViewer } from './MarkdownViewer';
import { showToast } from '../lib/toast';
import { beginAiBusy, endAiBusy } from '../lib/ai/busy';
import { DocCommentedMarkdown } from '../features/notes/DocComments';
import { useNotes, ensureNotesLoaded, existingTitleSet, useNotesVersion } from '../lib/notes';
import { NoteConnections } from '../features/notes/NoteConnections';
import { NoteView } from '../features/notes/NoteView';
import type { NoteDetail } from '../types';
import { MermaidDiagram } from './MermaidDiagram';
import { DocumentViewer } from './DocumentViewer';
import { OfficeViewer } from './OfficeViewer';
import { DrawioViewer, type DrawioHandle } from './DrawioViewer';
import { base64ToBytes } from '../lib/binary';
import { C, FONT, FS, MODAL_W, SHADOW, SP, TB } from '../lib/design';
import { Toolbar, ToolbarIconButton, PillSwitch } from './Toolbar';
import { ToolbarOverflowMenu, type OverflowItem } from './ToolbarOverflowMenu';
import { useToolbarOverflow } from '../hooks/useToolbarOverflow';
import { BackButton, Modal, ModalActions, Button, ConfirmDialog, FileTypeTile, useIsMobileModal, Menu as UiMenu, MenuItem } from './ui';
import { DiffView } from './DiffView';
import { registerCopyDoc, copyMarkdown, copyRenderedHtml } from '../lib/selectionScope';
// Тумблер панели «Оглавление» правит раскладку зон напрямую — тем же каналом, что
// кнопка «Открыть изменения» в git-баре над композером (ProjectGitBar)
import { wsPanels, zoneOf } from '../pages/workspace/panelStackState';
import { useThemeMode, getEffectiveTheme } from '../lib/themeMode';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

const CodeEditor = lazy(() =>
  import('./CodeEditor').then(m => ({ default: m.CodeEditor }))
);
// Live preview-редактор заметок — для правки notes/*.md (vault проекта)
const NoteEditor = lazy(() =>
  import('../features/notes/NoteEditor').then(m => ({ default: m.NoteEditor }))
);

SyntaxHighlighter.registerLanguage('tsx', tsx);
SyntaxHighlighter.registerLanguage('typescript', typescript);
SyntaxHighlighter.registerLanguage('javascript', javascript);
SyntaxHighlighter.registerLanguage('jsx', jsx);
SyntaxHighlighter.registerLanguage('csharp', csharp);
SyntaxHighlighter.registerLanguage('json', json);
SyntaxHighlighter.registerLanguage('markdown', markdown);
SyntaxHighlighter.registerLanguage('css', css);
SyntaxHighlighter.registerLanguage('scss', scss);
SyntaxHighlighter.registerLanguage('python', python);
SyntaxHighlighter.registerLanguage('go', go);
SyntaxHighlighter.registerLanguage('rust', rust);
SyntaxHighlighter.registerLanguage('java', java);
SyntaxHighlighter.registerLanguage('bash', bash);
SyntaxHighlighter.registerLanguage('yaml', yaml);
SyntaxHighlighter.registerLanguage('sql', sql);
SyntaxHighlighter.registerLanguage('markup', markup);

interface Props {
  project: Project;
  filePath: string;
  onClose: () => void;
  onToggleFullscreen?: () => void;
  // Текущий режим просмотра: true — файл на весь экран, false/не задано — сплит с чатом
  fullscreen?: boolean;
  isMobile?: boolean;
  onOpenSidebar?: () => void;
  // Стартовая вкладка: 'diff' — открытие из git-панели «Изменения»
  initialTab?: 'file' | 'diff';
  // Путь файла, открытого из git-«Изменений» как unstaged: включает зернистый stage
  // хунков/строк на diff-вкладке (diff при этом — worktree против индекса)
  gitStagePath?: string;
  // Номер строки для скролла после открытия (из графа / ссылок на строку)
  scrollToLine?: number;
  // Открыть другой файл в центре (переход по ссылке из md). anchor — слаг раздела,
  // если ссылка вида «foo.md#раздел»: FileViewer проскроллит к нему после рендера.
  onOpenFile?: (path: string, anchor?: string) => void;
  // Слаг заголовка для скролла после открытия файла (ставит WorkspacePage, когда
  // файл открыт переходом по md-ссылке с якорем). null/undefined — скроллить не нужно.
  scrollToAnchor?: string | null;
  // Back/Forward по истории открытых файлов (браузерная навигация в пределах сессии).
  // Кнопки видны, пока есть куда идти (canFileBack/canFileForward); неактивная — disabled.
  onFileBack?: () => void;
  onFileForward?: () => void;
  canFileBack?: boolean;
  canFileForward?: boolean;
  // Оглавление открытого md — наружу, панели «Оглавление» (см. DocToc). null означает
  // «оглавления сейчас нет»: файл не markdown, просмотрщик закрылся или документ
  // показывает не эта зона (заметка vault рисуется своим NoteView). Панель по этому
  // null исчезает вместе со своей кнопкой, сохраняя место в раскладке.
  onTocChange?: (toc: DocToc | null) => void;
}

interface FileContent {
  content: string | null;
  isBinary: boolean;
  isImage: boolean;
  isVideo?: boolean;
  isAudio?: boolean;
  isDocument?: boolean;
  docKind?: string;
  mimeType?: string;
  base64?: string;
  fileSize?: number;
}

function streamUrl(projectId: string, filePath: string): string {
  const token = typeof localStorage !== 'undefined'
    ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
    : null;
  const params = new URLSearchParams({ path: filePath });
  if (token) params.set('access_token', token);
  return `/api/projects/${projectId}/files/stream?${params}`;
}

type ViewTab = 'file' | 'diff' | 'blame' | 'history';
// Вкладка «Код» — HTML-файл в исходнике: отдельный сегмент того же трека, что и остальные
// вкладки (раньше рядом стоял второй, одинаковый по форме, переключатель «Просмотр | Код»)
type TabKey = ViewTab | 'code';

// Центральной области неважно, документ ли области или файл кода — любой путь открывается
// здесь же FileViewer-ом (md тоже отрендерится). Поэтому knownDocs пуст: резолв ссылок
// отличает только «якорь текущего документа» (kind 'doc', скроллим) от «другой файл»
// (kind 'repo', открываем) — внешние MarkdownViewer уводит в новую вкладку сам.
const EMPTY_DOCS: ReadonlySet<string> = new Set();

// Ступени шапки по ширине ПАНЕЛИ (не окна — в сплите панель живёт своей жизнью).
// comfort — подписи, cozy/narrow — иконки, tight — вкладки уезжают в меню.
type Tier = 'comfort' | 'cozy' | 'narrow' | 'tight';

// Описание единственного «главного действия режима»: Править | Сохранить | Редактировать…
interface ToolbarAction {
  key: string;
  label: string;
  title?: string;
  icon: ReactNode;
  onClick: () => void;
  primary?: boolean;
  disabled?: boolean;
  loading?: boolean;
}

// Спиннер 14px в габарите icon-кнопки тулбара
const InlineSpinner = ({ color }: { color?: string }) => (
  <span style={{
    width: 14, height: 14, borderRadius: '50%', flexShrink: 0,
    border: `2.5px solid ${C.border}`, borderTopColor: color ?? C.accent,
    animation: 'spin 0.6s linear infinite',
  }} />
);

const DiscardIcon = () => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M3 7v6h6"/>
    <path d="M3 13C5.333 7.333 11.6 4 18 7a9 9 0 0 1 3 2"/>
  </svg>
);

const CloudGlyph = ({ filled }: { filled?: boolean }) => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill={filled ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z" />
  </svg>
);

// Рендер unified-diff вынесен в общий модуль DiffView.tsx

function AudioFilePlayer({ src, mimeType, fileName, fileSizeMb }: {
  src: string; mimeType?: string; fileName: string; fileSizeMb: string | null;
}) {
  const audioRef = useRef<HTMLAudioElement>(null);
  const [playing, setPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);

  const toggle = () => {
    const a = audioRef.current;
    if (!a) return;
    playing ? a.pause() : a.play().catch(() => {});
  };

  const seek = (e: React.ChangeEvent<HTMLInputElement>) => {
    const a = audioRef.current;
    if (!a) return;
    const t = Number(e.target.value);
    a.currentTime = t;
    setCurrentTime(t);
  };

  const skip = (delta: number) => {
    const a = audioRef.current;
    if (!a) return;
    a.currentTime = Math.max(0, Math.min(duration, a.currentTime + delta));
  };

  const fmt = (s: number) => {
    if (!isFinite(s) || isNaN(s)) return '0:00';
    const m = Math.floor(s / 60);
    return `${m}:${Math.floor(s % 60).toString().padStart(2, '0')}`;
  };

  const pct = duration > 0 ? (currentTime / duration) * 100 : 0;

  const skipBtnStyle: React.CSSProperties = {
    background: 'none', border: 'none', cursor: 'pointer',
    color: C.textSecondary, padding: '10px 8px',
    display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 3,
    fontSize: 10, fontFamily: FONT.mono, fontWeight: 600, lineHeight: 1,
    minWidth: 44,
  };

  return (
    <div style={{
      background: C.bgPanel, borderRadius: 16, border: `1px solid ${C.border}`,
      padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: 16,
      width: '100%', maxWidth: 440, boxShadow: SHADOW.card,
    }}>
      <audio
        ref={audioRef}
        onPlay={() => setPlaying(true)}
        onPause={() => setPlaying(false)}
        onEnded={() => { setPlaying(false); setCurrentTime(0); }}
        onTimeUpdate={() => setCurrentTime(audioRef.current?.currentTime ?? 0)}
        onLoadedMetadata={() => setDuration(audioRef.current?.duration ?? 0)}
      >
        <source src={src} type={mimeType} />
      </audio>

      {/* Иконка + имя файла */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <div style={{
          width: 40, height: 40, borderRadius: 10, background: C.accent,
          display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        }}>
          <Music size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} color={C.onAccent} />
        </div>
        <span style={{ fontFamily: FONT.mono, fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1 }}>
          {fileName}
        </span>
      </div>

      {/* Слайдер прогресса */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <input
          type="range"
          className="audio-seek"
          min={0}
          max={duration || 100}
          step={0.1}
          value={currentTime}
          onChange={seek}
          style={{ '--seek-pct': `${pct}%` } as React.CSSProperties}
        />
        <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, fontFamily: FONT.mono, color: C.textMuted }}>
          <span>{fmt(currentTime)}</span>
          <span>{fmt(duration)}</span>
        </div>
      </div>

      {/* Управление */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8 }}>
        <button onClick={() => skip(-10)} style={skipBtnStyle} title="−10 сек">
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M1 4v6h6"/><path d="M3.51 15a9 9 0 1 0 .49-3.36"/>
          </svg>
          −10
        </button>

        <button
          onClick={toggle}
          style={{
            width: 60, height: 60, borderRadius: '50%', border: 'none',
            background: C.accent, color: C.onAccent, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: SHADOW.button, flexShrink: 0,
          }}
        >
          {playing
            ? <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="4" width="4" height="16" rx="1"/><rect x="14" y="4" width="4" height="16" rx="1"/></svg>
            : <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor" style={{ marginLeft: 3 }}><polygon points="5 3 19 12 5 21 5 3"/></svg>
          }
        </button>

        <button onClick={() => skip(10)} style={skipBtnStyle} title="+10 сек">
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M23 4v6h-6"/><path d="M20.49 15a9 9 0 1 1-.49-3.36"/>
          </svg>
          +10
        </button>
      </div>

      {/* Метаданные */}
      <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.mono, textAlign: 'center' }}>
        {(mimeType?.split('/')[1] ?? fileName.split('.').pop() ?? '').toUpperCase()}
        {fileSizeMb && ` · ${fileSizeMb} МБ`}
      </div>
    </div>
  );
}

export function FileViewer({ project, filePath, onClose, onToggleFullscreen, fullscreen, isMobile, onOpenSidebar, initialTab, gitStagePath, scrollToLine, onOpenFile, scrollToAnchor, onFileBack, onFileForward, canFileBack, canFileForward, onTocChange }: Props) {
  const online = useOnline();
  // Хост-режим: путь абсолютный (вне корня проекта) — файл открыт карточкой инструмента/
  // изменённого файла чата, живущего в другом дереве. Контент — через /host-files/content,
  // а не projects/{id}/files/*: обычные project-эндпоинты дали бы 403 (SafeJoin).
  const isHostMode = /^[A-Za-z]:[\\/]|^\//.test(filePath);
  // Заметки vault (notes/*.md): рендерим [[wikilinks]] и уводим по клику в раздел «Заметки»
  const allNotes = useNotes();
  // На абсолютном пути эвристика ложно срабатывает (мало ли где встретится «notes/»
  // за пределами проекта) — в хост-режиме файл заметкой не считается никогда.
  const isNotesFile = !isHostMode && /(^|\/)notes\//i.test(filePath);
  useEffect(() => { if (isNotesFile) void ensureNotesLoaded(); }, [isNotesFile]);
  const noteTitles = useMemo(() => existingTitleSet(allNotes), [allNotes]);
  const openNoteByTitle = (t: string) => {
    const name = t.split('/').pop()!.split('#')[0].trim();
    sessionStorage.setItem('cc_pending_note_title', name);
    window.dispatchEvent(new Event('cc-open-note'));
  };
  // Hover-preview и embed ![[…]] в проектных notes/*.md
  const resolveNoteByName = async (name: string, anchor?: string) => {
    try {
      const r = await api.notes.resolve(name, anchor);
      return { title: r.note.title, content: r.fragment ?? r.note.content };
    } catch { return null; }
  };
  // Связи заметки (backlinks/исходящие/граф) для сайдбара просмотра
  const notesVersion = useNotesVersion();
  const [noteDetail, setNoteDetail] = useState<NoteDetail | null>(null);
  useEffect(() => {
    if (!isNotesFile) { setNoteDetail(null); return; }
    let alive = true;
    const title = filePath.split('/').pop()!.replace(/\.md$/i, '');
    api.notes.resolve(title)
      .then(r => { if (alive) setNoteDetail(r.note); })
      .catch(() => { if (alive) setNoteDetail(null); });
    return () => { alive = false; };
  }, [isNotesFile, filePath, notesVersion]);
  const openNoteById = (id: string, title: string) => {
    if (title) { openNoteByTitle(title); return; }
    sessionStorage.setItem('cc_pending_note_id', id);
    window.dispatchEvent(new Event('cc-open-note'));
  };
  // Навигация по ссылкам/backlinks внутри вьювера: открываем другую заметку на месте,
  // не уводя в раздел «Заметки» (сброс при смене файла в дереве)
  const [noteIdOverride, setNoteIdOverride] = useState<string | null>(null);
  useEffect(() => { setNoteIdOverride(null); }, [filePath]);
  const openWikilinkInPlace = (target: string) => {
    const name = target.split('/').pop()!.split('#')[0].trim().toLowerCase();
    const found = allNotes.find(n => n.title.trim().toLowerCase() === name);
    if (found) setNoteIdOverride(found.id);
    else openNoteByTitle(target);
  };
  // Подписка на тему: подсветка кода переключается light/dark вместе с приложением
  useThemeMode();
  const codeTheme = getEffectiveTheme() === 'dark' ? oneDark : oneLight;
  const [fileContent, setFileContent] = useState<FileContent | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);
  // 403 хост-режима (путь вне досягаемости песочницы) — отдельно от прочих ошибок,
  // у него человекочитаемое сообщение вместо общего «не удалось открыть»
  const [loadForbidden, setLoadForbidden] = useState(false);
  const [diff, setDiff] = useState<string | null>(null);
  const [tab, setTab] = useState<ViewTab>('file');
  // Git: репо-статус (гейт вкладки «Авторы»), blame-кэш и busy зернистого stage
  const gitSt = useGitState(project.id);
  useEffect(() => { ensureGit(project.id); }, [project.id]);
  const inRepo = gitSt.status?.isRepo ?? false;
  const [blame, setBlame] = useState<GitBlameLine[] | null>(null);
  const [blameLoading, setBlameLoading] = useState(false);
  const [blameError, setBlameError] = useState(false);
  // Вкладка «История» — версии этого файла (git log --follow) + diff выбранной версии
  const [fileLog, setFileLog] = useState<GitLogEntry[] | null>(null);
  const [fileLogLoading, setFileLogLoading] = useState(false);
  const [versionSha, setVersionSha] = useState<string | null>(null);
  const [versionDiff, setVersionDiff] = useState<string | null>(null);
  const [versionDiffLoading, setVersionDiffLoading] = useState(false);
  // Вид выбранной версии: изменения (diff) либо файл целиком «как был»
  const [versionView, setVersionView] = useState<'diff' | 'content'>('diff');
  const [versionContent, setVersionContent] = useState<string | null>(null);
  const [versionContentLoading, setVersionContentLoading] = useState(false);
  const [restoreConfirmSha, setRestoreConfirmSha] = useState<string | null>(null);
  const [restoring, setRestoring] = useState(false);
  const docMode = gitSt.remote?.autoCommit === true;
  const [stageBusy, setStageBusy] = useState(false);
  const [editing, setEditing] = useState(false);
  const [htmlTab, setHtmlTab] = useState<'preview' | 'code'>('preview');
  const [officeMode, setOfficeMode] = useState<'view' | 'edit'>('view');
  const [officeSwitching, setOfficeSwitching] = useState(false);
  // Подтверждение отката office-правок — диалог (на мобиле сам станет шторкой)
  const [officeDiscardDialog, setOfficeDiscardDialog] = useState(false);
  const [officeCacheKey, setOfficeCacheKey] = useState<string | undefined>();
  // Режим draw.io: по умолчанию просмотр (read-only), кнопка «Редактировать» → edit
  const [drawioMode, setDrawioMode] = useState<'view' | 'edit'>('view');
  const [editContent, setEditContent] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const [unsavedConfirm, setUnsavedConfirm] = useState(false);
  // Что делаем после диалога несохранённых правок: закрыть файл или сменить режим просмотра
  const [unsavedIntent, setUnsavedIntent] = useState<'close' | 'mode'>('close');
  // Ошибка мутации (сохранение/откат/удаление) офлайн или при сбое — inline-фидбек
  const [actionError, setActionError] = useState<string | null>(null);
  const [imgDims, setImgDims] = useState<{ w: number; h: number } | null>(null);
  // Счётчики комментариев к документу — чип в тулбаре (данные поднимает DocCommentedMarkdown)
  const [commentCounts, setCommentCounts] = useState<{ total: number; open: number } | null>(null);
  useEffect(() => { setCommentCounts(null); }, [filePath]);
  const onCommentCounts = useCallback((total: number, open: number) => setCommentCounts({ total, open }), []);
  const drawioRef = useRef<DrawioHandle>(null);
  const marks = useSyncMarks(project.id);
  // Фидбек кнопки «Скопировать» в тулбаре
  const [copied, setCopied] = useState(false);
  // Контент-зона просмотра: корень «форматированного» копирования + источник Ctrl+C без выделения
  const contentAreaRef = useRef<HTMLDivElement>(null);

  // Ширина самой панели (не окна): в сплите она сжимается до 200px, и на такой
  // ширине двухсегментный трек «Сплит | Полный» (~195px) неуместен — вместо него
  // рисуем икону-тумблер. 0 — ещё не померили (первый рендер).
  const rootRef = useRef<HTMLDivElement>(null);
  const [panelWidth, setPanelWidth] = useState(0);
  useEffect(() => {
    const el = rootRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setPanelWidth(el.clientWidth));
    ro.observe(el);
    setPanelWidth(el.clientWidth);
    return () => ro.disconnect();
  }, []);

  const content = fileContent?.content ?? '';
  const hasUnsavedChanges = editing && editContent !== content;

  // Оглавление md для скролла к якорю: снимается с DOM контент-зоны (md внутри
  // DocCommentedMarkdown — вложенность querySelectorAll не мешает). Используется и для
  // клика по «#раздел» текущего документа, и для якоря при открытии файла по ссылке.
  const headings = useHeadings(contentAreaRef, content);
  // Якорь, ждущий отрисовки md. Привязан к пути: между сменой файла и пересбором
  // оглавления есть кадр, где headings ещё от прежнего — без проверки пути якорь
  // искался бы в чужом оглавлении. В ref, а не в состоянии: значение нужно эффекту.
  const pendingAnchorRef = useRef<{ path: string; anchor: string } | null>(null);

  // Переход к разделу — общим scrollToHeading: он сам находит скроллер, удерживает цель,
  // пока md дорисовывается, и мигает ею по приезде. Свой одноразовый прыжок тут был
  // ненадёжен вдвойне: nativeScrollIntoView на раскладке DocCommentedMarkdown (flex-рядок
  // со sticky-сайдбаром комментариев) молчит, а прыжок по смещению недоматывал на тяжёлых
  // документах — комментарии и подсветка кода доезжают уже после него.
  const scrollDocTo = useCallback((h: Heading) => {
    scrollToHeading(contentAreaRef.current, h);
  }, []);

  // Открытие файла по md-ссылке с якорем: WorkspacePage ставит scrollToAnchor, здесь
  // запоминаем его (привязав к текущему пути) — сработает эффектом ниже, когда md отрисован
  useEffect(() => {
    if (scrollToAnchor) pendingAnchorRef.current = { path: filePath, anchor: scrollToAnchor };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scrollToAnchor]);

  // Живого узла цели ещё нет (md перерисовывается) — ждём следующего пересбора оглавления,
  // якорь не гасим: иначе переход молча терялся бы
  useEffect(() => {
    const pending = pendingAnchorRef.current;
    if (!pending || pending.path !== filePath || !content || headings.length === 0) return;
    const target = headings.find(h => slugify(h.text) === pending.anchor);
    if (!target || !scrollToHeading(contentAreaRef.current, target)) return;
    pendingAnchorRef.current = null;
  }, [filePath, content, headings]);

  // Клик по ссылке в md (режим документации MarkdownViewer): резолв относительного пути
  // и якоря. Якорь текущего документа — скролл; другой файл — onOpenFile (с якорем);
  // внешние MarkdownViewer уводит в _blank сам, сюда они не доходят.
  const handleDocLink = useCallback((href: string) => {
    if (!filePath) return;
    const link = resolveDocLink(filePath, href, EMPTY_DOCS);
    if (!link || link.kind === 'external') return;
    if (link.kind === 'doc' && link.target === filePath && link.anchor) {
      const target = headings.find(h => slugify(h.text) === link.anchor);
      if (target) scrollDocTo(target);
      return;
    }
    onOpenFile?.(link.target, link.anchor ?? undefined);
  }, [filePath, headings, onOpenFile, scrollDocTo]);
  const syncState = computeSyncState(marks, filePath);
  // Помечен, но содержимое ещё не скачано → спиннер
  const pending = !!syncState && !isDownloaded(project.id, filePath);

  // В режиме зернистого stage дифф — worktree против ИНДЕКСА (git diff без staged),
  // иначе патчи хунков не соответствовали бы содержимому индекса
  const fetchDiff = () => gitStagePath
    ? api.git.diff(project.id, filePath, false)
    : api.files.getDiff(project.id, filePath);

  useEffect(() => {
    setEditing(false);
    setTab('file');
    setHtmlTab('preview');
    setOfficeMode('view');
    setOfficeSwitching(false);
    setOfficeDiscardDialog(false);
    setOfficeCacheKey(undefined);
    setDrawioMode('view');
    setLoading(true);
    setLoadError(false);
    setLoadForbidden(false);
    setFileContent(null);
    setImgDims(null);
    setActionError(null);
    setBlame(null);
    setBlameError(false);
    setFileLog(null);
    setVersionSha(null);
    setVersionDiff(null);
    setRestoreConfirmSha(null);
    const contentPromise = isHostMode ? api.hostFiles.getContent(filePath) : api.files.getContent(project.id, filePath);
    contentPromise.then(r => {
      setFileContent(r);
      setEditContent(r.content ?? '');
    }).catch(e => {
      if (isHostMode && (e as { status?: number })?.status === 403) setLoadForbidden(true);
      setLoadError(true);
    }).finally(() => setLoading(false));
    // Дифф — понятие проектного git-репо; у файла вне проекта его нет
    if (isHostMode) setDiff(null);
    // diff недоступен офлайн — мягко игнорируем ошибку
    else fetchDiff().then(r => setDiff(r.diff)).catch(() => setDiff(null));
  }, [project.id, filePath, gitStagePath, isHostMode]);

  // Blame — лениво при первом открытии вкладки «Авторы» (кэш до смены файла)
  useEffect(() => {
    if (tab !== 'blame' || blame || blameLoading || blameError) return;
    setBlameLoading(true);
    api.git.blame(project.id, filePath)
      .then(b => setBlame(b))
      .catch(() => setBlameError(true))
      .finally(() => setBlameLoading(false));
  }, [tab, blame, blameLoading, blameError, project.id, filePath]);

  // История файла — лениво при первом открытии вкладки (кэш до смены файла)
  useEffect(() => {
    if (tab !== 'history' || fileLog || fileLogLoading) return;
    setFileLogLoading(true);
    void loadGitRemote(project.id);
    api.git.fileLog(project.id, filePath)
      .then(log => {
        setFileLog(log);
        if (log.length) setVersionSha(log[0].sha);
      })
      .catch(() => setFileLog([]))
      .finally(() => setFileLogLoading(false));
  }, [tab, fileLog, fileLogLoading, project.id, filePath]);

  // Diff выбранной версии файла (смена версии сбрасывает кэш «содержимого»)
  useEffect(() => {
    if (tab !== 'history' || !versionSha) return;
    let cancelled = false;
    setVersionDiffLoading(true);
    setVersionContent(null);
    api.git.commitFileDiff(project.id, versionSha, filePath)
      .then(r => { if (!cancelled) setVersionDiff(r.diff); })
      .catch(() => { if (!cancelled) setVersionDiff(null); })
      .finally(() => { if (!cancelled) setVersionDiffLoading(false); });
    return () => { cancelled = true; };
  }, [tab, versionSha, project.id, filePath]);

  // Содержимое файла «как был» в версии — лениво при переключении на вид «Содержимое»
  useEffect(() => {
    if (tab !== 'history' || versionView !== 'content' || !versionSha || versionContent !== null) return;
    let cancelled = false;
    setVersionContentLoading(true);
    api.git.fileAtCommit(project.id, versionSha, filePath)
      .then(r => { if (!cancelled) setVersionContent(r.content ?? ''); })
      .catch(() => { if (!cancelled) setVersionContent(''); })
      .finally(() => { if (!cancelled) setVersionContentLoading(false); });
    return () => { cancelled = true; };
  }, [tab, versionView, versionSha, versionContent, project.id, filePath]);

  const handleRestoreVersion = async () => {
    if (!restoreConfirmSha) return;
    setRestoring(true);
    const ok = await gitRestoreFile(project.id, restoreConfirmSha, filePath);
    setRestoring(false);
    if (ok) {
      setRestoreConfirmSha(null);
      // Содержимое файла изменилось — перечитать файл и diff, вернуться на «Файл»
      setTab('file');
      setLoading(true);
      api.files.getContent(project.id, filePath).then(r => {
        setFileContent(r);
        setEditContent(r.content ?? '');
      }).catch(() => {}).finally(() => setLoading(false));
      fetchDiff().then(r => setDiff(r.diff)).catch(() => {});
      setFileLog(null);
      setBlame(null);
    }
  };

  // Открытие из git-панели «Изменения» — сразу вкладка Diff (эффект объявлен ПОСЛЕ
  // основного, чтобы перебить его сброс на 'file'; срабатывает и когда тот же файл
  // повторно открывают уже в diff-режиме)
  useEffect(() => {
    if (initialTab) setTab(initialTab);
  }, [initialTab, filePath]);

  // Скролл к строке при открытии файла (из графа / ссылок на строку)
  useEffect(() => {
    if (!scrollToLine || tab !== 'file' || !content || loading) return;
    // Даём рендеру SyntaxHighlighter осесть
    const id = setTimeout(() => {
      const container = contentAreaRef.current;
      if (!container) return;
      // react-syntax-highlighter с showLineNumbers рендерит строки как строчные
      // элементы; ищем N-й line-number по классу
      const lines = container.querySelectorAll('[class*="line-number"], [class*="linenumber"]');
      const target = lines[scrollToLine - 1];
      if (target) {
        target.scrollIntoView({ block: 'center', behavior: 'smooth' });
      }
    }, 100);
    return () => clearTimeout(id);
  }, [scrollToLine, tab, content, loading]);

  // Метки синхронизации + набор скачанных файлов — в общий стор (синхронно с деревом)
  useEffect(() => {
    loadSyncMarks(project.id);
    loadDownloadedSet(project.id);
  }, [project.id]);

  // Watcher: открытый файл изменился на диске → перечитываем (если не редактируем — не затираем правки).
  // Хост-режим — файл вне проекта, событий по нему не будет: подписка бессмысленна.
  useEffect(() => {
    if (isHostMode) return;
    return onFilesChanged(({ projectId, paths }) => {
      // Пока draw.io в режиме edit — не перечитываем: autosave сам пишет файл,
      // а перезагрузка content дала бы лишние refetch на каждый autosave.
      const isDrawioEditing = /\.(drawio|dio)$/i.test(filePath) && drawioMode === 'edit';
      if (projectId !== project.id || editing || isDrawioEditing) return;
      const norm = filePath.replace(/\\/g, '/');
      if (!paths.some(p => p.replace(/\\/g, '/') === norm)) return;
      api.files.getContent(project.id, filePath).then(r => { setFileContent(r); setEditContent(r.content ?? ''); setLoadError(false); }).catch(() => {});
      fetchDiff().then(r => setDiff(r.diff)).catch(() => {});
      setBlame(null);   // авторство устарело — перечитается при открытии вкладки
      setBlameError(false);
    });
  }, [project.id, filePath, editing, drawioMode, gitStagePath, isHostMode]);

  const handleToggleSync = () => {
    toggleSyncMark(project.id, {
      name: fileName, path: filePath, isDirectory: false,
      modified: '', isModified: false,
    });
  };

  // Понятный текст для ошибки мутации
  const mutationErrorText = (e: unknown, fallback: string) =>
    e instanceof OfflineError ? 'Действие недоступно офлайн' : fallback;

  const handleSave = async (): Promise<boolean> => {
    try {
      await api.files.saveContent(project.id, filePath, editContent);
      setFileContent(prev => prev ? { ...prev, content: editContent } : prev);
      setEditing(false);
      setActionError(null);
      const r = await fetchDiff();
      setDiff(r.diff);
      return true;
    } catch (e) {
      // Не выходим из режима редактирования — иначе потеряются несохранённые правки
      setActionError(mutationErrorText(e, 'Не удалось сохранить файл'));
      return false;
    }
  };

  const handleDelete = async () => {
    try {
      await api.files.delete(project.id, filePath);
      onClose();
    } catch (e) {
      setDeleteConfirm(false);
      setActionError(mutationErrorText(e, 'Не удалось удалить файл'));
    }
  };

  const handleRevert = async () => {
    try {
      await api.files.revert(project.id, filePath);
      const r = await api.files.getContent(project.id, filePath);
      setFileContent(r);
      setEditContent(r.content ?? '');
      setDiff(null);
      setBlame(null);
      setBlameError(false);
      setTab('file');
      setActionError(null);
    } catch (e) {
      setActionError(mutationErrorText(e, 'Не удалось откатить файл'));
    }
  };

  // === Зернистый stage хунков/строк (файл открыт из git-«Изменений» как unstaged) ===

  const refreshAfterStage = async () => {
    // Статус в git-сторе обновится по realtime git_status_changed; дифф перечитываем локально
    try { const r = await fetchDiff(); setDiff(r.diff); } catch { /* оставляем как есть */ }
  };

  const handleStageHunk = async (hunkIdx: number) => {
    if (!gitStagePath || !diff || stageBusy) return;
    const parsed = parseDiffToHunks(diff);
    const hunk = parsed.hunks[hunkIdx];
    if (!hunk) return;
    setStageBusy(true);
    try {
      await api.git.stageHunk(project.id, buildHunkPatch(parsed.fileHeader, hunk));
      await refreshAfterStage();
      setActionError(null);
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Не удалось проиндексировать хунк');
    }
    setStageBusy(false);
  };

  const handleStageLines = async (selected: Map<number, Set<number>>) => {
    if (!gitStagePath || !diff || stageBusy || selected.size === 0) return;
    const parsed = parseDiffToHunks(diff);
    setStageBusy(true);
    try {
      // По патчу на хунк, по возрастанию — git apply сам компенсирует сдвиг строк
      const idxs = [...selected.keys()].sort((a, b) => a - b);
      for (const hunkIdx of idxs) {
        const hunk = parsed.hunks[hunkIdx];
        if (!hunk) continue;
        await api.git.stageHunk(project.id, buildLinesPatch(parsed.fileHeader, hunk, selected.get(hunkIdx)!));
      }
      await refreshAfterStage();
      setActionError(null);
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Не удалось проиндексировать строки');
      await refreshAfterStage();   // часть хунков могла примениться — дифф уже другой
    }
    setStageBusy(false);
  };

  // «Спросить Claude про файл» (AI-хаб, action file.ask) — эквивалент note.ask заметки:
  // кладём затравку с путём файла и (для текстовых файлов) его содержимым в общий канал
  // композера. Любой смонтированный композер заберёт её по событию, а следующий — при
  // монтировании (Composer.consume). Закрываем файл, открывая чат проекта.
  // ИИ по документу (pdf/docx/xlsx/pptx) через локальную модель: результат — в модалку
  const [docAi, setDocAi] = useState<{ title: string; markdown: string } | null>(null);
  const [docAiBusy, setDocAiBusy] = useState(false);
  const runDocAi = async (kind: 'summary' | 'extract' | 'tags' | 'convert') => {
    // Разрешаем документы (pdf/docx/…) и текстовые файлы; блокируем прочие бинарные и повторный клик.
    // Хост-режим — эндпоинты проектные (project.id + путь), для файла вне проекта не сработают
    if (docAiBusy || isHostMode || !fileContent || (fileContent.isBinary && !fileContent.isDocument)) return;
    setDocAiBusy(true);
    beginAiBusy();
    try {
      if (kind === 'summary') {
        const r = await api.files.documentSummary(project.id, filePath);
        setDocAi({ title: 'Краткое содержание', markdown: r.summary || '_пусто_' });
      } else if (kind === 'convert') {
        const r = await api.files.documentConvert(project.id, filePath);
        setDocAi({ title: 'Markdown документа', markdown: r.markdown || '_пусто_' });
      } else if (kind === 'tags') {
        const r = await api.files.documentTags(project.id, filePath);
        setDocAi({ title: 'Теги документа', markdown: r.tags.map(t => `\`${t}\``).join('  ') || '_нет тегов_' });
      } else {
        const r = await api.files.documentExtract(project.id, filePath);
        const sec = (h: string, xs: string[]) => xs.length ? `## ${h}\n${xs.map(x => `- ${x}`).join('\n')}\n\n` : '';
        const md = sec('Решения', r.decisions) + sec('Даты', r.dates) + sec('Участники', r.people) + sec('Действия', r.actionItems);
        setDocAi({ title: 'Выжимка из документа', markdown: md || '_ничего не извлечено_' });
      }
    } catch {
      showToast('Ошибка', 'Не удалось обработать документ', 'info');
    } finally {
      setDocAiBusy(false);
      endAiBusy();
    }
  };

  const askAboutFile = () => {
    const isText = !!fileContent && !fileContent.isBinary && !fileContent.isImage
      && !fileContent.isDocument && !fileContent.isVideo && !fileContent.isAudio;
    const body = isText && fileContent?.content
      ? `\n\n\`\`\`\n${fileContent.content}\n\`\`\`\n\n`
      : '\n\n';
    sessionStorage.setItem('cc_pending_chat_prompt', `Про файл «${filePath}»:${body}`);
    window.dispatchEvent(new Event('cc-compose-prefill'));
    onClose();
  };

  // Подписка на контекстное действие AI-хаба (снимается на unmount)
  useEffect(() => {
    const onRun = (e: Event) => {
      const a = (e as CustomEvent<{ action?: string }>).detail?.action;
      if (a === 'file.ask') askAboutFile();
      else if (a === 'file.summary') void runDocAi('summary');
      else if (a === 'file.extract') void runDocAi('extract');
      else if (a === 'file.tags') void runDocAi('tags');
      else if (a === 'file.convert') void runDocAi('convert');
      else if (a === 'file.toMarkdown' && !isHostMode) void (async () => {
        beginAiBusy();
        try {
          const r = await api.files.toMarkdown(project.id, filePath);
          showToast('Сохранено в Markdown', r.savedPath);
        } catch { showToast('Ошибка', 'Не удалось трансформировать файл', 'info'); }
        finally { endAiBusy(); }
      })();
    };
    window.addEventListener('cc-ai-run', onRun);
    return () => window.removeEventListener('cc-ai-run', onRun);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filePath, fileContent]);

  const handleClose = async () => {
    // draw.io в режиме edit — сохраняем текущие правки перед закрытием
    if (isDrawio && drawioMode === 'edit') await drawioRef.current?.flush();
    if (hasUnsavedChanges) {
      setUnsavedIntent('close');
      setUnsavedConfirm(true);
    } else {
      onClose();
    }
  };

  // Escape закрывает файл (handleClose сам спросит про несохранённое). Не перехватываем,
  // если печатают в поле/редакторе или уже открыт диалог/меню — там Escape нужнее им.
  // Через ref: слушатель вешаем один раз, но зовём всегда свежий handleClose.
  const closeRef = useRef(handleClose);
  closeRef.current = handleClose;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      if (document.querySelector('[role="dialog"], [role="menu"]')) return;
      void closeRef.current();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Смена режима «сплит ↔ на весь экран»: FileViewer при этом пересоздаётся
  // (в WorkspacePage это две разные ветки дерева), поэтому несохранённые правки
  // пропали бы молча — спрашиваем тем же диалогом, что и при закрытии.
  const handleToggleMode = () => {
    if (!onToggleFullscreen) return;
    if (hasUnsavedChanges) {
      setUnsavedIntent('mode');
      setUnsavedConfirm(true);
    } else {
      onToggleFullscreen();
    }
  };

  const finishUnsavedIntent = () => {
    if (unsavedIntent === 'mode') onToggleFullscreen?.();
    else onClose();
  };

  const handleCloseWithoutSave = () => {
    setUnsavedConfirm(false);
    finishUnsavedIntent();
  };

  const handleSaveAndClose = async () => {
    setUnsavedConfirm(false);
    // Продолжаем только при успешном сохранении — иначе правки потеряются (офлайн/сбой)
    const ok = await handleSave();
    if (ok) finishUnsavedIntent();
  };

  const handleDownload = () => {
    if (!fileContent?.base64) return;
    const blob = new Blob([base64ToBytes(fileContent.base64)], { type: fileContent.mimeType ?? 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(url);
  };

  // basename (не split('/').pop()): хост-путь может прийти с обратными слэшами
  // (Windows), как есть — relPath не нормализует то, что вне корня проекта
  const fileName = basename(filePath) || filePath;
  const isMarkdown = /\.(md|mdx)$/i.test(fileName);
  // Текстовый файл, содержимое которого можно скопировать целиком
  const isCopyableText = !!fileContent && !fileContent.isBinary && !fileContent.isImage
    && !fileContent.isDocument && !fileContent.isVideo && !fileContent.isAudio;

  // === ОГЛАВЛЕНИЕ НАРУЖУ (панель «Оглавление») ===
  // Заголовки уже собраны выше (useHeadings) ради якорей — отдаём тот же список панели
  // вместе с действиями над ним: прокруткой владеет просмотрщик (свой скроллер, своя
  // поправка на шапку), нарезкой раздела — тоже он, потому что резать надо ИСХОДНЫЙ
  // markdown, а не текст из DOM.
  const sectionOf = useCallback(
    (h: Heading) => sliceSection(content, slugify(h.text)),
    [content]);

  // Переход к разделу из панели. Узел берём НЕ из самого заголовка, а ищем заново
  // (resolveHeadingEl): панель живёт рядом с документом сколько угодно, а markdown за
  // это время перерисовывается — комментарии к документу приезжают асинхронно и меняют
  // разметку целиком. Собранные узлы после такой перерисовки оторваны от документа, и
  // прокрутка по ним молча уезжала в начало вместо нужного раздела.
  // Стабильная (scrollDocTo стабилен) — иначе объект оглавления пересобирался бы на
  // каждый рендер и эффект ниже гонял бы setState по кругу.
  // Какой раздел читают сейчас — панель узнаёт подпиской (см. DocToc.subscribeActive)
  const { subscribe: subscribeActiveHeading, pin: pinHeading } = useHeadingSpy(contentAreaRef, headings);

  // Зависимости — на сами функции, а не на объект хука: объект в зависимостях
  // пересобирал бы оглавление на каждом рендере (см. useHeadingSpy)
  const jumpToHeading = useCallback((h: Heading) => {
    // Сначала подсветка цели, потом прокрутка: клик по строке обязан отзываться
    // мгновенно, а не после того, как документ доедет
    pinHeading(h);
    scrollDocTo(h);
  }, [scrollDocTo, pinHeading]);

  // Заметка vault рисуется отдельным NoteView (ранний return ниже) — её markdown в
  // contentAreaRef не попадает, и оглавление там всегда пустое. Честнее не показывать
  // панель вовсе, чем держать кнопку, которая открывает пустоту.
  const tocAvailable = isMarkdown && !(isNotesFile && (noteIdOverride || noteDetail));

  useEffect(() => {
    if (!onTocChange) return;
    onTocChange(tocAvailable
      ? { path: filePath, headings, jump: jumpToHeading, sectionOf, subscribeActive: subscribeActiveHeading }
      : null);
  }, [onTocChange, tocAvailable, filePath, headings, jumpToHeading, sectionOf, subscribeActiveHeading]);

  // Просмотрщик ушёл с экрана — панель обязана исчезнуть вместе с ним: иначе она
  // осталась бы висеть с оглавлением закрытого документа
  useEffect(() => () => onTocChange?.(null), [onTocChange]);

  // Тумблер панели «Оглавление» в тулбаре. Кнопка рельсы стоит у самого края окна и
  // при чтении оказывается далеко от глаз, поэтому оглавление зовётся и отсюда —
  // от документа, к которому относится.
  //
  // Раскладку правим напрямую через стор зон (как «Открыть изменения» в git-баре):
  // панель может лежать в любой из рельс, и знать это просмотрщику незачем — reveal
  // открывает закрытую в её домашней зоне, close убирает открытую где бы то ни было.
  const { zones: panelZones, reveal: revealPanelKey, close: closePanelKey } = wsPanels.use();
  const tocPanelOpen = zoneOf(panelZones, 'toc') !== null;
  // Тумблер нужен, только когда панели есть куда открыться: без onTocChange контент
  // панели никто не собирает (мобильная вёрстка), и кнопка вела бы в пустоту
  const tocToggleVisible = tocAvailable && !isMobile && !!onTocChange;
  const toggleTocPanel = () => {
    if (tocPanelOpen) closePanelKey('toc');
    else revealPanelKey('toc');
  };

  // Ctrl+C без выделения: отдаём исходник открытого текстового файла (см. selectionScope)
  const copySourceRef = useRef<() => string | null>(() => null);
  copySourceRef.current = () => (isCopyableText ? (fileContent?.content ?? null) : null);
  useEffect(() => {
    const el = contentAreaRef.current;
    if (!el) return;
    return registerCopyDoc(el, () => copySourceRef.current());
  }, []);

  // Клик — скопировать исходник (raw markdown/код); Shift+клик по .md — с форматированием
  // (из «···» приходим без события — только исходник)
  const copyContent = async (withFormat: boolean) => {
    const raw = editing ? editContent : content;
    const rendered = withFormat
      ? contentAreaRef.current?.querySelector<HTMLElement>('[data-selection-scope]')
      : null;
    const ok = rendered ? await copyRenderedHtml(rendered) : await copyMarkdown(raw);
    if (ok) { setCopied(true); setTimeout(() => setCopied(false), 1500); }
  };
  const handleCopyContent = (e: React.MouseEvent) => {
    void copyContent(e.shiftKey && isMarkdown && !editing);
  };
  const isMermaid = /\.mmd$/i.test(fileName);
  const isHtml = /\.html?$/i.test(fileName);
  const isDrawio = /\.(drawio|dio)$/i.test(fileName);
  const diffStats = diff ? {
    added: diff.split('\n').filter(l => l.startsWith('+') && !l.startsWith('+++')).length,
    removed: diff.split('\n').filter(l => l.startsWith('-') && !l.startsWith('---')).length,
  } : null;
  // Вкладка «Авторы» (blame) — только для текстовых файлов в git-репо; в хост-режиме
  // git-вкладки скрыты целиком (файл вне проекта — своего репо/истории тут нет)
  const showBlameTab = !isHostMode && inRepo && !loading && !loadError && !!fileContent && !fileContent.isBinary && !fileContent.isImage;
  const fileSizeMb = fileContent?.fileSize != null ? (fileContent.fileSize / 1024 / 1024).toFixed(2) : null;

  const btnPrimary: React.CSSProperties = {
    border: 'none', background: C.accent, color: C.onAccent,
    borderRadius: 8, padding: '5px 13px', cursor: 'pointer', fontSize: 13, fontWeight: 600,
  };

  // OnlyOffice — эндпоинт проектный (project.id + filePath), для хост-файла не сработает;
  // офисные документы вне проекта показываем как обычный бинарник (см. рендер ниже)
  const isOfficeFile = !isHostMode && !loading && !loadError && tab === 'file' && !!fileContent?.isDocument && fileContent.docKind !== 'pdf';
  // Visio OnlyOffice открывает только на просмотр — переключатель «Редактировать» не показываем
  const isVisioFile = fileContent?.docKind === 'visio';
  const isCodeEditing = editing && tab === 'file' && !fileContent?.isBinary && !fileContent?.isImage;
  const isPdfViewing = !loading && !loadError && tab === 'file' && !!fileContent?.isDocument && fileContent.docKind === 'pdf';
  const isHtmlPreviewing = !loading && !loadError && tab === 'file' && isHtml && htmlTab === 'preview' && !editing && !fileContent?.isBinary;
  const isDrawioViewing = !loading && !loadError && tab === 'file' && isDrawio && !fileContent?.isBinary;

  // Сохранение диаграммы из встроенного редактора draw.io: пишем XML и обновляем diff.
  // fileContent.content обновляем, но iframe не перезагружаем (DrawioViewer грузит XML
  // только по событию init), поэтому редактор не сбрасывается.
  const handleDrawioSave = async (xml: string) => {
    try {
      await api.files.saveContent(project.id, filePath, xml);
      setFileContent(prev => prev ? { ...prev, content: xml } : prev);
      setEditContent(xml);
      setActionError(null);
      const r = await fetchDiff();
      setDiff(r.diff);
    } catch (e) {
      setActionError(mutationErrorText(e, 'Не удалось сохранить диаграмму'));
    }
  };

  // ===== Модель шапки: ступени по ширине панели =====
  // Строка собирается из блоков: [☰ + имя] [бейджи + вкладки + главное действие]
  // [полоса вторичных иконок] [«···»] [якорь-выход]. Гибкое только имя — остальное
  // либо влезает целиком, либо уезжает в «···» (useToolbarOverflow).
  const tier: Tier = panelWidth === 0 || panelWidth >= 840 ? 'comfort'
    : panelWidth >= 600 ? 'cozy'
    : panelWidth >= 400 ? 'narrow'
    : 'tight';
  // Узкая ступень десктопа: главное действие — иконкой, ☰ уезжает в «···»
  const iconTier = !isMobile && (tier === 'narrow' || tier === 'tight');
  const rowGap = !isMobile && tier === 'tight' ? SP.xs : TB.gap;
  // Сколько места держим под имя файла при подсчёте влезающих кнопок
  const nameReserve = isMobile ? 90 : tier === 'tight' ? 40 : tier === 'narrow' ? 90 : 120;
  const iconBox = isMobile ? TB.iconHitMobile : TB.iconHitDesktop;
  // Бейджи-чипы (комментарии, diff) на самой узкой ступени не рисуем
  const showChips = isMobile || tier !== 'tight';

  // Имя файла: расширение отдельным span'ом — ellipsis режет хвост, а по нему как раз
  // и узнают файл (SomeVeryLong….tsx вместо SomeVeryLongComponen…)
  const dotAt = fileName.lastIndexOf('.');
  const nameBase = dotAt > 0 ? fileName.slice(0, dotAt) : fileName;
  const nameExt = dotAt > 0 ? fileName.slice(dotAt) : '';
  // --- Вкладки (включая «Просмотр | Код» для HTML) ---
  const htmlSplit = isHtml && !editing && !isOfficeFile && !fileContent?.isBinary;
  const tabValue: TabKey = htmlSplit && tab === 'file' && htmlTab === 'code' ? 'code' : tab;
  const tabOptions: { value: TabKey; label: string; title?: string; icon: ReactNode }[] = [
    htmlSplit
      ? { value: 'file', label: 'Просмотр', icon: <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> }
      : { value: 'file', label: 'Файл', icon: <File size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
    ...(htmlSplit ? [{ value: 'code' as TabKey, label: 'Код', icon: <Code size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> }] : []),
    ...(diff ? [{ value: 'diff' as TabKey, label: 'Diff', icon: <FileDiff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> }] : []),
    ...(showBlameTab ? [
      { value: 'history' as TabKey, label: 'История', icon: <History size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
      { value: 'blame' as TabKey, label: 'Кто менял', icon: <Users size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
    ] : []),
  ];
  const showTabs = !loading && !loadError && !isOfficeFile && tabOptions.length > 1;
  const activeTabOption = tabOptions.find(o => o.value === tabValue) ?? tabOptions[0];
  const selectTab = (v: TabKey) => {
    if (v === 'code') { setTab('file'); setHtmlTab('code'); return; }
    if (v === 'file') { setTab('file'); setHtmlTab('preview'); return; }
    setTab(v as ViewTab);
  };

  // --- Слот «главное действие режима»: ровно одна кнопка (+ необязательная «Отмена») ---
  // IIFE вместо let+условных присвоений в скоупе компонента: так и читаемее (один return
  // на ветку), и не путает react-compiler-lint (мутируемый let он то принимает за ref).
  const cancelEdit = () => { setEditing(false); setEditContent(content); setActionError(null); };
  const { mainAction, cancelAction } = (() => {
  let mainAction: ToolbarAction | null = null;
  let cancelAction: ToolbarAction | null = null;
  if (!loading && !loadError) {
    if (editing) {
      mainAction = {
        key: 'save', label: 'Сохранить', primary: true, disabled: !online,
        title: online ? 'Сохранить' : 'Сохранение недоступно офлайн',
        icon: <Save size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
        onClick: () => { void handleSave(); },
      };
      cancelAction = {
        key: 'cancel-edit', label: 'Отмена', title: 'Отменить правки',
        icon: <DiscardIcon />, onClick: cancelEdit,
      };
    } else if (isOfficeFile && !isVisioFile) {
      if (officeSwitching) {
        mainAction = { key: 'office-wait', label: 'Открываю…', icon: null, disabled: true, loading: true, onClick: () => {} };
      } else if (officeMode === 'view') {
        mainAction = {
          key: 'office-edit', label: 'Редактировать',
          icon: <SquarePen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
          onClick: () => { setOfficeCacheKey(undefined); setOfficeSwitching(true); setOfficeMode('edit'); },
        };
      } else {
        mainAction = {
          key: 'office-save', label: 'Сохранить', primary: true,
          icon: <Save size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
          onClick: () => { void (async () => {
            setOfficeSwitching(true);
            await api.files.officeForceSave(project.id, filePath).catch(() => {});
            setOfficeCacheKey(String(Date.now()));
            setOfficeMode('view');
          })(); },
        };
        cancelAction = {
          key: 'office-cancel', label: 'Отмена', title: 'Отменить изменения',
          icon: <DiscardIcon />, onClick: () => setOfficeDiscardDialog(true),
        };
      }
    } else if (isDrawioViewing && !isHostMode) {
      mainAction = drawioMode === 'view'
        ? {
            key: 'drawio-edit', label: 'Редактировать',
            icon: <SquarePen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
            onClick: () => setDrawioMode('edit'),
          }
        : {
            key: 'drawio-view', label: 'Просмотр', primary: true, title: 'Просмотр (правки сохраняются)',
            icon: <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
            onClick: () => { void (async () => { await drawioRef.current?.flush(); setDrawioMode('view'); })(); },
          };
    } else if (online && !isMobile && !isHostMode && !fileContent?.isBinary) {
      // На мобиле правку открывает плавающая кнопка (FAB) внизу слева
      mainAction = {
        key: 'edit', label: 'Править', primary: true,
        icon: <SquarePen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
        onClick: isHtml && htmlTab === 'preview'
          ? () => setHtmlTab('code')
          : () => { setEditing(true); setTab('file'); },
      };
    }
  }
  return { mainAction, cancelAction };
  })();
  const actionAsIcon = isMobile || iconTier;
  const actionButton = (a: ToolbarAction) => (
    <Button
      size="sm" variant={a.primary ? 'primary' : 'ghost'}
      loading={a.loading} disabled={a.disabled}
      title={a.title ?? a.label} onClick={a.onClick}
      leftIcon={a.icon} style={{ flexShrink: 0, whiteSpace: 'nowrap' }}
    >
      {a.label}
    </Button>
  );
  const actionIcon = (a: ToolbarAction) => (
    <ToolbarIconButton
      isMobile={isMobile} onClick={a.onClick} disabled={a.disabled}
      title={a.title ?? a.label} color={a.primary && !a.disabled ? C.accent : undefined}
    >
      {a.loading ? <InlineSpinner /> : a.icon}
    </ToolbarIconButton>
  );
  // Слот главного действия режима («Править»/«Сохранить»/…) — на десктопе живёт в правом
  // якоре, на мобиле в конце строки. На узких ступенях кнопка ужимается в иконку.
  const actionSlot = (mainAction || cancelAction) ? (
    actionAsIcon
      ? <>{cancelAction && actionIcon(cancelAction)}{mainAction && actionIcon(mainAction)}</>
      : <>{cancelAction && actionButton(cancelAction)}{mainAction && actionButton(mainAction)}</>
  ) : null;

  // --- Вторичные действия: одинаковые кнопки, лишние уезжают в «···» справа налево ---
  const secondary: { key: string; node: ReactNode; item: OverflowItem }[] = [];
  if (!loading) {
    // Оглавление — первым: при чтении документа к нему обращаются чаще прочих
    // вторичных действий, а в «···» уезжают последние в этом списке
    if (tocToggleVisible) {
      const tocTitle = tocPanelOpen ? 'Скрыть оглавление' : 'Оглавление документа';
      secondary.push({
        key: 'toc',
        node: (
          <ToolbarIconButton
            isMobile={isMobile} onClick={toggleTocPanel} title={tocTitle}
            color={tocPanelOpen ? C.accent : undefined}
          >
            <TableOfContents size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        ),
        item: {
          key: 'toc', icon: <TableOfContents size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          label: tocTitle, onClick: toggleTocPanel,
        },
      });
    }
    if (!loadError && tab === 'file' && isCopyableText && !isDrawio && !(isHtml && htmlTab === 'preview')) {
      const copyTitle = copied ? 'Скопировано'
        : isMarkdown ? 'Скопировать Markdown (Shift — с форматированием)' : 'Скопировать содержимое';
      secondary.push({
        key: 'copy',
        node: (
          <ToolbarIconButton isMobile={isMobile} onClick={handleCopyContent} title={copyTitle} color={copied ? C.success : undefined}>
            {copied
              ? <Check size={ICON_SIZE.sm} strokeWidth={2.5} />
              : <Copy size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          </ToolbarIconButton>
        ),
        item: {
          key: 'copy', icon: <Copy size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          label: 'Скопировать содержимое', onClick: () => { void copyContent(false); },
        },
      });
    }
    if (!loadError && online && !editing && !fileContent?.isBinary && diff) {
      secondary.push({
        key: 'revert',
        node: (
          <ToolbarIconButton isMobile={isMobile} onClick={handleRevert} title="Откатить изменения">
            <RotateCcw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        ),
        item: {
          key: 'revert', icon: <RotateCcw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
          label: 'Откатить изменения', onClick: () => { void handleRevert(); },
        },
      });
    }
    if (!loadError && online && !editing && !isHostMode) {
      const cloud = <CloudGlyph filled={syncState === 'direct'} />;
      if (pending) {
        secondary.push(syncState === 'direct'
          ? {
              key: 'sync',
              node: (
                <ToolbarIconButton isMobile={isMobile} onClick={handleToggleSync} title="Отменить синхронизацию">
                  <InlineSpinner />
                </ToolbarIconButton>
              ),
              item: { key: 'sync', icon: cloud, label: 'Отменить синхронизацию', onClick: handleToggleSync },
            }
          : {
              key: 'sync',
              node: (
                <span title="Загружается…" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: iconBox, height: iconBox, flexShrink: 0 }}>
                  <InlineSpinner />
                </span>
              ),
              item: { key: 'sync', icon: cloud, label: 'Загружается…', disabled: true },
            });
      } else if (syncState === 'inherited') {
        secondary.push({
          key: 'sync',
          node: (
            <span title="Синхронизируется через папку/проект" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: iconBox, height: iconBox, flexShrink: 0, color: C.accentMuted }}>
              <CloudGlyph filled />
            </span>
          ),
          item: { key: 'sync', icon: <CloudGlyph filled />, label: 'Синхронизируется через папку', disabled: true },
        });
      } else {
        const syncTitle = syncState === 'direct' ? 'Отключить синхронизацию' : 'Синхронизировать для офлайна';
        secondary.push({
          key: 'sync',
          node: (
            <ToolbarIconButton isMobile={isMobile} onClick={handleToggleSync} title={syncTitle} color={syncState === 'direct' ? C.accent : undefined}>
              {cloud}
            </ToolbarIconButton>
          ),
          item: { key: 'sync', icon: cloud, label: syncTitle, onClick: handleToggleSync },
        });
      }
    }
    if (!editing && fileContent?.base64) {
      secondary.push({
        key: 'download',
        node: (
          <ToolbarIconButton isMobile={isMobile} onClick={handleDownload} title="Скачать">
            <Download size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        ),
        item: { key: 'download', icon: <Download size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Скачать', onClick: handleDownload },
      });
    }
    if (online && !editing && !isHostMode) {
      secondary.push({
        key: 'delete',
        node: (
          <ToolbarIconButton isMobile={isMobile} onClick={() => setDeleteConfirm(true)} title="Удалить">
            <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        ),
        item: { key: 'delete', icon: <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Удалить', danger: true, onClick: () => setDeleteConfirm(true) },
      });
    }
  }

  // Главное действие и «Отмена» — неприкосновенный слот (часть 1, п.4): всегда видны
  // целиком на любой ширине, меняют только форму (текст → иконка). В overflow не уезжают —
  // сворачиваются только вторичные действия (Copy/Revert/Sync/Download/Delete).
  const collapsible = secondary;

  const stripRef = useRef<HTMLDivElement>(null);
  const fixedLeftRef = useRef<HTMLDivElement>(null);
  const badgesRef = useRef<HTMLDivElement>(null);
  const rightRef = useRef<HTMLDivElement>(null);
  const visibleCount = useToolbarOverflow({
    stripRef, fixedLeftRef, badgesRef, rightRef,
    count: collapsible.length,
    enabled: true,
    itemWidth: iconBox,
    gap: rowGap,
    menuWidth: iconBox,
    reserve: nameReserve,
  });
  // «···»: свёрнутый ☰ и всё, что не влезло из вторичных действий.
  // Пустое меню не рисуем — это дефект, а не состояние.
  const menuItems: OverflowItem[] = [
    ...(onOpenSidebar && !isMobile && iconTier
      ? [{ key: 'sidebar', icon: <Menu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />, label: 'Открыть панель', onClick: onOpenSidebar }]
      : []),
    ...collapsible.slice(visibleCount).map(c => c.item),
  ];

  // Вкладка текущего файла в «···»-стиле: на самой узкой ступени трек не помещается
  const [tabMenu, setTabMenu] = useState<DOMRect | null>(null);

  // Блок «бейджи + вкладки + действие» пуст (загрузка, бинарник без вкладок) — не рисуем
  // его вовсе, иначе строка получает лишний зазор в пустом месте
  const commentsChipVisible = showChips && !!commentCounts && commentCounts.total > 0 && !editing && tab === 'file';
  const diffChipVisible = showChips && !!diffStats && tab === 'diff';
  const badgesVisible = commentsChipVisible || diffChipVisible || showTabs;

  // Заметка vault — полноценный NoteView (теги, ✨-связи, перенос, правка через
  // notes-API с переименованием): тот же функционал, что в разделе «Заметки».
  // Fallback на обычный рендер ниже — пока заметка не зарезолвилась (или файл не .md).
  if (isNotesFile && isMarkdown && (noteIdOverride || noteDetail)) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgCard }}>
        <NoteView
          key={noteIdOverride ?? noteDetail!.id}
          noteId={noteIdOverride ?? noteDetail!.id}
          existingTitles={noteTitles}
          onWikilink={openWikilinkInPlace}
          onSelectNote={id => setNoteIdOverride(id)}
          onDeleted={onClose}
          isMobile={isMobile}
          onBack={isMobile ? onClose : undefined}
          extraToolbar={
            <>
              {/* Тумблер режима: иконка ЦЕЛЕВОГО состояния — из полноэкранного
                  режима заметки должен быть обратный путь в сплит */}
              {!isMobile && onToggleFullscreen && (
                <ToolbarIconButton
                  isMobile={isMobile}
                  onClick={onToggleFullscreen}
                  title={fullscreen ? 'Свернуть: сплит с чатом' : 'На весь экран'}
                >
                  {fullscreen
                    ? <Columns2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                    : <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                </ToolbarIconButton>
              )}
              {!isMobile && (
                <ToolbarIconButton isMobile={isMobile} onClick={onClose} title="Закрыть">
                  <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                </ToolbarIconButton>
              )}
            </>
          }
        />
      </div>
    );
  }

  return (
    <div ref={rootRef} style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgCard, position: 'relative' }}>
      {/* Шапка-«ступени»: [☰ + имя] [бейджи · вкладки · главное действие]
          [вторичные иконки] [«···»] [якорь-выход]. Гибкое только имя; чем уже панель,
          тем больше контролов схлопывается в иконки и уезжает в «···». Якорь
          [режим][закрыть] не сжимается и доступен при любой ширине. */}
      <Toolbar isMobile={isMobile}>
        <div ref={stripRef} style={{ display: 'flex', alignItems: 'center', gap: rowGap, flex: 1, minWidth: 0 }}>
        {/* Левый блок: на десктопе «Закрыть» + ☰ + back/forward, на мобиле «Файлы».
            Ширина входит в fixedLeftRef — useToolbarOverflow учитывает её в расчёте «···». */}
        <div ref={fixedLeftRef} style={{ display: 'flex', alignItems: 'center', gap: rowGap, flexShrink: 0 }}>
          {isMobile ? (
            <BackButton onClick={handleClose} title="К списку файлов" style={{ height: 32 }}>
              <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textSecondary }}>Файлы</span>
            </BackButton>
          ) : (
            <>
              {/* Закрыть файл — слева */}
              <ToolbarIconButton isMobile={isMobile} onClick={handleClose} title="Закрыть">
                <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </ToolbarIconButton>
              {onOpenSidebar && !iconTier && (
                <ToolbarIconButton onClick={onOpenSidebar} title="Открыть панель" isMobile={isMobile}>
                  <Menu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                </ToolbarIconButton>
              )}
              {/* Back/Forward: видимы, пока есть хотя бы одно направление навигации; неактивная — disabled */}
              {(canFileBack || canFileForward) && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 2 }}>
                  <ToolbarIconButton isMobile={isMobile} onClick={onFileBack} disabled={!canFileBack} title="Назад по истории файлов">
                    <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                  </ToolbarIconButton>
                  <ToolbarIconButton isMobile={isMobile} onClick={onFileForward} disabled={!canFileForward} title="Вперёд по истории файлов">
                    <ChevronRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                  </ToolbarIconButton>
                </div>
              )}
            </>
          )}
        </div>

        {/* Плитка расширения перед именем — как в списке файлов */}
        <FileTypeTile name={fileName} />

        {/* Имя файла — единственный гибкий элемент строки. Расширение отдельным span'ом:
            ellipsis режет хвост, а по нему и узнают файл. title — полный путь. */}
        <span title={filePath} style={{
          display: 'flex', alignItems: 'baseline', flex: '1 1 auto', minWidth: 0,
          fontFamily: FONT.mono, fontWeight: 700, fontSize: FS.base, color: C.textHeading,
        }}>
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{nameBase}</span>
          {nameExt && <span style={{ flexShrink: 0 }}>{nameExt}</span>}
        </span>

        {/* Бейджи + вкладки — несжимаемый блок */}
        {badgesVisible && (
        <div ref={badgesRef} style={{ display: 'flex', alignItems: 'center', gap: rowGap, flexShrink: 0 }}>
        {/* Комментарии к документу (флаг doc-annotations): счётчик в тулбаре */}
        {commentsChipVisible && commentCounts && (
          <span
            title={`Комментариев: ${commentCounts.total}, открытых: ${commentCounts.open}`}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0,
              fontSize: 11.5, fontWeight: 600, borderRadius: 11, padding: '1px 8px',
              color: commentCounts.open > 0 ? C.warningText : C.successText,
              background: commentCounts.open > 0 ? C.warningBg : C.successBg,
            }}>
            {commentCounts.open > 0
              ? <MessageCircle size={11} strokeWidth={2.5} />
              : <Check size={11} strokeWidth={2.5} />}
            {commentCounts.total}{commentCounts.open > 0 && !isMobile ? ` · ${commentCounts.open} откр.` : ''}
          </span>
        )}

        {/* Статистика diff — рядом со своей вкладкой, а не постоянным блоком в строке */}
        {diffChipVisible && diffStats && (
          <span style={{ display: 'flex', gap: SP.xs, flexShrink: 0 }}>
            <span style={{ fontSize: FS.sm, fontFamily: FONT.mono, color: C.success, fontWeight: 600 }}>+{diffStats.added}</span>
            <span style={{ fontSize: FS.sm, fontFamily: FONT.mono, color: C.danger, fontWeight: 600 }}>-{diffStats.removed}</span>
          </span>
        )}

        {/* Вкладки: Просмотр · Код (HTML) · Diff · История · Кто менял — один трек.
            Скрыты для Office-файлов и на время загрузки. comfort — с подписями,
            cozy/narrow — только иконки, tight — кнопка текущей вкладки + меню. */}
        {showTabs && (
          !isMobile && tier === 'tight' ? (
            <>
              <ToolbarIconButton
                isMobile={isMobile}
                active={!!tabMenu}
                title={`Вкладка: ${activeTabOption.label}`}
                onClick={e => setTabMenu(e.currentTarget.getBoundingClientRect())}
              >
                {activeTabOption.icon}
              </ToolbarIconButton>
              {tabMenu && (
                <UiMenu anchor={tabMenu} minWidth={210} maxHeight={240} onClose={() => setTabMenu(null)}>
                  {tabOptions.map(o => (
                    <MenuItem
                      key={o.value}
                      icon={o.icon}
                      onClick={() => { selectTab(o.value); setTabMenu(null); }}
                      label={
                        <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flex: 1, gap: SP.sm }}>
                          {o.label}
                          {o.value === tabValue && <Check size={ICON_SIZE.xs} strokeWidth={2.4} style={{ color: C.accent, flexShrink: 0 }} />}
                        </span>
                      }
                    />
                  ))}
                </UiMenu>
              )}
            </>
          ) : (
            <PillSwitch<TabKey>
              value={tabValue}
              options={tabOptions}
              onChange={selectTab}
              isMobile={isMobile}
              compact={isMobile}
              iconsOnly={!isMobile && tier !== 'comfort'}
            />
          )
        )}
        </div>
        )}

        {/* Вторичные действия: полоса одинаковых кнопок; что не влезло — в «···» */}
        {collapsible.slice(0, visibleCount).map(c => (
          <span key={c.key} style={{ display: 'flex', flexShrink: 0 }}>{c.node}</span>
        ))}
        {menuItems.length > 0 && (
          <ToolbarOverflowMenu isMobile={isMobile} items={menuItems} title="Ещё" />
        )}

        {/* На мобиле правого якоря нет — главное действие ставим в конце строки */}
        {isMobile && actionSlot}

        {/* Правый якорь: режим просмотра (прячется при уходе курсора) + «Править» — контрастная
            кнопка, самая правая, видна всегда. Несжимаемая группа последним ребёнком строки. */}
        {!isMobile && (
          <div ref={rightRef} style={{ display: 'flex', gap: SP.xs, alignItems: 'center', flexShrink: 0 }}>
            {/* Режим просмотра: сплит с чатом / на весь экран. Ступени: подписи →
                только иконки → тумблер-иконка. */}
            {onToggleFullscreen && (
              <span style={{ display: 'flex', alignItems: 'center' }}>
                {tier === 'tight' ? (
                  <ToolbarIconButton
                    isMobile={isMobile}
                    onClick={handleToggleMode}
                    title={fullscreen ? 'Свернуть: сплит с чатом' : 'Развернуть на весь экран'}
                  >
                    {fullscreen
                      ? <Columns2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                      : <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                  </ToolbarIconButton>
                ) : (
                  <PillSwitch
                    value={fullscreen ? 'full' : 'split'}
                    iconsOnly={tier !== 'comfort'}
                    options={[
                      { value: 'split' as const, label: 'Сплит', title: 'Сплит с чатом', icon: <Columns2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> },
                      { value: 'full' as const, label: 'Полный', title: 'На весь экран', icon: <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> },
                    ]}
                    onChange={(v) => {
                      // handleToggleMode — toggle без аргумента, поэтому клик по уже активному
                      // сегменту (напр. «Сплит», когда уже сплит) должен быть no-op, иначе toggle
                      // уведёт в противоположный режим
                      if (v === 'full' && !fullscreen) handleToggleMode();
                      if (v === 'split' && fullscreen) handleToggleMode();
                    }}
                  />
                )}
              </span>
            )}
            {/* Главное действие («Править») — контрастная, самая правая, видна всегда */}
            {actionSlot}
          </div>
        )}
        </div>{/* конец строки шапки */}
      </Toolbar>

      {/* Баннер ошибки мутации (офлайн/сбой) */}
      {actionError && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: 8,
          padding: '8px 16px', background: C.dangerBg,
          borderBottom: `1px solid ${C.dangerBorder}`,
          fontSize: 13, color: C.danger,
        }}>
          <span style={{ flexShrink: 0, color: C.danger, display: 'flex' }}><AlertTriangle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /></span>
          <span style={{ flex: 1 }}>{actionError}</span>
          <button
            onClick={() => setActionError(null)}
            style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.danger, padding: 0, flexShrink: 0, display: 'flex' }}
          ><X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /></button>
        </div>
      )}

      {/* Содержимое. Для .md (просмотр и редактирование) — белый «лист» вместо
          карточного фона; в тёмной теме bgWhite = карточный тон, глаз не режет. */}
      <div ref={contentAreaRef} style={{ flex: 1, overflow: (isOfficeFile || isCodeEditing || isPdfViewing || isHtmlPreviewing || isDrawioViewing) ? 'hidden' : 'auto', padding: (isOfficeFile || isCodeEditing || isPdfViewing || isHtmlPreviewing || isDrawioViewing) ? 0 : 16, display: 'flex', flexDirection: 'column', background: (isMarkdown && tab === 'file') ? C.bgWhite : undefined }}>
        {loading && (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', flex: 1, gap: 14 }}>
            <div style={{ width: 36, height: 36, borderRadius: '50%', border: `3px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.8s linear infinite' }} />
            <div style={{ fontSize: 13, color: C.textMuted }}>Загружаю файл…</div>
          </div>
        )}

        {!loading && loadError && loadForbidden && (
          <EmptyState
            icon={<File size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Файл вне досягаемости песочницы"
            subtitle={filePath}
          />
        )}

        {!loading && loadError && !loadForbidden && (
          <EmptyState
            icon={<File size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title={online ? 'Не удалось открыть файл' : 'Файл не синхронизирован'}
            subtitle={online
              ? `Не удалось загрузить ${fileName}`
              : 'Этот файл не сохранён для офлайна. Включите синхронизацию, когда будете онлайн.'}
          />
        )}

        {!loading && !loadError && tab === 'file' && (
          <>
            {fileContent?.isImage && fileContent.base64 && (
              <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, padding: 16 }}>
                <img
                  src={`data:${fileContent.mimeType};base64,${fileContent.base64}`}
                  onLoad={e => setImgDims({ w: e.currentTarget.naturalWidth, h: e.currentTarget.naturalHeight })}
                  style={{ maxWidth: '100%', borderRadius: 8, boxShadow: SHADOW.card }}
                  alt={fileName}
                />
                {/* Метаданные изображения: тип · размеры · вес */}
                <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.mono, display: 'flex', gap: 7, flexWrap: 'wrap', justifyContent: 'center' }}>
                  <span>{(fileContent.mimeType?.split('/')[1] ?? fileName.split('.').pop() ?? '').toUpperCase()}</span>
                  {imgDims && <><span style={{ opacity: 0.5 }}>·</span><span>{imgDims.w}×{imgDims.h}</span></>}
                  {fileSizeMb && <><span style={{ opacity: 0.5 }}>·</span><span>{fileSizeMb} МБ</span></>}
                </div>
              </div>
            )}

            {/* Стрим — проектный эндпоинт (project.id + путь): для хост-файла вне
                проекта не сработает, показываем как обычный бинарник со скачиванием */}
            {fileContent?.isVideo && !isHostMode && (
              <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, padding: 16 }}>
                <video
                  controls
                  style={{ maxWidth: '100%', borderRadius: 8, boxShadow: SHADOW.card }}
                >
                  <source src={streamUrl(project.id, filePath)} type={fileContent.mimeType} />
                </video>
                <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.mono, display: 'flex', gap: 7, flexWrap: 'wrap', justifyContent: 'center' }}>
                  <span>{(fileContent.mimeType?.split('/')[1] ?? fileName.split('.').pop() ?? '').toUpperCase()}</span>
                  {fileSizeMb && <><span style={{ opacity: 0.5 }}>·</span><span>{fileSizeMb} МБ</span></>}
                </div>
              </div>
            )}

            {fileContent?.isAudio && !isHostMode && (
              isMobile
                ? (
                  <div style={{ display: 'flex', justifyContent: 'center', padding: 20 }}>
                    <AudioFilePlayer
                      src={streamUrl(project.id, filePath)}
                      mimeType={fileContent.mimeType}
                      fileName={fileName}
                      fileSizeMb={fileSizeMb}
                    />
                  </div>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, padding: 24 }}>
                    <div style={{
                      background: C.bgPanel, borderRadius: 14, border: `1px solid ${C.border}`,
                      padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: 12,
                      width: '100%', maxWidth: 440, boxShadow: SHADOW.card,
                    }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                        <div style={{
                          width: 40, height: 40, borderRadius: 10, background: C.accent,
                          display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
                        }}>
                          <Music size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} color={C.onAccent} />
                        </div>
                        <span style={{ fontFamily: FONT.mono, fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1 }}>
                          {fileName}
                        </span>
                      </div>
                      <div style={{ borderRadius: 8, overflow: 'hidden' }}>
                        <audio controls style={{ width: '100%', height: 40, outline: 'none', display: 'block' }}>
                          <source src={streamUrl(project.id, filePath)} type={fileContent.mimeType} />
                        </audio>
                      </div>
                      <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.mono, display: 'flex', gap: 6 }}>
                        <span>{(fileContent.mimeType?.split('/')[1] ?? fileName.split('.').pop() ?? '').toUpperCase()}</span>
                        {fileSizeMb && <><span style={{ opacity: 0.4 }}>·</span><span>{fileSizeMb} МБ</span></>}
                      </div>
                    </div>
                  </div>
                )
            )}

            {/* PDF — клиентский рендеринг через pdf.js */}
            {fileContent?.isDocument && fileContent.docKind === 'pdf' && (
              fileContent.base64
                ? <DocumentViewer base64={fileContent.base64} />
                : <EmptyState
                    icon={<File size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
                    title="Документ слишком большой"
                    subtitle={`${fileName}${fileSizeMb ? ` — ${fileSizeMb} МБ` : ''}. Просмотр недоступен для файлов больше 25 МБ.`}
                  />
            )}

            {/* Office-файлы (docx/xlsx/pptx) — через OnlyOffice Document Server (проектный
                эндпоинт); для хост-файла вне проекта не сработает — только скачивание */}
            {fileContent?.isDocument && fileContent.docKind !== 'pdf' && !isHostMode && (
              <div style={{ position: 'relative', width: '100%', height: '100%' }}>
                <OfficeViewer
                  key={`${filePath}-${officeMode}-${officeCacheKey ?? ''}`}
                  projectId={project.id}
                  filePath={filePath}
                  mode={officeMode}
                  cacheKey={officeCacheKey}
                  onReady={() => setOfficeSwitching(false)}
                />
                {officeSwitching && (
                  <div style={{ position: 'absolute', inset: 0, background: C.bgMain, display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 10 }}>
                    <span style={{ width: 32, height: 32, borderRadius: '50%', border: `3px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.7s linear infinite' }} />
                  </div>
                )}
              </div>
            )}

            {isHostMode && fileContent && (fileContent.isVideo || fileContent.isAudio || (fileContent.isDocument && fileContent.docKind !== 'pdf')) && (
              <EmptyState
                icon={<File size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
                title="Просмотр недоступен вне проекта"
                subtitle={`${fileName}${fileSizeMb ? ` — ${fileSizeMb} МБ` : ''}`}
                action={
                  fileContent.base64 ? (
                    <button onClick={handleDownload} style={{ ...btnPrimary, padding: '8px 16px' }}>
                      Скачать
                    </button>
                  ) : undefined
                }
              />
            )}

            {fileContent?.isBinary && !fileContent.isImage && !fileContent.isVideo && !fileContent.isAudio && !fileContent.isDocument && (
              <EmptyState
                icon={<File size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
                title="Нельзя показать"
                subtitle={`${fileName} — бинарный файл${fileSizeMb ? `, ${fileSizeMb} МБ` : ''}`}
                action={
                  fileContent.base64 ? (
                    <button onClick={handleDownload} style={{ ...btnPrimary, padding: '8px 16px' }}>
                      Скачать
                    </button>
                  ) : undefined
                }
              />
            )}

            {!fileContent?.isBinary && !fileContent?.isImage && (
              editing
                ? (
                  <Suspense fallback={
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', gap: 10, color: C.textMuted, fontSize: 13 }}>
                      <div style={{ width: 20, height: 20, borderRadius: '50%', border: `2.5px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.7s linear infinite' }} />
                      Загрузка редактора…
                    </div>
                  }>
                    {isNotesFile && isMarkdown ? (
                      <NoteEditor
                        key={filePath}
                        value={editContent}
                        onChange={setEditContent}
                        onWikilink={openNoteByTitle}
                        fill
                      />
                    ) : (
                      <CodeEditor
                        key={filePath}
                        value={editContent}
                        onChange={setEditContent}
                        filePath={filePath}
                      />
                    )}
                  </Suspense>
                )
                : isDrawio
                  ? <DrawioViewer ref={drawioRef} key={drawioMode} content={content} mode={drawioMode} onSave={handleDrawioSave} />
                : isHtml && htmlTab === 'preview'
                  ? <iframe
                      srcDoc={content}
                      sandbox="allow-scripts allow-forms allow-popups allow-modals"
                      style={{ width: '100%', height: '100%', border: 'none', display: 'block' }}
                      title={fileName}
                    />
                  : isMermaid
                  ? <div style={{ padding: 16 }}><MermaidDiagram code={content} /></div>
                  : isMarkdown && isNotesFile
                  ? (
                    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 18 }}>
                      <div style={{ flex: 1, minWidth: 0 }} data-selection-scope="doc" data-selection-priority="2">
                        <MarkdownViewer content={content}
                          existingTitles={noteTitles} onWikilink={openNoteByTitle}
                          resolveNote={resolveNoteByName} embedSource={project.id} />
                        {noteDetail && isMobile && (
                          <div style={{ marginTop: 20, borderTop: `1px solid ${C.border}`, paddingTop: 12 }}>
                            <NoteConnections note={noteDetail} onOpenNote={openNoteById}
                              onWikilink={openNoteByTitle} />
                          </div>
                        )}
                      </div>
                      {/* Связи заметки — сайдбар справа (sticky в скролле), на мобиле — снизу */}
                      {noteDetail && !isMobile && (
                        <aside style={{
                          width: 270, flex: 'none', position: 'sticky', top: 0,
                          maxHeight: 'calc(100vh - 160px)', overflowY: 'auto',
                          borderLeft: `1px solid ${C.border}`, paddingLeft: 14,
                        }}>
                          <NoteConnections note={noteDetail} onOpenNote={openNoteById}
                            onWikilink={openNoteByTitle} />
                        </aside>
                      )}
                    </div>
                  )
                  : isMarkdown && isHostMode
                  // Хост-режим: без комментариев к документу и резолва картинок —
                  // обе фичи проектные (scope=project.id), для файла вне проекта не годятся
                  ? <div data-selection-scope="doc" data-selection-priority="2"><MarkdownViewer content={content} onDocLink={handleDocLink} /></div>
                  : isMarkdown
                  ? <div data-selection-scope="doc" data-selection-priority="2"><DocCommentedMarkdown
                      scope={project.id} docPath={filePath} content={content} isMobile={isMobile}
                      onCounts={onCommentCounts}
                      // Логотип и скриншоты README лежат рядом в репозитории: путь в src
                      // относителен документа, грузить их надо через файловый эндпоинт.
                      // onDocLink — переход по md-ссылкам внутри файла (другой файл/якорь),
                      // иначе клик уводил бы браузер из SPA на главный экран
                      viewer={{ onDocLink: handleDocLink, resolveImageSrc: src => {
                        const target = resolveDocImage(filePath, src);
                        return target ? api.files.fileUrl(project.id, target) : undefined;
                      } }}
                    /></div>
                  : <div data-selection-scope="doc" data-selection-priority="2"><SyntaxHighlighter
                      language={getLanguage(filePath)}
                      style={codeTheme}
                      customStyle={{ margin: 0, padding: 0, background: 'transparent', fontSize: 13, lineHeight: '1.6', fontFamily: FONT.mono }}
                      codeTagProps={{ style: { fontFamily: FONT.mono } }}
                      showLineNumbers
                      lineNumberStyle={{ minWidth: '2.6em', paddingRight: '1.1em', textAlign: 'right', color: C.textMuted, userSelect: 'none' }}
                      wrapLongLines
                    >
                      {content}
                    </SyntaxHighlighter></div>
            )}
          </>
        )}

        {!loading && !loadError && tab === 'diff' && (
          diff
            ? <div data-selection-scope="doc" data-selection-priority="2"><DiffView
                diff={diff}
                staging={gitStagePath ? { busy: stageBusy, onStageHunk: handleStageHunk, onStageLines: handleStageLines } : undefined}
              /></div>
            : <div style={{ color: C.textMuted, fontSize: 13, padding: 16 }}>Файл не изменён</div>
        )}

        {/* Вкладка «Кто менял» — blame по строкам (lazy, кэш до смены файла) */}
        {!loading && !loadError && tab === 'blame' && (
          blameLoading ? (
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', flex: 1, gap: 10, color: C.textMuted, fontSize: 13 }}>
              <div style={{ width: 20, height: 20, borderRadius: '50%', border: `2.5px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.7s linear infinite' }} />
              Загружаю авторство…
            </div>
          ) : blame && blame.length > 0 ? (
            <BlameView lines={blame} />
          ) : (
            <div style={{ color: C.textMuted, fontSize: 13, padding: 16 }}>Авторство недоступно</div>
          )
        )}

        {/* Вкладка «История» — версии файла: лента сверху, diff выбранной ниже */}
        {!loading && !loadError && tab === 'history' && (
          fileLogLoading ? (
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', flex: 1, gap: 10, color: C.textMuted, fontSize: 13 }}>
              <div style={{ width: 20, height: 20, borderRadius: '50%', border: `2.5px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.7s linear infinite' }} />
              Загружаю историю…
            </div>
          ) : !fileLog || fileLog.length === 0 ? (
            <div style={{ color: C.textMuted, fontSize: 13, padding: 16 }}>У файла пока нет сохранённых версий</div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
              {/* Лента версий */}
              <div style={{ maxHeight: isMobile ? '38%' : '34%', overflowY: 'auto', borderBottom: `1px solid ${C.border}`, padding: '6px 8px', flexShrink: 0 }}>
                {fileLog.map(v => {
                  const active = v.sha === versionSha;
                  return (
                    <div
                      key={v.sha}
                      onClick={() => setVersionSha(v.sha)}
                      style={{
                        display: 'flex', alignItems: 'center', gap: 8, padding: '5px 8px', cursor: 'pointer',
                        borderRadius: 8, background: active ? C.bgSelected : 'transparent',
                      }}
                    >
                      <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.accent, background: C.accentLight, padding: '1px 6px', borderRadius: 4, flexShrink: 0 }}>{v.shortSha}</span>
                      <span style={{ flex: 1, minWidth: 0, fontSize: 12.5, color: active ? C.textHeading : C.textPrimary, fontWeight: active ? 600 : 400, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }} title={v.subject}>{v.subject}</span>
                      <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0 }}>{v.author} · {relTime(v.date)}</span>
                      {active && (
                        <button
                          onClick={e => { e.stopPropagation(); setRestoreConfirmSha(v.sha); }}
                          style={{
                            flexShrink: 0, padding: '3px 9px', borderRadius: 6, cursor: 'pointer',
                            border: `1px solid ${C.accent}`, background: C.accentLight, color: C.accent,
                            fontSize: 11.5, fontWeight: 600,
                          }}
                        >
                          Вернуть эту версию
                        </button>
                      )}
                    </div>
                  );
                })}
              </div>
              {/* Вид версии: изменения (diff) / файл целиком «как был» */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 4, padding: '6px 10px 0', flexShrink: 0 }}>
                {(['diff', 'content'] as const).map(v => (
                  <button
                    key={v}
                    onClick={() => setVersionView(v)}
                    style={{
                      padding: '3px 10px', borderRadius: 999, cursor: 'pointer', fontSize: 11.5, fontWeight: 600,
                      border: `1px solid ${versionView === v ? C.accent : C.border}`,
                      background: versionView === v ? C.accentLight : 'transparent',
                      color: versionView === v ? C.accent : C.textSecondary, fontFamily: FONT.sans,
                    }}
                  >
                    {v === 'diff' ? 'Изменения' : 'Как было'}
                  </button>
                ))}
              </div>
              <div style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
                {versionView === 'diff' ? (
                  versionDiffLoading ? (
                    <div style={{ color: C.textMuted, fontSize: 13, padding: 16, fontFamily: FONT.mono }}>Загрузка…</div>
                  ) : versionDiff ? (
                    <DiffView diff={versionDiff} />
                  ) : (
                    <div style={{ color: C.textMuted, fontSize: 13, padding: 16 }}>Изменений файла в этой версии не найдено</div>
                  )
                ) : (
                  versionContentLoading || versionContent === null ? (
                    <div style={{ color: C.textMuted, fontSize: 13, padding: 16, fontFamily: FONT.mono }}>Загрузка…</div>
                  ) : versionContent === '' ? (
                    <div style={{ color: C.textMuted, fontSize: 13, padding: 16 }}>Содержимое недоступно (бинарный файл?)</div>
                  ) : (
                    <pre style={{
                      margin: 0, padding: '10px 14px', fontFamily: FONT.mono, fontSize: 12.5,
                      color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', lineHeight: 1.55,
                    }}>{versionContent}</pre>
                  )
                )}
              </div>
            </div>
          )
        )}
      </div>

      {/* Плавающая кнопка редактирования на мобиле (MA4). ЛЕВЫЙ нижний угол — правый занят
          глобальным AiLauncher (⌘/Ctrl+K), чтобы кнопки не накладывались. */}
      {isMobile && online && !editing && !isHostMode && tab === 'file' && fileContent && !fileContent.isBinary && !fileContent.isImage && !fileContent.isDocument && !fileContent.isVideo && !fileContent.isAudio && !isDrawio && !(isHtml && htmlTab === 'preview') && (
        <button
          onClick={() => { setEditing(true); setTab('file'); }}
          title="Редактировать"
          style={{
            position: 'absolute', left: 18, bottom: 18, width: 52, height: 52, borderRadius: '50%',
            border: 'none', background: C.accent, color: C.onAccent, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: SHADOW.fab, zIndex: 20,
          }}
        >
          <SquarePen size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />
        </button>
      )}

      {/* Подтверждение возврата файла к версии из «Истории» */}
      {restoreConfirmSha && (
        <Modal
          width={MODAL_W.confirm}
          onClose={() => { if (!restoring) setRestoreConfirmSha(null); }}
          title="Вернуть эту версию файла"
          subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{fileName}</span>}
          footer={
            <ModalActions
              confirmLabel={restoring ? 'Возвращаю…' : 'Вернуть'}
              confirmDisabled={restoring}
              onConfirm={handleRestoreVersion}
              onCancel={() => setRestoreConfirmSha(null)}
            />
          }
        >
          <div style={{ fontSize: 13, color: C.textPrimary, lineHeight: 1.5 }}>
            Файл станет таким, каким был в версии {fileLog?.find(v => v.sha === restoreConfirmSha)?.shortSha ?? restoreConfirmSha.slice(0, 7)}.
            {docMode
              ? ' Возврат сразу сохранится в историю — его можно отменить тем же способом.'
              : ' Возврат появится в «Изменениях» — зафиксируйте его коммитом или отмените.'}
          </div>
          {gitSt.error && <div style={{ marginTop: 8, fontSize: 12.5, color: C.dangerText }}>{gitSt.error}</div>}
        </Modal>
      )}

      {/* Результат ИИ по документу (краткое содержание / выжимка / теги / markdown) */}
      {docAi && (
        <Modal
          width={MODAL_W.form}
          onClose={() => setDocAi(null)}
          title={docAi.title}
          subtitle={<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{fileName}</span>}
        >
          <div style={{ maxHeight: '60vh', overflowY: 'auto' }}>
            <MarkdownViewer content={docAi.markdown} />
          </div>
        </Modal>
      )}

      {/* Диалог удаления */}
      {deleteConfirm && (
        <Modal
          title="Удалить файл?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteConfirm(false)}
          subtitle={
            <>
              Файл <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{fileName}</span> будет удалён без возможности восстановления.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleDelete}
              onCancel={() => setDeleteConfirm(false)}
            />
          }
        />
      )}

      {/* Подтверждение отката office-правок — раньше на десктопе была inline-плашка
          в тулбаре, теперь единый ConfirmDialog (на мобиле сам становится шторкой) */}
      {officeDiscardDialog && (
        <ConfirmDialog
          title="Отменить изменения?"
          subtitle="Несохранённые правки будут потеряны."
          confirmLabel="Отменить правки"
          confirmVariant="danger"
          onConfirm={async () => {
            setOfficeDiscardDialog(false);
            setOfficeSwitching(true);
            try { await api.files.officeDiscard(project.id, filePath); } catch {}
            setOfficeMode('view');
          }}
          onCancel={() => setOfficeDiscardDialog(false)}
        />
      )}

      {/* Диалог несохранённых изменений (три исхода: сохранить / не сохранять / остаться) */}
      {unsavedConfirm && (
        <Modal
          title="Сохранить изменения?"
          width={MODAL_W.confirm}
          onClose={() => setUnsavedConfirm(false)}
          subtitle={
            <>
              В файле <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{fileName}</span> есть несохранённые правки.
            </>
          }
          footer={
            <UnsavedActions
              onSave={handleSaveAndClose}
              onDiscard={handleCloseWithoutSave}
              onCancel={() => setUnsavedConfirm(false)}
            />
          }
        />
      )}
    </div>
  );
}

// Вкладка «Авторы»: строки файла с колонкой авторства слева. Подряд идущие строки
// одного коммита группируются (sha/автор — только у первой, как на GitHub),
// группы различаются чередующимся фоном.
function BlameView({ lines }: { lines: GitBlameLine[] }) {
  const rows = useMemo(() => {
    let group = -1;
    let prevSha = '';
    return lines.map(l => {
      const first = l.sha !== prevSha;
      if (first) { group++; prevSha = l.sha; }
      return { l, first, group };
    });
  }, [lines]);
  return (
    <div style={{ fontFamily: FONT.mono, fontSize: 12, lineHeight: '1.55' }}>
      {rows.map(({ l, first, group }) => (
        <div key={l.line} style={{ display: 'flex', alignItems: 'flex-start', background: group % 2 === 0 ? C.bgMain : C.bgCard }}>
          <span
            title={first ? `${l.shortSha} · ${l.author} · ${relTime(l.date)}` : undefined}
            style={{
              width: 170, flexShrink: 0, display: 'flex', alignItems: 'baseline', gap: 6,
              padding: '0 8px', overflow: 'hidden', whiteSpace: 'nowrap',
              borderRight: `1px solid ${C.border}`,
            }}
          >
            {first && (
              <>
                <span style={{ color: C.accent, flexShrink: 0 }}>{l.shortSha}</span>
                <span style={{ color: C.textSecondary, fontFamily: FONT.sans, fontSize: 11, overflow: 'hidden', textOverflow: 'ellipsis', flex: 1, minWidth: 0 }}>{l.author}</span>
                <span style={{ color: C.textMuted, fontFamily: FONT.sans, fontSize: 10, flexShrink: 0 }}>{relTime(l.date)}</span>
              </>
            )}
          </span>
          <span style={{ width: 40, textAlign: 'right', padding: '0 7px', color: C.textMuted, userSelect: 'none', flexShrink: 0 }}>{l.line}</span>
          <span style={{ flex: 1, whiteSpace: 'pre-wrap', wordBreak: 'break-all', color: C.textHeading, paddingRight: 10 }}>{l.content || ' '}</span>
        </div>
      ))}
    </div>
  );
}

// Три исхода для диалога несохранённых изменений: сохранить / не сохранять / остаться.
//  • Десктоп: один ряд (Отмена · Не сохранять · Сохранить), основное справа.
//  • Мобила (шторка): «Сохранить» отдельной строкой-акцентом сверху, ниже в ряд «Не сохранять» и «Отмена» — компактно по вертикали.
function UnsavedActions({ onSave, onDiscard, onCancel }: {
  onSave: () => void; onDiscard: () => void; onCancel: () => void;
}) {
  const isMobile = useIsMobileModal();
  const save = <Button variant="primary" size="md" fullWidth onClick={onSave}>Сохранить</Button>;
  const discard = <Button variant="ghost" size="md" fullWidth onClick={onDiscard}>Не сохранять</Button>;
  const cancel = <Button variant="secondary" size="md" fullWidth onClick={onCancel}>Отмена</Button>;
  if (isMobile) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10, width: '100%' }}>
        {save}
        <div style={{ display: 'flex', gap: 10 }}>
          <div style={{ flex: 1 }}>{cancel}</div>
          <div style={{ flex: 1 }}>{discard}</div>
        </div>
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', gap: 10, width: '100%' }}>
      {cancel}{discard}{save}
    </div>
  );
}
