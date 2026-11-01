// S2c-F3 (п.28): конфликт сохранения заметки в NoteView — etag/If-Match и тот же
// SaveConflictDialog, что у файлов (S2c-F2b). Окружение — как FileViewerSaveConflict.test.ts:
// node + fakeDom + spyOn на api.notes. Правку начинаем настоящим путём: клик «Править»,
// затем «Сохранить» (CodeMirror подменён заглушкой — сам редактор здесь не нужен).
// Диалог (S2c-F1) НЕ подменён: его вид и тексты проверяет SaveConflictDialog.test.ts,
// здесь важна вязка NoteView — данные конфликта, три действия, офлайн-путь.
import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NoteView } from '../NoteView';
import { api, NoteConflictError } from '../../../lib/api';
import { OfflineError, setOnline } from '../../../lib/offline';
import { bumpNotes } from '../../../lib/notes';
import { drainNotesOutbox } from '../../../lib/notesOffline';
import type { NoteDetail } from '../../../types';
import { installFakeDom, mount, findNodeByAttr, reactProps, textNodes, uninstallFakeDom } from '../../../components/chat/__tests__/fakeDom';

// signalr: DocComments зовёт ensurePersonasLoaded → wireRealtime строит реальное
// SignalR-соединение и читает localStorage — в node падает на резолве '/hubs/session'
vi.mock('../../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

// Мок idb — in-memory: офлайн-слой заметок (запись, индекс, очередь) должен работать
// по-настоящему, иначе проверка «офлайн-правка несёт свою базу» была бы вакуумом.
const { store } = vi.hoisted(() => ({
  store: { kv: new Map<string, any>(), notes: new Map<string, any>(), outbox: new Map<number, any>() },
}));
vi.mock('../../../lib/idb', () => ({
  idbGet: async (k: string) => store.kv.get(k),
  idbSet: async (k: string, v: unknown) => { store.kv.set(k, v); return 1; },
  idbDelete: async (k: string) => { store.kv.delete(k); return undefined; },
  noteContentGet: async (lk: string) => store.notes.get(lk),
  noteContentPut: async (r: { localKey: string }) => { store.notes.set(r.localKey, r); return 1; },
  noteContentDelete: async (lk: string) => { store.notes.delete(lk); return undefined; },
  noteContentAll: async () => [...store.notes.values()],
  notesOutboxAll: async () => [...store.outbox.values()],
  notesOutboxPut: async (op: { opId: number }) => { store.outbox.set(op.opId, op); return op.opId; },
  notesOutboxDelete: async (id: number) => { store.outbox.delete(id); return undefined; },
  // прочие экспорты idb в тестируемых путях не вызываются — заглушки
  idbCountRange: async () => 0, idbRangeStamps: async () => [], idbKeys: async () => [], idbClear: async () => {},
  outboxPut: async () => 1, outboxGet: async () => undefined, outboxDelete: async () => undefined, outboxAll: async () => [],
}));

// CodeMirror в node не поднимается; для проверки «что в редакторе» нужен только его value,
// и хук onChange — чтобы «набрать» свой текст, как это делает человек.
const { editorHooks } = vi.hoisted(() => ({ editorHooks: { current: null as ((v: string) => void) | null } }));
vi.mock('../NoteEditor', () => ({
  NoteEditor: ({ value, onChange }: { value?: string; onChange?: (v: string) => void }) => {
    editorHooks.current = (v: string) => onChange?.(v);
    return createElement('div', { 'data-testid': 'note-editor' }, value ?? '');
  },
}));

vi.mock('../../../lib/toast', () => ({ showToast: () => {} }));

const NOTE = (over: Partial<NoteDetail> = {}): NoteDetail => ({
  id: 'n1', title: 'Заметка', source: 'personal', sourceLabel: 'Личный', path: 'Заметка.md',
  content: 'my text', tags: [], links: [], backlinks: [], unlinkedMentions: [],
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-11-01T08:00:00Z',
  ...over,
});

// Тело 409 note_changed (К8): автор и источник — null (бэк заполнит с S3-3), диалог сам
// подставит «Другой participant»… — «Другой участник».
const CONFLICT_BODY = {
  sha256: 'sha-other', content: 'their text', author: null,
  changedAt: '2026-11-01T09:00:00Z', source: null,
};
const conflictError = (over: Partial<typeof CONFLICT_BODY> = {}) => new NoteConflictError({ ...CONFLICT_BODY, ...over });

let root: { unmount: () => void } | null = null;
let container: any = null;

function setupMocks() {
  vi.useFakeTimers();
  const ls: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in ls ? ls[k] : null),
    setItem: (k: string, v: string) => { ls[k] = String(v); },
    removeItem: (k: string) => { delete ls[k]; },
    clear: () => { for (const k of Object.keys(ls)) delete ls[k]; },
    key: () => null, get length() { return Object.keys(ls).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  store.kv.clear(); store.notes.clear(); store.outbox.clear();
  editorHooks.current = null;
  setOnline(true);
  // Реальный рендер NoteView (связи, задачи заметки, комментарии, список проектов) не
  // должен ходить в сеть: эти ответы — пустые, они не часть контракта шага.
  vi.spyOn(api.notes, 'list').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'folders').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'tasks').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'annotations').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'backlinks').mockResolvedValue([] as never);
  vi.spyOn(api.projects, 'list').mockResolvedValue([] as never);
  // DocComments зовёт ensurePersonasLoaded → reloadPersonas: без мока это живой запрос,
  // он падает OfflineError и даёт unhandled rejection в прогоне
  vi.spyOn(api.personas, 'list').mockResolvedValue([] as never);
}

function teardown() {
  // Диалог — портал в document.body: без снятия он остался бы в поиске следующего теста
  act(() => { root?.unmount(); });
  root = null; container = null;
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
}

function mountView(noteId = 'n1') {
  installFakeDom();
  const r = mount(createElement(NoteView, {
    noteId, existingTitles: new Set<string>(), onWikilink: () => {},
    onSelectNote: vi.fn(), onDeleted: vi.fn(),
  }));
  root = r.root; container = r.container;
  return r;
}

// Прогнать микротаски (промисы офлайн-слоя и api.notes) и таймеры
async function flush() {
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
}

const bodyText = () => textNodes((globalThis as any).document.body).join('\n');
const dialogActions = () => findNodeByAttr((globalThis as any).document.body, 'data-testid', 'conflict-actions');
const diffBox = () => findNodeByAttr((globalThis as any).document.body, 'data-testid', 'conflict-diff');
const editorNode = () => findNodeByAttr(container, 'data-testid', 'note-editor');
const editorValue = () => (editorNode() ? textNodes(editorNode() as never).join('') : null);

// Кнопка по подписи: текстовый узел → ближайший предок с onClick в reactProps (ui/Button,
// тулбарные button). Поиск — от контейнера NoteView (его тулбар) или от document.body
// (диалог живёт порталом).
function clickIn(from: any, label: string): (() => void) | null {
  let hit: (() => void) | null = null;
  const walk = (n: any): void => {
    if (hit) return;
    if (n.nodeType === 3 && n.nodeValue === label) {
      let p = n.parentNode;
      while (p) {
        const props = reactProps(p) as { onClick?: unknown } | null;
        if (props && typeof props.onClick === 'function') { hit = props.onClick as () => void; return; }
        p = p.parentNode;
      }
      return;
    }
    for (const c of n.childNodes ?? []) walk(c);
  };
  walk(from);
  return hit;
}
const clickToolbar = (label: string) => clickIn(container, label);
const clickDialog = (label: string) => clickIn((globalThis as any).document.body, label);

// Войти в правку и нажать «Сохранить» — тот путь, которым это делает человек.
// Возвращает замыкание последнего нажатия, чтобы менять его поведение в тесте.
async function startSaving(type?: string) {
  const edit = clickToolbar('Править');
  expect(edit, 'кнопка «Править» в тулбаре').not.toBeNull();
  await act(async () => { edit!(); await flush(); });
  if (type !== undefined) {
    expect(editorHooks.current, 'заглушка редактора не отдала onChange').not.toBeNull();
    await act(async () => { editorHooks.current!(type); await flush(); });
  }
  const save = clickToolbar('Сохранить');
  expect(save, 'кнопка «Сохранить» в тулбаре правки').not.toBeNull();
  await act(async () => { await save!(); });
  await flush();
}

// Открыть конфликт: прочитать заметку с тегом, сохранить (409 → диалог). Возвращает spy сохранения.
async function openConflict() {
  vi.spyOn(api.notes, 'get').mockResolvedValue(NOTE({ etag: 'etag-1' }) as never);
  const upd = vi.spyOn(api.notes, 'update').mockImplementation(() => { throw conflictError(); });
  mountView();
  await flush();
  await startSaving('my text');
  return upd;
}

describe('NoteView: etag и If-Match при сохранении (S2c-F3)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('хранит etag прочитанной версии и шлёт If-Match; новый etag из ответа — для следующего сохранения', async () => {
    // Перечитку списка держим «висящей»: иначе bumpNotes после записи вызвал бы перечитку
    // заметки, и «взят ли тег из ответа» проверялось бы по новому чтению, а не по ответу PUT
    vi.spyOn(api.notes, 'list').mockReturnValue(new Promise<never>(() => {}) as never);
    vi.spyOn(api.notes, 'get').mockResolvedValue(NOTE({ etag: 'etag-1' }) as never);
    const upd = vi.spyOn(api.notes, 'update').mockResolvedValue(NOTE({ etag: 'etag-2' }) as never);
    mountView();
    await flush();
    await startSaving('my new text');
    expect(upd.mock.calls[0][2], 'If-Match — тег прочитанной версии').toBe('etag-1');
    expect(upd.mock.calls[0][1].content, 'пишется набранный текст').toBe('my new text');
    await startSaving();
    expect(upd.mock.calls[1][2], 'If-Match — новый etag из ответа 200').toBe('etag-2');
  });

  it('заметка без etag (офлайн-кэш, черновик без базы): без If-Match и без диалога — ровно как раньше', async () => {
    vi.spyOn(api.notes, 'get').mockResolvedValue(NOTE() as never);
    const upd = vi.spyOn(api.notes, 'update').mockResolvedValue(NOTE() as never);
    mountView();
    await flush();
    await startSaving();
    expect(upd.mock.calls[0][2], 'без тега — If-Match нет').toBeUndefined();
    expect(dialogActions(), 'диалога конфликта нет').toBeNull();
  });

  it('событие notes_changed: в режиме правки текст и база черновика не трогаются, диалог не открывается', async () => {
    const get = vi.spyOn(api.notes, 'get').mockResolvedValue(NOTE({ etag: 'etag-1' }) as never);
    const upd = vi.spyOn(api.notes, 'update').mockResolvedValue(NOTE({ etag: 'etag-1' }) as never);
    mountView();
    await flush();
    const edit = clickToolbar('Править');
    await act(async () => { edit!(); await flush(); });
    // Заметку изменили под нами (realtime-событие → перечитка списка → перечитка открытой)
    get.mockResolvedValue(NOTE({ content: 'their text', updatedAt: '2026-11-01T09:00:00Z', etag: 'etag-2' }) as never);
    await act(async () => { bumpNotes(); await flush(); });
    expect(editorValue(), 'текст, который человек правит, не заменён').toBe('my text');
    expect(dialogActions(), 'диалог по событию не открывается — только по 409 при сохранении').toBeNull();
    const save = clickToolbar('Сохранить');
    await act(async () => { await save!(); });
    expect(upd.mock.calls[0][2], 'база черновика — прежняя версия, а не свежий тег').toBe('etag-1');
  });
});

describe('NoteView: диалог конфликта сохранения (S2c-F3)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('409 note_changed: диалог с данными из ошибки — чужой текст в диффе, имя копии, «Другой участник»', async () => {
    (globalThis as any).localStorage.setItem('cc_display_name', 'Андрей');
    const upd = await openConflict();
    expect(upd, 'одно сохранение — один отказ').toHaveBeenCalledTimes(1);
    expect(dialogActions(), 'диалог конфликта открыт').not.toBeNull();
    const text = bodyText();
    expect(text).toContain('Заметку изменили, пока вы правили');
    expect(text, 'author null — диалог сам подставил подпись').toContain('Другой участник');
    expect(text, 'имя копии — из имени текущего пользователя').toContain('Заметка (копия, Андрей)');
    const box = diffBox();
    expect(box, 'дифф «в проекте → ваша версия»').not.toBeNull();
    const diffText = textNodes(box as never).join('\n');
    expect(diffText).toContain('their text');
    expect(diffText).toContain('my text');
    expect(editorValue(), 'из правки не вышли: свой текст остался в редакторе').toBe('my text');
  });

  it('«Сохранить мою копией»: создаёт заметку-копию рядом в том же источнике и не трогает оригинал', async () => {
    (globalThis as any).localStorage.setItem('cc_display_name', 'Андрей');
    const upd = await openConflict();
    const create = vi.spyOn(api.notes, 'create').mockResolvedValue(NOTE({ id: 'n2', title: 'Заметка (копия, Андрей)' }) as never);
    const btn = clickDialog('Сохранить мою копией');
    expect(btn, 'кнопка «копия» в диалоге').not.toBeNull();
    await act(async () => { btn!(); await flush(); });
    expect(create, 'копия — новая заметка с моим текстом, тот же источник и папка').toHaveBeenCalledWith({
      title: 'Заметка (копия, Андрей)', content: 'my text', source: 'personal', folder: '',
    });
    expect(upd, 'оригинал не перезаписан').toHaveBeenCalledTimes(1);
    expect(dialogActions(), 'конфликт закрыт').toBeNull();
  });

  it('ошибка создания копии: Notice, диалог остаётся, свой текст в редакторе', async () => {
    (globalThis as any).localStorage.setItem('cc_display_name', 'Андрей');
    await openConflict();
    const create = vi.spyOn(api.notes, 'create').mockImplementation(() => { throw new Error('нет места'); });
    const btn = clickDialog('Сохранить мою копией');
    await act(async () => { btn!(); await flush(); });
    expect(create).toHaveBeenCalledTimes(1);
    expect(dialogActions(), 'диалог остался').not.toBeNull();
    expect(bodyText()).toContain('Копию сохранить не удалось');
    expect(editorValue(), 'свой текст не потерян').toBe('my text');
  });

  it('«Заменить своей»: повторное сохранение с ifMatch = sha256 из 409', async () => {
    const upd = await openConflict();
    upd.mockReset();
    upd.mockResolvedValue(NOTE({ etag: 'sha-other' }) as never);
    const btn = clickDialog('Заменить своей');
    expect(btn, 'кнопка «заменить» в диалоге').not.toBeNull();
    await act(async () => { btn!(); await flush(); });
    expect(upd.mock.calls[0][1].content, 'пишется моя версия').toBe('my text');
    expect(upd.mock.calls[0][2], 'If-Match — sha256 из тела 409').toBe('sha-other');
    expect(dialogActions(), 'конфликт закрыт').toBeNull();
  });

  it('повторный 409: Notice над диффом, диалог остаётся, следующий «заменить» — по новому sha256', async () => {
    const upd = await openConflict();
    upd.mockReset();
    upd.mockImplementation(() => { throw conflictError({ sha256: 'sha-third', content: 'their newer text' }); });
    const btn = () => clickDialog('Заменить своей');
    await act(async () => { btn()!(); await flush(); });
    expect(upd.mock.calls[0][2], 'первый повтор — по sha256 исходного 409').toBe('sha-other');
    expect(dialogActions(), 'диалог остался').not.toBeNull();
    // Notice повторного конфликта в общем диалоге пока говорит «Файл…» и для заметки
    // (отдельный дефект) — проверяется сама фраза, а не её существительное
    expect(bodyText()).toContain('изменили ещё раз');
    const box = diffBox();
    expect(textNodes(box as never).join('\n')).toContain('their newer text');
    await act(async () => { btn()!(); await flush(); });
    expect(upd.mock.calls[1][2], 'второй повтор — по sha256 свежего 409').toBe('sha-third');
  });

  it('«Взять чужую версию»: редактор и база — чужой текст и чужой тег', async () => {
    const upd = await openConflict();
    const btn = clickDialog('Взять чужую версию');
    expect(btn, 'кнопка «взять чужую» в диалоге').not.toBeNull();
    await act(async () => { btn!(); await flush(); });
    expect(dialogActions(), 'конфликт закрыт').toBeNull();
    expect(editorValue(), 'в редакторе теперь чужой текст').toBe('their text');
    upd.mockReset();
    upd.mockResolvedValue(NOTE({ content: 'their text', etag: 'sha-next' }) as never);
    const save = clickToolbar('Сохранить');
    await act(async () => { await save!(); });
    expect(upd.mock.calls[0][1].content, 'пишется взятая версия').toBe('their text');
    expect(upd.mock.calls[0][2], 'и против взятой же версии').toBe('sha-other');
  });
});

describe('NoteView: офлайн-правка заметки и её синхронизация (S2c-F3)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('офлайн правка остаётся локальной и несёт свою базу; синк пишет по If-Match, 409 — конфликт-копия', async () => {
    const get = vi.spyOn(api.notes, 'get').mockResolvedValue(NOTE({ etag: 'etag-1' }) as never);
    const upd = vi.spyOn(api.notes, 'update').mockImplementation(() => { throw new OfflineError(); });
    mountView();
    await flush();
    // Офлайн: правка не уходит на сервер и не «применяется потом молча» — она стоит в очереди
    await startSaving('my offline text');
    expect(upd, 'офлайн-сохранение дошло до отказа и ушло в очередь').toHaveBeenCalledTimes(1);
    const ops = [...store.outbox.values()];
    expect(ops.length, 'офлайн-правка стоит в очереди').toBe(1);
    expect(ops[0].kind, 'правка не потерялась').toBe('update');
    expect(ops[0].payload.content, 'в очереди — набранный текст').toBe('my offline text');
    expect(ops[0].baseEtag, 'операция несёт базой тег прочитанной версии').toBe('etag-1');
    // Связь поднялась: заметку под нами изменили → обычный путь с If-Match даёт 409,
    // правка уходит конфликт-копией, канонической остаётся серверная версия
    setOnline(true);
    upd.mockReset();
    upd.mockImplementation(() => { throw conflictError(); });
    get.mockResolvedValue(NOTE({ content: 'their text', etag: 'etag-2' }) as never);
    const create = vi.spyOn(api.notes, 'create').mockResolvedValue(NOTE({ content: 'their text', etag: 'etag-2' }) as never);
    await act(async () => { await drainNotesOutbox(); });
    expect(upd.mock.calls[0][2], 'синхронизация пишет по If-Match, а не поверх чужой правки молча').toBe('etag-1');
    expect(create.mock.calls[0][0].title, 'правка не потерялась: она уходит конфликт-копией').toContain('(conflict');
    expect(create.mock.calls[0][0].content, 'в копий — мой текст').toBe('my offline text');
  });
});
