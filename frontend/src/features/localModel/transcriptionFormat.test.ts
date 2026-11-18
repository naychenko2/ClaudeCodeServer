// View-модель вкладки «Встречи» (Ф3 + Ф4): тексты и флаги верхних блоков и нижних
// (сбои, встречи, сверка, зависимости) из снимка. Чистые функции — без DOM; времена
// фиксированные (nowMs параметром).
//
// Мутации, которые обязаны покрасить:
// - баннер рисовать при любом level (не только при тревоге) — краснеет «в норме баннера нет»;
// - «Завис?» выводить по возрасту updatedAt (свой порог) — краснеет тест danger по
//   севшей зависимости: там конвейер жив, «Завис?» — вранье;
// - гашение (stale) раскинуть на всю вкладку, включая карточку конвейера, — краснеет
//   тест «Сбоев» (макет: при севшей зависимости значения не гасятся);
// - тексты баннера сочинять свои — краснеют дословные проверки по proposal.md.

import { describe, expect, it } from 'vitest';
import {
  agoText, durationText, durationWords, meetingLengthText, sinceWords, transTabView,
} from './transcriptionFormat';
import type { TranscriptionSnapshot } from './types';

const NOW = 1_763_460_000_000; // фиксированный «сейчас» (2025-11-18T17:00:00Z ± пояс)

// Снимок «Идёт пачка» — состояние work из макета: всё на местах, тревоги нет
function snap(over: Partial<TranscriptionSnapshot> = {}): TranscriptionSnapshot {
  return {
    phase: 'batch', updatedAt: NOW - 8_000, host: 'worker', pid: 41233, alive: true,
    outputHost: 'worker', outputDir: '~/meetings', level: 'ok',
    file: '2026-11-18 Обсуждение бюджета.mp4', folder: 'Video-GPB', stage: 'asr',
    stageStartedAt: NOW - 192_000, durationMin: 3.2, batchIndex: 2, batchSize: 5,
    asrQueue: [
      { file: '2026-11-18 Стендап.mp4', folder: 'Video-GPB', since: NOW - 360_000 },
      { file: '2026-11-18 Встреча с юристами.mp4', folder: 'Video-SIAM', since: NOW - 16_800_000 },
    ],
    summaryQueue: 1,
    fails: [],
    recent: [{ at: NOW - 3_600_000, folder: 'Video-GPB', file: '2026-11-18 планёрка.md' }],
    dify: [{ folder: 'Video-GPB', local: 128, inDify: 128 }],
    deps: [{ key: 'asr', state: 'ok', checkedAt: NOW }],
    ...over,
  };
}

describe('transTabView: норма (макет «Идёт пачка»)', () => {
  it('баннера нет; плашка «Идёт пачка»; гашения нет', () => {
    const v = transTabView(snap(), NOW);
    expect(v.banner).toBeNull();
    expect(v.pipeline.badge).toEqual({ tone: 'accent', label: 'Идёт пачка' });
    expect(v.stale).toBe(false);
    expect(v.pipeline.updatedAlarm).toBe(false);
  });

  it('конвейер: «8 с назад», «worker · pid 41233», «worker · ~/meetings» без тревоги', () => {
    const p = transTabView(snap(), NOW).pipeline;
    expect(p.updatedText).toBe('8 с назад');
    expect(p.whereText).toBe('worker');
    expect(p.pidText).toBe('pid 41233');
    expect(p.writesText).toBe('worker · ~/meetings');
    expect(p.writesWarn).toBe(false);
  });

  it('«Сейчас»: активный файл, этап «Распознавание» текущий, «3 мин 12 с», «пачка 2 из 5»', () => {
    const n = transTabView(snap(), NOW).now;
    expect(n.kind).toBe('active');
    if (n.kind !== 'active') return;
    expect(n.file).toBe('2026-11-18 Обсуждение бюджета.mp4');
    expect(n.folder).toBe('Video-GPB');
    expect(n.steps.map(s => s.state)).toEqual(['cur', 'todo', 'todo']);
    expect(n.steps.map(s => s.label)).toEqual(['Распознавание', 'Сводка', 'В Dify']);
    expect(n.runningText).toBe('3 мин 12 с');
    expect(n.batchText).toBe('пачка 2 из 5');
  });

  it('этап «Сводка»: первый пройден, второй текущий', () => {
    const n = transTabView(snap({ stage: 'summary' }), NOW).now;
    if (n.kind !== 'active') throw new Error('expected active');
    expect(n.steps.map(s => s.state)).toEqual(['done', 'cur', 'todo']);
  });

  it('этапа нет (фаза не помечена файлом) — ни пройденных, ни текущего', () => {
    const n = transTabView(snap({ stage: null }), NOW).now;
    if (n.kind !== 'active') throw new Error('expected active');
    expect(n.steps.map(s => s.state)).toEqual(['todo', 'todo', 'todo']);
  });

  it('очередь: счётчики 2 и 1, файлы с «ждёт 6 минут» / «ждёт 4 ч 40 мин»', () => {
    const q = transTabView(snap(), NOW).queue;
    expect(q.asrCount).toBe(2);
    expect(q.asrNote).toBe('в сетевой папке');
    expect(q.sumCount).toBe(1);
    expect(q.sumNote).toBe('транскрипт готов');
    expect(q.files[0].waitText).toBe('ждёт 6 минут');
    expect(q.files[1].waitText).toBe('ждёт 4 ч 40 мин');
  });

  it('empty-очередь: нули с «всё разобрано», списка файлов нет', () => {
    const q = transTabView(snap({ asrQueue: [], summaryQueue: 0 }), NOW).queue;
    expect(q.asrCount).toBe(0);
    expect(q.asrNote).toBe('всё разобрано');
    expect(q.sumNote).toBe('всё разобрано');
    expect(q.files).toEqual([]);
  });
});

describe('transTabView: «Ожидание» и пустое «Сейчас» (макет idle)', () => {
  const idle = snap({ phase: 'idle', file: null, stage: null, durationMin: null, batchIndex: null, asrQueue: [], summaryQueue: 0 });

  it('плашка «Ожидание»; «Сейчас» — пустое состояние с временем последней работы', () => {
    const v = transTabView(idle, NOW);
    expect(v.pipeline.badge).toEqual({ tone: 'success', label: 'Ожидание' });
    expect(v.banner).toBeNull();
    const n = v.now;
    expect(n.kind).toBe('empty');
    if (n.kind !== 'empty') return;
    // время локальное (пояс тестовой машины) — проверяем форму, не «17:00»
    expect(n.note).toMatch(/^Последняя завершена в \d{2}:\d{2}\./);
    expect(n.note).toContain('конвейер подхватит сам');
  });

  it('завершённых встреч нет — второй строки про «последнюю» нет', () => {
    const n = transTabView(snap({ phase: 'idle', file: null, recent: [] }), NOW).now;
    if (n.kind !== 'empty') throw new Error('expected empty');
    expect(n.note).not.toContain('Последняя завершена');
  });

  it('durationMin нет, stageStartedAt есть — «идёт» считается от начала этапа', () => {
    const n = transTabView(snap({ durationMin: null }), NOW).now;
    if (n.kind !== 'active') throw new Error('expected active');
    expect(n.runningText).toBe('3 мин 12 с');
  });

  it('ни того, ни другого — «идёт» не рисуется (null), не «0 с»', () => {
    const n = transTabView(snap({ durationMin: null, stageStartedAt: null }), NOW).now;
    if (n.kind !== 'active') throw new Error('expected active');
    expect(n.runningText).toBeNull();
  });
});

describe('transTabView: «Завис?» (макет stale)', () => {
  const hung = snap({ level: 'danger', updatedAt: NOW - 23 * 60_000 });

  it('баннер danger с текстом по proposal; плашка «Завис?»', () => {
    const v = transTabView(hung, NOW);
    expect(v.banner).not.toBeNull();
    expect(v.banner!.tone).toBe('danger');
    expect(v.banner!.title).toBe('Конвейер не отчитывался 23 минуты');
    expect(v.banner!.body).toContain('state.json обновляется каждые ~10 с');
    expect(v.pipeline.badge).toEqual({ tone: 'danger', label: 'Завис?' });
  });

  it('гашение включено: «Обновлено 23 минут назад» краснеет', () => {
    const p = transTabView(hung, NOW).pipeline;
    expect(p.updatedText).toBe('23 минуты назад');
    expect(p.updatedAlarm).toBe(true);
    expect(transTabView(hung, NOW).stale).toBe(true);
  });

  it('уровень «danger» по севшей зависимости — НЕ «Завис?»: конвейер жив, значения не гаснут', () => {
    const down = snap({ level: 'danger', deps: [{ key: 'asr', state: 'down', checkedAt: NOW }] });
    const v = transTabView(down, NOW);
    expect(v.pipeline.badge).toEqual({ tone: 'accent', label: 'Идёт пачка' });
    expect(v.stale).toBe(false);
    // текста про севшую зависимость в proposal нет — баннера нет (причину видит блок Ф4)
    expect(v.banner).toBeNull();
  });
});

describe('transTabView: «Остановлен» (макет down)', () => {
  const stopped = snap({ phase: 'stopped', level: 'danger', pid: null, alive: false, file: null, stage: null });

  it('баннер danger с командой запуска; плашка «Остановлен»; гашение', () => {
    const v = transTabView(stopped, NOW);
    expect(v.banner!.tone).toBe('danger');
    expect(v.banner!.title).toBe('Сервис transcription-supervisor остановлен');
    expect(v.banner!.body).toContain('systemctl --user start transcription-supervisor');
    expect(v.banner!.body).toMatch(/не обрабатываются с \d{2}:\d{2}\./);
    expect(v.pipeline.badge).toEqual({ tone: 'danger', label: 'Остановлен' });
    expect(v.stale).toBe(true);
  });

  it('pid нет — «нет процесса» (курсив рисует компонент); «Сейчас» — «Процесса нет.»', () => {
    const v = transTabView(stopped, NOW);
    expect(v.pipeline.pidText).toBeNull();
    expect(v.now.kind).toBe('empty');
    if (v.now.kind !== 'empty') return;
    expect(v.now.note).toBe('Процесса нет.');
  });
});

describe('transTabView: расхождения (макет fail)', () => {
  it('level warn + дефицит Dify: баннер warning «4 встречи не попали в Dify» со строками папок', () => {
    const v = transTabView(snap({
      level: 'warn',
      dify: [{ folder: 'Video-GPB', local: 128, inDify: 128 }, { folder: 'Video-SIAM', local: 41, inDify: 37 }],
    }), NOW);
    expect(v.banner).not.toBeNull();
    expect(v.banner!.tone).toBe('warning');
    expect(v.banner!.title).toBe('4 встречи не попали в Dify');
    expect(v.banner!.body).toBe('Video-SIAM: локально 41, в Dify 37');
    expect(v.pipeline.writesWarn).toBe(false);
  });

  it('one: «1 встреча не попала в Dify» (согласование числительного)', () => {
    const v = transTabView(snap({ level: 'warn', dify: [{ folder: 'Video-RR', local: 78, inDify: 77 }] }), NOW);
    expect(v.banner!.title).toBe('1 встреча не попала в Dify');
  });

  it('inDify=null (v1: не проверялось) — дефицита нет, баннера нет', () => {
    const v = transTabView(snap({ level: 'warn', dify: [{ folder: 'Video-SIAM', local: 41, inDify: null }] }), NOW);
    expect(v.banner).toBeNull();
  });

  it('level warn без Dify — «Пишет в не туда»: жёлтая строка карточки, не баннер', () => {
    const v = transTabView(snap({ level: 'warn' }), NOW);
    expect(v.banner).toBeNull();
    expect(v.pipeline.writesWarn).toBe(true);
    expect(v.pipeline.writesText).toBe('worker · ~/meetings — не та машина');
  });

  it('уровня нет (null) — ни баннера, ни гашения, плашка по фазе', () => {
    const v = transTabView(snap({ level: null }), NOW);
    expect(v.banner).toBeNull();
    expect(v.stale).toBe(false);
    expect(v.pipeline.badge.label).toBe('Идёт пачка');
  });

  it('фазы нет — плашка «нет данных», а не выдуманное «Ожидание»', () => {
    const v = transTabView(snap({ phase: 'unknown' as TranscriptionSnapshot['phase'] }), NOW);
    expect(v.pipeline.badge).toEqual({ tone: 'neutral', label: 'нет данных' });
  });
});

describe('форматтеры времени', () => {
  it('agoText: «только что» / «N с» / «N минут назад» / «4 ч 31 мин назад»', () => {
    expect(agoText(NOW - 1_000, NOW)).toBe('только что');
    expect(agoText(NOW - 8_000, NOW)).toBe('8 с назад');
    expect(agoText(NOW - 61_000, NOW)).toBe('1 минуту назад');
    expect(agoText(NOW - 23 * 60_000, NOW)).toBe('23 минуты назад');
    expect(agoText(NOW - 2 * 3_600_000, NOW)).toBe('2 часа назад');
    expect(agoText(NOW - (4 * 60 + 31) * 60_000, NOW)).toBe('4 ч 31 мин назад');
    expect(agoText(NOW + 5_000, NOW)).toBe('только что'); // часы вразнобой — не «-5 с назад»
  });

  it('durationText: «48 с» / «3 мин 12 с» / «26 мин»', () => {
    expect(durationText(0.8)).toBe('48 с');
    expect(durationText(3.2)).toBe('3 мин 12 с');
    expect(durationText(26)).toBe('26 мин');
  });

  it('durationWords и sinceWords: согласование («1 минуту», «23 минуты», «1 час 5 минут»)', () => {
    expect(durationWords(1)).toBe('1 минуту');
    expect(durationWords(23)).toBe('23 минуты');
    expect(durationWords(11)).toBe('11 минут');
    expect(durationWords(65)).toBe('1 час 5 минут');
    expect(sinceWords(NOW - 360_000, NOW)).toBe('6 минут');
    expect(sinceWords(NOW - 45_000, NOW)).toBe('45 с');
  });

  it('meetingLengthText: короткая форма длины «47 мин», «1 ч 5 мин», «2 ч»', () => {
    expect(meetingLengthText(47)).toBe('47 мин');
    expect(meetingLengthText(65)).toBe('1 ч 5 мин');
    expect(meetingLengthText(120)).toBe('2 ч');
    expect(meetingLengthText(0)).toBe('0 мин');
  });
});

describe('transTabView: низ вкладки (Ф4): сбои, встречи, сверка, зависимости', () => {
  const FAIL = { at: NOW - 4_000_000, folder: 'Video-RR', file: '2026-11-18 Интервью кандидата.mp4', minutes: 41, comment: 'fail: ComfyUI не ответил за 600 с (узел CcsAudioWorker)' };
  const MEET = { at: NOW - 3_600_000, folder: 'Video-GPB', file: '2026-11-18 планёрка.md', minutes: 47, speakers: 4, comment: 'asr 2.7м + саммари 1.2м (локально)', mdPath: '/srv/meetings/planerka.md' };

  it('сбой: id = folder/file, имя без расширения, строка не открывается, комментарий и длина — как в журнале', () => {
    const v = transTabView(snap({ fails: [FAIL] }), NOW);
    expect(v.failures).toHaveLength(1);
    const f = v.failures[0];
    expect(f.id).toBe('Video-RR/2026-11-18 Интервью кандидата.mp4');
    expect(f.name).toBe('2026-11-18 Интервью кандидата');
    expect(f.openable).toBe(false);
    expect(f.comment).toBe('fail: ComfyUI не ответил за 600 с (узел CcsAudioWorker)');
    expect(f.lengthText).toBe('41 мин');
  });

  it('встреча с mdPath — открывается; «47 мин», «4 спикера», сегодня — только время', () => {
    const m = transTabView(snap({ recent: [MEET] }), NOW).meetings[0];
    expect(m.openable).toBe(true);
    expect(m.lengthText).toBe('47 мин');
    expect(m.speakersText).toBe('4 спикера');
    expect(m.comment).toBe('asr 2.7м + саммари 1.2м (локально)');
    expect(m.timeText).toMatch(/^\d{2}:\d{2}$/); // сегодня: без слова «вчера» (пояс тестовой машины)
  });

  it('mdPath/спикеров/минут нет — openable=false и пустых строк нет', () => {
    const m = transTabView(snap({ recent: [{ at: NOW - 60_000, folder: 'Video-GPB', file: 'x.md' }] }), NOW).meetings[0];
    expect(m.openable).toBe(false);
    expect(m.lengthText).toBeNull();
    expect(m.speakersText).toBeNull();
    expect(m.comment).toBeNull();
  });

  it('«вчера»: прошлые календарные сутки подписаны (окно бэка — 24 ч)', () => {
    const m = transTabView(snap({ recent: [{ at: NOW - 26 * 3_600_000, folder: 'Video-RR', file: 'y.md', mdPath: '/m/y.md' }] }), NOW).meetings[0];
    expect(m.timeText).toMatch(/^вчера \d{2}:\d{2}$/);
  });

  it('сверка: папки без «Video-»; дефицит только при local > inDify; в Dify больше — плашки нет', () => {
    const v = transTabView(snap({
      dify: [{ folder: 'Video-GPB', local: 128, inDify: 128 }, { folder: 'Video-SIAM', local: 41, inDify: 37 }, { folder: 'RR', local: 7, inDify: 9 }],
    }), NOW);
    expect(v.difyRows.map(r => r.folder)).toEqual(['GPB', 'SIAM', 'RR']);
    expect(v.difyRows[0].deficit).toBeNull(); // равны — «сходится» рисует компонент
    expect(v.difyRows[1].deficit).toBe(4);
    expect(v.difyRows[2].deficit).toBeNull();
  });

  it('inDify=null — строки сверки нет вовсе: дефицит из неизвестного не считается', () => {
    expect(transTabView(snap({ dify: [{ folder: 'Video-SIAM', local: 41, inDify: null }] }), NOW).difyRows).toEqual([]);
  });

  it('checkedAt — «12 минут назад»; поля нет — строки свежести нет', () => {
    expect(transTabView(snap({ checkedAt: NOW - 12 * 60_000 }), NOW).difyAgeText).toBe('12 минут назад');
    expect(transTabView(snap({ checkedAt: null }), NOW).difyAgeText).toBeNull();
  });

  it('зависимости: имена по ключам макета, слова по состоянию; чужой ключ — как есть, чужое значение — «не проверялось»', () => {
    const v = transTabView(snap({ deps: [
      { key: 'asr', state: 'ok', checkedAt: NOW },
      { key: 'summary', state: 'slow', checkedAt: NOW },
      { key: 'dify', state: 'down', checkedAt: NOW },
      { key: 'share', state: 'unknown', checkedAt: null },
      { key: 'future', state: 'weird' as 'ok', checkedAt: NOW },
    ] }), NOW);
    expect(v.deps.map(d => d.name)).toEqual(['Распознавание', 'Сводки', 'Dify', 'Сетевая папка', 'future']);
    expect(v.deps.map(d => d.word)).toEqual(['в норме', 'медленно', 'не отвечает', 'не проверялось', 'не проверялось']);
  });

  it('блоков нет при пустых полях: fails/recent/dify/deps отсутствуют — пустые списки, не падение', () => {
    const v = transTabView({ phase: 'idle', updatedAt: NOW - 14_000 } as TranscriptionSnapshot, NOW);
    expect([v.failures, v.meetings, v.difyRows, v.deps]).toEqual([[], [], [], []]);
    expect(v.difyAgeText).toBeNull();
  });
});
