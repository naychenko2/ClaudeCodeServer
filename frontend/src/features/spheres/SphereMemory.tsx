import { useCallback, useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Trash2 } from 'lucide-react';
import type { SphereMemoryEntry, SphereMemoryResponse, SphereMemoryType } from '../../types';
import { api } from '../../lib/api';
import { plural } from '../../lib/plural';
import { C, FONT, FS } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { Badge, Button, IconButton, TextArea } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { MEMORY_TYPE_LABEL, memoryAdded, memoryAdopted, memoryRemoved } from './sphereLogic';

const hint: CSSProperties = { fontSize: FS.base, color: C.textMuted, lineHeight: 1.5 };
const SHELF_SHOWN = 5;

// Метка происхождения записи сферы: перенос из проекта, ручная (без метки) или автоматическая
function originLabel(e: SphereMemoryEntry): string {
  if (e.promotedFrom) return 'перенесено из проекта';
  if (e.source === 'manual') return '';
  return 'авто';
}

function shortDate(iso: string) {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
}

interface ViewProps {
  data: SphereMemoryResponse;
  busyId: string | null;
  isMobile: boolean;
  onAdopt: (projectId: string, entryId: string) => void;
  onRemove: (entryId: string) => void;
}

// Две полки памяти: «Память сферы» и «Память проектов сферы» (у записей проектов — «Перенести в сферу»)
export function SphereMemoryShelves({ data, busyId, isMobile, onAdopt, onRemove }: ViewProps) {
  const [showAll, setShowAll] = useState(false);
  const projectEntries = data.projects.flatMap(p => p.entries.map(e => ({ ...e, projectName: p.projectName })));
  const shown = showAll ? projectEntries : projectEntries.slice(0, SHELF_SHOWN);

  const head = (label: string, count: number) => (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
      {label}<span style={{ color: C.textMuted }}>{count}</span>
    </div>
  );
  const meta = (type: string, who: string, at: string) => (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', marginTop: 3 }}>
      <Badge tone="neutral" size="xs">{MEMORY_TYPE_LABEL[type] ?? type}</Badge>
      <span style={{ fontSize: FS.xs, color: C.textMuted }}>{[who, shortDate(at)].filter(Boolean).join(' · ')}</span>
    </div>
  );

  return (
    <>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-shelf="sphere">
        {head('Память сферы', data.sphere.length)}
        {data.sphere.length === 0 && (
          <div style={hint}>Здесь соберутся решения и договорённости, которые переживут любой проект сферы.</div>
        )}
        {data.sphere.map((e: SphereMemoryEntry) => (
          <div key={e.id} style={{ display: 'flex', gap: 8, alignItems: 'flex-start', padding: '6px 0', borderTop: `1px solid ${C.divider}` }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.45, overflowWrap: 'anywhere', whiteSpace: 'pre-wrap' }}>{e.text}</div>
              {meta(e.type, originLabel(e), e.createdAt)}
            </div>
            <IconButton title="Удалить запись" ariaLabel="Удалить запись" size={isMobile ? 'lg' : 'sm'} disabled={busyId === e.id} onClick={() => onRemove(e.id)}>
              <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
        ))}
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-shelf="projects">
        {head('Память проектов сферы', projectEntries.length)}
        {projectEntries.length === 0 && (
          <div style={hint}>В проектах сферы пока нечего переносить — записи памяти проектов появятся здесь.</div>
        )}
        {shown.map(e => (
          <div key={e.id}
            style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: isMobile ? 6 : 8, alignItems: isMobile ? 'stretch' : 'flex-start', padding: '6px 0', borderTop: `1px solid ${C.divider}` }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.45, overflowWrap: 'anywhere', whiteSpace: 'pre-wrap' }}>{e.text}</div>
              {meta(e.type, e.projectName, e.createdAt)}
            </div>
            <div style={{ alignSelf: isMobile ? 'flex-start' : undefined, flexShrink: 0 }}>
              <Button variant="secondary" size="sm" disabled={busyId === e.id}
                title="Запись переедет в память сферы и станет видна во всех её проектах"
                onClick={() => onAdopt(e.projectId, e.id)}>
                Перенести в сферу
              </Button>
            </div>
          </div>
        ))}
        {projectEntries.length > SHELF_SHOWN && !showAll && (
          <button type="button" onClick={() => setShowAll(true)}
            style={{ alignSelf: 'flex-start', background: 'none', border: 'none', padding: 0, cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.base, color: C.accent }}>
            и ещё {projectEntries.length - SHELF_SHOWN}
          </button>
        )}
      </div>
    </>
  );
}

// Раздел «Память» страницы сферы: загрузка, добавление, удаление, перенос из проектов
export function SphereMemorySection({ sphereId, onCountChange }: { sphereId: string; onCountChange?: (count: number) => void }) {
  const isMobile = useIsMobile();
  const [data, setData] = useState<SphereMemoryResponse | null>(null);
  const [error, setError] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [adding, setAdding] = useState(false);

  const load = useCallback(() => {
    api.spheres.memory(sphereId)
      .then(m => { setData(m); setError(''); })
      .catch(e => setError(e instanceof Error ? e.message : 'Не удалось загрузить память'));
  }, [sphereId]);
  useEffect(() => { load(); }, [load]);
  useEffect(() => { if (data) onCountChange?.(data.sphere.length); }, [data, onCountChange]);

  const act = async (id: string, failText: string, run: () => Promise<void>) => {
    setBusyId(id); setError('');
    try { await run(); } catch (e) { setError(e instanceof Error ? e.message : failText); }
    finally { setBusyId(null); }
  };
  const adopt = (projectId: string, entryId: string) => act(entryId, 'Не удалось перенести запись', async () => {
    const adopted = await api.spheres.adoptMemory(sphereId, projectId, entryId);
    setData(d => d && memoryAdopted(d, projectId, entryId, adopted));
  });
  const remove = (entryId: string) => act(entryId, 'Не удалось удалить запись', async () => {
    await api.spheres.removeMemory(sphereId, entryId);
    setData(d => d && memoryRemoved(d, entryId));
  });
  const add = async () => {
    const t = text.trim();
    if (!t) return;
    setAdding(true); setError('');
    try {
      const created = await api.spheres.addMemory(sphereId, t, 'fact' as SphereMemoryType);
      setData(d => d && memoryAdded(d, created));
      setText('');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось добавить запись');
    } finally { setAdding(false); }
  };

  const full = data && data.sphere.length >= data.maxEntries;

  return (
    <>
      {error && (
        <div role="alert" style={{ ...hint, color: C.dangerText }}>
          {error}{' '}
          {!data && (
            <button type="button" onClick={load}
              style={{ background: 'none', border: 'none', cursor: 'pointer', color: C.accent, textDecoration: 'underline', fontFamily: 'inherit', fontSize: 'inherit' }}>
              Повторить
            </button>
          )}
        </div>
      )}
      {!data && !error && <div style={hint}>Загружаем память…</div>}
      {data && (
        <>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <TextArea value={text} onChange={setText} autoGrow minHeight={44} maxHeight={160}
              placeholder="Напр.: интеграция с АБС — только через шлюз" disabled={!!full} />
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Button variant="secondary" size="sm" disabled={!text.trim() || adding || !!full} onClick={add}>Добавить в память сферы</Button>
              {full && <span style={{ fontSize: FS.sm, color: C.textMuted }}>Лимит — {data.maxEntries} {plural(data.maxEntries, 'запись', 'записи', 'записей')}</span>}
            </div>
          </div>
          <SphereMemoryShelves data={data} busyId={busyId} isMobile={isMobile} onAdopt={adopt} onRemove={remove} />
        </>
      )}
    </>
  );
}
