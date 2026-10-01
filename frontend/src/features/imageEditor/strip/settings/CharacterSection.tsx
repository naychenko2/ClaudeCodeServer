// Секция «Персонаж и образцы». В личной области (вне проекта) персонажа нет — только образцы
// с компьютера: источник персонажей и образцов из проекта — его папки.

import { useRef, useState } from 'react';
import { FolderOpen, Plus, Upload, User } from 'lucide-react';
import { Button, Chip, Menu, MenuItem, Modal, SP, ICON_SIZE, api as appApi } from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCatalog, type ReferenceRole } from '../../api';
import { CHARACTERS_PANEL, revealWorkspacePanel } from '../../characters/panel';
import { useCharacters } from '../../characters/useCharacters';
import { maxSamples, roleShort, SAMPLE_ROLES, type Sample } from '../../editorInputs';
import { ProjectImagePicker } from '../../PanelSections';
import { isPersonalScope } from '../../scope';
import { setPrefs } from '../../thread/prefs';
import { getSamples, setSamples } from '../../thread/threadStore';
import { ic, Label, type Launch } from './primitives';

// projectId = null — личная область: персонажей нет, запросов к проекту тоже
export function useCharacter(projectId: string | null, slug: string | null) {
  const { list } = useCharacters(projectId);
  const current = projectId && slug ? list?.find(c => c.slug === slug) ?? null : null;
  const photo = projectId && current?.photos[0] ? imageEditorApi().characterPhotoUrl(projectId, current.slug, current.photos[0].file) : null;
  return { current, photo, name: current?.name ?? slug };
}

export function openCharacters(isMobile: boolean, sheet: () => void) {
  if (isMobile) sheet();
  else revealWorkspacePanel(CHARACTERS_PANEL);
}

// Образцы: чипы с миниатюрой, клик — роль, «+ Образец» — с компьютера или из проекта
// (у личной области — сразу с компьютера)
function SampleChips({ projectId, max }: { projectId: string; max: number }) {
  const personal = isPersonalScope(projectId);
  const samples = getSamples(projectId);
  const [roleFor, setRoleFor] = useState<string | null>(null);
  const [addAt, setAddAt] = useState<DOMRect | null>(null);
  const [picker, setPicker] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const id = () => `s${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  const add = (more: Sample[]) => setSamples(projectId, [...samples, ...more].slice(0, max));
  const roleOf = roleFor ? samples.find(s => s.id === roleFor) ?? null : null;

  return (
    <>
      {samples.map(s => (
        <span key={s.id} data-sample={s.name} style={{ display: 'inline-flex' }}>
          <Chip leading={<img src={s.url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />} maxW={170}
            title={`Образец · ${roleShort(s.role).toLowerCase()} — нажмите, чтобы сменить роль`}
            onClick={() => setRoleFor(s.id)}
            onRemove={() => setSamples(projectId, samples.filter(x => x.id !== s.id))}>
            {roleShort(s.role)} · {s.name}
          </Chip>
        </span>
      ))}
      {samples.length < max && (
        <span style={{ display: 'inline-flex' }}
          onClick={e => { if (personal) input.current?.click(); else setAddAt((e.currentTarget as HTMLElement).getBoundingClientRect()); }}>
          <Chip dashed leading={ic(Plus)} title="Картинка-пример для модели: лицо, стиль или предмет">Образец</Chip>
        </span>
      )}
      {addAt && (
        <Menu anchor={addAt} minWidth={230} onClose={() => setAddAt(null)}>
          <MenuItem icon={ic(Upload, ICON_SIZE.sm)} label="С компьютера" onClick={() => { setAddAt(null); input.current?.click(); }} />
          <MenuItem icon={ic(FolderOpen, ICON_SIZE.sm)} label="Из файлов проекта…" onClick={() => { setAddAt(null); setPicker(true); }} />
        </Menu>
      )}
      {roleOf && (
        <Modal title={`Как модели использовать «${roleOf.name}»`} width={380} onClose={() => setRoleFor(null)}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            {SAMPLE_ROLES.map(([role, full]) => (
              <Button key={role} size="sm" fullWidth variant={roleOf.role === role ? 'ghostAccent' : 'ghost'} style={{ justifyContent: 'flex-start' }}
                onClick={() => { setSamples(projectId, samples.map(x => (x.id === roleOf.id ? { ...x, role: role as ReferenceRole } : x))); setRoleFor(null); }}>
                {full}
              </Button>
            ))}
          </div>
        </Modal>
      )}
      {picker && !personal && (
        <ProjectImagePicker projectId={projectId}
          taken={samples.flatMap(s => (s.source === 'project' ? [s.path] : []))}
          onPick={path => {
            add([{ id: id(), source: 'project', name: path.split('/').pop() ?? path, role: 'style', path, url: appApi.files.fileUrl(projectId, path) }]);
            setPicker(false);
          }}
          onClose={() => setPicker(false)} />
      )}
      <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" multiple hidden
        onChange={e => {
          const files = [...(e.target.files ?? [])];
          e.target.value = '';
          if (files.length) add(files.map(f => ({ id: id(), source: 'upload', name: f.name, role: 'style', file: f, url: URL.createObjectURL(f) })));
        }} />
    </>
  );
}

// Шторку персонажей на телефоне держит хозяин секции: onCharacterSheet её открывает
export function CharacterSection({ projectId, L, catalog, isMobile, onCharacterSheet }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; isMobile: boolean; onCharacterSheet: () => void;
}) {
  const personal = isPersonalScope(projectId);
  const { current, photo, name } = useCharacter(personal ? null : projectId, L.prefs.characterSlug);
  const max = maxSamples(catalog.limits.maxReferences, L.model?.caps?.maxReferences);
  return (
    <>
      {(!personal || max > 0) && <Label>{personal ? 'Образцы' : 'Персонаж и образцы'}</Label>}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
        {personal ? null : L.prefs.characterSlug
          ? <Chip selected leading={photo ? <img src={photo} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : undefined}
              maxW={160} title="Фото персонажа уходят в каждую генерацию" onClick={() => openCharacters(isMobile, onCharacterSheet)}
              onRemove={() => setPrefs(projectId, { characterSlug: null })}>
              {current?.name ?? name}
            </Chip>
          : <Chip dashed leading={ic(User)} title="Персонажи проекта" onClick={() => openCharacters(isMobile, onCharacterSheet)}>Персонаж</Chip>}
        {max > 0 && <SampleChips projectId={projectId} max={max} />}
      </div>
    </>
  );
}
