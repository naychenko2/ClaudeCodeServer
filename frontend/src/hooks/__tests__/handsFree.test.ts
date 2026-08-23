import { describe, it, expect } from 'vitest';
import {
  handsFreeReducer, HANDS_FREE_INITIAL, BARREN_WARN, BARREN_OFF, isStopCommand,
  pendingDelayFor, PENDING_FAST_MS, PENDING_MS, PENDING_SLOW_MS,
  type HandsFreeState, type HandsFreeEvent,
} from '../useHandsFree';

// Автомат петли разговора. Компонентных тестов в репе нет, поэтому вся логика петли
// живёт чистым редьюсером — здесь и проверяется, включая защиту от эха.
function run(events: HandsFreeEvent[], from: HandsFreeState = HANDS_FREE_INITIAL): HandsFreeState {
  return events.reduce(handsFreeReducer, from);
}

const listening = () => run([{ type: 'toggle' }]);

describe('isStopCommand', () => {
  it.each([
    ['стоп'], ['Стоп'], ['стоп.'], ['стоп,'], [' хватит '],
    ['Отбой!'], ['конец связи'], ['Выключи разговор'], ['достаточно'],
  ])('«%s» — команда выхода', (text) => {
    expect(isStopCommand(text)).toBe(true);
  });

  it.each([
    ['стоп а теперь расскажи про моря'],
    ['хватит денег ушло на такси'],
    ['расскажи про стоп'],
    ['выключи свет на кухне'],
    [''],
  ])('«%s» — обычная речь, не команда', (text) => {
    expect(isStopCommand(text)).toBe(false);
  });
});

describe('handsFreeReducer', () => {
  it('полный круг разговора', () => {
    let s = listening();
    expect(s.phase).toBe('listening');

    s = handsFreeReducer(s, { type: 'recognized', text: 'привет' });
    expect(s.phase).toBe('pending');
    expect(s.buffer).toBe('привет');

    s = handsFreeReducer(s, { type: 'pendingElapsed' });
    expect(s.phase).toBe('sending');

    s = handsFreeReducer(s, { type: 'turnStarted' });
    expect(s.phase).toBe('waiting');
    expect(s.buffer).toBe('');

    s = handsFreeReducer(s, { type: 'speechWillStart' });
    expect(s.phase).toBe('speaking');

    s = handsFreeReducer(s, { type: 'speechFinished' });
    expect(s.phase).toBe('listening');
    expect(s.buffer).toBe('');
  });

  it('тап в окне отмены выключает петлю', () => {
    const s = run([{ type: 'recognized', text: 'ой не то' }, { type: 'toggle' }], listening());
    expect(s.phase).toBe('off');
    expect(s.buffer).toBe('');
  });

  it('голосовая «стоп» выключает петлю в фазе слушания, буфер не отправляется', () => {
    let s = listening();
    s = handsFreeReducer(s, { type: 'recognized', text: 'привет' });
    expect(s.phase).toBe('pending');
    // Продолжение в окне отмены: человек передумал и сказал «стоп» — это команда
    s = handsFreeReducer(s, { type: 'recognized', text: 'Стоп.' });
    expect(s.phase).toBe('off');
    expect(s.notice).toBe('voiceOff');
    expect(s.buffer).toBe('');
  });

  it('«стоп» в окне отмены гасит и накопленный буфер', () => {
    // Сценарий: наговорил текст → пауза → окно взводится → сказал «стоп». Буфер
    // обязан умереть вместе с петлёй, иначе он уйдёт в чат следующим ходом
    const s = run([
      { type: 'recognized', text: 'напиши сочинение' },
      { type: 'cycleEnded' },
      { type: 'recognized', text: 'стоп' },
    ], listening());
    expect(s.phase).toBe('off');
    expect(s.buffer).toBe('');
    expect(s.notice).toBe('voiceOff');
  });

  it('фраза со «стоп» внутри — обычная речь, петля работает дальше', () => {
    let s = listening();
    s = handsFreeReducer(s, { type: 'recognized', text: 'стоп а теперь продолжай' });
    expect(s.phase).toBe('pending');
    expect(s.buffer).toBe('стоп а теперь продолжай');
    // Окно отправки взводится как обычно
    s = handsFreeReducer(s, { type: 'cycleEnded' });
    expect(s.phase).toBe('pending');
  });

  it('речь в окне отмены дописывает буфер и снимает таймер', () => {
    let s = handsFreeReducer(listening(), { type: 'recognized', text: 'первое' });
    expect(s.phase).toBe('pending');
    s = handsFreeReducer(s, { type: 'recognized', text: 'второе' });
    expect(s.phase).toBe('listening');   // окно снято, слушаем дальше
    expect(s.buffer).toBe('первое второе');
    // Замолчал — окно взводится снова с накопленным текстом
    s = handsFreeReducer(s, { type: 'cycleEnded' });
    expect(s.phase).toBe('pending');
    expect(s.buffer).toBe('первое второе');
  });

  it('молчание не отправляет пустое сообщение', () => {
    const s = run([{ type: 'pendingElapsed' }], { ...listening(), phase: 'pending', buffer: '   ' });
    expect(s.phase).toBe('listening');
    expect(s.buffer).toBe('');
  });

  it('вопрос модели выводит из петли', () => {
    const s = run([
      { type: 'recognized', text: 'сделай' }, { type: 'pendingElapsed' },
      { type: 'turnStarted' }, { type: 'needsDecision' },
    ], listening());
    expect(s.phase).toBe('off');
    expect(s.notice).toBe('needDecision');
  });

  it('вопрос посреди озвучки не рвёт ответ — выходим после него', () => {
    let s = run([
      { type: 'recognized', text: 'сделай' }, { type: 'pendingElapsed' },
      { type: 'turnStarted' }, { type: 'speechWillStart' }, { type: 'needsDecision' },
    ], listening());
    expect(s.phase).toBe('speaking');
    expect(s.pendingExit).toBe(true);
    s = handsFreeReducer(s, { type: 'speechFinished' });
    expect(s.phase).toBe('off');
    expect(s.notice).toBe('needDecision');
  });

  it('бесплодные циклы: три подряд — предупреждение, пять — выключение', () => {
    let s = listening();
    for (let i = 0; i < BARREN_WARN; i++) s = handsFreeReducer(s, { type: 'cycleEnded' });
    // Предупреждение — это речь петли: микрофон обязан быть закрыт, иначе синтез
    // услышит сам себя и петля заговорит сама с собой
    expect(s.phase).toBe('speaking');
    expect(s.notice).toBe('stillThere');
    expect(s.noticeSpeech).toBe(true);

    // Реплика дочитана — слушаем дальше, но счётчик бесплодных циклов не обнулился,
    // иначе автовыключение не наступило бы никогда
    s = handsFreeReducer(s, { type: 'noticeSaid' });
    s = handsFreeReducer(s, { type: 'speechFinished' });
    expect(s.phase).toBe('listening');
    expect(s.barren).toBe(BARREN_WARN);
    expect(s.warned).toBe(true);

    for (let i = BARREN_WARN; i < BARREN_OFF - 1; i++) s = handsFreeReducer(s, { type: 'cycleEnded' });
    expect(s.phase).toBe('listening');
    s = handsFreeReducer(s, { type: 'cycleEnded' });
    expect(s.phase).toBe('off');
    expect(s.notice).toBe('idleOff');
  });

  it('ошибка и конец одного цикла считаются за единицу', () => {
    // Web Speech на пустом цикле шлёт onerror('no-speech') И следом onend — без этого
    // счётчик рос бы вдвое, и предупреждение звучало бы после полутора циклов
    let s = listening();
    s = handsFreeReducer(s, { type: 'cycleError', code: 'no-speech' });
    expect(s.barren).toBe(0);
    s = handsFreeReducer(s, { type: 'cycleEnded' });
    expect(s.barren).toBe(1);
    s = handsFreeReducer(s, { type: 'cycleError', code: 'network' });
    s = handsFreeReducer(s, { type: 'cycleEnded' });
    expect(s.barren).toBe(2);
  });

  it('ошибка с накопленным текстом всё равно взводит окно отправки', () => {
    const s = run([{ type: 'recognized', text: 'а' }, { type: 'recognized', text: 'б' },
      { type: 'cycleError', code: 'network' }], listening());
    expect(s.phase).toBe('pending');
    expect(s.buffer).toBe('а б');
  });

  it('неудачная отправка возвращает петлю в слушание', () => {
    const waiting = run([
      { type: 'recognized', text: 'а' }, { type: 'pendingElapsed' }, { type: 'turnStarted' },
    ], listening());
    expect(waiting.phase).toBe('waiting');
    expect(handsFreeReducer(waiting, { type: 'sendFailed' }).phase).toBe('listening');
  });

  it('свой abort циклом не считается', () => {
    const s = handsFreeReducer(listening(), { type: 'cycleError', code: 'aborted' });
    expect(s.barren).toBe(0);
  });

  it('распознанная речь обнуляет счётчик бесплодных циклов', () => {
    let s = run([{ type: 'cycleEnded' }, { type: 'cycleEnded' }], listening());
    expect(s.barren).toBe(2);
    s = handsFreeReducer(s, { type: 'recognized', text: 'я тут' });
    expect(s.barren).toBe(0);
    expect(s.warned).toBe(false);
  });

  it('во время озвучки микрофон не открывается ничем', () => {
    const speaking = run([
      { type: 'recognized', text: 'а' }, { type: 'pendingElapsed' },
      { type: 'turnStarted' }, { type: 'speechWillStart' },
    ], listening());
    expect(speaking.phase).toBe('speaking');
    for (const e of [
      { type: 'speechSkipped' }, { type: 'cycleEnded' }, { type: 'pendingElapsed' },
      { type: 'recognized', text: 'эхо собственного голоса' },
    ] as HandsFreeEvent[]) {
      expect(handsFreeReducer(speaking, e).phase).toBe('speaking');
    }
  });

  it('озвучка, начавшаяся после страховки, немедленно закрывает микрофон', () => {
    const s = handsFreeReducer({ ...listening(), phase: 'listening' }, { type: 'speechWillStart' });
    expect(s.phase).toBe('speaking');
  });

  it('ожидание хода выходит в слушание только по страховке или озвучке', () => {
    const waiting = run([
      { type: 'recognized', text: 'а' }, { type: 'pendingElapsed' }, { type: 'turnStarted' },
    ], listening());
    expect(waiting.phase).toBe('waiting');
    // Никакое событие «ход кончился» само по себе микрофон не открывает: прямого
    // перехода turnEnded → listening в автомате нет сознательно (Р13)
    for (const e of [
      { type: 'cycleEnded' }, { type: 'cycleError', code: 'network' },
      { type: 'recognized', text: 'шум' }, { type: 'turnStarted' },
    ] as HandsFreeEvent[]) {
      expect(handsFreeReducer(waiting, e).phase).not.toBe('listening');
    }
    expect(handsFreeReducer(waiting, { type: 'speechSkipped' }).phase).toBe('listening');
  });

  it('офлайн и мёртвый движок гасят петлю', () => {
    expect(handsFreeReducer(listening(), { type: 'offline' }).phase).toBe('off');
    const dead = handsFreeReducer(listening(), { type: 'micDead' });
    expect(dead.phase).toBe('off');
    expect(dead.notice).toBe('micDead');
  });

  it('в выключенном состоянии внешние события ничего не будят', () => {
    for (const e of [
      { type: 'recognized', text: 'мимо' }, { type: 'speechWillStart' },
      { type: 'needsDecision' }, { type: 'idleTimeout' }, { type: 'offline' },
    ] as HandsFreeEvent[]) {
      expect(handsFreeReducer(HANDS_FREE_INITIAL, e)).toEqual(HANDS_FREE_INITIAL);
    }
  });

  it('бездействие минуту выключает разговор', () => {
    const s = handsFreeReducer(listening(), { type: 'idleTimeout' });
    expect(s.phase).toBe('off');
    expect(s.notice).toBe('idleOff');
  });
});

describe('барж-ин (перебивание голосом)', () => {
  const speaking = () => run([
    { type: 'recognized', text: 'расскажи' }, { type: 'pendingElapsed' },
    { type: 'turnStarted' }, { type: 'speechWillStart' },
  ], listening());

  it('перебивание под озвучку возвращает в слушание с пустым буфером и репликой', () => {
    const s = handsFreeReducer(speaking(), { type: 'bargeIn' });
    expect(s.phase).toBe('listening');
    expect(s.buffer).toBe('');
    // Оборванная на полуслове озвучка неотличима от обрыва связи — петля отвечает
    // словом, что перебивание засчитано
    expect(s.notice).toBe('bargeAck');
  });

  it('вопрос модели важнее перебивания: с pendingExit выходим к решению', () => {
    const withExit = handsFreeReducer(speaking(), { type: 'needsDecision' });
    expect(withExit.pendingExit).toBe(true);
    const s = handsFreeReducer(withExit, { type: 'bargeIn' });
    expect(s.phase).toBe('off');
    expect(s.notice).toBe('needDecision');
  });

  it('перебитая реплика петли гасит noticeSpeech — следующий speechFinished настоящий', () => {
    // «Ты ещё здесь?» → человек перебил → ход → озвучка ответа. Без сброса noticeSpeech
    // конец ЭТОЙ озвучки ушёл бы в ветку реплики и не сбросил счётчик бесплодных циклов
    let s = listening();
    for (let i = 0; i < BARREN_WARN; i++) s = handsFreeReducer(s, { type: 'cycleEnded' });
    expect(s.noticeSpeech).toBe(true);
    s = handsFreeReducer(s, { type: 'bargeIn' });
    expect(s.phase).toBe('listening');
    expect(s.noticeSpeech).toBe(false);
    // Счётчик не обнулился перебиванием: его сбрасывает только распознанная речь
    expect(s.barren).toBe(BARREN_WARN);
    s = run([
      { type: 'recognized', text: 'вопрос' }, { type: 'pendingElapsed' },
      { type: 'turnStarted' }, { type: 'speechWillStart' }, { type: 'speechFinished' },
    ], s);
    expect(s.phase).toBe('listening');
    expect(s.barren).toBe(0);
  });

  it('вне фазы озвучки событие глотается', () => {
    for (const from of [
      listening(),
      handsFreeReducer(listening(), { type: 'recognized', text: 'а' }), // pending
      run([{ type: 'recognized', text: 'а' }, { type: 'pendingElapsed' }, { type: 'turnStarted' }], listening()), // waiting
      HANDS_FREE_INITIAL, // off
    ]) {
      expect(handsFreeReducer(from, { type: 'bargeIn' })).toEqual(from);
    }
  });
});

// Конфликт захватов микрофона пойман на лету: человек говорит, а движок под нашим
// вторым захватом глух. Ждать конца цикла (5-6 с) нельзя — сказанное всё равно
// потеряно, поэтому признаёмся вслух и слушаем заново
describe('нерасслышанная речь (ранний конфликт захватов)', () => {
  it('из слушания уводит в реплику, а не молчит', () => {
    const s = handsFreeReducer(listening(), { type: 'misheard' });
    expect(s.phase).toBe('speaking');
    expect(s.notice).toBe('misheard');
    expect(s.noticeSpeech).toBe(true);
  });

  it('огрызок фразы в буфере не уходит в чат', () => {
    const withTail = run([{ type: 'recognized', text: 'посмотри' }], listening());
    expect(withTail.buffer).not.toBe('');
    expect(handsFreeReducer(withTail, { type: 'misheard' }).buffer).toBe('');
  });

  it('по концу реплики петля возвращается слушать', () => {
    const s = run([{ type: 'misheard' }, { type: 'speechFinished' }], listening());
    expect(s.phase).toBe('listening');
  });

  it('счётчик бесплодных циклов обнуляется: движок был глух не по вине человека', () => {
    const barren = run([{ type: 'cycleEnded' }, { type: 'cycleEnded' }], listening());
    expect(barren.barren).toBeGreaterThan(0);
    expect(handsFreeReducer(barren, { type: 'misheard' }).barren).toBe(0);
  });

  // Вместе со счётчиком снимается и отметка «уже спрашивали»: иначе следующая серия
  // пустых циклов пройдёт без вопроса «Ты ещё здесь?» и разговор выключится молча
  it('отметка о заданном вопросе снимается вместе со счётчиком', () => {
    // BARREN_WARN пустых циклов уводят в реплику «Ты ещё здесь?»; по её концу петля
    // возвращается слушать, неся отметку warned
    const warned = run([
      ...Array.from({ length: BARREN_WARN }, () => ({ type: 'cycleEnded' as const })),
      { type: 'speechFinished' },
    ], listening());
    expect(warned.phase).toBe('listening');
    expect(warned.warned).toBe(true);
    expect(handsFreeReducer(warned, { type: 'misheard' }).warned).toBe(false);
  });

  it.each([
    ['waiting', run([{ type: 'recognized', text: 'да' }, { type: 'pendingElapsed' },
      { type: 'turnStarted' }], listening())],
    ['off', HANDS_FREE_INITIAL],
  ])('в фазе %s микрофон закрыт — вердикт игнорируется', (_phase, from) => {
    expect(handsFreeReducer(from, { type: 'misheard' })).toEqual(from);
  });
});

describe('pendingDelayFor — адаптивное окно отправки', () => {
  it.each([
    ['ну вот смотри я тут подумал и'],
    ['надо бы починить это потому'],
    ['а'],
    ['слушай э'],
  ])('«%s» — мысль оборвана, ждём дольше', (text) => {
    expect(pendingDelayFor(text)).toBe(PENDING_SLOW_MS);
  });

  it.each([
    ['посмотри что там с тестами'],
    ['почему сборка упала на линуксе'],
    ['да'],
    ['погнали'],
    ['готово.'],
  ])('«%s» — реплика дозрела, отправляем быстро', (text) => {
    expect(pendingDelayFor(text)).toBe(PENDING_FAST_MS);
  });

  it.each([
    ['открой файл настроек'],
    [''],
  ])('«%s» — непонятно, прежняя пауза', (text) => {
    expect(pendingDelayFor(text)).toBe(PENDING_MS);
  });

  it('быстрый порог строго короче прежнего, медленный — длиннее', () => {
    expect(PENDING_FAST_MS).toBeLessThan(PENDING_MS);
    expect(PENDING_SLOW_MS).toBeGreaterThan(PENDING_MS);
  });

  it('регистр и пунктуация не мешают распознать хвост', () => {
    expect(pendingDelayFor('Значит, надо сделать И')).toBe(PENDING_SLOW_MS);
    expect(pendingDelayFor('Да!')).toBe(PENDING_FAST_MS);
  });

  // Правило «4+ слов = мысль дозрела» перебивает всё остальное, поэтому длинная
  // фраза, оборванная на местоимении, союзном слове или числительном, улетала
  // через минимальную паузу — замер на планшете поймал ровно эти формы
  it.each([
    ['слушай а что если мы'],
    ['давай посмотрим на файл который'],
    ['проверь пожалуйста вот эти три'],
    ['мне кажется тут дело в том какой'],
    ['открой настройки и покажи мне'],
  ])('«%s…» — длинная фраза оборвана, ждём дольше', (text) => {
    expect(pendingDelayFor(text)).toBe(PENDING_SLOW_MS);
  });

  // Обратная сторона: слова, которыми фразу как раз ЗАКАНЧИВАЮТ, в список
  // висячих не попали — иначе законченная реплика ждала бы впустую
  it.each([
    ['ну давай на этом всё'],
    ['да мне это непонятно вообще'],
    // Винительный падеж — обычный хвост императива, в том числе с предлогом
    ['открой конфиг и посмотри на него'],
    ['проверь ссылки и почини их'],
    ['возьми последнюю версию и накати её'],
    ['а это вообще зачем'],
  ])('«%s» — слово-завершитель не считается висячим', (text) => {
    expect(pendingDelayFor(text)).toBe(PENDING_FAST_MS);
  });
});
