// Стор «Архитектуры»: сохранения из iframe привязаны к своему фрейму и проекту (P1 ревью
// Viaduct 7/9), «Только просмотр» не пропускает правки в файл, а гидратация стора
// редактора в просмотре не считается правкой (P2). Хвостовое сообщение старого фрейма
// (pagehide → save) Chromium при удалении iframe сам не доставляет — поэтому оно
// подаётся здесь напрямую, как его доставил бы другой движок.
import { beforeEach, describe, expect, it, vi } from 'vitest';

const A = 'proj-a';
const B = 'proj-b';

const model = (name: string) => JSON.stringify({ state: { model: { systems: [{ id: 's1', name }] } }, version: 0 });

const api = vi.hoisted(() => ({
  architectureModel: vi.fn(),
  architectureSaveModel: vi.fn(),
  architectureGenerate: vi.fn(),
}));
// Стор берёт api из runtime-кита оболочки: мокаем кит целиком, иначе тест потянул бы
// весь граф кита (компоненты, сторы) и упал на точечном моке react ниже
vi.mock('aihome_shell/kit', () => ({ api: { projects: api } }));
// Хук стора вне React: отдаёт текущий снимок
vi.mock('react', () => ({ useSyncExternalStore: (_s: unknown, get: () => unknown) => get() }));

type Store = typeof import('../architectureStore');
let store: Store;

const flush = () => new Promise(r => setTimeout(r, 0));
const dto = (content: string | null, version: string | null) =>
  ({ exists: content !== null, content, version, updatedAt: null, updatedBy: null });

beforeEach(async () => {
  vi.resetModules();
  api.architectureModel.mockReset();
  api.architectureSaveModel.mockReset();
  api.architectureSaveModel.mockImplementation(async (_id: string, content: string) =>
    ({ version: 'v-' + content.length, updatedAt: null, updatedBy: null }));
  store = await import('../architectureStore');
});

async function open(projectId: string, content: string | null, version: string | null) {
  api.architectureModel.mockResolvedValueOnce(dto(content, version));
  await store.loadArchitecture(projectId);
  return { projectId, frameKey: current().frameKey };
}

const current = () => store.useArchitecture();

describe('architectureStore: сохранение привязано к фрейму и проекту', () => {
  it('отложенная правка проекта A после перехода к B не уходит в B', async () => {
    const stampA = await open(A, model('Alpha'), 'va');
    await open(B, null, null);

    store.onFrameSave(model('Alpha-EDIT'), stampA);
    await flush();

    expect(api.architectureSaveModel).not.toHaveBeenCalled();
    expect(current().projectId).toBe(B);
    expect(current().localValue).toBeNull();
  });

  it('«Перезагрузить» в конфликте: хвост старого фрейма не затирает взятую версию', async () => {
    const stamp = await open(A, model('Alpha'), 'va');
    api.architectureSaveModel.mockRejectedValueOnce(Object.assign(new Error('409'), {
      status: 409, body: { current: { content: model('Чужая'), version: 'vb', updatedAt: null, updatedBy: 'Персона' } },
    }));
    store.onFrameSave(model('Моя правка'), stamp);
    await flush(); await flush();
    expect(current().conflict).not.toBeNull();

    store.takeServerVersion();
    api.architectureSaveModel.mockClear();
    store.onFrameSave(model('Моя правка 2'), stamp); // хвост pagehide старого фрейма
    await flush();

    expect(api.architectureSaveModel).not.toHaveBeenCalled();
    expect(current().content).toBe(model('Чужая'));
    expect(current().version).toBe('vb');

    // Правка нового фрейма — пишется, от взятой версии
    store.onFrameSave(model('Поверх чужой'), { projectId: A, frameKey: current().frameKey });
    await flush();
    expect(api.architectureSaveModel).toHaveBeenCalledWith(A, expect.any(String), 'vb');
  });

  it('сохранение B, пришедшее во время PUT проекта A, не теряется', async () => {
    const stampA = await open(A, model('Alpha'), 'va');
    let resolveA!: (v: unknown) => void;
    api.architectureSaveModel.mockImplementationOnce(() => new Promise(r => { resolveA = r; }));
    store.onFrameSave(model('Alpha-EDIT'), stampA);
    await flush();
    expect(api.architectureSaveModel).toHaveBeenCalledTimes(1);

    const stampB = await open(B, model('Beta'), 'vb');
    store.onFrameSave(model('Beta-EDIT'), stampB); // pump выходит по inflight
    resolveA({ version: 'va2', updatedAt: null, updatedBy: null });
    await flush(); await flush();

    expect(api.architectureSaveModel).toHaveBeenCalledTimes(2);
    expect(api.architectureSaveModel).toHaveBeenLastCalledWith(B, expect.stringContaining('Beta-EDIT'), 'vb');
    expect(current().save).toBe('saved');
    expect(current().dirty).toBe(false);
  });
});

describe('architectureStore: внешнее изменение и удаление файла модели', () => {
  it('push с путём модели: откат файла подхватывается тихо, чужие пути сверку не зовут', async () => {
    const stamp = await open(A, model('Правка'), 'v2');

    store.onProjectFilesChanged(A, ['src/Program.cs']);
    expect(api.architectureModel).toHaveBeenCalledTimes(1);

    api.architectureModel.mockResolvedValueOnce(dto(model('Как в git'), 'v1'));
    store.onProjectFilesChanged(A, [store.MODEL_PATH]);
    await flush();

    expect(current().content).toBe(model('Как в git'));
    expect(current().version).toBe('v1');
    expect(current().conflict).toBeNull();
    expect(current().frameKey).toBe(stamp.frameKey + 1);
  });

  it('полная пересинхронизация (full) тоже зовёт сверку', async () => {
    await open(A, model('Alpha'), 'va');
    api.architectureModel.mockResolvedValueOnce(dto(model('Alpha'), 'va'));
    store.onProjectFilesChanged(A, [], true);
    await flush();
    expect(api.architectureModel).toHaveBeenCalledTimes(2);
  });

  it('файл удалили: без своих правок — «модели нет», холст старого фрейма файл не воскрешает', async () => {
    const stamp = await open(A, model('Alpha'), 'va');

    api.architectureModel.mockResolvedValueOnce(dto(null, null));
    store.onProjectFilesChanged(A, [store.MODEL_PATH]);
    await flush();

    expect(current().status).toBe('missing');
    expect(current().content).toBeNull();
    expect(current().version).toBeNull();

    // Хвост снятого фрейма (pagehide) — не пишется
    store.onFrameSave(model('Alpha-EDIT'), stamp);
    await flush();
    expect(api.architectureSaveModel).not.toHaveBeenCalled();
  });

  it('файл удалили при своих правках — плашка конфликта, а «Перезагрузить» ведёт в «модели нет»', async () => {
    const stamp = await open(A, model('Alpha'), 'va');
    store.onFrameDirty(stamp);

    api.architectureModel.mockResolvedValueOnce(dto(null, null));
    await store.checkRemote(A);
    expect(current().conflict).toMatchObject({ content: null, version: null });

    store.onFrameSave(model('Моя правка'), stamp);
    await flush();
    expect(api.architectureSaveModel).not.toHaveBeenCalled();

    store.takeServerVersion();
    expect(current().status).toBe('missing');
    expect(current().version).toBeNull();
  });

  it('сверка, начатая до своего сохранения, не откатывает холст к прежней версии', async () => {
    const stamp = await open(A, model('Alpha'), 'va');
    let resolveGet!: (v: unknown) => void;
    api.architectureModel.mockImplementationOnce(() => new Promise(r => { resolveGet = r; }));
    const check = store.checkRemote(A);

    store.onFrameSave(model('Моя правка'), stamp);
    await flush(); await flush();
    const saved = current().version;
    expect(saved).not.toBe('va');

    // Ответ сверки прочитал файл ДО записи
    resolveGet(dto(model('Alpha'), 'va'));
    await check;

    expect(current().version).toBe(saved);
    expect(current().frameKey).toBe(stamp.frameKey);
    expect(current().conflict).toBeNull();
  });

  it('две сверки подряд: поздний ответ ранней не откатывает холст к её версии', async () => {
    await open(A, model('Alpha'), 'va');
    let resolveFirst!: (v: unknown) => void;
    api.architectureModel.mockImplementationOnce(() => new Promise(r => { resolveFirst = r; }));
    const first = store.checkRemote(A);
    api.architectureModel.mockResolvedValueOnce(dto(model('V2'), 'v2'));
    await store.checkRemote(A);
    expect(current().version).toBe('v2');

    // Ранняя сверка прочитала файл до второй правки и ответила последней
    resolveFirst(dto(model('V1'), 'v1'));
    await first;

    expect(current().version).toBe('v2');
    expect(current().content).toBe(model('V2'));
  });

  it('загрузка в полёте + push: сверка упала, загрузка успешна — раздел не висит в «загрузке»', async () => {
    let resolveLoad!: (v: unknown) => void;
    api.architectureModel.mockImplementationOnce(() => new Promise(r => { resolveLoad = r; }));
    const load = store.loadArchitecture(A);
    expect(current().status).toBe('loading');
    api.architectureModel.mockRejectedValueOnce(new Error('сеть'));
    store.onProjectFilesChanged(A, ['docs/architecture/model.viaduct.json']);
    await flush();

    // Ответ загрузки отброшен по эпохе — его обогнала сверка
    resolveLoad(dto(model('Alpha'), 'va'));
    await load;

    expect(current().status).toBe('error');
    expect(current().error).toBe('сеть');
  });

  it('поздний ответ загрузки не откатывает холст к версии, которую обогнала сверка', async () => {
    await open(A, model('Alpha'), 'va');
    let resolveLoad!: (v: unknown) => void;
    api.architectureModel.mockImplementationOnce(() => new Promise(r => { resolveLoad = r; }));
    const load = store.loadArchitecture(A, true);
    api.architectureModel.mockResolvedValueOnce(dto(model('V2'), 'v2'));
    await store.checkRemote(A);
    expect(current().version).toBe('v2');
    const frameKey = current().frameKey;

    // Загрузка прочитала файл до чужой правки и ответила последней
    resolveLoad(dto(model('Alpha'), 'va'));
    await load;

    expect(current().version).toBe('v2');
    expect(current().content).toBe(model('V2'));
    expect(current().frameKey).toBe(frameKey);
  });

  it('push с путём модели в другом регистре и с обратными слешами — сверка идёт', async () => {
    await open(A, model('Alpha'), 'va');
    api.architectureModel.mockResolvedValueOnce(dto(model('Как в git'), 'v1'));
    store.onProjectFilesChanged(A, ['Docs\\Architecture\\Model.Viaduct.json']);
    await flush();

    expect(api.architectureModel).toHaveBeenCalledTimes(2);
    expect(current().version).toBe('v1');
  });
});

describe('architectureStore: «Только просмотр»', () => {
  it('правка в просмотре не пишется, а на выходе холст пересоздаётся из файла', async () => {
    const stamp = await open(A, model('Alpha'), 'va');
    store.setReadOnly(true);

    store.onFrameSave(model('Правка в просмотре'), stamp);
    await flush();
    expect(api.architectureSaveModel).not.toHaveBeenCalled();

    store.setReadOnly(false);
    expect(current().frameKey).toBe(stamp.frameKey + 1);
    expect(current().localValue).toBeNull();
    expect(current().dirty).toBe(false);

    // Хвост старого фрейма после выхода из просмотра тоже не пишется
    store.onFrameSave(model('Правка в просмотре'), stamp);
    await flush();
    expect(api.architectureSaveModel).not.toHaveBeenCalled();
  });

  it('без расхождения выход из просмотра холст не пересоздаёт', async () => {
    const stamp = await open(A, model('Alpha'), 'va');
    store.setReadOnly(true);
    store.onFrameSave(model('Alpha'), stamp);
    store.setReadOnly(false);
    expect(current().frameKey).toBe(stamp.frameKey);
  });

  it('мобила: гидратация в просмотре — не правка, чужая версия подхватывается тихо', async () => {
    const stamp = await open(A, model('Alpha'), 'va');
    store.setReadOnly(true);

    // Стор редактора переписал ту же модель в другом форматировании
    store.onFrameSave(JSON.stringify(JSON.parse(model('Alpha')), null, 4), stamp);
    expect(current().dirty).toBe(false);

    api.architectureModel.mockResolvedValueOnce(dto(model('Чужая'), 'vb'));
    await store.checkRemote(A);

    expect(current().conflict).toBeNull();
    expect(current().content).toBe(model('Чужая'));
  });

  it('мобила: правка в просмотре не своя — чужая версия подхватывается тихо', async () => {
    const stamp = await open(A, model('Alpha'), 'va');
    store.setReadOnly(true);

    store.onFrameDirty(stamp);
    store.onFrameSave(model('Правка в просмотре'), stamp);
    expect(current().dirty).toBe(false);

    api.architectureModel.mockResolvedValueOnce(dto(model('Чужая'), 'vb'));
    await store.checkRemote(A);

    expect(api.architectureSaveModel).not.toHaveBeenCalled();
    expect(current().conflict).toBeNull();
    expect(current().content).toBe(model('Чужая'));
    expect(current().frameKey).toBe(stamp.frameKey + 1);
  });
});

describe('architectureStore: «Собрать архитектуру» с агентом', () => {
  const pass1 = { modelPath: 'm', graphBuiltAt: null, generatedAt: 't', containers: 1, components: 0, added: 1, matched: 0, connectionsAdded: 0 };
  const reject = (status: number, body: object) => Object.assign(new Error(String(status)), { status, body });

  it('200 с агентом: сводка и задача исполнителя, withAgent уходит в запрос', async () => {
    await open(A, null, null);
    api.architectureGenerate.mockResolvedValueOnce({ ...pass1, agentTaskId: 't1', agentPersonaId: null, agentError: null });
    api.architectureModel.mockResolvedValueOnce(dto(model('Alpha'), 'va'));
    await store.generateArchitecture(A, true);

    expect(api.architectureGenerate).toHaveBeenLastCalledWith(A, true);
    expect(current().status).toBe('ready');
    expect(current().lastBuild?.added).toBe(1);
    expect(current().agent).toEqual({ taskId: 't1', personaId: null, error: null });
    expect(current().generateError).toBeNull();
  });

  it('409 build_in_progress без result: модель не трогаем, ошибки нет, задача известна', async () => {
    await open(A, model('Alpha'), 'va');
    api.architectureGenerate.mockRejectedValueOnce(reject(409, { code: 'build_in_progress', agentTaskId: 't0', result: null }));
    await store.generateArchitecture(A, true);

    expect(current().generating).toBe(false);
    expect(current().generateError).toBeNull();
    expect(current().agent).toEqual({ taskId: 't0', personaId: null, error: 'build_in_progress' });
    expect(current().content).toBe(model('Alpha'));
  });

  it('503 agent_unavailable с result: проход 1 применён как успех со сноской', async () => {
    await open(A, null, null);
    api.architectureGenerate.mockRejectedValueOnce(reject(503, { code: 'agent_unavailable', result: pass1 }));
    api.architectureModel.mockResolvedValueOnce(dto(model('Alpha'), 'va'));
    await store.generateArchitecture(A, true);

    expect(current().status).toBe('ready');
    expect(current().generateError).toBeNull();
    expect(current().lastBuild?.added).toBe(1);
    expect(current().agent?.error).toBe('agent_unavailable');
  });

  it('503 graph_unavailable — по-прежнему ошибка сборки', async () => {
    await open(A, null, null);
    api.architectureGenerate.mockRejectedValueOnce(reject(503, { code: 'graph_unavailable', message: 'Граф кода не построился' }));
    await store.generateArchitecture(A);

    expect(current().generateError).toBe('Граф кода не построился');
    expect(current().agent).toBeNull();
  });

  it('ошибка пересборки готовой модели: модель цела, прежняя сводка сброшена, крестик убирает ошибку', async () => {
    await open(A, null, null);
    api.architectureGenerate.mockResolvedValueOnce(pass1);
    api.architectureModel.mockResolvedValueOnce(dto(model('Alpha'), 'va'));
    await store.generateArchitecture(A);
    expect(current().lastBuild?.added).toBe(1);

    api.architectureGenerate.mockRejectedValueOnce(reject(503, { code: 'graph_unavailable', message: 'Граф кода не построился' }));
    await store.generateArchitecture(A);
    expect(current().status).toBe('ready');
    expect(current().content).toBe(model('Alpha'));
    expect(current().generateError).toBe('Граф кода не построился');
    expect(current().lastBuild).toBeNull();

    store.dismissBuildSummary();
    expect(current().generateError).toBeNull();
  });

  it('409 build_in_progress без result после прежней сборки: сводки нет', async () => {
    await open(A, null, null);
    api.architectureGenerate.mockResolvedValueOnce(pass1);
    api.architectureModel.mockResolvedValueOnce(dto(model('Alpha'), 'va'));
    await store.generateArchitecture(A);

    api.architectureGenerate.mockRejectedValueOnce(reject(409, { code: 'build_in_progress', agentTaskId: 't0', result: null }));
    await store.generateArchitecture(A, true);
    expect(current().lastBuild).toBeNull();
    expect(current().agent?.error).toBe('build_in_progress');
  });
});
