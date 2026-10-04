// Список персонажей проекта панели «Персонажи» (слот workspace-panel-def, записка v3): аватар, число
// фото, «В контекст» / «В контексте ✓» по контексту чата, «Изменить». Форму открывает хозяин через
// editing/onEditing: «＋ Персонаж» живёт в закреплённом низу панели.

import { useMemo } from 'react';
import { Pencil, Users } from 'lucide-react';
import {
  Button, ContextAddButton, EmptyState, IconButton, refOf, useChatContext, C, FS, R, SP, ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import { imageEditorApi } from '../api';
import { setPrefs, usePrefs } from '../thread/prefs';
import { CharacterForm } from './CharacterForm';
import { dropCharacter, putCharacter, reloadCharacters, useCharacters } from './useCharacters';

const ic = (I: typeof Users, size: number = ICON_SIZE.sm) => <I size={size} strokeWidth={ICON_STROKE} />;

export type CharacterEditing = { kind: 'new' } | { kind: 'edit'; slug: string } | null;

export function CharactersPanel({ projectId, editing, onEditing, contextSessionId }: {
  projectId: string; editing: CharacterEditing; onEditing: (e: CharacterEditing) => void;
  // Чат, в контекст которого кладётся персонаж; null — чат не выбран, кнопки «В контекст» нет
  contextSessionId: string | null;
}) {
  const api = useMemo(() => imageEditorApi(), []);
  const { list, error } = useCharacters(projectId);
  const prefs = usePrefs(projectId);
  const ctx = useChatContext(contextSessionId);
  const setEditing = onEditing;

  const current = editing?.kind === 'edit' ? list?.find(c => c.slug === editing.slug) ?? null : null;
  if (editing && (editing.kind === 'new' || current)) {
    return (
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <CharacterForm key={editing.kind === 'edit' ? editing.slug : 'new'} api={api} projectId={projectId}
          character={current}
          onSaved={c => { putCharacter(projectId, c); setEditing(null); }}
          onDeleted={slug => {
            dropCharacter(projectId, slug);
            if (prefs.characterSlug === slug) setPrefs(projectId, { characterSlug: null });
            setEditing(null);
          }}
          onClose={() => setEditing(null)} />
      </div>
    );
  }

  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: SP.xxs, padding: SP.sm }}>
        {list === null && <div style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.sm }}>Загружаем…</div>}
        {list?.length === 0 && (error
          ? <EmptyState compact icon={ic(Users)} title="Не удалось загрузить персонажей"
              action={<Button size="sm" variant="secondary" onClick={() => { void reloadCharacters(projectId); }}>Повторить</Button>} />
          : <EmptyState compact icon={ic(Users)} title="Персонажей пока нет"
              subtitle="Персонаж — 3–10 фото лица. Положите его «В контекст» — он уйдёт в запрос." />)}
        {list?.map(c => {
          const candidate = { kind: 'image-character', ref: { slug: c.slug } };
          const on = !!contextSessionId && !!refOf(ctx, candidate);
          const photo = c.photos[0];
          return (
            <div key={c.slug} data-character={c.slug} style={{
              display: 'flex', alignItems: 'center', gap: SP.sm, padding: SP.xs, borderRadius: R.lg,
              border: `1px solid ${on ? C.accent : 'transparent'}`, background: on ? C.accentMuted : 'transparent',
            }}>
              <span style={{
                width: 36, height: 36, flex: '0 0 36px', borderRadius: R.max, overflow: 'hidden',
                background: C.bgInset, border: `1px solid ${C.border}`, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted,
              }}>
                {photo
                  ? <img src={api.characterPhotoUrl(projectId, c.slug, photo.file)} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                  : ic(Users, ICON_SIZE.xs)}
              </span>
              <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.name}</span>
                <span style={{ fontSize: FS.xs, color: C.textMuted }}>{c.photos.length} фото{on ? ' · в контексте · персонаж' : ''}</span>
              </span>
              {contextSessionId && <ContextAddButton sessionId={contextSessionId} projectId={projectId} candidate={candidate} size="sm" toggle />}
              <IconButton size="sm" title="Изменить" ariaLabel={`Изменить «${c.name}»`} onClick={() => setEditing({ kind: 'edit', slug: c.slug })}>
                {ic(Pencil, ICON_SIZE.xs)}
              </IconButton>
            </div>
          );
        })}
      </div>
    </div>
  );
}
