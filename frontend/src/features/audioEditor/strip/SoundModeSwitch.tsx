// Переключатель «Голос / Музыка / Обработка» — зеркало в полосе и в панели (макет
// audio-panel-v4-proposal.md, вариант 3). Оба места зовут setSoundMode, выбор общий.
// «Обработка» без звука в работе приглушена и спрашивает «Что обработать?»: звуки чата
// свежими сверху и «Склеить несколько…». Детали — из общего слоя кита.

import { useState } from 'react';
import type { RefObject } from 'react';
import { AudioLines, Combine, Mic, Music, SlidersHorizontal } from 'lucide-react';
import {
  GenerationModeSwitch, GenerationPickMenu, gitRelTime, ICON_SIZE, ICON_STROKE,
  type GenerationModeOption, type GenerationPickRow,
} from 'aihome_shell/kit';
import type { AudioMode, AudioThread } from '../api';
import { MODE_LABEL } from '../ops';
import { processThreadByHuman, setSoundMode, startConcat } from '../thread/actions';
import { hasSound } from '../thread/modeState';
import { soundPickRows } from '../thread/pickMenu';

export const MODE_ICON: Record<AudioMode, typeof Mic> = { voice: Mic, music: Music, process: SlidersHorizontal };

export function soundModeOptions(thread: AudioThread | null): GenerationModeOption<AudioMode>[] {
  const muted = !hasSound(thread);
  return (['voice', 'music', 'process'] as const).map(m => ({
    value: m, label: MODE_LABEL[m], icon: MODE_ICON[m],
    ...(m === 'process' && muted ? { muted: true, title: 'Выбрать звук этого чата для обработки' } : {}),
  }));
}

const rowIcon = (mode: AudioMode | null) => {
  const I = mode ? MODE_ICON[mode] : AudioLines;
  return <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;
};

export function SoundModeSwitch({ scope, sessionId, mode, thread, threads, isMobile, compact, quiet, bar }: {
  scope: string;
  sessionId: string | null;
  mode: AudioMode;
  thread: AudioThread | null;
  threads: readonly AudioThread[];
  isMobile?: boolean;
  compact?: boolean;
  quiet?: boolean;
  // Полоса: меню встаёт над ней целиком, а не над сегментом
  bar?: RefObject<HTMLElement | null>;
}) {
  const [menuAt, setMenuAt] = useState<DOMRect | null>(null);
  const close = () => setMenuAt(null);
  const open = (seg: DOMRect) => {
    const b = bar?.current?.getBoundingClientRect();
    setMenuAt(b ? new DOMRect(seg.left, b.top, seg.width, b.height) : seg);
  };
  const rows: GenerationPickRow[] = menuAt
    ? soundPickRows(threads, gitRelTime, thread?.id ?? null).map(r => ({ id: r.id, name: r.name, sub: r.sub || undefined, icon: rowIcon(r.mode) }))
    : [];
  const pick = (id: string) => {
    close();
    if (sessionId) void processThreadByHuman(scope, sessionId, id);
  };
  return (
    <>
      <span data-sound-mode-switch="" style={{ display: 'inline-flex', flexShrink: 0 }}>
        <GenerationModeSwitch<AudioMode>
          value={mode}
          options={soundModeOptions(thread)}
          onChange={m => { setSoundMode(scope, sessionId, m); }}
          onMutedClick={(_, a) => open(a)}
          compact={compact}
          quiet={quiet}
          isMobile={isMobile}
        />
      </span>
      {menuAt && (
        <GenerationPickMenu
          title="Что обработать?"
          subtitle="Звуки этого чата, свежие сверху"
          rows={rows}
          onPick={pick}
          emptyText="В этом чате пока нет звуков"
          emptyHint="Создайте звук в «Голосе» или «Музыке» или прикрепите файл через «＋»"
          extras={[{
            key: 'concat', label: 'Склеить несколько…', hint: 'без ИИ · бесплатно · куски выбираются в панели',
            icon: <Combine size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, disabled: rows.length === 0,
            onClick: () => { close(); void startConcat(scope, sessionId); },
          }]}
          footer="Или «Обработать ▾» на карточке в ленте"
          onClose={close}
          anchor={menuAt}
          fullWidth={isMobile}
          isMobile={isMobile}
        />
      )}
    </>
  );
}
