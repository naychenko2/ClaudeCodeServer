// Шторка панели «Звук» в узком окне (макет audio-editor-v2-proposal.md, «Телефон 360 px»).
// Уже GEN_PANEL_INLINE_MIN панели генерации нет места в зоне (genPanelPlacement), поэтому
// вклад composer-chip (смонтирован в каждом чате, какая бы полоса ни была выбрана) поднимает ту же
// SoundPanel шторкой по тому же запросу показа панели: кнопка-сводка, ярлык, «Новый звук».
// Портала здесь нет: поднятую шторку каркас сам уносит в body, а опущенная стоит в потоке над
// полем ввода и не закрывает его нижний ряд.

import { useEffect, useState } from 'react';
import { FLAGS, REVEAL_PANEL_EVENT, isGenPanelKey, markGenPanelDismissed, useFeature, useGenerationSheet, type RevealPanelDetail } from 'aihome_shell/kit';
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
  const { sessionId } = ctx;
  const narrow = useGenerationSheet();
  // Смена чата закрывает шторку; эффект стоит первым, чтобы при монтировании не погасить запрос
  useEffect(() => { setOpen(false); }, [sessionId]);
  useEffect(() => {
    // В широком окне запрос забирает рабочая область — здесь его только гасим
    const pull = () => { if (take() && narrow) setOpen(true); };
    pull();
    subs.add(pull);
    return () => { subs.delete(pull); };
  }, [narrow]);

  // Шторка соседнего раздела (клик по карточке картинки) встаёт вместо этой: две шторки разом не живут
  useEffect(() => {
    if (!open) return;
    const off = (e: Event) => {
      const k = (e as CustomEvent<Partial<RevealPanelDetail>>).detail?.key;
      if (k && k !== SOUND_PANEL && isGenPanelKey(k)) setOpen(false);
    };
    window.addEventListener(REVEAL_PANEL_EVENT, off);
    return () => window.removeEventListener(REVEAL_PANEL_EVENT, off);
  }, [open]);

  if (!on || !narrow || !open) return null;
  // Во всю ширину ряда чипов: опущенная шторка — блок над полем ввода, а не чип
  return (
    <div data-sound-sheet="" style={{ flexBasis: '100%', minWidth: 0 }}>
      <SoundPanel ctx={{
        projectId: ctx.projectId, sessionId, isMobile: true,
        onClose: () => { markGenPanelDismissed(sessionId, SOUND_PANEL); setOpen(false); },
      }} />
    </div>
  );
}
