// Отрисовка верха вкладки «Встречи» (Ф3): порядок блоков, баннер по уровню, пустое
// состояние «Сейчас», скелеты без снимка — статическим рендером (паттерн EngineCard/
// hardware.test). Цвета гашения/тревоги и раскрытие очереди — mount на fakeDom по
// текстовым узлам (мутации, которые обязаны покрасить):
// - баннер рисовать при любом level (краснеет «в норме баннера нет»);
// - гашение не привязывать к stale (краснеют цвета «Завис?»);
// - скелеты заменить нулями (краснеет тест загрузки);
// - порядок блоков переставить (краснеет тест порядка).

import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import { TranscriptionTab } from './TranscriptionTab';
import type { TranscriptionSnapshot } from './types';

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

  it('файл, «идёт 3 мин 12 с» и счётчики очереди — обычными цветами (не гашены, не красные)', () => {
    const m = mountTab(snap());
    expect(styleOfText(m.container, '2026-11-18 Обсуждение бюджета.mp4')?.color).toBe('var(--c-text-heading)');
    expect(styleOfText(m.container, '3 мин 12 с')?.color).toBe('var(--c-text-heading)');
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

  it('баннер тревоги с текстом по proposal; плашка «Завис?»', () => {
    const h = staticHtml(hung());
    expect(h).toContain('role="status"');
    expect(h).toContain('Конвейер не отчитывался 23 минуты');
    expect(h).toContain('Завис?');
  });

  it('«Сейчас» гаснет до C.textMuted, а «идёт» краснеет', () => {
    const m = mountTab(hung());
    expect(styleOfText(m.container, '2026-11-18 Обсуждение бюджета.mp4')?.color).toBe('var(--c-text-muted)');
    expect(styleOfText(m.container, '3 мин 12 с')?.color).toBe('var(--c-danger-text)');
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

  it('снимка ещё нет — три скелета (aria-busy), без нулей и без выдуманных статусов', () => {
    const h = staticHtml(null);
    expect((h.match(/aria-busy="true"/g) ?? []).length).toBe(3);
    expect(h).not.toMatch(/>([^<]*\b0\b[^<]*)</);
    expect(h).not.toContain('Идёт пачка');
    expect(h).not.toContain('Ожидание');
    expect(h).not.toContain('Завис?');
  });
});
