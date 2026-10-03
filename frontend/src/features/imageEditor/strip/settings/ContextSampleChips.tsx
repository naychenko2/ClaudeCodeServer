// Образцы и персонаж при флаге composer-context-row (ADR-023, 2к-3): те же чипы, что у SampleChips и
// «Персонажа», но источник — референсы контекста чата, а не память вкладки. Добавление — загрузка и
// attachRef, снятие — detachRef; выбор переживает перезагрузку.

import { useRef, useState } from 'react';
import { FolderOpen, Plus, Upload } from 'lucide-react';
import {
  Button, Chip, FLAGS, Menu, MenuItem, C, SP, ICON_SIZE, detachRef, showToast, useChatContext, useFeature,
  type ChatContextRef,
} from 'aihome_shell/kit';
import { IMAGE_SAMPLE_ROLES } from '../../context/roles';
import { addProjectSample, addUploadSamples, changeSampleRole, SAMPLE_ACCEPT } from '../../context/samples';
import { ProjectImagePicker } from '../../PanelSections';
import { isPersonalScope } from '../../scope';
import { modeAware } from '../../thread/modeState';
import { ic, touchWrap } from './primitives';

// Под флагом и с чатом образцы живут в контексте
export function useContextSamples(sessionId: string | null | undefined) {
  const on = useFeature(FLAGS.composerContextRow);
  const state = useChatContext(on && sessionId ? sessionId : null);
  return on && sessionId ? { sessionId, state } : null;
}

const roleLabel = (role: string | null) => IMAGE_SAMPLE_ROLES.find(r => r.role === role)?.label.replace(/^Как /, '') ?? role ?? '';

export function ContextSampleChips({ projectId, sessionId, refs, max, touch }: {
  projectId: string; sessionId: string; refs: readonly ChatContextRef[]; max: number; touch?: boolean;
}) {
  const personal = isPersonalScope(projectId);
  const [roleFor, setRoleFor] = useState<string | null>(null);
  const [addAt, setAddAt] = useState<DOMRect | null>(null);
  const [picker, setPicker] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const roleOf = roleFor ? refs.find(r => r.id === roleFor) ?? null : null;
  const upload = (files: File[]) => {
    void addUploadSamples(projectId, sessionId, files.slice(0, Math.max(0, max - refs.length)), IMAGE_SAMPLE_ROLES[0].role)
      .catch(e => showToast((e as Error).message || 'Не удалось загрузить образец', '', 'error'));
  };
  return (
    <>
      {refs.map(r => (
        <span key={r.id} data-sample={r.label} data-ctx-sample={r.role} style={touchWrap(touch)}>
          <Chip touch={touch} maxW={170} selected={roleFor === r.id}
            leading={r.thumb ? <img src={r.thumb} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : undefined}
            title={`Образец · ${roleLabel(r.role).toLowerCase()} — нажмите, чтобы сменить роль`}
            onClick={() => setRoleFor(roleFor === r.id ? null : r.id)}
            onRemove={() => { void detachRef(sessionId, r.id); }}>
            {roleLabel(r.role)} · {r.label}
          </Chip>
        </span>
      ))}
      {refs.length < max && (
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
        <div data-sample-roles={roleOf.label} role="group" aria-label={`Как модели использовать «${roleOf.label}»`}
          style={{ flexBasis: '100%', display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.xs, color: C.textMuted }}>
          <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>Как использовать «{roleOf.label}»:</span>
          {IMAGE_SAMPLE_ROLES.map(({ role, label }) => {
            const on = roleOf.role === role;
            return (
              <Button key={role} size="xs" pill variant={on ? 'ghostAccent' : 'secondary'} style={{ border: `1px solid ${on ? C.accent : C.border}` }}
                onClick={() => { setRoleFor(null); if (!on) void changeSampleRole(sessionId, roleOf, role); }}>
                {label}
              </Button>
            );
          })}
        </div>
      )}
      {picker && !personal && (
        <ProjectImagePicker projectId={projectId} pickOnClick={modeAware()}
          taken={refs.flatMap(r => (r.kind === 'project-file' && typeof r.ref.path === 'string' ? [r.ref.path] : []))}
          onPick={path => { void addProjectSample(sessionId, path, IMAGE_SAMPLE_ROLES[0].role); setPicker(false); }}
          onClose={() => setPicker(false)} />
      )}
      <input ref={input} type="file" accept={SAMPLE_ACCEPT} multiple hidden
        onChange={e => {
          const files = [...(e.target.files ?? [])];
          e.target.value = '';
          if (files.length) upload(files);
        }} />
    </>
  );
}
