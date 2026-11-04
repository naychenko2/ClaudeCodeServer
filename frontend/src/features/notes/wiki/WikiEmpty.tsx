// «Вики пока пустая» (LI-3, решение Андрея 04.11): вики создаётся сама — сервер заводит
// её при действиях человека («Разобрать этот чат», перенос заметки) и при включении
// ночного разбора, поэтому кнопки «Завести вики» здесь нет. Тот же экран показывается и
// когда вики уже заведена, но разделов ещё нет: голый каталог здесь не о чём.
//
// Состояние ночного разбора — из стора настроек (тот же снимок, что читает блок
// «Ночной разбор»): нового запроса здесь нет. Пока настроек в сторе нет — второй строки
// нет: состояния не знаем, выдумывать нечем.
//
// onOpenSettings — переход к «Настройкам» там, где панель достижима состоянием
// вызывающего (подкладка вкладки или шторка панели). Без него текст остаётся без ссылки:
// кнопка, которая ничего не делает, хуже отсутствия кнопки.

import { BookOpen } from 'lucide-react';
import { SP, useWikiSettings } from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { EmptyState } from '../../../components/ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';

export function WikiEmpty({ onOpenSettings }: {
  /** Открыть «Настройки» вики; нет — текст про настройки остаётся без ссылки */
  onOpenSettings?: () => void;
}) {
  const snapshot = useWikiSettings();
  const nightlyOff = snapshot !== null && !snapshot.nightly.enabled;
  return (
    <div data-testid="wiki-empty" style={{ height: '100%' }}>
      <EmptyState
        icon={<BookOpen size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
        title="Вики пока пустая"
        subtitle={
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, maxWidth: 340 }}>
            <span>Она наполнится сама, когда вы разберёте чат или перенесёте заметку.</span>
            {snapshot && (
              <span>
                {snapshot.nightly.enabled
                  ? 'Ночной разбор включён — первые страницы появятся после ночи.'
                  : 'Ночной разбор выключен — его можно включить в настройках вики.'}
              </span>
            )}
          </div>
        }
        action={nightlyOff && onOpenSettings ? (
          <Button variant="ghost" size="sm" dataTestId="wiki-empty-settings" onClick={onOpenSettings}>Открыть настройки</Button>
        ) : undefined}
      />
    </div>
  );
}
