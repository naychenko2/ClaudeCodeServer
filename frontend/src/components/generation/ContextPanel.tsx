// Панель «Контекст» чата (ADR-023 §Д1, макет docs/mockups/composer-actions-v1 §2): одна панель
// оболочки вместо «Картинок», «Звука» и «Видео». Каркас — GenerationPanel; здесь только
// отрисовка по готовой модели: секции «Где», «С чем», «Чем», «Плюс», «Параметры запуска» и низ.
// Подписи объекта и референсов приходят из DTO (`label`, `version`), своих форматтеров нет;
// превью, редактор и строки «Чем» отдаёт вид через слот context-kind (ContextPanelHost).
// Параметры — закрытый набор LaunchParam: неизвестный `kind` не рисуется.
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ChevronDown, ChevronLeft, ChevronRight, GitBranch, Pencil, Plus, SlidersHorizontal, Trash2, X } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { roleLabel } from '../../lib/chatContext/roleLabels';
import type {
  ActionRun, ChatContextPrimary, ChatContextRef, ContextAction, ContextReturn, ExecutorListModel, LaunchParam,
} from '../../lib/chatContext/types';
import { Button, IconButton, InlineSegmented, Menu, MenuItem, ProgressBar, Stepper } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { changesWord, GitMenuBody, MenuHead, type RowGit } from '../chat/ContextRowView';
import { ExecutorList, ExecutorSummaryRow, rowPriceShort } from './ExecutorList';
import { GenerationPanel } from './GenerationPanel';
import { RunLabel } from './RunLabel';

// Тексты пустых состояний — из макета, раздел 4
export const EMPTY = {
  primary: 'Ничего не выбрано — «Работать с этой» на карточке ленты или в «Файлах»',
  execChat: 'Исполнитель появится, когда в поле выбрано действие. В «Чате» сообщение уходит Claude — исполнитель не нужен',
  execPending: 'Исполнители появятся после расчёта цены выбранного действия',
  execNoObject: 'Исполнитель появится, когда выберете, с чем работать',
  refs: 'Референсы: персонаж, образец стиля, голос, кадр, файл проекта. Их видят и Claude, и генератор',
  paramsChat: 'Параметры появятся, когда в поле ввода выбрано действие. В «Чате» запуска нет — сообщение уходит Claude',
  paramsNone: (label: string) => `У «${label}» параметров нет: один результат, как есть`,
  footChat: 'В «Чате» запуска нет — сообщение уходит Claude',
} as const;

export interface AddFromItem { id: string; label: string; hint: string; disabledReason?: string; run: () => void }

export interface ContextPanelProps {
  isMobile: boolean;
  // Видимость и работа в проекте: без проекта секции «Где» нет
  git: RowGit | null;
  primary: ChatContextPrimary | null;
  refs: readonly ChatContextRef[];
  iconOf: (kind: string) => ReactNode;
  // От вида: миниатюра, волна, кадр; без вида — иконка
  preview: ReactNode;
  editor: { label: string; hint: string; open: () => void } | null;
  step: { prev: (() => void) | null; next: (() => void) | null } | null;
  ret: ContextReturn | null;
  onReturn: () => void;
  // Выбранное run-действие; null — «Чат»
  action: ContextAction | null;
  exec: ExecutorListModel | null;
  params: readonly LaunchParam[];
  onParam: (p: LaunchParam, value: number | string) => void;
  addFrom: readonly AddFromItem[];
  run: ActionRun;
  // Счётчик повторных показов открытой панели: каждый шаг мигает карточкой «С чем»
  flash: number;
  onRelease: () => void;
  onDetach: (itemId: string) => void;
  onClear: () => void;
  onClose?: () => void;
  // Витрина: шторка внутри своего блока вместо портала в body
  contained?: boolean;
  layout?: 'auto' | 'column' | 'sheet';
  // Витрина: шторка опущена до цены
  peeked?: boolean;
}

const Section = ({ name, title, aside, children }: { name: string; title: ReactNode; aside?: ReactNode; children: ReactNode }) => (
  <section data-ctx-section={name} style={{ padding: `${SP.sm}px 0`, borderBottom: `1px solid ${C.borderLight}` }}>
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.xs + 2 }}>
      <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>{title}</span>
      <span style={{ flex: 1 }} />
      {aside}
    </div>
    {children}
  </section>
);

const Empty = ({ children }: { children: ReactNode }) => (
  <div data-ctx-empty="" style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{children}</div>
);

function Thumb({ item, icon, size, round }: { item: { thumb: string | null }; icon: ReactNode; size: number; round?: boolean }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: size, height: size, flexShrink: 0,
      borderRadius: round ? R.max : R.sm, overflow: 'hidden', background: C.bgInset, color: C.accent,
    }}>
      {item.thumb ? <img src={item.thumb} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : icon}
    </span>
  );
}

// ── Где ──

function WhereSection({ git }: { git: RowGit }) {
  const [rect, setRect] = useState<DOMRect | null>(null);
  return (
    <Section name="where" title="Где">
      <button type="button" data-ctx-git="" onClick={e => setRect(e.currentTarget.getBoundingClientRect())}
        title="Ветка и дерево: зафиксировать, опубликовать, показать изменения"
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm - 2, width: '100%', boxSizing: 'border-box', minHeight: 32,
          padding: `0 ${SP.sm}px`, border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgCard,
          fontSize: FS.sm, color: C.textSecondary, cursor: 'pointer', fontFamily: 'inherit', textAlign: 'left',
        }}>
        <GitBranch size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        <b style={{ color: C.textHeading, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{git.label}</b>
        <span style={{ color: C.textMuted, flexShrink: 0 }}>· {git.changes ? changesWord(git.changes) : 'чисто'}{git.publishN ? ` · ↑${git.publishN}` : ''}</span>
        <span style={{ flex: 1 }} />
        <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
      </button>
      {rect && (
        <Menu anchor={rect} onClose={() => setRect(null)} minWidth={300} maxWidth={340} maxHeight={320} anchorAlign="start">
          <GitMenuBody g={git} close={() => setRect(null)} />
        </Menu>
      )}
    </Section>
  );
}

// ── С чем ──

function WithSection(p: ContextPanelProps) {
  const { primary } = p;
  const [blink, setBlink] = useState(false);
  const first = useRef(true);
  // Повторный показ открытой панели мигает карточкой; первый показ (монтирование) не мигает
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    setBlink(true);
    const t = setTimeout(() => setBlink(false), 900);
    return () => clearTimeout(t);
  }, [p.flash]);

  return (
    <Section name="with" title="С чем">
      {!primary ? <Empty>{EMPTY.primary}</Empty> : (
        <div data-ctx-card="" data-ctx-flash={blink ? '1' : '0'} style={{
          border: `1px solid ${blink ? C.accent : C.accentMuted}`, borderRadius: R.lg, background: C.bgCard, overflow: 'hidden',
          boxShadow: blink ? `0 0 0 3px ${C.accentLight}` : 'none', transition: 'box-shadow .25s, border-color .25s',
          opacity: primary.missing ? 0.6 : 1,
        }}>
          <div style={{ display: 'flex', justifyContent: 'center', background: C.bgInset, minHeight: 96, maxHeight: 180, overflow: 'hidden' }}>
            {p.preview ?? <Thumb item={primary} icon={p.iconOf(primary.kind)} size={96} />}
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: `${SP.sm - 2}px ${SP.sm}px` }}>
            {p.step && (
              <IconButton size="xs" title="Предыдущая версия" ariaLabel="Предыдущая версия" disabled={!p.step.prev} onClick={p.step.prev ?? undefined}>
                <ChevronLeft size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
            <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
              <span data-ctx-label="" style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {primary.label}{primary.by === 'agent' && <span title="Взял в работу Claude" style={{ color: C.accent, marginLeft: SP.xs }}>✦</span>}
              </span>
              {primary.version && <span data-ctx-version="" style={{ fontSize: FS.xs, color: C.textMuted }}>{primary.version}</span>}
            </span>
            {p.step && (
              <IconButton size="xs" title="Следующая версия" ariaLabel="Следующая версия" disabled={!p.step.next} onClick={p.step.next ?? undefined}>
                <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
            <IconButton size="xs" title={`Снять «${primary.label}» с работы`} ariaLabel={`Снять «${primary.label}» с работы`} onClick={p.onRelease}>
              <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
          {p.editor && (
            <div style={{ padding: `0 ${SP.sm}px ${SP.sm}px` }}>
              <Button size="xs" variant="secondary" leftIcon={<Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={p.editor.open}
                title={p.editor.hint} style={{ width: '100%' }}>
                {p.editor.label}
              </Button>
              <div style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textMuted }}>{p.editor.hint}</div>
            </div>
          )}
        </div>
      )}
      {p.ret && (
        <button type="button" data-ctx-return="" onClick={p.onReturn} style={{
          marginTop: SP.xs + 2, padding: 0, border: 'none', background: 'transparent', color: C.accent, cursor: 'pointer',
          fontSize: FS.sm, fontFamily: 'inherit', textAlign: 'left',
        }}>
          ← {p.ret.label}
        </button>
      )}
    </Section>
  );
}

// ── Чем ──

function BySection({ p }: { p: ContextPanelProps }) {
  const [open, setOpen] = useState(false);
  const { exec, action, primary } = p;
  const row = exec?.rows.find(r => r.id === exec.value) ?? exec?.rows[0];
  return (
    <Section name="by" title={action ? `Чем · для «${action.label}»` : 'Чем'}>
      {!exec || !action || !row ? (
        <Empty>{!primary ? EMPTY.execNoObject : !action ? EMPTY.execChat : EMPTY.execPending}</Empty>
      ) : (
        <>
          <ExecutorSummaryRow name={row.name} parts={row.sub ? [row.sub] : []} price={{ label: rowPriceShort(row), tone: row.free ? 'success' : 'neutral' }}
            open={open} onToggle={() => setOpen(o => !o)} isMobile={p.isMobile} />
          {open && (
            <div style={{ marginTop: SP.xs }}>
              <ExecutorList rows={exec.rows} value={exec.value} isMobile={p.isMobile} onChange={id => { exec.onChange(id); setOpen(false); }} />
            </div>
          )}
        </>
      )}
    </Section>
  );
}

// ── Плюс ──

function PlusSection({ p }: { p: ContextPanelProps }) {
  const [rect, setRect] = useState<DOMRect | null>(null);
  const actionLabel = p.action?.label ?? null;
  // В «Чате» серых нет: Claude видит всё подключённое
  const gray = (r: ChatContextRef) => !!actionLabel && r.usedBy.length === 0;
  return (
    <Section name="plus" title={`Плюс${p.refs.length ? ` · ${p.refs.length}` : ''}`}
      aside={p.refs.length > 0 ? (
        <button type="button" data-ctx-clear="" onClick={p.onClear} style={{
          display: 'inline-flex', alignItems: 'center', gap: SP.xs, border: 'none', background: 'transparent', padding: 0,
          color: C.textMuted, fontSize: FS.xs, cursor: 'pointer', fontFamily: 'inherit',
        }}><Trash2 size={ICON_SIZE.xs - 1} strokeWidth={ICON_STROKE} />Очистить контекст</button>
      ) : undefined}>
      {p.refs.length === 0 ? <Empty>{EMPTY.refs}</Empty> : (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs + 2 }}>
          {p.refs.map(r => {
            const role = roleLabel(r.role);
            const g = gray(r);
            return (
              <span key={r.id} data-ctx-ref={g ? 'gray' : 'on'}
                title={g ? `Серый — не используется в «${actionLabel}»: останется в контексте для Claude и других действий` : r.label}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: SP.xs + 1, maxWidth: '100%', boxSizing: 'border-box',
                  height: 26, padding: `0 ${SP.xs + 2}px 0 ${SP.xs + 1}px`, borderRadius: R.md, background: C.bgCard,
                  border: `1px ${g ? 'dashed' : 'solid'} ${C.border}`, opacity: g || r.missing ? 0.55 : 1, fontSize: FS.sm, color: C.textSecondary,
                }}>
                <Thumb item={r} icon={p.iconOf(r.kind)} size={18} round={r.role === 'char' || r.role === 'character'} />
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, textDecoration: g ? 'line-through' : undefined }}>{r.label}</span>
                {role && <span style={{ color: C.textMuted, flexShrink: 0 }}>· {role}</span>}
                <IconButton size="xs" title={`Отключить «${r.label}»`} ariaLabel={`Отключить «${r.label}»`} onClick={() => p.onDetach(r.id)}>
                  <X size={ICON_SIZE.xs - 3} strokeWidth={ICON_STROKE} />
                </IconButton>
              </span>
            );
          })}
        </div>
      )}
      {p.addFrom.length > 0 && (
        <div data-ctx-add="" style={{ marginTop: SP.sm }}>
          <Button size="xs" variant="ghost" leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={e => setRect((e.currentTarget as HTMLElement).getBoundingClientRect())}>
            Добавить из… ▾
          </Button>
        </div>
      )}
      {rect && (
        <Menu anchor={rect} onClose={() => setRect(null)} minWidth={280} maxWidth={340} anchorAlign="start">
          <MenuHead>Добавить к контексту</MenuHead>
          {p.addFrom.map(it => (
            <MenuItem key={it.id} label={it.label} hint={it.disabledReason ?? it.hint} disabled={!!it.disabledReason}
              onClick={() => { setRect(null); it.run(); }} />
          ))}
        </Menu>
      )}
    </Section>
  );
}

// ── Параметры запуска ──

// Закрытый набор: новый kind добавляется правкой типа LaunchParam и ветки здесь; неизвестный не рисуется
function ParamControl({ param, onChange, isMobile }: { param: LaunchParam; onChange: (v: number | string) => void; isMobile: boolean }) {
  const row = (label: string, control: ReactNode) => (
    <div data-ctx-param={param.kind} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: 30 }}>
      <span style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: 92 }}>{label}</span>
      {control}
    </div>
  );
  switch (param.kind) {
    case 'variants':
      return row('Вариантов', <Stepper ariaLabel="Сколько вариантов" value={param.value} min={param.min} max={param.max} onChange={onChange} />);
    case 'duration':
      return row('Длительность', (
        <InlineSegmented isMobile={isMobile} value={String(param.value)} onChange={v => onChange(Number(v))}
          options={param.options.map(o => ({ value: String(o), label: `${o} с` }))} />
      ));
    case 'aspect':
      return row('Пропорции', (
        <InlineSegmented isMobile={isMobile} value={param.value} onChange={onChange}
          options={param.options.map(o => ({ value: o, label: o }))} />
      ));
    case 'fromQuestion':
      return <div data-ctx-param="fromQuestion" style={{ fontSize: FS.sm, color: C.textSecondary }}>{param.label}</div>;
    default:
      return null;
  }
}

export const isKnownParam = (p: { kind: string }): p is LaunchParam =>
  p.kind === 'variants' || p.kind === 'duration' || p.kind === 'aspect' || p.kind === 'fromQuestion';

function ParamsSection({ p }: { p: ContextPanelProps }) {
  const known = p.params.filter(isKnownParam);
  return (
    <Section name="params" title="Параметры запуска">
      {!p.action ? <Empty>{EMPTY.paramsChat}</Empty>
        : known.length === 0 ? <Empty>{EMPTY.paramsNone(p.action.label)}</Empty>
        : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            {known.map(param => <ParamControl key={param.kind} param={param} isMobile={p.isMobile} onChange={v => p.onParam(param, v)} />)}
          </div>
        )}
    </Section>
  );
}

// ── Низ ──

// Цена, кнопка и прогресс — по ActionRun (useActionRun: общая точка с полем ввода, подписи здесь нет)
function Foot({ run, action }: { run: ActionRun; action: ContextAction | null }) {
  const busy = run.state === 'running';
  const reason = run.blocked;
  return (
    <div data-ctx-section="foot" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs + 2 }}>
      {!action ? <Empty>{EMPTY.footChat}</Empty> : (
        <>
          {run.quote && (
            <div style={{ fontSize: FS.sm, lineHeight: 1.35, color: C.textSecondary }}>
              <b data-ctx-price="" style={{ display: 'block', color: C.textHeading }}>{run.quote.price}</b>
              {run.quote.detail && <span>{run.quote.detail}</span>}
            </div>
          )}
          {busy && run.progress !== null && <ProgressBar value={run.progress} />}
          {run.state === 'done' && run.result && (
            <div data-ctx-result="" style={{ fontSize: FS.sm, color: C.successText }}>✓ {run.result.summary}</div>
          )}
          <div data-ctx-run="">
            <Button size="xs" fullWidth disabled={!!reason || busy} title={reason ?? undefined}
              onClick={() => { run.run(run.text).catch(() => { /* причину уже показал запуск */ }); }} style={{ whiteSpace: 'nowrap' }}>
              <RunLabel parts={run.labelParts} />
            </Button>
          </div>
        </>
      )}
    </div>
  );
}

export function ContextPanel(p: ContextPanelProps) {
  return (
    <GenerationPanel
      title="Контекст"
      icon={<SlidersHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      panelKey="chatContext"
      layout={p.layout}
      peeked={p.peeked}
      contained={p.contained}
      onClose={p.onClose}
      footContent={<Foot run={p.run} action={p.action} />}
      peekSummary={p.primary ? `${p.primary.label}${p.primary.version ? ` · ${p.primary.version}` : ''}` : undefined}
    >
      <div data-context-panel="" style={{ display: 'flex', flexDirection: 'column' }}>
        {p.git && <WhereSection git={p.git} />}
        <WithSection {...p} />
        <BySection p={p} />
        <PlusSection p={p} />
        <ParamsSection p={p} />
      </div>
    </GenerationPanel>
  );
}
