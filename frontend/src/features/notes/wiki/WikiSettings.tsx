// Контейнер подвкладки «Настройки» (S7d-3, макет llm-wiki-v2 экран «Настройки»): стопка блоков
// в порядке макета — «Ночной разбор», «Применение», «Отбор чатов». Сами блоки свои (S7d-1/S7d-2),
// у каждого своя шапка и свои отступы: контейнер не добавляет своей шапки и разделителей —
// шапки блоков и есть заголовки разделов.
//
// «Ночной разбор» (WikiNightlyBlock, F2) идёт первым — порядок макета (строка 1 nav'а настроек).
// Шапку «Настройки вики» и подвал приватности (llm-wiki-v2.html:847–850) контейнер не добавляет:
// в макете это шапка и подвал НАВИГАЦИИ настроек (списка строк), а не стопки блоков; шапки
// блоков и есть заголовки разделов (решение S7d-3).
//
// selectedKey не читается: настройки и отбор относятся ко всей базе, не к выбранному разделу —
// ни у настроек, ни у отбора раздела в запросе нет (GET /api/wiki/settings, /api/wiki/chats).

import { WikiNightlyBlock } from './WikiNightlyBlock';
import { WikiApplySettings } from './WikiApplySettings';
import { WikiChatTriageList } from './WikiChatTriageList';

export function WikiSettings() {
  return (
    <div>
      <WikiNightlyBlock />
      <WikiApplySettings />
      <WikiChatTriageList />
    </div>
  );
}
