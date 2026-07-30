import { useContext, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { PanelHeaderSlotContext } from './panelHeaderSlotContext';

// Слот контролов в шапке панели — единственный штатный способ положить кнопки,
// переключатели, поле поиска и прочее в шапку своего PanelShell.
//
// Панель объявляет контролы прямо у себя в JSX, а они телепортируются порталом
// в шапку карточки:
//
//   function MyPanel() {
//     const [view, setView] = useState('list');
//     return (
//       <>
//         <PanelHeaderSlot>
//           <IconButton size="sm" title="Обновить" onClick={reload}>…</IconButton>
//         </PanelHeaderSlot>
//         …контент…
//       </>
//     );
//   }
//
// Почему портал, а не проп: состояние контролов почти всегда живёт внутри панели
// (выбранный вид, режим выбора файлов, фильтры). Прокидывать готовый узел наружу
// и обратно — значит держать ReactNode в состоянии владельца и синхронизировать
// его эффектом; ровно так и разъехались когда-то «Изменения» (колбэк onToolbar)
// и «Задачи» (проп panelHeaderExtras + window-событие на кнопку).
//
// Правила:
// - один PanelHeaderSlot на панель (несколько встанут в порядке монтирования —
//   порядок неочевиден, лучше собрать всё в один);
// - слот ищет БЛИЖАЙШИЙ PanelShell вверх по дереву. Панель, вложенная в контент
//   другой панели, попадёт в шапку внешней — это осознанное поведение bare-режима,
//   но во вложенных списках-виджетах слот использовать не стоит;
// - шапки может не быть (мобильный стек, сайдбар без PanelShell). Тогда слот
//   молча ничего не рендерит — проверяй useHasPanelHeader() и рисуй вариант
//   контролов в теле панели.

export function PanelHeaderSlot({ children }: { children: ReactNode }) {
  const { el } = useContext(PanelHeaderSlotContext);
  return el ? createPortal(children, el) : null;
}
