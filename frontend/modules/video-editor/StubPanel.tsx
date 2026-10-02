// Заглушка панели «Видео» (блок 4): ключ videoEditor, вклад за флагом. Тело — панель
// блока 5 (features/videoEditor) — подключится в subsystem.tsx, этот файл тогда исчезнет.
import { Clapperboard } from 'lucide-react';
import { C, FS, ICON_SIZE, ICON_STROKE, SP } from 'aihome_shell/kit';

export function StubPanel() {
  return (
    <div role="complementary" aria-label="Видео" style={{ padding: SP.md, fontSize: FS.sm, color: C.textMuted }}>
      <Clapperboard size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent }} />
      <div style={{ marginTop: SP.sm }}>Панель «Видео» готовится.</div>
    </div>
  );
}
