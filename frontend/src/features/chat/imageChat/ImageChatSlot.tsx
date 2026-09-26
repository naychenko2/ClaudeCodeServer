// Чат картинки для MF-модуля редактора (ADR-018 §10.3, вариант В): ядро отдаёт модулю
// готовый компонент через контекст слота `app-overlay`, чтобы в бандле модуля не было
// второй копии ChatPanel, SignalR и сторов.
//
// Пока заглушка: встраивание ChatPanel (embedded, hideHeader, заглушка композера до
// создания чата) — шаг 14 плана. Модуль до того этот компонент не рисует.

import type { ImageChatSlotProps } from '../../../lib/subsystems/registryCore';

export function ImageChatSlot(_props: ImageChatSlotProps) {
  return null;
}
