// Строка-ссылка «Видеокарты» — замена карточкам GpuCard на вкладках «LLM» и
// «Медиа» (только у админа). Card с role="button": касание переключает на вкладку
// «Железо». Содержимое: точка худшего уровня карт, «память 68° · 81°», плашки
// «в лимите мощности» (info) / «перегрев» (danger), «Железо →» (вправо).
//
// Данные — из поля hardware (HardwareBrief) ответа /stats или /stats/media
// (только админ; у не-админа поля нет → строки нет, ADR-030). LLM — по всем картам
// стенда; Медиа — только по картам с ролью media (пересечение brief и media.gpus по uuid).
// stale — строка приглушена (C.textMuted), без окраски.

import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import type { HardwareBrief } from './hardwareTypes';
import { hwBriefFlags, hwBriefMem, hwBriefGpusLevel } from './hardwareFormat';

// Сколько карт: «Видеокарты» (мн.) / «Видеокарта» (ед.).
function title(n: number): string {
  return n > 1 ? 'Видеокарты' : 'Видеокарта';
}

export function HwLinkRow({ brief, stale, onOpen }: {
  brief: HardwareBrief;
  stale?: boolean;
  onOpen: () => void;
}) {
  if (brief.gpus.length === 0) return null;
  const level = hwBriefGpusLevel(brief);
  const flags = hwBriefFlags(brief);
  const dotColor = stale ? C.track
    : level === 'danger' ? C.danger : level === 'warn' ? C.warning : C.success;

  return (
    <div
      role="button"
      tabIndex={0}
      aria-label="Видеокарты — открыть вкладку «Железо»"
      onClick={onOpen}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen(); } }}
      style={{
        display: 'flex', alignItems: 'center', gap: '6px 8px', flexWrap: 'wrap', cursor: 'pointer',
        background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
        padding: `${SP.sm}px ${SP.md}px`,
      }}
    >
      <span style={{ width: 8, height: 8, borderRadius: R.full, background: dotColor, flex: 'none' }} />
      <span style={{ fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.04em', color: C.textMuted }}>
        {title(brief.gpus.length)}
      </span>
      <span style={{ fontSize: FS.sm, color: stale ? C.textMuted : C.textSecondary, whiteSpace: 'nowrap' }}>
        {hwBriefMem(brief)}
      </span>
      {!stale && flags.thermal && (
        <span style={{ display: 'inline-flex', background: C.dangerBg, color: C.dangerText, borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap' }}>
          перегрев
        </span>
      )}
      {flags.powerCap && (
        <span style={{ display: 'inline-flex', background: C.infoBg, color: C.info, borderRadius: R.max, padding: `1px ${SP.sm}px`, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap' }}>
          в лимите мощности
        </span>
      )}
      <span style={{ marginLeft: 'auto', fontSize: FS.xs, fontWeight: 600, color: C.accent, whiteSpace: 'nowrap', fontFamily: FONT.sans }}>
        Железо →
      </span>
    </div>
  );
}
