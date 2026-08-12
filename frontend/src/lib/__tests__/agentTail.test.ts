import { describe, it, expect } from 'vitest';
import { splitAgentResultTail, formatTailTokens, formatTailDuration, isBgLaunchResult, asyncLaunchAckNote, bgEmptyAnswerNote } from '../agentTail';

// Реальный формат хвоста из транскриптов CLI: строка agentId (с подсказкой SendMessage
// в скобках) + блок <usage> с переводами строк между парами ключ-значение
const FULL_TAIL =
  "Ответ консультанта по существу.\n\n" +
  "agentId: a011da168d23b9e32 (use SendMessage with to: 'a011da168d23b9e32', summary: '<5-10 word recap>' to continue this agent)\n" +
  '<usage>subagent_tokens: 30161\ntool_uses: 1\nduration_ms: 31510</usage>';

describe('splitAgentResultTail', () => {
  it('вырезает agentId и usage, отдаёт метрики', () => {
    const { body, tail } = splitAgentResultTail(FULL_TAIL);
    expect(body).toBe('Ответ консультанта по существу.');
    expect(tail).toEqual({
      agentId: 'a011da168d23b9e32',
      tokens: 30161,
      toolUses: 1,
      durationMs: 31510,
    });
  });

  it('переживает хвост только с usage (без agentId)', () => {
    const { body, tail } = splitAgentResultTail(
      'Текст.\n<usage>subagent_tokens: 500\ntool_uses: 2\nduration_ms: 900</usage>');
    expect(body).toBe('Текст.');
    expect(tail).toEqual({ tokens: 500, toolUses: 2, durationMs: 900 });
  });

  it('переживает хвост только с agentId (без usage)', () => {
    const { body, tail } = splitAgentResultTail('Текст.\nagentId: abc123');
    expect(body).toBe('Текст.');
    expect(tail).toEqual({ agentId: 'abc123' });
  });

  it('не трогает agentId в середине текста', () => {
    const text = 'В логе видно agentId: xyz — это причина бага.\nИтог: чинить.';
    const { body, tail } = splitAgentResultTail(text);
    expect(body).toBe(text);
    expect(tail).toBeNull();
  });

  it('не трогает текст без хвоста', () => {
    const { body, tail } = splitAgentResultTail('Просто ответ.');
    expect(body).toBe('Просто ответ.');
    expect(tail).toBeNull();
  });

  it('usage в одну строку тоже парсится', () => {
    // ClaudeSession склеивает блоки через AppendLine, но подстрахуемся от однострочного вида
    const { tail } = splitAgentResultTail(
      'Ок.\n<usage>subagent_tokens: 100\ntool_uses: 3\nduration_ms: 4000</usage>');
    expect(tail?.toolUses).toBe(3);
  });
});

describe('isBgLaunchResult', () => {
  it('распознаёт все виды квитанций фонового запуска', () => {
    expect(isBgLaunchResult('Async agent launched successfully.\nagentId: a1\noutput_file: /x')).toBe(true);
    expect(isBgLaunchResult('Workflow launched in background.\nTranscript dir: C:\\tmp\\wf')).toBe(true);
    expect(isBgLaunchResult('Agent resumed from transcript in the background')).toBe(true);
  });

  it('обычный результат и пустые значения — не квитанция', () => {
    expect(isBgLaunchResult('Всё готово, отчёт приложен.')).toBe(false);
    expect(isBgLaunchResult('')).toBe(false);
    expect(isBgLaunchResult(undefined)).toBe(false);
    expect(isBgLaunchResult(null)).toBe(false);
  });
});

describe('форматтеры', () => {
  it('токены', () => {
    expect(formatTailTokens(999)).toBe('999');
    expect(formatTailTokens(30161)).toBe('30k');
    expect(formatTailTokens(1500)).toBe('1,5k');
    expect(formatTailTokens(133903)).toBe('134k');
  });

  it('длительность', () => {
    expect(formatTailDuration(31510)).toBe('32с');
    expect(formatTailDuration(772726)).toBe('12м 53с');
    expect(formatTailDuration(120000)).toBe('2м');
  });
});

describe('asyncLaunchAckNote', () => {
  it('живой агент — «работает в фоне»', () => {
    expect(asyncLaunchAckNote(undefined)).toBe('Агент работает в фоне — его ход виден в списке действий.');
    expect(asyncLaunchAckNote(false)).toBe('Агент работает в фоне — его ход виден в списке действий.');
  });

  it('прерванный агент — про обрыв выдачи, без категоричного «задача не завершена»', () => {
    const note = asyncLaunchAckNote(true);
    expect(note).toBe('Выдача прервана — ответа нет');
    // P8: не утверждаем, что задача не сделана — координатор мог восстановить результат
    expect(note).not.toContain('задача не завершена');
    expect(note).not.toContain('ответа не будет');
  });
});

describe('bgEmptyAnswerNote', () => {
  // P8: подпись тела карточки консультанта при отсутствии ответного текста. Раньше один
  // текст «Агент прерван — ответа не будет» шёл на все случаи — она лгала, когда агент
  // реально отработал (видно «Активность · N действий») или результат получен
  // координатором другим каналом. Ни одна подпись не должна утверждать «ответа не будет».

  it('обрыв + была активность — направляет в Активность, без «ответа не будет»', () => {
    const note = bgEmptyAnswerNote({ settledNoText: true, bgAborted: true, hasToolActivity: true });
    expect(note).toBe('Выдача прервана — детали в Активности');
    expect(note).not.toContain('ответа не будет');
  });

  it('обрыв без активности — честная констатация, без «ответа не будет»', () => {
    const note = bgEmptyAnswerNote({ settledNoText: true, bgAborted: true, hasToolActivity: false });
    expect(note).toBe('Выдача прервана — ответа нет');
    expect(note).not.toContain('ответа не будет');
  });

  it('штатное завершение без текста (не обрыв) — дефолт, никакого «прерван»', () => {
    // Координатор мог получить результат вне карточки; слово «прерван» тут — ложь.
    const note = bgEmptyAnswerNote({ settledNoText: true, bgAborted: false, hasToolActivity: true });
    expect(note).toBeUndefined();
  });

  it('ответный текст есть (включая восстановленный retry) — подписи об обрыве нет', () => {
    // Сценарий «оборвать выдачу, дать координатору восстановить»: если результат попал
    // в поток (settledNoText=false), карточка показывает ответ, а не пометку об обрыве.
    expect(bgEmptyAnswerNote({ settledNoText: false, bgAborted: true, hasToolActivity: true })).toBeUndefined();
    expect(bgEmptyAnswerNote({ settledNoText: false, bgAborted: false, hasToolActivity: false })).toBeUndefined();
  });

  it('агент ещё работает (не settled) — подписи нет', () => {
    expect(bgEmptyAnswerNote({ settledNoText: false, bgAborted: undefined, hasToolActivity: false })).toBeUndefined();
  });
});
