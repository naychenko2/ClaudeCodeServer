// Карточки ленты чата картинки (ADR-018 §2, макет image-editor-v2, «.card» и «.sysline»):
// запуск генерации агентом (image_generate), предложенный промпт (image_suggest_prompt) и
// тихие строки «Вы запустили: …» и «Сохранено как …». Ядро рисует их через слот
// chat-item-tool; в редакторе кнопки действуют через ImageEditorBridge, в полном чате —
// открывают редактор.

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { AlertTriangle, Check, ExternalLink, Image as ImageIcon, SlidersHorizontal, Sparkles, X, Zap } from 'lucide-react';
import {
  Button, Dot, C, FS, R, SHADOW, SP, ICON_SIZE, ICON_STROKE, onReconnected, personaLabel, showToast,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ChatItem } from '../../../types';
import {
  imageEditorApi, type EditCost, type ImageChatStateChange, type ImageEditCatalog, type ImageEditEstimate, type ImageEditJob,
} from '../api';
import { priceSum, priceText, variantsWord } from '../format';
import { changedLine, describeChanges, modelLabel } from './stateSync';
import { useImageEditorBridge } from './bridge';
import { openImageChatById } from './openFromChat';

type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;

const ic = (I: typeof Zap) => <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

// ── Каталог проекта для подписей моделей: один запрос на вкладку ──

const catalogs = new Map<string, Promise<ImageEditCatalog | null>>();

function useCatalog(projectId: string | null): ImageEditCatalog | null {
  const [catalog, setCatalog] = useState<ImageEditCatalog | null>(null);
  useEffect(() => {
    if (!projectId) return;
    let alive = true;
    let p = catalogs.get(projectId);
    if (!p) {
      p = imageEditorApi().catalog(projectId).catch(() => { catalogs.delete(projectId); return null; });
      catalogs.set(projectId, p);
    }
    void p.then(c => { if (alive) setCatalog(c); });
    return () => { alive = false; };
  }, [projectId]);
  return catalog;
}

// ── Разбор вызова ──

function parseJson(text: string | undefined): Record<string, unknown> | null {
  if (!text) return null;
  try {
    const v = JSON.parse(text) as unknown;
    return v && typeof v === 'object' ? v as Record<string, unknown> : null;
  } catch { return null; }
}

function inputOf(item: ToolItem): Record<string, unknown> {
  return item.input && typeof item.input === 'object' ? item.input as Record<string, unknown> : {};
}

const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v : null);

interface LaunchResult {
  jobId: string;
  provider: string | null;
  model: string | null;
  estimate: ImageEditEstimate | null;
  expectedSeconds: number | null;
  changes: ImageChatStateChange[];
}

export function parseLaunchResult(text: string | undefined): LaunchResult | null {
  const r = parseJson(text);
  const jobId = str(r?.jobId);
  if (!r || !jobId) return null;
  const quote = (r.quote && typeof r.quote === 'object' ? r.quote : {}) as Record<string, unknown>;
  const est = quote.estimate && typeof quote.estimate === 'object' ? quote.estimate as ImageEditEstimate : null;
  const changes = Array.isArray(r.changes)
    ? (r.changes as unknown[]).filter((c): c is ImageChatStateChange => !!c && typeof c === 'object' && typeof (c as { field?: unknown }).field === 'string')
    : [];
  return {
    jobId, provider: str(quote.provider), model: str(quote.model), estimate: est,
    expectedSeconds: num(quote.expectedSeconds), changes,
  };
}

// ── Живой статус задачи: события image_edit_*, догон GET …/jobs/{id} ──

type LaunchPhase = 'run' | 'done' | 'cancel' | 'error' | 'lost';

interface LaunchStatus {
  phase: LaunchPhase;
  variants: number;
  charged: boolean | null;
  error: string | null;
  cost: EditCost | null;
  createdAt: number | null;
}

function statusOf(job: ImageEditJob): LaunchStatus {
  const base = { variants: job.variants.length, charged: job.charged ?? null, error: job.error ?? null, cost: job.cost ?? null, createdAt: Date.parse(job.createdAt) || null };
  switch (job.status) {
    case 'completed': return { ...base, phase: 'done' };
    case 'cancelled': return { ...base, phase: 'cancel' };
    case 'failed': return { ...base, phase: job.outcome === 'cancelled' ? 'cancel' : 'error' };
    case 'interrupted': return { ...base, phase: 'lost' };
    default: return { ...base, phase: 'run' };
  }
}

function useLaunchStatus(projectId: string | null, jobId: string | null) {
  const [status, setStatus] = useState<LaunchStatus | null>(null);
  const api = useMemo(() => imageEditorApi(), []);
  useEffect(() => {
    if (!projectId || !jobId) return;
    let alive = true;
    const load = () => api.getJob(projectId, jobId)
      .then(job => { if (alive) setStatus(statusOf(job)); })
      // Задачи нет: бэкенд перезапускался, а задачи живут в памяти
      .catch(() => { if (alive) setStatus(s => s ?? { phase: 'lost', variants: 0, charged: null, error: null, cost: null, createdAt: null }); });
    void load();
    const off = api.subscribe(e => {
      if (e.jobId !== jobId) return;
      if (e.type === 'image_edit_completed') {
        setStatus(s => ({ ...(s ?? EMPTY_STATUS), phase: 'done', variants: e.variants.length, cost: e.cost ?? null }));
      } else if (e.type === 'image_edit_failed') {
        setStatus(s => ({ ...(s ?? EMPTY_STATUS), phase: e.outcome === 'cancelled' ? 'cancel' : 'error', charged: e.charged ?? null, error: e.error ?? null }));
      }
    });
    const offRe = onReconnected(() => { void load(); });
    return () => { alive = false; off(); offRe(); };
  }, [api, projectId, jobId]);

  const cancel = async () => {
    if (!projectId || !jobId) return;
    try {
      const job = await api.cancelJob(projectId, jobId);
      setStatus(statusOf(job));
    } catch (e) {
      showToast(`Не удалось отменить: ${(e as Error).message}`, '', 'error');
    }
  };
  return { status, cancel };
}

const EMPTY_STATUS: LaunchStatus = { phase: 'run', variants: 0, charged: null, error: null, cost: null, createdAt: null };

// Проценты от ожидаемой длительности: поставщики процентов не присылают
function useProgress(running: boolean, startedAt: number | null, expectedSeconds: number | null): number {
  const [mounted] = useState(() => Date.now());
  const [now, setNow] = useState(mounted);
  useEffect(() => {
    if (!running) return;
    const t = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(t);
  }, [running]);
  const expected = Math.max(5, expectedSeconds ?? 30) * 1000;
  return Math.max(0, Math.min(95, ((now - (startedAt ?? mounted)) / expected) * 90));
}

// ── Общий вид ──

function Card({ tone, children }: { tone?: 'ok' | 'off'; children: ReactNode }) {
  return (
    <div data-image-card={tone ?? 'run'} style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, maxWidth: 520,
      border: `1px solid ${tone === 'ok' ? C.success : C.border}`, borderRadius: R.xl,
      background: C.bgCard, boxShadow: SHADOW.card, opacity: tone === 'off' ? 0.8 : 1,
    }}>
      {children}
    </div>
  );
}

function CardHead({ icon, title, meta, color }: { icon?: ReactNode; title: string; meta?: string | null; color?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm, fontWeight: 600, color: color ?? C.textHeading }}>
      {icon && <span style={{ display: 'inline-flex' }}>{icon}</span>}
      <span>{title}</span>
      {meta && <span style={{ fontWeight: 400, color: C.textSecondary }}>· {meta}</span>}
    </div>
  );
}

function Note({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{children}</div>;
}

function Acts({ children }: { children: ReactNode }) {
  return <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>{children}</div>;
}

// ── Запуск генерации агентом ──

export function ImageLaunchCard({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  const bridge = useImageEditorBridge(ctx.sessionId);
  const catalog = useCatalog(ctx.projectId);
  const input = inputOf(item);
  const result = item.isError ? null : parseLaunchResult(item.result);
  const { status, cancel } = useLaunchStatus(ctx.projectId, result?.jobId ?? null);
  const running = !!result && (!status || status.phase === 'run');
  const progress = useProgress(running, status?.createdAt ?? null, result?.expectedSeconds ?? null);

  const count = num(input.count) ?? num(result?.changes.find(c => c.field === 'count')?.to)
    ?? (status?.phase === 'done' && status.variants ? status.variants : null);

  // Задача ещё идёт — открытый редактор этого чата показывает её в центре
  const tracked = useRef(false);
  useEffect(() => {
    if (!bridge || !result || tracked.current || status?.phase !== 'run') return;
    tracked.current = true;
    bridge.trackJob(result.jobId, count ?? 1, result.expectedSeconds);
  }, [bridge, result, status?.phase, count]);

  if (item.result === undefined) {
    return <Card><CardHead icon={<Dot color={C.accent} />} title="Запускаю генерацию…" /></Card>;
  }
  if (!result) {
    const err = parseJson(item.result);
    return (
      <Card tone="off">
        <CardHead icon={ic(AlertTriangle)} title="Генерация не запущена" color={C.dangerText} />
        <Note>{str(err?.error) ?? item.result}</Note>
      </Card>
    );
  }

  const est = result.estimate;
  const price = est ? priceSum(est.amount, est.unit, est.approx) : null;
  const meta = [price, count ? variantsWord(count) : null].filter(Boolean).join(' · ');
  const changed = changedLine(describeChanges(result.changes, catalog), !!ctx.persona);
  const phase = status?.phase ?? 'run';
  const openInEditor = (withJob: boolean) => {
    if (!ctx.sessionId) return;
    void openImageChatById(ctx.sessionId, withJob ? { job: { jobId: result.jobId, count: count ?? 1 } } : undefined);
  };
  const showVariants = () => {
    if (bridge) bridge.showJob(result.jobId, count ?? status?.variants ?? 1, result.expectedSeconds);
    else openInEditor(true);
  };
  const noMoney = status?.charged === true ? 'Поставщик уже списал оплату.' : 'Деньги не списаны.';

  const title = {
    run: 'Запущена генерация', done: 'Готово', cancel: 'Генерация отменена', error: 'Генерация не удалась', lost: 'Генерация прервалась',
  }[phase];
  const icon = phase === 'run' ? <Dot color={C.accent} /> : phase === 'done' ? ic(Check) : phase === 'cancel' ? ic(X) : ic(AlertTriangle);

  return (
    <Card tone={phase === 'done' ? 'ok' : phase === 'run' ? undefined : 'off'}>
      <CardHead icon={icon} title={title} meta={meta || null} color={phase === 'done' ? C.successText : undefined} />
      {changed && (
        <div data-image-changed="" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textSecondary }}>
          {ic(SlidersHorizontal)}<span>{changed}</span>
        </div>
      )}
      {phase === 'run' && (
        <div style={{ height: 4, borderRadius: R.max, background: C.track, overflow: 'hidden' }}>
          <div style={{ width: `${progress}%`, height: '100%', background: C.accent, transition: 'width .5s linear' }} />
        </div>
      )}
      {phase === 'cancel' && <Note>{noMoney}{bridge ? ' Промпт остался в поле сверху.' : ''}</Note>}
      {phase === 'error' && <Note>{status?.error ?? 'Сервис рисования отказал.'}{status?.charged === false ? ' Деньги не списаны.' : ''}</Note>}
      {phase === 'lost' && <Note>Сервер перезапускался, задача не сохранилась. Проверьте траты в «Модели и расход».</Note>}
      <Acts>
        {phase === 'run' && <Button size="sm" variant="secondary" leftIcon={ic(X)} onClick={() => { void cancel(); }}>Отменить</Button>}
        {phase === 'done' && <Button size="sm" variant="primary" onClick={showVariants}>Показать варианты</Button>}
        {!bridge && phase !== 'done' && ctx.sessionId && (
          <Button size="sm" variant="ghost" leftIcon={ic(ExternalLink)} onClick={() => openInEditor(phase === 'run')}>Открыть в редакторе</Button>
        )}
      </Acts>
    </Card>
  );
}

// ── Предложенный промпт ──

export function ImagePromptCard({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  const bridge = useImageEditorBridge(ctx.sessionId);
  const input = inputOf(item);
  const prompt = str(input.prompt);
  const count = num(input.count);
  const [inserted, setInserted] = useState(false);

  if (!prompt) {
    return item.result === undefined
      ? <Card><CardHead icon={ic(Sparkles)} title="Готовлю промпт…" /></Card>
      : null;
  }
  return (
    <Card>
      <CardHead icon={ic(Sparkles)} title="Промпт" meta={count ? variantsWord(count) : null} />
      <div data-image-prompt="" style={{
        fontSize: FS.base, lineHeight: 1.5, background: C.bgInset, borderRadius: R.md, padding: SP.sm,
        color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere',
      }}>
        {prompt}
      </div>
      <Acts>
        {bridge ? (
          <>
            <Button size="sm" variant="secondary" disabled={bridge.busy} leftIcon={inserted ? ic(Check) : undefined}
              onClick={() => { bridge.insertPrompt(prompt, { count }); setInserted(true); }}>
              {inserted ? 'Вставлено' : 'Вставить в промпт'}
            </Button>
            <Button size="sm" variant="primary" disabled={bridge.busy} leftIcon={ic(Sparkles)}
              onClick={() => { bridge.generate(prompt, { count }); setInserted(true); }}>
              {bridge.priceSum ? `Сгенерировать · ${bridge.priceSum}` : 'Сгенерировать'}
            </Button>
          </>
        ) : ctx.sessionId && (
          <Button size="sm" variant="secondary" leftIcon={ic(ExternalLink)}
            onClick={() => { void openImageChatById(ctx.sessionId!, { prompt }); }}>
            Открыть в редакторе с этим промптом
          </Button>
        )}
      </Acts>
    </Card>
  );
}

// ── Тихие строки ──

function SysLine({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <div data-image-sysline="" style={{
      alignSelf: 'center', maxWidth: 420, margin: '0 auto', textAlign: 'center', overflowWrap: 'anywhere',
      fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45,
    }}>
      <span style={{ display: 'inline-flex', verticalAlign: '-2px', marginRight: SP.xs }}>{icon}</span>{children}
    </div>
  );
}

// «Вы запустили: «вечер» · FLUX Fill · ≈ $0.10 · 2 варианта»
export function ImageLaunchRow({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as Extract<ChatItem, { kind: 'image_launch' }>;
  const catalog = useCatalog(ctx.projectId);
  const model = modelLabel(catalog, item.provider, item.model);
  const est = item.estimate;
  const price = est ? priceText(est.amount, est.unit, est.approx, item.count) : variantsWord(item.count);
  const who = item.by === 'agent'
    ? `${ctx.persona ? personaLabel(ctx.persona) : 'Claude'} ${ctx.persona ? '— запуск' : 'запустил'}`
    : 'Вы запустили';
  const what = item.prompt.trim() ? `«${item.prompt.trim()}»` : 'генерацию';
  return <SysLine icon={ic(Zap)}>{who}: {what} · {model} · {price}</SysLine>;
}

export function ImageFileMovedRow({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as Extract<ChatItem, { kind: 'image_file_moved' }>;
  return <SysLine icon={ic(ImageIcon)}>Сохранено как {item.to}. Редактор перешёл на этот файл, чат — вместе с ним</SysLine>;
}
