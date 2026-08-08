import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  setMeFromServer, clearMe, getContextDefaultPersonaId, createChatWithContextPersona,
  OnboardingRequiredError,
} from '../defaultPersona';
import { setAllFlags, setFlagLocal, FLAGS } from '../featureFlags';
import { api } from '../api';
import type { Me, Project, Session } from '../../types';

// getContextDefaultPersonaId + createChatWithContextPersona (фича default-personas-onboarding,
// план §4.3): единый инвариант «чат только с персоной» на фронте. onMessage мокается — реальный
// SignalR-коннект не нужен и недоступен в node-окружении vitest.
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
    const project = { defaultPersonaId: 'project-lead' } as Pick<Project, 'defaultPersonaId'>;

    expect(getContextDefaultPersonaId(project)).toBe('project-lead');
  });

  it('проект без дефолта — падает на личный дефолт владельца', () => {
    setMeFromServer(fakeMe('personal-default'));
    const project = { defaultPersonaId: null } as Pick<Project, 'defaultPersonaId'>;

    expect(getContextDefaultPersonaId(project)).toBe('personal-default');
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
    setAllFlags({});
    clearMe();
  });

  it('флаг выключен — старый путь байт-в-байт, personaId не участвует даже при наличии дефолта', async () => {
    setMeFromServer(fakeMe('some-default'));
    vi.mocked(api.chats.create).mockResolvedValue(fakeSession);

    const result = await createChatWithContextPersona(null, { mode: 'auto', name: 'Чат' });

    expect(result).toBe(fakeSession);
    expect(api.chats.create).toHaveBeenCalledWith('auto', undefined, 'Чат');
    expect(api.personas.createChat).not.toHaveBeenCalled();
  });

  it('флаг включён, вне проекта, дефолт есть — создаёт чат от лица дефолт-персоны', async () => {
    setFlagLocal(FLAGS.defaultPersonasOnboarding, true);
    setMeFromServer(fakeMe('assistant-1'));
    vi.mocked(api.personas.createChat).mockResolvedValue(fakeSession);

    const result = await createChatWithContextPersona(null, { mode: 'auto' });

    expect(result).toBe(fakeSession);
    expect(api.personas.createChat).toHaveBeenCalledWith('assistant-1',
      { mode: 'auto', name: undefined, projectId: undefined });
    expect(api.chats.create).not.toHaveBeenCalled();
  });

  it('флаг включён, в проекте с руководителем — создаёт сессию проекта от его лица', async () => {
    setFlagLocal(FLAGS.defaultPersonasOnboarding, true);
    setMeFromServer(fakeMe('personal-default'));
    const project = { id: 'proj-1', defaultPersonaId: 'project-lead' } as Pick<Project, 'id' | 'defaultPersonaId'>;
    vi.mocked(api.personas.createChat).mockResolvedValue(fakeSession);

    await createChatWithContextPersona(project, { mode: 'acceptEdits' });

    expect(api.personas.createChat).toHaveBeenCalledWith('project-lead',
      { mode: 'acceptEdits', name: undefined, projectId: 'proj-1' });
    expect(api.sessions.create).not.toHaveBeenCalled();
  });

  it('флаг включён, дефолта нет (пустой) — идёт на старый путь без personaId, сервер провижнит сам', async () => {
    setFlagLocal(FLAGS.defaultPersonasOnboarding, true);
    setMeFromServer(fakeMe(null));
    vi.mocked(api.chats.create).mockResolvedValue(fakeSession);

    const result = await createChatWithContextPersona(null, { mode: 'auto' });

    expect(result).toBe(fakeSession);
    expect(api.chats.create).toHaveBeenCalledWith('auto', undefined, undefined);
    expect(api.personas.createChat).not.toHaveBeenCalled();
  });

  it('флаг включён, дефолта нет, сервер тоже не смог (400) — OnboardingRequiredError со scope user', async () => {
    setFlagLocal(FLAGS.defaultPersonasOnboarding, true);
    setMeFromServer(fakeMe(null));
    vi.mocked(api.chats.create).mockRejectedValue(Object.assign(new Error('нужна персона'), { status: 400 }));

    await expect(createChatWithContextPersona(null, { mode: 'auto' }))
      .rejects.toThrow(OnboardingRequiredError);
  });

  it('флаг включён, дефолта нет, проектный сценарий 400 — OnboardingRequiredError со scope project', async () => {
    setFlagLocal(FLAGS.defaultPersonasOnboarding, true);
    setMeFromServer(fakeMe(null));
    const project = { id: 'proj-1', defaultPersonaId: null } as Pick<Project, 'id' | 'defaultPersonaId'>;
    vi.mocked(api.sessions.create).mockRejectedValue(Object.assign(new Error('нужна персона'), { status: 400 }));

    const error = await createChatWithContextPersona(project, { mode: 'auto' }).catch(e => e);

    expect(error).toBeInstanceOf(OnboardingRequiredError);
    expect((error as InstanceType<typeof OnboardingRequiredError>).scope).toBe('project');
  });

  it('не-400 ошибка сервера прокидывается как есть, не превращается в OnboardingRequiredError', async () => {
    setFlagLocal(FLAGS.defaultPersonasOnboarding, true);
    setMeFromServer(fakeMe(null));
    const serverError = Object.assign(new Error('Сервер недоступен'), { status: 500 });
    vi.mocked(api.chats.create).mockRejectedValue(serverError);

    await expect(createChatWithContextPersona(null, { mode: 'auto' })).rejects.toBe(serverError);
  });
});
