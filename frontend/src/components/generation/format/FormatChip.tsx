// Свёрнутый чип «Формат» в строке контекста (ADR-025, таблица «Тексты чипа» записки): пропорции и размер,
// ✦ у формата агента, 🔒 у сцены фильма, ⚠ у предупреждения, жёлтый при округлении, приглушённый у
// «Увеличить» и «Как у модели». Подпись считает вид (FormatModel.chip), здесь только отрисовка.
import { Lock, RectangleHorizontal, TriangleAlert } from 'lucide-react';
import { C, FS, SP } from '../../../lib/design';
import type { FormatModel } from '../../../lib/chatContext/formatTypes';
import { RowChip, ellipsis } from '../../chat/RowChip';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';

// Ширина чипа на ступенях лестницы строки: полная форма и короткая (телефон, теснота)
export const FORMAT_CHIP_MAX = { full: 250, short: 150 } as const;

// bare — без размера («16:9», subBare; у «Как у исходника» вместо подписи только пропорции, mainBare): телефон и самая узкая ступень лестницы
export function FormatChip({ m, short, bare, open, onOpen, onAgentTip }: {
  m: FormatModel; short: boolean; bare?: boolean; open: boolean; onOpen: (e: { currentTarget: HTMLElement }) => void; onAgentTip?: () => void;
}) {
  const c = m.chip;
  const sub = bare && c.subBare !== undefined ? c.subBare : short && c.subShort !== undefined ? c.subShort : c.sub;
  const main = bare && c.mainBare !== undefined ? c.mainBare : c.main;
  const title = `Формат: пропорции и размер${m.by === 'agent' ? ' · поставил Claude' : ''}${m.locked ? ` · как у фильма (${m.locked.aspect})` : ''}`;
  return (
    <RowChip kind="format" max={short ? FORMAT_CHIP_MAX.short : FORMAT_CHIP_MAX.full} title={title} onClick={onOpen} dim={c.tone === 'dim'}
      warn={c.tone === 'warn' || !!m.mismatch} borderColor={open ? C.accent : undefined}>
      <RectangleHorizontal size={ICON_SIZE.xs - 1} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      {c.prefix && !short && <span style={{ color: C.textMuted, flexShrink: 0 }}>{c.prefix}</span>}
      <b data-fmt-chip-main="" style={{ ...ellipsis, fontWeight: 600, color: c.tone === 'warn' ? 'inherit' : C.textHeading }}>{main}</b>
      {sub && <span data-fmt-chip-sub="" style={{ ...ellipsis, color: c.tone === 'warn' ? 'inherit' : C.textMuted, flexShrink: 1 }}>{sub}</span>}
      {m.locked && <Lock size={ICON_SIZE.xs - 3} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} aria-label="Как у фильма" />}
      {(m.mismatch || m.frames) && <TriangleAlert size={ICON_SIZE.xs - 3} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} aria-label="Предупреждение" />}
      {m.by === 'agent' && (
        <button type="button" data-agent-mark="" title="Формат поставил Claude · ручная правка снимет метку" aria-label="Формат поставил Claude"
          onClick={e => { e.stopPropagation(); onAgentTip?.(); }}
          style={{ flexShrink: 0, fontSize: FS.xs, color: C.accent, lineHeight: 1, border: 'none', background: 'transparent', padding: 0, cursor: 'pointer', marginLeft: SP.xxs }}>✦</button>
      )}
    </RowChip>
  );
}
