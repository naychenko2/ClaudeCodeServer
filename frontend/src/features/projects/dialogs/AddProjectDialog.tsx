import { useEffect, useState } from 'react';
import type { Project, ProjectGroup, DesktopDevice } from '../../../types';
import { api } from '../../../lib/api';
import { C, MODAL_W } from '../../../lib/design';
import { Modal, ModalActions, TextField, Field, SegmentedControl } from '../../../components/ui';
import { GroupSelect } from '../GroupSelect';
import { SyncToggleRow } from '../components/SyncToggleRow';
import { GIT_MODES, GitModeCard, GitPushRow, type GitMode } from '../components/GitModeCards';
import { ProjectIconSection, type DraftGlyph } from '../ProjectIconSection';
import { invalidateProjectsCache } from '../useAllProjects';
import { basename } from '../../../lib/paths';
import { FLAGS, useFeature } from '../../../lib/featureFlags';

interface Props {
  groups: ProjectGroup[];
  defaultGroupId?: string;
  onSuccess: (project: Project) => void;
  onClose: () => void;
}

type Mode = 'new' | 'existing';
// Тип размещения проекта (ADR-016 §3.4): 'server' — обычный проект на хосте сервера,
// 'device' — локальный, привязан к устройству из реестра ADR-008. Сегмент показывается
// ТОЛЬКО под флагом `local-projects`; без флага поле скрыто и сразу подставляется
// 'server' — старые пути создания не меняются.
type Placement = 'server' | 'device';

// Единый диалог добавления проекта: сегмент «Новый / Существующий».
//  • Новый — создаём новую папку под путём по умолчанию (path = null).
//  • Существующий — привязываем существующую папку по пути.
// Под флагом `local-projects` добавляется второй сегмент «Серверный / Локальный»:
// локальный проект создаётся с привязкой к устройству (ADR-016 §4).
export function AddProjectDialog({ groups, defaultGroupId, onSuccess, onClose }: Props) {
  const localProjects = useFeature(FLAGS.localProjects);
  const [mode, setMode] = useState<Mode>('new');
  const [placement, setPlacement] = useState<Placement>('server');
  const [name, setName] = useState('');
  const [path, setPath] = useState('');
  const [groupId, setGroupId] = useState(defaultGroupId ?? '');
  const [sync, setSync] = useState(false);
  const [gitMode, setGitMode] = useState<GitMode>('none');
  const [gitPush, setGitPush] = useState(false);
  const [color, setColor] = useState<string | null>(null);
  // Подобранный значок держим до create(); после create() досылаем selectIcon.
  // null — не подбирали: у проекта будут инициалы (как до фичи, ADR-009 §7).
  const [draftGlyph, setDraftGlyph] = useState<DraftGlyph | null>(null);
  const [error, setError] = useState('');
  // Состояние секции устройства (ADR-016 §3.4): id выбранного устройства + путь
  // НА НЁМ. Список устройств подгружается только когда сегмент переключили на
  // «Локальный» — без этого лишний запрос на каждом открытии диалога
  const [devices, setDevices] = useState<DesktopDevice[] | null>(null);
  const [deviceId, setDeviceId] = useState('');
  const [devicePath, setDevicePath] = useState('');
  const [devicesLoading, setDevicesLoading] = useState(false);
  // Имя, набранное руками: пока его нет, для существующей папки название
  // подставляется из последнего сегмента пути (очистка поля снова включает автоподстановку).
  const [nameTouched, setNameTouched] = useState(false);

  const handleNameChange = (v: string) => {
    setName(v);
    setNameTouched(v.trim() !== '');
  };

  const handlePathChange = (v: string) => {
    setPath(v);
    if (!nameTouched) setName(basename(v.trim()));
  };

  const handleModeChange = (m: Mode) => {
    setMode(m);
    if (m === 'existing' && !nameTouched) setName(basename(path.trim()));
  };

  const handlePlacementChange = (p: Placement) => {
    setPlacement(p);
    if (p === 'device' && !devices) void loadDevices();
  };

  // Список устройств берём фильтрованно: для локального проекта годится только
  // устройство с возможностью exec (агент локальных проектов). revoked=false — живое;
  // harnessReady — без него привязка возможна, но ход не пройдёт — помечаем в пикере
  const loadDevices = async () => {
    setDevicesLoading(true);
    try {
      const list = await api.devices.list();
      // Тип capabilities у DesktopDevice — опциональный (старый бэк); трактуем
      // отсутствие как «exec неизвестен», т.е. не показываем (безопасный дефолт)
      const eligible = list.filter(d => !d.revoked && d.capabilities?.exec === true);
      setDevices(eligible);
    } catch {
      setDevices([]);
    } finally {
      setDevicesLoading(false);
    }
  };

  // Первое открытие под флагом: сегмент скрыт по умолчанию, но если флаг включён,
  // пользователь может переключиться — подгружаем устройства. Запрос ленивый
  // (см. handlePlacementChange), поэтому при mount не дёргаем
  useEffect(() => {
    if (!localProjects) return;
    // ничего — пользователь сам выберет «Локальный»
  }, [localProjects]);

  const localPlacement = placement === 'device';

  const handleConfirm = async () => {
    setError('');
    try {
      if (localPlacement) {
        if (!deviceId) {
          setError('Выберите устройство');
          return;
        }
        if (!devicePath.trim()) {
          setError('Укажите абсолютный путь к папке на устройстве');
          return;
        }
        const p = await api.projects.create(name.trim(), null, false, groupId || null, undefined, color, {
          deviceId,
          deviceRootPath: devicePath.trim(),
        });
        let created = p;
        if (draftGlyph && draftGlyph.name) {
          try {
            created = await api.projects.selectIcon(p.id, { name: draftGlyph.name });
          } catch { /* проект создан, иконку можно доставить в настройках */ }
        }
        invalidateProjectsCache();
        onSuccess(created);
        return;
      }
      const rootPath = mode === 'existing' ? (path.trim() || null) : null;
      const p = await api.projects.create(name.trim(), rootPath, false, groupId || null, {
        enableGit: gitMode !== 'none',
        gitAutoCommit: gitMode === 'auto',
        gitAutoPush: gitMode === 'auto' && gitPush,
      }, color);
      let created = p;
      if (draftGlyph && draftGlyph.name) {
        try {
          created = await api.projects.selectIcon(p.id, { name: draftGlyph.name });
        } catch { /* проект создан, иконку можно доставить в настройках проекта */ }
      }
      if (sync) api.sync.add(p.id, '', true).catch(() => {});
      invalidateProjectsCache(); // полка/палитра проектов видят новый проект сразу
      onSuccess(created);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Не удалось добавить проект');
    }
  };

  // Сегмент размещения: показываем только под флагом local-projects. ВАЖНО — это
  // НЕ сегмент «локальный/серверный» вообще, а переключатель «что именно создаём»:
  // «Серверный» = старый сценарий, «Локальный» = новый
  const placementOptions: { value: Placement; label: string }[] = localProjects
    ? [
        { value: 'server', label: 'Серверный' },
        { value: 'device', label: 'Локальный' },
      ]
    : [];

  // Локальные проекты не ведут git со стороны сервера — агент устройства сам
  // управляет файлами. Скрываем секцию git, чтобы не обещать лишнего
  const showGitSection = !localPlacement;
  // У локального путь НА устройстве обязателен и задаётся отдельным полем — старого
  // поля «путь к папке» для режима существующего нет: папка либо создаётся устройством,
  // либо должна существовать на нём (агент проверит при первом ходе)
  const showServerPath = !localPlacement && mode === 'existing';

  return (
    <Modal
      title="Добавить проект"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel={localPlacement ? 'Создать' : (mode === 'existing' ? 'Добавить' : 'Создать')}
          confirmDisabled={
            !name.trim() ||
            (localPlacement ? (!deviceId || !devicePath.trim()) : (mode === 'existing' && !path.trim()))
          }
          onConfirm={handleConfirm}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}

      <SegmentedControl<Mode>
        value={mode}
        onChange={handleModeChange}
        options={[{ value: 'new', label: 'Новый' }, { value: 'existing', label: 'Существующий' }]}
      />

      {placementOptions.length > 0 && (
        <SegmentedControl<Placement>
          value={placement}
          onChange={handlePlacementChange}
          options={placementOptions}
        />
      )}

      {/* Иконка + название проекта (тот же блок, что в «Редактировать проект»). В режиме
          создания значок держим в draftGlyph (name lucide) и крепится через selectIcon
          после create(). Черновой Project несёт актуальные name/color для превью. */}
      <ProjectIconSection
        creating
        project={{
          id: '', name, rootPath: '', createdAt: '', updatedAt: '',
          icon: {
            kind: 'glyph',
            color: color ?? undefined,
            glyph: draftGlyph ? { name: draftGlyph.name ?? null } : null,
          },
        }}
        name={name}
        onNameChange={handleNameChange}
        color={color}
        onColorChange={setColor}
        onIconUpdated={() => {}}
        onDraftGlyphChange={setDraftGlyph}
      />

      {localPlacement ? (
        <>
          <Field label="Устройство" hint="Где живут файлы проекта — машина из реестра устройств">
            {devicesLoading ? (
              <div style={{ fontSize: 13, color: C.textMuted, padding: '8px 0' }}>Загружаем устройства…</div>
            ) : devices && devices.length > 0 ? (
              <select
                value={deviceId}
                onChange={e => setDeviceId(e.target.value)}
                style={{
                  width: '100%', height: 34, padding: '0 10px', borderRadius: 6,
                  border: `1px solid ${C.border}`, background: C.bgWhite,
                  color: C.textPrimary, fontFamily: 'inherit', fontSize: 13,
                }}
              >
                <option value="">Выберите устройство</option>
                {devices.map(d => (
                  <option key={d.id} value={d.id}>
                    {d.name}{d.platform ? ` (${d.platform})` : ''}{d.online ? '' : ' · офлайн'}
                  </option>
                ))}
              </select>
            ) : (
              <div style={{ fontSize: 13, color: C.textSecondary, padding: '8px 0' }}>
                Нет устройств с агентом локальных проектов. Сопрягите устройство в меню «Устройства».
              </div>
            )}
          </Field>
          <Field label="Путь на устройстве" hint="Абсолютный путь к папке проекта на машине — например C:\Sources\my-project или /home/user/my-project">
            <TextField
              value={devicePath}
              onChange={setDevicePath}
              placeholder="C:\Sources\my-project"
              mono
            />
          </Field>
        </>
      ) : showServerPath ? (
        <Field label="Путь к папке" hint="Абсолютный путь к существующей папке проекта">
          <TextField value={path} onChange={handlePathChange} placeholder="C:\Sources\my-project" mono />
        </Field>
      ) : null}

      {groups.length > 0 && (
        <Field label="Группа">
          <GroupSelect groups={groups} value={groupId} onChange={setGroupId} />
        </Field>
      )}

      {/* Ведение истории файлов (git): без истории / ручной (код) / авто (документы).
          Карточки однострочные (подсказка в title) — тот же компактный паттерн, что и
          в «Редактировать проект» (GitModeCards), иначе секция разносит диалог по высоте.
          У ЛОКАЛЬНОГО проекта секция скрыта: историю ведёт агент устройства, серверный
          git к нему не относится (см. ADR-016 §4) */}
      {showGitSection && (
        <Field label="История файлов (Git)">
          <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
            {GIT_MODES.map(m => (
              <GitModeCard
                key={m.value}
                active={gitMode === m.value}
                label={m.label}
                hint={m.hint}
                onClick={() => setGitMode(m.value)}
              />
            ))}
            {gitMode === 'auto' && <GitPushRow checked={gitPush} onChange={setGitPush} />}
          </div>
        </Field>
      )}

      {/* Синк офлайн — серверная штука, локальному проекту не нужен (агент работает
          с файлами сам). У ЛОКАЛЬНОГО тоже скрываем, чтобы не обещать лишнего */}
      {!localPlacement && <SyncToggleRow enabled={sync} onChange={setSync} />}
    </Modal>
  );
}
