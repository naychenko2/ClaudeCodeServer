# Карточки остановки цикла «до готово» по причине

Макет: [work-loop-stopped-cards-v1.html](work-loop-stopped-cards-v1.html) — светлая/тёмная
тема и десктоп/мобильный 360 переключаются в шапке.

## Проблема

`case 'work_loop_stopped'` в `ChatItemView.tsx` (~1730) рендерит текст с сервера в одной
нейтральной серой пилюле независимо от `item.reason`. У SessionManager пять причин остановки
с разной срочностью и разной длиной текста (`manual`/`error`/`blocked`/`limit`/`waiting_timeout`,
`blocked` — до ~300 символов). Пилюля хороша только для `manual`: длинный текст блокера в ней
тонет без иерархии, а мягкое предупреждение о лимите ходов выглядит так же тревожно, как
реальная ошибка хода.

## Решение

`item.reason` уже приходит на фронт (см. тип `ChatItem`, `kind: 'work_loop_stopped'`) — новых
полей с бэкенда не нужно, различитель есть. Разводим не 5 разных контейнеров, а 4 яруса по
уже существующим в проекте паттернам:

1. **`manual`** — без изменений, тот же клон `interrupted` (нейтральная пилюля).
2. **`limit`** — мягкое предупреждение, тот же клон `rate_limit`/`truncated` (янтарная пилюля),
   иконка `AlertCircle` вместо занятого эмодзи-семейства ⏳/✂ у соседей — держим лексику
   иконок consistent с `lucide-react`, а не эмодзи.
3. **`blocked`, `waiting_timeout`** — «нужно решение человека», не поломка: карточка на всю
   ширину строки (как `case 'error'`, без `alignSelf: center` — длинный текст с переносами в
   пилюле-чипе смотрелся бы криво), но **синяя** палитра `C.info`/`C.infoBg` вместо красной —
   этот дуэт уже используется в проекте как «информационный» тон (`ToolTargetPicker`,
   `bindingMeta.tsx`, `PersonaMemoryPanel` и др.), border — `1px solid ${C.info}` (готовых
   `infoBorder`/`infoText` токенов в шкале нет, паттерн `border: 1px solid ${C.info}` уже
   встречается, например в `PersonaBindingsPanel.tsx`). Иконки разные по смыслу:
   `Ban` для «встал на блокере» (запрещающий знак — точнее `AlertOctagon`, который тоже
   не хуже, но `Ban` короче и однозначнее читается на 13px), `Clock` для таймаута ожидания
   (уже импортирован в файле).
4. **`error`** — без изменений, ровно `case 'error'`: `dangerBg`/`dangerBorder`/`dangerText`,
   `AlertTriangle`, тот же потолок высоты.

Длинный текст (`blocked`/`waiting_timeout`) переносится и упирается в `maxHeight: 180,
overflow: 'auto'` — ровно тот же паттерн, что уже проверен на `case 'error'`: не растягивает
ленту, весь текст доступен скроллом внутри карточки. В макете отдельный «крайний случай» —
`blocked` на ~300 символов, чтобы проверить, что скролл-потолок реально включается.

Кнопку действия сознательно не предлагаю: цикл уже остановлен окончательно, «Повторить» тут
не про что — пользователь либо решает блокер и пишет обычным сообщением, либо включает цикл
заново тумблером в композере (это уже есть). Если захотите кнопку «Включить цикл снова» прямо
в карточке `limit`/`blocked` — отдельная итерация, не блокер для этого макета.

## Иллюстративный TSX (не патч, только форма для обсуждения)

```tsx
case 'work_loop_stopped': {
  if (item.reason === 'manual') {
    // без изменений — тот же клон 'interrupted'
    return (
      <div style={{
        alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'wrap', justifyContent: 'center',
        background: C.bgSelected, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '6px 12px',
        fontSize: FS.sm, color: C.textSecondary, maxWidth: '100%', textAlign: 'center',
      }}>
        <svg width="11" height="11" viewBox="0 0 24 24" fill={C.textMuted}><rect x="5" y="5" width="14" height="14" rx="2" /></svg>
        <span>{item.text}</span>
      </div>
    );
  }

  if (item.reason === 'limit') {
    return (
      <div style={{
        alignSelf: 'center', maxWidth: '100%', display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', justifyContent: 'center',
        background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: R.md, padding: '7px 12px',
        fontSize: 12.5, color: C.warningText, textAlign: 'center',
      }}>
        <AlertCircle size={13} strokeWidth={2} style={{ flexShrink: 0 }} />
        <span>{item.text}</span>
      </div>
    );
  }

  // blocked / waiting_timeout — «нужно решение человека», не поломка: синяя карточка на всю
  // ширину, тот же паттерн потолка/скролла, что у case 'error'
  if (item.reason === 'blocked' || item.reason === 'waiting_timeout') {
    const Icon = item.reason === 'blocked' ? Ban : Clock;
    return (
      <div style={{
        background: C.infoBg, border: `1px solid ${C.info}`, borderRadius: R.md, padding: '8px 12px',
        fontSize: FS.base, color: C.info, display: 'flex', alignItems: 'flex-start', gap: 8,
      }}>
        <Icon size={13} strokeWidth={2} style={{ flexShrink: 0, marginTop: 1 }} />
        <span style={{ overflowWrap: 'break-word', maxHeight: 180, overflow: 'auto' }}>{item.text}</span>
      </div>
    );
  }

  // error и прочие технические reason (stuck_reset, team_restart) — как раньше
  return (
    <div style={{
      alignSelf: 'center', display: 'flex', alignItems: 'center', gap: 9, flexWrap: 'wrap', justifyContent: 'center',
      background: C.bgSelected, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '6px 12px',
      fontSize: FS.sm, color: C.textSecondary, maxWidth: '100%', textAlign: 'center',
    }}>
      <svg width="11" height="11" viewBox="0 0 24 24" fill={C.textMuted}><rect x="5" y="5" width="14" height="14" rx="2" /></svg>
      <span>{item.text}</span>
    </div>
  );
}
```

`error` в примере выше намеренно не склонирован из `case 'error'` дословно — правильнее
явно завести `reason === 'error'` на danger-карточку тем же кодом, что и у соседнего
`case 'error'` (можно вынести общий рендер в маленький хелпер-компонент, раз он используется
в двух кейсах — решать Кире по месту, это уже вопрос реализации, не макета).

## Что не в макете

- `stuck_reset` и `team_restart` — тоже `work_loop_stopped`, но это не про 5 причин из
  задания (это уведомления о ручном сбросе/рестарте хода, не про исчерпание цикла) — в
  примере выше они падают в тот же нейтральный `default`, что и раньше; отдельно их не
  трогал, не просили.
- Кнопка действия в карточке (см. рассуждение выше) — не спроектирована, только упомянута
  как возможная следующая итерация.
- Импорт `Ban`/`AlertCircle` в `ChatItemView.tsx` — сейчас есть `AlertTriangle`, `Clock`;
  `Ban` и `AlertCircle` придётся добавить в импорт `lucide-react`.

## Токены

Только `C.*`/`R.*`/`FS.*` из `design.ts`. Новых токенов не нужно — `C.info`/`C.infoBg`
уже в шкале и используется как «информационный» тон в нескольких местах продукта.
