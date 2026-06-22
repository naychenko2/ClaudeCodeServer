import { useEffect, useState } from 'react';
import SyntaxHighlighter from 'react-syntax-highlighter/dist/esm/prism-light';
import { oneLight } from 'react-syntax-highlighter/dist/esm/styles/prism';
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
import { toggleSyncMark, useSyncMarks, computeSyncState, isDownloaded, loadSyncMarks, loadDownloadedSet } from '../lib/sync';
import { onFilesChanged } from '../lib/signalr';
import { useOnline } from '../hooks/useOnline';
import { EmptyState } from './EmptyState';
import { getLanguage } from '../lib/getLanguage';
import { MarkdownViewer } from './MarkdownViewer';
import { DocumentViewer, type DocKind } from './DocumentViewer';
import { base64ToBytes } from '../lib/binary';
import { C } from '../lib/design';
import { Toolbar, ToolbarIconButton, PillSwitch, tbBtnPrimary, tbBtnGhost } from './Toolbar';

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
  isFullscreen?: boolean;
  onToggleFullscreen?: () => void;
  isMobile?: boolean;
}

interface FileContent {
  content: string | null;
  isBinary: boolean;
  isImage: boolean;
  isDocument?: boolean;
  docKind?: DocKind;
  mimeType?: string;
  base64?: string;
  fileSize?: number;
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

const SplitViewIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <rect x="3" y="3" width="18" height="18" rx="2"/>
    <line x1="12" y1="3" x2="12" y2="21"/>
  </svg>
);

const CloseIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round">
    <line x1="18" y1="6" x2="6" y2="18"/>
    <line x1="6" y1="6" x2="18" y2="18"/>
  </svg>
);

const EditIcon = () => (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/>
    <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
  </svg>
);

const RevertIcon = () => (
  <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
    <path d="M1 4v6h6"/>
    <path d="M3.5 15a9 9 0 1 0 2.1-9.4L1 10"/>
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

// Парсинг unified-diff (git) в строки с номерами old/new и заголовками ханков
interface DiffRow { type: 'hunk' | 'add' | 'del' | 'ctx' | 'meta'; text: string; oldNo?: number; newNo?: number }
function parseDiff(diff: string): DiffRow[] {
  const rows: DiffRow[] = [];
  let oldNo = 0, newNo = 0;
  for (const raw of diff.split('\n')) {
    if (raw.startsWith('diff --git') || raw.startsWith('index ') || raw.startsWith('--- ') || raw.startsWith('+++ ') ||
        raw.startsWith('new file') || raw.startsWith('deleted file') || raw.startsWith('similarity') || raw.startsWith('rename ')) continue;
    if (raw.startsWith('@@')) {
      const m = raw.match(/@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
      if (m) { oldNo = parseInt(m[1], 10); newNo = parseInt(m[2], 10); }
      rows.push({ type: 'hunk', text: raw });
    } else if (raw.startsWith('+')) {
      rows.push({ type: 'add', text: raw.slice(1), newNo }); newNo++;
    } else if (raw.startsWith('-')) {
      rows.push({ type: 'del', text: raw.slice(1), oldNo }); oldNo++;
    } else if (raw.startsWith('\\')) {
      rows.push({ type: 'meta', text: raw });
    } else {
      rows.push({ type: 'ctx', text: raw.startsWith(' ') ? raw.slice(1) : raw, oldNo, newNo }); oldNo++; newNo++;
    }
  }
  // Срезаем хвостовую пустую строку (split по \n)
  if (rows.length && rows[rows.length - 1].type === 'ctx' && rows[rows.length - 1].text === '') rows.pop();
  return rows;
}

function DiffView({ diff }: { diff: string }) {
  const rows = parseDiff(diff);
  const gutter: React.CSSProperties = { width: 40, textAlign: 'right', padding: '0 7px', color: '#C4BBA9', userSelect: 'none', flexShrink: 0 };
  return (
    <div style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, lineHeight: '1.55' }}>
      {rows.map((r, i) => {
        if (r.type === 'hunk') return (
          <div key={i} style={{ background: '#EEF2F6', color: '#5C7390', padding: '2px 10px', whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{r.text}</div>
        );
        if (r.type === 'meta') return (
          <div key={i} style={{ color: '#9A8F7E', padding: '0 10px', fontStyle: 'italic' }}>{r.text}</div>
        );
        const bg = r.type === 'add' ? '#EAF3E7' : r.type === 'del' ? '#F8E7E1' : 'transparent';
        const sign = r.type === 'add' ? '+' : r.type === 'del' ? '−' : '';
        const signColor = r.type === 'add' ? '#37722B' : '#A8392C';
        return (
          <div key={i} style={{ display: 'flex', background: bg, alignItems: 'flex-start' }}>
            <span style={gutter}>{r.oldNo ?? ''}</span>
            <span style={gutter}>{r.newNo ?? ''}</span>
            <span style={{ width: 16, textAlign: 'center', color: signColor, userSelect: 'none', flexShrink: 0 }}>{sign}</span>
            <span style={{ flex: 1, whiteSpace: 'pre-wrap', wordBreak: 'break-all', color: '#2A251F', paddingRight: 10 }}>{r.text || ' '}</span>
          </div>
        );
      })}
    </div>
  );
}

export function FileViewer({ project, filePath, onClose, isFullscreen, onToggleFullscreen, isMobile }: Props) {
  const online = useOnline();
  const [fileContent, setFileContent] = useState<FileContent | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState(false);
  const [diff, setDiff] = useState<string | null>(null);
  const [tab, setTab] = useState<ViewTab>('file');
  const [editing, setEditing] = useState(false);
  const [editContent, setEditContent] = useState('');
  const [deleteConfirm, setDeleteConfirm] = useState(false);
  const [unsavedConfirm, setUnsavedConfirm] = useState(false);
  const [imgDims, setImgDims] = useState<{ w: number; h: number } | null>(null);
  const marks = useSyncMarks(project.id);

  const content = fileContent?.content ?? '';
  const hasUnsavedChanges = editing && editContent !== content;
  const syncState = computeSyncState(marks, filePath);
  // Помечен, но содержимое ещё не скачано → спиннер
  const pending = !!syncState && !isDownloaded(project.id, filePath);

  useEffect(() => {
    setEditing(false);
    setTab('file');
    setLoading(true);
    setLoadError(false);
    setFileContent(null);
    setImgDims(null);
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
      if (projectId !== project.id || editing) return;
      const norm = filePath.replace(/\\/g, '/');
      if (!paths.some(p => p.replace(/\\/g, '/') === norm)) return;
      api.files.getContent(project.id, filePath).then(r => { setFileContent(r); setEditContent(r.content ?? ''); setLoadError(false); }).catch(() => {});
      api.files.getDiff(project.id, filePath).then(r => setDiff(r.diff)).catch(() => {});
    });
  }, [project.id, filePath, editing]);

  const handleToggleSync = () => {
    toggleSyncMark(project.id, {
      name: fileName, path: filePath, isDirectory: false,
      modified: '', isModified: false,
    });
  };

  const handleSave = async () => {
    await api.files.saveContent(project.id, filePath, editContent);
    setFileContent(prev => prev ? { ...prev, content: editContent } : prev);
    setEditing(false);
    const r = await api.files.getDiff(project.id, filePath);
    setDiff(r.diff);
  };

  const handleDelete = async () => {
    await api.files.delete(project.id, filePath);
    onClose();
  };

  const handleRevert = async () => {
    await api.files.revert(project.id, filePath);
    const r = await api.files.getContent(project.id, filePath);
    setFileContent(r);
    setEditContent(r.content ?? '');
    setDiff(null);
  };

  const handleClose = () => {
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
    await handleSave();
    onClose();
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
  const diffStats = diff ? {
    added: diff.split('\n').filter(l => l.startsWith('+') && !l.startsWith('+++')).length,
    removed: diff.split('\n').filter(l => l.startsWith('-') && !l.startsWith('---')).length,
  } : null;
  const fileSizeMb = fileContent?.fileSize != null ? (fileContent.fileSize / 1024 / 1024).toFixed(2) : null;

  const btnPrimary: React.CSSProperties = {
    border: 'none', background: C.accent, color: '#FBF8F2',
    borderRadius: 8, padding: '5px 13px', cursor: 'pointer', fontSize: 13, fontWeight: 600,
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: '#FBF8F2', position: 'relative' }}>
      {/* Шапка */}
      <Toolbar isMobile={isMobile}>
        {/* Кнопка назад — только в обычном режиме */}
        {!isFullscreen && (
          <button
            onClick={handleClose}
            style={{ display: 'flex', alignItems: 'center', gap: 5, background: 'none', border: 'none', cursor: 'pointer', color: C.textSecondary, fontSize: 13, fontWeight: 600, height: 32, padding: '0 6px', borderRadius: 8, flexShrink: 0, fontFamily: 'inherit' }}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M15 18l-6-6 6-6" />
            </svg>
            Файлы
          </button>
        )}

        {/* Имя файла */}
        <span style={{ fontFamily: "'JetBrains Mono', monospace", fontWeight: 700, fontSize: 13, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: C.textHeading }}>
          {fileName}
        </span>

        {/* Статистика diff */}
        {diffStats && (
          <span style={{ display: 'flex', gap: 4, flexShrink: 0 }}>
            <span style={{ fontSize: 12, fontFamily: "'JetBrains Mono', monospace", color: C.success, fontWeight: 600 }}>+{diffStats.added}</span>
            <span style={{ fontSize: 12, fontFamily: "'JetBrains Mono', monospace", color: C.danger, fontWeight: 600 }}>-{diffStats.removed}</span>
          </span>
        )}

        {/* Pill-переключатель Файл / Diff */}
        <PillSwitch<ViewTab>
          value={tab}
          options={[{ value: 'file', label: 'Файл' }, { value: 'diff', label: 'Diff' }]}
          onChange={setTab}
          isMobile={isMobile}
        />

        {/* Кнопки действий */}
        <div style={{ display: 'flex', gap: 4, alignItems: 'center', flexShrink: 0 }}>
          {online && !editing && !fileContent?.isBinary && (
            <>
              {diff && (
                isMobile
                  ? <ToolbarIconButton isMobile={isMobile} onClick={handleRevert} title="Откатить"><RevertIcon /></ToolbarIconButton>
                  : <button onClick={handleRevert} style={tbBtnGhost}>Откатить</button>
              )}
              {/* На мобиле редактирование — через плавающий FAB (см. ниже); в fullscreen — иконка, иначе — текст */}
              {!isMobile && (isFullscreen ? (
                <ToolbarIconButton onClick={() => { setEditing(true); setTab('file'); }} title="Редактировать">
                  <EditIcon />
                </ToolbarIconButton>
              ) : (
                <button onClick={() => { setEditing(true); setTab('file'); }} style={tbBtnPrimary}>Править</button>
              ))}
            </>
          )}
          {!editing && fileContent?.isBinary && null}
          {editing && (
            <>
              <button onClick={() => { setEditing(false); setEditContent(content); }} style={tbBtnGhost}>Отмена</button>
              <button onClick={handleSave} style={tbBtnPrimary}>Сохранить</button>
            </>
          )}

          {/* Синхронизация для офлайна */}
          {online && !editing && (
            pending ? (
              syncState === 'direct' ? (
                <ToolbarIconButton isMobile={isMobile} onClick={handleToggleSync} title="Отменить синхронизацию">
                  <span style={{ width: 14, height: 14, borderRadius: '50%', border: '2.5px solid #DACDB9', borderTopColor: C.accent, animation: 'spin 0.6s linear infinite' }} />
                </ToolbarIconButton>
              ) : (
                <span title="Загружается…" style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', width: isMobile ? 40 : 32, height: isMobile ? 40 : 32 }}>
                  <span style={{ width: 14, height: 14, borderRadius: '50%', border: '2.5px solid #DACDB9', borderTopColor: C.accent, animation: 'spin 0.6s linear infinite' }} />
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

          {/* Кнопка expand / split-view */}
          {onToggleFullscreen && !editing && (
            <ToolbarIconButton
              isMobile={isMobile}
              onClick={onToggleFullscreen}
              title={isFullscreen ? 'Режим разделения' : 'Развернуть на весь экран'}
            >
              {isFullscreen ? <SplitViewIcon /> : <ExpandIcon />}
            </ToolbarIconButton>
          )}

          {/* Корзина */}
          {online && !editing && (
            <ToolbarIconButton isMobile={isMobile} onClick={() => setDeleteConfirm(true)} title="Удалить">
              <TrashIcon />
            </ToolbarIconButton>
          )}

          {/* Закрыть */}
          <ToolbarIconButton isMobile={isMobile} onClick={handleClose} title="Закрыть">
            <CloseIcon />
          </ToolbarIconButton>
        </div>
      </Toolbar>

      {/* Содержимое */}
      <div style={{ flex: 1, overflow: 'auto', padding: 16, display: 'flex', flexDirection: 'column' }}>
        {loading && (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', flex: 1, gap: 14 }}>
            <div style={{ width: 36, height: 36, borderRadius: '50%', border: '3px solid #E0D7C8', borderTopColor: '#D97757', animation: 'spin 0.8s linear infinite' }} />
            <div style={{ fontSize: 13, color: '#9A8F7E' }}>Загружаю файл…</div>
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
                  style={{ maxWidth: '100%', borderRadius: 8, boxShadow: '0 2px 12px rgba(60,50,35,0.10)' }}
                  alt={fileName}
                />
                {/* Метаданные изображения: тип · размеры · вес */}
                <div style={{ fontSize: 12, color: '#9A8F7E', fontFamily: "'JetBrains Mono', monospace", display: 'flex', gap: 7, flexWrap: 'wrap', justifyContent: 'center' }}>
                  <span>{(fileContent.mimeType?.split('/')[1] ?? fileName.split('.').pop() ?? '').toUpperCase()}</span>
                  {imgDims && <><span style={{ opacity: 0.5 }}>·</span><span>{imgDims.w}×{imgDims.h}</span></>}
                  {fileSizeMb && <><span style={{ opacity: 0.5 }}>·</span><span>{fileSizeMb} МБ</span></>}
                </div>
              </div>
            )}

            {/* Документы: PDF / Word / Excel — клиентский рендеринг */}
            {fileContent?.isDocument && (
              fileContent.base64 && fileContent.docKind
                ? <DocumentViewer docKind={fileContent.docKind} base64={fileContent.base64} />
                : <EmptyState
                    icon={<FileSvg />}
                    title="Документ слишком большой"
                    subtitle={`${fileName}${fileSizeMb ? ` — ${fileSizeMb} МБ` : ''}. Просмотр недоступен для файлов больше 25 МБ.`}
                  />
            )}

            {fileContent?.isBinary && !fileContent.isImage && !fileContent.isDocument && (
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
                ? <textarea value={editContent} onChange={e => setEditContent(e.target.value)}
                    style={{ width: '100%', flex: 1, border: 'none', outline: 'none', resize: 'none', fontFamily: "'JetBrains Mono', monospace", fontSize: 13, lineHeight: 1.6, boxSizing: 'border-box', background: 'transparent' }} />
                : isMarkdown
                  ? <MarkdownViewer content={content} />
                  : <SyntaxHighlighter
                      language={getLanguage(filePath)}
                      style={oneLight}
                      customStyle={{ margin: 0, padding: 0, background: 'transparent', fontSize: 13, lineHeight: '1.6', fontFamily: "'JetBrains Mono', monospace" }}
                      codeTagProps={{ style: { fontFamily: "'JetBrains Mono', monospace" } }}
                      showLineNumbers
                      lineNumberStyle={{ minWidth: '2.6em', paddingRight: '1.1em', textAlign: 'right', color: '#C4BBA9', userSelect: 'none' }}
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
            : <div style={{ color: '#8A8070', fontSize: 13, padding: 16 }}>Файл не изменён</div>
        )}
      </div>

      {/* Плавающая кнопка редактирования на мобиле (MA4) */}
      {isMobile && online && !editing && tab === 'file' && fileContent && !fileContent.isBinary && !fileContent.isImage && !fileContent.isDocument && (
        <button
          onClick={() => { setEditing(true); setTab('file'); }}
          title="Редактировать"
          style={{
            position: 'absolute', right: 18, bottom: 18, width: 52, height: 52, borderRadius: '50%',
            border: 'none', background: '#D97757', color: '#FBF8F2', cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            boxShadow: '0 6px 18px rgba(217,119,87,0.42)', zIndex: 20,
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
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div style={{ background: '#F4F0E8', borderRadius: 20, padding: 24, width: 340, boxShadow: '0 24px 60px rgba(23,19,15,0.4)' }}>
            <h3 style={{ fontFamily: "'PT Serif', serif", fontWeight: 500, fontSize: 20, margin: '0 0 8px', letterSpacing: '-0.01em' }}>Удалить «{fileName}»?</h3>
            <p style={{ fontSize: 13, color: '#756B5E', marginBottom: 20 }}>Это действие нельзя отменить.</p>
            <div style={{ display: 'flex', gap: 10 }}>
              <button onClick={() => setDeleteConfirm(false)} style={{ flex: 1, padding: 12, borderRadius: 12, border: 'none', background: '#EDE7DC', color: '#5C5246', cursor: 'pointer', fontWeight: 600, fontSize: 14 }}>Отмена</button>
              <button onClick={handleDelete} style={{ flex: 1, padding: 12, borderRadius: 12, border: 'none', background: '#C0392B', color: '#FFF', cursor: 'pointer', fontWeight: 600, fontSize: 14 }}>Удалить</button>
            </div>
          </div>
        </div>
      )}

      {/* Диалог несохранённых изменений */}
      {unsavedConfirm && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div style={{ background: '#F4F0E8', borderRadius: 20, padding: 24, width: 360, boxShadow: '0 24px 60px rgba(23,19,15,0.4)' }}>
            <h3 style={{ fontFamily: "'PT Serif', serif", fontWeight: 500, fontSize: 20, margin: '0 0 8px', letterSpacing: '-0.01em' }}>Сохранить изменения?</h3>
            <p style={{ fontSize: 13, color: '#756B5E', marginBottom: 20 }}>В файле «{fileName}» есть несохранённые правки.</p>
            <div style={{ display: 'flex', gap: 10 }}>
              <button onClick={handleCloseWithoutSave} style={{ flex: 1, padding: 12, borderRadius: 12, border: 'none', background: '#EDE7DC', color: '#5C5246', cursor: 'pointer', fontWeight: 600, fontSize: 14 }}>Не сохранять</button>
              <button onClick={handleSaveAndClose} style={{ flex: 1, padding: 12, borderRadius: 12, border: 'none', background: '#D97757', color: '#FBF8F2', cursor: 'pointer', fontWeight: 600, fontSize: 14 }}>Сохранить</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
