import { describe, expect, it } from 'vitest';
import { clipsOf, trainProblem } from './TrainVoiceForm';

describe('«Обучить голос»: что мешает запуску', () => {
  it('нужны имя и хотя бы одна запись', () => {
    expect(trainProblem('', ['a.wav'])).toBe('Дайте голосу имя');
    expect(trainProblem('Андрей', [])).toMatch(/Добавьте записи/);
    expect(trainProblem('Андрей', ['  ', ''])).toMatch(/Добавьте записи/);
    expect(trainProblem(' Андрей ', ['records/a.wav'])).toBeNull();
  });
  it('пустые строки и пробелы по краям записей не едут', () => {
    expect(clipsOf([' a.wav ', '', '  ', 'b.wav'])).toEqual(['a.wav', 'b.wav']);
  });
});
