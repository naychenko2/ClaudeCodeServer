// Редактор «Монтаж» (ADR-023, шаг 3ф-2): порядок сцен, склейки, подрезка, музыка и сценарий фильма —
// прежняя вкладка «Фильм» (`FilmTab`) в окне во весь экран; меняется хост, не логика. Работает с фильмом ОСНОВНОГО
// объекта контекста. «Работать со сценой» делает сцену основной и ставит ссылку «К фильму «…»»; закрытие
// окна ничего не отменяет. Низ — сборка фильма: то же, что чип «Собрать», с прогрессом и результатом.

import { Check, Hammer } from 'lucide-react';
import {
  Button, C, FS, getChatContextState, ICON_SIZE, ICON_STROKE, Modal, setContextReturn, setPrimary, SP, useIsMobile,
  type ChatContextItem,
} from 'aihome_shell/kit';
import type { VideoScene } from '../api';
import { FILM_KIND, filmPathOf, SCENE_KIND } from '../context/state';
import { isPersonalScope, videoScope } from '../scope';
import { closeVideoEditor, filmName, useVideoStoreVersion } from '../store/videoStore';
import { FilmTab, useFilmPanel } from './montage/FilmTab';

// Фильм, с которым открыт монтаж, — до смены основного объекта; из него строится «К фильму «…»»
function filmItem(sessionId: string): ChatContextItem | null {
  const p = getChatContextState(sessionId).primary;
  return p && p.kind === FILM_KIND ? p : null;
}

const leaveFor = (sessionId: string, film: ChatContextItem | null) => {
  const path = film ? filmPathOf(film as never) : null;
  if (film && path) setContextReturn(sessionId, { prev: film, label: `К фильму «${filmName(path)}»` });
  closeVideoEditor();
};

export function MontageEditor({ projectId, sessionId }: { projectId: string | null; sessionId: string }) {
  useVideoStoreVersion();
  const mobile = useIsMobile();
  const film = filmItem(sessionId);
  const path = film ? filmPathOf(film as never) : null;
  const panel = useFilmPanel(projectId, sessionId, mobile, path);
  const foot = panel.foot;
  const personal = isPersonalScope(videoScope(projectId));

  const workWith = (scene: VideoScene) => {
    void setPrimary(sessionId, { kind: SCENE_KIND, ref: { sceneId: scene.sceneId } }).then(res => {
      if (res === 'ok') leaveFor(sessionId, film);
    });
  };
  // Новая сцена становится основной сама (зеркало фокуса на сервере): остаётся поставить ссылку назад
  const afterNewScene = (created: boolean) => { if (created) leaveFor(sessionId, film); };

  return (
    <Modal size="fullscreen" title={path ? `Монтаж · ${filmName(path)}` : 'Монтаж'}
      onClose={closeVideoEditor} closeOnBackdrop={false}
      footer={(
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', justifyContent: 'flex-end', flexWrap: 'wrap' }}>
          {foot && (
            <span data-video-montage-foot="" style={{ marginRight: 'auto', fontSize: FS.xs, color: foot.reason ? C.warningText : C.textMuted, minWidth: 0 }}>
              {foot.progress ? foot.progress.label : foot.reason ?? foot.price?.join(' · ')}
            </span>
          )}
          {foot && (
            <Button size="sm" variant="secondary" leftIcon={<Hammer size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              disabled={!!foot.reason || !!foot.progress || foot.runDisabled} onClick={foot.onRun}>
              {foot.runLabel}
            </Button>
          )}
          <Button size="sm" variant="primary" leftIcon={<Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={closeVideoEditor}>Готово</Button>
        </div>
      )}>
      <div data-video-montage="" style={{ padding: mobile ? SP.md : SP.lg, overflowY: 'auto', flex: 1, minHeight: 0, boxSizing: 'border-box' }}>
        {personal || !path
          ? <div style={{ fontSize: FS.sm, color: C.textMuted }}>Фильм не выбран — возьмите его в работу в чате.</div>
          : <FilmTab ctx={{ projectId, sessionId, isMobile: mobile, onClose: closeVideoEditor }} path={path} editor={{ workWith, afterNewScene }} />}
      </div>
    </Modal>
  );
}
