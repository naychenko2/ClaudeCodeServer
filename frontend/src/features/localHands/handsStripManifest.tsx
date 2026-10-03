// Вклад «Руки» в реестр: пилюля состояния рук в губе поля ввода (слот composer-chip). Руки — часть
// каркаса, а не подсистема с тумблером на бэке: манифест помечен core, доступность решает сам чип
// (руки проекта, ответ сервера).

import { registerSubsystem, type ComposerChipCtx, type SubsystemManifest } from '../../lib/subsystems/registryCore';
import { HandsChip } from './HandsChip';

export const handsManifest: SubsystemManifest = {
  key: 'local-hands',
  title: 'Руки',
  order: 90,
  noPill: true,
  core: true,
  slots: {
    'composer-chip': [
      { name: 'hands-chip', order: 90, render: (ctx: ComposerChipCtx) => <HandsChip ctx={ctx} /> },
    ],
  },
};

registerSubsystem(handsManifest);
