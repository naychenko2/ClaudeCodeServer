import { useEffect, useMemo, useState } from 'react';
import type { ProjectGroup } from '../../types';
import { api } from '../../lib/api';
import { C, R, FONT, FS, GROUP_COLORS, MODAL_W } from '../../lib/design';
import { Modal, ModalActions, Field, TextField, TextArea } from '../../components/ui';
import { Blank, GlyphIcon, LUCIDE_ICON_NAMES, preloadGlyph } from '../../lib/projectGlyphs';
import { invalidateProjectsCache } from '../projects/useAllProjects';
import { SphereTile } from './SphereTile';

export const CHARTER_MAX = 4000;
const CHARTER_COUNTER_FROM = 3500;
const PICKER_LIMIT = 60;

// Тематические значки, которые видны без поиска; остальной белый список — через поиск
const SUGGESTED_ICONS = [
  'landmark', 'heart-pulse', 'house', 'briefcase', 'graduation-cap', 'sprout', 'code', 'music',
  'palette', 'dumbbell', 'wallet', 'plane',
] as const;

interface Props {
  // Задана — режим правки, иначе создание
  sphere?: ProjectGroup;
  // Сколько сфер уже есть: по нему подбирается цвет по умолчанию
  existingCount: number;
  onSaved: (saved: ProjectGroup) => void;
  onClose: () => void;
}

// Диалог «Новая сфера» / «Изменить сферу»: название, значок (весь белый список lucide),
// цвет, устав. На мобиле Modal сам превращается в шторку снизу.
export function SphereDialog({ sphere, existingCount, onSaved, onClose }: Props) {
  const [name, setName] = useState(sphere?.name ?? '');
  const [icon, setIcon] = useState(sphere?.icon ?? '');
  const [color, setColor] = useState(sphere?.color || GROUP_COLORS[existingCount % GROUP_COLORS.length]);
  const [charter, setCharter] = useState(sphere?.charter ?? '');
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const q = query.trim().toLowerCase();
  const icons = useMemo<readonly string[]>(() => q
    ? LUCIDE_ICON_NAMES.filter(n => n.includes(q)).slice(0, PICKER_LIMIT)
    : SUGGESTED_ICONS, [q]);

  // Прогрев видимых значков, чтобы клетки пикера не мигали пустыми
  useEffect(() => { icons.forEach(n => { void preloadGlyph(n); }); }, [icons]);

  const save = async () => {
    const trimmed = name.trim();
    if (!trimmed || busy) return;
    setBusy(true);
    setError('');
    try {
      let saved: ProjectGroup;
      if (sphere) {
        saved = await api.projectGroups.update(sphere.id, { name: trimmed, color, icon, charter });
      } else {
        saved = await api.projectGroups.create(trimmed, color, { icon, charter });
      }
      invalidateProjectsCache();
      onSaved(saved);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить сферу');
      setBusy(false);
    }
  };

  return (
    <Modal
      title={sphere ? 'Изменить сферу' : 'Новая сфера'}
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={sphere ? 'Сохранить' : 'Создать сферу'}
          onConfirm={save}
          loading={busy}
          confirmDisabled={!name.trim()}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: FS.base }}>{error}</div>}

      <Field label="Название">
        <TextField value={name} onChange={setName} placeholder="Например, Банк" autoFocus onEnter={save} />
      </Field>

      <Field label="Значок" hint={q && icons.length === 0 ? 'Ничего не нашлось' : undefined}>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          <button
            type="button" onClick={() => setIcon('')} title="Без значка"
            style={iconCell(icon === '')}
          >
            <span style={{ fontFamily: FONT.sans, fontWeight: 700, fontSize: FS.base }}>Аа</span>
          </button>
          {icons.map(n => (
            <button key={n} type="button" onClick={() => setIcon(n)} title={n} style={iconCell(icon === n)}>
              <GlyphIcon name={n} fallback={Blank} size={18} strokeWidth={2} />
            </button>
          ))}
        </div>
        <TextField value={query} onChange={setQuery} placeholder="Найти значок по имени (bank, home, music…)" />
      </Field>

      <Field label="Цвет">
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          {GROUP_COLORS.map(c => (
            <button
              key={c} type="button" onClick={() => setColor(c)} title={c}
              style={{
                width: 26, height: 26, borderRadius: R.md, background: c, cursor: 'pointer',
                border: color === c ? `2px solid ${C.textHeading}` : `1px solid ${C.border}`,
              }}
            />
          ))}
        </div>
      </Field>

      <Field
        label="Чем занимается сфера"
        hint="Персоны сферы прочитают это в каждом её проекте: тема, конвенции, тон, запреты. До 4000 символов."
      >
        <TextArea value={charter} onChange={v => setCharter(v.slice(0, CHARTER_MAX))} minHeight={96} maxHeight={220} autoGrow />
        {charter.length >= CHARTER_COUNTER_FROM && (
          <span style={{ fontSize: FS.xs, color: C.warningText, alignSelf: 'flex-end' }}>
            {charter.length} / {CHARTER_MAX}
          </span>
        )}
      </Field>

      <div style={{ display: 'flex', alignItems: 'center', gap: 10, paddingTop: 2 }}>
        <SphereTile sphere={{ name: name || 'Н', color, icon }} size={22} />
        <span style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {name.trim() || 'Название сферы'}
        </span>
        <span style={{ fontSize: FS.sm, color: C.textMuted }}>— так сфера будет выглядеть на вкладке «Проекты»</span>
      </div>
    </Modal>
  );
}

function iconCell(active: boolean) {
  return {
    width: 38, height: 38, borderRadius: R.md, display: 'flex', alignItems: 'center', justifyContent: 'center',
    cursor: 'pointer', fontFamily: FONT.sans,
    background: active ? C.bgSelected : C.bgWhite,
    color: active ? C.accent : C.textSecondary,
    border: `1px solid ${active ? C.accent : C.border}`,
  } as const;
}
