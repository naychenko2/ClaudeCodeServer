import { useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { AlertTriangle, ChevronDown, ChevronRight, ChevronUp, Cpu, Sparkles, X } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { Badge, Button, IconButton, ResizeHandle, Stepper, Tabs } from '../ui';
import type { TabItem } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Общий каркас боковой панели генерации — «Картинки», «Звук», позже «Видео» (ADR-021 §3).
// Каркас рисует шапку, вкладки, строку контекста, прокручиваемое тело, закреплённый низ,
// корешок и шторку; вертикаль отдаёт только содержимое вкладок и данные низа.
// Образец разметки — genPanel() в docs/mockups/image-editor-v4-panel.html.

export const GEN_PANEL_W = { default: 380, min: 340, max: 520, spine: 44 } as const;
const HEAD_H = 42;
// Шторка телефона занимает 88 % высоты: над ней остаётся видна полоса ленты
const SHEET_H = '88%';

// Контракт закреплённого низа — ровно из макета (ADR-021 §3)
export interface GenerationFoot {
  reason?: string;                  // почему запуск невозможен; задана — кнопка гаснет
  queue?: string;                   // очередь GPU у локальных моделей
  count: number;
  maxCount: number;
  onCountChange?: (n: number) => void;
  maxCountHint?: string;            // причина потолка: «Эта операция даёт один вариант»
  price: [string, string];          // итог («≈ $0.08») и расшифровка («2 × $0.04 за картинку»)
  runLabel: string;                 // глагол запуска: «Изменить», «Перегенерировать»
  onRun: () => void;
}

// column — колонка справа, spine — свёрнута в корешок; sheet / peek — шторка телефона
// (поднята / опущена до цены). auto выбирает колонку или шторку по ширине окна.
export type GenerationPanelView = 'column' | 'spine' | 'sheet' | 'peek';

interface Props<T extends string> {
  title: string;
  subtitle?: string;
  icon: ReactNode;
  tabs: TabItem<T>[];
  tab: T;
  onTabChange: (t: T) => void;
  context?: ReactNode;              // строка «Работаем с: …» над телом
  children: ReactNode;              // тело активной вкладки
  foot?: GenerationFoot;
  footContent?: ReactNode;          // свой низ вкладки (на «Персонажах» — «Подключён: Аня · ＋»)
  peekSummary?: ReactNode;          // сводка одной строкой в опущенной шторке
  onClose?: () => void;
  // Вид: свёрнутость в корешок и опущенность шторки ведёт каркас сам, если не передали
  collapsed?: boolean;
  onCollapsedChange?: (v: boolean) => void;
  peeked?: boolean;
  onPeekedChange?: (v: boolean) => void;
  layout?: 'auto' | 'column' | 'sheet';
  width?: number;
  onWidthChange?: (w: number) => void;
  minWidth?: number;
  maxWidth?: number;
  // Шторка внутри ближайшего position: relative-родителя, а не во весь экран (витрина)
  contained?: boolean;
  style?: CSSProperties;
}

const icon = (Ico: typeof X) => <Ico size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

export function GenerationPanel<T extends string>(p: Props<T>) {
  const isMobile = useIsMobile();
  const [ownCollapsed, setOwnCollapsed] = useState(false);
  const [ownPeeked, setOwnPeeked] = useState(false);
  const [ownWidth, setOwnWidth] = useState<number>(GEN_PANEL_W.default);

  const collapsed = p.collapsed ?? ownCollapsed;
  const setCollapsed = (v: boolean) => { setOwnCollapsed(v); p.onCollapsedChange?.(v); };
  const peeked = p.peeked ?? ownPeeked;
  const setPeeked = (v: boolean) => { setOwnPeeked(v); p.onPeekedChange?.(v); };
  const width = p.width ?? ownWidth;
  const setWidth = (w: number) => { setOwnWidth(w); p.onWidthChange?.(w); };

  const sheet = p.layout === 'sheet' || (p.layout !== 'column' && isMobile);
  const view: GenerationPanelView = sheet ? (peeked ? 'peek' : 'sheet') : (collapsed ? 'spine' : 'column');

  if (view === 'spine') return <Spine {...p} onExpand={() => setCollapsed(false)} onTab={t => { p.onTabChange(t); setCollapsed(false); }} />;

  const head = (
    <div style={{
      height: HEAD_H, flex: `0 0 ${HEAD_H}px`, display: 'flex', alignItems: 'center', gap: SP.xs + 2,
      padding: `0 ${SP.xs + 2}px 0 ${SP.sm + 2}px`, minWidth: 0,
      borderBottom: `1px solid ${C.borderLight}`, background: sheet ? 'transparent' : C.bgInset,
    }}>
      <span style={{ display: 'inline-flex', color: C.accent, flexShrink: 0 }}>{p.icon}</span>
      <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>{p.title}</span>
      {p.subtitle && (
        <span style={{
          fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden',
          textOverflow: 'ellipsis', minWidth: 0,
        }}>{p.subtitle}</span>
      )}
      <span style={{ flex: 1 }} />
      {sheet ? (
        <IconButton size="xs" title={peeked ? 'Поднять шторку' : 'Опустить до цены — лента станет доступна'} onClick={() => setPeeked(!peeked)}>
          {icon(peeked ? ChevronUp : ChevronDown)}
        </IconButton>
      ) : (
        <IconButton size="xs" title="Свернуть в корешок" onClick={() => setCollapsed(true)}>
          {icon(ChevronRight)}
        </IconButton>
      )}
      {p.onClose && (
        <IconButton size="xs" title="Закрыть панель — сводка останется в полосе" onClick={p.onClose}>
          {icon(X)}
        </IconButton>
      )}
    </div>
  );

  const foot = p.footContent ?? (p.foot && <Foot foot={p.foot} />);
  const footBox = foot && (
    <div style={{
      flex: '0 0 auto', borderTop: `1px solid ${C.borderLight}`, background: C.bgCard,
      padding: `${SP.sm}px ${SP.md}px ${SP.sm + 2}px`,
    }}>
      {view === 'peek' && p.peekSummary && (
        <div style={{
          fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xs + 2,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{p.peekSummary}</div>
      )}
      {foot}
    </div>
  );

  const content = (
    <>
      <Tabs ariaLabel={`Панель «${p.title}»`} value={p.tab} items={p.tabs} onChange={p.onTabChange} />
      {p.context && (
        <div style={{
          flex: '0 0 auto', display: 'flex', alignItems: 'center', gap: SP.xs + 2, minWidth: 0,
          padding: `${SP.xs + 3}px ${SP.sm + 2}px`, borderBottom: `1px solid ${C.borderLight}`,
          fontSize: FS.sm, color: C.textSecondary,
        }}>{p.context}</div>
      )}
      <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: `${SP.xxs}px ${SP.md}px ${SP.md + 2}px` }}>
        {p.children}
      </div>
    </>
  );

  if (sheet) {
    const pos = p.contained ? 'absolute' : 'fixed';
    // Шторка: grab-хват поднимает и опускает её; тап по затемнению опускает, а не закрывает.
    // Опущенная — без затемнения: лента под ней остаётся рабочей.
    return (
      <>
        {view === 'sheet' && (
          <div aria-hidden onClick={() => setPeeked(true)} style={{
            position: pos, inset: 0, background: C.overlay, zIndex: Z.modal,
          }} />
        )}
        <div role="dialog" aria-label={p.title} style={{
          position: pos, left: 0, right: 0, bottom: 0, zIndex: Z.modal,
          height: view === 'sheet' ? SHEET_H : undefined,
          display: 'flex', flexDirection: 'column', overflow: 'hidden', fontFamily: FONT.sans,
          background: C.bgCard, borderRadius: `${R.sheet}px ${R.sheet}px 0 0`, boxShadow: SHADOW.sheet,
          ...p.style,
        }}>
          <button
            type="button"
            aria-label={peeked ? 'Поднять шторку' : 'Опустить шторку'}
            onClick={() => setPeeked(!peeked)}
            style={{
              flex: '0 0 auto', alignSelf: 'center', display: 'flex', justifyContent: 'center',
              width: 64, padding: `${SP.sm}px 0 ${SP.xxs}px`, border: 'none', background: 'transparent', cursor: 'pointer',
            }}
          >
            <span style={{ width: 40, height: 4, borderRadius: R.sm, background: C.track }} />
          </button>
          {head}
          {view === 'sheet' && content}
          {footBox}
        </div>
      </>
    );
  }

  return (
    <div role="complementary" aria-label={p.title} style={{
      position: 'relative', width, flex: `0 0 ${width}px`, minHeight: 0,
      display: 'flex', flexDirection: 'column', overflow: 'hidden', fontFamily: FONT.sans,
      background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.xxl,
      boxShadow: SHADOW.island,
      ...p.style,
    }}>
      <ResizeHandle
        value={width}
        min={p.minWidth ?? GEN_PANEL_W.min}
        max={p.maxWidth ?? GEN_PANEL_W.max}
        onChange={setWidth}
        ariaLabel={`Ширина панели «${p.title}»`}
      />
      {head}
      {content}
      {footBox}
    </div>
  );
}

// Низ: причина · очередь GPU · «− N +» · цена в две строки · кнопка запуска
function Foot({ foot: f }: { foot: GenerationFoot }) {
  return (
    <>
      {f.reason && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: SP.xs + 2, marginBottom: SP.xs + 2,
          fontSize: FS.sm, color: C.warningText,
        }}>
          <span style={{ display: 'inline-flex', marginTop: 1, flexShrink: 0 }}>{icon(AlertTriangle)}</span>
          <span>{f.reason}</span>
        </div>
      )}
      {f.queue && (
        <div style={{ marginBottom: SP.xs + 2 }}>
          <Badge tone="info" icon={<Cpu size={11} strokeWidth={ICON_STROKE} />}>{f.queue}</Badge>
        </div>
      )}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Stepper
          ariaLabel="Сколько вариантов"
          value={f.count}
          min={1}
          max={f.maxCount}
          maxHint={f.maxCountHint}
          onChange={n => f.onCountChange?.(n)}
        />
        <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, lineHeight: 1.35, color: C.textSecondary }}>
          <b style={{ color: C.textHeading }}>{f.price[0]}</b><br />{f.price[1]}
        </span>
        <Button
          size="sm"
          disabled={!!f.reason}
          title={f.reason}
          leftIcon={icon(Sparkles)}
          onClick={f.onRun}
          style={{ flexShrink: 0, whiteSpace: 'nowrap' }}
        >
          {f.runLabel}
        </Button>
      </div>
    </>
  );
}

// Корешок 44 px: развернуть, значки вкладок, круглая кнопка запуска с ценой в подсказке
function Spine<T extends string>(p: Props<T> & { onExpand: () => void; onTab: (t: T) => void }) {
  const f = p.foot;
  return (
    <div role="complementary" aria-label={p.title} style={{
      width: GEN_PANEL_W.spine, flex: `0 0 ${GEN_PANEL_W.spine}px`, minHeight: 0,
      display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs, padding: `${SP.xs + 2}px 0`,
      background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.xxl, boxShadow: SHADOW.island,
      ...p.style,
    }}>
      <IconButton active title={`Развернуть панель «${p.title}»`} onClick={p.onExpand}>{p.icon}</IconButton>
      <span style={{ width: 24, height: 1, background: C.divider, margin: `${SP.xxs}px 0` }} />
      {p.tabs.map(t => (
        <IconButton key={t.value} active={t.value === p.tab} title={t.label} onClick={() => p.onTab(t.value)}>
          {t.icon}
        </IconButton>
      ))}
      <span style={{ flex: 1 }} />
      {f && (
        <Button
          pill
          size="xs"
          disabled={!!f.reason}
          title={f.reason ?? `${f.runLabel} · ${f.price[0]}`}
          onClick={f.onRun}
          style={{ width: 32, height: 32, minHeight: 32, padding: 0 }}
        >
          {icon(Sparkles)}
        </Button>
      )}
    </div>
  );
}
