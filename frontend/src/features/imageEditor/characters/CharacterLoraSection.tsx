// Раздел «Своя обученная версия» шторки «Тонкая настройка»: сила сходства и выбор LoRA из ComfyUI.
// Список файлов приходит сверху. Наружу отдаёт ввод (или 'remove') только после того, как человек
// что-то тронул, — иначе LoRA не трогаем. Значение выбора — полный путь, в подписи пути нет.

import { useState } from 'react';
import { Folder } from 'lucide-react';
import { Button, Field, Select, TextField, ICON_SIZE, ICON_STROKE, C, FS, SP } from 'aihome_shell/kit';
import { labelOrShortName, loraLabelParts, loraLabels } from './loraName';
import { EXPERT } from './characterTexts';
import type { ImageEditCharacter, ImageEditLoras } from '../api';

type Lora = NonNullable<ImageEditCharacter['local']>;
export type LoraInput = { name: string; trigger: string; strength: number } | 'remove';

const DEFAULT_STRENGTH = 0.8;
// Узкий экран: в свёрнутом селекте режется имя, а дата и папка остаются видны; подпись под селектом — полная
const SELECT_NAME_MAX = 12;

export function CharacterLoraSection({ list, current, slug, disabled, onChange }: {
  // общий список ComfyUI, загруженный формой; null — ещё грузится
  list: ImageEditLoras | null;
  current: Lora | null | undefined;
  slug?: string;
  disabled?: boolean;
  // undefined — человек ничего не менял
  onChange: (lora: LoraInput | undefined) => void;
}) {
  const [name, setName] = useState(current?.lora ?? '');
  const [trigger, setTrigger] = useState(current?.trigger ?? '');
  const [strength, setStrength] = useState(current?.strength ?? DEFAULT_STRENGTH);
  const [removed, setRemoved] = useState(false);

  const touch = (n: string, t: string, s: number) => {
    // правка триггера или силы после «Убрать» отмену удаления не снимает
    if (!n && removed) return;
    setRemoved(false);
    onChange(n ? { name: n, trigger: t.trim(), strength: Number(s.toFixed(2)) } : undefined);
  };

  const remove = () => {
    // триггер и силу не стираем: при повторном выборе той же LoRA они подставятся сами
    setName(''); setRemoved(true);
    onChange('remove');
  };

  const has = !!name && !removed;
  const options = [...(list?.items ?? [])];
  // текущее имя, которого нет в списке, не теряем
  const missing = !!name && list?.available && !options.includes(name);
  const allNames = missing ? [name, ...options] : options;
  const shortLabels = loraLabels(allNames, list?.trained, SELECT_NAME_MAX);
  const fullLabels = loraLabels(allNames, list?.trained);
  const short = (n: string) => labelOrShortName(shortLabels, n);
  const selectOptions = [{ value: '', label: EXPERT.ownNone }, ...options.map(o => ({ value: o, label: short(o) }))];
  if (missing) selectOptions.splice(1, 0, { value: name, label: `${short(name)} (нет в ComfyUI)` });

  const hint = (text: string) => (
    <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{text}</div>
  );

  const picked = has ? loraLabelParts(labelOrShortName(fullLabels, name)) : null;

  // Выбор «не выбрана»: у персонажа была LoRA — снимаем, не было — ничего не меняем
  const pick = (v: string) => {
    if (v) { setName(v); touch(v, trigger, strength); return; }
    if (current?.lora) remove();
    else { setName(''); onChange(undefined); }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      {!list && hint('Загружаю список…')}
      {list && !list.available && hint(
        'ComfyUI сейчас недоступен, список LoRA получить нельзя. Настроить раздел можно будет, когда он заработает.'
        + (has ? ' Текущая LoRA сохранена и показана ниже.' : ''))}
      {list?.available && list.items.length === 0 && !has && hint(
        'В ComfyUI пока нет ни одной LoRA. Положите файл в папку loras у ComfyUI и откройте раздел снова.')}
      {list?.available && (list.items.length > 0 || has) && (
        <>
          <Field label={EXPERT.strengthLabel} hint={EXPERT.strengthHint}>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
              <input type="range" min={0} max={2} step={0.05} value={strength} aria-label={EXPERT.strengthLabel} disabled={disabled}
                onChange={e => { const s = Number(e.target.value); setStrength(s); touch(name, trigger, s); }}
                style={{ flex: 1, minWidth: 0, accentColor: C.accent }} />
              <span style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: 32, textAlign: 'right' }}>
                {strength.toFixed(2).replace('.', ',')}
              </span>
            </div>
          </Field>
          <Field label={EXPERT.ownLabel} hint={EXPERT.ownHint}>
            <Select value={removed ? '' : name} options={selectOptions} disabled={disabled} onChange={pick} />
          </Field>
          {picked && (
            <div data-lora-label style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, minWidth: 0, fontSize: FS.sm }}>
              <span title={`${picked.name} ${picked.rest}`.trim()} style={{
                color: C.textPrimary, fontWeight: 600, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>{picked.name}</span>
              {picked.rest && <span style={{ color: C.textMuted, flex: '0 0 auto', whiteSpace: 'nowrap' }}>{picked.rest}</span>}
            </div>
          )}
          {has && (
            <Field label="Слово-триггер своей версии" hint="Одно слово: буквы, цифры и «_». Оно попадёт в запрос, чтобы модель включила LoRA. Возьмите то, что указано при обучении LoRA.">
              <TextField value={trigger} placeholder="например, anna_face" disabled={disabled}
                onChange={v => { setTrigger(v); touch(name, v, strength); }} />
            </Field>
          )}
        </>
      )}
      {list && !list.available && has && (
        <div style={{ fontSize: FS.sm, color: C.textPrimary, overflowWrap: 'anywhere' }}>
          Файл: {labelOrShortName(fullLabels, name)}, {trigger}, {strength.toFixed(2).replace('.', ',')}
        </div>
      )}
      {has && (
        <div>
          <Button variant="ghost" size="sm" disabled={disabled} title="Убрать LoRA у персонажа" aria-label="Убрать LoRA у персонажа" onClick={remove}>
            Убрать
          </Button>
        </div>
      )}
      {slug && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>
          <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flex: '0 0 auto' }} />
          <span>Папка: characters/{slug}/</span>
        </div>
      )}
    </div>
  );
}
