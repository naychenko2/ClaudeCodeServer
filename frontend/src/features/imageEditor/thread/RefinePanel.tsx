// Панель «Доводка» у версии Fibo 2.0 (Ш8): одно поле JSON-описания сцены за запуск. Человек выбирает,
// что меняет (свет, ракурс, одежда или цвет главного объекта, фон, стиль), видит, что там сейчас, и
// пишет новое значение. Сервер переписывает описание под изменение и рисует новую версию тем же сидом —
// остальная сцена остаётся на месте.

import { useState } from 'react';
import { Wand2 } from 'lucide-react';
import { Button, Field, SegmentedControl, TextField, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { ImageThread, ImageThreadVersion } from './threadsApi';
import { launchRefine } from './useThreadLaunch';
import { parseVgl, VGL_FIELDS, VGL_MAX_VALUE, vglFieldValue, type VglField } from './vgl';

export function RefinePanel({ projectId, sessionId, thread, version }: {
  projectId: string; sessionId: string; thread: ImageThread; version: ImageThreadVersion;
}) {
  const [field, setField] = useState<VglField>('light');
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const scene = version.vgl ? parseVgl(version.vgl.json) : null;
  if (!scene) return null;
  const now = vglFieldValue(scene, field);
  const meta = VGL_FIELDS.find(f => f.key === field)!;
  const ready = value.trim().length > 0 && value.length <= VGL_MAX_VALUE && !busy;
  const run = async () => {
    setBusy(true);
    const ok = await launchRefine(projectId, sessionId, thread, version, field, value);
    setBusy(false);
    if (ok) setValue('');
  };

  return (
    <div data-image-refine="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <SegmentedControl value={field} onChange={v => setField(v as VglField)}
        options={VGL_FIELDS.map(f => ({ value: f.key, label: f.label }))} />
      <div title={now} style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, overflowWrap: 'anywhere' }}>
        Сейчас: {now || 'в описании не указано'}
      </div>
      <Field label="Новое значение" hint="Меняется только это — остальная сцена и сид прежние">
        <TextField value={value} onChange={setValue} placeholder={meta.placeholder} />
      </Field>
      <div style={{ display: 'flex' }}>
        <Button size="xs" variant="secondary" leftIcon={<Wand2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          disabled={!ready} onClick={() => { void run(); }}>
          Перерисовать
        </Button>
      </div>
    </div>
  );
}
