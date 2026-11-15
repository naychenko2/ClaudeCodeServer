// Панель «Файлы» личного чата (ADR-040 Ф2-9): папка «Личного» тем же файловым UI, что у проекта.
// Компоненты не копируются: псевдо-проект с id `personal-files:{чат}` переключает api-слой на личный
// маршрут (api.ts), а набор действий ограничен маршрутом 'personal' (personalFiles.ts). Просмотр
// файла открывается лайтбоксом поверх страницы — центра, как в проекте, у раздела «Чаты» нет.
import { useCallback, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { FileExplorer } from '../../components/FileExplorer';
import { FileViewer } from '../../components/FileViewer';
import { WallOverlay } from '../wall/WallOverlay';
import { Z } from '../../lib/design';
import { personalFilesProject } from './personalFiles';

export function PersonalFilesPanel({ sessionId }: { sessionId: string }) {
  const project = useMemo(() => personalFilesProject(sessionId), [sessionId]);
  const [openPath, setOpenPath] = useState<string | null>(null);
  const close = useCallback(() => setOpenPath(null), []);

  return (
    <>
      <FileExplorer key={project.id} project={project} activeFilePath={openPath} onOpenFile={setOpenPath} />
      {openPath !== null && createPortal(
        <div style={{ position: 'fixed', inset: 0, zIndex: Z.overlay }}>
          <WallOverlay onClose={close}>
            <FileViewer project={project} filePath={openPath} onClose={close} fullscreen />
          </WallOverlay>
        </div>,
        document.body,
      )}
    </>
  );
}
