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

// Ожидающая картинка при создании проекта (проекта ещё нет — держим blob в памяти вкладки
// и досылаем после create()). original/crop — для «Перекроить» загруженного файла.
export type DraftIcon = { blob: Blob; original?: File; crop?: AvatarCropResult['crop'] };

// Блок «Иконка» в настройках проекта (по образцу аватара персоны — тот же паттерн действий).
// Никакого переключателя режимов: режим = наличие картинки. Есть картинка → показываем её;
// нет → инициалы на цветном фоне. Все действия (сгенерировать/загрузить/перекроить/цвет/убрать)
// спрятаны в ✎-меню на превью — layout под превью не «прыгает» при переключениях.
//
// Два режима:
//  • Редактирование (по умолчанию): картинка мутируется СРАЗУ вызовами по project.id
//    (generate/select/upload/recrop/mode), возвращается Project → onIconUpdated.
//  • Создание (creating): проекта ещё нет. Генерация stateless (байты инлайн), загрузка/кроп
//    клиентские — картинка держится в pendingImage и отдаётся наружу через onDraftIconChange;
//    диалог создания прикрепит её после create(). Серверные вызовы иконки тут не идут.
// Цвет применяется на «Сохранить»/«Создать» родителем (color/onColorChange), превью — живое.
export function ProjectIconSection({ project, name, onNameChange, color, onColorChange, onIconUpdated, onDraftIconChange, creating = false }: {
  project: Project;
  name: string;
  onNameChange: (v: string) => void;
  color: string | null;
  onColorChange: (c: string | null) => void;
  onIconUpdated: (updated: Project) => void;
  // Только в creating: отдаёт ожидающую картинку наверх (диалог создания прикрепит после create).
  onDraftIconChange?: (draft: DraftIcon | null) => void;
  creating?: boolean;
}) {
  const [draftIcon, setDraftIcon] = useState(project.icon);
  const [pendingImage, setPendingImage] = useState<(DraftIcon & { url: string }) | null>(null);
  const [canGenerate, setCanGenerate] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [showGen, setShowGen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [generating, setGenerating] = useState(false);   // отдельно от busy — под спиннер/текст кнопки
  // Кандидаты генерации: в Edit — имена файлов на сервере, в creating — data-url (готовый src).
  const [candidates, setCandidates] = useState<string[] | null>(null);
  const [prompt, setPrompt] = useState('');
  const [crop, setCrop] = useState<{ src: string; initial: AvatarCropResult['crop'] | null; mode: 'upload' | 'recrop'; file?: File } | null>(null);
  const [err, setErr] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => { api.projects.iconCaps().then(r => setCanGenerate(r.generate)).catch(() => {}); }, []);

  // Отзыв objectURL превью при замене/размонтировании (иначе течёт память вкладки; data-url не трогаем)
  useEffect(() => {
    const url = pendingImage?.url;
    return () => { if (url && url.startsWith('blob:')) URL.revokeObjectURL(url); };
  }, [pendingImage?.url]);

  // Картинка активна? В creating — есть ожидающая; в Edit — сохранённая у проекта.
  const hasImage = creating ? !!pendingImage : (draftIcon?.kind === 'image' && !!draftIcon?.imageFile);
  const canRecrop = creating ? !!pendingImage?.original : (!!draftIcon?.originalFile && hasImage);
  // Превью: в Edit при картинке — kind image (ProjectIcon возьмёт iconUrl по id); иначе инициалы+цвет.
  // В creating картинку показываем через imageUrl-override (локальный objectURL/data-url).
  const previewIcon: ProjectIconType = (!creating && hasImage)
    ? { ...(draftIcon ?? { kind: 'image' }), kind: 'image' }
    : { ...(draftIcon ?? { kind: 'initials' }), kind: 'initials', color: color ?? undefined };
  const preview: Project = { ...project, icon: previewIcon };

  const applyUpdated = (updated: Project) => { setDraftIcon(updated.icon); onIconUpdated(updated); };

  const generate = async () => {
    setGenerating(true); setErr(''); setCandidates(null);
    try {
      if (creating) {
        const r = await api.projects.generateIconPreview({ name, prompt });
        setCandidates(r.candidates.map(c => c.dataUrl));
      } else {
        const r = await api.projects.generateIcon(project.id, { prompt, count: 4 });
        setCandidates(r.candidates);
      }
    } catch (e: any) { setErr(e.message ?? 'Не удалось сгенерировать'); }
    finally { setGenerating(false); }
  };

  const choose = async (item: string) => {
    setBusy(true); setErr('');
    try {
      if (creating) {
        // item — data-url кандидата: декодируем в blob и держим локально (без кроп-диалога —
        // генеративная иконка уже full-bleed, как и при выборе в Edit).
        const blob = await (await fetch(item)).blob();
        setPendingImage({ blob, url: item });
        onDraftIconChange?.({ blob });
      } else {
        applyUpdated(await api.projects.selectIcon(project.id, item));
      }
      setCandidates(null); setShowGen(false); setMenuOpen(false);
    } catch (e: any) { setErr(e.message ?? 'Не удалось выбрать'); }
    finally { setBusy(false); }
  };

  const onFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0];
    e.target.value = '';
    if (f) setCrop({ src: URL.createObjectURL(f), initial: null, mode: 'upload', file: f });
  };

  const openRecrop = () => {
    if (creating) {
      if (!pendingImage?.original) return;
      const c = pendingImage.crop;
      setCrop({ src: URL.createObjectURL(pendingImage.original), initial: c ?? null, mode: 'recrop', file: pendingImage.original });
      return;
    }
    // Оригинал берём из АКТУАЛЬНОГО draftIcon, а не из устаревшего пропа project:
    // после загрузки файла в этой же сессии originalFile обновился только в draftIcon.
    const src = api.projects.iconOriginalUrl({ ...project, icon: draftIcon ?? project.icon });
    if (!src) return;
    const c = draftIcon?.crop;
    setCrop({ src, initial: c ? { scale: c.scale, offsetX: c.offsetX, offsetY: c.offsetY } : null, mode: 'recrop' });
  };

  const applyCrop = async (res: AvatarCropResult) => {
    if (creating) {
      // Загрузка: original = выбранный файл; перекроп: original сохраняем из pendingImage.
      const original = crop?.mode === 'upload' ? crop.file : pendingImage?.original;
      setPendingImage({ blob: res.blob, url: URL.createObjectURL(res.blob), original, crop: res.crop });
      onDraftIconChange?.({ blob: res.blob, original, crop: res.crop });
      closeCrop();
      return;
    }
    if (crop?.mode === 'upload' && crop.file) applyUpdated(await api.projects.uploadIcon(project.id, crop.file, res.blob, res.crop));
    else if (crop?.mode === 'recrop') applyUpdated(await api.projects.recropIcon(project.id, res.blob, res.crop));
    closeCrop();
  };

  // «Убрать картинку» / «Вернуть картинку». В creating — локальный сброс ожидающей;
  // в Edit — смена kind на сервере без стирания файлов.
  const setMode = async (kind: 'initials' | 'image') => {
    if (creating) {
      if (kind === 'initials') { setPendingImage(null); onDraftIconChange?.(null); setMenuOpen(false); }
      return;
    }
    setBusy(true); setErr('');
    try { applyUpdated(await api.projects.setIconMode(project.id, kind)); setMenuOpen(false); }
    catch (e: any) { setErr(e.message ?? 'Не удалось переключить'); }
    finally { setBusy(false); }
  };

  // Закрыть кроп-диалог с отзывом blob-URL источника (в Edit-recrop src — http-URL, его не трогаем).
  const closeCrop = () => { if (crop?.src.startsWith('blob:')) URL.revokeObjectURL(crop.src); setCrop(null); };

  const menuAction = (fn: () => void) => { setMenuOpen(false); fn(); };
  const divider = <div style={{ borderTop: `1px solid ${C.borderLight}`, margin: '4px 2px' }} />;

  return (
    <div>
      {err && <div style={{ fontSize: 12, color: C.danger, marginBottom: 6 }}>{err}</div>}

      <div style={{ display: 'flex', alignItems: 'center', gap: 14 }}>
        {/* Превью + ✎-кнопка в углу (все действия — в меню, чтобы не «прыгал» layout) */}
        <div style={{ position: 'relative', flexShrink: 0 }}>
          <ProjectIcon project={preview} size={56} radius={14} imageUrl={creating ? (pendingImage?.url ?? null) : undefined} />
          <button
            type="button"
            onClick={() => setMenuOpen(v => !v)}
            aria-label="Изменить иконку"
            title="Изменить иконку"
            disabled={busy || generating}
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
              {canRecrop && <MenuItem label="Перекроить" onClick={() => menuAction(openRecrop)} />}
              {!creating && !hasImage && draftIcon?.imageFile && <MenuItem label="Вернуть картинку" onClick={() => void setMode('image')} />}
              {divider}
              {/* Палитра цвета фона (для инициалов; применяется на «Сохранить»/«Создать») */}
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
          autoFocus={creating}
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
            onKeyDown={e => { if (e.key === 'Enter' && !generating) { e.preventDefault(); void generate(); } }}
            disabled={generating}
            style={{
              flex: 1, minWidth: 0, boxSizing: 'border-box', height: 32, padding: '0 10px',
              borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite,
              fontSize: 12.5, color: C.textPrimary, outline: 'none', fontFamily: FONT.sans,
            }}
          />
          <Button variant="secondary" size="sm" onClick={generate} loading={generating} disabled={generating} style={{ flexShrink: 0 }}>
            {generating ? 'Генерирую…' : 'Создать 4 варианта'}
          </Button>
        </div>
      )}

      {/* Галерея кандидатов генерации */}
      {candidates && !generating && (
        <div style={{ marginTop: 8 }}>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8 }}>
            {candidates.map(c => (
              <button
                key={c}
                type="button"
                onClick={() => choose(c)}
                disabled={busy}
                style={{ padding: 0, border: 'none', background: 'none', cursor: 'pointer', borderRadius: 10, overflow: 'hidden', aspectRatio: '1' }}
              >
                <img src={creating ? c : api.projects.iconCandidateUrl(project.id, c)} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
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
          onClose={closeCrop}
        />
      )}
    </div>
  );
}
