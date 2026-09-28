// Карточки ленты (слот chat-item-tool): запуск генерации агентом (image_generate) и
// предложенный промпт (image_suggest_prompt) в чате проекта (ADR-019 §4), а также тихие
// строки «Вы запустили: …» и «Сохранено как …» из истории чатов картинки v2 — старые
// чаты уходят в архив, но открываются и должны читаться.

import { useState, type ReactNode } from 'react';
import { AlertTriangle, Check, ExternalLink, Image as ImageIcon, SlidersHorizontal, Sparkles, X, Zap } from 'lucide-react';
import {
  Button, Dot, ProgressBar, C, FS, R, SHADOW, SP, ICON_SIZE, ICON_STROKE, personaLabel,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ChatItem } from '../../../types';
import { AUTO_MODEL, type ImageEditCatalog, type ImageEditEstimate } from '../api';
import { useCatalog } from '../thread/catalog';
import { effectiveProvider, isFreeUnit, priceSum, priceText, variantsWord } from '../format';
import { launchOf, launchVersions } from '../thread/model';
import { getFocusedThread, openEditor, useThreads } from '../thread/threadStore';
import { useJobStatus, useProgress } from '../thread/useJobStatus';
import { launchThread, useThreadLaunch } from '../thread/useThreadLaunch';

type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;

const ic = (I: typeof Zap) => <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

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

// Изменение настроек запуска агентом: field — имя настройки, from/to — значения
export interface LaunchChange { field: string; from?: unknown; to?: unknown }

// Подписи поставщика и модели по каталогу; каталога нет — сырые ключи
function providerLabel(catalog: ImageEditCatalog | null, key: string | null | undefined): string {
  if (!catalog) return key || 'как в настройках';
  return effectiveProvider(catalog, key || 'settings')?.label ?? key ?? '';
}

export function modelLabel(catalog: ImageEditCatalog | null, provider: string | null | undefined, model: string | null | undefined): string {
  const id = model || AUTO_MODEL;
  const pv = catalog ? effectiveProvider(catalog, provider || 'settings') : null;
  const found = pv?.models.find(m => m.id === id);
  if (found) return found.label;
  return id === AUTO_MODEL ? 'Авто' : id;
}

// «промпт, модель → FLUX Fill, вариантов: 2» из changes[] результата image_generate
export function describeChanges(changes: LaunchChange[], catalog: ImageEditCatalog | null): string[] {
  const byField = new Map(changes.map(c => [c.field, c]));
  const parts: string[] = [];
  if (byField.has('prompt')) parts.push('промпт');
  const pv = byField.get('provider');
  if (pv && providerLabel(catalog, str(pv.from)) !== providerLabel(catalog, str(pv.to))) {
    parts.push(`поставщик → ${providerLabel(catalog, str(pv.to))}`);
  }
  const md = byField.get('model');
  if (md) parts.push(`модель → ${modelLabel(catalog, str(pv?.to), str(md.to))}`);
  const ct = byField.get('count');
  if (ct && typeof ct.to === 'number') parts.push(`вариантов: ${ct.to}`);
  const size = byField.get('matchSourceSize');
  if (size) parts.push(size.to ? 'вернуть размер оригинала' : 'без возврата размера');
  return parts;
}

// Строка карточки: «Изменил: …» от лица Claude, у персоны — безличное «Изменено: …»
// (рода персоны продукт не знает)
export function changedLine(parts: string[], persona: boolean): string | null {
  if (!parts.length) return null;
  return `${persona ? 'Изменено' : 'Изменил'}: ${parts.join(', ')}`;
}

interface LaunchResult {
  jobId: string;
  provider: string | null;
  model: string | null;
  estimate: ImageEditEstimate | null;
  expectedSeconds: number | null;
  changes: LaunchChange[];
}

export function parseLaunchResult(text: string | undefined): LaunchResult | null {
  const r = parseJson(text);
  const jobId = str(r?.jobId);
  if (!r || !jobId) return null;
  const quote = (r.quote && typeof r.quote === 'object' ? r.quote : {}) as Record<string, unknown>;
  const est = quote.estimate && typeof quote.estimate === 'object' ? quote.estimate as ImageEditEstimate : null;
  const changes = Array.isArray(r.changes)
    ? (r.changes as unknown[]).filter((c): c is LaunchChange => !!c && typeof c === 'object' && typeof (c as { field?: unknown }).field === 'string')
    : [];
  return {
    jobId, provider: str(quote.provider), model: str(quote.model), estimate: est,
    expectedSeconds: num(quote.expectedSeconds), changes,
  };
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
  const catalog = useCatalog(ctx.projectId);
  const input = inputOf(item);
  // threadId в image_generate обязателен (ADR-019, решение 1): по нему карточка ведёт в попап нити
  const threadId = str(input.threadId);
  const result = item.isError ? null : parseLaunchResult(item.result);
  // Запуск нити с версиями (v3) целиком рисует якорь image_launch_versions: строка
  // запуска, прогресс, «Отменить», версии. Своя карточка тут — второй блок на запуск
  const threads = useThreads(ctx.projectId, ctx.sessionId);
  const thread = threadId ? threads.threads.find(t => t.id === threadId) : undefined;
  const owned = !!result && !!thread && (!!launchOf(thread, result.jobId) || launchVersions(thread, result.jobId).length > 0);
  const { status, cancel } = useJobStatus(ctx.projectId ?? '', owned || !ctx.projectId ? null : result?.jobId ?? null);
  const running = !!result && (!status || status.phase === 'run');
  const progress = useProgress(status, running, result?.expectedSeconds ?? null);

  const count = num(input.count) ?? num(result?.changes.find(c => c.field === 'count')?.to)
    ?? (status?.phase === 'done' && status.count ? status.count : null);

  if (owned) return null;
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
  // У локальных моделей вместо «≈ $» — «Бесплатно», время и очередь на момент запуска
  const free = !!est && isFreeUnit(est.unit);
  const price = est ? priceSum(est.amount, est.unit, est.approx, est) : null;
  const meta = [price, count ? variantsWord(count) : null].filter(Boolean).join(' · ');
  const changed = changedLine(describeChanges(result.changes, catalog), !!ctx.persona);
  const phase = status?.phase ?? 'run';
  // Варианты и шаги живут в попапе «Редактор» нити
  const openInEditor = ctx.sessionId && threadId ? () => openEditor(ctx.sessionId!, threadId) : null;
  const noMoney = free ? '' : status?.charged === true ? 'Поставщик уже списал оплату.' : 'Деньги не списаны.';

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
        <>
          <ProgressBar value={progress.percent} transition="width .5s linear" />
          {progress.queuePosition && <Note>В очереди {progress.queuePosition}</Note>}
        </>
      )}
      {phase === 'cancel' && noMoney && <Note>{noMoney}</Note>}
      {phase === 'error' && <Note>{status?.error ?? 'Сервис рисования отказал.'}{status?.charged === false && !free ? ' Деньги не списаны.' : ''}</Note>}
      {phase === 'lost' && <Note>Сервер перезапускался, задача не сохранилась. Проверьте траты в «Модели и расход».</Note>}
      <Acts>
        {phase === 'run' && <Button size="sm" variant="secondary" leftIcon={ic(X)} onClick={() => { void cancel(); }}>Отменить</Button>}
        {phase === 'done' && openInEditor && <Button size="sm" variant="primary" onClick={openInEditor}>Показать варианты</Button>}
        {phase !== 'done' && openInEditor && (
          <Button size="sm" variant="ghost" leftIcon={ic(ExternalLink)} onClick={openInEditor}>Открыть в редакторе</Button>
        )}
      </Acts>
    </Card>
  );
}

// ── Предложенный промпт ──

export function ImagePromptCard({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  const input = inputOf(item);
  const prompt = str(input.prompt);
  const count = num(input.count);
  // Промпт уходит в выбранную картинку чата: без выбора запускать не во что
  useThreads(ctx.projectId, ctx.sessionId);
  const thread = getFocusedThread(ctx.sessionId);
  const L = useThreadLaunch(ctx.projectId ?? '', ctx.sessionId, thread);
  const [busy, setBusy] = useState(false);
  const [launched, setLaunched] = useState(false);

  if (!prompt) {
    return item.result === undefined
      ? <Card><CardHead icon={ic(Sparkles)} title="Готовлю промпт…" /></Card>
      : null;
  }
  const generate = async () => {
    if (!thread || !ctx.projectId || !ctx.sessionId) return;
    setBusy(true);
    const ok = await launchThread(ctx.projectId, ctx.sessionId, thread, { kind: 'prompt', prompt });
    setBusy(false);
    if (ok) setLaunched(true);
  };
  return (
    <Card>
      <CardHead icon={ic(Sparkles)} title="Промпт" meta={count ? variantsWord(count) : null} />
      <div data-image-prompt="" style={{
        fontSize: FS.base, lineHeight: 1.5, background: C.bgInset, borderRadius: R.md, padding: SP.sm,
        color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere',
      }}>
        {prompt}
      </div>
      {thread ? (
        <Acts>
          <Button size="sm" variant="primary" loading={busy} disabled={busy || launched}
            leftIcon={launched ? ic(Check) : ic(Sparkles)} onClick={() => { void generate(); }}>
            {launched ? 'Запущено' : L.price ? `Сгенерировать · ${L.price}` : 'Сгенерировать'}
          </Button>
        </Acts>
      ) : (
        <Note>Выберите картинку в ленте, чтобы запустить этот промпт.</Note>
      )}
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
  // Строка — след в истории: время и очередь на момент запуска в неё не пишутся
  const price = !est ? variantsWord(item.count)
    : isFreeUnit(est.unit) ? `Бесплатно · ${variantsWord(item.count)}` : priceText(est.amount, est.unit, est.approx, item.count);
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
