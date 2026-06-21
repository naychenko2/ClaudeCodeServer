import { useEffect, useState } from 'react';
import type { Project, FileEntry } from '../types';
import { api } from '../lib/api';
import { EmptyState } from './EmptyState';

interface Props {
  project: Project;
  onOpenFile: (path: string) => void;
}

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

export function FileExplorer({ project, onOpenFile }: Props) {
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [currentPath, setCurrentPath] = useState('');
  const [search, setSearch] = useState('');
  const [searchResults, setSearchResults] = useState<FileEntry[] | null>(null);
  const [showCreateFile, setShowCreateFile] = useState(false);
  const [newFileName, setNewFileName] = useState('');
  const [hoveredPath, setHoveredPath] = useState<string | null>(null);

  const load = (path: string) => {
    api.files.list(project.id, path).then(setEntries);
    setCurrentPath(path);
    setSearchResults(null);
    setSearch('');
  };

  useEffect(() => { load(''); }, [project.id]);

  const handleSearch = async (q: string) => {
    setSearch(q);
    if (!q.trim()) { setSearchResults(null); return; }
    const results = await api.files.search(project.id, q);
    setSearchResults(results);
  };

  const handleCreateFile = async () => {
    const path = currentPath ? `${currentPath}/${newFileName}` : newFileName;
    await api.files.createFile(project.id, path);
    setShowCreateFile(false);
    setNewFileName('');
    load(currentPath);
  };

  const displayEntries = searchResults ?? entries;
  const breadcrumbs = currentPath.split('/').filter(Boolean);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Search */}
      <div style={{ padding: '4px 12px 10px' }}>
        <div style={{ display: 'flex', alignItems: 'center', background: '#FFFFFF', border: '1px solid #E0D7C8', borderRadius: 10, padding: '0 11px', height: 38 }}>
          <span style={{ color: '#9A8F7E', marginRight: 8, display: 'flex', flexShrink: 0 }}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>
          </span>
          <input
            placeholder="Поиск…"
            value={search}
            onChange={e => handleSearch(e.target.value)}
            style={{ flex: 1, border: 'none', background: 'none', fontSize: 13, fontFamily: "'JetBrains Mono', monospace", color: '#2A251F', outline: 'none' }}
          />
          {search && (
            <button onClick={() => handleSearch('')} style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#9A8F7E', fontSize: 13, padding: 0 }}>✕</button>
          )}
        </div>

        {/* Новый файл — dashed */}
        <div
          onClick={() => setShowCreateFile(true)}
          style={{ marginTop: 8, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7, height: 34, border: '1.5px dashed #D0C6B4', borderRadius: 9, color: '#BE5536', fontSize: 12.5, fontWeight: 600, cursor: 'pointer' }}
        >
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M12 5v14M5 12h14"/></svg>
          Новый файл
        </div>
      </div>

      {/* Breadcrumbs */}
      {!searchResults && breadcrumbs.length > 0 && (
        <div style={{ padding: '0 12px 6px', fontSize: 11, color: '#9A8F7E', display: 'flex', gap: 4, flexWrap: 'wrap', fontFamily: "'JetBrains Mono', monospace" }}>
          <span style={{ cursor: 'pointer', color: '#3E7CA6' }} onClick={() => load('')}>корень</span>
          {breadcrumbs.map((seg, i) => (
            <span key={i}>
              / <span style={{ cursor: 'pointer', color: '#3E7CA6' }} onClick={() => load(breadcrumbs.slice(0, i + 1).join('/'))}>{seg}</span>
            </span>
          ))}
        </div>
      )}

      {/* Файлы */}
      <div style={{ flex: 1, overflowY: 'auto', padding: '0 8px 12px' }}>
        {searchResults !== null && searchResults.length === 0 ? (
          <EmptyState
            icon={<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>}
            title="Ничего не найдено"
            subtitle={`Нет файлов по запросу «${search}»`}
          />
        ) : displayEntries.length === 0 && !search ? (
          <EmptyState
            icon={<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>}
            title="Папка пуста"
            subtitle="Здесь пока нет файлов"
          />
        ) : (
          displayEntries.map(entry => {
            const em = entry.isDirectory ? null : getExtMeta(entry.name);
            return (
              <div
                key={entry.path}
                onClick={() => entry.isDirectory ? load(entry.path) : onOpenFile(entry.path)}
                onMouseEnter={() => setHoveredPath(entry.path)}
                onMouseLeave={() => setHoveredPath(null)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 9,
                  padding: '8px 8px', borderRadius: 8, cursor: 'pointer',
                  background: hoveredPath === entry.path ? '#E8E1D4' : 'transparent',
                  transition: 'background 0.1s',
                }}
              >
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
                <span style={{
                  fontFamily: "'JetBrains Mono', monospace",
                  fontSize: 13, flex: 1,
                  fontWeight: entry.isDirectory ? 700 : 500,
                  color: '#39332B',
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>{entry.name}</span>
                {entry.isModified && (
                  <span style={{ fontSize: 9, fontWeight: 700, color: '#C2693B', background: '#FBEBE0', width: 16, height: 16, borderRadius: 4, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>M</span>
                )}
              </div>
            );
          })
        )}
      </div>

      {/* Диалог создания файла */}
      {showCreateFile && (
        <div onClick={() => setShowCreateFile(false)} style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div onClick={e => e.stopPropagation()} style={{ width: 420, background: '#F4F0E8', borderRadius: 20, padding: 24, boxShadow: '0 24px 60px rgba(23,19,15,0.4)' }}>
            <h2 style={{ fontFamily: "'PT Serif', serif", fontWeight: 500, fontSize: 22, margin: '0 0 16px', letterSpacing: '-0.01em' }}>Новый файл</h2>
            <input
              placeholder="name.py"
              value={newFileName}
              onChange={e => setNewFileName(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && handleCreateFile()}
              autoFocus
              style={{ width: '100%', height: 48, border: '1px solid #E0D7C8', borderRadius: 12, background: '#FFFFFF', padding: '0 14px', fontSize: 15, color: '#2A251F', fontFamily: "'JetBrains Mono', monospace", boxSizing: 'border-box', marginBottom: 20, outline: 'none' }}
            />
            <div style={{ display: 'flex', gap: 10 }}>
              <div onClick={() => setShowCreateFile(false)} style={{ flex: 1, textAlign: 'center', fontSize: 14, fontWeight: 600, color: '#756B5E', background: '#EDE7DC', borderRadius: 12, padding: 13, cursor: 'pointer' }}>Отмена</div>
              <div onClick={handleCreateFile} style={{ flex: 1, textAlign: 'center', fontSize: 14, fontWeight: 600, color: '#FBF8F2', background: '#D97757', borderRadius: 12, padding: 13, cursor: 'pointer' }}>Создать</div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
