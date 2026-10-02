// Строки меню «Что обработать?» (макет audio-panel-v4-proposal.md, вариант 3): звуки этого чата,
// которые можно обработать, свежие сверху. Чистая функция — рисует их общий GenerationPickMenu.

import { pickRows } from 'aihome_shell/kit';
import type { AudioMode, AudioThread } from '../api';
import { focusLabel } from '../strip/summary';
import { hasSound } from './modeState';

export interface SoundPickRow {
  id: string;
  name: string;
  // «вы · 5 мин назад» / «агент · 2 ч назад»
  sub: string;
  // Режим, которым звук получен: по нему иконка строки; null — прикреплённый файл
  mode: AudioMode | null;
}

const ms = (iso: string | null | undefined) => {
  const t = iso ? Date.parse(iso) : NaN;
  return Number.isFinite(t) ? t : 0;
};

// Кто сделал текущую версию: запуск с её задачей; исходник прикреплённого файла — без автора
function author(t: AudioThread): string | null {
  const v = t.versions.find(x => x.id === t.currentVersionId);
  const launch = v?.jobId ? t.launches.find(l => l.jobId === v.jobId) : null;
  if (!launch) return null;
  return launch.initiator === 'agent' ? 'агент' : 'вы';
}

// Последнее изменение нити — по нему «свежие сверху»
function touchedAt(t: AudioThread): number {
  return Math.max(ms(t.createdAt), ...t.versions.map(v => ms(v.createdAt)), ...t.launches.map(l => ms(l.at)));
}

// Черновик без версии в меню не попадает: обрабатывать в нём нечего. Звук в работе — тоже:
// с ним «Обработка» и так доступна. ago — «5 мин назад» по ISO-времени
export function soundPickRows(threads: readonly AudioThread[], ago: (iso: string) => string, focusId: string | null = null): SoundPickRow[] {
  const cands = threads.map(t => ({ id: t.id, at: touchedAt(t), hidden: !hasSound(t), thread: t }));
  return pickRows(cands, { excludeId: focusId }).map(({ thread: t, at }) => ({
    id: t.id,
    name: focusLabel(t),
    sub: [author(t), at ? ago(new Date(at).toISOString()) : null].filter(Boolean).join(' · '),
    mode: t.settings?.mode ?? null,
  }));
}
