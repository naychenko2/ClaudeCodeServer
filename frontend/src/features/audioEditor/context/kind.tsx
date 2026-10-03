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
import { ensureAudioThreads, openEditor, subscribeAudioStore, getCatalog, useAudioStoreVersion } from '../thread/threadStore';
import { executorModel } from './executors';
import { launchAction, paramsFor, quoteAction } from './run';
import { actionOf, audioActions, AUDIO_KIND, threadOfPrimary, versionOfPrimary } from './state';

const VOICE_KIND = 'audio-voice';
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
    return audioActions(ctx, s);
  },
  preview: (ctx, item) => <AudioPreview ctx={ctx} item={item} />,
  editor: (ctx, item) => {
    const thread = threadOfPrimary(ctx.sessionId, item as never);
    if (!thread || !versionOfPrimary(thread, item as never)) return null;
    return {
      label: 'Редактор',
      hint: 'Волна и кусок, монтаж без ИИ: обрезать, затухание',
      open: () => openEditor(ctx.sessionId, thread.id, versionOfPrimary(thread, item as never)?.id ?? null),
    };
  },
  executors: (ctx, actionId) => {
    const found = actionOf(ctx, actionId);
    if (!found?.action.op) return null;
    return executorModel({
      sessionId: ctx.sessionId, op: found.action.op as AudioOp, catalog: getCatalog(found.scope),
      personal: isPersonalScope(found.scope), notify: notifyKindChanged,
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
  quote: quoteAction,
  launch: launchAction,
};
