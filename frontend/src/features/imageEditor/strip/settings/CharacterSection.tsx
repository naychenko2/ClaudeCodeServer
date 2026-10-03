// Секция «Персонаж и образцы». В личной области (вне проекта) персонажа нет — только образцы
// с компьютера: источник персонажей и образцов из проекта — его папки.

import { useRef, useState } from 'react';
import { FolderOpen, Plus, Upload, User } from 'lucide-react';
import { Button, Chip, Menu, MenuItem, C, SP, ICON_SIZE, api as appApi } from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCatalog, type ReferenceRole } from '../../api';
import { useCharacters } from '../../characters/useCharacters';
import { maxSamples, roleShort, SAMPLE_ROLES, type Sample } from '../../editorInputs';
import { ProjectImagePicker } from '../../PanelSections';
import { isPersonalScope } from '../../scope';
import { setPrefs } from '../../thread/prefs';
import { modeAware } from '../../thread/modeState';
import { getSamples, setSamples } from '../../thread/threadStore';
import { ic, Label, touchWrap, type Launch } from './primitives';
import { ContextSampleChips, useContextSamples } from './ContextSampleChips';
import { characterSlugOf as characterSlugIn, sampleRefs, setCharacterRef } from '../../context/samples';

// projectId = null — личная область: персонажей нет, запросов к проекту тоже
export function useCharacter(projectId: string | null, slug: string | null) {
  const { list } = useCharacters(projectId);
  const current = projectId && slug ? list?.find(c => c.slug === slug) ?? null : null;
  const photo = projectId && current?.photos[0] ? imageEditorApi().characterPhotoUrl(projectId, current.slug, current.photos[0].file) : null;
  return { current, photo, name: current?.name ?? slug };
}

export { touchWrap };

// Образцы: чипы с миниатюрой, клик — роль, «+ Образец» — с компьютера или из проекта
// (у личной области — сразу с компьютера). Роль выбирают строкой прямо под чипами.
// touch — телефон панели v5: чипы и крестики — тач-цели не ниже 40
export function SampleChips({ projectId, max, initialRoleFor = null, touch }: {
  projectId: string; max: number; initialRoleFor?: string | null; touch?: boolean;
}) {
  const personal = isPersonalScope(projectId);
  const samples = getSamples(projectId);
  const [roleFor, setRoleFor] = useState<string | null>(initialRoleFor);
  const [addAt, setAddAt] = useState<DOMRect | null>(null);
  const [picker, setPicker] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const id = () => `s${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  const add = (more: Sample[]) => setSamples(projectId, [...samples, ...more].slice(0, max));
  const roleOf = roleFor ? samples.find(s => s.id === roleFor) ?? null : null;
  const pickRole = (role: ReferenceRole) => {
    if (roleOf) setSamples(projectId, samples.map(x => (x.id === roleOf.id ? { ...x, role } : x)));
    setRoleFor(null);
  };

  return (
    <>
      {samples.map(s => (
        <span key={s.id} data-sample={s.name} style={touchWrap(touch)}>
          <Chip touch={touch} leading={<img src={s.url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />} maxW={170}
            selected={roleFor === s.id}
            title={`Образец · ${roleShort(s.role).toLowerCase()} — нажмите, чтобы сменить роль`}
            onClick={() => setRoleFor(roleFor === s.id ? null : s.id)}
            onRemove={() => setSamples(projectId, samples.filter(x => x.id !== s.id))}>
            {roleShort(s.role)} · {s.name}
          </Chip>
        </span>
      ))}
      {samples.length < max && (
        <span data-sample-add="" style={touchWrap(touch)}
          onClick={e => { if (personal) input.current?.click(); else setAddAt((e.currentTarget as HTMLElement).getBoundingClientRect()); }}>
          <Chip dashed touch={touch} leading={ic(Plus)} title="Картинка-пример для модели: лицо, стиль или предмет">Образец</Chip>
        </span>
      )}
      {addAt && (
        <Menu anchor={addAt} minWidth={230} onClose={() => setAddAt(null)}>
          <MenuItem icon={ic(Upload, ICON_SIZE.sm)} label="С компьютера" onClick={() => { setAddAt(null); input.current?.click(); }} />
          <MenuItem icon={ic(FolderOpen, ICON_SIZE.sm)} label="Из файлов проекта…" onClick={() => { setAddAt(null); setPicker(true); }} />
        </Menu>
      )}
      {roleOf && (
        <div data-sample-roles={roleOf.name} role="group" aria-label={`Как модели использовать «${roleOf.name}»`}
          style={{ flexBasis: '100%', display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.xs, color: C.textMuted }}>
          <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            Как использовать «{roleOf.name}»:
          </span>
          {SAMPLE_ROLES.map(([role, full]) => {
            const on = roleOf.role === role;
            return (
              <Button key={role} size="xs" pill variant={on ? 'ghostAccent' : 'secondary'}
                style={{ border: `1px solid ${on ? C.accent : C.border}` }} onClick={() => pickRole(role)}>
                {full}
              </Button>
            );
          })}
        </div>
      )}
      {picker && !personal && (
        <ProjectImagePicker projectId={projectId} pickOnClick={modeAware()}
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

// onCharacters — показ персонажей (панель «Картинки» переключает вкладку)
export function CharacterSection({ projectId, L, catalog, onCharacters, sessionId }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; onCharacters: () => void; sessionId?: string | null;
}) {
  const personal = isPersonalScope(projectId);
  // При флаге персонаж и образцы — референсы контекста чата, prefs и память вкладки не пишутся
  const ctx = useContextSamples(sessionId);
  const slug = ctx ? characterSlugIn(ctx.state) : L.prefs.characterSlug;
  const { current, photo, name } = useCharacter(personal ? null : projectId, slug);
  const max = maxSamples(catalog.limits.maxReferences, L.model?.caps?.maxReferences);
  return (
    <>
      {(!personal || max > 0) && <Label>{personal ? 'Образцы' : 'Персонаж и образцы'}</Label>}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
        {personal ? null : slug
          ? <Chip selected leading={photo ? <img src={photo} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : undefined}
              maxW={160} title="Фото персонажа уходят в каждую генерацию" onClick={onCharacters}
              onRemove={() => { if (ctx) void setCharacterRef(ctx.sessionId, null); else setPrefs(projectId, { characterSlug: null }); }}>
              {current?.name ?? name}
            </Chip>
          : <Chip dashed leading={ic(User)} title="Персонажи проекта" onClick={onCharacters}>Персонаж</Chip>}
        {max > 0 && (ctx
          ? <ContextSampleChips projectId={projectId} sessionId={ctx.sessionId} refs={sampleRefs(ctx.state)} max={max} />
          : <SampleChips projectId={projectId} max={max} />)}
      </div>
    </>
  );
}
