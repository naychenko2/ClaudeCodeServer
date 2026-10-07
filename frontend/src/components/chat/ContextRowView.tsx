// Строка контекста хода над полем ввода (ADR-023, макет docs/mockups/composer-context-row-v1):
// «Где» (ветка), «С чем» (основной объект), «Чем» (исполнитель), «Плюс» (референсы и «+N ›»).
// Здесь только отрисовка по готовой модели: подписи объекта и референсов приходят из DTO
// (`label`, `version`), своих форматтеров нет. Ширина — снаружи; форму каждого чипа выбирает
// чистая лестница lib/chatContext/ladder.ts, открытость панели в неё не входит.
import { createContext, useContext, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from 'react';
import { ChevronDown, ChevronRight, CloudUpload, Cpu, Eye, FolderGit2, GitBranch, MessageSquare, Plus, RotateCcw, Send, Trash2, X, Check, Info } from 'lucide-react';
import { C, COMPOSER_LIP, FONT, FS, R, SP, SHADOW, Z, composerLip } from '../../lib/design';
import { plural } from '../../lib/plural';
import { contextRowLadder, CAP, gitChipBox, ladderRungs, NOM, type LadderFacts, type LadderPick } from '../../lib/chatContext/ladder';
import { unusedBy } from '../../lib/chatContext/fill';
import { roleLabel } from '../../lib/chatContext/roleLabels';
import { baseName } from '../../lib/chatContext/labels';
import type { ChatContextPrimary, ChatContextRef } from '../../lib/chatContext/types';
import { Badge, Button, Menu, MenuItem, MenuSep, Modal } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { groupExecutorRows, rowPriceShort, EXECUTOR_GROUP_LABEL, type ExecutorRow } from '../generation/ExecutorList';

export const ROW_H = 30;
export const ROW_H_MOBILE = 32;
const CHIP_H = 22;

// ── Модель строки ──

export interface RowGit {
  label: string;
  changes: number;
  // Строки диффа рабочего дерева — для пилюли «+N −M» у правого края губы
  added?: number;
  deleted?: number;
  ahead: number;
  publishN: number;
  onCommitOwn: () => void;
  onCommitAll: () => void;
  onPublish: () => void;
  onShowChanges: () => void;
}

export interface RowExec {
  rows: readonly ExecutorRow[];
  value: string;
  onChange: (id: string) => void;
  // Заголовок меню: «Чем выполнить» или «Чем выполнить «Стемы»»
  title: string;
}

// until — срок плашки в сторе (мс, Date.now()): отсчёт «N с» считается от него
export interface RowOffer { text: string; until?: number }

export interface ContextRowViewProps {
  // Ширина строки в CSS-пикселях; null — ещё не измерена (берём самую широкую форму)
  width: number | null;
  isMobile: boolean;
  git: RowGit | null;
  primary: ChatContextPrimary | null;
  refs: readonly ChatContextRef[];
  exec: RowExec | null;
  // Подпись выбранного действия: серыми становятся референсы, которых оно не берёт; null — «Чат»
  actionLabel: string | null;
  // Операция выбранного действия: серый референс считается по ней; нет операции — по пустому usedBy
  actionOp?: string | null;
  iconOf: (kind: string) => ReactNode;
  offer: RowOffer | null;
  onUndo: () => void;
  onRelease: () => void;
  // Открыть объект в правой панели; нет панели — чип не кликабелен
  onOpenPrimary?: () => void;
  onDetach: (itemId: string) => void;
  onClear: () => void;
  // Витрина: принудительная ступень вместо лестницы
  pick?: LadderPick;
}

export const rowFacts = (p: Pick<ContextRowViewProps, 'git' | 'primary' | 'refs' | 'exec' | 'isMobile'>): LadderFacts => ({
  project: !!p.git,
  hasPrimary: !!p.primary,
  hasExec: !!p.exec,
  refs: p.refs.length,
  mobile: p.isMobile,
});

// Строка рисуется, пока есть ветка (проект) или объект; в личном чате без объекта её нет
export const showsRow = (p: Pick<ContextRowViewProps, 'git' | 'primary'>) => !!p.git || !!p.primary;

// ── Примитивы ──

const act = (fn: () => void) => (e: KeyboardEvent) => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fn(); }
};

// Чипы внутри губы плоские: рамку держит сама губа, вторая рамка у каждого чипа — «рамка в рамке».
// Остаются только рамки-сигналы: пунктир серого референса и цвет borderColor
const FlatChips = createContext(false);
// Высота чипа: на телефоне крупнее — это тач-цель под палец
const ChipHeight = createContext(CHIP_H);
const CHIP_H_MOBILE = 28;
// Телефон, строки «Где» и «С чем»: чип ужимается по ширине строки (имя — многоточием), а не уезжает за край
const ShrinkChips = createContext(false);

function RowChip({ kind, max, title, onClick, borderColor, dim, dashed, mono, children }: {
  kind: string;
  max: number;
  title?: string;
  onClick?: (e: { currentTarget: HTMLElement }) => void;
  borderColor?: string;
  dim?: boolean;
  dashed?: boolean;
  mono?: boolean;
  children: ReactNode;
}) {
  const flat = useContext(FlatChips);
  const chipH = useContext(ChipHeight);
  const shrink = useContext(ShrinkChips);
  const framed = !flat || dashed || borderColor != null;
  return (
    <span
      data-chip={kind}
      {...(dashed ? { 'data-gray': '' } : null)}
      title={title}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      onClick={onClick ? e => onClick(e) : undefined}
      onKeyDown={onClick ? e => act(() => onClick({ currentTarget: e.currentTarget as HTMLElement }))(e) : undefined}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs + 1, flexShrink: shrink ? 1 : 0, boxSizing: 'border-box',
        height: chipH, maxWidth: shrink ? `min(${max}px, 100%)` : max, padding: `0 ${SP.sm - 1}px`, borderRadius: R.md, minWidth: 0,
        border: framed ? `1px ${dashed ? 'dashed' : 'solid'} ${borderColor ?? C.border}` : '1px solid transparent',
        background: flat ? 'transparent' : C.bgCard,
        color: C.textSecondary, fontSize: FS.sm, fontFamily: mono ? FONT.mono : FONT.sans, whiteSpace: 'nowrap',
        cursor: onClick ? 'pointer' : 'default', opacity: dim ? 0.55 : 1,
      }}
    >
      {children}
    </span>
  );
}

const ellipsis = { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 } as const;

function XBtn({ title, onClick }: { title: string; onClick: () => void }) {
  return (
    <button type="button" data-chip-x="" title={title} aria-label={title}
      onClick={e => { e.stopPropagation(); onClick(); }}
      style={{
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, width: 16, height: 16,
        padding: 0, border: 'none', background: 'transparent', color: C.textMuted, cursor: 'pointer', borderRadius: R.sm,
      }}>
      <X size={ICON_SIZE.xs - 3} strokeWidth={ICON_STROKE} />
    </button>
  );
}

function Thumb({ item, icon, round }: { item: ChatContextPrimary | ChatContextRef; icon: ReactNode; round?: boolean }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: 18, height: 18, flexShrink: 0,
      borderRadius: round ? R.max : R.sm, overflow: 'hidden', background: C.bgInset, color: C.accent,
    }}>
      {item.thumb ? <img src={item.thumb} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : icon}
    </span>
  );
}

function AgentMark({ title, onClick }: { title: string; onClick?: () => void }) {
  const style = { flexShrink: 0, fontSize: FS.xs, color: C.accent, lineHeight: 1 } as const;
  return onClick
    ? <button type="button" data-agent-mark="" title={title} aria-label={title} onClick={e => { e.stopPropagation(); onClick(); }}
        style={{ ...style, border: 'none', background: 'transparent', padding: 0, cursor: 'pointer' }}>✦</button>
    : <span data-agent-mark="" title={title} style={style}>✦</span>;
}

// ── Чипы ──

export const changesWord = (n: number) => `${n} ${plural(n, 'изменение', 'изменения', 'изменений')}`;

function gitTitle(g: RowGit) {
  return `Ветка ${g.label} · ${g.changes ? changesWord(g.changes) : 'нет изменений'}${g.publishN ? ` · ${g.publishN} к публикации` : ''}`;
}

function GitChip({ g, form, onOpen, bare }: { g: RowGit; form: 0 | 1 | 2 | 3; onOpen: (r: DOMRect) => void; bare?: boolean }) {
  const icon = <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;
  // bare — справа в губе стоят пилюля и кнопки Git: чип — только ветка, без счётчиков и без
  // меню (всё, что в нём было, теперь кнопками)
  // Геометрия — как у кнопки режима в нижней губе (и прежнего «Git ▾»): высота 28, поле 10,
  // иконка 14, зазор 6 — иконки двух губ встают в одну вертикаль. Имя ветки — моно 12.5
  if (bare) {
    return (
      <span data-chip="git" data-git-bare="" title={gitTitle(g)} style={{
        display: 'flex', alignItems: 'center', gap: GIT_ICON_GAP, minWidth: 0,
        height: GIT_BTN_H, padding: `0 ${LIP_BTN_PAD_X}px`, flexShrink: 1,
      }}>
        <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />
        <span style={{ ...ellipsis, fontFamily: FONT.mono, fontSize: GIT_FONT, color: C.textSecondary }}>{g.label}</span>
      </span>
    );
  }
  const clean = !g.changes && !g.publishN;
  const up = g.publishN > 0 ? <span style={{ color: C.accent, fontWeight: 600, flexShrink: 0 }}>↑{g.publishN}</span> : null;
  const count = g.changes ? <span style={{ fontWeight: 600, flexShrink: 0 }}>{g.changes}</span> : null;
  const box = gitChipBox(form);
  const max = box.chip;
  const open = (e: { currentTarget: HTMLElement }) => onOpen(e.currentTarget.getBoundingClientRect());
  if (form === 0) {
    // Телефон: иконка с бейджем-счётчиком
    const badge = g.changes || g.publishN;
    return (
      <RowChip kind="git" max={max} title={gitTitle(g)} onClick={open} mono>
        {icon}
        {badge > 0 && <span style={{ fontWeight: 700, color: C.textHeading }}>{g.changes ? g.changes : `↑${g.publishN}`}</span>}
      </RowChip>
    );
  }
  return (
    <RowChip kind="git" max={max} title={gitTitle(g)} onClick={open} mono>
      {icon}
      {form >= 2 && <span style={box.label === null ? ellipsis : { ...ellipsis, maxWidth: box.label }}>{g.label}</span>}
      <span style={{ color: C.textMuted, flexShrink: 0 }}>·</span>
      {form === 3
        ? (g.changes ? <span style={{ flexShrink: 0 }}>{changesWord(g.changes)}</span> : (g.publishN ? null : <span style={{ flexShrink: 0 }}>нет изменений</span>))
        : (count ?? (clean ? <span style={{ flexShrink: 0 }}>чисто</span> : null))}
      {form === 3 && g.publishN > 0 && g.changes > 0 && <span style={{ color: C.textMuted, flexShrink: 0 }}>·</span>}
      {up}
      <ChevronDown size={ICON_SIZE.xs - 2} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
    </RowChip>
  );
}

function PrimaryChip({ p, form, icon, onOpen, onRelease, onAgentTip }: {
  p: ChatContextPrimary; form: 1 | 2; icon: ReactNode; onOpen?: () => void; onRelease: () => void; onAgentTip: () => void;
}) {
  const agent = p.by === 'agent';
  const ver = p.version ? ` · ${p.version}` : '';
  const title = `${p.label}${ver}${agent ? ' · взял в работу Claude' : ''}${onOpen ? ' — открыть в панели' : ''}`;
  // В губе основной объект выделен не рамкой, а именем: заголовочный цвет и полужирный против
  // приглушённых референсов — иначе он сливается с ними
  const flat = useContext(FlatChips);
  return (
    <RowChip kind="primary" max={form === 1 ? NOM.o1 : CAP.o2} title={title} onClick={onOpen} borderColor={flat ? undefined : C.accentMuted} dim={p.missing}>
      <Thumb item={p} icon={icon} />
      <span data-chip-label="" style={flat ? { ...ellipsis, color: C.textHeading, fontWeight: 600 } : ellipsis}>{baseName(p.label, true)}</span>
      {p.version && <span style={{ color: C.textMuted, flexShrink: 0 }}>· {p.version}</span>}
      {agent && <AgentMark title="Взял в работу Claude" onClick={onAgentTip} />}
      <XBtn title={`Снять «${p.label}» с работы`} onClick={onRelease} />
    </RowChip>
  );
}

function ExecChip({ e, form, onOpen }: { e: RowExec; form: 1 | 2 | 3; onOpen: (r: DOMRect) => void }) {
  const row = e.rows.find(r => r.id === e.value) ?? e.rows[0];
  if (!row) return null;
  // «Авто» с названием модели в now: «Авто · локально · Qwen»; строка группы auto с другим именем («Без ИИ») — как обычная
  const auto = row.group === 'auto' && row.name === 'Авто';
  const price = rowPriceShort(row);
  const free = !!row.free;
  const full = auto ? `Авто${row.now ? ` · ${row.now}` : ''}` : `${row.sub ? `${row.sub} · ` : ''}${row.name}`;
  const title = `Чем: ${full} · ${price} — сменить исполнителя`;
  const open = (ev: { currentTarget: HTMLElement }) => onOpen(ev.currentTarget.getBoundingClientRect());
  const badge = <Badge size="xs" tone={free ? 'success' : 'neutral'}>{price}</Badge>;
  // В губе вес держит только основной объект: имя исполнителя обычным начертанием, «Чем» узнаётся по бейджу цены
  const flat = useContext(FlatChips);
  const name = flat ? { fontWeight: 400 } : { color: C.textHeading, fontWeight: 600 };
  return (
    <RowChip kind="exec" max={NOM[`e${form}` as 'e1']} title={title} onClick={open}>
      {form === 1
        ? <Cpu size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        : form === 2
          ? <b style={{ ...ellipsis, ...name }}>{row.name}</b>
          : <>
              <span style={{ color: C.textMuted, flexShrink: 0 }}>Чем:</span>
              <span style={ellipsis}>
                {auto
                  ? <><b style={name}>Авто</b>{row.now ? ` · ${row.now}` : ''}</>
                  : <>{row.sub ? `${row.sub} · ` : ''}<b style={name}>{row.name}</b></>}
              </span>
            </>}
      <span style={{ flexShrink: 0, display: 'inline-flex' }}>{badge}</span>
      <ChevronDown size={ICON_SIZE.xs - 2} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
    </RowChip>
  );
}

function RefPill({ r, gray, icon, grayHint, onDetach }: {
  r: ChatContextRef; gray: boolean; icon: ReactNode; grayHint: string; onDetach: () => void;
}) {
  const role = roleLabel(r.role);
  const title = gray ? grayHint : `${r.label}${role ? ` · ${role}` : ''}${r.by === 'agent' ? ' · подключил Claude' : ''}`;
  return (
    <RowChip kind="ref" max={CAP.ref} title={title} dim={gray || r.missing} dashed={gray}>
      <Thumb item={r} icon={icon} round={r.role === 'char' || r.role === 'character'} />
      <span style={{ ...ellipsis, textDecoration: gray ? 'line-through' : undefined }}>{baseName(r.label, true)}</span>
      {r.by === 'agent' && <AgentMark title="Подключил Claude" />}
      <XBtn title={`Отключить «${r.label}»`} onClick={onDetach} />
    </RowChip>
  );
}

// ── Всплывашки ──

export function MenuHead({ children }: { children: ReactNode }) {
  return <div style={{ padding: `${SP.xs + 2}px ${SP.md - 2}px`, fontSize: FS.sm, color: C.textMuted }}>{children}</div>;
}

export function GitMenuBody({ g, close }: { g: RowGit; close: () => void }) {
  const files = `${g.changes} ${plural(g.changes, 'файл', 'файла', 'файлов')}`;
  const run = (fn: () => void) => () => { close(); fn(); };
  return (
    <>
      <MenuHead><b style={{ color: C.textHeading, fontFamily: FONT.mono }}>{g.label}</b> · {g.changes ? changesWord(g.changes) : 'нет изменений'}{g.publishN ? ` · ↑${g.publishN}` : ''}</MenuHead>
      <MenuItem icon={<Check size={15} strokeWidth={ICON_STROKE} />} label="Зафиксировать только этот чат"
        hint={g.changes ? `${files}, которые правил этот чат` : 'изменений этого чата нет'} disabled={!g.changes} onClick={run(g.onCommitOwn)} />
      <MenuItem icon={<Check size={15} strokeWidth={ICON_STROKE} />} label="Зафиксировать всё дерево"
        hint={g.changes ? `${files} · всё, что изменено в дереве` : 'дерево чистое'} disabled={!g.changes} onClick={run(g.onCommitAll)} />
      <MenuItem icon={<Send size={15} strokeWidth={ICON_STROKE} />} label={g.publishN ? `Опубликовать ${g.publishN}` : 'Опубликовать'}
        hint={g.publishN ? `${g.publishN} ${plural(g.publishN, 'коммит', 'коммита', 'коммитов')} в origin/${g.label}` : 'нечего публиковать'}
        disabled={!g.publishN} onClick={run(g.onPublish)} />
      <MenuSep />
      <MenuItem icon={<Eye size={15} strokeWidth={ICON_STROKE} />} label="Показать изменения" onClick={run(g.onShowChanges)} />
    </>
  );
}

// Меню кнопки «Зафиксировать» у правого края губы — область коммита, как у старой Git-полосы
function CommitMenuBody({ g, close }: { g: RowGit; close: () => void }) {
  const run = (fn: () => void) => () => { close(); fn(); };
  return (
    <>
      <MenuItem icon={<MessageSquare size={15} strokeWidth={ICON_STROKE} />} label="Только этот чат" onClick={run(g.onCommitOwn)} />
      <MenuItem icon={<FolderGit2 size={15} strokeWidth={ICON_STROKE} />} label="Всё дерево" onClick={run(g.onCommitAll)} />
    </>
  );
}

// Старые пилюля «файлы +N −M» и кнопки «Зафиксировать ▾» / «Опубликовать N» Git-полосы — в губе
// справа, прежние отделка и размер (высота 28, кегль 12.5): ряд губы — как у нижней (32)
const GIT_BTN_H = 28;
// Кегль и иконка ветки, пилюли и кнопок Git в губе — как у прежней Git-полосы: все в одном размере
const GIT_FONT = 12.5;
// Поле и зазор иконки — как у кнопки режима в нижней губе (Composer, кнопка режима)
const LIP_BTN_PAD_X = 10;
const GIT_ICON_GAP = 6;
// Зазор между веткой, пилюлей и кнопками — как у прежней полосы
const GIT_ROW_GAP = SP.md;
function GitActions({ g, onCommit }: { g: RowGit; onCommit: (rect: DOMRect) => void }) {
  // Button xs (24) поднят до ряда губы (28) и кегля прежней Git-полосы
  const base: CSSProperties = {
    height: GIT_BTN_H, minHeight: GIT_BTN_H, fontSize: GIT_FONT, gap: GIT_ICON_GAP,
    flexShrink: 0, whiteSpace: 'nowrap',
  };
  const files = `${g.changes} ${plural(g.changes, 'файл', 'файла', 'файлов')}`;
  const added = g.added ?? 0, deleted = g.deleted ?? 0;
  return (
    <span data-git-actions="" style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: GIT_ROW_GAP, flexShrink: 0 }}>
      {g.changes > 0 && (
        <Button variant="ghostFilled" size="xs" onClick={g.onShowChanges}
          title={`Изменено ${files}, строк +${added} −${deleted} — открыть изменения`}
          style={{ ...base, padding: `0 ${SP.md - 1}px`, fontFamily: FONT.mono, fontWeight: 400 }}>
          <span style={{ color: C.textSecondary }}>{g.changes}</span>
          {added > 0 && <span style={{ color: C.diffAddText }}>+{added}</span>}
          {deleted > 0 && <span style={{ color: C.diffRemText }}>−{deleted}</span>}
          {added === 0 && deleted === 0 && <span style={{ color: C.textMuted }}>±0</span>}
        </Button>
      )}
      {g.changes > 0 && (
        <Button variant="ghostFilled" size="xs" title="Зафиксировать изменения (git commit)"
          onClick={e => onCommit((e.currentTarget as HTMLElement).getBoundingClientRect())}
          leftIcon={<Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.accent} />}
          style={{ ...base, padding: `0 ${SP.sm + 2}px 0 ${SP.md}px`, background: C.bgCard, color: C.textHeading, fontWeight: 400 }}>
          Зафиксировать
          <ChevronDown size={ICON_SIZE.xs - 2} strokeWidth={ICON_STROKE} color={C.textMuted} />
        </Button>
      )}
      {g.publishN > 0 && (
        <Button variant="primary" size="xs" onClick={g.onPublish} title="Опубликовать (git push)"
          leftIcon={<CloudUpload size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          style={{ ...base, padding: `0 ${SP.md}px` }}>
          Опубликовать <span style={{ opacity: 0.85 }}>{g.publishN}</span>
        </Button>
      )}
    </span>
  );
}

// Заголовок меню — у шторки на телефоне он в шапке окна, второй раз не повторяем
function ExecMenuBody({ e, close, head = true }: { e: RowExec; close: () => void; head?: boolean }) {
  return (
    <>
      {head && <MenuHead>{e.title}</MenuHead>}
      {groupExecutorRows(e.rows).map(g => (
        <div key={g.group}>
          {g.group !== 'auto' && <MenuHead>{EXECUTOR_GROUP_LABEL[g.group]}</MenuHead>}
          {g.rows.map(r => (
            <MenuItem key={r.id} disabled={r.disabled}
              icon={<span style={{ width: 12, height: 12, borderRadius: R.max, border: `2px solid ${r.id === e.value ? C.accent : C.border}`, background: r.id === e.value ? C.accent : 'transparent' }} />}
              label={<span style={{ display: 'flex', gap: SP.sm, alignItems: 'center' }}><span style={{ flex: 1, minWidth: 0 }}>{r.name}</span><Badge size="xs" tone={r.free ? 'success' : 'neutral'}>{rowPriceShort(r)}</Badge></span>}
              hint={r.reason ?? r.sub ?? r.price}
              onClick={() => { close(); e.onChange(r.id); }} />
          ))}
        </div>
      ))}
    </>
  );
}

function RefsMenuBody({ refs, grayIds, grayHint, iconOf, onDetach, onClear, close }: {
  refs: readonly ChatContextRef[]; grayIds: ReadonlySet<string>; grayHint: string; iconOf: (k: string) => ReactNode;
  onDetach: (id: string) => void; onClear: () => void; close: () => void;
}) {
  return (
    <>
      <MenuHead><b style={{ color: C.textHeading }}>Подключено к ходу</b> · {refs.length}</MenuHead>
      {refs.map(r => (
        <MenuItem key={r.id} icon={iconOf(r.kind)}
          label={<span>{r.label}{r.by === 'agent' && <> <AgentMark title="Подключил Claude" /></>}</span>}
          hint={grayIds.has(r.id) ? grayHint.toLowerCase() : roleLabel(r.role) ?? undefined}
          action={{ icon: <X size={14} strokeWidth={ICON_STROKE} />, title: `Отключить «${r.label}»`, onClick: () => onDetach(r.id) }} />
      ))}
      <MenuSep />
      <MenuItem danger icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />} label="Очистить контекст"
        hint="снимет объект и всё подключённое; ветку и «Чем» не трогает"
        onClick={() => { close(); onClear(); }} />
    </>
  );
}

function UndoNotice({ offer, onUndo }: { offer: RowOffer; onUndo: () => void }) {
  const secondsLeft = () => (offer.until ? Math.max(0, Math.ceil((offer.until - Date.now()) / 1000)) : 0);
  const [left, setLeft] = useState(secondsLeft);
  useEffect(() => {
    setLeft(secondsLeft());
    const t = setInterval(() => setLeft(secondsLeft()), 250);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [offer.until]);
  return (
    <div data-context-notice="" role="status" style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, height: 28, margin: `0 0 ${SP.xs}px`, padding: `0 ${SP.sm}px`,
      border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgPanel, fontSize: FS.sm, color: C.textSecondary,
    }}>
      <Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      <span style={{ ...ellipsis, flex: 1 }}>Выбор снят — поле снова «Чат»</span>
      {offer.until && <span style={{ color: C.textMuted, flexShrink: 0 }}>{left} с</span>}
      <button type="button" data-undo="" onClick={onUndo} style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, border: 'none', background: 'transparent', cursor: 'pointer',
        color: C.accent, fontSize: FS.sm, fontWeight: 600, fontFamily: 'inherit', padding: 0, flexShrink: 0,
      }}>
        <RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />Вернуть
      </button>
    </div>
  );
}

function AgentTip({ p, onOpen, onClose }: { p: ChatContextPrimary; onOpen?: () => void; onClose: () => void }) {
  return (
    <div data-agent-tip="" style={{
      position: 'absolute', left: 0, bottom: '100%', marginBottom: SP.xs, zIndex: Z.dropdown, width: 'min(340px, 100%)',
      boxSizing: 'border-box', padding: SP.md - 2, border: `1px solid ${C.border}`, borderRadius: R.xl,
      background: C.bgWhite, boxShadow: SHADOW.dropdown, fontSize: FS.sm, color: C.textSecondary,
    }}>
      <b style={{ color: C.textHeading }}>✦ Взял в работу Claude</b> · {p.label}{p.version ? ` · ${p.version}` : ''}
      <div style={{ margin: `${SP.xs}px 0 ${SP.sm}px`, color: C.textMuted }}>
        Панель от действий Claude не двигается. Поле осталось «Чат»: Claude продолжит разговор, а чип действия отправит запуск генератору.
      </div>
      <div style={{ display: 'flex', gap: SP.sm }}>
        {onOpen && <button type="button" data-tip-open="" onClick={() => { onClose(); onOpen(); }} style={tipBtn(true)}>Открыть</button>}
        <button type="button" data-tip-close="" onClick={onClose} style={tipBtn(false)}>Понятно</button>
      </div>
    </div>
  );
}
const tipBtn = (primary: boolean) => ({
  border: `1px solid ${primary ? C.accent : C.border}`, background: primary ? C.accent : 'transparent',
  color: primary ? C.onAccent : C.textSecondary, borderRadius: R.md, padding: `${SP.xs}px ${SP.md - 2}px`,
  fontSize: FS.sm, fontWeight: 600, fontFamily: 'inherit', cursor: 'pointer',
}) as const;

// ── Строка ──

type OpenMenu = { kind: 'git' | 'exec' | 'refs' | 'commit'; rect: DOMRect } | null;

// Счётчик загрузок шрифтов страницы: растёт, когда шрифты готовы и после каждой догрузки (`loadingdone`)
function useFontsEpoch(): number {
  const [epoch, setEpoch] = useState(0);
  useEffect(() => {
    const fonts = typeof document !== 'undefined' ? document.fonts : undefined;
    if (!fonts) return;
    let alive = true;
    const bump = () => { if (alive) setEpoch(n => n + 1); };
    void fonts.ready.then(bump);
    fonts.addEventListener?.('loadingdone', bump);
    return () => { alive = false; fonts.removeEventListener?.('loadingdone', bump); };
  }, []);
  return epoch;
}

export function ContextRowView(props: ContextRowViewProps) {
  const { git, primary, refs, exec, isMobile, iconOf, actionLabel } = props;
  const [menu, setMenu] = useState<OpenMenu>(null);
  const [tip, setTip] = useState(false);
  const facts = rowFacts(props);
  // Телефон: лестницы нет, губа — смысловые строки «Где» (ветка целиком), «С чем» (объект и «Чем»),
  // «Подключено» (референсы). Строка «Подключено» в одну линию: не влезшие референсы по одному
  // уходят в «+N ›» (drop) после вёрстки
  const [drop, setDrop] = useState(0);
  const refsLineRef = useRef<HTMLDivElement>(null);
  const base = props.pick ?? (isMobile
    ? { form: { g: 3 as const, o: 2 as const, e: 2 as const, k: Math.max(0, refs.length - drop) }, index: -1, count: 0, need: 0, scroll: false }
    : contextRowLadder(props.width ?? 10_000, facts));
  // Номиналы ступеней — верхняя оценка (чип не шире номинала), реальные чипы уже. Поэтому после вёрстки
  // пробуем ступень богаче: влезла без прокрутки — оставляем, нет — возвращаемся на шаг назад и закрепляем
  const rowRef = useRef<HTMLDivElement>(null);
  const [shift, setShift] = useState(0);
  const trial = useRef({ sig: '', locked: false });
  // Ширины чипов меряются по реальному шрифту: пока моноширинный не загрузился, запасной шире и «не влезло» ложное
  const fontsEpoch = useFontsEpoch();
  const sig = [fontsEpoch, props.width, base.index, git?.label, git?.changes, primary?.label, primary?.version, exec?.value, refs.map(r => r.label).join(',')].join('|');
  const rungs = !props.pick && !isMobile ? ladderRungs(facts) : [];
  const last = rungs.length - 1;
  const at = Math.min(last, Math.max(0, base.index - shift));
  useLayoutEffect(() => {
    const el = rowRef.current;
    if (!el || !rungs.length) return;
    if (trial.current.sig !== sig) {
      trial.current = { sig, locked: false };
      if (shift !== 0) { setShift(0); return; }
    }
    if (el.scrollWidth > el.clientWidth + 1) {
      // Не влезло: назад на шаг, и вверх больше не пробуем
      if (at < last) { trial.current.locked = true; setShift(shift - 1); }
    } else if (!trial.current.locked && at > 0) {
      setShift(shift + 1);
    }
  });
  // Строка «Подключено» на телефоне переполнилась — ещё один референс в «+N ›»
  const lineSig = useRef('');
  useLayoutEffect(() => {
    const el = refsLineRef.current;
    if (!isMobile || props.pick) return;
    if (lineSig.current !== sig) {
      lineSig.current = sig;
      if (drop !== 0) { setDrop(0); return; }
    }
    if (el && el.scrollWidth > el.clientWidth + 1 && drop < refs.length) setDrop(drop + 1);
  });
  const pick = shift !== 0 && rungs.length ? { ...base, form: rungs[at], index: at } : base;
  if (!showsRow(props)) {
    // Ни ветки, ни объекта (проект без git, личный чат): строки нет, но «Вернуть» после ✕ обязана остаться
    return props.offer
      ? <div data-context-row-host="" style={{ position: 'relative', margin: `${SP.xs}px 0 ${SP.sm - 2}px` }}><UndoNotice offer={props.offer} onUndo={props.onUndo} /></div>
      : null;
  }
  const f = pick.form;
  const close = () => setMenu(null);
  const grayHint = actionLabel ? `Не используется в операции «${actionLabel}»` : '';
  // В «Чате» серых нет: Claude видит всё подключённое
  const grayIds = new Set(actionLabel ? refs.filter(r => unusedBy(r, props.actionOp)).map(r => r.id) : []);
  const shown = refs.slice(0, f.k);
  const hidden = refs.length - f.k;

  const open = (kind: 'git' | 'exec' | 'refs') => (rect: DOMRect) => setMenu({ kind, rect });
  // В строке одна ветка (десктоп): ветка — прежней подписью Git-полосы без меню, и раскладка —
  // как у полосы до строки контекста (поля и зазоры ниже). Справа пилюля и кнопки — если есть
  // что делать; на чистом дереве — только ветка
  const gitBare = !isMobile && !!git && !primary && refs.length === 0;
  const gitActions = gitBare && (git.changes > 0 || git.publishN > 0);

  const body = !menu ? null
    : menu.kind === 'git' && git ? <GitMenuBody g={git} close={close} />
    : menu.kind === 'commit' && git ? <CommitMenuBody g={git} close={close} />
    : menu.kind === 'exec' && exec ? <ExecMenuBody e={exec} close={close} head={!isMobile} />
    : menu.kind === 'refs' ? <RefsMenuBody refs={refs} grayIds={grayIds} grayHint={grayHint} iconOf={iconOf}
        onDetach={props.onDetach} onClear={props.onClear} close={close} />
    : null;

  // Строка — верхняя губа композера: заезжает под поле (оно лежит слоем выше), горизонтальные поля —
  // номинал лестницы, чтобы её расчёт ширины не разошёлся. Десктоп и планшет — ушко (как свёрнутая
  // полоса): закладка по ширине содержимого у левого края, ряд по чипам (CHIP_H), лишнее сжимает
  // лестница. Телефон — губа во всю ширину из смысловых строк и с крупными чипами
  const inner = COMPOSER_LIP.overlap + COMPOSER_LIP.gap;
  const shell: CSSProperties = isMobile
    ? {
        ...composerLip('top', { row: CHIP_H_MOBILE }),
        width: '100%', height: 'auto', flexDirection: 'column', alignItems: 'stretch', gap: SP.xs,
        padding: `${COMPOSER_LIP.edge}px ${NOM.pad / 2}px ${inner}px`,
      }
    : {
        // Губа во всю ширину поля и той же высоты, что нижняя (ряд COMPOSER_LIP.row): композер
        // симметричен, а в ряд встают кнопки Git прежнего размера. Чипы — по-прежнему слева
        ...composerLip('top', { row: COMPOSER_LIP.row }),
        width: '100%', maxWidth: '100%',
        // Одна ветка — поля прежней Git-полосы (composerLip, padX); иначе — номинал лестницы
        padding: `${COMPOSER_LIP.edge}px ${gitBare ? COMPOSER_LIP.padX : NOM.pad / 2}px ${inner}px`,
        ...(gitBare ? { gap: GIT_ROW_GAP } : null),
        overflowX: 'auto', overflowY: 'hidden', scrollbarWidth: 'none',
      };

  const primaryNode = primary && (
    <PrimaryChip p={primary} form={f.o} icon={iconOf(primary.kind)} onOpen={props.onOpenPrimary}
      onRelease={props.onRelease} onAgentTip={() => setTip(t => !t)} />
  );
  const execNode = primary && exec && <ExecChip e={exec} form={f.e} onOpen={open('exec')} />;
  const refsNodes = (
    <>
      <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} aria-label="Подключено к ходу" />
      {shown.map(r => (
        <RefPill key={r.id} r={r} gray={grayIds.has(r.id)} icon={iconOf(r.kind)} grayHint={grayHint} onDetach={() => props.onDetach(r.id)} />
      ))}
      <RowChip kind="more" max={NOM.more} onClick={e => setMenu({ kind: 'refs', rect: e.currentTarget.getBoundingClientRect() })}
        title={hidden > 0 ? `Ещё ${hidden}: показать всё подключённое` : 'Показать всё подключённое'}>
        {hidden > 0 && <span style={{ fontWeight: 600 }}>+{hidden}</span>}
        <ChevronRight size={ICON_SIZE.xs - 2} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      </RowChip>
    </>
  );
  // Строка губы на телефоне: одна линия, ширина строки — потолок для чипов
  const mobileLine: CSSProperties = { display: 'flex', alignItems: 'center', gap: SP.sm - 2, minWidth: 0 };

  return (
    // Десктоп — отступ сверху, как у прежней Git-полосы (10): тень губы вверх (SHADOW.liftSoft из
    // composerLip) ложится на этот зазор, а не на низ ленты
    <div data-context-row-host="" style={{ position: 'relative', margin: `${isMobile ? SP.xs : SP.sm + 2}px 0 0` }}>
      {props.offer && <UndoNotice offer={props.offer} onUndo={props.onUndo} />}
      {tip && primary && <AgentTip p={primary} onOpen={props.onOpenPrimary} onClose={() => setTip(false)} />}
      <FlatChips.Provider value>
      <ChipHeight.Provider value={isMobile ? CHIP_H_MOBILE : CHIP_H}>
      <div
        ref={rowRef}
        data-context-row="" data-ladder-step={pick.index} data-ladder-scroll={pick.scroll ? '1' : '0'}
        role="toolbar" aria-label="Контекст хода"
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm - 2, boxSizing: 'border-box',
          ...shell,
        }}
      >
        {isMobile ? (
          <>
            {git && (
              <div data-row-line="where" style={mobileLine}>
                <ShrinkChips.Provider value><GitChip g={git} form={f.g} onOpen={open('git')} /></ShrinkChips.Provider>
              </div>
            )}
            {primary && (
              <div data-row-line="what" style={mobileLine}>
                {/* Объект главнее «Чем»: держит до 60% строки и не ужимается, пока ужимается «Чем» */}
                <ShrinkChips.Provider value>
                  <span style={{ display: 'flex', minWidth: 0, flexShrink: 0, maxWidth: exec ? '60%' : '100%' }}>{primaryNode}</span>
                  {exec && <span style={{ display: 'flex', minWidth: 0, flex: '0 1 auto', marginLeft: 'auto' }}>{execNode}</span>}
                </ShrinkChips.Provider>
              </div>
            )}
            {refs.length > 0 && <div ref={refsLineRef} data-row-line="refs" style={{ ...mobileLine, overflow: 'hidden' }}>{refsNodes}</div>}
          </>
        ) : (
          <>
            {git && <GitChip g={git} form={f.g} onOpen={open('git')} bare={gitBare} />}
            {/* Губа во всю ширину, а в строке одна ветка — место есть: справа старые пилюля и
                кнопки Git-полосы (те же обработчики, что в меню ветки), а чип ветки — только имя.
                Только без объекта и референсов: чипов, которые ужимает лестница, нет, и кнопки
                не отнимают у неё ширину. Высота — по ряду губы (CHIP_H): губа не растёт */}
            {gitActions && git && <GitActions g={git} onCommit={rect => setMenu({ kind: 'commit', rect })} />}
            {/* Чистое дерево — справа просто «нет изменений»: текстом без бейджа и без зелени, как прежняя
                строка «ветка · чисто» Git-полосы. Делать нечего, поэтому не кнопка */}
            {gitBare && !gitActions && (
              <span data-git-clean="" title="Всё закоммичено и опубликовано"
                style={{ marginLeft: 'auto', flexShrink: 0, padding: `0 ${LIP_BTN_PAD_X}px`, fontSize: GIT_FONT, color: C.textMuted, whiteSpace: 'nowrap' }}>нет изменений</span>
            )}
            {git && (primary || refs.length > 0) && (
              <span data-row-sep="" style={{ width: NOM.vsep, height: 16, background: C.border, flexShrink: 0 }} />
            )}
            {primaryNode}
            {exec && execNode}
            {refs.length > 0 && refsNodes}
          </>
        )}
      </div>
      </ChipHeight.Provider>
      </FlatChips.Provider>
      {menu && body && (isMobile
        ? <Modal title={menu.kind === 'git' ? 'Ветка' : menu.kind === 'exec' ? exec?.title ?? 'Чем выполнить' : 'Подключено к ходу'} onClose={close}>{body}</Modal>
        : <Menu anchor={menu.rect} onClose={close} minWidth={300} maxWidth={340} maxHeight={320} preferUp anchorAlign="start">{body}</Menu>)}
    </div>
  );
}
