import type { ReactNode } from 'react';
import { C, FS, SP } from '../../lib/design';
import { Menu, MenuItem, MenuSep } from '../ui/Menu';

// Меню «Что обработать? / Что править?» — открывается приглушённым сегментом режима
// (GenerationModeSwitch.onMutedClick). Звуки или картинки чата свежими сверху
// (порядок готовит pickRows), отметка «правили последней», разделитель, доп. пункты
// («Склеить несколько…», «Из файлов проекта…»), подвал-подсказка. Пустой чат —
// строка «пока нет …» вместо списка, доп. пункты остаются.
// На телефоне — во всю ширину над полосой (fullWidth), строки по 40 px.
// У всех строк, включая доп. пункты, иконка на плитке 26×26 — колонка имён ровная.
// Вторая строка (мета, отметка) переносится, а не режется: на таче title нет.
// Якорь — левый край сегмента, меню открывается вверх (над полосой); чтобы оно
// встало ровно над полосой, хозяин передаёт прямоугольник с x сегмента и y полосы.

export interface GenerationPickRow {
  id: string;
  name: string;
  // Вторая строка: длительность · кто · когда
  sub?: string;
  // Миниатюра (адрес картинки) или иконка — что-то одно; миниатюра главнее
  thumb?: string;
  icon?: ReactNode;
  // Пометка в конце второй строки — «правили последней»
  mark?: string;
}

export interface GenerationPickExtra {
  key: string;
  label: string;
  icon?: ReactNode;
  hint?: string;
  disabled?: boolean;
  onClick: () => void;
}

const THUMB = 26;
// Кромка прямая, как .mi.last в макете: со скруглением она читалась скобкой «(»
const markStyle = { boxShadow: `inset 3px 0 0 ${C.accent}` };

export function GenerationPickMenu({
  title, subtitle, rows, onPick, extras = [], footer, emptyText, emptyHint, onClose,
  anchor, top, bottom, fullWidth, isMobile,
}: {
  title: string;
  subtitle?: string;
  rows: readonly GenerationPickRow[];
  onPick: (id: string) => void;
  extras?: readonly GenerationPickExtra[];
  footer?: ReactNode;
  // «В этом чате пока нет картинок» и подсказка, откуда их взять
  emptyText: string;
  emptyHint?: string;
  onClose: () => void;
  // Как у Menu: anchor — прямоугольник сегмента (портал, fixed), иначе absolute в родителе
  anchor?: DOMRect;
  top?: number;
  bottom?: number;
  fullWidth?: boolean;
  isMobile?: boolean;
}) {
  const rowH = isMobile ? 40 : undefined;
  return (
    <Menu onClose={onClose} anchor={anchor} anchorAlign="start" preferUp top={top} bottom={bottom} fullWidth={fullWidth} minWidth={300} maxWidth={360} maxHeight={420}>
      <div style={{ padding: `${SP.sm}px ${SP.md - 2}px ${SP.xs}px`, color: C.textHeading, fontSize: FS.base, fontWeight: 600 }}>
        {title}
        {subtitle && <div style={{ fontWeight: 400, fontSize: FS.xs, color: C.textMuted }}>{subtitle}</div>}
      </div>
      {rows.length === 0 ? (
        <div role="note" style={{ padding: `${SP.sm}px ${SP.md - 2}px`, fontSize: FS.sm, color: C.textSecondary }}>
          {emptyText}
          {emptyHint && <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>{emptyHint}</div>}
        </div>
      ) : rows.map(r => {
        const hint = [r.sub, r.mark].filter(Boolean).join(' · ') || undefined;
        return (
          <MenuItem
            key={r.id}
            iconSize={THUMB}
            iconTile
            icon={r.thumb
              ? <img src={r.thumb} alt="" style={{ width: THUMB, height: THUMB, objectFit: 'cover', display: 'block' }} />
              : r.icon}
            label={r.name}
            hint={hint}
            hintWrap
            onClick={() => onPick(r.id)}
            isMobile={isMobile}
            wrapper={r.mark || rowH ? { style: { ...(r.mark ? markStyle : null), ...(rowH ? { minHeight: rowH } : null) } } : undefined}
          />
        );
      })}
      {extras.length > 0 && <MenuSep />}
      {extras.map(x => (
        <MenuItem key={x.key} icon={x.icon} iconSize={THUMB} iconTile label={x.label} hint={x.hint} hintWrap disabled={x.disabled} onClick={x.onClick} isMobile={isMobile} />
      ))}
      {footer && (
        <div style={{ padding: `${SP.xs + 2}px ${SP.md - 2}px ${SP.sm}px`, fontSize: FS.xs, color: C.textMuted }}>{footer}</div>
      )}
    </Menu>
  );
}
