// Иконка типовой операции (operationOf): одна на шапку карточки инструмента и индикатор
// ожидания, чтобы «тесты» и «сборка» узнавались одинаково везде
import { FlaskConical, GitBranch, Hammer, Hourglass, Image, Server, type LucideIcon } from 'lucide-react';
import type { Operation } from './toolLabels';

export const OPERATION_ICON: Record<Operation, LucideIcon> = {
  tests: FlaskConical,
  build: Hammer,
  stand: Server,
  git: GitBranch,
  media: Image,
  wait: Hourglass,
};
