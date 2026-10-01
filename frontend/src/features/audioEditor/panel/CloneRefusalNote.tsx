// Отказ запуска «клон MiniMax протух или не создан» с кнопкой «Пересоздать · цена». Пересоздание
// пошло — отказ больше не правда, блок снимает сама панель (onCleared)

import { AlertTriangle } from 'lucide-react';
import { C, FS, R, SP } from 'aihome_shell/kit';
import { RecreateButton } from '../voices/RecreateButton';
import { ic } from './primitives';
import type { CloneRefusal } from './run';

export function CloneRefusalNote({ scope, refusal, onCleared }: { scope: string; refusal: CloneRefusal; onCleared: () => void }) {
  return (
    <div data-clone-refusal={refusal.slug} style={{
      display: 'flex', flexDirection: 'column', gap: SP.xs, marginTop: SP.sm, fontSize: FS.sm, lineHeight: 1.45,
      color: C.warningText, background: C.warningBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`,
    }}>
      <span style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start' }}>
        <span style={{ display: 'inline-flex', marginTop: 2 }}>{ic(AlertTriangle)}</span>
        <span>{refusal.message}</span>
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <RecreateButton scope={scope} slug={refusal.slug} quote={refusal.quote} onStarted={onCleared} />
      </span>
    </div>
  );
}
