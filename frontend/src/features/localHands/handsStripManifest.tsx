// Вклад полосы «Руки» в реестр composer-strip. Руки — часть каркаса, а не подсистема с
// тумблером на бэке: манифест помечен core, доступность решает сам вклад (руки проекта,
// ответ сервера).

import { registerSubsystem, type ComposerChipCtx, type ComposerStripApi, type ComposerStripCtx, type SubsystemManifest } from '../../lib/subsystems/registryCore';
import { HandsChip } from './HandsChip';
import { HandsStrip, handsStripIcon, handsStripStatus } from './HandsStripView';
import { HANDS_STRIP, handsStripAvailable, subscribeHandsStrip } from './handsStrip';

export const handsManifest: SubsystemManifest = {
  key: 'local-hands',
  title: 'Руки',
  order: 90,
  noPill: true,
  core: true,
  slots: {
    'composer-strip': [
      {
        name: HANDS_STRIP, order: 10,
        render: (ctx: ComposerStripCtx) => <HandsStrip ctx={ctx} />,
        action: {
          title: 'Руки',
          icon: handsStripIcon,
          isAvailable: handsStripAvailable,
          status: handsStripStatus,
        } satisfies ComposerStripApi as unknown as Record<string, unknown>,
      },
    ],
    // При строке контекста полос нет: руки живут пилюлей в губе поля (сам вклад молчит без флага)
    'composer-chip': [
      { name: 'hands-chip', order: 90, render: (ctx: ComposerChipCtx) => <HandsChip ctx={ctx} /> },
    ],
  },
};

registerSubsystem(handsManifest);

// Хост полос подписан на реестр, а не на стор рук: когда доступность полосы меняется
// (пришёл проект, сервер ответил «рук нет»), перерегистрация будит его пересчёт
let _availKey = '';
// Открытые чаты: sessionId → projectId
const _watched = new Map<string, string>();
function availabilityKey(): string {
  return [..._watched].map(([sessionId, projectId]) => `${sessionId}:${handsStripAvailable({ projectId, sessionId })}`).join(',');
}
export function watchHandsAvailability(projectId: string, sessionId: string) {
  _watched.set(sessionId, projectId);
  nudge();
  return () => { _watched.delete(sessionId); };
}
function nudge() {
  const key = availabilityKey();
  if (key === _availKey) return;
  _availKey = key;
  registerSubsystem(handsManifest);
}
subscribeHandsStrip(nudge);
