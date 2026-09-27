// Панель «Персонажи» (слот workspace-panel-def, записка v3 «Персонажи»): список
// персонажей проекта с аватаром и числом фото, «Подключить к работе» / «Отключить»,
// «Изменить» и «Новый персонаж». Форма открывается прямо в панели. Подключённый
// персонаж — на проект: это чип в полосе «Картинки», он уходит в каждую генерацию.

import { useMemo, useState } from 'react';
import { Pencil, UserPlus, Users } from 'lucide-react';
import { Button, EmptyState, IconButton, showToast, C, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCharacter } from '../api';
import { setPrefs, usePrefs } from '../thread/prefs';
import { CharacterForm } from './CharacterForm';
import { dropCharacter, putCharacter, reloadCharacters, useCharacters } from './useCharacters';

const ic = (I: typeof Users, size: number = ICON_SIZE.sm) => <I size={size} strokeWidth={ICON_STROKE} />;

type Editing = { kind: 'new' } | { kind: 'edit'; slug: string } | null;

export function CharactersPanel({ projectId }: { projectId: string }) {
  const api = useMemo(() => imageEditorApi(), []);
  const { list, error } = useCharacters(projectId);
  const prefs = usePrefs(projectId);
  const [editing, setEditing] = useState<Editing>(null);

  const toggle = (c: ImageEditCharacter) => {
    if (prefs.characterSlug === c.slug) {
      setPrefs(projectId, { characterSlug: null });
      showToast(`Персонаж «${c.name}» отключён`, '', 'info');
    } else {
      setPrefs(projectId, { characterSlug: c.slug });
      showToast(`Персонаж «${c.name}» подключён к работе — чип в полосе «Картинки»`, '', 'info');
    }
  };

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
      <div style={{ flexShrink: 0, padding: SP.sm, borderBottom: `1px solid ${C.borderLight}` }}>
        <Button size="sm" variant="dashed" fullWidth leftIcon={ic(UserPlus, ICON_SIZE.xs)} onClick={() => setEditing({ kind: 'new' })}>
          Новый персонаж
        </Button>
      </div>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: SP.xxs, padding: SP.sm }}>
        {list === null && <div style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.sm }}>Загружаем…</div>}
        {list?.length === 0 && (error
          ? <EmptyState compact icon={ic(Users)} title="Не удалось загрузить персонажей"
              action={<Button size="sm" variant="secondary" onClick={() => { void reloadCharacters(projectId); }}>Повторить</Button>} />
          : <EmptyState compact icon={ic(Users)} title="Персонажей пока нет"
              subtitle="Персонаж — 3–10 фото лица. Подключённый персонаж уходит в каждую генерацию картинки." />)}
        {list?.map(c => {
          const on = prefs.characterSlug === c.slug;
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
                <span style={{ fontSize: FS.xs, color: C.textMuted }}>{c.photos.length} фото</span>
              </span>
              <Button size="sm" variant={on ? 'secondary' : 'ghostAccent'} onClick={() => toggle(c)}>
                {on ? 'Отключить' : 'Подключить к работе'}
              </Button>
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
