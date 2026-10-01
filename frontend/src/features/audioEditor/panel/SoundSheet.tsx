// Шторка панели «Звук» на телефоне (макет audio-editor-v2-proposal.md, «Телефон 360 px»).
// Рабочей области справа на телефоне нет, и хост панели workspace-panel-def там не рисует, поэтому
// вклад composer-chip (смонтирован в каждом чате, какая бы полоса ни была выбрана) поднимает ту же
// SoundPanel шторкой по тому же запросу показа панели: кнопка-сводка, ярлык, «Новый звук».

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { FLAGS, REVEAL_PANEL_EVENT, markGenPanelDismissed, useFeature, type RevealPanelDetail } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { SOUND_PANEL } from '../thread/threadStore';
import { SoundPanel } from './SoundPanel';

// Запрос ловим на уровне модуля: вклад мог смонтироваться уже после события
let wanted = false;
const subs = new Set<() => void>();
const take = () => { const w = wanted; wanted = false; return w; };
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    if ((e as CustomEvent<Partial<RevealPanelDetail>>).detail?.key !== SOUND_PANEL) return;
    wanted = true;
    subs.forEach(fn => fn());
  });
}

export function SoundSheet({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.audioEditor);
  const [open, setOpen] = useState(false);
  const { isMobile, sessionId } = ctx;
  // Смена чата закрывает шторку; эффект стоит первым, чтобы при монтировании не погасить запрос
  useEffect(() => { setOpen(false); }, [sessionId]);
  useEffect(() => {
    // На десктопе запрос забирает рабочая область — здесь его только гасим
    const pull = () => { if (take() && isMobile) setOpen(true); };
    pull();
    subs.add(pull);
    return () => { subs.delete(pull); };
  }, [isMobile]);

  if (!on || !isMobile || !open || typeof document === 'undefined') return null;
  return createPortal(
    <SoundPanel ctx={{
      projectId: ctx.projectId, sessionId, isMobile: true,
      onClose: () => { markGenPanelDismissed(sessionId, SOUND_PANEL); setOpen(false); },
    }} />,
    document.body,
  );
}
