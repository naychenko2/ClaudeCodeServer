import { useEffect, useRef, useState } from 'react';
import { Pencil } from 'lucide-react';
import type { Project, ProjectIcon as ProjectIconType } from '../../types';
import { api } from '../../lib/api';
import { C, R, FONT, SHADOW } from '../../lib/design';
import { Button } from '../../components/ui';
import { Menu, MenuItem } from '../../components/ui/Menu';
import { AGENT_COLORS, agentDotColor } from '../../components/AgentSelector';
import { ProjectIcon } from './ProjectIcon';
import { AvatarCropDialog, type AvatarCropResult } from '../personas/AvatarCropDialog';

// Блок «Иконка» в настройках проекта (по образцу аватара персоны — тот же паттерн действий).
// Никакого переключателя режимов: режим = наличие картинки. Есть картинка → показываем её;
// нет → инициалы на цветном фоне. Все действия (сгенерировать/загрузить/перекроить/цвет/убрать)
// спрятаны в ✎-меню на превью — layout под превью не «прыгает» при переключениях.
// Картинка мутируется отдельными вызовами СРАЗУ (generate/select/upload/recrop/mode) и возвращает
// обновлённый Project — прокидываем наверх через onIconUpdated, не дожидаясь «Сохранить».
// Цвет применяется на «Сохранить» родителем (color/onColorChange), превью — живое.
export function ProjectIconSection({ project, name, onNameChange, color, onColorChange, onIconUpdated }: {
  project: Project;
  name: string;
  onNameChange: (v: string) => void;
  color: string | null;
  onColorChange: (c: string | null) => void;
  onIconUpdated: (updated: Project) => void;
}) {
  const [draftIcon, setDraftIcon] = useState(project.icon);
  const [canGenerate, setCanGenerate] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [showGen, setShowGen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [candidates, setCandidates] = useState<string[] | null>(null);
  const [prompt, setPrompt] = useState('');
  const [crop, setCrop] = useState<{ src: string; initial: AvatarCropResult['crop'] | null; mode: 'upload' | 'recrop'; file?: File } | null>(null);
  const [err, setErr] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => { api.projects.iconCaps().then(r => setCanGenerate(r.generate)).catch(() => {}); }, []);

  // Картинка активна? Иначе превью — инициалы с выбранным (ещё не сохранённым) цветом.
  const hasImage = draftIcon?.kind === 'image' && !!draftIcon?.imageFile;
  const previewIcon: ProjectIconType = hasImage
    ? { ...(draftIcon ?? { kind: 'image' }), kind: 'image' }
    : { ...(draftIcon ?? { kind: 'initials' }), kind: 'initials', color: color ?? undefined };
  const preview: Project = { ...project, icon: previewIcon };

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
    try { applyUpdated(await api.projects.selectIcon(project.id, file)); setCandidates(null); setShowGen(false); }
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

  // Переключение kind без стирания файлов: «Убрать картинку» (→ буквы) и «Вернуть картинку».
  const setMode = async (kind: 'initials' | 'image') => {
    setBusy(true); setErr('');
    try { applyUpdated(await api.projects.setIconMode(project.id, kind)); setMenuOpen(false); }
    catch (e: any) { setErr(e.message ?? 'Не удалось переключить'); }
    finally { setBusy(false); }
  };

  const menuAction = (fn: () => void) => { setMenuOpen(false); fn(); };
  const divider = <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '4px 2px' }} />;

  return (
    <div>
      {err && <div style={{ fontSize: 12, color: C.danger, marginBottom: 6 }}>{err}</div>}

      <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
        {/* Превью + ✎-кнопка в углу (все действия — в меню, чтобы не «прыгал» layout) */}
        <div style={{ position: 'relative', flexShrink: 0 }}>
          <ProjectIcon project={preview} size={56} radius={14} />
          <button
            type="button"
            onClick={() => setMenuOpen(v => !v)}
            aria-label="Изменить иконку"
            title="Изменить иконку"
            disabled={busy}
            style={{
              position: 'absolute', right: -5, bottom: -5, width: 24, height: 24, borderRadius: R.full,
              border: `2.5px solid ${C.bgMain}`, background: C.accent, color: C.onAccent,
              cursor: busy ? 'default' : 'pointer', padding: 0,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              boxShadow: SHADOW.thumb, transition: 'background 0.15s',
            }}
          >
            <Pencil size={13} strokeWidth={2.4} style={{ flexShrink: 0 }} />
          </button>

          {menuOpen && (
            <Menu onClose={() => setMenuOpen(false)} align="left" top={64} minWidth={236}>
              {canGenerate && <MenuItem label="✨ Сгенерировать" onClick={() => menuAction(() => setShowGen(true))} />}
              <MenuItem label="Загрузить файл…" onClick={() => menuAction(() => fileRef.current?.click())} />
              {draftIcon?.originalFile && hasImage && <MenuItem label="Перекроить" onClick={() => menuAction(openRecrop)} />}
              {!hasImage && draftIcon?.imageFile && <MenuItem label="Вернуть картинку" onClick={() => void setMode('image')} />}
              {divider}
              {/* Палитра цвета фона (для инициалов; применяется на «Сохранить») */}
              <div style={{ padding: '4px 8px 6px' }}>
                <div style={{ fontSize: 11.5, color: C.textMuted, marginBottom: 7, fontFamily: FONT.sans }}>Цвет фона</div>
                <div style={{ display: 'flex', gap: 7, flexWrap: 'wrap' }}>
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
              {hasImage && <>{divider}<MenuItem label="Убрать картинку" danger onClick={() => void setMode('initials')} /></>}
            </Menu>
          )}
        </div>

        {/* Название проекта — крупный serif-ввод рядом с иконкой (по образцу «Роли» персоны) */}
        <input
          value={name}
          onChange={e => onNameChange(e.target.value)}
          placeholder="Название проекта"
          style={{
            flex: 1, minWidth: 0, boxSizing: 'border-box',
            border: 'none', outline: 'none', background: 'transparent',
            fontFamily: FONT.serif, fontSize: 22, fontWeight: 500,
            color: C.textHeading, padding: 0, lineHeight: 1.3,
          }}
        />
      </div>

      {/* Форма генерации — раскрывается по «Сгенерировать» из меню */}
      {canGenerate && showGen && (
        <div style={{ marginTop: 10, display: 'flex', gap: 6 }}>
          <input
            value={prompt}
            onChange={e => setPrompt(e.target.value)}
            placeholder="Опишите иконку (необязательно)…"
            onKeyDown={e => { if (e.key === 'Enter' && !busy) { e.preventDefault(); void generate(); } }}
            style={{
              flex: 1, minWidth: 0, boxSizing: 'border-box', height: 32, padding: '0 10px',
              borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite,
              fontSize: 12.5, color: C.textPrimary, outline: 'none', fontFamily: FONT.sans,
            }}
          />
          <Button variant="secondary" size="sm" onClick={generate} disabled={busy} style={{ flexShrink: 0 }}>
            Создать 4 варианта
          </Button>
        </div>
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
    </div>
  );
}
