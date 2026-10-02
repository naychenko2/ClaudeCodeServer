// Карточки звука в ленте: каждая версия нити — своя полноценная карточка на своём месте. В
// history.json лежат только якоря module_record: audio_thread { threadId, versionId } — карточка
// версии (исходник, склейка, правка без ИИ), и audio_launch_versions { threadId, jobId, … } — запуск
// ИИ: карточка хода, по готовности — карточка на каждый вариант. Всё содержимое — живьём из стора
// нитей. Всё за флагом audio-editor: без него — строка fallback.

import { useState, type ReactNode } from 'react';
import { AudioLines, ChevronDown, Combine, Download, Mic, Music, Save, Scissors, Sparkles, Target, Wand2, X } from 'lucide-react';
import {
  Badge, Button, Dot, Menu, MenuItem, MenuSep, ProgressBar, C, FS, R, SHADOW, SP, ICON_SIZE, ICON_STROKE, FLAGS, isCardPick, useFeature,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { audioApi, type AudioOp, type AudioThread, type AudioThreadVersion } from '../api';
import { opInfo } from '../ops';
import { audioScope, isPersonalScope } from '../scope';
import { downloadFile, saveVersion, selectThreadByHuman, takeVersion } from './actions';
import {
  doneText, hasMain, launchEndNote, launchOf, launchVersions, licenseBadge, licenseBadgeOf, priceText, saveKind,
  threadName, versionTag, type LicenseBadge,
} from './model';
import { recordOf, str } from './records';
import { SaveAsDialog } from './SaveAsDialog';
import { procMenuItems, versionPiece, type ProcMenuItem } from './procMenu';
import { getJobsOf, requestOperation, useAudioThreads, type JobProgress } from './threadStore';
import { VersionBody } from './VersionBody';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

const PERSONAL_DOWNLOAD_HINT = 'В личном чате звук хранится только в этом чате — скачайте файл, чтобы забрать его';

function Note({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45, overflowWrap: 'anywhere' }}>{children}</div>;
}

function Frame({ current, dashed, testId, onPick, children }: {
  current: boolean; dashed?: boolean; testId: string; onPick?: () => void; children: ReactNode;
}) {
  return (
    <div data-audio-card={testId} data-current={current ? 'true' : 'false'}
      // Клик по карточке, а не по её кнопке, — выбор человеком: панель следует за ним
      onClick={onPick ? e => { if (isCardPick(e.target, e.currentTarget)) onPick(); } : undefined}
      style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, width: '100%', maxWidth: 560, boxSizing: 'border-box',
      border: `1px ${dashed ? 'dashed' : 'solid'} ${current ? C.accent : C.border}`, borderRadius: R.xl,
      background: C.bgCard, boxShadow: current ? `${SHADOW.card}, 0 0 0 3px ${C.accentLight}` : SHADOW.card, minWidth: 0,
    }}>
      {children}
    </div>
  );
}

function License({ badge }: { badge: LicenseBadge | null }) {
  if (!badge) return null;
  return <span title={badge.hint} data-audio-license={badge.label}><Badge size="xs" tone={badge.tone}>{badge.label}</Badge></span>;
}

const modeIcon = (thread: AudioThread) =>
  thread.settings?.mode === 'voice' ? Mic : thread.settings?.mode === 'music' ? Music : AudioLines;

// Сохранение версии: в проекте — «Сохранить в проект» и «Сохранить как…», в личном чате — «Скачать»
function SaveActions({ scope, sessionId, thread, version }: {
  scope: string; sessionId: string; thread: AudioThread; version: AudioThreadVersion;
}) {
  const [busy, setBusy] = useState(false);
  const [asOpen, setAsOpen] = useState(false);
  if (saveKind(isPersonalScope(scope)) === 'download') {
    if (!hasMain(version)) return null;
    return (
      <Button size="sm" variant="secondary" leftIcon={ic(Download)} title={PERSONAL_DOWNLOAD_HINT}
        onClick={() => downloadFile(scope, sessionId, thread, version.id)}>
        Скачать
      </Button>
    );
  }
  // Исходник уже лежит в проекте
  if (version.id === 'origin') return null;
  const save = async () => {
    setBusy(true);
    await saveVersion(scope, sessionId, thread, version.id);
    setBusy(false);
  };
  return (
    <>
      <Button size="sm" variant="secondary" leftIcon={ic(Save)} loading={busy} onClick={() => { void save(); }}>
        Сохранить в проект
      </Button>
      <Button size="sm" variant="ghost" onClick={() => setAsOpen(true)}>Сохранить как…</Button>
      {asOpen && <SaveAsDialog scope={scope} sessionId={sessionId} thread={thread} version={version} onClose={() => setAsOpen(false)} />}
    </>
  );
}

// ── Карточка нити ──

// «Обработать ▾»: пункт ставит эту версию в работу и открывает панель «Звук» на операции
function ProcessMenu({ scope, sessionId, thread, version }: {
  scope: string; sessionId: string; thread: AudioThread; version: AudioThreadVersion;
}) {
  const [at, setAt] = useState<DOMRect | null>(null);
  const items = procMenuItems();
  const pick = async (it: ProcMenuItem) => {
    setAt(null);
    if (version.id !== thread.currentVersionId) await takeVersion(scope, sessionId, thread, version.id);
    requestOperation(sessionId, thread.id, it.op, it.op === 'concat' ? versionPiece(thread, version.id) : undefined);
  };
  const icon = (op: AudioOp) => ic(op === 'concat' ? Combine : op === 'trim' ? Scissors : op === 'convertVoice' ? Mic : op === 'cover' || op === 'repaint' ? Music : Wand2, ICON_SIZE.sm);
  const row = (it: ProcMenuItem) => <MenuItem key={it.op} icon={icon(it.op)} label={it.label} onClick={() => { void pick(it); }} />;
  return (
    <>
      <span data-audio-process="" style={{ display: 'inline-flex' }}>
        <Button size="sm" variant="secondary" leftIcon={ic(Wand2)}
          title="Операции над этой версией — откроются в панели «Звук»"
          onClick={e => setAt(at ? null : (e.currentTarget as HTMLElement).getBoundingClientRect())}>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs }}>Обработать{ic(ChevronDown)}</span>
        </Button>
      </span>
      {at && (
        <Menu anchor={at} minWidth={240} maxHeight={480} onClose={() => setAt(null)}>
          {items.filter(i => i.group === 'process').map(row)}
          <MenuSep />
          {items.filter(i => i.group === 'other').map(row)}
        </Menu>
      )}
    </>
  );
}

function DraftBox({ thread, focused }: { thread: AudioThread; focused: boolean }) {
  return (
    <Frame current={focused} dashed testId="draft">
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs, textAlign: 'center', fontSize: FS.sm, color: C.textMuted }}>
        <b style={{ color: C.textHeading, fontSize: FS.base }}>{threadName(thread)}</b>
        {focused ? 'Опишите его в поле ввода — версии появятся здесь' : 'Ещё не создан'}
      </div>
    </Frame>
  );
}

// Карточка версии: открыта на своей версии и действует от неё — «Работать с этой», «Обработать ▾»,
// выделение куска, A/B с основой этой версии, сохранение. Листания нет: каждая версия нити — своя
// карточка на своём месте в ленте (исходник — у якоря нити, варианты — у якоря запуска)
export function VersionCard({ scope, sessionId, thread, version: v, focused, events }: {
  scope: string; sessionId: string; thread: AudioThread; version: AudioThreadVersion; focused: boolean;
  events?: Parameters<typeof doneText>[2];
}) {
  const [busy, setBusy] = useState(false);
  const working = focused && v.id === thread.currentVersionId;
  const done = doneText(thread, v, events);
  const Icon = modeIcon(thread);

  return (
    <Frame current={working} testId={v.id} onPick={() => { void selectThreadByHuman(scope, sessionId, thread.id, focused); }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm, minWidth: 0 }}>
        <span style={{ display: 'inline-flex', color: C.textMuted }}>{ic(Icon, ICON_SIZE.sm)}</span>
        <span style={{ fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere', minWidth: 0 }}>{threadName(thread)}</span>
        {working && <Badge size="xs" tone="accent" icon={ic(Target)}>в работе</Badge>}
        <License badge={licenseBadge(v)} />
        <span data-audio-nav="" style={{ marginLeft: 'auto', fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{versionTag(thread, v)}</span>
      </div>
      {done && <Note>{done}</Note>}

      <VersionBody scope={scope} sessionId={sessionId} thread={thread} version={v} />

      <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', alignItems: 'center' }}>
        {/* Не primary: акцент в ленте — у ▶ и главного действия, а не у каждой карточки */}
        {!working && (
          <Button size="sm" variant="secondary" leftIcon={ic(Target)} loading={busy}
            title="Полоса «Звук» и следующий запуск пойдут от этой версии"
            onClick={() => { setBusy(true); void takeVersion(scope, sessionId, thread, v.id).finally(() => setBusy(false)); }}>
            Работать с этой
          </Button>
        )}
        {hasMain(v) && <ProcessMenu scope={scope} sessionId={sessionId} thread={thread} version={v} />}
        <SaveActions scope={scope} sessionId={sessionId} thread={thread} version={v} />
      </div>
    </Frame>
  );
}

// Якорь нити { threadId, versionId }: карточка этой версии. Без версии (черновик «Новый звук») —
// пунктир до первого запуска, потом молчит: варианты рисуют якоря запусков ниже
export function ThreadAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  // Личный чат вне проекта: ctx.projectId = null, область — personal
  const scope = audioScope(ctx.projectId);
  const state = useAudioThreads(scope, ctx.sessionId);
  const on = useFeature(FLAGS.audioEditor);
  const threadId = str(rec?.data.threadId);
  const thread = threadId ? state.threads.find(t => t.id === threadId) : undefined;
  if (!on || !ctx.sessionId || !thread) {
    // Нить удалили или модуль выключен — якорь в истории остался; рисуем след
    return rec?.fallback ? <Note>{rec.fallback}</Note> : null;
  }
  const focused = state.focus === thread.id;
  const versionId = str(rec?.data.versionId);
  const v = versionId ? thread.versions.find(x => x.id === versionId) : undefined;
  if (v) return <VersionCard scope={scope} sessionId={ctx.sessionId} thread={thread} version={v} focused={focused} events={state.events} />;
  return thread.versions.length || thread.launches.length ? null : <DraftBox thread={thread} focused={focused} />;
}

// ── Запуск и его варианты ──

const eta = (sec: number) => (sec < 60 ? `${sec} с` : `${Math.round(sec / 60)} мин`);

export function progressText(j: JobProgress | null): string {
  if (!j) return 'идёт';
  if (j.stage === 'queued') {
    const pos = j.queuePosition && j.queuePosition > 0 ? `в очереди GPU · ${j.queuePosition}-я` : 'в очереди';
    return j.etaSeconds != null ? `${pos} · старт ≈ через ${eta(j.etaSeconds)}` : pos;
  }
  if (j.stage === 'downloading') return 'забираем результат';
  const of = j.count > 1 ? ` · вариант ${j.variant} из ${j.count}` : '';
  return `идёт${of}${j.etaSeconds != null ? ` · ещё ≈ ${eta(j.etaSeconds)}` : ''}`;
}

function RunningLine({ scope, sessionId, threadId, jobId }: { scope: string; sessionId: string; threadId: string; jobId: string }) {
  const job = getJobsOf(sessionId, threadId).find(j => j.jobId === jobId) ?? null;
  const [cancelling, setCancelling] = useState(false);
  const percent = job && job.count > 0 && job.stage !== 'queued' ? Math.round(((job.variant - 1) / job.count) * 100) : 0;
  return (
    <div data-audio-running="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm, color: C.textSecondary }}>
        <Dot color={C.accent} />{progressText(job)}
        <Button size="xs" variant="ghost" leftIcon={ic(X)} loading={cancelling}
          onClick={() => {
            setCancelling(true);
            void audioApi.cancelJob(scope, sessionId, jobId).catch(() => {}).finally(() => setCancelling(false));
          }}>
          {job?.stage === 'queued' ? 'Убрать из очереди' : 'Отменить'}
        </Button>
      </div>
      <ProgressBar value={percent} transition="width .5s linear" />
      <Note>Варианты появятся здесь по мере готовности.</Note>
    </div>
  );
}

// Запуск: пока идёт — одна карточка с ходом (очередь, «Отменить»), по готовности — карточка версии
// на каждый вариант, на месте запуска в ленте
export function LaunchCard({ scope, sessionId, thread, focused, data, events }: {
  scope: string; sessionId: string; thread: AudioThread; focused: boolean; data: Record<string, unknown>;
  events?: Parameters<typeof doneText>[2];
}) {
  const jobId = str(data.jobId)!;
  const launch = launchOf(thread, jobId);
  const versions = launchVersions(thread, jobId);
  const count = typeof data.count === 'number' && data.count > 0 ? data.count : 1;
  const status = launch?.status ?? (versions.length ? 'done' : 'running');
  const agent = (str(data.initiator) ?? launch?.initiator) === 'agent';
  const op = opInfo(str(data.op) as AudioOp | null);
  const price = priceText(data.price as Parameters<typeof priceText>[0]);
  const info = [str(data.model), count > 1 ? `вариантов: ${count}` : null, price].filter(Boolean).join(' · ');
  const prompt = launch?.prompt ?? null;
  const endNote = launchEndNote(status, versions.length, count);

  return (
    <div data-audio-launch={status} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0, width: '100%', maxWidth: 560 }}>
      {versions.map(v => (
        <VersionCard key={v.id} scope={scope} sessionId={sessionId} thread={thread} version={v} focused={focused} events={events} />
      ))}

      {status === 'running' && (
        <Frame current={false} testId="running">
          <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm, color: C.textSecondary }}>
            <span style={{ display: 'inline-flex', alignSelf: 'center', color: C.textMuted }}>{ic(Sparkles)}</span>
            <b style={{ color: C.textHeading }}>{agent ? 'Claude' : 'Вы'}: {op?.label ?? 'звук'}</b>
            <span>{threadName(thread)}{info ? ` · ${info}` : ''}</span>
            <License badge={licenseBadgeOf(str(data.license) ?? launch?.license)} />
            {prompt && <span style={{ color: C.textMuted, fontStyle: 'italic', overflowWrap: 'anywhere' }}>«{prompt}»</span>}
          </div>
          <RunningLine scope={scope} sessionId={sessionId} threadId={thread.id} jobId={jobId} />
        </Frame>
      )}

      {endNote && <Note>{endNote}</Note>}
    </div>
  );
}

export function LaunchAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  const scope = audioScope(ctx.projectId);
  const state = useAudioThreads(scope, ctx.sessionId);
  const on = useFeature(FLAGS.audioEditor);
  const threadId = str(rec?.data.threadId);
  const thread = threadId ? state.threads.find(t => t.id === threadId) : undefined;
  if (!on || !ctx.sessionId || !thread || !rec || !str(rec.data.jobId)) {
    return rec?.fallback ? <Note>{rec.fallback}</Note> : null;
  }
  return <LaunchCard scope={scope} sessionId={ctx.sessionId} thread={thread} focused={state.focus === thread.id} data={rec.data} events={state.events} />;
}
