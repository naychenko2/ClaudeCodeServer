// Вклад «картинка» в слот context-kind (ADR-023, шаг 2к-1): чипы действий поля ввода, превью и редактор
// основного объекта, «Чем» и параметры панели «Контекст», цена и запуск. Входы запуска бэкенд читает
// из стора контекста по ревизии, поэтому здесь только `op`, `params` и `contextRevision`.

import { Image as ImageIcon, User } from 'lucide-react';
import {
  C, ICON_SIZE, ICON_STROKE, R, SP, notifyKindChanged,
  type ChatContextItem, type ContextKindApi, type ContextKindCtx,
} from 'aihome_shell/kit';
import { enterScope } from '../scope';
import { getCatalog, loadCatalog } from '../thread/catalog';
import { createDraft } from '../thread/actions';
import { ensurePrefs, subscribePrefs } from '../thread/prefs';
import { threadHasImage } from '../thread/model';
import { subscribeThreadStore, useThreadStoreVersion } from '../thread/threadStore';
import { activeSrc, versionSrc } from '../thread/useThreadLaunch';
import { openEditor } from '../thread/threadStore';
import type { ImageEditOp } from '../api';
import { executorModel, settingsOf } from './executors';
import { launchAction, paramsFor, quoteAction } from './run';
import { imageRefRoles } from './roles';
import { actionOf, IMAGE_KIND, imageActions, threadOfPrimary } from './state';

const CHARACTER_KIND = 'image-character';

// Действия, «Чем» и цена зависят от внешнего состояния (нити, отметки, настройки, каталог): хост
// узнаёт о его смене через notifyKindChanged. Подписка одна на вкладку, каталог и настройки
// запрашиваются один раз на область
let _bridged = false;
const _warmed = new Set<string>();

function warm(scope: string) {
  if (!_bridged) {
    _bridged = true;
    subscribeThreadStore(notifyKindChanged);
    subscribePrefs(notifyKindChanged);
  }
  if (_warmed.has(scope)) return;
  _warmed.add(scope);
  void loadCatalog(scope).then(c => { if (c) notifyKindChanged(); });
  void ensurePrefs(scope);
}

// Миниатюра версии основного объекта: версия из ref, иначе текущая позиция нити
function ImagePreview({ ctx, item }: { ctx: ContextKindCtx; item: ChatContextItem }) {
  useThreadStoreVersion();
  const scope = enterScope(ctx.projectId, ctx.sessionId);
  const thread = threadOfPrimary(ctx.sessionId, item as never);
  const versionId = typeof item.ref.versionId === 'string' ? item.ref.versionId : null;
  const version = thread && versionId ? thread.versions?.find(v => v.id === versionId) ?? null : null;
  const src = thread ? (version ? versionSrc(scope, thread, version) : activeSrc(scope, thread)) ?? item.thumb : item.thumb;
  const box = { width: 96, height: 96, borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgPanel, flexShrink: 0 } as const;
  return src
    ? <img data-ctx-preview="image" src={src} alt={item.label} style={{ ...box, objectFit: 'cover' }} />
    : (
      <div data-ctx-preview="image" style={{ ...box, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, padding: SP.xs }}>
        <ImageIcon size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
      </div>
    );
}

export const imageKindApi: ContextKindApi = {
  kinds: [IMAGE_KIND, CHARACTER_KIND],
  icon: kind => kind === CHARACTER_KIND
    ? <User size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    : <ImageIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
  actions: (ctx, s) => {
    warm(enterScope(ctx.projectId, ctx.sessionId));
    return imageActions(ctx, s);
  },
  refRoles: (_ctx, primary, candidateKind) => (primary.kind === IMAGE_KIND ? imageRefRoles(candidateKind) : []),
  preview: (ctx, item) => <ImagePreview ctx={ctx} item={item} />,
  editor: (ctx, item) => {
    const thread = threadOfPrimary(ctx.sessionId, item as never);
    if (!thread || !threadHasImage(thread)) return null;
    return {
      label: 'Редактор',
      hint: 'Маска и «Без ИИ»: обрезать, повернуть, размер и формат',
      open: () => openEditor(ctx.sessionId, thread.id, null),
    };
  },
  executors: (ctx, actionId) => {
    const found = actionOf(ctx, actionId);
    if (!found?.action.op) return null;
    const catalog = getCatalog(found.scope);
    if (!catalog) return null;
    return executorModel({
      scope: found.scope, sessionId: ctx.sessionId, thread: found.thread, op: found.action.op as ImageEditOp,
      catalog, hasImage: found.input.hasFile, hasMask: found.input.hasMask,
    });
  },
  params: (ctx, actionId) => {
    const found = actionOf(ctx, actionId);
    if (!found?.action.op) return [];
    const op = found.action.op as ImageEditOp;
    return paramsFor(getCatalog(found.scope), settingsOf(found.scope, found.thread, op), op);
  },
  create: {
    title: 'Картинка',
    hint: 'черновик «Новая картинка»',
    icon: <ImageIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
    // Черновик становится основным объектом сам (фокус нити → контекст), панель не прыгает
    run: ctx => { void createDraft(enterScope(ctx.projectId, ctx.sessionId), ctx.sessionId, '', 'none'); },
  },
  priceSalt: (ctx, actionId) => {
    const input = actionOf(ctx, actionId)?.input;
    return input ? `${input.marks}:${input.hasMask}` : '';
  },
  quote: quoteAction,
  launch: launchAction,
};
