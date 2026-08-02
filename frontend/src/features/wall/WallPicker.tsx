// Пикер чатов для стены: все чаты владельца (кандидаты с бэка), сгруппированные по
// проектам, поиск по подстроке и фильтр «только занятые». Уже взятые — задизейблены.
import { useEffect, useMemo, useState } from 'react';
import { MessageCircle, Search } from 'lucide-react';
import type { Session } from '../../types';
import { C, FONT, FS, R } from '../../lib/design';
import { Modal, Toggle, IconField, Button } from '../../components/ui';
import { ICON_STROKE } from '../../components/ui/icons';
import { api } from '../../lib/api';
import { ProjectIcon } from '../projects/ProjectIcon';
import { useWallState, addChat, MAX_CHATS } from './wallStore';
import { showToast } from '../../lib/toast';

export function WallPicker({ onClose }: { onClose: () => void }) {
  const { chats, projects } = useWallState();
  const [candidates, setCandidates] = useState<Session[] | null>(null);
  const [query, setQuery] = useState('');
  const [busyOnly, setBusyOnly] = useState(false);

  useEffect(() => {
    api.wall.candidates().then(setCandidates).catch(() => setCandidates([]));
  }, []);

  const taken = useMemo(() => new Set(chats.map(c => c.id)), [chats]);

  // Группировка по проекту (внепроектные — отдельной группой в конце), фильтры сверху
  const groups = useMemo(() => {
    if (!candidates) return [];
    const q = query.trim().toLowerCase();
    const list = candidates.filter(s => {
      if (busyOnly && s.status !== 'working' && s.status !== 'waiting') return false;
      if (!q) return true;
      const project = s.projectId ? projects.get(s.projectId) : undefined;
      return (s.name ?? '').toLowerCase().includes(q) || (project?.name ?? '').toLowerCase().includes(q);
    });
    const byProject = new Map<string, Session[]>();
    for (const s of list) {
      const key = s.projectId ?? '';
      const arr = byProject.get(key) ?? [];
      arr.push(s);
      byProject.set(key, arr);
    }
    // Проектные группы по имени проекта, внепроектная — последней
    return [...byProject.entries()].sort(([a], [b]) => {
      if (a === '') return 1;
      if (b === '') return -1;
      return (projects.get(a)?.name ?? '').localeCompare(projects.get(b)?.name ?? '');
    });
  }, [candidates, query, busyOnly, projects]);

  return (
    <Modal
      title="Добавить чат на стену"
      onClose={onClose}
      width={520}
      // Набирают несколько чатов подряд, поэтому выход — явной кнопкой, а клик по
      // чату лишь добавляет его (строка сразу помечается «уже на стене»)
      footer={<Button variant="primary" size="md" fullWidth onClick={onClose}>Готово</Button>}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
        <IconField
          icon={<Search size={14} strokeWidth={ICON_STROKE} />}
          value={query}
          onChange={setQuery}
          placeholder="Поиск по чатам и проектам"
          autoFocus
          height={36}
          radius={R.md}
          fontSize={FS.base}
          style={{ flex: 1 }}
        />
        <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary, cursor: 'pointer', flexShrink: 0 }}>
          <Toggle checked={busyOnly} onChange={setBusyOnly} />
          активные сейчас
        </label>
      </div>

      {/* Высота ФИКСИРОВАННАЯ, а не по содержимому: иначе диалог прыгал бы при
          каждом переключении фильтра «активные сейчас» и на каждом добавленном чате */}
      <div style={{ height: '55vh', overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 4 }}>
        {candidates === null && (
          <div style={{ padding: 20, textAlign: 'center', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted }}>Загрузка…</div>
        )}
        {candidates !== null && groups.length === 0 && (
          <div style={{ padding: 20, textAlign: 'center', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted }}>
            {busyOnly ? 'Занятых чатов сейчас нет' : 'Ничего не нашлось'}
          </div>
        )}
        {groups.map(([pid, list]) => {
          const project = pid ? projects.get(pid) : undefined;
          return (
            <div key={pid || 'chats'}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '8px 2px 4px', fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}>
                {project && <ProjectIcon project={project} size={14} />}
                {project ? project.name : 'Чаты вне проектов'}
              </div>
              {list.map(s => {
                const isTaken = taken.has(s.id);
                const busy = s.status === 'working' || s.status === 'waiting';
                return (
                  <button
                    key={s.id}
                    disabled={isTaken}
                    // Пикер НЕ закрывается: набирают обычно несколько чатов подряд,
                    // а добавленный тут же становится «уже на стене» — видно, что взял.
                    // Мест не осталось — говорим об этом сразу, иначе клик молча ничего
                    // не делает и выглядит как поломка
                    onClick={() => {
                      if (addChat(s) === 'full') {
                        showToast('Стена', `На стене уже ${MAX_CHATS} чатов — уберите лишний`);
                      }
                    }}
                    style={{
                      display: 'flex', alignItems: 'center', gap: 8, width: '100%', textAlign: 'left',
                      padding: '7px 8px', border: `1px solid ${C.border}`, borderRadius: R.md,
                      background: C.bgWhite, cursor: isTaken ? 'default' : 'pointer',
                      opacity: isTaken ? 0.55 : 1, marginBottom: 4, boxSizing: 'border-box',
                    }}
                  >
                    <MessageCircle size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />
                    <span style={{ flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                      {s.name?.trim() || 'Без названия'}
                    </span>
                    {isTaken && <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>уже на стене</span>}
                    {!isTaken && busy && <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.accent, flexShrink: 0 }}>{s.status === 'working' ? 'идёт ход' : 'ждёт вас'}</span>}
                  </button>
                );
              })}
            </div>
          );
        })}
      </div>
    </Modal>
  );
}
