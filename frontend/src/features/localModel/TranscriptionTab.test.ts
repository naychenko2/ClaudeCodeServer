// Отрисовка вкладки «Встречи» (Ф3 + Ф4): порядок блоков, баннер по уровню, пустое
// состояние «Сейчас», скелеты без снимка — статическим рендером (паттерн EngineCard/
// hardware.test). Цвета гашения/тревоги и раскрытие очереди — mount на fakeDom по
// текстовым узлам (мутации, которые обязаны покрасить):
// - баннер рисовать при любом level (краснеет «в норме баннера нет»);
// - гашение не привязывать к stale (краснеют цвета «Завис?»);
// - скелеты заменить нулями (краснеет тест загрузки);
// - порядок блоков переставить (краснеет тест порядка);
// - карточку сбоев рисовать при пустом fails (краснеет тест «сбоев нет»);
// - фильтр строк сверки брать «любые числа» (краснеет тест inDify=null: появилась бы
//   плашка «не хватает» из неизвестного);
// - «Показать ещё» не раскрывать список (краснеет тест шестой строки);
// - делать ссылкой строку без mdPath (краснеет тест openMeetingNote: вызов на
//   неоткрываемой строке или лишний вызов);
// - не рисовать строку «запись» (краснеет её тест; значение — «—», не 0);
// - красить «Пишет в» только без баннера дефицита (краснеет тест combined: при чужом
//   узле вывода и дефиците Dify нужны оба);
// - не гасить «запись» при «Завис?» (макет .stale .facts b гасит все значения строки
//   фактов, красный только «идёт») — краснеет тест «Завис?».

import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import { TranscriptionTab } from './TranscriptionTab';
import { openMeetingNote } from './openMeetingNote';
import type { TranscriptionSnapshot } from './types';

// Точку входа открытия сводки подменяем шпионом: тест проверяет сам вызов и его аргумент,
// а не тост заглушки (openMeetingNote.ts). meetingId и остальное — настоящие.
vi.mock('./openMeetingNote', async (importOriginal) => {
  const real = await importOriginal<typeof import('./openMeetingNote')>();
  return { ...real, openMeetingNote: vi.fn() };
});
const openMeetingNoteMock = vi.mocked(openMeetingNote);
beforeEach(() => { openMeetingNoteMock.mockClear(); });

const NOW = 1_763_460_000_000;

function snap(over: Partial<TranscriptionSnapshot> = {}): TranscriptionSnapshot {
  return {
    phase: 'batch', updatedAt: NOW - 8_000, host: 'worker', pid: 41233, alive: true,
    outputHost: 'worker', outputDir: '~/meetings', level: 'ok',
    file: '2026-11-18 Обсуждение бюджета.mp4', folder: 'Video-GPB', stage: 'asr',
    stageStartedAt: NOW - 192_000, durationMin: 3.2, batchIndex: 2, batchSize: 5,
    asrQueue: [
      { file: '2026-11-18 Стендап.mp4', folder: 'Video-GPB', since: NOW - 360_000 },
      { file: '2026-11-18 Встреча с юристами.mp4', folder: 'Video-SIAM', since: NOW - 600_000 },
    ],
    summaryQueue: 1,
    fails: [],
    recent: [{ at: NOW - 3_600_000, folder: 'Video-GPB', file: '2026-11-18 планёрка.md' }],
    dify: [{ folder: 'Video-GPB', local: 128, inDify: 128 }],
    deps: [{ key: 'asr', state: 'ok', checkedAt: NOW }],
    ...over,
  };
}

const staticHtml = (data: TranscriptionSnapshot | null) =>
  renderToStaticMarkup(createElement(TranscriptionTab, { data, now: NOW }));

// === Цвет по текстовому узлу (mount на fakeDom): текст → его элемент → style.color.
// Точное совпадение nodeValue: «пачка 2 из 5» не подменится под поиск «2». ===
type Style = Record<string, string>;
function styleOfText(root: { childNodes: unknown[] }, text: string): Style | null {
  let hit: Style | null = null;
  const walk = (x: { nodeType?: number; nodeValue?: string; childNodes: unknown[]; style?: Style }) => {
    if (hit) return;
    if (x.nodeType === 3 && x.nodeValue === text) {
      const parent = (x as { parentNode?: { style?: Style } }).parentNode;
      hit = parent?.style ?? null;
      return;
    }
    for (const c of x.childNodes ?? []) walk(c as Parameters<typeof walk>[0]);
  };
  walk(root as Parameters<typeof walk>[0]);
  return hit;
}

const mountTab = (data: TranscriptionSnapshot | null) =>
  mount(createElement(TranscriptionTab, { data, now: NOW }));

beforeAll(() => { installFakeDom(); });
afterAll(() => { uninstallFakeDom(); });

describe('TranscriptionTab: норма (снимка тревоги нет)', () => {
  it('баннера нет; «Конвейер» → «Сейчас» → «Очередь» сверху вниз', () => {
    const h = staticHtml(snap());
    expect(h).not.toContain('role="status"');
    const iPipe = h.indexOf('>Конвейер<');
    const iNow = h.indexOf('>Сейчас<');
    const iQueue = h.indexOf('>Очередь<');
    expect(iPipe).toBeGreaterThanOrEqual(0);
    expect(iNow).toBeGreaterThan(iPipe);
    expect(iQueue).toBeGreaterThan(iNow);
  });

  it('плашка «Идёт пачка», «worker · ~/meetings» без «не та машина», «пачка 2 из 5»', () => {
    const h = staticHtml(snap());
    expect(h).toContain('Идёт пачка');
    expect(h).toContain('worker · ~/meetings');
    expect(h).not.toContain('не та машина');
    expect(h).toContain('пачка 2 из 5');
    expect(h).toContain('Распознавание');
    expect(h).toContain('Сводка');
    expect(h).toContain('В Dify');
  });

  it('файл, «идёт 3 мин 12 с», «запись —» и счётчики очереди — обычными цветами (не гашены, не красные)', () => {
    const m = mountTab(snap());
    expect(styleOfText(m.container, '2026-11-18 Обсуждение бюджета.mp4')?.color).toBe('var(--c-text-heading)');
    expect(styleOfText(m.container, '3 мин 12 с')?.color).toBe('var(--c-text-heading)');
    // строка «запись» есть всегда, значения в контракте нет — «—», не 0
    expect(styleOfText(m.container, '—')?.color).toBe('var(--c-text-heading)');
    expect(styleOfText(m.container, '2')?.color).toBe('var(--c-text-heading)'); // asrCount
    expect(styleOfText(m.container, '1')?.color).toBe('var(--c-text-heading)'); // sumCount
    expect(styleOfText(m.container, '8 с назад')?.color).toBe('var(--c-text-primary)');
  });

  it('список очереди — только после раскрытия; там файл и «ждёт 6 минут»', () => {
    const m = mountTab(snap());
    const btn = m.container.querySelector('[role="button"]');
    expect(btn).not.toBeNull();
    expect(styleOfText(m.container, '2026-11-18 Стендап.mp4')).toBeNull();
    // PressableArea держит на div обычный onClick (это не проп компонента)
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(styleOfText(m.container, '2026-11-18 Стендап.mp4')).not.toBeNull();
    expect(styleOfText(m.container, 'ждёт 6 минут')).not.toBeNull();
  });
});

describe('TranscriptionTab: «Завис?» (level danger при живом процессе)', () => {
  const hung = () => snap({ level: 'danger', updatedAt: NOW - 23 * 60_000 });

  it('баннер тревоги с текстом по proposal (форма «23 мин»); плашка «Завис?»', () => {
    const h = staticHtml(hung());
    expect(h).toContain('role="status"');
    expect(h).toContain('Конвейер не отчитывался 23 мин');
    expect(h).toContain('Завис?');
  });

  it('«Сейчас» гаснет до C.textMuted, а «идёт» краснеет', () => {
    const m = mountTab(hung());
    expect(styleOfText(m.container, '2026-11-18 Обсуждение бюджета.mp4')?.color).toBe('var(--c-text-muted)');
    expect(styleOfText(m.container, '3 мин 12 с')?.color).toBe('var(--c-danger-text)');
    // «запись» гасится вместе со всеми значениями строки фактов (макет .stale .facts b),
    // в отличие от «идёт» — он красный
    expect(styleOfText(m.container, '—')?.color).toBe('var(--c-text-muted)');
  });

  it('значения очереди гаснут', () => {
    const m = mountTab(hung());
    expect(styleOfText(m.container, '2')?.color).toBe('var(--c-text-muted)');
    expect(styleOfText(m.container, '1')?.color).toBe('var(--c-text-muted)');
  });

  it('карточка конвейера НЕ гасится: «Обновлено» краснеет, «Где работает» остаётся обычным', () => {
    const m = mountTab(hung());
    expect(styleOfText(m.container, '23 минуты назад')?.color).toBe('var(--c-danger-text)');
    expect(styleOfText(m.container, 'worker')?.color).toBe('var(--c-text-primary)');
  });
});

describe('TranscriptionTab: «Пишет в» не туда (18.11: чужой узел вывода)', () => {
  // Как это приходит с бэка: OutputHost=null при известном outputDir (каталог вывода не
  // локальный) и level=warn по дефициту Dify — TranscriptionStateReader.cs:91-97
  const foreign = () => snap({
    level: 'warn', outputHost: null, outputDir: '/srv/meetings/Встречи',
    dify: [{ folder: 'Video-SIAM', local: 41, inDify: 37 }],
  });

  it('дефицит Dify и чужой узел — баннер И жёлтая строка «Пишет в», а не вместо неё', () => {
    const h = staticHtml(foreign());
    expect(h).toContain('4 встречи не попали в Dify');
    expect(h).toContain('Конвейер пишет не на этом узле, а «Встречи» читают с worker.');
    expect(h).toContain('/srv/meetings/Встречи — не та машина');
    const m = mountTab(foreign());
    expect(styleOfText(m.container, '/srv/meetings/Встречи — не та машина')?.color).toBe('var(--c-warning-text)');
  });

  it('чужой узел без дефицита — жёлтая строка есть, баннера нет', () => {
    const noDeficit = snap({ level: 'warn', outputHost: null, outputDir: '/srv/meetings/Встречи' });
    expect(staticHtml(noDeficit)).not.toContain('role="status"');
    const m = mountTab(noDeficit);
    expect(styleOfText(m.container, '/srv/meetings/Встречи — не та машина')?.color).toBe('var(--c-warning-text)');
  });

  it('свой узел вывода — строка обычным цветом, «не та машина» нет', () => {
    const h = staticHtml(snap());
    expect(h).not.toContain('не та машина');
    expect(styleOfText(mountTab(snap()).container, 'worker · ~/meetings')?.color).toBe('var(--c-text-primary)');
  });
});

describe('TranscriptionTab: пустое состояние, остановка, загрузка', () => {
  it('файла нет — «Ничего не обрабатывается» с подсказкой про сетевую папку', () => {
    const h = staticHtml(snap({ phase: 'idle', file: null, stage: null, asrQueue: [], summaryQueue: 0 }));
    expect(h).toContain('Ничего не обрабатывается');
    expect(h).toContain('конвейер подхватит сам');
    expect(h).not.toContain('3 мин 12 с');
    expect(h).not.toContain('Завис?');
  });

  it('остановлен — баннер с командой запуска, «Процесса нет.», «нет процесса» курсивом', () => {
    const h = staticHtml(snap({ phase: 'stopped', level: 'danger', pid: null, file: null }));
    expect(h).toContain('Сервис transcription-supervisor остановлен');
    expect(h).toContain('systemctl --user start transcription-supervisor');
    expect(h).toContain('Процесса нет.');
    expect(h).toContain('font-style:italic');
    expect(h).toContain('нет процесса');
    expect(h).toContain('Остановлен');
  });

  it('снимка ещё нет — четыре скелета (aria-busy), без нулей и без выдуманных статусов', () => {
    // макет loadingBody: Конвейер, Сейчас, Очередь, Последние встречи
    const h = staticHtml(null);
    expect((h.match(/aria-busy="true"/g) ?? []).length).toBe(4);
    expect(h).not.toMatch(/>([^<]*\b0\b[^<]*)</);
    expect(h).not.toContain('Идёт пачка');
    expect(h).not.toContain('Ожидание');
    expect(h).not.toContain('Завис?');
  });
});

// Нажимаемая область по её тексту: role="button", чей текст ровно text («Показать ещё»)
function buttonByText(root: unknown, text: string): unknown | null {
  let hit: unknown | null = null;
  const walk = (x: { nodeType?: number; childNodes?: unknown[]; getAttribute?: (a: string) => string | null; textContent?: string }) => {
    if (hit) return;
    if (x.nodeType === 1 && x.getAttribute?.('role') === 'button' && x.textContent === text) { hit = x; return; }
    for (const c of x.childNodes ?? []) walk(c as Parameters<typeof walk>[0]);
  };
  walk(root as Parameters<typeof walk>[0]);
  return hit;
}

const click = (node: unknown) => { act(() => { (reactProps(node as Parameters<typeof reactProps>[0])!.onClick as () => void)(); }); };

// Нижняя часть (Ф4): данные как в состоянии fail макета
const FAILS = [
  { at: NOW - 4_000_000, folder: 'Video-RR', file: '2026-11-18 Интервью кандидата.mp4', minutes: 41, comment: 'fail: ComfyUI не ответил за 600 с (узел CcsAudioWorker)' },
  { at: NOW - 6_000_000, folder: 'Video-GPB', file: '2026-11-18 Короткий синк.mp4', minutes: 3, comment: 'fail: пустая дорожка — нечего распознавать' },
];
const MEET = (i: number) => ({
  at: NOW - (i + 1) * 3_600_000, folder: 'Video-GPB', file: `2026-11-18 встреча ${i}.md`,
  minutes: 30 + i, speakers: 3, comment: 'asr 2.1м + саммари 0.9м (локально)', mdPath: `/srv/meetings/v${i}.md`,
});
const MEET6 = [0, 1, 2, 3, 4, 5].map(MEET);

describe('TranscriptionTab: «Сбои за сутки» (Ф4)', () => {
  it('сбоев нет — карточки нет вовсе', () => {
    expect(staticHtml(snap({ fails: [] }))).not.toContain('Сбои за сутки');
  });

  it('сбои есть — карточка с числом; заголовок и комментарий — C.dangerText, строка не открывается', () => {
    const h = staticHtml(snap({ fails: FAILS }));
    expect(h).toContain('Сбои за сутки · 2');
    expect(h).toContain('2026-11-18 Интервью кандидата');
    expect(h).toContain('41 мин');
    const m = mountTab(snap({ fails: FAILS }));
    expect(styleOfText(m.container, 'Сбои за сутки · 2')?.color).toBe('var(--c-danger-text)');
    // комментарий журнала — целиком, красным (не обрезан многоточием)
    expect(styleOfText(m.container, FAILS[0].comment!)?.color).toBe('var(--c-danger-text)');
    // строка сбоя — не ссылка: перехода нет ни у одной
    expect(m.container.querySelector('[aria-label="Открыть сводку: 2026-11-18 Интервью кандидата"]')).toBeNull();
    expect(openMeetingNoteMock).not.toHaveBeenCalled();
  });
});

describe('TranscriptionTab: «Последние встречи» (Ф4)', () => {
  it('пять строк; шестая — только после «Показать ещё»', () => {
    const m = mountTab(snap({ recent: MEET6 }));
    expect(styleOfText(m.container, '2026-11-18 встреча 4')).not.toBeNull();
    expect(styleOfText(m.container, '2026-11-18 встреча 5')).toBeNull();
    const more = buttonByText(m.container, 'Показать ещё');
    expect(more).not.toBeNull();
    click(more);
    expect(styleOfText(m.container, '2026-11-18 встреча 5')).not.toBeNull();
    expect(buttonByText(m.container, 'Показать ещё')).toBeNull(); // кнопка больше не нужна
  });

  it('касание строки — ровно один вызов openMeetingNote с id этой строки (folder/file)', () => {
    const m = mountTab(snap({ recent: [MEET(0)] }));
    const row = m.container.querySelector('[aria-label="Открыть сводку: 2026-11-18 встреча 0"]');
    expect(row).not.toBeNull();
    click(row);
    expect(openMeetingNoteMock).toHaveBeenCalledTimes(1);
    expect(openMeetingNoteMock).toHaveBeenCalledWith('Video-GPB/2026-11-18 встреча 0.md');
  });

  it('mdPath нет — строка не ссылка и не зовёт openMeetingNote', () => {
    const m = mountTab(snap({ recent: [{ at: NOW - 3_600_000, folder: 'Video-GPB', file: '2026-11-18 планёрка.md' }] }));
    expect(m.container.querySelector('[aria-label="Открыть сводку: 2026-11-18 планёрка"]')).toBeNull();
    expect(openMeetingNoteMock).not.toHaveBeenCalled();
  });

  it('метка «.md» есть у открываемой строки; длина и спикеры — на месте', () => {
    const h = staticHtml(snap({ recent: [MEET(0)] }));
    expect(h).toContain('.md');
    expect(h).toContain('30 мин');
    expect(h).toContain('3 спикера');
  });
});

describe('TranscriptionTab: «Сверка с Dify» (Ф4)', () => {
  const DIFY = [
    { folder: 'Video-GPB', local: 128, inDify: 128 },
    { folder: 'Video-SIAM', local: 41, inDify: 37 },
    { folder: 'Video-RR', local: 77, inDify: 77 },
  ];

  it('три папки без «Video-», «Локально / В Dify», «сходится» и «не хватает 4», свежесть «12 минут назад · раз в 30 мин»', () => {
    const h = staticHtml(snap({ dify: DIFY, checkedAt: NOW - 12 * 60_000 }));
    expect(h).toContain('Сверка с Dify');
    expect(h).toContain('Локально');
    expect(h).toContain('В Dify');
    expect(h).toContain('12 минут назад · раз в 30 мин');
    expect(h).toContain('>SIAM<'); // общий префикс «Video-» из таблицы убран
    expect(h).not.toContain('Video-SIAM');
    expect(h).toContain('сходится');
    expect(h).toContain('не хватает 4');
  });

  it('inDify=null — дефицита нет: ни «не хватает», ни пустой плашки, ни строки папки', () => {
    const h = staticHtml(snap({ dify: [{ folder: 'Video-SIAM', local: 41, inDify: null }], checkedAt: NOW }));
    expect(h).not.toContain('не хватает');
    expect(h).not.toContain('сходится');
    expect(h).not.toContain('Сверка с Dify'); // строк нет — блока нет
  });

  it('checkedAt нет — строки свежести нет (блок остаётся)', () => {
    const h = staticHtml(snap({ dify: [DIFY[1]] }));
    expect(h).toContain('Сверка с Dify');
    expect(h).not.toContain('раз в 30 мин');
  });
});

describe('TranscriptionTab: «Зависимости» (Ф4)', () => {
  const DEPS = [
    { key: 'asr', state: 'ok' as const, checkedAt: NOW },
    { key: 'summary', state: 'slow' as const, checkedAt: NOW },
    { key: 'dify', state: 'down' as const, checkedAt: NOW },
    { key: 'share', state: 'unknown' as const, checkedAt: null },
  ];

  it('четыре пункта с точкой и словом; цвета слова — по состоянию', () => {
    const m = mountTab(snap({ deps: DEPS }));
    expect(styleOfText(m.container, 'Сводки')?.color).toBe('var(--c-text-heading)');
    expect(styleOfText(m.container, 'Сетевая папка')?.color).toBe('var(--c-text-heading)');
    expect(styleOfText(m.container, 'в норме')?.color).toBe('var(--c-text-secondary)');
    expect(styleOfText(m.container, 'медленно')?.color).toBe('var(--c-warning-text)');
    expect(styleOfText(m.container, 'не отвечает')?.color).toBe('var(--c-danger-text)');
    expect(styleOfText(m.container, 'не проверялось')?.color).toBe('var(--c-text-muted)');
  });

  it('неизвестный ключ — показан как есть, без выдуманного имени', () => {
    const h = staticHtml(snap({ deps: [{ key: 'future', state: 'unknown', checkedAt: null }] }));
    expect(h).toContain('>future<');
    expect(h).not.toContain('Сетевая папка');
  });

  it('зависимостей нет — карточки нет', () => {
    expect(staticHtml(snap({ deps: [] }))).not.toContain('Зависимости');
  });
});
