// Слот `app-overlay`: слои подсистем уровня приложения, живущие НАД страницами.
// Сейчас его наполняет редактор картинок (MF-модуль image-editor): уход с проекта должен
// спросить про несохранённые варианты, а не молча размонтировать редактор вместе с
// деревом файлов. Контекст несёт чат картинки ядра (ADR-018 §10.3).

import { Fragment } from 'react';
import { useSlot, type AppOverlayCtx } from '../lib/subsystems/registry';
import { ImageChatSlot } from '../features/chat/imageChat/ImageChatSlot';

const CTX: AppOverlayCtx = { ImageChat: ImageChatSlot };

export function AppOverlaySlot() {
  const items = useSlot<AppOverlayCtx>('app-overlay');
  return <>{items.map((c, i) => <Fragment key={c.name ?? i}>{c.render?.(CTX)}</Fragment>)}</>;
}
