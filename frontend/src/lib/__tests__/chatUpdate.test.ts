import { describe, it, expect, beforeEach, vi } from 'vitest';
import { updateChatFields } from '../chatUpdate';
import { api } from '../api';
import type { Session } from '../../types';

vi.mock('../api', () => ({
  api: { sessions: { update: vi.fn() }, chats: { update: vi.fn() } },
}));

// Чат с именем и моделью: именно на нём ловился баг «тумблер поднимает чат наверх списка»
const session = { id: 's1', projectId: 'p1', name: 'Мой чат', model: 'opus' } as Session;

describe('updateChatFields — частичный патч, а не полная замена', () => {
  beforeEach(() => vi.clearAllMocks());

  it('регрессия: настройка чата не тащит за собой name — иначе бэк поднимает UpdatedAt', async () => {
    // SessionManager.Update поднимает UpdatedAt при непустом name: чат прыгал наверх
    // списка и метился непрочитанным от одного лишь тумблера голосового режима
    await updateChatFields(session, { voiceMode: true });
    expect(api.sessions.update).toHaveBeenCalledWith('p1', 's1', { voiceMode: true });
  });

  // Стиль озвучки уходит и БЕЗ voiceMode: он принадлежит устройству, и второе устройство
  // выправляет его у чата с уже включённой озвучкой. Поле из тех, что молча теряются —
  // белый список подмешивает только известные ключи
  it('стиль озвучки не теряется в белом списке полей', async () => {
    await updateChatFields(session, { voiceStyle: 'digest' });
    expect(api.sessions.update).toHaveBeenCalledWith('p1', 's1', { voiceStyle: 'digest' });
  });

  it('переданные поля уходят как есть, включая очистку имени пустой строкой', async () => {
    await updateChatFields(session, { name: '', model: 'glm-5.2' });
    expect(api.sessions.update).toHaveBeenCalledWith('p1', 's1', { name: '', model: 'glm-5.2' });
  });

  it('чат вне проекта уходит на /chats', async () => {
    await updateChatFields({ ...session, projectId: undefined } as Session, { notificationsMuted: true });
    expect(api.chats.update).toHaveBeenCalledWith('s1', { notificationsMuted: true });
  });
});
