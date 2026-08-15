import { describe, it, expect } from 'vitest';
import {
  handsFreeReducer, HANDS_FREE_INITIAL, BARREN_WARN, BARREN_OFF,
  type HandsFreeState, type HandsFreeEvent,
} from '../useHandsFree';

// Автомат петли разговора. Компонентных тестов в репе нет, поэтому вся логика петли
// живёт чистым редьюсером — здесь и проверяется, включая защиту от эха.
function run(events: HandsFreeEvent[], from: HandsFreeState = HANDS_FREE_INITIAL): HandsFreeState {
  return events.reduce(handsFreeReducer, from);
}

const listening = () => run([{ type: 'toggle' }]);

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
