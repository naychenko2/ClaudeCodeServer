import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, X, File, Trash2, Maximize2, RotateCcw, Save, Download, Music, Menu, SquarePen, Eye, Copy, Check, FileDiff, History, Users, MessageCircle } from 'lucide-react';
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
import { OfflineError } from '../lib/offline';
import { useGitState, ensureGit, gitRestoreFile, loadGitRemote } from '../lib/git';
import { parseDiffToHunks, buildHunkPatch, buildLinesPatch } from '../lib/gitPatch';
import { relTime } from './GitPanel';
import { toggleSyncMark, useSyncMarks, computeSyncState, isDownloaded, loadSyncMarks, loadDownloadedSet } from '../lib/sync';
import { onFilesChanged } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
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
import { C, FONT, MODAL_W, SHADOW } from '../lib/design';
import { Toolbar, ToolbarIconButton, PillSwitch, tbBtnPrimary, tbBtnGhost } from './Toolbar';
import { BackButton, Modal, ModalActions, Button, ConfirmDialog, useIsMobileModal } from './ui';
import { DiffView } from './DiffView';
import { registerCopyDoc, copyMarkdown, copyRenderedHtml } from '../lib/selectionScope';
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
  isMobile?: boolean;
  onOpenSidebar?: () => void;
  // Стартовая вкладка: 'diff' — открытие из git-панели «Изменения»
  initialTab?: 'file' | 'diff';
  // Путь файла, открытого из git-«Изменений» как unstaged: включает зернистый stage
  // хунков/строк на diff-вкладке (diff при этом — worktree против индекса)
  gitStagePath?: string;
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

export function FileViewer({ project, filePath, onClose, onToggleFullscreen, isMobile, onOpenSidebar, initialTab, gitStagePath }: Props) {
  const online = useOnline();
  // Заметки vault (notes/*.md): рендерим [[wikilinks]] и уводим по клику в раздел «Заметки»
  const allNotes = useNotes();
  const isNotesFile = /(^|\/)notes\//i.test(filePath);
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
  const [officeDiscardConfirm, setOfficeDiscardConfirm] = useState(false);
  // Мобильный вариант подтверждения отката office-правок — диалог (на десктопе — инлайн-плашка)
  const [officeDiscardDialog, setOfficeDiscardDialog] = useState(false);
  const [officeCacheKey, setOfficeCacheKey] = useState<string | undefined>();
  // Режим draw.io: по умолчанию просмотр (read-only), кнопка «Редактировать» → edit
  const [drawioMode, setDrawioMode] = useState<'view' | 'edit'>('view');
  const [editContent, setEditContent] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const [unsavedConfirm, setUnsavedConfirm] = useState(false);
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

  const content = fileContent?.content ?? '';
  const hasUnsavedChanges = editing && editContent !== content;
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
    setOfficeDiscardConfirm(false);
    setOfficeCacheKey(undefined);
    setDrawioMode('view');
    setLoading(true);
    setLoadError(false);
    setFileContent(null);
    setImgDims(null);
    setActionError(null);
    setBlame(null);
    setBlameError(false);
    setFileLog(null);
    setVersionSha(null);
    setVersionDiff(null);
    setRestoreConfirmSha(null);
    api.files.getContent(project.id, filePath).then(r => {
      setFileContent(r);
      setEditContent(r.content ?? '');
    }).catch(() => setLoadError(true)).finally(() => setLoading(false));
    // diff недоступен офлайн — мягко игнорируем ошибку
    fetchDiff().then(r => setDiff(r.diff)).catch(() => setDiff(null));
  }, [project.id, filePath, gitStagePath]);

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

  // Метки синхронизации + набор скачанных файлов — в общий стор (синхронно с деревом)
  useEffect(() => {
    loadSyncMarks(project.id);
    loadDownloadedSet(project.id);
  }, [project.id]);

  // Watcher: открытый файл изменился на диске → перечитываем (если не редактируем — не затираем правки)
  useEffect(() => {
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
  }, [project.id, filePath, editing, drawioMode, gitStagePath]);

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
    // Разрешаем документы (pdf/docx/…) и текстовые файлы; блокируем прочие бинарные и повторный клик
    if (docAiBusy || !fileContent || (fileContent.isBinary && !fileContent.isDocument)) return;
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
      else if (a === 'file.toMarkdown') void (async () => {
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
      setUnsavedConfirm(true);
    } else {
      onClose();
    }
  };

  const handleCloseWithoutSave = () => {
    setUnsavedConfirm(false);
    onClose();
  };

  const handleSaveAndClose = async () => {
    setUnsavedConfirm(false);
    // Закрываем только при успешном сохранении — иначе правки потеряются (офлайн/сбой)
    const ok = await handleSave();
    if (ok) onClose();
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

  const fileName = filePath.split('/').pop() ?? filePath;
  const isMarkdown = /\.(md|mdx)$/i.test(fileName);
  // Текстовый файл, содержимое которого можно скопировать целиком
  const isCopyableText = !!fileContent && !fileContent.isBinary && !fileContent.isImage
    && !fileContent.isDocument && !fileContent.isVideo && !fileContent.isAudio;

  // Ctrl+C без выделения: отдаём исходник открытого текстового файла (см. selectionScope)
  const copySourceRef = useRef<() => string | null>(() => null);
  copySourceRef.current = () => (isCopyableText ? (fileContent?.content ?? null) : null);
  useEffect(() => {
    const el = contentAreaRef.current;
    if (!el) return;
    return registerCopyDoc(el, () => copySourceRef.current());
  }, []);

  // Клик — скопировать исходник (raw markdown/код); Shift+клик по .md — с форматированием
  const handleCopyContent = async (e: React.MouseEvent) => {
    const raw = editing ? editContent : content;
    const rendered = e.shiftKey && isMarkdown && !editing
      ? contentAreaRef.current?.querySelector<HTMLElement>('[data-selection-scope]')
      : null;
    const ok = rendered ? await copyRenderedHtml(rendered) : await copyMarkdown(raw);
    if (ok) { setCopied(true); setTimeout(() => setCopied(false), 1500); }
  };
  const isMermaid = /\.mmd$/i.test(fileName);
  const isHtml = /\.html?$/i.test(fileName);
  const isDrawio = /\.(drawio|dio)$/i.test(fileName);
  const diffStats = diff ? {
    added: diff.split('\n').filter(l => l.startsWith('+') && !l.startsWith('+++')).length,
    removed: diff.split('\n').filter(l => l.startsWith('-') && !l.startsWith('---')).length,
  } : null;
  // Вкладка «Авторы» (blame) — только для текстовых файлов в git-репо
  const showBlameTab = inRepo && !loading && !loadError && !!fileContent && !fileContent.isBinary && !fileContent.isImage;
  const fileSizeMb = fileContent?.fileSize != null ? (fileContent.fileSize / 1024 / 1024).toFixed(2) : null;

  const btnPrimary: React.CSSProperties = {
    border: 'none', background: C.accent, color: C.onAccent,
    borderRadius: 8, padding: '5px 13px', cursor: 'pointer', fontSize: 13, fontWeight: 600,
  };

  const isOfficeFile = !loading && !loadError && tab === 'file' && !!fileContent?.isDocument && fileContent.docKind !== 'pdf';
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
              {!isMobile && onToggleFullscreen && (
                <ToolbarIconButton isMobile={isMobile} onClick={onToggleFullscreen} title="На весь экран">
                  <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
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
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgCard, position: 'relative' }}>
      {/* Шапка */}
      <Toolbar isMobile={isMobile}>
        {/* Кнопка открытия сайдбара — только когда он свёрнут (не на мобиле) */}
        {onOpenSidebar && !isMobile && (
          <ToolbarIconButton onClick={onOpenSidebar} title="Открыть панель" isMobile={isMobile}>
            <Menu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        )}
        {/* Кнопка назад — только на мобиле */}
        {isMobile && (
          <BackButton onClick={handleClose} title="К списку файлов" style={{ height: 32 }}>
            <span style={{ fontSize: 13, fontWeight: 600, color: C.textSecondary }}>Файлы</span>
          </BackButton>
        )}

        {/* Имя файла */}
        <span style={{ fontFamily: FONT.mono, fontWeight: 700, fontSize: 13, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: C.textHeading }}>
          {fileName}
        </span>

        {/* Комментарии к документу (флаг doc-annotations): счётчик в тулбаре */}
        {commentCounts && commentCounts.total > 0 && !editing && tab === 'file' && (
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

        {/* Статистика diff */}
        {diffStats && (
          <span style={{ display: 'flex', gap: 4, flexShrink: 0 }}>
            <span style={{ fontSize: 12, fontFamily: FONT.mono, color: C.success, fontWeight: 600 }}>+{diffStats.added}</span>
            <span style={{ fontSize: 12, fontFamily: FONT.mono, color: C.danger, fontWeight: 600 }}>-{diffStats.removed}</span>
          </span>
        )}

        {/* Pill-переключатель Файл / Diff / История / Кто менял — скрыт для Office-файлов;
            Diff — когда файл изменён, «История» и «Кто менял» — когда проект в git-репо.
            На мобиле — компакт (только иконки) */}
        {!isOfficeFile && (!!diff || showBlameTab) && (
          <PillSwitch<ViewTab>
            value={tab}
            options={[
              { value: 'file', label: 'Файл', icon: <File size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
              ...(diff ? [{ value: 'diff' as ViewTab, label: 'Diff', icon: <FileDiff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> }] : []),
              ...(showBlameTab ? [{ value: 'history' as ViewTab, label: 'История', icon: <History size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> }] : []),
              ...(showBlameTab ? [{ value: 'blame' as ViewTab, label: 'Кто менял', icon: <Users size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> }] : []),
            ]}
            onChange={setTab}
            isMobile={isMobile}
            compact={isMobile}
          />
        )}

        {/* Переключатель Просмотр / Код для HTML-файлов */}
        {isHtml && !editing && tab === 'file' && !fileContent?.isBinary && (
          <PillSwitch<'preview' | 'code'>
            value={htmlTab}
            options={[{ value: 'preview', label: 'Просмотр' }, { value: 'code', label: 'Код' }]}
            onChange={v => { setHtmlTab(v); if (v === 'code') setEditing(false); }}
            isMobile={isMobile}
          />
        )}

        {/* Переключатель просмотр/редактирование для draw.io */}
        {isDrawioViewing && (
          drawioMode === 'view' ? (
            <button
              title="Редактировать"
              onClick={() => setDrawioMode('edit')}
              style={{ display: 'flex', alignItems: 'center', gap: 6, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.border}`, background: C.bgPanel, color: C.textHeading, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
            >
              <SquarePen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, opacity: 0.7 }} />
              {!isMobile && <span>Редактировать</span>}
            </button>
          ) : (
            <button
              title="Просмотр (правки сохраняются)"
              onClick={async () => { await drawioRef.current?.flush(); setDrawioMode('view'); }}
              style={{ display: 'flex', alignItems: 'center', gap: 6, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.accent}`, background: C.accent, color: C.onAccent, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
            >
              <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
              {!isMobile && <span>Просмотр</span>}
            </button>
          )
        )}

        {/* Переключатель режима просмотра/редактирования для Office-файлов (Visio — только просмотр) */}
        {isOfficeFile && !isVisioFile && (
          officeSwitching ? (
            // Загрузка
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.border}`, background: C.border, color: C.textMuted, fontSize: 13, fontWeight: 500, whiteSpace: 'nowrap', pointerEvents: 'none' }}>
              <span style={{ width: 13, height: 13, borderRadius: '50%', border: `2px solid ${C.dashed}`, borderTopColor: C.textMuted, animation: 'spin 0.7s linear infinite', flexShrink: 0 }} />
              {!isMobile && <span>Открываю…</span>}
            </div>
          ) : officeMode === 'view' ? (
            // Кнопка «Редактировать»
            <button
              title="Редактировать"
              onClick={() => { setOfficeCacheKey(undefined); setOfficeSwitching(true); setOfficeMode('edit'); }}
              style={{ display: 'flex', alignItems: 'center', gap: 6, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.border}`, background: C.bgPanel, color: C.textHeading, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
            >
              <SquarePen size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, opacity: 0.7 }} />
              {!isMobile && <span>Редактировать</span>}
            </button>
          ) : officeDiscardConfirm ? (
            // Подтверждение отмены изменений
            <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
              {!isMobile && <span style={{ fontSize: 12, color: C.textSecondary, whiteSpace: 'nowrap', padding: '0 4px' }}>Отменить изменения?</span>}
              <button
                title="Откатить изменения"
                onClick={async () => {
                  setOfficeDiscardConfirm(false);
                  setOfficeSwitching(true);
                  try { await api.files.officeDiscard(project.id, filePath); } catch {}
                  setOfficeMode('view');
                }}
                style={{ display: 'flex', alignItems: 'center', gap: 5, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.dangerBorder}`, background: C.dangerBg, color: C.dangerText, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
              >
                <DiscardIcon />
                {!isMobile && <span>Откатить</span>}
              </button>
              <button
                title="Нет, продолжить редактирование"
                onClick={() => setOfficeDiscardConfirm(false)}
                style={{ display: 'flex', alignItems: 'center', padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.border}`, background: C.bgPanel, color: C.textHeading, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
              >
                {!isMobile ? 'Нет' : <X size={13} strokeWidth={2} />}
              </button>
            </div>
          ) : (
            // Кнопки редактирования: [Отмена] [Сохранить]
            <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
              <button
                title="Отменить изменения"
                onClick={isMobile
                  ? () => setOfficeDiscardDialog(true)
                  : () => setOfficeDiscardConfirm(true)
                }
                style={{ display: 'flex', alignItems: 'center', gap: 5, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.border}`, background: C.bgPanel, color: C.textHeading, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
              >
                <DiscardIcon />
                {!isMobile && <span>Отмена</span>}
              </button>
              <button
                title="Сохранить"
                onClick={async () => {
                  setOfficeSwitching(true);
                  await api.files.officeForceSave(project.id, filePath).catch(() => {});
                  setOfficeCacheKey(String(Date.now()));
                  setOfficeMode('view');
                }}
                style={{ display: 'flex', alignItems: 'center', gap: 5, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.accent}`, background: C.accent, color: C.onAccent, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
              >
                <Save size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                {!isMobile && <span>Сохранить</span>}
              </button>
            </div>
          )
        )}

        {/* Кнопки действий */}
        <div style={{ display: 'flex', gap: 4, alignItems: 'center', flexShrink: 0 }}>
          {/* Скопировать содержимое текстового файла (для .md — Shift = с форматированием) */}
          {tab === 'file' && isCopyableText && !isDrawio && !(isHtml && htmlTab === 'preview') && (
            <ToolbarIconButton
              isMobile={isMobile}
              onClick={handleCopyContent}
              title={copied ? 'Скопировано' : isMarkdown ? 'Скопировать Markdown (Shift — с форматированием)' : 'Скопировать содержимое'}
              color={copied ? C.success : undefined}
            >
              {copied
                ? <Check size={ICON_SIZE.sm} strokeWidth={2.5} />
                : <Copy size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            </ToolbarIconButton>
          )}
          {online && !editing && !fileContent?.isBinary && (
            <>
              {diff && (
                isMobile
                  ? <ToolbarIconButton isMobile={isMobile} onClick={handleRevert} title="Откатить"><RotateCcw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /></ToolbarIconButton>
                  : <button onClick={handleRevert} style={tbBtnGhost}>Откатить</button>
              )}
              {!isMobile && !isDrawio && (
                isHtml && htmlTab === 'preview'
                  ? <button onClick={() => setHtmlTab('code')} style={tbBtnPrimary}>Править</button>
                  : <button onClick={() => { setEditing(true); setTab('file'); }} style={tbBtnPrimary}>Править</button>
              )}
            </>
          )}
          {!editing && fileContent?.isBinary && null}
          {editing && (
            isMobile ? (
              <>
                <ToolbarIconButton isMobile onClick={() => { setEditing(false); setEditContent(content); setActionError(null); }} title="Отмена">
                  <DiscardIcon />
                </ToolbarIconButton>
                <ToolbarIconButton
                  isMobile
                  onClick={handleSave}
                  title={!online ? 'Сохранение недоступно офлайн' : 'Сохранить'}
                  color={online ? C.accent : undefined}
                  disabled={!online}
                >
                  <Save size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                </ToolbarIconButton>
              </>
            ) : (
              <>
                <button onClick={() => { setEditing(false); setEditContent(content); setActionError(null); }} style={tbBtnGhost}>Отмена</button>
                <button
                  onClick={handleSave}
                  disabled={!online}
                  title={!online ? 'Сохранение недоступно офлайн' : undefined}
                  style={{ ...tbBtnPrimary, ...(!online ? { opacity: 0.5, cursor: 'not-allowed' } : {}) }}
                >Сохранить</button>
              </>
            )
          )}

          {/* Синхронизация для офлайна */}
          {online && !editing && (
            pending ? (
              syncState === 'direct' ? (
                <ToolbarIconButton isMobile={isMobile} onClick={handleToggleSync} title="Отменить синхронизацию">
                  <span style={{ width: 14, height: 14, borderRadius: '50%', border: `2.5px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.6s linear infinite' }} />
                </ToolbarIconButton>
              ) : (
                <span title="Загружается…" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: isMobile ? 40 : 32, height: isMobile ? 40 : 32 }}>
                  <span style={{ width: 14, height: 14, borderRadius: '50%', border: `2.5px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.6s linear infinite' }} />
                </span>
              )
            ) : syncState === 'inherited' ? (
              <span title="Синхронизируется через папку/проект" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: isMobile ? 40 : 32, height: isMobile ? 40 : 32, color: C.accentMuted }}>
                <CloudGlyph filled />
              </span>
            ) : (
              <ToolbarIconButton
                isMobile={isMobile}
                onClick={handleToggleSync}
                title={syncState === 'direct' ? 'Отключить синхронизацию' : 'Синхронизировать для офлайна'}
                color={syncState === 'direct' ? C.accent : undefined}
              >
                <CloudGlyph filled={syncState === 'direct'} />
              </ToolbarIconButton>
            )
          )}

          {/* Скачать — для документов и картинок (когда есть данные) */}
          {!editing && fileContent?.base64 && (
            <ToolbarIconButton isMobile={isMobile} onClick={handleDownload} title="Скачать">
              <Download size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </ToolbarIconButton>
          )}

          {/* Корзина */}
          {online && !editing && (
            <ToolbarIconButton isMobile={isMobile} onClick={() => setDeleteConfirm(true)} title="Удалить">
              <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </ToolbarIconButton>
          )}

          {/* Развернуть на весь экран — только в split-режиме */}
          {!isMobile && onToggleFullscreen && !editing && (
            <ToolbarIconButton isMobile={isMobile} onClick={onToggleFullscreen} title="На весь экран">
              <Maximize2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </ToolbarIconButton>
          )}

          {/* Закрыть — десктоп */}
          {!isMobile && (
            <ToolbarIconButton isMobile={isMobile} onClick={handleClose} title="Закрыть">
              <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </ToolbarIconButton>
          )}
        </div>
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

        {!loading && loadError && (
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

            {fileContent?.isVideo && (
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

            {fileContent?.isAudio && (
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

            {/* Office-файлы (docx/xlsx/pptx) — через OnlyOffice Document Server */}
            {fileContent?.isDocument && fileContent.docKind !== 'pdf' && (
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
                  : isMarkdown
                  ? <div data-selection-scope="doc" data-selection-priority="2"><DocCommentedMarkdown scope={project.id} docPath={filePath} content={content} isMobile={isMobile} onCounts={onCommentCounts} /></div>
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
      {isMobile && online && !editing && tab === 'file' && fileContent && !fileContent.isBinary && !fileContent.isImage && !fileContent.isDocument && !fileContent.isVideo && !fileContent.isAudio && !isDrawio && !(isHtml && htmlTab === 'preview') && (
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

      {/* Подтверждение отката office-правок (мобилка; на десктопе — инлайн-плашка в тулбаре) */}
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
