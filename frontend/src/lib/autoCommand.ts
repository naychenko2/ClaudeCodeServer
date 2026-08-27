// Авто-слэш-команды в ленте чата (QA Fold 8): компактный чип-разделитель вместо
// карточки агента. Команда — только текст, начинающийся с `/`. Прочие авто-сообщения
// (промпт задачи «## ЗАДАЧА…», доклад «↩ Отчёт…», автоматизации) тоже приходят с
// auto, но чип им не положен — они рендерятся карточкой.

/** Токен команды, если текст — слэш-команда; иначе null. */
export function detectAutoCommand(text: string): string | null {
  const m = /^(\/\S+)/.exec(text.trim());
  return m ? m[1] : null;
}

/** Cancel-команды семейства oh-my-claudecode:cancel* (+ стоп-слова). */
export const isCancelCommand = (cmd: string): boolean =>
  /cancel|stop|abort|прервать/i.test(cmd);
