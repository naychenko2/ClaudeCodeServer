import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react';
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
import type { Project } from '../types';
import { api } from '../lib/api';
import { OfflineError } from '../lib/offline';
import { toggleSyncMark, useSyncMarks, computeSyncState, isDownloaded, loadSyncMarks, loadDownloadedSet } from '../lib/sync';
import { onFilesChanged } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { EmptyState } from './EmptyState';
import { getLanguage } from '../lib/getLanguage';
import { MarkdownViewer } from './MarkdownViewer';
import { useNotes, ensureNotesLoaded, existingTitleSet } from '../lib/notes';
import { MermaidDiagram } from './MermaidDiagram';
import { DocumentViewer } from './DocumentViewer';
import { OfficeViewer } from './OfficeViewer';
import { DrawioViewer, type DrawioHandle } from './DrawioViewer';
import { base64ToBytes } from '../lib/binary';
import { C, FONT, MODAL_W, SHADOW } from '../lib/design';
import { Toolbar, ToolbarIconButton, PillSwitch, tbBtnPrimary, tbBtnGhost } from './Toolbar';
import { BackButton, Modal, ModalActions, Button, useIsMobileModal } from './ui';
import { DiffView } from './DiffView';
import { useThemeMode, getEffectiveTheme } from '../lib/themeMode';

const CodeEditor = lazy(() =>
  import('./CodeEditor').then(m => ({ default: m.CodeEditor }))
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

type ViewTab = 'file' | 'diff';

const FileSvg = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
    <polyline points="14 2 14 8 20 8" />
  </svg>
);

const TrashIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <polyline points="3 6 5 6 21 6" />
    <path d="M19 6l-1 14H6L5 6" />
    <path d="M10 11v6M14 11v6M9 6V4h6v2" />
  </svg>
);

const ExpandIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <polyline points="15 3 21 3 21 9"/>
    <polyline points="9 21 3 21 3 15"/>
    <line x1="21" y1="3" x2="14" y2="10"/>
    <line x1="3" y1="21" x2="10" y2="14"/>
  </svg>
);

const CloseIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
    <line x1="18" y1="6" x2="6" y2="18"/>
    <line x1="6" y1="6" x2="18" y2="18"/>
  </svg>
);

const RevertIcon = () => (
  <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
    <path d="M1 4v6h6"/>
    <path d="M3.5 15a9 9 0 1 0 2.1-9.4L1 10"/>
  </svg>
);

const SaveIcon = () => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/>
    <polyline points="17 21 17 13 7 13 7 21"/>
    <polyline points="7 3 7 8 15 8"/>
  </svg>
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

const DownloadIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
    <polyline points="7 10 12 15 17 10" />
    <line x1="12" y1="15" x2="12" y2="3" />
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
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke={C.onAccent} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M9 18V5l12-2v13" /><circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" />
          </svg>
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

export function FileViewer({ project, filePath, onClose, onToggleFullscreen, isMobile, onOpenSidebar }: Props) {
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
  // Подписка на тему: подсветка кода переключается light/dark вместе с приложением
  useThemeMode();
  const codeTheme = getEffectiveTheme() === 'dark' ? oneDark : oneLight;
  const [fileContent, setFileContent] = useState<FileContent | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);
  const [diff, setDiff] = useState<string | null>(null);
  const [tab, setTab] = useState<ViewTab>('file');
  const [editing, setEditing] = useState(false);
  const [htmlTab, setHtmlTab] = useState<'preview' | 'code'>('preview');
  const [officeMode, setOfficeMode] = useState<'view' | 'edit'>('view');
  const [officeSwitching, setOfficeSwitching] = useState(false);
  const [officeDiscardConfirm, setOfficeDiscardConfirm] = useState(false);
  const [officeCacheKey, setOfficeCacheKey] = useState<string | undefined>();
  // Режим draw.io: по умолчанию просмотр (read-only), кнопка «Редактировать» → edit
  const [drawioMode, setDrawioMode] = useState<'view' | 'edit'>('view');
  const [editContent, setEditContent] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const [unsavedConfirm, setUnsavedConfirm] = useState(false);
  // Ошибка мутации (сохранение/откат/удаление) офлайн или при сбое — inline-фидбек
  const [actionError, setActionError] = useState<string | null>(null);
  const [imgDims, setImgDims] = useState<{ w: number; h: number } | null>(null);
  const drawioRef = useRef<DrawioHandle>(null);
  const marks = useSyncMarks(project.id);

  const content = fileContent?.content ?? '';
  const hasUnsavedChanges = editing && editContent !== content;
  const syncState = computeSyncState(marks, filePath);
  // Помечен, но содержимое ещё не скачано → спиннер
  const pending = !!syncState && !isDownloaded(project.id, filePath);

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
    api.files.getContent(project.id, filePath).then(r => {
      setFileContent(r);
      setEditContent(r.content ?? '');
    }).catch(() => setLoadError(true)).finally(() => setLoading(false));
    // diff недоступен офлайн — мягко игнорируем ошибку
    api.files.getDiff(project.id, filePath).then(r => setDiff(r.diff)).catch(() => setDiff(null));
  }, [project.id, filePath]);

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
      api.files.getDiff(project.id, filePath).then(r => setDiff(r.diff)).catch(() => {});
    });
  }, [project.id, filePath, editing, drawioMode]);

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
      const r = await api.files.getDiff(project.id, filePath);
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
      setTab('file');
      setActionError(null);
    } catch (e) {
      setActionError(mutationErrorText(e, 'Не удалось откатить файл'));
    }
  };

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
  const isMermaid = /\.mmd$/i.test(fileName);
  const isHtml = /\.html?$/i.test(fileName);
  const isDrawio = /\.(drawio|dio)$/i.test(fileName);
  const diffStats = diff ? {
    added: diff.split('\n').filter(l => l.startsWith('+') && !l.startsWith('+++')).length,
    removed: diff.split('\n').filter(l => l.startsWith('-') && !l.startsWith('---')).length,
  } : null;
  const fileSizeMb = fileContent?.fileSize != null ? (fileContent.fileSize / 1024 / 1024).toFixed(2) : null;

  const btnPrimary: React.CSSProperties = {
    border: 'none', background: C.accent, color: C.onAccent,
    borderRadius: 8, padding: '5px 13px', cursor: 'pointer', fontSize: 13, fontWeight: 600,
  };

  const isOfficeFile = !loading && !loadError && tab === 'file' && !!fileContent?.isDocument && fileContent.docKind !== 'pdf';
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
      const r = await api.files.getDiff(project.id, filePath);
      setDiff(r.diff);
    } catch (e) {
      setActionError(mutationErrorText(e, 'Не удалось сохранить диаграмму'));
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgCard, position: 'relative' }}>
      {/* Шапка */}
      <Toolbar isMobile={isMobile}>
        {/* Кнопка открытия сайдбара — только когда он свёрнут (не на мобиле) */}
        {onOpenSidebar && !isMobile && (
          <ToolbarIconButton onClick={onOpenSidebar} title="Открыть панель" isMobile={isMobile}>
            <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
              <path d="M2 4h12M2 8h12M2 12h12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"/>
            </svg>
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

        {/* Статистика diff */}
        {diffStats && (
          <span style={{ display: 'flex', gap: 4, flexShrink: 0 }}>
            <span style={{ fontSize: 12, fontFamily: FONT.mono, color: C.success, fontWeight: 600 }}>+{diffStats.added}</span>
            <span style={{ fontSize: 12, fontFamily: FONT.mono, color: C.danger, fontWeight: 600 }}>-{diffStats.removed}</span>
          </span>
        )}

        {/* Pill-переключатель Файл / Diff — скрыт для Office-файлов и когда нет диффа */}
        {!isOfficeFile && !!diff && (
          <PillSwitch<ViewTab>
            value={tab}
            options={[{ value: 'file', label: 'Файл' }, { value: 'diff', label: 'Diff' }]}
            onChange={setTab}
            isMobile={isMobile}
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
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0, opacity: 0.7 }}>
                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
              </svg>
              {!isMobile && <span>Редактировать</span>}
            </button>
          ) : (
            <button
              title="Просмотр (правки сохраняются)"
              onClick={async () => { await drawioRef.current?.flush(); setDrawioMode('view'); }}
              style={{ display: 'flex', alignItems: 'center', gap: 6, padding: isMobile ? '5px 8px' : '5px 12px', borderRadius: 8, border: `1px solid ${C.accent}`, background: C.accent, color: C.onAccent, fontSize: 13, fontWeight: 500, cursor: 'pointer', whiteSpace: 'nowrap' }}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                <path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7-11-7-11-7z"/><circle cx="12" cy="12" r="3"/>
              </svg>
              {!isMobile && <span>Просмотр</span>}
            </button>
          )
        )}

        {/* Переключатель режима просмотра/редактирования для Office-файлов */}
        {isOfficeFile && (
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
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0, opacity: 0.7 }}>
                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
              </svg>
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
                {!isMobile ? 'Нет' : '✕'}
              </button>
            </div>
          ) : (
            // Кнопки редактирования: [Отмена] [Сохранить]
            <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
              <button
                title="Отменить изменения"
                onClick={isMobile
                  ? async () => {
                      if (!window.confirm('Отменить изменения? Несохранённые правки будут потеряны.')) return;
                      setOfficeSwitching(true);
                      try { await api.files.officeDiscard(project.id, filePath); } catch {}
                      setOfficeMode('view');
                    }
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
                <SaveIcon />
                {!isMobile && <span>Сохранить</span>}
              </button>
            </div>
          )
        )}

        {/* Кнопки действий */}
        <div style={{ display: 'flex', gap: 4, alignItems: 'center', flexShrink: 0 }}>
          {online && !editing && !fileContent?.isBinary && (
            <>
              {diff && (
                isMobile
                  ? <ToolbarIconButton isMobile={isMobile} onClick={handleRevert} title="Откатить"><RevertIcon /></ToolbarIconButton>
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
                  <SaveIcon />
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
              <DownloadIcon />
            </ToolbarIconButton>
          )}

          {/* Корзина */}
          {online && !editing && (
            <ToolbarIconButton isMobile={isMobile} onClick={() => setDeleteConfirm(true)} title="Удалить">
              <TrashIcon />
            </ToolbarIconButton>
          )}

          {/* Развернуть на весь экран — только в split-режиме */}
          {!isMobile && onToggleFullscreen && !editing && (
            <ToolbarIconButton isMobile={isMobile} onClick={onToggleFullscreen} title="На весь экран">
              <ExpandIcon />
            </ToolbarIconButton>
          )}

          {/* Закрыть — десктоп */}
          {!isMobile && (
            <ToolbarIconButton isMobile={isMobile} onClick={handleClose} title="Закрыть">
              <CloseIcon />
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
          <span style={{ flexShrink: 0 }}>⚠</span>
          <span style={{ flex: 1 }}>{actionError}</span>
          <button
            onClick={() => setActionError(null)}
            style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.danger, fontSize: 14, padding: 0, flexShrink: 0 }}
          >✕</button>
        </div>
      )}

      {/* Содержимое */}
      <div style={{ flex: 1, overflow: (isOfficeFile || isCodeEditing || isPdfViewing || isHtmlPreviewing || isDrawioViewing) ? 'hidden' : 'auto', padding: (isOfficeFile || isCodeEditing || isPdfViewing || isHtmlPreviewing || isDrawioViewing) ? 0 : 16, display: 'flex', flexDirection: 'column' }}>
        {loading && (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', flex: 1, gap: 14 }}>
            <div style={{ width: 36, height: 36, borderRadius: '50%', border: `3px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.8s linear infinite' }} />
            <div style={{ fontSize: 13, color: C.textMuted }}>Загружаю файл…</div>
          </div>
        )}

        {!loading && loadError && (
          <EmptyState
            icon={<FileSvg />}
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
                          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke={C.onAccent} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <path d="M9 18V5l12-2v13" /><circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" />
                          </svg>
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
                    icon={<FileSvg />}
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
                icon={<FileSvg />}
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
                    <CodeEditor
                      key={filePath}
                      value={editContent}
                      onChange={setEditContent}
                      filePath={filePath}
                    />
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
                  : isMarkdown
                  ? <MarkdownViewer content={content} {...(isNotesFile ? { existingTitles: noteTitles, onWikilink: openNoteByTitle } : {})} />
                  : <SyntaxHighlighter
                      language={getLanguage(filePath)}
                      style={codeTheme}
                      customStyle={{ margin: 0, padding: 0, background: 'transparent', fontSize: 13, lineHeight: '1.6', fontFamily: FONT.mono }}
                      codeTagProps={{ style: { fontFamily: FONT.mono } }}
                      showLineNumbers
                      lineNumberStyle={{ minWidth: '2.6em', paddingRight: '1.1em', textAlign: 'right', color: C.textMuted, userSelect: 'none' }}
                      wrapLongLines
                    >
                      {content}
                    </SyntaxHighlighter>
            )}
          </>
        )}

        {!loading && !loadError && tab === 'diff' && (
          diff
            ? <DiffView diff={diff} />
            : <div style={{ color: C.textMuted, fontSize: 13, padding: 16 }}>Файл не изменён</div>
        )}
      </div>

      {/* Плавающая кнопка редактирования на мобиле (MA4) */}
      {isMobile && online && !editing && tab === 'file' && fileContent && !fileContent.isBinary && !fileContent.isImage && !fileContent.isDocument && !fileContent.isVideo && !fileContent.isAudio && !isDrawio && !(isHtml && htmlTab === 'preview') && (
        <button
          onClick={() => { setEditing(true); setTab('file'); }}
          title="Редактировать"
          style={{
            position: 'absolute', right: 18, bottom: 18, width: 52, height: 52, borderRadius: '50%',
            border: 'none', background: C.accent, color: C.onAccent, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: SHADOW.fab, zIndex: 20,
          }}
        >
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
            <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
          </svg>
        </button>
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
