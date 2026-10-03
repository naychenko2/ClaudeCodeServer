// Строка контекста хода над полем ввода (ADR-023, макет docs/mockups/composer-context-row-v1):
// «Где» (ветка), «С чем» (основной объект), «Чем» (исполнитель), «Плюс» (референсы и «+N ›»).
// Здесь только отрисовка по готовой модели: подписи объекта и референсов приходят из DTO
// (`label`, `version`), своих форматтеров нет. Ширина — снаружи; форму каждого чипа выбирает
// чистая лестница lib/chatContext/ladder.ts, открытость панели в неё не входит.
import { useEffect, useState, type KeyboardEvent, type ReactNode } from 'react';
import { ChevronDown, ChevronRight, Cpu, Eye, GitBranch, Plus, RotateCcw, Send, Trash2, X, Check, Info } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW, Z } from '../../lib/design';
import { plural } from '../../lib/plural';
import { contextRowLadder, NOM, type LadderFacts, type LadderPick } from '../../lib/chatContext/ladder';
import { roleLabel } from '../../lib/chatContext/roleLabels';
import type { ChatContextPrimary, ChatContextRef } from '../../lib/chatContext/types';
import { Badge, Menu, MenuItem, MenuSep, Modal } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { groupExecutorRows, EXECUTOR_GROUP_LABEL, type ExecutorRow } from '../generation/ExecutorList';

export const ROW_H = 30;
export const ROW_H_MOBILE = 32;
const CHIP_H = 22;

// ── Модель строки ──

export interface RowGit {
  label: string;
  changes: number;
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

export interface RowOffer { text: string }

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
  return (
    <span
      data-chip={kind}
      title={title}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      onClick={onClick ? e => onClick(e) : undefined}
      onKeyDown={onClick ? e => act(() => onClick({ currentTarget: e.currentTarget as HTMLElement }))(e) : undefined}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs + 1, flexShrink: 0, boxSizing: 'border-box',
        height: CHIP_H, maxWidth: max, padding: `0 ${SP.sm - 1}px`, borderRadius: R.md, minWidth: 0,
        border: `1px ${dashed ? 'dashed' : 'solid'} ${borderColor ?? C.border}`, background: C.bgCard,
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
  return `Ветка ${g.label} · ${g.changes ? changesWord(g.changes) : 'чисто'}${g.publishN ? ` · ${g.publishN} к публикации` : ''}`;
}

function GitChip({ g, form, onOpen }: { g: RowGit; form: 0 | 1 | 2 | 3; onOpen: (r: DOMRect) => void }) {
  const icon = <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;
  const clean = !g.changes && !g.publishN;
  const up = g.publishN > 0 ? <span style={{ color: C.accent, fontWeight: 600, flexShrink: 0 }}>↑{g.publishN}</span> : null;
  const count = g.changes ? <span style={{ fontWeight: 600, flexShrink: 0 }}>{g.changes}</span> : null;
  const max = form === 0 ? NOM.g0 : NOM[`g${form}` as 'g1'];
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
      {form >= 2 && <span style={{ ...ellipsis, maxWidth: form === 3 ? 136 : 76 }}>{g.label}</span>}
      <span style={{ color: C.textMuted, flexShrink: 0 }}>·</span>
      {form === 3
        ? (g.changes ? <span style={{ flexShrink: 0 }}>{changesWord(g.changes)}</span> : (g.publishN ? null : <span style={{ flexShrink: 0 }}>чисто</span>))
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
  return (
    <RowChip kind="primary" max={form === 1 ? NOM.o1 : NOM.o2} title={title} onClick={onOpen} borderColor={C.accentMuted} dim={p.missing}>
      <Thumb item={p} icon={icon} />
      <span data-chip-label="" style={ellipsis}>{p.label}</span>
      {p.version && <span style={{ color: C.textMuted, flexShrink: 0 }}>· {p.version}</span>}
      {agent && <AgentMark title="Взял в работу Claude" onClick={onAgentTip} />}
      <XBtn title={`Снять «${p.label}» с работы`} onClick={onRelease} />
    </RowChip>
  );
}

function ExecChip({ e, form, onOpen }: { e: RowExec; form: 1 | 2 | 3; onOpen: (r: DOMRect) => void }) {
  const row = e.rows.find(r => r.id === e.value) ?? e.rows[0];
  if (!row) return null;
  const auto = row.group === 'auto';
  const price = row.price.split(' · ')[0];
  const free = row.badges?.some(b => b.tone === 'success') ?? /^бесплатно/.test(row.price);
  const full = `${auto ? 'Авто · ' : ''}${row.sub ? `${row.sub} · ` : ''}${row.name}`;
  const title = `Чем: ${full} · ${price} — сменить исполнителя`;
  const open = (ev: { currentTarget: HTMLElement }) => onOpen(ev.currentTarget.getBoundingClientRect());
  const badge = <Badge size="xs" tone={free ? 'success' : 'neutral'}>{price}</Badge>;
  return (
    <RowChip kind="exec" max={NOM[`e${form}` as 'e1']} title={title} onClick={open}>
      {form === 1
        ? <Cpu size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        : form === 2
          ? <b style={{ ...ellipsis, color: C.textHeading, fontWeight: 600 }}>{row.name}</b>
          : <>
              <span style={{ color: C.textMuted, flexShrink: 0 }}>Чем:</span>
              <span style={ellipsis}>{auto && <b style={{ color: C.textHeading, fontWeight: 600 }}>Авто · </b>}{row.sub ? `${row.sub} · ` : ''}<b style={{ color: C.textHeading, fontWeight: 600 }}>{row.name}</b></span>
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
    <RowChip kind="ref" max={NOM.ref} title={title} dim={gray || r.missing} dashed={gray}>
      <Thumb item={r} icon={icon} round={r.role === 'char' || r.role === 'character'} />
      <span style={{ ...ellipsis, textDecoration: gray ? 'line-through' : undefined }}>{r.label}</span>
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
      <MenuHead><b style={{ color: C.textHeading, fontFamily: FONT.mono }}>{g.label}</b> · {g.changes ? changesWord(g.changes) : 'чисто'}{g.publishN ? ` · ↑${g.publishN}` : ''}</MenuHead>
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

function ExecMenuBody({ e, close }: { e: RowExec; close: () => void }) {
  return (
    <>
      <MenuHead>{e.title}</MenuHead>
      {groupExecutorRows(e.rows).map(g => (
        <div key={g.group}>
          {g.group !== 'auto' && <MenuHead>{EXECUTOR_GROUP_LABEL[g.group]}</MenuHead>}
          {g.rows.map(r => (
            <MenuItem key={r.id} disabled={r.disabled}
              icon={<span style={{ width: 12, height: 12, borderRadius: R.max, border: `2px solid ${r.id === e.value ? C.accent : C.border}`, background: r.id === e.value ? C.accent : 'transparent' }} />}
              label={<span style={{ display: 'flex', gap: SP.sm, alignItems: 'center' }}><span style={{ flex: 1, minWidth: 0 }}>{r.name}</span><Badge size="xs" tone={/^бесплатно/.test(r.price) ? 'success' : 'neutral'}>{r.price.split(' · ')[0]}</Badge></span>}
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
  const [left, setLeft] = useState(4);
  useEffect(() => {
    const t = setInterval(() => setLeft(v => Math.max(0, v - 1)), 1000);
    return () => clearInterval(t);
  }, []);
  return (
    <div data-context-notice="" role="status" style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, height: 28, margin: `0 0 ${SP.xs}px`, padding: `0 ${SP.sm}px`,
      border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgPanel, fontSize: FS.sm, color: C.textSecondary,
    }}>
      <Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      <span style={{ ...ellipsis, flex: 1 }}>{offer.text ? `Выбор снят — поле снова «Чат»` : 'Выбор снят'}</span>
      <span style={{ color: C.textMuted, flexShrink: 0 }}>{left} с</span>
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
        Панель от действий Claude не двигается. Поле осталось «Чат»: Claude продолжит разговор, а «Картинка» отправит промпт генератору.
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

type OpenMenu = { kind: 'git' | 'exec' | 'refs'; rect: DOMRect } | null;

export function ContextRowView(props: ContextRowViewProps) {
  const { git, primary, refs, exec, isMobile, iconOf, actionLabel } = props;
  const [menu, setMenu] = useState<OpenMenu>(null);
  const [tip, setTip] = useState(false);
  if (!showsRow(props)) return null;

  const facts = rowFacts(props);
  // Телефон: лестницы нет, строка прокручивается; ветка иконкой, «Чем» без подписи
  const pick = props.pick ?? (isMobile
    ? { form: { g: 0 as const, o: 2 as const, e: 2 as const, k: refs.length }, index: -1, count: 0, need: 0, scroll: true }
    : contextRowLadder(props.width ?? 10_000, facts));
  const f = pick.form;
  const close = () => setMenu(null);
  const grayHint = actionLabel ? `Не используется в операции «${actionLabel}»` : '';
  // В «Чате» серых нет: Claude видит всё подключённое
  const grayIds = new Set(actionLabel ? refs.filter(r => r.usedBy.length === 0).map(r => r.id) : []);
  const shown = refs.slice(0, f.k);
  const hidden = refs.length - f.k;

  const open = (kind: 'git' | 'exec' | 'refs') => (rect: DOMRect) => setMenu({ kind, rect });

  const body = !menu ? null
    : menu.kind === 'git' && git ? <GitMenuBody g={git} close={close} />
    : menu.kind === 'exec' && exec ? <ExecMenuBody e={exec} close={close} />
    : menu.kind === 'refs' ? <RefsMenuBody refs={refs} grayIds={grayIds} grayHint={grayHint} iconOf={iconOf}
        onDetach={props.onDetach} onClear={props.onClear} close={close} />
    : null;

  return (
    <div data-context-row-host="" style={{ position: 'relative', margin: `${SP.xs}px 0 ${SP.sm - 2}px` }}>
      {props.offer && <UndoNotice offer={props.offer} onUndo={props.onUndo} />}
      {tip && primary && <AgentTip p={primary} onOpen={props.onOpenPrimary} onClose={() => setTip(false)} />}
      <div
        data-context-row="" data-ladder-step={pick.index} data-ladder-scroll={pick.scroll ? '1' : '0'}
        role="toolbar" aria-label="Контекст хода"
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm - 2, boxSizing: 'border-box', width: '100%',
          height: isMobile ? ROW_H_MOBILE : ROW_H, padding: `0 ${NOM.pad / 2}px`,
          background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.lg,
          overflowX: 'auto', overflowY: 'hidden', scrollbarWidth: 'none',
        }}
      >
        {git && <GitChip g={git} form={isMobile ? 0 : f.g} onOpen={open('git')} />}
        {git && (primary || refs.length > 0) && (
          <span data-row-sep="" style={{ width: NOM.vsep, height: 16, background: C.border, flexShrink: 0 }} />
        )}
        {primary && (
          <PrimaryChip p={primary} form={f.o} icon={iconOf(primary.kind)} onOpen={props.onOpenPrimary}
            onRelease={props.onRelease} onAgentTip={() => setTip(t => !t)} />
        )}
        {primary && exec && <ExecChip e={exec} form={f.e} onOpen={open('exec')} />}
        {refs.length > 0 && (
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
        )}
      </div>
      {menu && body && (isMobile
        ? <Modal title={menu.kind === 'git' ? 'Ветка' : menu.kind === 'exec' ? 'Чем выполнить' : 'Подключено к ходу'} onClose={close}>{body}</Modal>
        : <Menu anchor={menu.rect} onClose={close} minWidth={300} maxWidth={340} maxHeight={320} preferUp anchorAlign="start">{body}</Menu>)}
    </div>
  );
}
