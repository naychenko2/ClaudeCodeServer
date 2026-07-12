// Пикер файла проекта для секции «Файлы» в форме задачи: поиск по имени/пути
// (api.files.search) + выбор кликом. Модал — тот же, что везде в приложении.

import { useEffect, useState } from 'react';
import type { FileEntry } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R } from '../../lib/design';
import { Modal } from '../../components/ui';
import { ExtBadge } from './bits';

interface Props {
  projectId: string;
  exclude?: string[];          // уже прикреплённые пути — недоступны в выдаче
  onSelect: (path: string) => void;
  onClose: () => void;
}

export function FilePicker({ projectId, exclude, onSelect, onClose }: Props) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<FileEntry[] | null>(null);
  const [loading, setLoading] = useState(false);
  const excluded = new Set(exclude ?? []);

  // Поиск с дебаунсом 200 мс; пустой запрос — не ищем (подсказка)
  useEffect(() => {
    const q = query.trim();
    if (!q) { setResults(null); setLoading(false); return; }
    let cancelled = false;
    setLoading(true);
    const t = setTimeout(() => {
      api.files.search(projectId, q)
        .then(r => { if (!cancelled) setResults(r.filter(f => !f.isDirectory)); })
        .catch(() => { if (!cancelled) setResults([]); })
        .finally(() => { if (!cancelled) setLoading(false); });
    }, 200);
    return () => { cancelled = true; clearTimeout(t); };
  }, [projectId, query]);

  const choose = (path: string) => {
    onSelect(path);
    onClose();
  };

  return (
    <Modal title="Прикрепить файл" subtitle="Поиск по файлам проекта" width={520} onClose={onClose}>
      <input
        value={query}
        onChange={e => setQuery(e.target.value)}
        placeholder="Имя или часть пути…"
        autoFocus
        style={{
          width: '100%', boxSizing: 'border-box', marginBottom: 14,
          padding: '10px 13px', border: `1px solid ${C.border}`, borderRadius: R.lg,
          outline: 'none', background: C.bgWhite,
          fontFamily: FONT.sans, fontSize: 14, color: C.textPrimary,
        }}
      />
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {loading && (
          <div style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, padding: '6px 2px' }}>
            Поиск…
          </div>
        )}
        {!loading && results && results.length === 0 && (
          <div style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, padding: '6px 2px' }}>
            Ничего не найдено.
          </div>
        )}
        {!loading && results && results.map(f => {
          const taken = excluded.has(f.path);
          return (
            <button
              key={f.path}
              onClick={() => !taken && choose(f.path)}
              disabled={taken}
              style={{
                display: 'flex', alignItems: 'center', gap: 10,
                width: '100%', boxSizing: 'border-box', textAlign: 'left',
                padding: '9px 12px', cursor: taken ? 'default' : 'pointer',
                border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
                background: C.bgWhite,
                opacity: taken ? 0.45 : 1,
              }}
            >
              <ExtBadge filename={f.path} />
              <span style={{
                fontFamily: FONT.mono, fontSize: 13, color: C.textPrimary,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>
                {f.path}
              </span>
              {taken && (
                <span style={{ marginLeft: 'auto', fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
                  добавлен
                </span>
              )}
            </button>
          );
        })}
        {!loading && !results && (
          <div style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, padding: '6px 2px' }}>
            Начните вводить имя файла — покажем совпадения по проекту.
          </div>
        )}
      </div>
    </Modal>
  );
}
