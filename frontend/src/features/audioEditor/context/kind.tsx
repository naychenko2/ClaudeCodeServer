// Вклад «звук» в слот context-kind (ADR-023, шаг 2з-1): чипы действий поля ввода, волна и редактор
// основного объекта, «Чем» и параметры панели «Контекст», цена и запуск. Входы запуска бэкенд читает
// из стора контекста по ревизии, поэтому здесь только `op`, текст, `params` и `contextRevision`.

import { AudioLines, Mic } from 'lucide-react';
import {
  C, ICON_SIZE, ICON_STROKE, R, SP, notifyKindChanged,
  type ChatContextItem, type ContextKindApi, type ContextKindCtx,
} from 'aihome_shell/kit';
import type { AudioOp } from '../api';
import { AudioWave } from '../player/AudioWave';
import { audioScope, isPersonalScope } from '../scope';
import { createDraft } from '../thread/actions';
import { useServerPeaks } from '../thread/serverPeaks';
import { ensureAudioThreads, openEditor, subscribeAudioStore, getCatalog, getThreadsState, useAudioStoreVersion } from '../thread/threadStore';
import { hasMain, threadName } from '../thread/model';
import { executorModel, getChoice } from './executors';
import { launchAction, paramsFor, quoteAction } from './run';
import { migrateLegacyVoice } from './legacyInputs';
import { audioRefRoles } from './roles';
import { actionOf, audioActions, AUDIO_KIND, threadOfPrimary, versionOfPrimary } from './state';
import { workWithInContext } from './work';

const VOICE_KIND = 'audio-voice';
const versionTitle = (v: { id: string; number: number }) => (v.id === 'origin' ? 'исходник' : `версия ${v.number}`);
const WAVE_POINTS = 90;

// Действия, «Чем» и цена зависят от внешнего состояния (нити, каталог, выделение на волне): хост
// узнаёт о его смене через notifyKindChanged. Подписка одна на вкладку, нити и каталог запрашиваются
// один раз на чат
let _bridged = false;
const _warmed = new Set<string>();

function warm(ctx: ContextKindCtx) {
  if (!_bridged) {
    _bridged = true;
    subscribeAudioStore(notifyKindChanged);
  }
  const key = ctx.sessionId;
  if (_warmed.has(key)) return;
  _warmed.add(key);
  void ensureAudioThreads(audioScope(ctx.projectId), ctx.sessionId);
}

// Волна версии основного объекта; нет звука (черновик) — значок
function AudioPreview({ ctx, item }: { ctx: ContextKindCtx; item: ChatContextItem }) {
  useAudioStoreVersion();
  const thread = threadOfPrimary(ctx.sessionId, item as never);
  const version = thread ? versionOfPrimary(thread, item as never) : null;
  const [peaks] = useServerPeaks(thread && version
    ? [{ scope: audioScope(ctx.projectId), sessionId: ctx.sessionId, threadId: thread.id, versionId: version.id, role: null, points: WAVE_POINTS }]
    : []);
  const box = { width: 160, borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgPanel, flexShrink: 0, padding: SP.xs } as const;
  return (
    <div data-ctx-preview="audio" style={box}>
      {version
        ? <AudioWave peaks={peaks?.peaks ?? []} duration={peaks?.seconds ?? 0} size="sm" ariaLabel={item.label} />
        : <div style={{ display: 'flex', justifyContent: 'center', color: C.textMuted }}><AudioLines size={ICON_SIZE.md} strokeWidth={ICON_STROKE} /></div>}
    </div>
  );
}

export const audioKindApi: ContextKindApi = {
  kinds: [AUDIO_KIND, VOICE_KIND],
  icon: kind => kind === VOICE_KIND
    ? <Mic size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    : <AudioLines size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
  actions: (ctx, s) => {
    warm(ctx);
    const thread = threadOfPrimary(ctx.sessionId, s.primary);
    if (thread) migrateLegacyVoice(audioScope(ctx.projectId), ctx.sessionId, thread.id, s.refs);
    return audioActions(ctx, s);
  },
  refRoles: (_ctx, primary, candidateKind) => (primary.kind === AUDIO_KIND ? audioRefRoles(candidateKind) : []),
  preview: (ctx, item) => <AudioPreview ctx={ctx} item={item} />,
  sub: (ctx, item) => {
    const thread = threadOfPrimary(ctx.sessionId, item as never);
    const cur = thread ? versionOfPrimary(thread, item as never) : null;
    if (!thread || !cur || item.kind !== AUDIO_KIND) return null;
    const mains = thread.versions;
    return mains.length > 1 ? `${versionTitle(cur)} из ${mains.length}` : versionTitle(cur);
  },
  // Звуки ленты: текущая версия каждой нити, у которой есть главный файл
  feed: ctx => getThreadsState(ctx.sessionId).threads
    .flatMap(t => {
      const cur = t.versions.find(v => v.id === t.currentVersionId);
      return cur && hasMain(cur)
        ? [{ id: t.id, label: threadName(t), hint: versionTitle(cur), candidate: { kind: AUDIO_KIND, ref: { threadId: t.id, versionId: cur.id } } }]
        : [];
    }),
  // ‹ › версий: основной объект переставляется на соседнюю версию той же нити
  step: (ctx, item) => {
    const thread = threadOfPrimary(ctx.sessionId, item as never);
    const cur = thread ? versionOfPrimary(thread, item as never) : null;
    if (!thread || !cur || item.kind !== AUDIO_KIND || thread.versions.length < 2) return null;
    const i = thread.versions.findIndex(v => v.id === cur.id);
    const go = (j: number) => (j >= 0 && j < thread.versions.length
      ? () => { void workWithInContext(ctx.sessionId, thread.id, thread.versions[j].id, false); } : null);
    return { prev: go(i - 1), next: go(i + 1) };
  },
  editor: (ctx, item) => {
    const thread = threadOfPrimary(ctx.sessionId, item as never);
    if (!thread || !versionOfPrimary(thread, item as never)) return null;
    return {
      label: 'Открыть редактор',
      hint: 'Волна и кусок, монтаж без ИИ',
      open: () => openEditor(ctx.sessionId, thread.id, versionOfPrimary(thread, item as never)?.id ?? null),
    };
  },
  executors: (ctx, actionId, answer) => {
    const found = actionOf(ctx, actionId);
    if (!found?.action.op) return null;
    return executorModel({
      sessionId: ctx.sessionId, op: found.action.op as AudioOp, catalog: getCatalog(found.scope),
      personal: isPersonalScope(found.scope), notify: notifyKindChanged, stemSet: answer,
    });
  },
  params: (ctx, actionId) => {
    const found = actionOf(ctx, actionId);
    return found?.action.op ? paramsFor(getCatalog(found.scope), found.action.op as AudioOp) : [];
  },
  create: {
    title: 'Звук',
    hint: 'черновик «Новый звук»',
    icon: <AudioLines size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
    // Черновик становится основным объектом сам (фокус нити → контекст), панель не прыгает
    run: ctx => {
      const scope = audioScope(ctx.projectId);
      void ensureAudioThreads(scope, ctx.sessionId).then(() => createDraft(scope, ctx.sessionId, 'voice'));
    },
  },
  // Исполнитель входит в ключ цены: смена «Чем» пересчитывает котировку, а не оставляет чужую цену
  priceSalt: (ctx, actionId) => {
    const op = actionOf(ctx, actionId)?.action.op as AudioOp | undefined;
    const c = op ? getChoice(ctx.sessionId, op) : null;
    return c ? `${c.provider}/${c.model}` : '';
  },
  quote: quoteAction,
  launch: launchAction,
};
