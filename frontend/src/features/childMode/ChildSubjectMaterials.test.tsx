// К5b: «Файлы предмета» — опись (тип/размер/дата), загрузка с перечитыванием, превью под
// строкой, «Распознаём страницу…» для картинки, удаление через подтверждение, отказ сервера
// его текстом, постоянная подсказка про решебники и гонка смены предмета.
// Окружение — node + linkedom, как в соседних .test.tsx волны; childApi.materials подменён
// по индексу вызова: без записи в answers запрос висит in-flight (на нём проверяются
// блокировка повторного вызова и отбрасывание устаревшего ответа).
// «Необработанных rejection нет» не слушается отдельно: неохваченный rejection сам краснит прогон.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ChildSubjectMaterial, ChildSubjectMaterialUploaded } from './types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile читает window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Ответ по индексу вызова каждого метода: Error — отказ с status/body, как его выносит
// request() из lib/offline; объект — немедленный ответ; нет записи — promise держится in-flight.
type Pending = { resolve: (v: unknown) => void; reject: (e: unknown) => void };
let listCalls: { userId: string; subjectId: string }[] = [];
let uploadCalls: { userId: string; subjectId: string; file: File }[] = [];
let removeCalls: { userId: string; subjectId: string; id: string }[] = [];
let listAnswers: (Error | unknown)[] = [];
let uploadAnswers: (Error | unknown)[] = [];
let removeAnswers: (Error | unknown)[] = [];
let listPending = new Map<number, Pending>();
let uploadPending = new Map<number, Pending>();
let removePending = new Map<number, Pending>();
let idx = { list: 0, upload: 0, remove: 0 };

function deferred(map: Map<number, Pending>, answers: (Error | unknown)[], i: number): Promise<unknown> {
  const a = answers[i];
  if (a instanceof Error) return Promise.reject(a);
  if (a !== undefined) return Promise.resolve(a);
  return new Promise((res, rej) => { map.set(i, { resolve: res, reject: rej }); });
}

vi.mock('./api', () => ({
  childApi: {
    materials: {
      list: (userId: string, subjectId: string) => {
        const i = idx.list++;
        listCalls.push({ userId, subjectId });
        return deferred(listPending, listAnswers, i);
      },
      upload: (userId: string, subjectId: string, file: File) => {
        const i = idx.upload++;
        uploadCalls.push({ userId, subjectId, file });
        return deferred(uploadPending, uploadAnswers, i);
      },
      remove: (userId: string, subjectId: string, id: string) => {
        const i = idx.remove++;
        removeCalls.push({ userId, subjectId, id });
        return deferred(removePending, removeAnswers, i);
      },
    },
  },
}));

const { ChildSubjectMaterials } = await import('./ChildSubjectMaterials');

const material = (over: Partial<ChildSubjectMaterial> = {}): ChildSubjectMaterial => ({
  id: 'm_1', name: 'Учебник.pdf', kind: 'pdf', bytes: 1_572_864, uploadedAt: '2026-03-12T12:00:00', ...over,
});
const uploaded = (over: Partial<ChildSubjectMaterialUploaded> = {}): ChildSubjectMaterialUploaded => ({
  ...material(over), preview: 'Правила сложения: слагаемые…, текст страницы',
});

// Даты фикстур — локальные (ISO без оффсета): new Date трактует их в поясе процесса, formatDate
// берёт те же локальные части — ожидаемая дата не зависит от TZ. С «Z» фикстура ломалась в поясах
// +13/+14: 12:00 UTC там уже следующий день.
const httpErr = (status: number, code: string, message: string) =>
  Object.assign(new Error('отказ'), { status, body: { code, message } });

// Файл в компоненте читаются только name и type (isImage + имя строки «идёт») — подменяем их.
const fakeFile = (name: string, type: string) => ({ name, type }) as unknown as File;

async function settle(fn: () => void) {
  await act(async () => {
    fn();
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

async function mount(userId = 'u-liza', subjectId = 's-math') {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(ChildSubjectMaterials, { userId, subjectId })); });
  return {
    host: host as HTMLElement,
    // Тот же смонтированный экземпляр с другим предметом: на этом проверяется сброс состояния
    // и отбрасывание ответа прежнего subjectId, а не перемонтирование.
    async setSubject(next: string) {
      await act(async () => { root.render(createElement(ChildSubjectMaterials, { userId, subjectId: next })); });
    },
  };
}

const panel = (host: HTMLElement) => host.querySelector('[data-testid="child-subject-materials"]') as HTMLElement;
const rows = (host: HTMLElement) => Array.from(panel(host).querySelectorAll('[data-testid="material-row"]')) as HTMLElement[];
const rowText = (host: HTMLElement, i: number) => rows(host)[i]?.textContent ?? '';
const noticeByTestId = (host: HTMLElement, testid: string) => panel(host).querySelector(`[data-testid="${testid}"]`) as HTMLElement | null;
const btn = (host: HTMLElement, testid: string) => panel(host).querySelector(`[data-testid="${testid}"]`) as HTMLButtonElement;
const delBtn = (host: HTMLElement, name: string) =>
  panel(host).querySelector(`button[aria-label="Удалить файл ${name}"]`) as HTMLButtonElement;

async function click(el: HTMLElement) {
  await act(async () => { el.dispatchEvent(new win.Event('click', { bubbles: true })); });
}

async function pickFile(host: HTMLElement, file: File) {
  const input = panel(host).querySelector('[data-testid="material-file-input"]') as HTMLInputElement;
  Object.defineProperty(input, 'files', { value: [file], configurable: true });
  await act(async () => { input.dispatchEvent(new win.Event('change', { bubbles: true })); });
}

beforeEach(() => {
  mobile = false;
  listCalls = []; uploadCalls = []; removeCalls = [];
  listAnswers = []; uploadAnswers = []; removeAnswers = [];
  listPending = new Map(); uploadPending = new Map(); removePending = new Map();
  idx = { list: 0, upload: 0, remove: 0 };
  doc.getElementById('root')!.innerHTML = '';
});

describe('ChildSubjectMaterials', () => {
  it('опись из двух материалов — две строки с типом, размером и датой; предупреждение видно', async () => {
    listAnswers[0] = [
      material(),
      material({ id: 'm_2', name: 'Задания по дробям', kind: 'text', bytes: 204_800, uploadedAt: '2026-03-13T12:00:00' }),
    ];
    const { host } = await mount();
    expect(listCalls).toEqual([{ userId: 'u-liza', subjectId: 's-math' }]);
    expect(rows(host)).toHaveLength(2);
    expect(rowText(host, 0)).toContain('Учебник.pdf');
    expect(rowText(host, 0)).toContain('PDF · 1,5 МБ · 12.03.2026');
    expect(rowText(host, 1)).toContain('TXT · 200 КБ · 13.03.2026');
    expect(panel(host).textContent).toContain('2 файла');
    expect(noticeByTestId(host, 'materials-warning')?.textContent).toContain('Не загружайте решебники');
  });

  it('пустая опись — «Файлов пока нет», предупреждение видно и при пустом списке', async () => {
    listAnswers[0] = [];
    const { host } = await mount();
    expect(noticeByTestId(host, 'materials-empty')?.textContent).toContain('Файлов пока нет');
    expect(noticeByTestId(host, 'materials-warning')?.textContent).toContain('Не загружайте решебники');
    expect(rows(host)).toHaveLength(0);
  });

  it('загрузка: upload → перечитывание list → строка и превью «Так репетитор прочтёт эту страницу» со сворачиванием', async () => {
    listAnswers[0] = [];
    const { host } = await mount();
    const file = fakeFile('конспект.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document');
    await pickFile(host, file);
    expect(uploadCalls).toEqual([{ userId: 'u-liza', subjectId: 's-math', file }]);
    const row = uploaded();
    await settle(() => { uploadPending.get(0)!.resolve(row); });
    await settle(() => { listPending.get(1)!.resolve([row]); });
    expect(listCalls).toHaveLength(2); // после успеха — повторный list
    expect(rows(host)).toHaveLength(1);
    const preview = noticeByTestId(host, 'material-preview');
    expect(preview?.textContent).toContain('Так репетитор прочтёт эту страницу');
    expect(noticeByTestId(host, 'material-preview-text')?.textContent).toContain('Правила сложения');
    await click(btn(host, 'preview-toggle'));
    expect(noticeByTestId(host, 'material-preview-text')).toBeNull(); // свёрнуто: текста нет, подпись остаётся
    expect(noticeByTestId(host, 'material-preview')?.textContent).toContain('Так репетитор прочтёт эту страницу');
    await click(btn(host, 'preview-toggle'));
    expect(noticeByTestId(host, 'material-preview-text')?.textContent).toContain('Правила сложения'); // развернуто
  });

  it('png с висящим ответом: «Распознаём страницу…» и блокировка повторной загрузки; после ответа — превью', async () => {
    listAnswers[0] = [];
    const { host } = await mount();
    await pickFile(host, fakeFile('страница.png', 'image/png'));
    expect(noticeByTestId(host, 'material-uploading')?.textContent).toContain('Распознаём страницу…');
    expect(btn(host, 'material-upload').disabled).toBe(true); // «идёт»: повтор заблокирован
    await pickFile(host, fakeFile('вторая.png', 'image/png')); // второй вызов во время запроса игнорируется
    expect(uploadCalls).toHaveLength(1);
    const row = uploaded({ id: 'm_img', name: 'страница.png', kind: 'image', bytes: 812_000 });
    await settle(() => { uploadPending.get(0)!.resolve(row); });
    await settle(() => { listPending.get(1)!.resolve([row]); });
    expect(noticeByTestId(host, 'material-uploading')).toBeNull();
    expect(rowText(host, 0)).toContain('IMG · 793 КБ');
    expect(noticeByTestId(host, 'material-preview-text')?.textContent).toContain('Правила сложения');
  });

  it('отказ 415 unsupported_type и 503 ocr_unavailable — Notice с текстом сервера как есть', async () => {
    listAnswers[0] = [];
    const { host } = await mount();
    await pickFile(host, fakeFile('архив.zip', 'application/zip'));
    await settle(() => { uploadPending.get(0)!.reject(httpErr(415, 'unsupported_type', 'Такой файл я не открою: нужен PDF, текст или фото страницы.')); });
    expect(noticeByTestId(host, 'material-reject')?.textContent).toContain('Такой файл я не открою: нужен PDF, текст или фото страницы.');
    expect(rows(host)).toHaveLength(0);
    await pickFile(host, fakeFile('страница.jpg', 'image/jpeg'));
    await settle(() => { uploadPending.get(1)!.reject(httpErr(503, 'ocr_unavailable', 'Распознавание страницы сейчас недоступно.')); });
    expect(noticeByTestId(host, 'material-reject')?.textContent).toContain('Распознавание страницы сейчас недоступно.');
  });

  it('отказ list — Notice с сообщением сервера, пустой описи при этом нет', async () => {
    listAnswers[0] = httpErr(500, 'storage_error', 'Хранилище недоступно.');
    const { host } = await mount();
    expect(noticeByTestId(host, 'materials-list-failed')?.textContent).toContain('Хранилище недоступно.');
    expect(noticeByTestId(host, 'materials-empty')).toBeNull();
  });

  it('удаление: подтверждение в строке → remove(id) → строки и её превью нет; «Отмена» ничего не удаляет', async () => {
    listAnswers[0] = [material(), material({ id: 'm_2', name: 'Черновик.txt', kind: 'text', bytes: 1024 })];
    const { host } = await mount();
    await click(delBtn(host, 'Черновик.txt'));
    const confirm = noticeByTestId(host, 'material-confirm');
    expect(confirm?.textContent).toContain('Удалить «Черновик.txt»?');
    await click(btn(host, 'material-remove-no'));
    expect(removeCalls).toHaveLength(0);
    expect(rows(host)).toHaveLength(2);
    await click(delBtn(host, 'Черновик.txt'));
    await click(btn(host, 'material-remove-yes'));
    expect(removeCalls).toEqual([{ userId: 'u-liza', subjectId: 's-math', id: 'm_2' }]);
    await settle(() => { removePending.get(0)!.resolve(undefined); });
    expect(rows(host)).toHaveLength(1);
    expect(rowText(host, 0)).toContain('Учебник.pdf');
  });

  it('второй «Удалить» во время запроса игнорируется', async () => {
    listAnswers[0] = [material(), material({ id: 'm_2', name: 'Черновик.txt', kind: 'text', bytes: 1024 })];
    const { host } = await mount();
    await click(delBtn(host, 'Учебник.pdf'));
    await click(btn(host, 'material-remove-yes')); // remove #0 in-flight
    await click(delBtn(host, 'Черновик.txt'));
    await click(btn(host, 'material-remove-yes'));
    expect(removeCalls).toHaveLength(1);
    await settle(() => { removePending.get(0)!.resolve(undefined); });
  });

  it('Materials_ListResponseAfterSubjectSwitch_Ignored: ответ list прежнего предмета не применяется', async () => {
    const { host, setSubject } = await mount('u-liza', 's-math'); // list #0 — in-flight
    await setSubject('s-phys'); // list #1 — in-flight; состояние s-math сброшено
    await settle(() => { listPending.get(0)!.resolve([material({ name: 'Учебник по математике.pdf' })]); });
    expect(rows(host)).toHaveLength(0); // ответ про s-math не доехал до экрана
    await settle(() => { listPending.get(1)!.resolve([material({ id: 'm_p', name: 'Физика. Оптика.txt', kind: 'text', bytes: 2048 })]); });
    expect(rowText(host, 0)).toContain('Физика. Оптика.txt');
    expect(panel(host).textContent).not.toContain('Учебник по математике.pdf');
  });

  it('ответ upload прежнего предмета не даёт ни строки, ни превью; смена предмета сбрасывает превью и ошибки', async () => {
    listAnswers[0] = [];
    const { host, setSubject } = await mount('u-liza', 's-math');
    await pickFile(host, fakeFile('конспект.pdf', 'application/pdf')); // upload #0 — in-flight
    await setSubject('s-phys'); // list #1 — in-flight; превью и строки s-math сброшены
    await settle(() => { uploadPending.get(0)!.resolve(uploaded({ name: 'конспект.pdf' })); });
    expect(noticeByTestId(host, 'material-preview')).toBeNull();
    expect(rows(host)).toHaveLength(0);
    expect(listCalls.map((c) => c.subjectId)).toEqual(['s-math', 's-phys']);
  });

  it('Materials_ABA_StaleFirstResponseIgnored: A→B→A — применяется только ответ последнего запуска', async () => {
    const { host, setSubject } = await mount('u-liza', 's-math'); // list #0 (A) — in-flight
    await setSubject('s-phys'); // list #1 (B) — in-flight
    await setSubject('s-math'); // list #2 (тот же A) — in-flight
    await settle(() => { listPending.get(0)!.resolve([material({ name: 'Учебник по математике.pdf' })]); });
    expect(rows(host)).toHaveLength(0); // поздний ответ первого A — тот же subjectId, но прежнее поколение
    await settle(() => { listPending.get(1)!.resolve([material({ id: 'm_p', name: 'Физика. Оптика.txt', kind: 'text', bytes: 2048 })]); });
    expect(rows(host)).toHaveLength(0); // ответ B к A тоже не применяется
    await settle(() => { listPending.get(2)!.resolve([material()]); });
    expect(rowText(host, 0)).toContain('Учебник.pdf'); // ответ последнего запуска — принят
    expect(panel(host).textContent).not.toContain('Учебник по математике.pdf');
  });

  it('Materials_ABA_StaleListErrorIgnored: отказ прежнего запуска list не показывает Notice', async () => {
    const { host, setSubject } = await mount('u-liza', 's-math'); // list #0 (A) — in-flight
    await setSubject('s-phys'); // list #1 (B) — in-flight
    await setSubject('s-math'); // list #2 (тот же A) — in-flight
    await settle(() => { listPending.get(0)!.reject(httpErr(500, 'storage_error', 'Хранилище недоступно.')); });
    expect(noticeByTestId(host, 'materials-list-failed')).toBeNull(); // ошибка старого запроса не на экране
    await settle(() => { listPending.get(2)!.resolve([material()]); });
    expect(rows(host)).toHaveLength(1);
    expect(noticeByTestId(host, 'materials-list-failed')).toBeNull();
  });

  it('фото без расширения в имени: isImage по типу файла, «Распознаём страницу…»', async () => {
    listAnswers[0] = [];
    const { host } = await mount();
    await pickFile(host, fakeFile('страница', 'image/png')); // имени «.png» нет — решение по type
    const uploading = noticeByTestId(host, 'material-uploading');
    expect(uploading?.textContent).toContain('Распознаём страницу…');
    expect(uploading?.textContent).not.toContain('достаю текст');
  });
});
