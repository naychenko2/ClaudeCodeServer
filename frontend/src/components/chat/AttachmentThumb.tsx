// Миниатюра картинки-вложения в пузыре сообщения (ADR-041, строка №12 таблицы ручек).
// Показывает только уже загруженное вложение: URL — исключительно через
// chatsApi.chats.attachmentRawUrl (единственная точка). Не-картинка, нет sessionId,
// агентный маршрут (своих файлов на маршруте агента через эту ручку нет) и любая ошибка
// загрузки <img> — тот же ярлык, что был до миниатюр (стиль и текст — ChatItemView).
import { useState } from 'react';
import { C, R } from '../../lib/design';
import { ImageLightbox } from '../ui/ImageLightbox';
import { chatsApi } from '../../features/chat/api';

// Бэк определяет тип по сигнатуре файла, не по расширению (ADR-041, строка №8), но
// расширение — единственный сигнал, доступный фронту до запроса: не-картинка не делает
// запроса и сразу показывает ярлык. Список совпадает с image-allow-list строки №12.
const IMAGE_EXT = /\.(png|jpe?g|webp|gif)$/i;

export function AttachmentThumb({ sessionId, path, label, agentRoute }: {
  sessionId: string | null;
  path: string;
  label: string;
  agentRoute?: boolean;
}) {
  // Падение привязано к path (тот же приём, что у HistoryThumb): ошибка загрузки
  // асинхронна и может прийти, когда компонент уже показывает другой путь, — старое
  // падение не должно прятать миниатюру нового.
  const [failedPath, setFailedPath] = useState<string | null>(null);
  const [lightbox, setLightbox] = useState(false);
  const src = sessionId && !agentRoute && IMAGE_EXT.test(path)
    ? chatsApi.chats.attachmentRawUrl(sessionId, path)
    : null;

  if (!src || failedPath === path) {
    // Ярлык ровно как в ленте до миниатюр (ChatItemView, attachedPaths): тот же стиль, тот же текст
    return (
      <span style={{
        background: C.bgPanel, color: C.textSecondary, borderRadius: 5,
        padding: '1px 6px', fontSize: 11, maxWidth: '100%', overflowWrap: 'anywhere',
      }}>
        {label}
      </span>
    );
  }

  return (
    <>
      {/* Клик открывает лайтбокс (тот же путь, что у картинок в markdown); href остаётся
          для «открыть ссылку» — URL с токеном работает и как прямая ссылка */}
      <a href={src} onClick={e => { e.preventDefault(); setLightbox(true); }}
        style={{ display: 'inline-block', maxWidth: '100%' }}>
        <img src={src} alt={label} loading="lazy" onError={() => setFailedPath(path)}
          style={{
            maxHeight: 160, maxWidth: '100%', objectFit: 'contain', display: 'block',
            background: C.bgPanel, borderRadius: R.sm, border: `1px solid ${C.border}`,
            cursor: 'zoom-in',
          }} />
      </a>
      {lightbox && <ImageLightbox src={src} alt={label} onClose={() => setLightbox(false)} />}
    </>
  );
}
