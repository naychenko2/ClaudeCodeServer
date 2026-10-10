// Раздел «LoRA для локальной модели» в форме персонажа. Сворачиваемый, раскрыт сам,
// если LoRA уже задана. Список файлов приходит от формы персонажа. Наружу отдаёт ввод
// (или 'remove') только после того, как человек что-то тронул, — иначе LoRA не трогаем.

import { useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { Button, Field, Select, TextField, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import { labelOrShortName, loraLabels } from './loraName';
import type { ImageEditCharacter, ImageEditLoras } from '../api';

type Lora = NonNullable<ImageEditCharacter['local']>;
export type LoraInput = { name: string; trigger: string; strength: number } | 'remove';

const DEFAULT_STRENGTH = 0.8;
// Узкий экран: в свёрнутом селекте режется имя, а дата и папка остаются видны; подпись под селектом — полная
const SELECT_NAME_MAX = 12;

export function CharacterLoraSection({ list, current, disabled, onChange }: {
  // общий список ComfyUI, загруженный формой; null — ещё грузится
  list: ImageEditLoras | null;
  current: Lora | null | undefined;
  disabled?: boolean;
  // undefined — человек ничего не менял
  onChange: (lora: LoraInput | undefined) => void;
}) {
  const [open, setOpen] = useState(!!current);
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
  const selectOptions = options.map(o => ({ value: o, label: short(o) }));
  if (missing) selectOptions.unshift({ value: name, label: `${short(name)} (нет в ComfyUI)` });

  const hint = (text: string) => (
    <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{text}</div>
  );

  return (
    <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, minWidth: 0 }}>
      <button type="button" aria-expanded={open} onClick={() => setOpen(o => !o)} style={{
        display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%', padding: `${SP.sm}px ${SP.md}px`,
        background: 'none', border: 'none', cursor: 'pointer', textAlign: 'left',
        fontSize: FS.base, fontWeight: 600, color: C.textHeading, fontFamily: 'inherit',
      }}>
        {open ? <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : <ChevronRight size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        LoRA для локальной модели
      </button>
      {open && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, padding: `0 ${SP.md}px ${SP.md}px`, minWidth: 0 }}>
          {hint('Необязательно. С LoRA локальная модель точнее держит лицо персонажа. Без неё всё работает как раньше.')}
          {!list && hint('Загружаю список…')}
          {list && !list.available && hint(
            'ComfyUI сейчас недоступен, список LoRA получить нельзя. Настроить раздел можно будет, когда он заработает.'
            + (has ? ' Текущая LoRA сохранена и показана ниже.' : ''))}
          {list?.available && list.items.length === 0 && !has && hint(
            'В ComfyUI пока нет ни одной LoRA. Положите файл в папку loras у ComfyUI и откройте раздел снова.')}
          {list?.available && (list.items.length > 0 || has) && (
            <>
              <Field label="Файл LoRA" hint="Выберите из списка: AI Home берёт файлы, которые лежат в ComfyUI.">
                <Select value={removed ? '' : name} options={selectOptions} placeholder="Не выбрана" disabled={disabled}
                  onChange={v => { setName(v); touch(v, trigger, strength); }} />
              </Field>
              <Field label="Слово-триггер" hint="Одно слово: буквы, цифры и «_». Оно попадёт в запрос, чтобы модель включила LoRA. Возьмите то, что указано при обучении LoRA.">
                <TextField value={trigger} placeholder="например, anna_face" disabled={disabled}
                  onChange={v => { setTrigger(v); touch(name, v, strength); }} />
              </Field>
              <Field label="Сила" hint="От 0 до 2, по умолчанию 0,8. Чем больше, тем сильнее LoRA влияет на лицо; слишком большая сила искажает картинку.">
                <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
                  <input type="range" min={0} max={2} step={0.05} value={strength} aria-label="Сила LoRA" disabled={disabled}
                    onChange={e => { const s = Number(e.target.value); setStrength(s); touch(name, trigger, s); }}
                    style={{ flex: 1, minWidth: 0, accentColor: C.accent }} />
                  <span style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: 32, textAlign: 'right' }}>
                    {strength.toFixed(2).replace('.', ',')}
                  </span>
                </div>
              </Field>
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
        </div>
      )}
    </div>
  );
}
