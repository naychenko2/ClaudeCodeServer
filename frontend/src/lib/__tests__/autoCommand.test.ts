import { describe, expect, it } from 'vitest';
import { detectAutoCommand, isCancelCommand } from '../autoCommand';

// Детект авто-слэш-команд для компактного чипа в ленте. Регрессия 03fc0783:
// детект брал первый токен ЛЮБОГО авто-сообщения, и постановка задачи
// исполнителю («## ЗАДАЧА…») рисовалась чипом «Команда · «##»» вместо карточки.

describe('detectAutoCommand', () => {
  it('распознаёт слэш-команду', () => {
    expect(detectAutoCommand('/oh-my-claudecode:cancel')).toBe('/oh-my-claudecode:cancel');
    expect(detectAutoCommand('/loop')).toBe('/loop');
  });

  it('допускает ведущие пробелы перед /', () => {
    expect(detectAutoCommand('  /ralph вычисти состояние')).toBe('/ralph');
  });

  it('не считает командой авто-сообщения без /', () => {
    // промпт задачи исполнителю (персона и обычный режим)
    expect(detectAutoCommand('## ЗАДАЧА\nВыполни задачу из трекера (id задачи: 123).')).toBeNull();
    expect(detectAutoCommand('Выполни задачу из трекера (id задачи: 123).\n\n# Починить билд')).toBeNull();
    // доклад исполнителя без персоны
    expect(detectAutoCommand('↩ Отчёт по делегированной задаче: готово')).toBeNull();
    // произвольный текст автоматизации
    expect(detectAutoCommand('Проверь статус сервера')).toBeNull();
    expect(detectAutoCommand('')).toBeNull();
  });
});

describe('isCancelCommand', () => {
  it('ловит cancel-семейство и стоп-слова', () => {
    expect(isCancelCommand('/oh-my-claudecode:cancel')).toBe(true);
    expect(isCancelCommand('/cancel')).toBe(true);
    expect(isCancelCommand('/loop')).toBe(false);
    expect(isCancelCommand('/ralph')).toBe(false);
  });
});
