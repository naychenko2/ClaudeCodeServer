import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { CSSProperties, ReactNode } from 'react';
import { AlertTriangle, Check, ChevronDown, ChevronRight, ChevronUp, CornerUpLeft, Cpu, RefreshCw, Sparkles, X } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { GEN_PANEL_INLINE_MIN, useWindowWidth } from '../../lib/breakpoints';
import { holdGenSheetRaised } from '../../lib/genSheet';
import { useGenDraft } from '../../lib/genDrafts';
import { followPeeked, holdGenPanelOpen } from '../../lib/genPanelOpen';
import type { GenerationAgentPick } from '../../lib/genPanelFollow';
import { useRequestPanelFill } from '../../pages/workspace/panelFill';
import { Badge, Button, IconButton, PanelHeaderSlot, ProgressBar, ResizeHandle, Stepper, Tabs, useHasPanelHeader } from '../ui';
import type { TabItem } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Общий каркас боковой панели генерации — «Картинки», «Звук», позже «Видео» (ADR-021 §3).
// Каркас рисует шапку, вкладки, строку контекста, прокручиваемое тело, закреплённый низ,
// корешок и шторку; вертикаль отдаёт только содержимое вкладок и данные низа.
// Образец разметки — genPanel() в docs/mockups/image-editor-v4-panel.html.
// Внутри оболочки панели рабочей области (PanelShell зоны) шапку, закрытие и ширину держит
// оболочка: каркас рисует только вкладки, тело и низ, подзаголовок уходит в её шапку.
// Там каркас просит у зоны всю высоту колонки: низ с ценой и запуском закреплён у кромки
// окна и не прыгает между вкладками и операциями.

export const GEN_PANEL_W = { default: 380, min: 340, max: 520, spine: 44 } as const;
const HEAD_H = 42;
// Разовые отступы макета вне шкалы SP: имена вместо «SP.xs + 2» по месту
const PAD = {
  row: SP.xs + 2,       // зазор в строках шапки, контекста, низа и корешка
  edge: SP.sm + 2,      // левый край шапки и строки контекста, низ подвала
  ctxY: SP.xs + 3,      // вертикаль строки контекста
  bodyB: SP.md + 2,     // низ прокручиваемого тела
} as const;
// Хват шторки: зона нажатия шире видимой полоски
const GRAB = { hitW: 64, barW: 40, barH: 4 } as const;
const QUEUE_ICON = 11;  // значок в бейдже очереди — мельче ICON_SIZE.xs, по макету
const SPINE_DIVIDER_W = 24;
const SPINE_RUN = 32;   // круглая кнопка запуска в корешке
const TOUCH_MIN = 44;   // тач-цель на телефоне: кнопки низа шторки не ниже этого
// Шторка телефона занимает 88 % высоты: над ней остаётся видна полоса ленты
const SHEET_H = '88%';

// Контракт закреплённого низа — ровно из макета (ADR-021 §3). Общая часть плюс два
// независимых выбора: счётчик вариантов и состояние запуска.
// Счётчик: без count «− N +» не рисуется (у «Фильма» один результат); при maxCount: 1
// колбэк не нужен (кнопки ± заперты), при любом другом потолке тип требует
// onCountChange — иначе «+» кликался бы молча впустую.
// Состояние: покой (кнопка запуска), progress (идёт работа — вместо кнопки полоса и
// «Отмена»), result (готовый файл с действиями над кнопкой). stale — причины пересборки
// над кнопкой, сочетается с покоем и результатом.
export interface GenerationFootProgress {
  label: string;                    // «Собираем: сцена 3 из 4»
  p?: number;                       // 0..100; нет — ход неизвестен, полоса не рисуется
  onCancel?: () => void;
}
export interface GenerationFootResult {
  file: string;                     // «film.mp4»
  actions: { label: string; onClick: () => void }[];
}
export type GenerationFootCount =
  | { count?: undefined; maxCount?: undefined; onCountChange?: undefined }
  | ({ count: number; maxCountHint?: string } & (
    | { maxCount: 1; onCountChange?: (n: number) => void }
    | { maxCount: number; onCountChange: (n: number) => void }
  ));
export type GenerationFootState =
  | { progress?: undefined; result?: undefined }
  | { progress: GenerationFootProgress; result?: undefined }
  | { progress?: undefined; result: GenerationFootResult };
export type GenerationFoot = {
  reason?: string;                  // почему запуск невозможен; задана — кнопка гаснет
  queue?: string;                   // очередь GPU у локальных моделей
  price?: [string, string];         // итог («≈ $0.08») и расшифровка («2 × $0.04»)
  runLabel: string;                 // глагол запуска: «Изменить», «Перегенерировать»
  runDisabled?: boolean;            // кнопка серая без причины: делать нечего («Собрано» — результат уже актуален)
  runIcon?: ReactNode;              // значок запуска; без него ✦ — значок ИИ
  noCount?: boolean;                // правка без ИИ: один результат, «− N +» не рисуем
  stale?: string[];                 // что изменилось после сборки: «порядок сцен», «склейка 2»
  onRun: () => void;
} & GenerationFootCount & GenerationFootState;

// Уже GEN_PANEL_INLINE_MIN панели генерации нет места в зоне: её рисует шторкой вертикаль
// у полосы над полем ввода (genPanelPlacement)
export const useGenerationSheet = (): boolean => useWindowWidth() < GEN_PANEL_INLINE_MIN;

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
  contextAction?: ReactNode;        // кнопка в конце строки контекста («Снять выбор» ✕)
  // Ключ панели в рабочей области («images», «sound»): каркас отмечает её открытой, пока
  // смонтирован, — по этому признаку клик по карточке переключает панель (genPanelFollow)
  panelKey?: string;
  // «↩ К сцене 5» — ссылка назад к панели, из которой сюда пришли (returnTo события показа)
  returnLink?: { label: string; onClick: () => void };
  // Подсказка «✦ Claude взял в работу: имя · Открыть · ✕» под шапкой
  agentPick?: GenerationAgentPick;
  // Ключ элемента «Работаем с»: есть черновик — в строке контекста пометка «черновик»
  draftKey?: string | null;
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
  const narrow = useGenerationSheet();
  const inShell = useHasPanelHeader();
  const [ownCollapsed, setOwnCollapsed] = useState(false);
  // Шторка, пришедшая на смену опущенной соседке по клику на карточку, тоже опущена
  const [ownPeeked, setOwnPeeked] = useState(() => !!p.panelKey && followPeeked(p.panelKey));
  const [ownWidth, setOwnWidth] = useState<number>(GEN_PANEL_W.default);

  const collapsed = p.collapsed ?? ownCollapsed;
  const setCollapsed = (v: boolean) => { setOwnCollapsed(v); p.onCollapsedChange?.(v); };
  const peeked = p.peeked ?? ownPeeked;
  const setPeeked = (v: boolean) => { setOwnPeeked(v); p.onPeekedChange?.(v); };
  const width = p.width ?? ownWidth;
  const setWidth = (w: number) => { setOwnWidth(w); p.onWidthChange?.(w); };

  // Внутри оболочки зоны вид держит оболочка: шторка там дала бы вторую шапку поверх её шапки
  const sheet = !inShell && (p.layout === 'sheet' || (p.layout !== 'column' && narrow));
  const view: GenerationPanelView = sheet ? (peeked ? 'peek' : 'sheet') : (collapsed ? 'spine' : 'column');
  // Опущенная шторка встаёт в поток над полем ввода (там, где её рисует полоса): поверх
  // низа экрана она закрывала бы композер, а смысл вида «до цены» — рабочие лента и поле
  const peekInFlow = view === 'peek' && !p.contained;
  const raised = view === 'sheet' && !p.contained;

  useRequestPanelFill(inShell);
  useEffect(() => (raised ? holdGenSheetRaised() : undefined), [raised]);
  const openView = view === 'spine' ? null : view;
  useEffect(() => (p.panelKey && openView ? holdGenPanelOpen(p.panelKey, openView) : undefined), [p.panelKey, openView]);
  const draft = useGenDraft(p.draftKey ?? null);

  if (view === 'spine' && !inShell) return <Spine {...p} onExpand={() => setCollapsed(false)} onTab={t => { p.onTabChange(t); setCollapsed(false); }} />;

  const head = (
    <div style={{
      height: HEAD_H, flex: `0 0 ${HEAD_H}px`, display: 'flex', alignItems: 'center', gap: PAD.row,
      padding: `0 ${PAD.row}px 0 ${PAD.edge}px`, minWidth: 0,
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

  const foot = p.footContent ?? (p.foot && <GenerationFootView foot={p.foot} touch={sheet} />);
  const footBox = foot && (
    <div style={{
      flex: '0 0 auto', borderTop: `1px solid ${C.borderLight}`, background: C.bgCard,
      padding: `${SP.sm}px ${SP.md}px ${PAD.edge}px`,
    }}>
      {view === 'peek' && p.peekSummary && (
        <div style={{
          fontSize: FS.sm, color: C.textSecondary, marginBottom: PAD.row,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{p.peekSummary}</div>
      )}
      {foot}
    </div>
  );

  const pick = p.agentPick && <AgentPickRow pick={p.agentPick} />;
  const content = (
    <>
      {p.returnLink && <ReturnRow link={p.returnLink} />}
      <Tabs ariaLabel={`Панель «${p.title}»`} value={p.tab} items={p.tabs} onChange={p.onTabChange} transparent={sheet} />
      {p.context && (
        <div style={{
          flex: '0 0 auto', display: 'flex', alignItems: 'center', gap: PAD.row, minWidth: 0,
          padding: `${PAD.ctxY}px ${PAD.edge}px`, borderBottom: `1px solid ${C.borderLight}`,
          fontSize: FS.sm, color: C.textSecondary,
        }}>
          {p.context}
          {draft && <span data-gen-draft="" style={{ flexShrink: 0 }}><Badge size="xs" tone="warning">черновик</Badge></span>}
          {p.contextAction}
        </div>
      )}
      <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: `${SP.xxs}px ${SP.md}px ${PAD.bodyB}px` }}>
        {p.children}
      </div>
    </>
  );

  if (inShell) {
    return (
      <div role="complementary" aria-label={p.title} style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column', fontFamily: FONT.sans, ...p.style }}>
        {p.subtitle && (
          <PanelHeaderSlot side="left">
            <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}>
              {p.subtitle}
            </span>
          </PanelHeaderSlot>
        )}
        {pick}
        {content}
        {footBox}
      </div>
    );
  }

  if (sheet) {
    const pos = p.contained ? 'absolute' : 'fixed';
    // Шторка: grab-хват поднимает и опускает её; тап по затемнению опускает, а не закрывает.
    // Опущенная — без затемнения: лента под ней остаётся рабочей.
    // Затемнение намеренно декоративное (aria-hidden, без фокуса): с клавиатуры шторку
    // опускают хват ниже и кнопка-шеврон в шапке.
    const node = (
      <>
        {view === 'sheet' && (
          <div aria-hidden onClick={() => setPeeked(true)} style={{
            position: pos, inset: 0, background: C.overlay, zIndex: Z.modal,
          }} />
        )}
        <div role="dialog" aria-label={p.title} data-gen-sheet={view} style={{
          ...(peekInFlow
            ? { position: 'relative', marginBottom: SP.xs, border: `1px solid ${C.border}`, borderRadius: R.xxl }
            : {
              position: pos, left: 0, right: 0, bottom: 0, zIndex: Z.modal,
              borderRadius: `${R.sheet}px ${R.sheet}px 0 0`, boxShadow: SHADOW.sheet,
            }),
          height: view === 'sheet' ? SHEET_H : undefined,
          display: 'flex', flexDirection: 'column', overflow: 'hidden', fontFamily: FONT.sans,
          background: C.bgCard,
          ...p.style,
        }}>
          <button
            type="button"
            aria-label={peeked ? 'Поднять шторку' : 'Опустить шторку'}
            onClick={() => setPeeked(!peeked)}
            style={{
              flex: '0 0 auto', alignSelf: 'center', display: 'flex', justifyContent: 'center',
              width: GRAB.hitW, padding: `${SP.sm}px 0 ${SP.xxs}px`, border: 'none', background: 'transparent', cursor: 'pointer',
            }}
          >
            <span style={{ width: GRAB.barW, height: GRAB.barH, borderRadius: R.sm, background: C.track }} />
          </button>
          {head}
          {view === 'sheet' && pick}
          {view === 'sheet' && content}
          {footBox}
        </div>
      </>
    );
    // Поднятая — порталом в body: внутри композера её слой локален контексту наложения
    // полосы, и тосты оболочки ложились поверх шапки. Опущенная стоит в потоке на месте
    return raised && typeof document !== 'undefined' ? createPortal(node, document.body) : node;
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
      {pick}
      {content}
      {footBox}
    </div>
  );
}

// «↩ К фильму «утро» — панель «Видео»»: возврат к вызвавшей панели
function ReturnRow({ link }: { link: { label: string; onClick: () => void } }) {
  return (
    <div data-gen-return="" style={{
      flex: '0 0 auto', display: 'flex', alignItems: 'center', minWidth: 0,
      padding: `${SP.xxs}px ${PAD.row}px ${SP.xxs}px ${PAD.row}px`, borderBottom: `1px solid ${C.borderLight}`,
    }}>
      <Button size="xs" variant="ghost" leftIcon={icon(CornerUpLeft)} onClick={link.onClick} title={link.label}
        style={{ minWidth: 0, maxWidth: '100%' }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{link.label}</span>
      </Button>
    </div>
  );
}

// «✦ Claude взял в работу: кадр-6.png · Открыть · ✕» — выбор агента в другом разделе
function AgentPickRow({ pick }: { pick: GenerationAgentPick }) {
  return (
    <div data-gen-agent-pick="" style={{
      flex: '0 0 auto', display: 'flex', alignItems: 'center', gap: PAD.row, minWidth: 0,
      padding: `${SP.xxs}px ${PAD.row}px ${SP.xxs}px ${PAD.edge}px`, borderBottom: `1px solid ${C.borderLight}`,
      background: C.accentLight, fontSize: FS.sm, color: C.textSecondary,
    }}>
      <span style={{ display: 'inline-flex', color: C.accent, flexShrink: 0 }}>{icon(Sparkles)}</span>
      <span style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        Claude взял в работу: <b style={{ color: C.textHeading }}>{pick.label}</b>
      </span>
      <Button size="xs" variant="ghost" onClick={pick.onOpen} style={{ flexShrink: 0 }}>Открыть</Button>
      <IconButton size="xs" title="Скрыть подсказку" ariaLabel="Скрыть подсказку" onClick={pick.onDismiss}>{icon(X)}</IconButton>
    </div>
  );
}

// Низ: причина · очередь GPU · «устарел» · результат · «− N +» · цена в две строки · кнопка
// запуска; во время работы строка запуска заменяется полосой прогресса с «Отменить»
export function GenerationFootView({ foot: f, touch }: { foot: GenerationFoot; touch?: boolean }) {
  // На телефоне кнопки низа — тач-цели 44 px: главная — размера md, остальные растянуты по высоте
  const hit = touch ? { height: TOUCH_MIN, minHeight: TOUCH_MIN } : undefined;
  const showCount = !f.noCount && f.count !== undefined;
  return (
    <>
      {f.reason && (
        <div style={{
          display: 'flex', alignItems: 'flex-start', gap: PAD.row, marginBottom: PAD.row,
          fontSize: FS.sm, color: C.warningText,
        }}>
          <span style={{ display: 'inline-flex', marginTop: 1, flexShrink: 0 }}>{icon(AlertTriangle)}</span>
          <span>{f.reason}</span>
        </div>
      )}
      {f.queue && (
        <div style={{ marginBottom: PAD.row }}>
          <Badge tone="info" icon={<Cpu size={QUEUE_ICON} strokeWidth={ICON_STROKE} />}>{f.queue}</Badge>
        </div>
      )}
      {f.stale && f.stale.length > 0 && (
        <div data-gen-foot-stale="" style={{
          display: 'flex', alignItems: 'flex-start', gap: PAD.row, marginBottom: PAD.row,
          fontSize: FS.sm, color: C.info,
        }}>
          <span style={{ display: 'inline-flex', marginTop: 1, flexShrink: 0 }}>{icon(RefreshCw)}</span>
          <span>Изменено после сборки: {f.stale.join(' · ')}</span>
        </div>
      )}
      {f.result && (
        <div data-gen-foot-result="" style={{
          display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: PAD.row, marginBottom: PAD.row, minWidth: 0,
          fontSize: FS.sm, color: C.textSecondary,
        }}>
          <span style={{ display: 'inline-flex', color: C.success, flexShrink: 0 }}>{icon(Check)}</span>
          <span title={f.result.file} style={{
            flex: '1 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            fontWeight: 600, color: C.textHeading, textDecoration: f.stale?.length ? 'line-through' : undefined,
          }}>{f.result.file}</span>
          {f.stale?.length ? <Badge size="xs" tone="warning">устарел</Badge> : null}
          {f.result.actions.map(a => (
            <Button key={a.label} size="xs" variant="ghost" onClick={a.onClick} style={{ flexShrink: 0, ...hit }}>{a.label}</Button>
          ))}
        </div>
      )}
      {f.progress ? (
        <div data-gen-foot-progress="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: PAD.row }}>
            <span title={f.progress.label} style={{
              fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}>{f.progress.label}</span>
            {f.progress.p !== undefined && <ProgressBar value={f.progress.p} />}
          </span>
          {f.progress.onCancel && (
            <Button size="xs" variant="ghost" onClick={f.progress.onCancel} style={{ flexShrink: 0, ...hit }}>Отменить</Button>
          )}
        </div>
      ) : (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          {showCount && <Stepper
            ariaLabel="Сколько вариантов"
            value={f.count!}
            min={1}
            max={f.maxCount!}
            maxHint={f.maxCountHint}
            touch={touch}
            onChange={n => f.onCountChange?.(n)}
          />}
          {/* Цена ровно в две строки: строки не переносятся, а режутся многоточием */}
          {f.price ? (
            <span title={`${f.price[0]} · ${f.price[1]}`} style={{
              flex: 1, minWidth: 0, fontSize: FS.sm, lineHeight: 1.35, color: C.textSecondary,
              whiteSpace: 'nowrap',
            }}>
              <b style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', color: C.textHeading }}>{f.price[0]}</b>
              <span style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.price[1]}</span>
            </span>
          ) : <span style={{ flex: 1 }} />}
          <Button
            size={touch ? 'md' : 'xs'}
            disabled={!!f.reason || !!f.runDisabled}
            title={f.reason}
            leftIcon={f.runIcon ?? icon(Sparkles)}
            onClick={f.onRun}
            style={{ flexShrink: 0, whiteSpace: 'nowrap', ...(touch ? { minHeight: TOUCH_MIN } : null) }}
          >
            {f.runLabel}
          </Button>
        </div>
      )}
    </>
  );
}

// Корешок 44 px: развернуть, значки вкладок, круглая кнопка запуска с ценой в подсказке
function Spine<T extends string>(p: Props<T> & { onExpand: () => void; onTab: (t: T) => void }) {
  const f = p.foot;
  return (
    <div role="complementary" aria-label={p.title} style={{
      width: GEN_PANEL_W.spine, flex: `0 0 ${GEN_PANEL_W.spine}px`, minHeight: 0,
      display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs, padding: `${PAD.row}px 0`,
      background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.xxl, boxShadow: SHADOW.island,
      ...p.style,
    }}>
      <IconButton active title={`Развернуть панель «${p.title}»`} onClick={p.onExpand}>{p.icon}</IconButton>
      <span style={{ width: SPINE_DIVIDER_W, height: 1, background: C.divider, margin: `${SP.xxs}px 0` }} />
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
          disabled={!!f.reason || !!f.runDisabled}
          title={f.reason ?? (f.price ? `${f.runLabel} · ${f.price[0]}` : f.runLabel)}
          onClick={f.onRun}
          style={{ width: SPINE_RUN, height: SPINE_RUN, minHeight: SPINE_RUN, padding: 0 }}
        >
          {f.runIcon ?? icon(Sparkles)}
        </Button>
      )}
    </div>
  );
}
