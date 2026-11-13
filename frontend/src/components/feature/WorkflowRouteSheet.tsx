// Шторка «Маршрут фичи» (мобильный вид блока подтверждения, F05; макет route-confirm.html,
// R33–R38): нижняя шторка ui-кита Modal. Тело (лестница, обоснование) скроллится, подвал с
// кнопками по состоянию липкий и прижат к низу — Modal на мобиле так и устроен (safe-area
// учтён). Состав кнопок задаёт вызывающий: шторка про состояния маршрута ничего не знает.

import type { ReactNode } from 'react';
import { Modal } from '../../components/ui';

export interface WorkflowRouteSheetProps {
  onClose: () => void;
  children: ReactNode;
  // Липкий подвал: кнопки по состоянию (Modal раскладывает их колонкой на всю ширину).
  footer: ReactNode;
}

export function WorkflowRouteSheet({ onClose, children, footer }: WorkflowRouteSheetProps): React.ReactElement {
  return (
    <Modal title="Маршрут фичи" onClose={onClose} footer={footer}>
      <div data-testid="workflow-route-sheet" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {children}
      </div>
    </Modal>
  );
}
