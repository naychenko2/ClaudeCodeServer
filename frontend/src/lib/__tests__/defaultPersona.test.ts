import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  setMeFromServer, clearMe, getContextDefaultPersonaId, createChatWithContextPersona,
  OnboardingRequiredError,
} from '../defaultPersona';
import { api } from '../api';
import type { Me, Session } from '../../types';

// getContextDefaultPersonaId + createChatWithContextPersona: единый инвариант «чат только с
// персоной» на фронте. onMessage мокается — реальный SignalR-коннект не нужен и недоступен
// в node-окружении vitest.
vi.mock('../signalr', () => ({ onMessage: vi.fn(() => () => {}) }));
vi.mock('../api', () => ({
  api: {
    sessions: { create: vi.fn() },
    chats: { create: vi.fn() },
    personas: { createChat: vi.fn() },
  },
}));

const fakeMe = (defaultPersonaId: string | null): Me => ({
  userId: 'u1', username: 'test', role: 'user', defaultPersonaId,
});

const fakeSession = { id: 'session-1' } as Session;

describe('getContextDefaultPersonaId', () => {
  beforeEach(() => clearMe());

  it('проект с дефолтом — возвращает дефолт проекта (руководитель), не личный', () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { defaultPersonaId: 'project-lead', ownerId: 'u1' };

    expect(getContextDefaultPersonaId(project)).toBe('project-lead');
  });

  it('проект без дефолта — падает на личный дефолт владельца', () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { defaultPersonaId: null, ownerId: 'u1' };

    expect(getContextDefaultPersonaId(project)).toBe('personal-default');
  });

  // W3-F2a (дефект D1): в общем проекте без руководителя личная персона не подставляется —
  // иначе бэк молча создал бы участнику личный чат вне проекта. Признак «не владелец» —
  // ownerId проекта != id текущего пользователя (me.userId == Project.ownerId бэка).
  it('общий проект без руководителя, вызывающий — не владелец: личная персона НЕ подставляется', () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { defaultPersonaId: null, ownerId: 'owner-2' };

    expect(getContextDefaultPersonaId(project)).toBeNull();
  });

  it('тот же проект для владельца — фолбэк на личную персону, как раньше', () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { defaultPersonaId: null, ownerId: 'u1' };

    expect(getContextDefaultPersonaId(project)).toBe('personal-default');
  });

  // Руководитель важнее признака владельца: участнику общего проекта с руководителем —
  // руководитель (ничего не меняется).
  it('общий проект с руководителем, вызывающий — не владелец: дефолт проекта', () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { defaultPersonaId: 'project-lead', ownerId: 'owner-2' };

    expect(getContextDefaultPersonaId(project)).toBe('project-lead');
  });

  it('вне проекта — сразу личный дефолт', () => {
    setMeFromServer(fakeMe('personal-default'));

    expect(getContextDefaultPersonaId(null)).toBe('personal-default');
  });

  it('дефолта нет нигде — null (осиротевший юзер без ассистента)', () => {
    setMeFromServer(fakeMe(null));

    expect(getContextDefaultPersonaId(null)).toBeNull();
  });
});

describe('createChatWithContextPersona', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    clearMe();
  });

  it('вне проекта, дефолт есть — создаёт чат от лица дефолт-персоны', async () => {
    setMeFromServer(fakeMe('assistant-1'));
    vi.mocked(api.personas.createChat).mockResolvedValue(fakeSession);

    const result = await createChatWithContextPersona(null, { mode: 'auto' });

    expect(result).toBe(fakeSession);
    expect(api.personas.createChat).toHaveBeenCalledWith('assistant-1',
      { mode: 'auto', name: undefined, projectId: undefined });
    expect(api.chats.create).not.toHaveBeenCalled();
  });

  it('без явного режима — режим не передаётся, умолчание решает сервер', async () => {
    setMeFromServer(fakeMe('assistant-1'));
    vi.mocked(api.personas.createChat).mockResolvedValue(fakeSession);

    await createChatWithContextPersona(null);

    expect(api.personas.createChat).toHaveBeenCalledWith('assistant-1',
      { mode: undefined, name: undefined, projectId: undefined });
  });

  it('в проекте с руководителем — создаёт сессию проекта от его лица', async () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { id: 'proj-1', defaultPersonaId: 'project-lead', ownerId: 'u1' };
    vi.mocked(api.personas.createChat).mockResolvedValue(fakeSession);

    await createChatWithContextPersona(project, { mode: 'acceptEdits' });

    expect(api.personas.createChat).toHaveBeenCalledWith('project-lead',
      { mode: 'acceptEdits', name: undefined, projectId: 'proj-1' });
    expect(api.sessions.create).not.toHaveBeenCalled();
  });

  it('дефолта нет (пустой) — идёт на старый путь без personaId, сервер провижнит сам', async () => {
    setMeFromServer(fakeMe(null));
    vi.mocked(api.chats.create).mockResolvedValue(fakeSession);

    const result = await createChatWithContextPersona(null, { mode: 'auto' });

    expect(result).toBe(fakeSession);
    expect(api.chats.create).toHaveBeenCalledWith('auto', undefined, undefined);
    expect(api.personas.createChat).not.toHaveBeenCalled();
  });

  it('дефолта нет, сервер тоже не смог (400) — OnboardingRequiredError со scope user', async () => {
    setMeFromServer(fakeMe(null));
    vi.mocked(api.chats.create).mockRejectedValue(Object.assign(new Error('нужна персона'), { status: 400 }));

    await expect(createChatWithContextPersona(null, { mode: 'auto' }))
      .rejects.toThrow(OnboardingRequiredError);
  });

  it('дефолта нет, проектный сценарий 400 — OnboardingRequiredError со scope project', async () => {
    setMeFromServer(fakeMe(null));
    const project = { id: 'proj-1', defaultPersonaId: null, ownerId: 'u1' };
    vi.mocked(api.sessions.create).mockRejectedValue(Object.assign(new Error('нужна персона'), { status: 400 }));

    const error = await createChatWithContextPersona(project, { mode: 'auto' }).catch(e => e);

    expect(error).toBeInstanceOf(OnboardingRequiredError);
    expect((error as InstanceType<typeof OnboardingRequiredError>).scope).toBe('project');
  });

  it('не-400 ошибка сервера прокидывается как есть, не превращается в OnboardingRequiredError', async () => {
    setMeFromServer(fakeMe(null));
    const serverError = Object.assign(new Error('Сервер недоступен'), { status: 500 });
    vi.mocked(api.chats.create).mockRejectedValue(serverError);

    await expect(createChatWithContextPersona(null, { mode: 'auto' })).rejects.toBe(serverError);
  });

  // W3-F2a (контракт W3-B4b): участник общего проекта без руководителя — 400 с кодом
  // no_project_persona. Это не онбординг (назначает только владелец), поэтому текст
  // сервера уходит вызывающему как есть — мокаем ошибку так же, как её строит транспорт
  // offline.ts:1685–1690: message = body.error, поля status/body прикреплены.
  it('400 no_project_persona — Error с текстом сервера, не OnboardingRequiredError', async () => {
    setMeFromServer(fakeMe(null));
    const project = { id: 'proj-1', defaultPersonaId: null, ownerId: 'owner-2' };
    const serverText = 'В проекте нет руководителя — попросите владельца назначить';
    vi.mocked(api.sessions.create).mockRejectedValue(
      Object.assign(new Error(serverText), { status: 400, body: { code: 'no_project_persona', error: serverText } }),
    );

    const error = await createChatWithContextPersona(project, { mode: 'auto' }).catch(e => e);

    expect(error).toBeInstanceOf(Error);
    expect(error).not.toBeInstanceOf(OnboardingRequiredError);
    expect((error as Error).message).toBe(serverText);
  });

  // Различаем по body.code, не по тексту: тот же 400 с другим кодом — прежний путь.
  it('400 с другим кодом — как раньше, OnboardingRequiredError (scope project)', async () => {
    setMeFromServer(fakeMe(null));
    const project = { id: 'proj-1', defaultPersonaId: null, ownerId: 'owner-2' };
    vi.mocked(api.sessions.create).mockRejectedValue(
      Object.assign(new Error('нужна персона'), { status: 400, body: { code: 'global_persona_in_shared', error: 'нет персоны' } }),
    );

    const error = await createChatWithContextPersona(project, { mode: 'auto' }).catch(e => e);

    expect(error).toBeInstanceOf(OnboardingRequiredError);
    expect((error as InstanceType<typeof OnboardingRequiredError>).scope).toBe('project');
  });

  // W3-F2a (дефект D1): участник общего проекта без руководителя — даже с личной дефолт-
  // персоной запрос идёт БЕЗ personaId (через sessions.create), а не через личную персону:
  // бэк отвечает 400 no_project_persona, текст уходит вызывающему как есть (тост).
  it('участник общего проекта без руководителя — запрос без personaId, не через личную персону', async () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { id: 'proj-1', defaultPersonaId: null, ownerId: 'owner-2' };
    const serverText = 'В проекте нет руководителя — попросите владельца назначить';
    vi.mocked(api.sessions.create).mockRejectedValue(
      Object.assign(new Error(serverText), { status: 400, body: { code: 'no_project_persona', error: serverText } }),
    );

    const error = await createChatWithContextPersona(project, { mode: 'auto' }).catch(e => e);

    expect(api.personas.createChat).not.toHaveBeenCalled();
    expect(api.sessions.create).toHaveBeenCalledWith('proj-1', 'auto', undefined, undefined);
    expect(error).not.toBeInstanceOf(OnboardingRequiredError);
    expect((error as Error).message).toBe(serverText);
  });

  // Владелец проекта без руководителя — прежний путь: чат от лица личной персоны.
  it('владелец проекта без руководителя — создаёт чат от лица личной персоны, как раньше', async () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { id: 'proj-1', defaultPersonaId: null, ownerId: 'u1' };
    vi.mocked(api.personas.createChat).mockResolvedValue(fakeSession);

    const result = await createChatWithContextPersona(project, { mode: 'auto' });

    expect(result).toBe(fakeSession);
    expect(api.personas.createChat).toHaveBeenCalledWith('personal-default',
      { mode: 'auto', name: undefined, projectId: 'proj-1' });
    expect(api.sessions.create).not.toHaveBeenCalled();
  });

  // Тело без error (деградантный ответ транспорта): текст не выдумываем, оставляем message.
  it('400 no_project_persona без текста в теле — message исходной ошибки сохраняется', async () => {
    setMeFromServer(fakeMe(null));
    const project = { id: 'proj-1', defaultPersonaId: null, ownerId: 'owner-2' };
    vi.mocked(api.sessions.create).mockRejectedValue(
      Object.assign(new Error('Bad Request'), { status: 400, body: { code: 'no_project_persona' } }),
    );

    const error = await createChatWithContextPersona(project, { mode: 'auto' }).catch(e => e);

    expect(error).not.toBeInstanceOf(OnboardingRequiredError);
    expect((error as Error).message).toBe('Bad Request');
  });
});
