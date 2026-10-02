// Общие поля тела панели «Картинки» v5 (флаг image-panel-v5, макет image-panel-v5.html,
// вариант 1): «Персонаж ▾» и образцы, список «Чем», «Ещё настройки · N ›». Собирают их
// CreateBody («Создать») и EditBody («Править»).

import { useState, type ReactNode } from 'react';
import { ChevronDown, ChevronRight, Contact, Info, Send, User } from 'lucide-react';
import {
  Button, Chip, ExecutorList, ExecutorSummaryRow, Menu, MenuItem, MenuSep, C, FS, SP, ICON_SIZE,
} from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCatalog } from '../api';
import { useCharacters } from '../characters/useCharacters';
import { maxSamples } from '../editorInputs';
import { isPersonalScope } from '../scope';
import { SampleChips, useCharacter } from '../strip/settings/CharacterSection';
import { ic, Label, type Launch } from '../strip/settings/primitives';
import { setPrefs } from '../thread/prefs';
import { executorRows, executorSettings, executorSummary, executorValue } from './executorRows';

// Подпись поля с пояснением справа: «Где менять · отметки — в редакторе»
export function FieldLabel({ children, aside }: { children: ReactNode; aside?: ReactNode }) {
  return (
    <Label>
      {children}
      {aside && <span style={{ textTransform: 'none', letterSpacing: 0, fontWeight: 400 }}> · {aside}</span>}
    </Label>
  );
}

// Строка-подсказка под полем: «✈ Что нарисовать — в поле ввода чата»
export function BodyHint({ icon = 'send', children }: { icon?: 'send' | 'info'; children: ReactNode }) {
  return (
    <div data-image-body-hint="" style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', marginTop: SP.xs, color: C.textMuted, fontSize: FS.sm }}>
      <span style={{ display: 'inline-flex', paddingTop: 2, flexShrink: 0 }}>{ic(icon === 'send' ? Send : Info)}</span>
      <span>{children}</span>
    </div>
  );
}

// «Персонаж ▾»: персонажи проекта меню прямо в «Настройках», вкладка «Персонажи» — библиотека
function CharacterPick({ projectId, slug, onCharacters }: { projectId: string; slug: string | null; onCharacters: () => void }) {
  const { list } = useCharacters(projectId);
  const { current, photo, name } = useCharacter(projectId, slug);
  const [at, setAt] = useState<DOMRect | null>(null);
  const open = (e: { currentTarget: EventTarget }) => setAt((e.currentTarget as HTMLElement).getBoundingClientRect());
  const thumb = (src: string | null) => (src ? <img src={src} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : undefined);
  const api = imageEditorApi();
  return (
    <>
      <span data-image-character-pick="" style={{ display: 'inline-flex' }} onClick={open}>
        {slug
          ? <Chip selected leading={thumb(photo)} maxW={170} title="Фото персонажа уходят в каждую генерацию · нажмите, чтобы сменить"
              onRemove={() => setPrefs(projectId, { characterSlug: null })}>
              {current?.name ?? name}{current ? ` · ${current.photos.length} фото` : ''}
            </Chip>
          : <Chip dashed leading={ic(User)} title="Персонажи проекта">
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>Персонаж{ic(ChevronDown)}</span>
            </Chip>}
      </span>
      {at && (
        <Menu anchor={at} anchorAlign="start" minWidth={240} onClose={() => setAt(null)}>
          {(list ?? []).map(c => (
            <MenuItem key={c.slug} iconSize={28}
              icon={c.photos[0]
                ? <img src={api.characterPhotoUrl(projectId, c.slug, c.photos[0].file)} alt="" style={{ width: 28, height: 28, borderRadius: '50%', objectFit: 'cover', display: 'block' }} />
                : ic(User, ICON_SIZE.sm)}
              label={c.name} hint={`${c.photos.length} фото${c.slug === slug ? ' · подключён' : ''}`}
              onClick={() => { setAt(null); setPrefs(projectId, { characterSlug: c.slug }); }} />
          ))}
          {!!list?.length && <MenuSep />}
          <MenuItem icon={ic(Contact, ICON_SIZE.sm)} label={list?.length ? 'Все персонажи…' : 'Завести персонажа…'} hint="вкладка «Персонажи»"
            onClick={() => { setAt(null); onCharacters(); }} />
        </Menu>
      )}
    </>
  );
}

// «Персонаж и образцы»: главное поле «Создать» и «Изменить». В личном чате персонажей нет
export function SamplesField({ projectId, L, catalog, onCharacters }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; onCharacters: () => void;
}) {
  const personal = isPersonalScope(projectId);
  const max = maxSamples(catalog.limits.maxReferences, L.model?.caps?.maxReferences);
  if (personal && max === 0) return null;
  return (
    <div data-image-samples-field="">
      <FieldLabel aside={max > 0 ? `до ${max}${L.model ? ` у ${L.model.label}` : ''}` : undefined}>
        {personal ? 'Образцы' : 'Персонаж и образцы'}
      </FieldLabel>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
        {!personal && <CharacterPick projectId={projectId} slug={L.prefs.characterSlug} onCharacters={onCharacters} />}
        {max > 0 && <SampleChips projectId={projectId} max={max} />}
      </div>
    </div>
  );
}

// «Чем: **Авто** · локально · Qwen-Image Edit · бесплатно ▾» и список под ним
export function ExecutorField({ L, catalog, isMobile }: { L: Launch; catalog: ImageEditCatalog; isMobile: boolean }) {
  const [open, setOpen] = useState(false);
  const route = L.route;
  const done = route ? { provider: route.provider, model: route.model } : L.provider && L.model ? { provider: L.provider.key, model: L.model.id } : null;
  const { name, parts } = executorSummary(catalog, L.settings, done);
  const free = L.priceLines[0] === 'Бесплатно';
  const rows = executorRows(catalog, { op: L.op, hasImage: L.hasImage, hasMask: L.hasMask, quick: L.quickAction });
  return (
    <div data-image-executor="" style={{ marginTop: SP.md }}>
      <ExecutorSummaryRow name={name} parts={parts} price={{ label: L.priceLines[0], tone: free ? 'success' : 'neutral' }}
        open={open} onToggle={() => setOpen(o => !o)} isMobile={isMobile} />
      {open && (
        <div style={{ marginTop: SP.xs }}>
          <ExecutorList rows={rows} value={executorValue(catalog, L.settings)} isMobile={isMobile}
            onChange={id => L.setSettings(executorSettings(id))} />
        </div>
      )}
    </div>
  );
}

// «› Ещё настройки · N» со сводкой справа; раскрывается на месте
export function MoreSettings({ summary, children }: { summary: string[]; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  if (!summary.length) return null;
  return (
    <div data-image-more="">
      <Button size="sm" variant="ghost" fullWidth leftIcon={ic(open ? ChevronDown : ChevronRight)} onClick={() => setOpen(o => !o)}
        style={{ marginTop: SP.xs, justifyContent: 'flex-start', fontWeight: 400 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flex: 1, minWidth: 0 }}>
          <span style={{ flexShrink: 0 }}>Ещё настройки · {summary.length}</span>
          <span style={{ flex: 1, minWidth: 0, textAlign: 'right', color: C.textMuted, fontSize: FS.xs, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {summary.join(', ')}
          </span>
        </span>
      </Button>
      {open && <div data-image-more-body="">{children}</div>}
    </div>
  );
}
