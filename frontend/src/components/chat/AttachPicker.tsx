import { useState, useEffect } from 'react';
import type { FileEntry } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R, MODAL_W } from '../../lib/design';
import { Modal } from '../ui';

// Модальный пикер вложений
interface AttachPickerProps {
  projectId: string;
  selected: string[];
  onToggle: (path: string) => void;
  onClose: () => void;
}

export function AttachPicker({ projectId, selected, onToggle, onClose }: AttachPickerProps) {
  const [query, setQuery] = useState('');
  const [files, setFiles] = useState<FileEntry[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    const t = setTimeout(() => {
      api.files.search(projectId, query)
        .then(setFiles)
        .finally(() => setLoading(false));
    }, query ? 200 : 0);
    return () => clearTimeout(t);
  }, [projectId, query]);

  return (
    <Modal
      title="Прикрепить файлы"
      width={MODAL_W.form}
      onClose={onClose}
      cardStyle={{ maxHeight: '70vh' }}
    >
      <div style={{ marginBottom: 8 }}>
        <input
          autoFocus
          value={query}
          onChange={e => setQuery(e.target.value)}
          placeholder="Поиск по имени файла…"
          style={{
            width: '100%', boxSizing: 'border-box',
            padding: '8px 10px', borderRadius: R.md, border: `1px solid ${C.border}`,
            background: C.bgMain, color: C.textPrimary, fontSize: 13,
            fontFamily: FONT.mono, outline: 'none',
          }}
        />
      </div>
      <div style={{ margin: '-4px -8px', maxHeight: '46vh', overflowY: 'auto' }}>
        {loading && (
          <div style={{ padding: 16, color: C.textMuted, fontSize: 13, textAlign: 'center' }}>
            Загрузка…
          </div>
        )}
        {!loading && files.map(f => {
          const isSelected = selected.includes(f.path);
          return (
            <div
              key={f.path}
              onClick={() => onToggle(f.path)}
              style={{
                padding: '8px 12px', cursor: 'pointer', fontSize: 13, borderRadius: R.md,
                color: C.textPrimary, display: 'flex', alignItems: 'center', gap: 8,
                background: isSelected ? C.accentLight : 'transparent',
              }}
              onMouseEnter={e => { if (!isSelected) e.currentTarget.style.background = C.bgInset; }}
              onMouseLeave={e => { e.currentTarget.style.background = isSelected ? C.accentLight : 'transparent'; }}
            >
              <span style={{
                width: 14, height: 14, flexShrink: 0, borderRadius: 3, border: `1.5px solid ${isSelected ? C.accent : C.border}`,
                background: isSelected ? C.accent : 'transparent', display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}>
                {isSelected && <svg width="9" height="7" viewBox="0 0 9 7" fill="none"><path d="M1 3.5L3.5 6L8 1" stroke="white" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/></svg>}
              </span>
              <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: FONT.mono }}>
                {f.path}
              </span>
            </div>
          );
        })}
        {!loading && files.length === 0 && (
          <div style={{ padding: 16, color: C.textMuted, fontSize: 13, textAlign: 'center' }}>
            Файлы не найдены
          </div>
        )}
      </div>
      <div style={{ marginTop: 10, display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
        {selected.length > 0 && (
          <span style={{ fontSize: 12, color: C.textMuted, alignSelf: 'center' }}>
            Выбрано: {selected.length}
          </span>
        )}
        <button
          onClick={onClose}
          style={{
            padding: '7px 16px', borderRadius: R.md, border: 'none', cursor: 'pointer',
            background: C.accent, color: '#fff', fontSize: 13, fontWeight: 600,
            fontFamily: FONT.sans,
          }}
        >
          Готово
        </button>
      </div>
    </Modal>
  );
}
