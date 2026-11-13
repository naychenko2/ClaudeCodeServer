// «Озвучить сцену»: выбор модели с ожидаемым временем и необязательной подсказкой. Показывается под кнопкой
// в карточке сцены (как выбор фильма). Логика — в sceneSound.ts.

import { useEffect, useState } from 'react';
import { Button, C, FS, R, SP, TextField, showToast } from 'aihome_shell/kit';
import { etaLabel, loadSoundOptions, quoteEtas, tooLong, voiceScene, type SoundOptions } from './sceneSound';

export function SoundChooser({ scope, sessionId, sceneId, versionId, durationSec, onClose }: {
  scope: string; sessionId: string; sceneId: string; versionId: string | null; durationSec: number; onClose: () => void;
}) {
  const [options, setOptions] = useState<SoundOptions | null>(null);
  const [etas, setEtas] = useState<Record<string, number | null>>({});
  const [model, setModel] = useState<string | null>(null);
  const [prompt, setPrompt] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    void (async () => {
      try {
        const o = await loadSoundOptions(scope, sessionId);
        if (!alive) return;
        setOptions(o);
        setModel(o.models[0]?.id ?? null);
        if (!o.reason) { const e = await quoteEtas(scope, sessionId, o.models, durationSec); if (alive) setEtas(e); }
      } catch (e) { if (alive) setError((e as Error).message || 'Каталог звука недоступен'); }
    })();
    return () => { alive = false; };
  }, [scope, sessionId, durationSec]);

  const chosen = options?.models.find(m => m.id === model) ?? null;
  const blocked = options?.reason ?? (chosen ? tooLong(chosen, durationSec) : 'Нет модели');
  const run = async () => {
    if (!chosen) return;
    setBusy(true);
    try {
      await voiceScene({ scope, sessionId, sceneId, versionId, modelId: chosen.id, durationSec, prompt });
      showToast('Озвучка запущена', 'Новая версия сцены появится, когда звук будет готов', 'info');
      onClose();
    } catch (e) { setError((e as Error).message || 'Не удалось запустить'); } finally { setBusy(false); }
  };

  return (
    <div data-video-sound-chooser="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md }}>
      <b style={{ fontSize: FS.sm, color: C.textHeading }}>Озвучить сцену</b>
      {!options && !error && <span style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаем модели…</span>}
      {options?.reason && <span style={{ fontSize: FS.sm, color: C.warningText }}>{options.reason}</span>}
      {options?.models.map(m => (
        <label key={m.id} data-video-sound-model={m.id} style={{ display: 'flex', gap: SP.xs, alignItems: 'center', fontSize: FS.sm, color: C.textSecondary, cursor: 'pointer' }}>
          <input type="radio" name={`sound-${sceneId}`} checked={model === m.id} disabled={!!options.reason} onChange={() => setModel(m.id)} />
          <span style={{ flex: 1, minWidth: 0 }}>{m.label}</span>
          <span style={{ color: C.textMuted, whiteSpace: 'nowrap' }}>{etaLabel(etas[m.id])}{m.heavy ? ' · тяжёлая' : ''}</span>
        </label>
      ))}
      {chosen && <span style={{ fontSize: FS.xs, color: C.textMuted }}>Лицензия: {chosen.license}</span>}
      <TextField value={prompt} onChange={setPrompt} placeholder="Подсказка (необязательно): «шаги по гравию, далёкий дождь»" />
      {(error || (blocked && !options?.reason)) && <span style={{ fontSize: FS.sm, color: C.warningText }}>{error ?? blocked}</span>}
      <div style={{ display: 'flex', gap: SP.xs }}>
        <Button size="sm" variant="primary" loading={busy} disabled={!!blocked || busy} onClick={() => { void run(); }}>Озвучить</Button>
        <Button size="xs" variant="ghost" onClick={onClose}>Отмена</Button>
      </div>
    </div>
  );
}
