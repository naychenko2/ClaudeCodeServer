import { useEffect, useState, useCallback, useMemo, useRef } from 'react';
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

interface TreeNode {
  entry: FileEntry;
  depth: number;
}

export function FileExplorer({ project, onOpenFile }: Props) {
  const [dirCache, setDirCache] = useState<Map<string, FileEntry[]>>(new Map());
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [loadingDirs, setLoadingDirs] = useState<Set<string>>(new Set());
  const inFlight = useRef(new Set<string>());

  const [search, setSearch] = useState('');
  const [searchResults, setSearchResults] = useState<FileEntry[] | null>(null);
  const [hoveredPath, setHoveredPath] = useState<string | null>(null);
  const [showCreateFile, setShowCreateFile] = useState(false);
  const [newFileName, setNewFileName] = useState('');
  const [createInDir, setCreateInDir] = useState('');

  const loadDir = useCallback(async (path: string) => {
    if (inFlight.current.has(path)) return;
    inFlight.current.add(path);
    setLoadingDirs(prev => new Set(prev).add(path));
    try {
      const entries = await api.files.list(project.id, path);
      setDirCache(prev => new Map(prev).set(path, entries));
    } finally {
      inFlight.current.delete(path);
      setLoadingDirs(prev => { const n = new Set(prev); n.delete(path); return n; });
    }
  }, [project.id]);

  useEffect(() => {
    setDirCache(new Map());
    setExpanded(new Set());
    setCreateInDir('');
    setSearch('');
    setSearchResults(null);
    inFlight.current.clear();
    loadDir('');
  }, [project.id, loadDir]);

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

  const handleSearch = async (q: string) => {
    setSearch(q);
    if (!q.trim()) { setSearchResults(null); return; }
    const results = await api.files.search(project.id, q);
    setSearchResults(results);
  };

  const handleCreateFile = async () => {
    const path = createInDir ? `${createInDir}/${newFileName}` : newFileName;
    await api.files.createFile(project.id, path);
    setShowCreateFile(false);
    setNewFileName('');
    await invalidateDir(createInDir);
    if (createInDir) setExpanded(prev => new Set(prev).add(createInDir));
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

  const renderFileRow = (entry: FileEntry, depth: number) => {
    const isExpanded = expanded.has(entry.path);
    const isLoading = loadingDirs.has(entry.path);
    const em = entry.isDirectory ? null : getExtMeta(entry.name);
    return (
      <div
        key={entry.path}
        onClick={() => entry.isDirectory ? handleToggleDir(entry) : onOpenFile(entry.path)}
        onMouseEnter={() => setHoveredPath(entry.path)}
        onMouseLeave={() => setHoveredPath(null)}
        style={{
          display: 'flex', alignItems: 'center', gap: 6,
          paddingLeft: 8 + depth * 16, paddingRight: 8,
          paddingTop: 6, paddingBottom: 6,
          borderRadius: 8, cursor: 'pointer',
          background: hoveredPath === entry.path ? '#E8E1D4' : 'transparent',
          transition: 'background 0.1s',
        }}
      >
        <span style={{ width: 12, flexShrink: 0, textAlign: 'center', userSelect: 'none', color: '#9A8F7E', fontSize: 9, lineHeight: 1 }}>
          {entry.isDirectory ? (isLoading ? '·' : (isExpanded ? '▾' : '▸')) : ''}
        </span>
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
  };

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

        <div
          onClick={() => setShowCreateFile(true)}
          style={{ marginTop: 8, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7, height: 34, border: '1.5px dashed #D0C6B4', borderRadius: 9, color: '#BE5536', fontSize: 12.5, fontWeight: 600, cursor: 'pointer' }}
        >
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M12 5v14M5 12h14"/></svg>
          Новый файл
        </div>
      </div>

      {/* Tree / результаты поиска */}
      <div style={{ flex: 1, overflowY: 'auto', padding: '0 4px 12px' }}>
        {rootLoading ? (
          <div style={{ padding: '24px 12px', color: '#9A8F7E', fontSize: 13, textAlign: 'center', fontFamily: "'JetBrains Mono', monospace" }}>Загрузка…</div>
        ) : searchResults !== null ? (
          searchResults.length === 0 ? (
            <EmptyState
              icon={<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>}
              title="Ничего не найдено"
              subtitle={`Нет файлов по запросу «${search}»`}
            />
          ) : (
            searchResults.map(entry => renderFileRow(entry, 0))
          )
        ) : flatTree.length === 0 ? (
          <EmptyState
            icon={<svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>}
            title="Папка пуста"
            subtitle="Здесь пока нет файлов"
          />
        ) : (
          flatTree.map(({ entry, depth }) => renderFileRow(entry, depth))
        )}
      </div>

      {/* Диалог создания файла */}
      {showCreateFile && (
        <div onClick={() => setShowCreateFile(false)} style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 100 }}>
          <div onClick={e => e.stopPropagation()} style={{ width: 420, background: '#F4F0E8', borderRadius: 20, padding: 24, boxShadow: '0 24px 60px rgba(23,19,15,0.4)' }}>
            <h2 style={{ fontFamily: "'PT Serif', serif", fontWeight: 500, fontSize: 22, margin: '0 0 6px', letterSpacing: '-0.01em' }}>Новый файл</h2>
            {createInDir && (
              <div style={{ fontSize: 12, fontFamily: "'JetBrains Mono', monospace", color: '#9A8F7E', marginBottom: 12 }}>{createInDir}/</div>
            )}
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
