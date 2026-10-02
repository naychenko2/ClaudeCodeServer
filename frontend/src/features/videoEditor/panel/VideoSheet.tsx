// Шторка панели «Видео» в узком окне (макет v7, «Телефон 360 px»): уже GEN_PANEL_INLINE_MIN колонке нет
// места, поэтому вклад composer-chip (смонтирован в каждом чате) поднимает ту же VideoPanel шторкой по
// тому же запросу показа. Опущенная стоит в потоке над полем ввода, поднятая уходит порталом в body.

import { useEffect, useState } from 'react';
import { FLAGS, REVEAL_PANEL_EVENT, isGenPanelKey, markGenPanelDismissed, useFeature, useGenerationSheet, type RevealPanelDetail } from 'aihome_shell/kit';
import type { ComposerChipCtx } from '../../../lib/subsystems/registryCore';
import { VIDEO_PANEL } from '../store/videoStore';
import { VideoPanel } from './VideoPanel';

// Запрос ловим на уровне модуля: вклад мог смонтироваться уже после события
let wanted = false;
const subs = new Set<() => void>();
const take = () => { const w = wanted; wanted = false; return w; };
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    if ((e as CustomEvent<Partial<RevealPanelDetail>>).detail?.key !== VIDEO_PANEL) return;
    wanted = true;
    subs.forEach(fn => fn());
  });
}

export function VideoSheet({ ctx }: { ctx: ComposerChipCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  const [open, setOpen] = useState(false);
  const { sessionId } = ctx;
  const narrow = useGenerationSheet();
  useEffect(() => { setOpen(false); }, [sessionId]);
  useEffect(() => {
    // В широком окне запрос забирает рабочая область — здесь его только гасим
    const pull = () => { if (take() && narrow) setOpen(true); };
    pull();
    subs.add(pull);
    return () => { subs.delete(pull); };
  }, [narrow]);

  // Шторка соседнего раздела встаёт вместо этой: две шторки разом не живут
  useEffect(() => {
    if (!open) return;
    const off = (e: Event) => {
      const k = (e as CustomEvent<Partial<RevealPanelDetail>>).detail?.key;
      if (k && k !== VIDEO_PANEL && isGenPanelKey(k)) setOpen(false);
    };
    window.addEventListener(REVEAL_PANEL_EVENT, off);
    return () => window.removeEventListener(REVEAL_PANEL_EVENT, off);
  }, [open]);

  if (!on || !narrow || !open) return null;
  return (
    <div data-video-sheet="" style={{ flexBasis: '100%', minWidth: 0 }}>
      <VideoPanel ctx={{
        projectId: ctx.projectId, sessionId, isMobile: true,
        onClose: () => { markGenPanelDismissed(sessionId, VIDEO_PANEL); setOpen(false); },
      }} />
    </div>
  );
}
