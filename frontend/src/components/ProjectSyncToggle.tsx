import { useEffect } from 'react';
import { useSyncMarks, toggleSyncMark, isSyncing, loadSyncMarks } from '../lib/sync';
import { C, R, SHADOW } from '../lib/design';

// Переключатель синхронизации ВСЕГО проекта (корневая метка "").
// Включение → все файлы проекта становятся синхронизированными (inherited) и качаются офлайн.
export function ProjectSyncToggle({ projectId, online }: { projectId: string; online: boolean }) {
  const marks = useSyncMarks(projectId);
  useEffect(() => { loadSyncMarks(projectId); }, [projectId]);

  const enabled = marks.some(m => m.isDirectory && m.path === '');
  const syncing = isSyncing(projectId, '');

  const toggle = () => {
    if (!online) return; // во время синхронизации клик допустим — он её отменяет
    toggleSyncMark(projectId, { name: '', path: '', isDirectory: true, modified: '', isModified: false });
  };

  const hint = syncing
    ? 'Синхронизируется… (нажмите, чтобы отменить)'
    : enabled
      ? 'Все файлы проекта доступны офлайн'
      : online
        ? 'Скачать все файлы проекта для офлайна'
        : 'Недоступно офлайн';

  return (
    <div style={{
      display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12,
      padding: '12px 14px', background: C.bgWhite, border: `1px solid ${C.border}`,
      borderRadius: R.xl,
    }}>
      <div style={{ minWidth: 0 }}>
        <div style={{ fontSize: 14, fontWeight: 600, color: C.textHeading }}>Синхронизировать весь проект</div>
        <div style={{ fontSize: 12, color: C.textMuted, marginTop: 2 }}>{hint}</div>
      </div>
      <button
        onClick={toggle}
        disabled={!online}
        title={syncing ? 'Отменить синхронизацию' : enabled ? 'Отключить синхронизацию проекта' : 'Синхронизировать весь проект'}
        style={{
          position: 'relative', width: 44, height: 26, borderRadius: 999, border: 'none',
          cursor: (!online || syncing) ? 'default' : 'pointer', flexShrink: 0,
          background: enabled ? C.accent : C.track, transition: 'background 0.15s',
          opacity: (!online && !enabled) ? 0.5 : 1,
        }}
      >
        {syncing ? (
          <span style={{ position: 'absolute', top: 6, left: enabled ? 24 : 6, width: 14, height: 14, borderRadius: '50%', border: `2px solid ${C.bgWhite}`, borderTopColor: 'transparent', animation: 'spin 0.8s linear infinite' }} />
        ) : (
          <span style={{ position: 'absolute', top: 3, left: enabled ? 21 : 3, width: 20, height: 20, borderRadius: '50%', background: C.bgWhite, transition: 'left 0.15s', boxShadow: SHADOW.thumb }} />
        )}
      </button>
    </div>
  );
}
