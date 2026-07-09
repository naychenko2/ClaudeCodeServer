import { useMemo, useState } from 'react';
import type { NoteSummary } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes } from '../../lib/notes';
import { C, FONT, R } from '../../lib/design';
import { CollapseGroup, SourceDot, IconFolder } from './shared';

interface Group { source: string; label: string; root: FolderNode }

// Дерево папок источника: notes — заметки этого уровня, children — подпапки
interface FolderNode {
  name: string;
  path: string;          // полный путь папки ("Идеи/Черновики"); корень = ""
  children: FolderNode[];
  notes: NoteSummary[];
}

function buildTree(notes: NoteSummary[]): FolderNode {
  const root: FolderNode = { name: '', path: '', children: [], notes: [] };
  const byPath = new Map<string, FolderNode>([['', root]]);
  const dirOf = (p: string) => { const i = p.lastIndexOf('/'); return i < 0 ? '' : p.slice(0, i); };

  const ensureFolder = (path: string): FolderNode => {
    const existing = byPath.get(path);
    if (existing) return existing;
    const parent = ensureFolder(dirOf(path));
    const node: FolderNode = { name: path.split('/').pop()!, path, children: [], notes: [] };
    parent.children.push(node);
    byPath.set(path, node);
    return node;
  };

  for (const n of notes) ensureFolder(dirOf(n.path)).notes.push(n);
  const sortRec = (node: FolderNode) => {
    node.children.sort((a, b) => a.name.localeCompare(b.name, 'ru'));
    node.children.forEach(sortRec);
  };
  sortRec(root);
  return root;
}

// Список заметок: источники → дерево папок → заметки. Перенос — drag&drop
// заметки на папку/заголовок источника (в пределах источника).
export function NotesList({ notes, selectedId, onSelect, onMoved }: {
  notes: NoteSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  // Заметка перенесена (id сменился) — вызывающий обновляет выбор
  onMoved?: (oldId: string, newId: string) => void;
}) {
  // Свёрнутые папки (ключ source|path); по умолчанию всё раскрыто
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [dropTarget, setDropTarget] = useState<string | null>(null);   // source|path

  const groups = useMemo<Group[]>(() => {
    const map = new Map<string, { source: string; label: string; notes: NoteSummary[] }>();
    for (const n of notes) {
      let g = map.get(n.source);
      if (!g) { g = { source: n.source, label: n.sourceLabel, notes: [] }; map.set(n.source, g); }
      g.notes.push(n);
    }
    return [...map.values()]
      .sort((a, b) => a.source === 'personal' ? -1 : b.source === 'personal' ? 1 : a.label.localeCompare(b.label, 'ru'))
      .map(g => ({ source: g.source, label: g.label, root: buildTree(g.notes) }));
  }, [notes]);

  const doMove = async (noteId: string, source: string, folder: string) => {
    const note = notes.find(n => n.id === noteId);
    if (!note || note.source !== source) return;   // перенос только внутри источника
    const dir = note.path.includes('/') ? note.path.slice(0, note.path.lastIndexOf('/')) : '';
    if (dir === folder) return;
    try {
      const updated = await api.notes.move(noteId, folder || null);
      bumpNotes();
      onMoved?.(noteId, updated.id);
    } catch { /* конфликт имени — реалтайм/повтор покажет актуальное */ }
  };

  const dropProps = (source: string, folder: string) => ({
    onDragOver: (e: React.DragEvent) => {
      if (e.dataTransfer.types.includes('application/x-note-id')) {
        e.preventDefault();
        setDropTarget(`${source}|${folder}`);
      }
    },
    onDragLeave: () => setDropTarget(prev => prev === `${source}|${folder}` ? null : prev),
    onDrop: (e: React.DragEvent) => {
      e.preventDefault();
      setDropTarget(null);
      const noteId = e.dataTransfer.getData('application/x-note-id');
      const noteSource = e.dataTransfer.getData('application/x-note-source');
      if (noteId && noteSource === source) void doMove(noteId, source, folder);
    },
  });

  const renderNote = (n: NoteSummary, depth: number) => {
    const active = n.id === selectedId;
    return (
      <button
        key={n.id}
        onClick={() => onSelect(n.id)}
        title={n.title}
        draggable
        onDragStart={e => {
          e.dataTransfer.setData('application/x-note-id', n.id);
          e.dataTransfer.setData('application/x-note-source', n.source);
          e.dataTransfer.effectAllowed = 'move';
        }}
        style={{
          width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
          padding: `5px 8px 5px ${24 + depth * 14}px`, borderRadius: R.sm, fontFamily: FONT.sans,
          fontSize: 12.5, marginBottom: 1,
          background: active ? C.accentMuted : 'transparent',
          color: active ? C.textHeading : C.textSecondary,
          fontWeight: active ? 500 : 400,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}
      >{n.title}</button>
    );
  };

  const renderFolder = (source: string, node: FolderNode, depth: number): React.ReactNode => {
    const key = `${source}|${node.path}`;
    const isCollapsed = collapsed.has(key);
    const isDrop = dropTarget === key;
    return (
      <div key={key}>
        <button
          onClick={() => setCollapsed(prev => { const next = new Set(prev); isCollapsed ? next.delete(key) : next.add(key); return next; })}
          {...dropProps(source, node.path)}
          style={{
            width: '100%', textAlign: 'left', border: 'none', cursor: 'pointer',
            display: 'flex', alignItems: 'center', gap: 6,
            padding: `4px 8px 4px ${10 + depth * 14}px`, borderRadius: R.sm, fontFamily: FONT.sans,
            fontSize: 12, fontWeight: 500, color: C.textSecondary,
            background: isDrop ? C.accentMuted : 'transparent',
            boxShadow: isDrop ? `inset 0 0 0 1.5px ${C.accent}` : 'none',
          }}
        >
          <span style={{ fontSize: 8, color: C.textMuted, width: 8 }}>{isCollapsed ? '▸' : '▾'}</span>
          <span style={{ color: C.accent, display: 'flex' }}><IconFolder /></span>
          <span style={{ flex: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{node.name}</span>
          <span style={{ fontSize: 10, color: C.textMuted }}>{countNotes(node)}</span>
        </button>
        {!isCollapsed && (
          <>
            {node.children.map(c => renderFolder(source, c, depth + 1))}
            {node.notes.map(n => renderNote(n, depth + 1))}
          </>
        )}
      </div>
    );
  };

  if (notes.length === 0)
    return (
      <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.6 }}>
        Пока нет заметок. Создай первую или попроси Claude законспектировать что-нибудь.
      </div>
    );

  return (
    <div style={{ padding: '8px 8px 20px' }}>
      {groups.map(g => (
        <div key={g.source} {...dropProps(g.source, '')}
          style={dropTarget === `${g.source}|` ? { borderRadius: R.md, boxShadow: `inset 0 0 0 1.5px ${C.accent}` } : undefined}>
          <CollapseGroup
            defaultOpen
            title={
              <span style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
                <SourceDot source={g.source} />
                <span style={{ fontSize: 12.5, fontWeight: 500, color: C.textPrimary }}>{g.label}</span>
              </span>
            }
            tail={<span style={{ fontSize: 11, color: C.textMuted }}>{countNotes(g.root)}</span>}
          >
            {g.root.children.map(c => renderFolder(g.source, c, 0))}
            {g.root.notes.map(n => renderNote(n, 0))}
          </CollapseGroup>
        </div>
      ))}
    </div>
  );
}

function countNotes(node: FolderNode): number {
  return node.notes.length + node.children.reduce((s, c) => s + countNotes(c), 0);
}
