// Шторка панели «Контекст» в узком окне (макет composer-actions-v1, «телефон 360»): уже
// GEN_PANEL_INLINE_MIN панели генерации нет места в зоне (genPanelPlacement), поэтому строку
// контекста — она смонтирована в каждом чате — поднимает ту же ContextPanelHost шторкой по тому же
// запросу показа панели. Шторка поднимается только по просьбе (чип объекта, пункт «Открыть»), а не
// от действий агента. Портал делает сам каркас: опущенная стоит в потоке, поднятая уходит в body.
import { useEffect, useState } from 'react';
import type { Project, Session } from '../../types';
import { markGenPanelDismissed } from '../../lib/genPanelDismissed';
import { REVEAL_PANEL_EVENT, type RevealPanelDetail } from '../../lib/subsystems/registryCore';
import { useGenerationSheet } from '../generation/GenerationPanel';
import { ContextPanelHost } from '../generation/ContextPanelHost';

// Запрос ловим на уровне модуля: хост мог смонтироваться уже после события
// Запрос несёт чат: просьба другого чата шторку этого не поднимает. Без sessionId — любому
let wanted: { sessionId: string | null } | null = null;
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
    if (d?.key !== 'chatContext') return;
    wanted = { sessionId: d.sessionId ?? null };
    subs.forEach(fn => fn());
  });
}

export function ContextSheet({ session, project }: { session: Session; project: Project | null }) {
  const narrow = useGenerationSheet();
  const [open, setOpen] = useState(false);
  // Смена чата закрывает шторку; эффект стоит первым, чтобы при монтировании не погасить запрос
  useEffect(() => { setOpen(false); }, [session.id]);
  useEffect(() => {
    // В широком окне запрос забирает рабочая область — здесь его только гасим
    const pull = () => { if (takeSheetRequest(session.id) && narrow) setOpen(true); };
    pull();
    subs.add(pull);
    return () => { subs.delete(pull); };
  }, [narrow, session.id]);

  if (!narrow || !open) return null;
  return (
    <div data-context-sheet="" style={{ flexBasis: '100%', minWidth: 0 }}>
      <ContextPanelHost session={session} project={project} isMobile layout="sheet"
        onClose={() => { markGenPanelDismissed(session.id, 'chatContext'); setOpen(false); }} />
    </div>
  );
}
