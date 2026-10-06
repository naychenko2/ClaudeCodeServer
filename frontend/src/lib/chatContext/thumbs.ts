// Миниатюры объектов контекста: сервер их не присылает (thumb = null), поэтому адрес собирает фронт — вид объекта
// знает, где лежит его картинка (версия нити), а файл проекта берётся по пути. Хост подставляет их в DTO до отрисовки.

import { api } from '../api';
import { getKindApi } from './registry';
import type { ChatContextItem, ChatContextPrimary, ChatContextRef, ContextKindCtx } from './types';

const IMAGE_FILE = /\.(png|jpe?g|webp|gif|bmp)$/i;

export function thumbOf(ctx: ContextKindCtx, item: ChatContextItem): string | null {
  if (item.thumb) return item.thumb;
  const fromKind = getKindApi(item.kind)?.thumb?.(ctx, item);
  if (fromKind) return fromKind;
  const path = item.kind === 'project-file' ? item.ref.path : null;
  return typeof path === 'string' && ctx.projectId && IMAGE_FILE.test(path) ? api.files.fileUrl(ctx.projectId, path) : null;
}

export const withThumb = <T extends ChatContextPrimary | ChatContextRef>(ctx: ContextKindCtx, item: T): T => {
  const thumb = thumbOf(ctx, item);
  return thumb === item.thumb ? item : { ...item, thumb };
};
