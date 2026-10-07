// Тело «Формата» (ADR-025, макет docs/mockups/media-format-v1.html): одно и то же во всплывашке над строкой,
// в шторке телефона и в секции панели «Контекст». Здесь только отрисовка по готовой FormatModel: всё
// посчитано сервером (ручка …/format), своей арифметики кратности и «ближайшего» нет. Тексты — из записки
// (lib/chatContext/formatText.ts).
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Check, Info, Maximize2, Send, TriangleAlert } from 'lucide-react';
import { C, FS, R, SP } from '../../../lib/design';
import { FORMAT_TEXT, TIER_NAME, TIER_ORDER, tierCaption } from '../../../lib/chatContext/formatText';
import type { CropAnchor, FramesAnchor, FormatEditModel, FormatFramesModel, FormatModel, FormatTierOption, MediaFormatState } from '../../../lib/chatContext/formatTypes';
import { Button, InlineSegmented, Notice, TextField } from '../../ui';
import { ICON_SIZE, ICON_STROKE } from '../../ui/icons';
import { previewRects, ratioOf } from './formatPreview';

// Пресеты «Точно…» — подсказки полей, значения всё равно проходит сервер
const EXACT_PRESETS: readonly (readonly [number, number])[] = [[1920, 1080], [1080, 1920], [1080, 1080], [1280, 720], [2048, 2048]];
// Пауза перед записью набранного размера: ответ ручки приходит уже посчитанным, а не на каждую цифру
const EXACT_DEBOUNCE_MS = 500;

const label = { fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 } as const;

function Lab({ children, aside }: { children: ReactNode; aside?: ReactNode }) {
  return (
    <div style={{ ...label, display: 'flex', alignItems: 'center', gap: SP.sm - 2 }}>
      {children}
      {aside != null && <span style={{ marginLeft: 'auto', textTransform: 'none', letterSpacing: 0, fontWeight: 400 }}>{aside}</span>}
    </div>
  );
}

// Силуэт кадра пропорции: большая сторона 18 px
function Glyph({ aspect }: { aspect: string }) {
  const r = ratioOf(aspect) ?? 1;
  const w = r >= 1 ? 18 : Math.round(18 * r);
  const h = r >= 1 ? Math.round(18 / r) : 18;
  return <span aria-hidden style={{ display: 'inline-block', width: w, height: h, border: '1.5px solid currentColor', borderRadius: 2, flexShrink: 0 }} />;
}

const tileBase = (on: boolean, isMobile: boolean) => ({
  display: 'inline-flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 3,
  minWidth: isMobile ? 48 : 46, height: isMobile ? 52 : 46, padding: `${SP.xs}px ${SP.sm - 2}px`, boxSizing: 'border-box',
  border: `1px solid ${on ? C.accent : C.border}`, borderRadius: R.md, cursor: 'pointer', fontFamily: 'inherit',
  background: on ? C.accentLight : C.bgCard, color: on ? C.accent : C.textSecondary, fontSize: FS.xs, fontWeight: on ? 600 : 400,
}) as const;

function Aspects({ aspects, active, isMobile, onPick }: { aspects: readonly string[]; active: string | null; isMobile: boolean; onPick: (a: string) => void }) {
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm - 2 }}>
      {aspects.map(a => (
        <button key={a} type="button" data-fmt-aspect={a} aria-pressed={a === active} onClick={() => onPick(a)} style={tileBase(a === active, isMobile)}>
          <Glyph aspect={a} />
          <span>{a}</span>
        </button>
      ))}
    </div>
  );
}

function Tier({ o, on, onPick }: { o: FormatTierOption; on: boolean; onPick: () => void }) {
  const cap = tierCaption(o);
  const off = !!o.disabledReason;
  const body = (
    <>
      <b style={{ fontSize: FS.sm, color: on ? C.accent : C.textHeading }}>{TIER_NAME[o.tier]}</b>
      <span title={cap.size} style={{ fontSize: FS.xs, color: C.textSecondary, maxWidth: '100%', overflowWrap: 'anywhere' }}>{cap.size}</span>
      <em title={cap.price || undefined} style={{ fontStyle: 'normal', fontSize: FS.xs, color: C.textMuted, maxWidth: '100%', overflowWrap: 'anywhere' }}>{cap.price || ' '}</em>
    </>
  );
  const style = {
    display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: 1, padding: `${SP.sm - 1}px ${SP.sm}px`,
    minWidth: 0, boxSizing: 'border-box', textAlign: 'left', fontFamily: 'inherit', borderRadius: R.md,
    border: `1px ${off ? 'dashed' : 'solid'} ${on ? C.accent : C.border}`, background: on ? C.accentLight : off ? 'transparent' : C.bgCard,
  } as const;
  return off
    // Серая ступень не кнопка: причина в подсказке и в строке под рядом
    ? <div data-fmt-tier={o.tier} data-fmt-off="" title={o.disabledReason ?? undefined} style={{ ...style, opacity: 0.6, cursor: 'default' }}>{body}</div>
    : <button type="button" data-fmt-tier={o.tier} aria-pressed={on} onClick={onPick} style={{ ...style, cursor: 'pointer' }}>{body}</button>;
}

function Tiers({ state, onPick }: { state: MediaFormatState; onPick: (t: FormatTierOption) => void }) {
  const byTier = new Map(state.tiers.map(t => [t.tier, t]));
  const grey = state.tiers.find(t => t.disabledReason)?.disabledReason;
  const exact = state.resolved.mode === 'exact';
  const note = exact ? null : state.tiers.find(t => t.tier === state.resolved.tier)?.note;
  return (
    <>
      <Lab>Качество</Lab>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: SP.sm - 2 }}>
        {TIER_ORDER.map(t => byTier.get(t)).filter((o): o is FormatTierOption => !!o).map(o => (
          <Tier key={o.tier} o={o} on={!exact && state.resolved.tier === o.tier} onPick={() => onPick(o)} />
        ))}
      </div>
      {grey && <div data-fmt-hint="" style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>{grey}</div>}
      {note && <div data-fmt-hint="tier-note" style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>{note}</div>}
    </>
  );
}

// ── «Точно…» ──

const digits = (v: string) => v.replace(/[^\d]/g, '').slice(0, 5);

function ExactPanel({ m, state, isMobile, onClose }: { m: FormatModel; state: MediaFormatState; isMobile: boolean; onClose: () => void }) {
  const r = state.resolved;
  const wished = state.wish?.mode === 'exact' ? state.wish : null;
  const [w, setW] = useState(String(wished?.width ?? r.width ?? ''));
  const [h, setH] = useState(String(wished?.height ?? r.height ?? ''));
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const push = (nw: string, nh: string, now = false) => {
    if (timer.current) clearTimeout(timer.current);
    const width = Number(nw);
    const height = Number(nh);
    if (!(width > 0 && height > 0)) return;
    const send = () => m.onChange({ mode: 'exact', width, height });
    if (now) send(); else timer.current = setTimeout(send, EXACT_DEBOUNCE_MS);
  };
  const box = isMobile ? 96 : 84;
  const input = (value: string, aria: string, onChange: (v: string) => void) => (
    <div style={{ width: box }}>
      <TextField value={value} placeholder={aria} title={aria} onChange={onChange}
        style={{ padding: `${SP.xs + 1}px ${SP.sm}px`, fontSize: isMobile ? FS.lg : FS.base, fontVariantNumeric: 'tabular-nums' }} />
    </div>
  );
  const exact = r.mode === 'exact';
  return (
    <div data-fmt-exact="" style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm - 2, padding: SP.sm, border: `1px solid ${C.borderLight}`,
      borderRadius: R.md, background: C.bgPanel,
    }}>
      <Lab aside={<button type="button" data-fmt-exact-close="" onClick={onClose} style={linkStyle}>{exact ? FORMAT_TEXT.exactBack : FORMAT_TEXT.exactHide}</button>}>Точно</Lab>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm - 2 }}>
        {input(w, 'Ширина', v => { const d = digits(v); setW(d); push(d, h); })}
        <span style={{ color: C.textSecondary }}>×</span>
        {input(h, 'Высота', v => { const d = digits(v); setH(d); push(w, d); })}
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>px</span>
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
        {EXACT_PRESETS.map(([pw, ph]) => {
          const on = wished?.width === pw && wished?.height === ph;
          return (
            <button key={`${pw}x${ph}`} type="button" data-fmt-preset={`${pw}x${ph}`} onClick={() => { setW(String(pw)); setH(String(ph)); push(String(pw), String(ph), true); }}
              style={{
                height: isMobile ? 32 : 24, padding: `0 ${SP.sm - 1}px`, border: `1px solid ${on ? C.accent : C.borderLight}`, borderRadius: R.max,
                background: C.bgCard, color: on ? C.accent : C.textSecondary, fontSize: FS.xs, fontFamily: 'inherit', cursor: 'pointer', fontVariantNumeric: 'tabular-nums',
              }}>{pw}×{ph}</button>
          );
        })}
      </div>
      {exact && r.width && r.height && (
        <div data-fmt-out="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm - 2, fontSize: FS.sm, color: C.textSecondary }}>
          <Send size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          <span>{FORMAT_TEXT.exactOut}</span>
          <b style={{ color: C.textHeading, fontVariantNumeric: 'tabular-nums' }}>{r.width}×{r.height}</b>
        </div>
      )}
      {exact && r.rounded && r.note && <Notice tone="warning" icon={TriangleAlert} data-fmt-note="rounded">{r.note}</Notice>}
    </div>
  );
}

const linkStyle = {
  border: 'none', background: 'transparent', color: C.accent, cursor: 'pointer', padding: 0, fontSize: FS.sm, fontFamily: 'inherit',
} as const;

// ── Правка картинки ──

function Preview({ source, target, mode, anchor, height = 64 }: {
  source: { aspect: string; thumb: string | null }; target: string; mode: 'pad' | 'crop'; anchor: FramesAnchor; height?: number;
}) {
  const g = previewRects(source.aspect, target, mode, anchor, height);
  if (!g) return null;
  const px = (n: number) => Math.round(n * 10) / 10;
  const img = (
    <div style={{
      position: 'absolute', left: px(g.image.x), top: px(g.image.y), width: px(g.image.w), height: px(g.image.h),
      background: source.thumb ? `center / cover url("${source.thumb}")` : C.accentMuted,
    }} />
  );
  return (
    <div data-fmt-preview={mode} style={{ position: 'relative', flexShrink: 0, width: px(g.width), height: g.height, background: C.bgInset, borderRadius: R.sm / 2, overflow: 'hidden' }}>
      {mode === 'pad' && <div style={{ position: 'absolute', inset: 0, background: `repeating-linear-gradient(45deg, ${C.track} 0 4px, transparent 4px 8px)` }} />}
      {img}
      {mode === 'crop' && (
        <>
          {/* Вне рамки — затемнение: что отрежется */}
          <div style={{ position: 'absolute', left: 0, top: 0, width: px(g.frame.x), height: g.height, background: C.overlay }} />
          <div style={{ position: 'absolute', left: px(g.frame.x + g.frame.w), top: 0, width: px(g.width - g.frame.x - g.frame.w), height: g.height, background: C.overlay }} />
          <div style={{ position: 'absolute', left: px(g.frame.x), top: 0, width: px(g.frame.w), height: px(g.frame.y), background: C.overlay }} />
          <div style={{ position: 'absolute', left: px(g.frame.x), top: px(g.frame.y + g.frame.h), width: px(g.frame.w), height: px(g.height - g.frame.y - g.frame.h), background: C.overlay }} />
          <div style={{ position: 'absolute', left: px(g.frame.x), top: px(g.frame.y), width: px(g.frame.w), height: px(g.frame.h), border: `1.5px solid ${C.accent}`, boxSizing: 'border-box' }} />
        </>
      )}
    </div>
  );
}

const optBtn = {
  display: 'flex', gap: SP.md - 2, alignItems: 'center', padding: SP.sm - 1, border: `1px solid ${C.border}`, borderRadius: R.md,
  background: C.bgCard, cursor: 'pointer', textAlign: 'left', width: '100%', fontFamily: 'inherit', fontSize: FS.sm, color: C.textPrimary,
} as const;

export function Anchors({ value, onChange, isMobile }: { value: CropAnchor; onChange: (a: CropAnchor) => void; isMobile: boolean }) {
  return (
    <InlineSegmented<CropAnchor> isMobile={isMobile} value={value} onChange={onChange}
      options={(['left', 'center', 'right'] as const).map(a => ({ value: a, label: FORMAT_TEXT.anchors[a] }))} />
  );
}

// Вопрос «Дорисовать края / Обрезать»: сама модель пропорции не тянет и не режет — только явный выбор
function Ask({ e, isMobile }: { e: FormatEditModel; isMobile: boolean }) {
  const ref = useRef<HTMLDivElement>(null);
  const shown = !!e.source && !!e.aspect;
  // Вопрос появляется ниже сетки пропорций и в узкой панели уезжает за край: «Обрезать» не видно без прокрутки
  useEffect(() => { if (shown) ref.current?.scrollIntoView?.({ block: 'nearest' }); }, [shown, e.aspect]);
  if (!e.source || !e.aspect) return null;
  const src = { aspect: e.source.aspect, thumb: e.source.thumb };
  return (
    <div ref={ref} data-fmt-ask="" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm - 2, padding: SP.sm, border: `1px solid ${C.accentMuted}`, borderRadius: R.md, background: C.bgCard }}>
      <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>{e.source.aspect} → {e.aspect}. Как получить {e.aspect}?</div>
      <button type="button" data-fmt-how="outpaint" onClick={() => e.onHow('outpaint')} style={optBtn}>
        <Preview source={src} target={e.aspect} mode="pad" anchor={e.anchor} />
        <span style={{ flex: 1, minWidth: 0 }}>
          <b style={{ display: 'block', color: C.textHeading }}>{FORMAT_TEXT.askOutpaint}</b>
          <small style={{ color: C.textMuted, fontSize: FS.xs }}>{e.outpaint.detail}</small>
        </span>
      </button>
      <button type="button" data-fmt-how="crop" onClick={() => e.onHow('crop')} style={optBtn}>
        <Preview source={src} target={e.aspect} mode="crop" anchor={e.anchor} />
        <span style={{ flex: 1, minWidth: 0 }}>
          <b style={{ display: 'block', color: C.textHeading }}>{FORMAT_TEXT.askCrop}</b>
          <small style={{ color: C.textMuted, fontSize: FS.xs }}>{e.crop.detail}</small>
        </span>
      </button>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>{FORMAT_TEXT.anchorLabel}</span>
        <Anchors value={e.anchor} onChange={e.onAnchor} isMobile={isMobile} />
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
        {FORMAT_TEXT.askNote} <button type="button" data-fmt-cancel="" onClick={e.onCancel} style={{ ...linkStyle, fontSize: FS.xs }}>Отмена</button>
      </div>
    </div>
  );
}

// Кадры A/B будут подрезаны под формат сцены (видео): превью в рамке цели и якорь подрезки
function Frames({ f, isMobile }: { f: FormatFramesModel; isMobile: boolean }) {
  return (
    <Notice tone="warning" icon={TriangleAlert} data-fmt-note="frames">
      {f.text}
      <div data-fmt-frames="" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: SP.xs }}>
        {f.previews.map(p => (
          <div key={p.slot} style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs, alignItems: 'flex-start' }}>
            <Preview source={{ aspect: p.aspect, thumb: p.thumb }} target={f.target} mode="crop" anchor={f.anchor} height={56} />
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>Кадр {p.slot} · {p.aspect}</span>
          </div>
        ))}
      </div>
      {f.axis === 'sides' && <div style={{ marginTop: SP.xs }}><Anchors value={f.anchor as CropAnchor} onChange={f.onAnchor} isMobile={isMobile} /></div>}
      {f.axis === 'height' && (
        <div data-fmt-anchors="height" style={{ marginTop: SP.xs }}>
          <InlineSegmented<FramesAnchor> isMobile={isMobile} value={f.anchor} onChange={f.onAnchor}
            options={(['top', 'center', 'bottom'] as const).map(a => ({ value: a, label: FORMAT_TEXT.anchorsHeight[a] }))} />
        </div>
      )}
      {f.axis === 'mixed' && <div data-fmt-anchors="mixed" style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textMuted }}>{FORMAT_TEXT.anchorMixed}</div>}
    </Notice>
  );
}

function EditPanel({ e, state, isMobile }: { e: FormatEditModel; state: MediaFormatState | null; isMobile: boolean }) {
  const aspects = state?.caps?.aspects.filter(a => a !== e.source?.aspect) ?? [];
  const asSource = e.aspect === null;
  return (
    <>
      <button type="button" data-fmt-source="" aria-pressed={asSource} onClick={e.onSource} style={{
        ...optBtn, border: `1px solid ${asSource ? C.accent : C.border}`, background: asSource ? C.accentLight : C.bgCard,
      }}>
        <span style={{ flex: 1, minWidth: 0 }}>
          <b style={{ color: C.textHeading }}>{FORMAT_TEXT.asSource}</b>{e.source ? ` · ${e.source.aspect}` : ''}
          {e.source && (
            <small style={{ display: 'block', color: C.textMuted, fontSize: FS.xs }}>
              {e.source.width}×{e.source.height} — модель работает своим размером, результат вернётся к исходнику
            </small>
          )}
        </span>
        {asSource && <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      </button>
      {aspects.length > 0 && (
        <>
          <Lab aside={state?.caps?.modelLabel}>Другие пропорции</Lab>
          <div data-fmt-hint="edit-aspects" style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>{FORMAT_TEXT.editAspectsHint}</div>
          <Aspects aspects={aspects} active={e.aspect} isMobile={isMobile} onPick={e.onAspect} />
        </>
      )}
      {e.aspect && e.how === null && <Ask e={e} isMobile={isMobile} />}
      {e.aspect && e.how === 'outpaint' && (
        <Notice tone="info" icon={Maximize2} data-fmt-note="outpaint">
          Края дорисует ИИ{e.source && e.outpaint.size ? `: ${e.source.width}×${e.source.height} → ` : ': '}<b>{e.outpaint.size ?? ''}</b>. {e.outpaint.detail}
        </Notice>
      )}
      {e.aspect && e.how === 'crop' && (
        <Notice tone="info" icon={Maximize2} data-fmt-note="crop">
          Обрежется без ИИ{e.crop.size ? ` до ${e.crop.size}` : ''} — новой версией.
          <div style={{ marginTop: SP.xs }}><Anchors value={e.anchor} onChange={e.onAnchor} isMobile={isMobile} /></div>
        </Notice>
      )}
    </>
  );
}

// ── Тело ──

export function FormatBody({ m, isMobile, projectName, onOpenExec }: {
  m: FormatModel; isMobile: boolean; projectName: string | null; onOpenExec?: () => void;
}) {
  const state = m.state;
  const wishExact = state?.wish?.mode === 'exact' || state?.resolved.mode === 'exact';
  const [exactOpen, setExactOpen] = useState(!!wishExact);
  useEffect(() => { if (wishExact) setExactOpen(true); }, [wishExact]);

  const head = (children: ReactNode) => (
    // Всплывашка ограничена по высоте окна: прокручивается только середина, подвал остаётся на месте
    <div data-fmt-body="" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px ${SP.md}px`, flex: '1 1 auto', minHeight: 0 }}>
      <div data-fmt-scroll="" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, flex: '1 1 auto', minHeight: 0, overflowY: 'auto' }}>{children}</div>
      <FormatFoot m={m} projectName={projectName} />
    </div>
  );

  if (m.kind === 'readonly') {
    return head(<Notice tone="info" icon={Info} data-fmt-note="readonly">{m.readonlyNote}</Notice>);
  }
  if (m.kind === 'edit' && m.edit) {
    return head(
      <>
        {m.needsChoice && m.hint && <Notice tone="info" icon={Info} data-fmt-note="needs-choice">{m.hint}</Notice>}
        <EditPanel e={m.edit} state={state} isMobile={isMobile} />
      </>,
    );
  }
  if (!state) return head(<div style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаем возможности модели…</div>);
  if (!state.caps) {
    return head(
      <>
        <div data-fmt-nocaps="" style={{ ...optBtn, cursor: 'default', border: `1px solid ${C.accent}`, background: C.accentLight }}>
          <span style={{ flex: 1, minWidth: 0 }}>
            <b style={{ color: C.textHeading }}>{FORMAT_TEXT.asModel}</b>
            {m.hint && <small style={{ display: 'block', color: C.textMuted, fontSize: FS.xs }}>{m.hint}</small>}
          </span>
        </div>
        <Notice tone="info" icon={Info} data-fmt-note="nocaps">
          {FORMAT_TEXT.noCapsMore}
          {onOpenExec && <div style={{ marginTop: SP.sm - 2 }}><Button size="xs" variant="secondary" onClick={onOpenExec}>{FORMAT_TEXT.changeModel}</Button></div>}
        </Notice>
      </>,
    );
  }

  const r = state.resolved;
  const exactActive = r.mode === 'exact';
  // Меняется одно из двух: второе вид возьмёт из желания, а не из ещё не обновлённого ответа ручки — быстрые клики не теряют друг друга
  const pickAspect = (a: string) => m.onChange({ mode: 'aspect', aspect: a });
  const pickTier = (t: FormatTierOption) => m.onChange({ mode: 'aspect', tier: t.tier });
  return head(
    <>
      {m.lockAsk && (
        <Notice tone="warning" icon={TriangleAlert} data-fmt-note="lock-ask">
          {m.lockAsk.text}
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.xs }}>
            <span data-fmt-lock="keep" style={{ display: 'inline-flex' }}><Button size="xs" variant="primary" onClick={m.lockAsk.onKeep}>{m.lockAsk.keepLabel}</Button></span>
            <span data-fmt-lock="force" style={{ display: 'inline-flex' }}><Button size="xs" variant="secondary" onClick={m.lockAsk.onForce}>{m.lockAsk.forceLabel}</Button></span>
          </div>
        </Notice>
      )}
      {m.locked && <Notice tone="info" icon={Info} data-fmt-note="locked">{m.locked.note ?? `Формат фильма — ${m.locked.aspect}.`}</Notice>}
      {m.mismatch && (
        <Notice tone="warning" icon={TriangleAlert} data-fmt-note="mismatch">
          {m.mismatch}
          {m.mismatchAction && <div style={{ marginTop: SP.xs }}><span data-fmt-fix="" style={{ display: 'inline-flex' }}><Button size="xs" variant="primary" onClick={m.mismatchAction.onClick}>{m.mismatchAction.label}</Button></span></div>}
        </Notice>
      )}
      {m.frames && <Frames f={m.frames} isMobile={isMobile} />}
      {r.substituted && r.note && <Notice tone="info" icon={Info} data-fmt-note="substituted">{r.note}</Notice>}
      <Lab aside={state.caps.modelLabel}>Пропорции</Lab>
      <div style={exactActive ? { opacity: 0.45 } : undefined}>
        <Aspects aspects={state.caps.aspects} active={exactActive ? null : r.aspect ?? null} isMobile={isMobile} onPick={pickAspect} />
      </div>
      {state.tiers.length > 0 && <Tiers state={state} onPick={pickTier} />}
      {state.exactUnavailableReason
        ? <div data-fmt-hint="exact" style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, display: 'flex', gap: SP.xs + 1 }}>
            <Info size={ICON_SIZE.xs - 1} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />{state.exactUnavailableReason}
          </div>
        : state.exact && (exactOpen
          ? <ExactPanel m={m} state={state} isMobile={isMobile} onClose={() => { setExactOpen(false); if (exactActive) pickAspect(r.aspect ?? state.caps?.aspects[0] ?? ''); }} />
          : <div><button type="button" data-fmt-exact-open="" onClick={() => setExactOpen(true)} style={linkStyle}>{FORMAT_TEXT.exactLink}</button></div>)}
    </>,
  );
}

export function FormatFoot({ m, projectName }: { m: FormatModel; projectName: string | null }) {
  const text = m.footer(projectName);
  if (!text) return null;
  return (
    <div data-fmt-foot="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm - 2, fontSize: FS.xs, color: C.textMuted, paddingTop: SP.xs }}>
      <Info size={ICON_SIZE.xs - 3} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      <span>{text}</span>
    </div>
  );
}
