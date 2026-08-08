import type { Session } from '../types';
import { api } from './api';

// Единая точка обновления полей чата.
//
// Две тонкости, из-за которых это не однострочник у каждого вызывающего:
//  1) эндпоинты разные — у проектной сессии /projects/{id}/sessions, у чата вне
//     проекта /chats;
//  2) update на бэке — ПОЛНАЯ замена: name/model/effort перезаписываются целиком,
//     и отсутствующее поле обнуляется. Поэтому недостающие подмешиваем из текущей
//     сессии, иначе смена одного срока жизни стирала бы имя и модель чата.
// Время жизни, теги, opt-out истории и тумблер уведомлений живут по sentinel-семантике
// (нет поля = не менять) — их подставляем только когда их реально просили изменить.
export interface ChatFieldsPatch {
  name?: string | null;
  model?: string | null;
  effort?: string | null;
  expiresAfterMinutes?: number | null;
  tags?: string[];
  // Opt-out «не сохранять решения из этого чата» (ADR-004 §6). Только у проектных сессий
  excludeFromDossiers?: boolean | null;
  notificationsMuted?: boolean;
}

export function updateChatFields(session: Session, patch: ChatFieldsPatch): Promise<Session> {
  const data = {
    name: patch.name !== undefined ? patch.name : (session.name ?? null),
    model: patch.model !== undefined ? patch.model : (session.model ?? null),
    effort: patch.effort !== undefined ? patch.effort : (session.effort ?? null),
    ...(patch.expiresAfterMinutes !== undefined && { expiresAfterMinutes: patch.expiresAfterMinutes }),
    ...(patch.tags !== undefined && { tags: patch.tags }),
    ...(patch.excludeFromDossiers !== undefined && { excludeFromDossiers: patch.excludeFromDossiers }),
    ...(patch.notificationsMuted !== undefined && { notificationsMuted: patch.notificationsMuted }),
  };
  return session.projectId
    ? api.sessions.update(session.projectId, session.id, data)
    : api.chats.update(session.id, data);
}
