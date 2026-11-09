// S2c-F2b (п.27): конфликт сохранения в FileViewer — etag/If-Match и диалог SaveConflictDialog.
// Окружение — как FileViewer.test.ts: node + fakeDom + spyOn на api.files; обработители
// записи зовём через тестовый хук __test (UI для этого не нужен). Сам диалог (S2c-F1) НЕ
// подменён: его вид и тексты проверяет SaveConflictDialog.test.ts, здесь важна вязка
// FileViewer — данные конфликта, три действия и пауза автосейва draw.io.
import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FileViewer, __test } from '../FileViewer';
import { api } from '../../lib/api';
import { FileConflictError } from '../../features/files/api';
import type { Project } from '../../types';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr, reactProps, textNodes } from '../chat/__tests__/fakeDom';

vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
// В node navigator.onLine не определён → «офлайн», а в офлайне сохранение запрещено
vi.mock('../../hooks/useOnline', () => ({ useOnline: () => true }));
// signalr: ensureGit (wireRealtime) строит соединение и читает localStorage — в node падает
vi.mock('../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/home/an/proj',
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
};

// Тело 409 file_changed (К8): автор и источник — null (бэк заполнит с S3-3/S2d-4), диалог
// сам подставит «Другой участник».
const CONFLICT_BODY = {
  sha256: 'sha-other', content: 'their text', author: null,
  changedAt: '2026-11-01T09:00:00Z', source: null,
};
const conflictError = (over: Partial<typeof CONFLICT_BODY> = {}) => new FileConflictError({ ...CONFLICT_BODY, ...over });

let contentSpy: { mockResolvedValue: (v: unknown) => void; mock: { calls: unknown[][] } } | null = null;
let root: { unmount: () => void } | null = null;

function setupMocks() {
  vi.useFakeTimers();
  const store: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = String(v); },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
    key: () => null, get length() { return Object.keys(store).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  // Ответ getContent по умолчанию — С еtag'ом: редактируемый текст до 10 МиБ (С2c-F2a).
  // Тест «без etag» переопределяет его сам.
  contentSpy = vi.spyOn(api.files, 'getContent').mockResolvedValue({
    content: 'my text', isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: 7, etag: 'etag-1',
  }) as never;
  vi.spyOn(api.files, 'getDiff').mockResolvedValue({ diff: null });
  vi.spyOn(api.files, 'fileUrl').mockReturnValue('http://localhost:5000/files/x');
}

function teardown() {
  // Диалог — портал в document.body: без снятия он остался бы в поиске следующего теста
  act(() => { root?.unmount(); });
  root = null;
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
  contentSpy = null;
}

function mountViewer(filePath = 'docs/readme.txt') {
  installFakeDom();
  const r = mount(createElement(FileViewer, { project, filePath, onClose: vi.fn() }));
  root = r.root;
  return r;
}

// Прогнать microtasks (промисы api.files) + таймеры
async function flush() {
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
}

const bodyText = () => textNodes((globalThis as any).document.body).join('\n');
const dialogActions = () => findNodeByAttr((globalThis as any).document.body, 'data-testid', 'conflict-actions');
const diffBox = () => findNodeByAttr((globalThis as any).document.body, 'data-testid', 'conflict-diff');

// Кнопка диалога по подписи: текстовый узел → ближайший предок с onClick в reactProps (ui/Button)
function clickDialogAction(label: string): (() => void) | null {
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
  walk((globalThis as any).document.body);
  return hit;
}

// Открыть конфликт: смонтировать, позвать сохранение (409 → диалог). Возвращает spy сохранения.
async function openConflict(saveImpl?: (...args: any[]) => Promise<void>) {
  const save = vi.spyOn(api.files, 'saveContent').mockImplementation(saveImpl ?? (() => { throw conflictError(); }));
  mountViewer();
  await flush();
  const hooks = __test.writeHooks.current;
  expect(hooks, 'тестовый хук не установлен').not.toBeNull();
  await act(async () => { await hooks!.save(); });
  await flush();
  return save;
}

describe('FileViewer: etag и If-Match при сохранении (S2c-F2b)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('хранит etag прочитанной версии и шлёт If-Match; новый etag ответа — для следующего сохранения', async () => {
    const save = vi.spyOn(api.files, 'saveContent').mockImplementation(async (_p, _path, _c, _ifMatch, etagOut) => {
      if (etagOut) etagOut.etag = 'etag-2';
    });
    mountViewer();
    await flush();
    await act(async () => { await __test.writeHooks.current!.save(); });
    expect(save.mock.calls[0][3], 'If-Match — тег прочитанной версии').toBe('etag-1');
    // Ответ 200 принёс новый ETag (S2c-1) — следующее сохранение идёт против него
    await act(async () => { await __test.writeHooks.current!.save(); });
    expect(save.mock.calls[1][3], 'If-Match — новый etag из ответа').toBe('etag-2');
  });

  it('файл без etag (бинарный/сверх потолка/агентская ветка): без If-Match и без диалога', async () => {
    contentSpy!.mockResolvedValue({ content: 'my text', isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: 7 });
    const save = vi.spyOn(api.files, 'saveContent').mockResolvedValue(undefined);
    mountViewer();
    await flush();
    await act(async () => { await __test.writeHooks.current!.save(); });
    expect(save.mock.calls[0][3], 'без тега — If-Match нет').toBeUndefined();
    expect(dialogActions(), 'диалога конфликта нет').toBeNull();
  });
});

describe('FileViewer: диалог конфликта сохранения (S2c-F2b)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('409 file_changed: диалог с данными из ошибки — чужой текст в диффе, имя копии, «Другой участник»', async () => {
    (globalThis as any).localStorage.setItem('cc_display_name', 'Андрей');
    const save = await openConflict();
    expect(save, 'одно сохранение — один отказ').toHaveBeenCalledTimes(1);
    expect(dialogActions(), 'диалог конфликта открыт').not.toBeNull();
    const text = bodyText();
    expect(text).toContain('Файл изменили, пока вы правили');
    expect(text, 'author null — диалог сам подставил подпись').toContain('Другой участник');
    expect(text).toContain('readme.txt');
    expect(text, 'имя копии — из имени текущего пользователя').toContain('docs/readme (копия, Андрей).txt');
    const box = diffBox();
    expect(box, 'дифф «в проекте → ваша версия»').not.toBeNull();
    const diffText = textNodes(box as never).join('\n');
    expect(diffText).toContain('their text');
    expect(diffText).toContain('my text');
  });

  it('«Сохранить мою копией»: создаёт файл-копию рядом и не трогает оригинал', async () => {
    (globalThis as any).localStorage.setItem('cc_display_name', 'Андрей');
    const save = await openConflict();
    const create = vi.spyOn(api.files, 'createFile').mockResolvedValue(undefined);
    const btn = clickDialogAction('Сохранить мою копией');
    expect(btn, 'кнопка «копия» в диалоге').not.toBeNull();
    await act(async () => { btn!(); await flush(); });
    expect(create, 'копия — новый файл с моим текстом').toHaveBeenCalledWith('p1', 'docs/readme (копия, Андрей).txt', 'my text');
    expect(save, 'оригинал не перезаписан').toHaveBeenCalledTimes(1);
    expect(dialogActions(), 'конфликт закрыт').toBeNull();
  });

  it('ошибка создания копии: Notice, диалог остаётся, свой текст в редакторе', async () => {
    (globalThis as any).localStorage.setItem('cc_display_name', 'Андрей');
    await openConflict();
    const create = vi.spyOn(api.files, 'createFile').mockImplementation(() => { throw new Error('нет места'); });
    const btn = clickDialogAction('Сохранить мою копией');
    await act(async () => { btn!(); await flush(); });
    expect(create).toHaveBeenCalledTimes(1);
    expect(dialogActions(), 'диалог остался').not.toBeNull();
    expect(bodyText()).toContain('Копию сохранить не удалось');
  });

  it('«Заменить своей»: повторное сохранение с ifMatch = sha256 из 409', async () => {
    const save = await openConflict();
    save.mockReset();
    save.mockImplementation(async (_p, _path, _c, _ifMatch, etagOut) => { if (etagOut) etagOut.etag = 'sha-other'; });
    const btn = clickDialogAction('Заменить своей');
    expect(btn, 'кнопка «заменить» в диалоге').not.toBeNull();
    await act(async () => { btn!(); await flush(); });
    expect(save.mock.calls[0][2], 'пишется моя версия').toBe('my text');
    expect(save.mock.calls[0][3], 'If-Match — sha256 из тела 409').toBe('sha-other');
    expect(dialogActions(), 'конфликт закрыт').toBeNull();
  });

  it('повторный 409: Notice над диффом, диалог остаётся, следующий «заменить» — по новому sha256', async () => {
    const save = await openConflict();
    save.mockReset();
    save.mockImplementation(() => { throw conflictError({ sha256: 'sha-third', content: 'their newer text' }); });
    const btn = () => clickDialogAction('Заменить своей');
    await act(async () => { btn()!(); await flush(); });
    expect(save.mock.calls[0][3], 'первый повтор — по sha256 исходного 409').toBe('sha-other');
    expect(dialogActions(), 'диалог остался').not.toBeNull();
    expect(bodyText()).toContain('Файл изменили ещё раз');
    const box = diffBox();
    expect(box, 'дифф пересчитан по новому чужому тексту').not.toBeNull();
    expect(textNodes(box as never).join('\n')).toContain('their newer text');
    await act(async () => { btn()!(); await flush(); });
    expect(save.mock.calls[1][3], 'второй повтор — по sha256 свежего 409').toBe('sha-third');
  });

  it('«Взять чужую версию»: редактор и эталон версии — чужой текст и чужой тег', async () => {
    const save = await openConflict();
    const btn = clickDialogAction('Взять чужую версию');
    expect(btn, 'кнопка «взять чужую» в диалоге').not.toBeNull();
    await act(async () => { btn!(); await flush(); });
    expect(dialogActions(), 'конфликт закрыт').toBeNull();
    save.mockReset();
    save.mockImplementation(async (_p, _path, _c, _ifMatch, etagOut) => { if (etagOut) etagOut.etag = 'sha-next'; });
    await act(async () => { await __test.writeHooks.current!.save(); });
    expect(save.mock.calls[0][2], 'в редакторе теперь чужой текст').toBe('their text');
    expect(save.mock.calls[0][3], 'и против чужой же версии').toBe('sha-other');
  });
});

describe('FileViewer: draw.io — автосейв на паузе при открытом диалоге (S2c-F2b)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  const DIAGRAM = '<mxfile><diagram>a</diagram></mxfile>';

  it('первый автосейв даёт 409 → диалог без диффа; следующий автосейв под диалогом не пишется', async () => {
    contentSpy!.mockResolvedValue({ content: DIAGRAM, isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: DIAGRAM.length, etag: 'etag-d' });
    const save = vi.spyOn(api.files, 'saveContent').mockImplementation(() => { throw conflictError({ content: '<mxfile>b</mxfile>' }); });
    mountViewer('docs/diagram.drawio');
    await flush();
    expect(__test.diagramSave.current, 'тестовый хук автосейва не установлен').not.toBeNull();
    // Хук перечитываем на каждый вызов: как DrawioViewer (onSaveRef.current = onSave на
    // каждый рендер), автосейв всегда идёт самым свежим замыканием обработчика
    const diagram = (xml: string) => __test.diagramSave.current!(xml);
    await act(async () => { await diagram(DIAGRAM); });
    expect(save, 'первый автосейв дошёл до API').toHaveBeenCalledTimes(1);
    expect(dialogActions(), 'диалог конфликта открыт').not.toBeNull();
    // Диалог для draw.io — вариант без диффа (п.27): XML-разницу человек не читает
    expect(diffBox(), 'диффа нет').toBeNull();
    expect(bodyText()).toContain('ваша версия');
    // Пауза: пока человек выбирает, автосейв не плодит новых 409
    await act(async () => { await diagram('<mxfile><diagram>c</diagram></mxfile>'); });
    expect(save, 'автосейв под открытым диалогом не пишется').toHaveBeenCalledTimes(1);
  });
});
