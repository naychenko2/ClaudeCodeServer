// Шторка панели «Контекст» в узком окне (макет composer-actions-v1, «телефон 360»): уже
// GEN_PANEL_INLINE_MIN панели генерации нет места в зоне (genPanelPlacement), поэтому строку
// контекста — она смонтирована в каждом чате — поднимает ту же ContextPanelHost шторкой по тому же
// запросу показа панели. Шторка поднимается только по просьбе (чип объекта, пункт «Открыть»), а не
// от действий агента. Портал делает сам каркас: опущенная стоит в потоке, поднятая уходит в body.
import { useEffect, useState, type ReactNode } from 'react';
import type { Project, Session } from '../../types';
import { markGenPanelDismissed } from '../../lib/genPanelDismissed';
import { REVEAL_PANEL_EVENT, useSlotItem, type RevealPanelDetail, type WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';
import { useGenerationSheet } from '../generation/GenerationPanel';
import { ContextPanelHost } from '../generation/ContextPanelHost';

// Запрос ловим на уровне модуля: хост мог смонтироваться уже после события
// Запрос несёт чат: просьба другого чата шторку этого не поднимает. Без sessionId — любому
let wanted: { sessionId: string | null } | null = null;
// Библиотеки «Из «Персонажей»» / «Из «Голосов»» на узком окне открываются шторкой поверх: зоны для них нет
export const LIBRARY_KEYS = ['characters', 'voices'] as const;
type LibraryKey = (typeof LIBRARY_KEYS)[number];
let wantedLibrary: { key: LibraryKey; sessionId: string | null } | null = null;
const subs = new Set<() => void>();
export const takeSheetRequest = (sessionId: string): boolean => {
  if (!wanted || (wanted.sessionId && wanted.sessionId !== sessionId)) return false;
  wanted = null;
  return true;
};
export const __resetSheetRequest = () => { wanted = null; };
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    const d = (e as CustomEvent<Partial<RevealPanelDetail>>).detail;
    if (LIBRARY_KEYS.includes(d?.key as LibraryKey)) {
      wantedLibrary = { key: d!.key as LibraryKey, sessionId: d?.sessionId ?? null };
      subs.forEach(fn => fn());
      return;
    }
    if (d?.key !== 'chatContext') return;
    wanted = { sessionId: d.sessionId ?? null };
    subs.forEach(fn => fn());
  });
}

export const takeLibraryRequest = (sessionId: string): LibraryKey | null => {
  if (!wantedLibrary || (wantedLibrary.sessionId && wantedLibrary.sessionId !== sessionId)) return null;
  const k = wantedLibrary.key;
  wantedLibrary = null;
  return k;
};

function LibrarySheet({ panelKey, project, session, onClose }: { panelKey: LibraryKey; project: Project | null; session: Session; onClose: () => void }) {
  const def = useSlotItem<never, Record<string, unknown>>('workspace-panel-def', panelKey);
  if (!def?.render) return null;
  const ctx: WorkspacePanelDefCtx = { projectId: project?.id ?? null, sessionId: session.id, isMobile: true, onClose };
  return <>{(def.render as (c: WorkspacePanelDefCtx) => ReactNode)(ctx)}</>;
}

export function ContextSheet({ session, project }: { session: Session; project: Project | null }) {
  const narrow = useGenerationSheet();
  const [open, setOpen] = useState(false);
  const [library, setLibrary] = useState<LibraryKey | null>(null);
  // Смена чата закрывает шторку; эффект стоит первым, чтобы при монтировании не погасить запрос
  useEffect(() => { setOpen(false); setLibrary(null); }, [session.id]);
  useEffect(() => {
    // В широком окне запрос забирает рабочая область — здесь его только гасим
    const pull = () => {
      if (takeSheetRequest(session.id) && narrow) setOpen(true);
      const lib = takeLibraryRequest(session.id);
      // Библиотека заменяет шторку «Контекст»: две шторки разом не нужны
      if (lib && narrow) { setOpen(false); setLibrary(lib); }
    };
    pull();
    subs.add(pull);
    return () => { subs.delete(pull); };
  }, [narrow, session.id]);

  if (!narrow) return null;
  if (library) {
    return (
      <div data-context-sheet="library" style={{ flexBasis: '100%', minWidth: 0 }}>
        <LibrarySheet panelKey={library} project={project} session={session} onClose={() => setLibrary(null)} />
      </div>
    );
  }
  if (!open) return null;
  return (
    <div data-context-sheet="" style={{ flexBasis: '100%', minWidth: 0 }}>
      <ContextPanelHost session={session} project={project} isMobile layout="sheet"
        onClose={() => { markGenPanelDismissed(session.id, 'chatContext'); setOpen(false); }} />
    </div>
  );
}
