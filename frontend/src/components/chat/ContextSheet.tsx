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
let wanted = false;
const subs = new Set<() => void>();
const take = () => { const w = wanted; wanted = false; return w; };
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    if ((e as CustomEvent<Partial<RevealPanelDetail>>).detail?.key !== 'chatContext') return;
    wanted = true;
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
    const pull = () => { if (take() && narrow) setOpen(true); };
    pull();
    subs.add(pull);
    return () => { subs.delete(pull); };
  }, [narrow]);

  if (!narrow || !open) return null;
  return (
    <div data-context-sheet="" style={{ flexBasis: '100%', minWidth: 0 }}>
      <ContextPanelHost session={session} project={project} isMobile layout="sheet"
        onClose={() => { markGenPanelDismissed(session.id, 'chatContext'); setOpen(false); }} />
    </div>
  );
}
