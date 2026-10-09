import { useEffect, useState } from 'react';
import { DeployModal } from './DeployModal';
import { api } from '../lib/api';
import { OPEN_DEPLOY_EVENT } from '../lib/deployState';

type Mode = 'closed' | 'open' | 'minimized';

// Хост окна выкатки над страницами. Две задачи:
// • окно и его слежение переживают переход между разделами (шапка их бы размонтировала);
// • после перезагрузки страницы идущая выкатка подхватывается заново — свёрнутой плашкой
//   в углу. Пока раннер собирает, продукт жив и отвечает, так что статус «running» виден сразу.
// Монтируется только у админа: эндпоинт admin-only.
export function DeployHost() {
  const [mode, setMode] = useState<Mode>('closed');
  // Подхват уже идущей выкатки: окно начинает сразу со слежения, минуя кнопку «Выкатить»
  const [resume, setResume] = useState(false);

  useEffect(() => {
    const open = () => { setResume(false); setMode('open'); };
    window.addEventListener(OPEN_DEPLOY_EVENT, open);
    return () => window.removeEventListener(OPEN_DEPLOY_EVENT, open);
  }, []);

  useEffect(() => {
    let alive = true;
    api.deploy.status()
      .then(s => {
        // canLaunch=false отсекает повисший running: его сервер считает протухшим и запуск
        // разрешает — подхватывать такую «выкатку» незачем
        if (!alive || !s.enabled || s.status?.result !== 'running' || s.canLaunch) return;
        setResume(true);
        setMode(m => (m === 'closed' ? 'minimized' : m));
      })
      .catch(() => { /* нет ответа — подхватывать нечего */ });
    return () => { alive = false; };
  }, []);

  if (mode === 'closed') return null;
  return (
    <DeployModal
      resume={resume}
      minimized={mode === 'minimized'}
      onMinimize={() => setMode('minimized')}
      onExpand={() => setMode('open')}
      onClose={() => setMode('closed')}
    />
  );
}
