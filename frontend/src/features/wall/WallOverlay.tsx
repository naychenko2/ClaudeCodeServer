// Лайтбокс стены: результат клика из панели проекта (файл, коммит, задача,
// персона, командный центр, граф, превью сервиса) открывается большим островом
// ПОВЕРХ колонок — стена остаётся на месте, закрыл — работаешь дальше.
//
// Слой Z.overlay (900): выше колонок и peek/закрепа панелей, НИЖЕ Z.modal —
// вложенные модалки вьюеров (подтверждения, диалоги) портируются в body с
// Z.modal и ложатся поверх. Закрытие: клик по scrim, Escape (с уважением к
// e.defaultPrevented — вложенные слои главнее), крестики самих вьюеров.
import { useEffect, type ReactNode } from 'react';
import { C, ISLAND, SHADOW, Z } from '../../lib/design';
import { Island } from '../../components/ui';

export function WallOverlay({ onClose, children }: { onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !e.defaultPrevented) onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div
      // Только прямой клик по scrim: выделение текста во вьюере, отпущенное за краем
      // острова, даёт click на общем предке — закрывать оверлей от этого нельзя
      onClick={e => { if (e.target === e.currentTarget) onClose(); }}
      style={{
        position: 'absolute', inset: 0, zIndex: Z.overlay,
        background: C.overlay, display: 'flex',
        padding: ISLAND.pad * 3, boxSizing: 'border-box',
      }}
    >
      <Island
        bg={C.bgMain}
        shadow={SHADOW.modal}
        style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}
        // Клики внутри острова не закрывают оверлей
        rootProps={{ onClick: e => e.stopPropagation() }}
      >
        <div style={{ flex: 1, minHeight: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {children}
        </div>
      </Island>
    </div>
  );
}
