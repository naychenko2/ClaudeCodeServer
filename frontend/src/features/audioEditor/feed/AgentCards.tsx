// Карточки ленты для вызовов агентом инструментов audio_* (слот chat-item-tool, по образцу
// карточек image_* редактора картинок): выбор звука — тихой строкой с переходом к нити,
// запуск — карточкой со статусом задачи из стора нитей, предложенный текст — с кнопками
// «Вставить в промпт» и «Сгенерировать», служебные вызовы — короткой строкой без JSON.

import { useState, type ReactNode } from 'react';
import {
  AlertTriangle, AudioLines, Check, Eye, ListMusic, Mic, PencilLine, Plus, Scissors, Sparkles, Square, X,
} from 'lucide-react';
import { Button, Dot, C, FS, R, SHADOW, SP, ICON_SIZE, ICON_STROKE, personaLabel, showToast } from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ChatItem, Persona } from '../../../types';
import { audioApi, type AudioCatalog, type AudioThread } from '../api';
import { MODE_LABEL } from '../ops';
import { audioScope } from '../scope';
import { launchFromComposer } from '../thread/actions';
import { soundSource } from '../thread/modeState';
import {
  focusThread, getCatalog, getFocusedThread, getJobsOf, mutate, suggestPrompt, useAudioThreads,
  type JobProgress,
} from '../thread/threadStore';
import { promptLaunch, promptRunLabel } from './promptLaunch';
import { launchOf, launchVersions } from '../thread/model';
import {
  asMode, cancelLine, opTitle, parseDenial, parseFocus, parseLaunch, parseReady, priceText, stateLine, str, voicesLine,
  type Denial, type LaunchView,
} from './parse';

type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;

const ic = (I: typeof Check) => <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

const inputOf = (item: ToolItem): Record<string, unknown> =>
  item.input && typeof item.input === 'object' ? item.input as Record<string, unknown> : {};

// «Claude» или имя персоны чата
const who = (persona: Persona | null) => (persona ? personaLabel(persona) : 'Claude');

// ── Общий вид ──

function Card({ tone, kind, children }: { tone?: 'ok' | 'off'; kind: string; children: ReactNode }) {
  return (
    <div data-audio-card={kind} data-audio-tone={tone ?? 'run'} style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, maxWidth: 520,
      border: `1px solid ${tone === 'ok' ? C.success : C.border}`, borderRadius: R.xl,
      background: C.bgCard, boxShadow: SHADOW.card, opacity: tone === 'off' ? 0.85 : 1,
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
      {meta && <span style={{ fontWeight: 400, color: C.textSecondary, overflowWrap: 'anywhere' }}>· {meta}</span>}
    </div>
  );
}

function Note({ children }: { children: ReactNode }) {
  return <div data-audio-note="" style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{children}</div>;
}

function Acts({ children }: { children: ReactNode }) {
  return <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>{children}</div>;
}

function SysLine({ kind, icon, children }: { kind: string; icon: ReactNode; children: ReactNode }) {
  return (
    <div data-audio-sysline={kind} style={{
      alignSelf: 'center', maxWidth: 460, margin: '0 auto', display: 'flex', flexWrap: 'wrap', alignItems: 'center',
      justifyContent: 'center', columnGap: SP.xs, rowGap: 2, textAlign: 'center', overflowWrap: 'anywhere',
      fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45,
    }}>
      <span style={{ display: 'inline-flex' }}>{icon}</span>{children}
    </div>
  );
}

// Отказ вызова: заголовок по виду, текст — человеку, а не агенту
const DENIAL_TITLE: Record<Denial['kind'], string> = {
  turnLimit: 'Лимит запусков за ход',
  delegated: 'Запуск недоступен на чужом ходу',
  report: 'Запуск недоступен на этом ходу',
  unchecked: 'Запуск не проверен',
  error: 'Не запущено',
};

function DeniedCard({ kind, result, title }: { kind: string; result: string | undefined; title?: string }) {
  const d = parseDenial(result);
  return (
    <Card kind={kind} tone="off">
      <CardHead icon={ic(AlertTriangle)} title={d.kind === 'error' && title ? title : DENIAL_TITLE[d.kind]} color={C.dangerText} />
      <Note>{d.text}</Note>
    </Card>
  );
}

// ── Подписи каталога ──

function providerLabel(catalog: AudioCatalog | null, key: string | null): string | null {
  if (!key) return null;
  return catalog?.providers.find(p => p.key === key)?.label ?? key;
}

function modelLabel(catalog: AudioCatalog | null, provider: string | null, model: string | null): string | null {
  if (!model) return null;
  if (model === (catalog?.autoModelId ?? 'auto')) return 'Авто';
  const pv = catalog?.providers.find(p => p.key === provider);
  return pv?.models.find(m => m.id === model)?.label ?? model;
}

// «Локально · Qwen3-TTS 1.7B · 2 вар. · бесплатно»
export function launchMeta(L: LaunchView, catalog: AudioCatalog | null): string {
  return [
    providerLabel(catalog, L.provider), modelLabel(catalog, L.provider, L.model),
    L.count && L.count > 1 ? `${L.count} вар.` : null, priceText(L.price),
  ].filter(Boolean).join(' · ');
}

// ── Статус запуска из стора нити ──

// Запуск, который нить уже знает, целиком рисует якорь audio_launch_versions карточки нити:
// строка запуска, прогресс, «Отменить», версии. Своя карточка тут — второй блок на запуск
export const launchOwned = (thread: AudioThread | undefined, jobId: string): boolean =>
  !!thread && (!!launchOf(thread, jobId) || launchVersions(thread, jobId).length > 0);

// Своей карточке остаются два вида: нить ещё не знает запуск — идёт; нити в чате нет — 'gone'.
// Итог запуска (готово, сбой, отмена, обрыв) пишется в нить, и запуск сразу уходит якорю
export type LaunchPhase = 'run' | 'gone';

// Готовая версия без запуска (монтаж без ИИ, склейка): её целиком рисует якорь audio_thread с карточкой этой
// версии — как у запуска человеком. Своя карточка тут — второй блок на то же действие
export const versionOwned = (thread: AudioThread | undefined, versionId: string): boolean =>
  !!thread && thread.versions.some(v => v.id === versionId);

export const launchPhase = (thread: AudioThread | undefined, loaded: boolean): LaunchPhase =>
  !thread && loaded ? 'gone' : 'run';

// «GPU: 2-я в очереди», «вариант 1 из 2 · ещё ≈ 40 с»
export function progressText(p: JobProgress | undefined): string | null {
  if (!p) return null;
  const eta = p.etaSeconds != null ? (p.etaSeconds < 60 ? `${p.etaSeconds} с` : `${Math.round(p.etaSeconds / 60)} мин`) : null;
  if (p.stage === 'queued') {
    const pos = p.queuePosition && p.queuePosition > 0 ? `GPU: ${p.queuePosition}-я в очереди` : 'в очереди';
    return eta ? `${pos} · старт ≈ через ${eta}` : pos;
  }
  if (p.stage === 'downloading') return 'забираем результат';
  const of = p.count > 1 ? `вариант ${p.variant} из ${p.count}` : 'идёт';
  return eta ? `${of} · ещё ≈ ${eta}` : of;
}

// Выбрать версию: она станет текущей, звук — в работе (полоса «Звук», без панели)
async function pickVersion(scope: string, sessionId: string, threadId: string, versionId: string) {
  const ok = await mutate(scope, sessionId, rev => audioApi.current(scope, sessionId, threadId, versionId, rev));
  if (ok && getFocusedThread(sessionId)?.id !== threadId) await focusThread(scope, sessionId, threadId);
}

// ── audio_generate ──

export function AudioLaunchCard({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  const input = inputOf(item);
  const scope = audioScope(ctx.projectId);
  const state = useAudioThreads(scope, ctx.sessionId);
  const catalog = getCatalog(scope);
  const launch = item.isError ? null : parseLaunch(item.result);
  const ready = item.isError || launch ? null : parseReady(item.result);
  const threadId = launch?.threadId ?? ready?.threadId ?? str(input.threadId);
  const thread = threadId ? state.threads.find(t => t.id === threadId) : undefined;
  const loaded = state.revision > 0 || state.threads.length > 0;
  const [cancelling, setCancelling] = useState(false);

  const title = opTitle(launch?.op ?? (str(input.op) as never), asMode(input.mode));
  if (launch && launchOwned(thread, launch.jobId)) return null;
  if (item.result === undefined) {
    return <Card kind="launch"><CardHead icon={<Dot color={C.accent} />} title="Запускаю операцию со звуком…" meta={title} /></Card>;
  }
  if (item.isError || (!launch && !ready)) return <DeniedCard kind="launch" result={item.result} title="Операция не запущена" />;

  // Монтаж без ИИ: версия готова сразу
  if (ready) {
    if (versionOwned(thread, ready.versionId)) return null;
    return (
      <Card kind="edit" tone="ok">
        <CardHead icon={ic(Scissors)} title={`Готово: ${title}`} meta="монтаж без ИИ · бесплатно" color={C.successText} />
        {thread && ctx.sessionId && (
          <Acts>
            <Button size="sm" variant="ghost" leftIcon={ic(AudioLines)}
              onClick={() => { void pickVersion(scope, ctx.sessionId!, thread.id, ready.versionId); }}>
              Показать версию
            </Button>
          </Acts>
        )}
      </Card>
    );
  }

  const L = launch!;
  const phase = launchPhase(thread, loaded);
  const meta = launchMeta(L, catalog);
  const progress = progressText(getJobsOf(ctx.sessionId, threadId).find(j => j.jobId === L.jobId));
  const icon = phase === 'run' ? <Dot color={C.accent} /> : ic(AudioLines);
  const cancel = async () => {
    setCancelling(true);
    try { await audioApi.cancelJob(scope, ctx.sessionId, L.jobId); }
    catch (e) { showToast((e as Error).message || 'Не удалось отменить', '', 'error'); }
    finally { setCancelling(false); }
  };

  return (
    <Card kind="launch" tone={phase === 'run' ? undefined : 'off'}>
      <CardHead icon={icon} title={`${who(ctx.persona)} — запуск: ${title}`} meta={meta || null} />
      {L.baseLabel && <Note>От: {L.baseLabel}</Note>}
      {phase === 'run' && <Note>{progress ?? 'идёт'}</Note>}
      {phase === 'gone' && <Note>Этого звука в чате уже нет.</Note>}
      {phase === 'run' && (
        <Acts>
          <Button size="sm" variant="secondary" leftIcon={ic(Square)} loading={cancelling} disabled={cancelling}
            onClick={() => { void cancel(); }}>
            Отменить
          </Button>
        </Acts>
      )}
    </Card>
  );
}

// ── audio_concat ──

export function AudioConcatCard({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  const input = inputOf(item);
  const scope = audioScope(ctx.projectId);
  const state = useAudioThreads(scope, ctx.sessionId);
  const pieces = Array.isArray(input.pieces) ? input.pieces.length : 0;
  const ready = item.isError ? null : parseReady(item.result);
  if (item.result === undefined) {
    return <Card kind="concat"><CardHead icon={<Dot color={C.accent} />} title="Склеиваю куски…" meta={pieces ? `кусков: ${pieces}` : null} /></Card>;
  }
  if (!ready) return <DeniedCard kind="concat" result={item.result} title="Склейка не выполнена" />;
  const thread = state.threads.find(t => t.id === ready.threadId);
  if (versionOwned(thread, ready.versionId)) return null;
  return (
    <Card kind="concat" tone="ok">
      <CardHead icon={ic(ListMusic)} title={`Склеено: ${ready.name ?? 'новый звук'}`}
        meta={[pieces ? `кусков: ${pieces}` : null, 'без ИИ · бесплатно'].filter(Boolean).join(' · ')} color={C.successText} />
      {thread && ctx.sessionId && (
        <Acts>
          <Button size="sm" variant="ghost" leftIcon={ic(AudioLines)}
            onClick={() => { void pickVersion(scope, ctx.sessionId!, thread.id, ready.versionId); }}>
            Показать звук
          </Button>
        </Acts>
      )}
    </Card>
  );
}

// ── audio_suggest_prompt ──

export function AudioPromptCard({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  const input = inputOf(item);
  const prompt = str(input.prompt);
  const mode = asMode(input.mode);
  const scope = audioScope(ctx.projectId);
  useAudioThreads(scope, ctx.sessionId);
  const thread = getFocusedThread(ctx.sessionId);
  // Запуск — по режиму и модели карточки, если агент их назвал, иначе по полосе; цена — того, что пойдёт
  const src = thread ? soundSource(scope, ctx.sessionId, thread) : null;
  const planned = src
    ? promptLaunch(src.thread, src.prefs, getCatalog(scope), src.mode, { mode, model: str(input.model) })
    : null;
  const [busy, setBusy] = useState(false);
  const [launched, setLaunched] = useState(false);

  if (!prompt) {
    return item.result === undefined ? <Card kind="prompt"><CardHead icon={ic(Sparkles)} title="Готовлю текст…" /></Card> : null;
  }
  const generate = async () => {
    if (!thread || !ctx.sessionId) return;
    setBusy(true);
    const ok = await launchFromComposer(scope, ctx.sessionId, thread, prompt, planned?.override);
    setBusy(false);
    if (ok) setLaunched(true);
  };
  const meta = [mode ? MODE_LABEL[mode] : null, str(input.model)].filter(Boolean).join(' · ');
  return (
    <Card kind="prompt">
      <CardHead icon={ic(Sparkles)} title="Текст для звука" meta={meta || null} />
      <div data-audio-prompt="" style={{
        fontSize: FS.base, lineHeight: 1.5, background: C.bgInset, borderRadius: R.md, padding: SP.sm,
        color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere',
      }}>
        {prompt}
      </div>
      {thread && ctx.sessionId ? (
        <Acts>
          <Button size="sm" variant="ghost" leftIcon={ic(PencilLine)} onClick={() => suggestPrompt(ctx.sessionId!, prompt)}>
            Вставить в промпт
          </Button>
          <Button size="sm" variant="primary" loading={busy} disabled={busy || launched}
            leftIcon={launched ? ic(Check) : ic(Sparkles)} onClick={() => { void generate(); }}>
            {launched ? 'Запущено' : planned ? promptRunLabel(planned) : 'Сгенерировать'}
          </Button>
        </Acts>
      ) : (
        <Note>Выберите звук в полосе «Звук», чтобы запустить этот текст.</Note>
      )}
    </Card>
  );
}

// ── audio_focus и audio_new ──

export function AudioFocusLine({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  const isNew = item.name.endsWith('audio_new');
  const scope = audioScope(ctx.projectId);
  if (item.result === undefined) return null;
  if (item.isError) {
    return <SysLine kind="focus-error" icon={ic(AlertTriangle)}>{who(ctx.persona)}: звук не выбран — {parseDenial(item.result).text}</SysLine>;
  }
  const f = parseFocus(item.result);
  if (!f) return null;
  if (!f.threadId) return <SysLine kind="unfocus" icon={ic(X)}>{who(ctx.persona)} снял выбор звука</SysLine>;
  const label = [f.name, f.version].filter(Boolean).join(' · ');
  const mode = asMode(inputOf(item).mode);
  const lead = isNew
    ? `${ctx.persona ? `${who(ctx.persona)} — новый звук` : 'Claude завёл новый звук'}${mode ? ` (${MODE_LABEL[mode].toLowerCase()})` : ''}`
    : ctx.persona ? `${who(ctx.persona)} — в работе` : 'Claude взял в работу';
  const pick = ctx.sessionId ? () => { void focusThread(scope, ctx.sessionId!, f.threadId); } : undefined;
  return (
    <SysLine kind={isNew ? 'new' : 'focus'} icon={ic(isNew ? Plus : AudioLines)}>
      <span>{lead}:</span>
      {pick
        ? <Button size="xs" variant="ghost" onClick={pick}>{label}</Button>
        : label}
    </SysLine>
  );
}

// ── audio_state, audio_voices, audio_cancel ──

export function AudioServiceLine({ ctx }: { ctx: ChatItemToolCtx }) {
  const item = ctx.item as ToolItem;
  if (item.result === undefined) return null;
  const name = item.name.replace(/^mcp__audio-editor__/, '');
  if (item.isError) {
    const what = name === 'audio_cancel' ? 'отменить операцию' : name === 'audio_voices' ? 'получить дикторов' : 'посмотреть звуки';
    return <SysLine kind={`${name}-error`} icon={ic(AlertTriangle)}>Не удалось {what}: {parseDenial(item.result).text}</SysLine>;
  }
  if (name === 'audio_cancel') return <SysLine kind="cancel" icon={ic(Square)}>{cancelLine(item.result)}</SysLine>;
  if (name === 'audio_voices') return <SysLine kind="voices" icon={ic(Mic)}>{voicesLine(inputOf(item), item.result)}</SysLine>;
  return <SysLine kind="state" icon={ic(Eye)}>{stateLine(item.result)}</SysLine>;
}
