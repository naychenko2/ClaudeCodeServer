// Чистые функции вкладки «Медиа»: юнит-тесты без React и DOM.
// Мутации, которые обязаны покрасить:
//  - «Занят» по слотам/тяжёлой (isComfyBusy) — тон статуса;
//  - факт против ETA с порогом 20 % (isEtaOverdue) — смена порога красит;
//  - «В памяти» (loadedModelLine) — gpus==null → «узел не сообщает», не «загружена»;
//    без loadedModels идущий прогон подписан «работает сейчас», не «загружена»;
//  - «ещё N» (inMemoryLine) — показ не более 3 имён;
//  - чужой прогон без sessionId (isOwnJob) — без перехода.

import { describe, expect, it } from 'vitest';
import {
  comfyStatusInfo, isComfyBusy, loadedModelLine, modelFromOp, truncateName, inMemoryLine,
  formatClock, formatClockTime, queueStartLine, isEtaOverdue, doneOutcomeLine,
  lastHourSummary, runningProgress, remainingLine, isOwnJob, jobOwnerLabel,
  slotsLine, queueJobLabel, LAST_HOUR_VISIBLE,
  formatDurationHuman, formatClockFromIso, formatClockSecFromIso, formatDayTimeFromIso,
  dayGroupLabel, historyKindLabel, historyOpLabel, historyRowTitle, goneReasonText,
  humanErrorPhrase, historySummaryLine, historyWaitSeconds, historyWaitLine, historyRowTime,
  seedFormatted, historyVariantCount, historyParamsRows, historyTimeRows,
  historyPeriodTail, historyPeriodFrom, liveCompletionMarker, historyOutcomeBadge,
} from './mediaFormat';
import type { LocalModelMedia, MediaDone, MediaRunning } from './api';

// === Статус ComfyUI ===

describe('comfyStatusInfo: статус → подпись + тон', () => {
  it('working → «Работает»/ok', () => {
    expect(comfyStatusInfo('working')).toEqual({ label: 'Работает', tone: 'ok' });
  });
  it('busy → «Занят»/busy', () => {
    expect(comfyStatusInfo('busy')).toEqual({ label: 'Занят', tone: 'busy' });
  });
  it('down → «Не отвечает»/down', () => {
    expect(comfyStatusInfo('down')).toEqual({ label: 'Не отвечает', tone: 'down' });
  });
  it('disabled → «Выключено»/disabled', () => {
    expect(comfyStatusInfo('disabled')).toEqual({ label: 'Выключено', tone: 'disabled' });
  });
});

// === «Занят»: 4 из 4 мест ИЛИ тяжёлая задача ===

describe('isComfyBusy: «Занят» по слотам и тяжёлой', () => {
  it('все места заняты → true', () => {
    expect(isComfyBusy({ used: 4, limit: 4 }, [])).toBe(true);
  });
  it('свободные места, но идёт тяжёлая → true', () => {
    expect(isComfyBusy({ used: 1, limit: 4 }, [{ heavy: true } as MediaRunning])).toBe(true);
  });
  it('свободные места, нет тяжёлой → false', () => {
    expect(isComfyBusy({ used: 1, limit: 4 }, [{ heavy: false } as MediaRunning])).toBe(false);
  });
  it('limit 0 (неизвестно) и нет тяжёлой → false', () => {
    expect(isComfyBusy({ used: 0, limit: 0 }, [])).toBe(false);
  });
});

// === «Модель в памяти» ===

describe('modelFromOp: имя модели из операции', () => {
  it('«Видео 5 с · MiniMax H3» → «MiniMax H3»', () => {
    expect(modelFromOp('Видео 5 с · MiniMax H3')).toBe('MiniMax H3');
  });
  it('«Картинка · Qwen-Image 2.1» → «Qwen-Image 2.1»', () => {
    expect(modelFromOp('Картинка · Qwen-Image 2.1')).toBe('Qwen-Image 2.1');
  });
  it('без «·» — вся строка', () => {
    expect(modelFromOp('MiniMax H3')).toBe('MiniMax H3');
  });
  it('пусто — пусто', () => {
    expect(modelFromOp('')).toBe('');
    expect(modelFromOp(null)).toBe('');
  });
});

describe('truncateName: длинные имена — с многоточием', () => {
  it('короткое имя — как есть', () => {
    expect(truncateName('MiniMax H3')).toBe('MiniMax H3');
  });
  it('ровно 24 символа — без обрезки', () => {
    const n = 'a'.repeat(24);
    expect(truncateName(n)).toBe(n);
  });
  it('длинное — обрезка с «…» (24 символа итого)', () => {
    const n = 'ACE-Step-v10-large.safetensors'; // 31 символ
    const t = truncateName(n);
    expect(t.endsWith('…')).toBe(true);
    expect(t.length).toBe(24);
    expect(t).toBe(`${n.slice(0, 23)}…`);
  });
});

describe('inMemoryLine: «В памяти: <имена>», не более 3 + «ещё N»', () => {
  it('одно имя — без «ещё»', () => {
    expect(inMemoryLine(['MiniMax H3'])).toBe('В памяти: MiniMax H3');
  });
  it('три имени — все, без «ещё»', () => {
    expect(inMemoryLine(['a', 'b', 'c'])).toBe('В памяти: a, b, c');
  });
  it('больше 3 — три + «ещё N»', () => {
    expect(inMemoryLine(['a', 'b', 'c', 'd', 'e'])).toBe('В памяти: a, b, c, ещё 2');
  });
  it('длинные имена — обрезаны с многоточием', () => {
    const line = inMemoryLine(['MiniMax H3', 'qwen_image.safetensors', 'ACE-Step-v10-large.safetensors']);
    expect(line).toBe(`В памяти: MiniMax H3, qwen_image.safetensors, ${truncateName('ACE-Step-v10-large.safetensors')}`);
  });
});

describe('loadedModelLine: «В памяти»', () => {
  const media = (over: Partial<LocalModelMedia>): LocalModelMedia => ({
    configured: true,
    comfy: { status: 'working', downSince: null },
    slots: { used: 0, limit: 4 },
    gpus: [{ uuid: 'g', index: 0, name: 'RTX 3090', roles: ['media'] }],
    loadedModels: null,
    running: [],
    queue: [],
    lastHour: [],
    ...over,
  });

  it('loadedModels есть → loaded, «В памяти: <имена>» (честно, не «загружена»)', () => {
    const line = loadedModelLine(media({ loadedModels: ['MiniMax H3'] }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: MiniMax H3');
  });
  it('loadedModels более 3 → 3 имени + «ещё N»', () => {
    const line = loadedModelLine(media({ loadedModels: ['a', 'b', 'c', 'd', 'e'] }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: a, b, c, ещё 2');
  });
  it('loadedModels null, gpus есть, ничего не идёт → empty (не «узел не сообщает»)', () => {
    const line = loadedModelLine(media({ loadedModels: null }));
    expect(line.state).toBe('empty');
    expect(line.label).toBe('ничего, модель загрузится на первой задаче');
  });
  it('loadedModels пустой [] → empty (не выдумываем «загружена»)', () => {
    const line = loadedModelLine(media({ loadedModels: [] }));
    expect(line.state).toBe('empty');
    expect(line.label).toBe('ничего, модель загрузится на первой задаче');
  });
  it('gpus==null и loadedModels null → «узел не сообщает» (не выдумываем «загружена»)', () => {
    const line = loadedModelLine(media({ gpus: null, loadedModels: null }));
    expect(line.state).toBe('unknown');
    expect(line.label).toBe('узел не сообщает');
  });
  it('loadedModels есть, но gpus==null → loaded (честные имена приоритетнее)', () => {
    const line = loadedModelLine(media({ gpus: null, loadedModels: ['MiniMax H3'] }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: MiniMax H3');
  });
  it('loadedModels нет, идёт прогон → «работает сейчас: <модель>» (мутация «показывать «загружена»» красит)', () => {
    const line = loadedModelLine(media({
      loadedModels: null,
      running: [{ kind: 'video', op: 'Видео 5 с · MiniMax H3', heavy: false, own: true } as MediaRunning],
    }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('работает сейчас: MiniMax H3');
  });
  it('loadedModels есть, идёт прогон → loadedModels (не модель из op)', () => {
    const line = loadedModelLine(media({
      loadedModels: ['Qwen-Image 2.1'],
      running: [{ kind: 'video', op: 'Видео 5 с · MiniMax H3', heavy: false, own: true } as MediaRunning],
    }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: Qwen-Image 2.1');
  });
  it('ничего не идёт, loadedModels нет → empty', () => {
    const line = loadedModelLine(media({}));
    expect(line.state).toBe('empty');
    expect(line.label).toBe('ничего, модель загрузится на первой задаче');
  });
});

// === Время ===

describe('formatClock: секунды → M:SS / H:MM:SS', () => {
  it('«5:40», «0:47», «1:12»', () => {
    expect(formatClock(340)).toBe('5:40');
    expect(formatClock(47)).toBe('0:47');
    expect(formatClock(72)).toBe('1:12');
  });
  it('час+ → H:MM:SS', () => {
    expect(formatClock(3661)).toBe('1:01:01');
  });
  it('без значения — «—»', () => {
    expect(formatClock(null)).toBe('—');
    expect(formatClock(undefined)).toBe('—');
    expect(formatClock(-1)).toBe('—');
  });
});

describe('formatClockTime: unix-мс → HH:MM', () => {
  it('локальное время', () => {
    const ms = new Date(2026, 9, 13, 14, 32, 0).getTime();
    expect(formatClockTime(ms)).toBe('14:32');
  });
  it('без момента — пусто', () => {
    expect(formatClockTime(null)).toBe('');
    expect(formatClockTime(undefined)).toBe('');
  });
});

// === Очередь: ожидаемый старт ===

describe('queueStartLine: старт очереди', () => {
  const now = new Date(2026, 9, 13, 14, 0, 0).getTime();
  it('startInSeconds → «старт ≈ HH:MM · через N мин»', () => {
    // 120 с = 2 мин; старт в 14:02
    expect(queueStartLine(120, 120, now)).toBe('старт ≈ 14:02 · через 2 мин');
  });
  it('compact → «≈ N мин»', () => {
    expect(queueStartLine(120, 120, now, true)).toBe('≈ 2 мин');
  });
  it('только etaSeconds → «старт ≈ через N мин»', () => {
    expect(queueStartLine(null, 240, now)).toBe('старт ≈ через 4 мин');
  });
  it('ничего → «время не замерено»', () => {
    expect(queueStartLine(null, null, now)).toBe('время не замерено');
  });
});

// === «Последний час»: факт против ETA, порог 20 % ===

describe('isEtaOverdue: факт дольше обещанного на 20 %+', () => {
  it('ровно 20 % → true (>=)', () => {
    // fact 120, eta 100 → (120-100)/100 = 0.2
    expect(isEtaOverdue(120, 100)).toBe(true);
  });
  it('19 % → false', () => {
    // fact 119, eta 100 → 0.19
    expect(isEtaOverdue(119, 100)).toBe(false);
  });
  it('без eta (null) → false', () => {
    expect(isEtaOverdue(120, null)).toBe(false);
  });
  it('без fact (null) → false', () => {
    expect(isEtaOverdue(null, 100)).toBe(false);
  });
  it('eta 0 → false (делить нельзя)', () => {
    expect(isEtaOverdue(120, 0)).toBe(false);
  });
  it('мутация порога: 0.19 вместо 0.2 красит «ровно 20 % → true»', () => {
    // (120-100)/100 = 0.2; при пороге 0.19 это true, при 0.21 — false
    expect(isEtaOverdue(120, 100, 0.19)).toBe(true);
    expect(isEtaOverdue(120, 100, 0.21)).toBe(false);
  });
});

describe('doneOutcomeLine: «готово в HH:MM» / «упала: …»', () => {
  const done = (over: Partial<MediaDone>): MediaDone => ({
    kind: 'video', op: 'Видео 5 с · MiniMax H3', own: true, outcome: 'completed',
    runSeconds: 340, etaSeconds: 330, finishedAt: new Date(2026, 9, 13, 14, 12, 0).getTime(),
    ...over,
  });
  it('completed → «готово в HH:MM»', () => {
    expect(doneOutcomeLine(done({}))).toBe('готово в 14:12');
  });
  it('failed → «упала: {модель}»', () => {
    expect(doneOutcomeLine(done({ outcome: 'failed' }))).toBe('упала: MiniMax H3');
  });
  it('cancelled → «отменено»', () => {
    expect(doneOutcomeLine(done({ outcome: 'cancelled' }))).toBe('отменено');
  });
});

// === Итог «последнего часа» ===

describe('lastHourSummary: итог + «и ещё N»', () => {
  const mk = (n: number): MediaDone[] =>
    Array.from({ length: n }).map((_, i) => ({
      kind: 'video', op: 'Видео · M', own: true, outcome: i % 3 === 0 ? 'failed' : 'completed',
      runSeconds: 120, etaSeconds: 100, finishedAt: Date.now() - i * 60_000,
    }));
  it('считает готово/упало и среднее превышение', () => {
    const s = lastHourSummary(mk(3)); // 3 прогона: i=0 failed, i=1,2 completed
    expect(s.done).toBe(2);
    expect(s.failed).toBe(1);
    // (120-100)/100 = 0.2 по всем трём
    expect(s.avgOverPerc).toBeCloseTo(0.2, 5);
    expect(s.more).toBe(0);
  });
  it('больше 8 → «и ещё N»', () => {
    const s = lastHourSummary(mk(10));
    expect(s.more).toBe(10 - LAST_HOUR_VISIBLE);
  });
  it('пусто → нули, avg null', () => {
    const s = lastHourSummary([]);
    expect(s).toEqual({ done: 0, failed: 0, avgOverPerc: null, more: 0 });
  });
});

// === Прогресс и «осталось» ===

describe('runningProgress: полоса по шагам/percent', () => {
  it('шаги → exact, fraction=step/total (предел 0.99)', () => {
    const p = runningProgress({ steps: { step: 12, total: 25 } } as MediaRunning);
    expect(p.mode).toBe('exact');
    expect(p.fraction).toBeCloseTo(12 / 25, 5);
  });
  it('шаги 99/100 → 0.99 (предел)', () => {
    const p = runningProgress({ steps: { step: 99, total: 100 } } as MediaRunning);
    expect(p.fraction).toBe(0.99);
  });
  it('percentExact → exact по percent', () => {
    const p = runningProgress({ percent: 40, percentExact: true } as MediaRunning);
    expect(p.mode).toBe('exact');
    expect(p.fraction).toBe(0.4);
  });
  it('percent без exact → estimate (предел 0.95)', () => {
    const p = runningProgress({ percent: 90, percentExact: false } as MediaRunning);
    expect(p.mode).toBe('estimate');
    expect(p.fraction).toBe(0.9);
  });
  it('ничего → indeterminate', () => {
    expect(runningProgress({} as MediaRunning).mode).toBe('indeterminate');
  });
});

describe('remainingLine: «осталось ≈ N» / «время не замерено»', () => {
  it('remainingSeconds → «осталось ≈ N мин»', () => {
    expect(remainingLine({ remainingSeconds: 120 } as MediaRunning)).toBe('осталось ≈ 2 мин');
  });
  it('без ETA → «время не замерено»', () => {
    expect(remainingLine({ remainingSeconds: null } as MediaRunning)).toBe('время не замерено');
  });
});

// === Чей прогон: свой (с переходом) / чужой (без) ===

describe('isOwnJob / jobOwnerLabel: анонимизация', () => {
  it('свой с sessionId → true (кнопка, переход)', () => {
    expect(isOwnJob({ own: true, sessionId: 's1' })).toBe(true);
  });
  it('свой БЕЗ sessionId → false (без перехода): мутация «показывать sessionId чужого» красит', () => {
    expect(isOwnJob({ own: true, sessionId: null })).toBe(false);
  });
  it('чужой → false', () => {
    expect(isOwnJob({ own: false, sessionId: 's1' })).toBe(false);
  });
  it('чужой → «Чат другого пользователя», свой → название', () => {
    expect(jobOwnerLabel({ own: false, title: 'Секрет' })).toBe('Чат другого пользователя');
    expect(jobOwnerLabel({ own: true, title: 'Мой чат' })).toBe('Мой чат');
    expect(jobOwnerLabel({ own: true, title: null })).toBe('—');
  });
});

// === Счётчики/подписи ===

describe('slotsLine / queueJobLabel', () => {
  it('«N из M мест занято»', () => {
    expect(slotsLine({ used: 3, limit: 4 })).toBe('3 из 4 мест занято');
  });
  it('«N. операция»', () => {
    expect(queueJobLabel({ position: 2, op: 'Картинка · Qwen-Image 2.1' } as never)).toBe('2. Картинка · Qwen-Image 2.1');
  });
});

// === История (ADR-030): длительность и время ===

describe('formatDurationHuman: секунды → человек', () => {
  it('0 → «0 с», <60 → «N с»', () => {
    expect(formatDurationHuman(0)).toBe('0 с');
    expect(formatDurationHuman(42)).toBe('42 с');
  });
  it('<3600 → «N мин M с», ровно по минуте → «N мин»', () => {
    expect(formatDurationHuman(80)).toBe('1 мин 20 с');
    expect(formatDurationHuman(360)).toBe('6 мин');
  });
  it('час+ → «N ч MM мин»', () => {
    expect(formatDurationHuman(3661)).toBe('1 ч 01 мин');
  });
  it('null / отрицательное → «—»', () => {
    expect(formatDurationHuman(null)).toBe('—');
    expect(formatDurationHuman(-1)).toBe('—');
  });
});

describe('formatClockFromIso / formatClockSecFromIso / formatDayTimeFromIso: ISO → местное время', () => {
  const iso = new Date(2026, 9, 15, 14, 32, 0).toISOString();
  it('«14:32» / «14:32:00» / «15 октября, 14:32»', () => {
    expect(formatClockFromIso(iso)).toBe('14:32');
    expect(formatClockSecFromIso(iso)).toBe('14:32:00');
    expect(formatDayTimeFromIso(iso)).toBe('15 октября, 14:32');
  });
  it('null / некорректно → пусто', () => {
    expect(formatClockFromIso(null)).toBe('');
    expect(formatClockFromIso('not-a-date')).toBe('');
    expect(formatClockSecFromIso(null)).toBe('');
    expect(formatDayTimeFromIso(null)).toBe('');
  });
});

describe('dayGroupLabel: «Сегодня» / «Вчера» / «N месяца»', () => {
  const now = new Date(2026, 9, 15, 14, 0, 0).getTime();
  it('сегодня → «Сегодня»', () => {
    expect(dayGroupLabel(new Date(2026, 9, 15, 10, 0, 0).toISOString(), now)).toBe('Сегодня');
  });
  it('вчера → «Вчера»', () => {
    expect(dayGroupLabel(new Date(2026, 9, 14, 10, 0, 0).toISOString(), now)).toBe('Вчера');
  });
  it('раньше → «13 октября» (без года)', () => {
    expect(dayGroupLabel(new Date(2026, 9, 13, 10, 0, 0).toISOString(), now)).toBe('13 октября');
  });
  it('null → пусто', () => {
    expect(dayGroupLabel(null, now)).toBe('');
  });
});

// === История: вид, операция, заголовок строки ===

describe('historyKindLabel / historyOpLabel / historyRowTitle', () => {
  it('вид: известные + неизвестный/отсутствующий → «Генерация»', () => {
    expect(historyKindLabel('image')).toBe('Картинка');
    expect(historyKindLabel('video')).toBe('Видео');
    expect(historyKindLabel('audio')).toBe('Звук');
    expect(historyKindLabel('train')).toBe('Обучение');
    expect(historyKindLabel('other')).toBe('Генерация');
    expect(historyKindLabel(null)).toBe('Генерация');
    expect(historyKindLabel('weird')).toBe('Генерация');
  });
  it('операция: известные → человек, неизвестная — как есть, null → пусто', () => {
    expect(historyOpLabel('generate_image')).toBe('по тексту');
    expect(historyOpLabel('image_to_video')).toBe('из картинки');
    expect(historyOpLabel('music_generate')).toBe('песня');
    expect(historyOpLabel('lora_train')).toBe('обучение LoRA');
    expect(historyOpLabel('weird_op')).toBe('weird_op');
    expect(historyOpLabel(null)).toBe('');
    expect(historyOpLabel('')).toBe('');
  });
  it('заголовок: «Вид · операция»; без операции — только вид', () => {
    expect(historyRowTitle('image', 'generate_image')).toBe('Картинка · по тексту');
    expect(historyRowTitle('video', 'image_to_video')).toBe('Видео · из картинки');
    expect(historyRowTitle('audio', 'music_generate')).toBe('Звук · песня');
    expect(historyRowTitle('image', null)).toBe('Картинка');
    expect(historyRowTitle(null, null)).toBe('Генерация');
  });
});

// === История: 410, ошибки, сводка, очередь, параметры, период, маркер, бейдж ===

describe('goneReasonText: пять причин 410 + неизвестная', () => {
  it('expired / missing / project_gone / local_project / source_disabled', () => {
    expect(goneReasonText('expired')).toBe('Истёк срок хранения: рабочие файлы редактора живут 7 дней.');
    expect(goneReasonText('missing')).toBe('Файл удалили.');
    expect(goneReasonText('project_gone')).toBe('Проект удалён.');
    expect(goneReasonText('local_project')).toBe('Проект переехал на устройство — его файлы больше не на сервере.');
    expect(goneReasonText('source_disabled')).toBe('Редактор, в котором делали генерацию, выключен на этом сервере.');
  });
  it('null / пусто / неизвестный → «Файл недоступен.»', () => {
    expect(goneReasonText(null)).toBe('Файл недоступен.');
    expect(goneReasonText('')).toBe('Файл недоступен.');
    expect(goneReasonText('weird')).toBe('Файл недоступен.');
  });
});

describe('humanErrorPhrase: OOM → человеческая фраза, иначе общий текст', () => {
  it('«out of memory» (регистр не важен) → «не хватило памяти видеокарты»', () => {
    expect(humanErrorPhrase('CUDA out of memory')).toBe('Ошибка: не хватило памяти видеокарты.');
    expect(humanErrorPhrase('OUT OF MEMORY')).toBe('Ошибка: не хватило памяти видеокарты.');
  });
  it('прочие / null → «Ошибка при расчёте.»', () => {
    expect(humanErrorPhrase('some other error')).toBe('Ошибка при расчёте.');
    expect(humanErrorPhrase(null)).toBe('Ошибка при расчёте.');
  });
});

describe('historySummaryLine: «Готово N · отменено N · …», только ненулевые', () => {
  it('все четыре → полный ряд', () => {
    expect(historySummaryLine({ outcomes: { completed: 290, cancelled: 4, failed: 12, interrupted: 6 } }))
      .toBe('Готово 290 · отменено 4 · ошибок 12 · прервано 6');
  });
  it('одно → одна часть; нули → null; null → null', () => {
    expect(historySummaryLine({ outcomes: { completed: 5 } })).toBe('Готово 5');
    expect(historySummaryLine({ outcomes: {} })).toBeNull();
    expect(historySummaryLine(null)).toBeNull();
  });
  it('нулевые части не показываются', () => {
    expect(historySummaryLine({ outcomes: { completed: 0, cancelled: 3 } })).toBe('отменено 3');
  });
});

describe('historyWaitSeconds / historyWaitLine: ожидание = started − enqueued', () => {
  const enq = '2026-10-15T14:00:00Z';
  const start = '2026-10-15T14:01:20Z';
  it('80 с ожидания; без started → null', () => {
    expect(historyWaitSeconds(enq, start)).toBe(80);
    expect(historyWaitSeconds(enq, null)).toBeNull();
    expect(historyWaitSeconds(null, start)).toBeNull();
  });
  it('«очередь 1 мин 20 с»; без старта — «очередь —»', () => {
    expect(historyWaitLine(enq, start)).toBe('очередь 1 мин 20 с');
    expect(historyWaitLine(enq, null)).toBe('очередь —');
  });
});

describe('historyRowTime: время запуска + длительность, «10:24 · 39 с»', () => {
  const enq = new Date(2026, 9, 15, 9, 58, 0).toISOString();
  const start = new Date(2026, 9, 15, 10, 24, 0).toISOString();
  it('старт + длительность → «ЧЧ:ММ · N с»', () => {
    expect(historyRowTime({ enqueuedAt: enq, startedAt: start, runSeconds: 39 })).toBe('10:24 · 39 с');
  });
  it('длительность ≥ 60 с → «1 мин 12 с»', () => {
    expect(historyRowTime({ enqueuedAt: enq, startedAt: start, runSeconds: 72 })).toBe('10:24 · 1 мин 12 с');
  });
  it('без runSeconds → только время, без « · »', () => {
    expect(historyRowTime({ enqueuedAt: enq, startedAt: start, runSeconds: null })).toBe('10:24');
  });
  it('без startedAt → время постановки (enqueuedAt)', () => {
    expect(historyRowTime({ enqueuedAt: enq, startedAt: null, runSeconds: null })).toBe('09:58');
  });
  it('без всего → пустая строка', () => {
    expect(historyRowTime({ enqueuedAt: null, startedAt: null, runSeconds: null })).toBe('');
  });
});

describe('seedFormatted / historyVariantCount: seed и варианты', () => {
  it('seed → группы по три с пробелами; null → «не задан»', () => {
    expect(seedFormatted(2914337016)).toBe('2 914 337 016');
    expect(seedFormatted(1234)).toBe('1 234');
    expect(seedFormatted(null)).toBe('не задан');
  });
  it('варианты: count из params; не число/отсутствует → 1', () => {
    expect(historyVariantCount({ count: 4 })).toBe(4);
    expect(historyVariantCount({ count: 1 })).toBe(1);
    expect(historyVariantCount({ count: 0 })).toBe(1);
    expect(historyVariantCount({ count: 'x' as never })).toBe(1);
    expect(historyVariantCount(null)).toBe(1);
  });
});

describe('historyParamsRows: фиксированный порядок, пустые не выводим', () => {
  it('полная запись: модель, операция, размер, длительность, варианты, шаги, seed, lora', () => {
    const rows = historyParamsRows({
      model: 'Qwen-Image 2.1', op: 'generate_image',
      params: { width: 1024, height: 1024, durationSec: 5, count: 4, steps: 25, seed: 2914337016, lora: 'style.safetensors' },
    });
    expect(rows.map(r => r.key)).toEqual(['Модель', 'Операция', 'Размер', 'Длительность', 'Вариантов', 'Шаги', 'Seed', 'LoRA']);
    expect(rows[0].value).toBe('Qwen-Image 2.1');
    expect(rows[2].value).toBe('1024 × 1024');
    expect(rows[3].value).toBe('5 с');
    expect(rows[4].value).toBe('4');
    expect(rows[5].value).toBe('25');
    expect(rows[6].value).toBe('2 914 337 016');
    expect(rows[7].value).toBe('style.safetensors');
  });
  it('без параметров: только Seed «не задан» (его отсутствие в макете показано явно)', () => {
    const rows = historyParamsRows({ model: null, op: null, params: null });
    expect(rows).toEqual([{ key: 'Seed', value: 'не задан', mono: false }]);
  });
  it('count=1 → строки «Вариантов» нет', () => {
    const rows = historyParamsRows({ model: 'm', op: null, params: { count: 1 } });
    expect(rows.some(r => r.key === 'Вариантов')).toBe(false);
  });
});

describe('historyTimeRows: «Время и карта»', () => {
  it('полная запись: поставлена, ожидание, расчёт, карта «не сообщает»', () => {
    const rows = historyTimeRows({
      enqueuedAt: '2026-10-15T14:00:00Z', startedAt: '2026-10-15T14:01:20Z', runSeconds: 42,
    });
    expect(rows.map(r => r.key)).toEqual(['Поставлена', 'Ожидание', 'Расчёт', 'Карта']);
    expect(rows[1].value).toBe('1 мин 20 с');
    expect(rows[2].value).toBe('42 с');
    expect(rows[3]).toMatchObject({ value: 'не сообщает', na: true });
  });
  it('без старта: ожидание «неизвестно»; без расчёта: «не начинался»', () => {
    const rows = historyTimeRows({ enqueuedAt: '2026-10-15T14:00:00Z', startedAt: null, runSeconds: null });
    expect(rows[1]).toMatchObject({ value: 'неизвестно', na: true });
    expect(rows[2]).toMatchObject({ value: 'не начинался', na: true });
  });
});

describe('historyPeriodTail / historyPeriodFrom: период и хвост ленты', () => {
  it('подписи хвоста', () => {
    expect(historyPeriodTail('today')).toBe('Это все генерации за сегодня');
    expect(historyPeriodTail('24h')).toBe('Это все генерации за 24 часа');
    expect(historyPeriodTail('7d')).toBe('Это все генерации за 7 дней');
  });
  it('from: сегодня → местная полночь; 24h/7d → назад от now', () => {
    const now = new Date(2026, 9, 15, 14, 0, 0).getTime();
    expect(historyPeriodFrom('today', now)).toBe(new Date(2026, 9, 15, 0, 0, 0).toISOString());
    expect(historyPeriodFrom('24h', now)).toBe(new Date(now - 24 * 3600 * 1000).toISOString());
    expect(historyPeriodFrom('7d', now)).toBe(new Date(now - 7 * 24 * 3600 * 1000).toISOString());
  });
});

describe('liveCompletionMarker: маркер живых завершений', () => {
  it('null → пусто; строка lastHour + числа running/queue', () => {
    expect(liveCompletionMarker(null)).toBe('');
    const marker = liveCompletionMarker({
      lastHour: [{ finishedAt: 1700000000000, op: 'Видео 5 с · MiniMax H3' }],
      running: [{ heavy: false }], queue: [{}, {}],
    });
    expect(marker).toBe('1700000000000:Видео 5 с · MiniMax H3|1|2');
  });
  it('изменение finishedAt/op/чисел меняет маркер', () => {
    const a = liveCompletionMarker({ lastHour: [{ finishedAt: 1, op: 'x' }], running: [], queue: [] });
    const b = liveCompletionMarker({ lastHour: [{ finishedAt: 2, op: 'x' }], running: [], queue: [] });
    expect(a).not.toBe(b);
  });
});

describe('historyOutcomeBadge: статус → бейдж', () => {
  it('завершённые — по исходу; идущие/ждущие — нейтральные; unknown — без бейджа', () => {
    expect(historyOutcomeBadge('completed')).toEqual({ label: 'готово', tone: 'ok' });
    expect(historyOutcomeBadge('cancelled')).toEqual({ label: 'отменено', tone: 'cancel' });
    expect(historyOutcomeBadge('failed')).toEqual({ label: 'ошибка', tone: 'fail' });
    expect(historyOutcomeBadge('interrupted')).toEqual({ label: 'прервано', tone: 'int' });
    expect(historyOutcomeBadge('running')).toEqual({ label: 'идёт', tone: 'neutral' });
    expect(historyOutcomeBadge('queued')).toEqual({ label: 'ждёт', tone: 'neutral' });
    expect(historyOutcomeBadge('unknown')).toBeNull();
    expect(historyOutcomeBadge(null)).toBeNull();
  });
});
