import { useEffect, useRef, useState } from 'react';
import type { Project } from '../../types';
import { api } from '../../lib/api';
import { C, R, FONT } from '../../lib/design';
import { Button, Field } from '../../components/ui';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { ProjectIcon } from './ProjectIcon';
import { AvatarCropDialog, type AvatarCropResult } from '../personas/AvatarCropDialog';

// Блок «Иконка» в настройках проекта (по образцу блока аватара персоны): превью + палитра
// цвета + AI-генерация 4 кандидатов + загрузка своего файла с кроп-диалогом.
// Картинка мутируется отдельными вызовами СРАЗУ (generate/select/upload/recrop) и возвращает
// обновлённый Project — прокидываем его наверх через onIconUpdated, не дожидаясь «Сохранить».
// Цвет применяется на «Сохранить» родителем (value/onColorChange), превью — живое.
export function ProjectIconSection({ project, color, onColorChange, onIconUpdated }: {
  project: Project;
  color: string | null;
  onColorChange: (c: string | null) => void;
  onIconUpdated: (updated: Project) => void;
}) {
  const [draftIcon, setDraftIcon] = useState(project.icon);
  const [canGenerate, setCanGenerate] = useState(false);
  const [busy, setBusy] = useState(false);
  const [candidates, setCandidates] = useState<string[] | null>(null);
  const [prompt, setPrompt] = useState('');
  const [crop, setCrop] = useState<{ src: string; initial: AvatarCropResult['crop'] | null; mode: 'upload' | 'recrop'; file?: File } | null>(null);
  const [err, setErr] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => { api.projects.iconCaps().then(r => setCanGenerate(r.generate)).catch(() => {}); }, []);

  // Превью-проект: актуальная картинка draftIcon + выбранный (ещё не сохранённый) цвет
  const preview: Project = { ...project, icon: { ...(draftIcon ?? { kind: 'initials' }), color: color ?? undefined } };

  const applyUpdated = (updated: Project) => { setDraftIcon(updated.icon); onIconUpdated(updated); };

  const generate = async () => {
    setBusy(true); setErr('');
    try {
      const r = await api.projects.generateIcon(project.id, { prompt, count: 4 });
      setCandidates(r.candidates);
    } catch (e: any) { setErr(e.message ?? 'Не удалось сгенерировать'); }
    finally { setBusy(false); }
  };

  const choose = async (file: string) => {
    setBusy(true); setErr('');
    try { applyUpdated(await api.projects.selectIcon(project.id, file)); setCandidates(null); }
    catch (e: any) { setErr(e.message ?? 'Не удалось выбрать'); }
    finally { setBusy(false); }
  };

  const onFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0];
    e.target.value = '';
    if (f) setCrop({ src: URL.createObjectURL(f), initial: null, mode: 'upload', file: f });
  };

  const openRecrop = () => {
    // Оригинал берём из АКТУАЛЬНОГО draftIcon, а не из устаревшего пропа project:
    // после загрузки файла в этой же сессии originalFile обновился только в draftIcon.
    const src = api.projects.iconOriginalUrl({ ...project, icon: draftIcon ?? project.icon });
    if (!src) return;
    const c = draftIcon?.crop;
    setCrop({ src, initial: c ? { scale: c.scale, offsetX: c.offsetX, offsetY: c.offsetY } : null, mode: 'recrop' });
  };

  const applyCrop = async (res: AvatarCropResult) => {
    if (crop?.mode === 'upload' && crop.file) applyUpdated(await api.projects.uploadIcon(project.id, crop.file, res.blob, res.crop));
    else if (crop?.mode === 'recrop') applyUpdated(await api.projects.recropIcon(project.id, res.blob, res.crop));
    setCrop(null);
  };

  return (
    <Field label="Иконка">
      {err && <div style={{ fontSize: 12, color: C.danger, marginBottom: 6 }}>{err}</div>}
      <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
        <ProjectIcon project={preview} size={56} radius={14} />
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            {canGenerate && <Button variant="secondary" size="sm" onClick={generate} disabled={busy}>✨ Сгенерировать</Button>}
            <Button variant="ghost" size="sm" onClick={() => fileRef.current?.click()} disabled={busy}>Загрузить файл</Button>
            {draftIcon?.originalFile && <Button variant="ghost" size="sm" onClick={openRecrop} disabled={busy}>Перекроить</Button>}
          </div>
          {/* Палитра цвета (для инициалов; применяется на «Сохранить») */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
            {Object.keys(AGENT_COLORS).map(key => (
              <button
                key={key}
                type="button"
                title={key}
                onClick={() => onColorChange(color === key ? null : key)}
                style={{
                  width: 20, height: 20, borderRadius: '50%', cursor: 'pointer', flexShrink: 0,
                  background: agentDotColor(key),
                  border: color === key ? `2px solid ${C.textHeading}` : '2px solid transparent',
                }}
              />
            ))}
          </div>
        </div>
      </div>

      {/* Поле промпта генерации (опционально) */}
      {canGenerate && (
        <input
          value={prompt}
          onChange={e => setPrompt(e.target.value)}
          placeholder="Опишите иконку (необязательно)…"
          style={{
            width: '100%', boxSizing: 'border-box', marginTop: 8, height: 32, padding: '0 10px',
            borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite,
            fontSize: 12.5, color: C.textPrimary, outline: 'none', fontFamily: FONT.sans,
          }}
        />
      )}

      {/* Галерея кандидатов генерации */}
      {candidates && (
        <div style={{ marginTop: 8 }}>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8 }}>
            {candidates.map(f => (
              <button
                key={f}
                type="button"
                onClick={() => choose(f)}
                disabled={busy}
                style={{ padding: 0, border: 'none', background: 'none', cursor: 'pointer', borderRadius: 10, overflow: 'hidden', aspectRatio: '1' }}
              >
                <img src={api.projects.iconCandidateUrl(project.id, f)} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
              </button>
            ))}
          </div>
          <button
            type="button"
            onClick={() => setCandidates(null)}
            style={{ marginTop: 6, background: 'none', border: 'none', color: C.textMuted, fontSize: 12, cursor: 'pointer', fontFamily: FONT.sans }}
          >
            Отмена
          </button>
        </div>
      )}

      <input ref={fileRef} type="file" accept="image/png,image/jpeg,image/webp" onChange={onFile} style={{ display: 'none' }} />
      {crop && (
        <AvatarCropDialog
          src={crop.src}
          initial={crop.initial}
          title="Кроп иконки"
          onApply={applyCrop}
          onClose={() => setCrop(null)}
        />
      )}
    </Field>
  );
}
