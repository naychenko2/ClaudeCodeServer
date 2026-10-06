// Монтажный список вкладки «Фильм» (макет v7): строки сцен с ⋮⋮ (перетаскивание), меню «⋯», метками
// «● обновлена», «✦ Claude», ⚠; между строками — склейка с выбором прямо под стыком; подрезка
// раскрывается в строке по ленте кадров, без попапа.

import { useState, type DragEvent } from 'react';
import { AlertTriangle, ArrowDown, ArrowUp, Clapperboard, FolderTree, GripVertical, MoreHorizontal, Scissors, Trash2 } from 'lucide-react';
import {
  api, Badge, Button, ByClaude, C, Dot, FS, IconButton, Menu, MenuItem, MenuSep, R, SegmentedControl, SHADOW, SP,
} from 'aihome_shell/kit';
import type { FilmCut, FilmCutType, FilmItem, FilmItemMark } from '../../api';
import { ic } from '../primitives';
import { TOUCH } from '../../useBoxWidth';
import { clampTrim, clipLength, CUT_LABEL, CUT_SECS, cutLabel, fileName, TRIM_STEP, trimLabel, trimmedLength } from '../../film/model';

export interface RowActions {
  onMove: (from: number, to: number) => void;
  onRemove: (i: number) => void;
  onTrim: (i: number, trim: [number, number]) => void;
  onCut: (i: number, type: FilmCutType, sec: number) => void;
  onReshoot: (i: number) => void;
  // «Работать со сценой» (редактор «Монтаж»): сцена становится основным объектом контекста; заменяет «Переснять»
  onWork?: (i: number) => void;
  // Сцена строки есть в этом чате (иначе «Работать» заведёт новую по снимку из фильма)
  hasScene?: (i: number) => boolean;
  onReveal: (i: number) => void;
}

// Миниатюра строки и номер на ней: размеры макета v7
const THUMB = { w: 64, h: 36, num: 14, numGap: 2, handle: 3, trimBtn: 20, cutBtn: 22, stepBtn: 24, dot: 6 } as const;
const FRAMES = 8;

const sec = (n: number) => `${String(n).replace('.', ',')} с`;

export function FilmList({ scope, items, cuts, marks, highlight, isMobile, a }: {
  scope: string; items: FilmItem[]; cuts: FilmCut[]; marks: FilmItemMark[]; highlight: number | null; isMobile: boolean; a: RowActions;
}) {
  const [drag, setDrag] = useState<number | null>(null);
  const [over, setOver] = useState<number | null>(null);
  const [cutOpen, setCutOpen] = useState<number | null>(null);
  const [trimOpen, setTrimOpen] = useState<number | null>(null);
  const drop = (to: number) => {
    if (drag !== null && drag !== to) a.onMove(drag, to);
    setDrag(null);
    setOver(null);
  };
  return (
    <div data-video-film-list="">
      {items.map((it, i) => (
        <div key={`${it.file}#${i}`}>
          <Row scope={scope} it={it} i={i} mark={marks.find(m => m.index === i)} last={i === items.length - 1}
            highlight={highlight === i} dragging={drag === i} over={over === i && drag !== i} isMobile={isMobile}
            trimOpen={trimOpen === i} onTrimToggle={() => setTrimOpen(trimOpen === i ? null : i)}
            onDragStart={() => setDrag(i)} onDragEnter={() => setOver(i)} onDrop={() => drop(i)} onDragEnd={() => { setDrag(null); setOver(null); }}
            a={a} />
          {i < items.length - 1 && (
            <Cut c={cuts[i]} i={i} isMobile={isMobile} open={cutOpen === i} onToggle={() => setCutOpen(cutOpen === i ? null : i)}
              onPick={(t, s) => a.onCut(i, t, s)} />
          )}
        </div>
      ))}
    </div>
  );
}

function Row({ scope, it, i, mark, last, highlight, dragging, over, isMobile, trimOpen, onTrimToggle, onDragStart, onDragEnter, onDrop, onDragEnd, a }: {
  scope: string; it: FilmItem; i: number; mark?: FilmItemMark; last: boolean; highlight: boolean; dragging: boolean; over: boolean;
  isMobile: boolean; trimOpen: boolean; onTrimToggle: () => void;
  onDragStart: () => void; onDragEnter: () => void; onDrop: () => void; onDragEnd: () => void; a: RowActions;
}) {
  const [menuAt, setMenuAt] = useState<DOMRect | null>(null);
  const src = api.files.fileUrl(scope, it.file);
  const prevent = (e: DragEvent) => { e.preventDefault(); };
  return (
    <div data-video-film-row={i} data-highlight={highlight ? 'true' : undefined}
      onDragOver={prevent} onDragEnter={onDragEnter} onDrop={e => { e.preventDefault(); onDrop(); }}
      style={{
        border: `1px solid ${highlight ? C.accent : over ? C.accentMuted : C.borderLight}`, borderRadius: R.lg, background: C.bgCard,
        boxShadow: highlight ? SHADOW.selected : undefined, opacity: dragging ? 0.5 : 1, overflow: 'hidden',
      }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: `${SP.xs}px ${SP.xs}px ${SP.xs}px 0` }}>
        {!isMobile && (
          <span draggable onDragStart={e => { e.dataTransfer.effectAllowed = 'move'; onDragStart(); }} onDragEnd={onDragEnd}
            title="Перетащите, чтобы поменять порядок" aria-label="Перетащить" data-video-drag=""
            style={{ display: 'inline-flex', color: C.textMuted, cursor: 'grab', padding: `0 ${SP.xxs}px` }}>{ic(GripVertical)}</span>
        )}
        <span style={{ position: 'relative', width: THUMB.w, height: THUMB.h, flexShrink: 0, borderRadius: R.sm, overflow: 'hidden', background: C.bgInset, marginLeft: isMobile ? SP.xs : 0 }}>
          {src && <video src={`${src}#t=0.1`} preload="metadata" muted playsInline style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />}
          <span style={{ position: 'absolute', left: THUMB.numGap, top: THUMB.numGap, minWidth: THUMB.num, height: THUMB.num, borderRadius: R.sm, background: C.bgCard, fontSize: FS.xs, lineHeight: `${THUMB.num}px`, textAlign: 'center', fontWeight: 700, color: C.textHeading }}>{i + 1}</span>
        </span>
        <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: SP.xxs / 2 }}>
          <span title={it.file} style={{ fontSize: FS.sm, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{fileName(it.file)}</span>
          <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.xs, color: C.textMuted }}>
            <Button size="xs" variant="ghost" onClick={onTrimToggle} title="Подрезать"
              style={{ height: isMobile ? TOUCH : THUMB.trimBtn, minHeight: isMobile ? TOUCH : undefined, padding: `0 ${SP.xxs}px` }}>{trimLabel(it)}</Button>
            {mark?.updated && <span data-video-mark="updated" title="Новый файл сцены — пересоберите"><Badge size="xs" tone="info" icon={<Dot color={C.info} size={THUMB.dot} />}>обновлена</Badge></span>}
            {mark?.claude && <ByClaude />}
            {mark?.stale && <span data-video-mark="stale" title="Текст или кадр изменён — переснимите" style={{ color: C.warningText, display: 'inline-flex' }}>{ic(AlertTriangle)}</span>}
          </span>
        </span>
        <IconButton size={isMobile ? 'lg' : 'xs'} title="Действия со сценой" ariaLabel="Действия со сценой" style={isMobile ? { width: TOUCH, height: TOUCH } : undefined}
          onClick={e => setMenuAt((e.currentTarget as HTMLElement).getBoundingClientRect())}>{ic(MoreHorizontal)}</IconButton>
      </div>
      {trimOpen && <Trim scope={scope} it={it} isMobile={isMobile} onDone={t => { a.onTrim(i, t); onTrimToggle(); }} />}
      {menuAt && (
        <Menu onClose={() => setMenuAt(null)} anchor={menuAt} anchorAlign="end" minWidth={240}>
          {a.onWork
            ? <MenuItem icon={ic(Clapperboard)} label="Работать со сценой" hint={a.hasScene?.(i) === false ? 'Сцены нет в этом чате — заведём новую' : 'Станет основной в контексте'}
              onClick={() => { setMenuAt(null); a.onWork?.(i); }} />
            : <MenuItem icon={ic(Clapperboard)} label="Переснять" hint="Вкладка «Сцена»" onClick={() => { setMenuAt(null); a.onReshoot(i); }} />}
          <MenuItem icon={ic(Scissors)} label="Подрезать…" onClick={() => { setMenuAt(null); onTrimToggle(); }} />
          <MenuItem icon={ic(ArrowUp)} label="Раньше" disabled={i === 0} onClick={() => { setMenuAt(null); a.onMove(i, i - 1); }} />
          <MenuItem icon={ic(ArrowDown)} label="Позже" disabled={last} onClick={() => { setMenuAt(null); a.onMove(i, i + 1); }} />
          <MenuItem icon={ic(FolderTree)} label="Показать в дереве" onClick={() => { setMenuAt(null); a.onReveal(i); }} />
          <MenuSep />
          <MenuItem icon={ic(Trash2)} label="Убрать из фильма" hint="Файл останется в проекте" onClick={() => { setMenuAt(null); a.onRemove(i); }} />
        </Menu>
      )}
    </div>
  );
}

// Склейка под стыком: «| встык», «≈ наплыв 1 с», «■ затемнение 1 с»; клик — выбор прямо здесь
function Cut({ c, i, isMobile, open, onToggle, onPick }: { c: FilmCut | undefined; i: number; isMobile: boolean; open: boolean; onToggle: () => void; onPick: (t: FilmCutType, s: number) => void }) {
  const type = c?.type ?? 'butt';
  const s = c && c.type !== 'butt' ? c.sec : 1;
  return (
    <div data-video-cut={i} style={{ margin: `${SP.xxs}px 0`, paddingLeft: SP.lg }}>
      <Button size="xs" variant="ghost" onClick={onToggle} aria-expanded={open} title="Склейка между сценами"
        style={{ height: isMobile ? TOUCH : THUMB.cutBtn, minHeight: isMobile ? TOUCH : undefined, color: C.textSecondary }}>{cutLabel(c)}</Button>
      {open && (
        <div style={{ margin: `${SP.xs}px 0 ${SP.sm}px`, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md, background: C.bgCard }}>
          <SegmentedControl<FilmCutType> value={type} onChange={t => onPick(t, t === 'butt' ? 0 : s)}
            options={(['butt', 'dissolve', 'fade'] as const).map(t => ({ value: t, label: CUT_LABEL[t] }))} />
          {type !== 'butt' && (
            <div style={{ marginTop: SP.xs }}>
              <SegmentedControl<string> value={String(s)} onChange={v => onPick(type, Number(v))}
                options={CUT_SECS.map(n => ({ value: String(n), label: sec(n) }))} />
            </div>
          )}
          <div style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textMuted }}>
            Бесплатно, генерация не нужна — меняет только сборку. Наплыв накладывает клипы и укорачивает фильм.
          </div>
        </div>
      )}
    </div>
  );
}

// Подрезка в строке: плёнка из восьми кадров клипа, затемнённые края, ± по 0,5 с
function Trim({ scope, it, isMobile, onDone }: { scope: string; it: FilmItem; isMobile: boolean; onDone: (t: [number, number]) => void }) {
  const full = clipLength(it) || 1;
  const [t, setT] = useState<[number, number]>([it.trim[0] ?? 0, it.trim[1] ?? full]);
  const set = (n: [number, number]) => setT(clampTrim(n, full));
  const src = api.files.fileUrl(scope, it.file);
  const left = (t[0] / full) * 100;
  const right = 100 - (t[1] / full) * 100;
  const box = isMobile ? TOUCH : THUMB.stepBtn;
  const stepBox = { minWidth: box, height: box, minHeight: box, padding: 0 } as const;
  const step = (label: string, v: number, on: (d: number) => void) => (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, fontSize: FS.xs, color: C.textSecondary }}>
      {label}
      <Button size="xs" variant="secondary" onClick={() => on(-TRIM_STEP)} title={`${label} − 0,5 с`} style={stepBox}>−</Button>
      <b style={{ minWidth: 36, textAlign: 'center', color: C.textHeading }}>{sec(v)}</b>
      <Button size="xs" variant="secondary" onClick={() => on(TRIM_STEP)} title={`${label} + 0,5 с`} style={stepBox}>+</Button>
    </span>
  );
  return (
    <div data-video-trim="" style={{ padding: `0 ${SP.sm}px ${SP.sm}px` }}>
      <div style={{ position: 'relative', display: 'flex', height: THUMB.h, borderRadius: R.sm, overflow: 'hidden', background: C.bgInset }}>
        {Array.from({ length: FRAMES }, (_, k) => (
          <span key={k} style={{ flex: 1, minWidth: 0, borderRight: k < FRAMES - 1 ? `1px solid ${C.bgCard}` : undefined }}>
            {src && <video src={`${src}#t=${((k + 0.5) * full / FRAMES).toFixed(2)}`} preload="metadata" muted playsInline
              style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />}
          </span>
        ))}
        <span style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: `${left}%`, background: C.overlay }} />
        <span style={{ position: 'absolute', right: 0, top: 0, bottom: 0, width: `${right}%`, background: C.overlay }} />
        <span style={{ position: 'absolute', left: `${left}%`, top: 0, bottom: 0, width: THUMB.handle, background: C.accent }} />
        <span style={{ position: 'absolute', right: `${right}%`, top: 0, bottom: 0, width: THUMB.handle, background: C.accent }} />
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.sm, marginTop: SP.xs }}>
        {step('Начало', t[0], d => set([t[0] + d, t[1]]))}
        {step('Конец', t[1], d => set([t[0], t[1] + d]))}
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs }}>
        <span style={{ flex: 1, fontSize: FS.xs, color: C.textMuted }}>
          Шаг 0,5 с · остаётся {sec(trimmedLength({ ...it, trim: t }))} из {sec(full)} · файл сцены не меняется
        </span>
        <Button size="xs" onClick={() => onDone(t)} style={isMobile ? { height: TOUCH, minHeight: TOUCH } : undefined}>Готово</Button>
      </div>
    </div>
  );
}
