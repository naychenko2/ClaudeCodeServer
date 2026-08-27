import type { Session } from '../types';
import { api } from './api';

// Единая точка обновления полей чата.
//
// Тонкость, из-за которой это не однострочник у каждого вызывающего: эндпоинты разные —
// у проектной сессии /projects/{id}/sessions, у чата вне проекта /chats.
//
// Все поля идут по sentinel-семантике (нет поля = не менять), поэтому подставляем только
// то, что реально просили изменить: у name/model/effort на бэке «не менять» — это null,
// а очистка значения — пустая строка (SessionManager.Update). Подмешивать их из текущей
// сессии нельзя: непустое name поднимает UpdatedAt, и чат прыгал бы наверх списка
// непрочитанным от любой смены настройки (тумблер голосового режима, срок жизни, мьют).
export interface ChatFieldsPatch {
  name?: string | null;
  model?: string | null;
  effort?: string | null;
  expiresAfterMinutes?: number | null;
  tags?: string[];
  // Opt-out «не сохранять решения из этого чата» (ADR-004 §6). Только у проектных сессий
  excludeFromDossiers?: boolean | null;
  notificationsMuted?: boolean;
  // Голосовой режим чата: ответы озвучиваются (POST /api/tts)
  voiceMode?: boolean;
  // Стиль озвучки ('talk' | 'digest'). Уходит и БЕЗ voiceMode: стиль принадлежит
  // устройству, и второе устройство выправляет его у чата с уже включённой озвучкой
  voiceStyle?: string;
  // Архив: true — убрать чат из списка, false — вернуть. Работает у обоих видов чатов
  archived?: boolean;
}

export function updateChatFields(session: Session, patch: ChatFieldsPatch): Promise<Session> {
  const data = {
    ...(patch.name !== undefined && { name: patch.name }),
    ...(patch.model !== undefined && { model: patch.model }),
    ...(patch.effort !== undefined && { effort: patch.effort }),
    ...(patch.expiresAfterMinutes !== undefined && { expiresAfterMinutes: patch.expiresAfterMinutes }),
    ...(patch.tags !== undefined && { tags: patch.tags }),
    ...(patch.excludeFromDossiers !== undefined && { excludeFromDossiers: patch.excludeFromDossiers }),
    ...(patch.notificationsMuted !== undefined && { notificationsMuted: patch.notificationsMuted }),
    ...(patch.voiceMode !== undefined && { voiceMode: patch.voiceMode }),
    ...(patch.voiceStyle !== undefined && { voiceStyle: patch.voiceStyle }),
    ...(patch.archived !== undefined && { archived: patch.archived }),
  };
  return session.projectId
    ? api.sessions.update(session.projectId, session.id, data)
    : api.chats.update(session.id, data);
}

// Сосед архивируемого чата в списке: на кого переключить центр, когда активный чат
// ушёл в архив. «Предыдущий» — по порядку списка (свежесть/закрепление уже учтены
// в его сортировке), не по истории переходов: после архивации из списка пропадает
// и сама строка, и «исторический сосед» стал бы ссылкой на чат, которого на экране нет.
// null — неархивных соседей нет (архивировали последний) — центр уходит в пустое состояние.
export function chatNeighborForArchive(list: Session[], archivedId: string): Session | null {
  const live = list.filter(s => s.id !== archivedId && !s.archivedAt);
  if (live.length === 0) return null;
  const idx = list.findIndex(s => s.id === archivedId);
  // Чата нет в списке (архивация со стены/из другого окна) — просто первый живой
  if (idx < 0) return live[0];
  // Ближайший НЕархивный сосед: сперва вверх по списку, потом вниз. Вверх — первым:
  // список свежими сверху, и чат СВЕРХУ — тот, из которого пришли в архивируемый
  for (let i = idx - 1; i >= 0; i--) {
    const s = list[i];
    if (!s.archivedAt) return s;
  }
  for (let i = idx + 1; i < list.length; i++) {
    const s = list[i];
    if (!s.archivedAt) return s;
  }
  return null;
}
