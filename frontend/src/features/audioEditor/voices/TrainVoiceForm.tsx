// «Обучить голос» (ADR-023 §Д3, ТО-2 (2)): кнопка библиотеки «Голоса», а не чип объекта — обучение не действие
// над звуком в работе. Имя голоса и записи из проекта; запуск — тот же путь, что у панели: котировка → задача по
// quoteId. Своего исполнителя у формы нет: поставщика и модель выбирает сервер по умолчанию операции.

import { useState } from 'react';
import { Mic, X } from 'lucide-react';
import { Button, Field, IconButton, TextField, showToast, C, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { audioApi } from '../api';
import { dropLegacyClipPaths, legacyClipPaths } from '../context/legacyInputs';
import { PathList } from '../panel/OpFields';

export const TRAIN_HINT = 'по записям одного человека: 2–30 минут чистой речи · бесплатно на своей видеокарте';

// Запись без пробелов по краям; пустые строки не едут
export const clipsOf = (paths: readonly string[]) => paths.map(p => p.trim()).filter(Boolean);

export function trainProblem(name: string, clips: readonly string[]): string | null {
  if (!name.trim()) return 'Дайте голосу имя';
  if (clipsOf(clips).length < 1) return 'Добавьте записи голоса — от 2 до 30 минут';
  return null;
}

export function TrainVoiceForm({ scope, sessionId, onClose }: { scope: string; sessionId: string | null; onClose: () => void }) {
  const [name, setName] = useState('');
  const [clips, setClips] = useState<string[]>(() => legacyClipPaths(scope));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problem = trainProblem(name, clips);

  const submit = async () => {
    if (problem) return;
    setBusy(true);
    setError(null);
    try {
      const prompt = name.trim();
      const quote = await audioApi.quote(scope, sessionId, {
        mode: 'voice', operation: 'trainVoice', provider: null, model: null, count: 1, sessionId, threadId: null, prompt,
      });
      await audioApi.startJob(scope, sessionId, { quoteId: quote.quoteId, sessionId, threadId: null, prompt, clipPaths: clipsOf(clips) });
      dropLegacyClipPaths(scope);
      showToast(`Обучаем голос «${prompt}»`, 'Он появится в списке, когда обучение закончится', 'info');
      onClose();
    } catch (e) {
      setError((e as Error)?.message || 'Обучение не запущено');
      setBusy(false);
    }
  };

  return (
    <div data-train-voice="" style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.sm,
      borderRadius: R.lg, border: `1px solid ${C.accent}`, background: C.bgPanel,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
        <span style={{ flex: 1, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>Обучить голос</span>
        <IconButton size="xs" title="Закрыть" ariaLabel="Закрыть форму обучения" disabled={busy} onClick={onClose}>
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
      <Field label="Имя голоса"><TextField value={name} onChange={setName} placeholder="Например, Андрей" autoFocus disabled={busy} /></Field>
      <Field label="Записи голоса · 1–20 файлов" hint="Путь к записи в проекте. Музыку сначала уберите: «Стемы»">
        <PathList paths={clips} onChange={setClips} />
      </Field>
      {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
      <div style={{ display: 'flex', gap: SP.xs, justifyContent: 'flex-end', alignItems: 'center', flexWrap: 'wrap' }}>
        {problem && (name.trim() || clipsOf(clips).length > 0) && (
          <span style={{ flex: 1, minWidth: 0, fontSize: FS.xs, color: C.textMuted }}>{problem}</span>
        )}
        <Button size="sm" variant="ghost" disabled={busy} onClick={onClose}>Отмена</Button>
        <Button size="sm" variant="primary" leftIcon={<Mic size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} loading={busy}
          disabled={busy || !!problem} onClick={() => { void submit(); }}>
          Обучить · бесплатно
        </Button>
      </div>
    </div>
  );
}
