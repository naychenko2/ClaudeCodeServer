// Экран «Специальности» — центральная зона раздела «Персоны» в режиме specialties
// (волна 4 «Персонализация специальностей»). Роутер между тремя экранами:
//
//   - список ролей (нет roleKey):       SpecialtyListView
//   - визитка роли (roleKey):            SpecialtyRoleView
//   - настройка роли (viewMode === 'edit'): SpecialtyEditView
//
// Шапку с PillSwitch и подзаголовком рисует PersonasPage — этот компонент
// несёт только контент: ошибки стора + текущий экран в белой карточке.

import { useCallback, useEffect, useMemo, useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP, MODAL_W } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button } from '../../components/ui';
import { api } from '../../lib/api';
import { useProviderData, type TierKey } from '../../lib/modelProvidersShared';
import {
  loadUserLayer, saveLayer, useSpecialtySettings, useSaveState,
} from '../../lib/presets';
import type { LayerReducer } from '../../lib/presets';
import {
  getPromptSectionsCatalog, loadPromptSectionsCatalog, reloadSpecialties,
  useSpecialtyCatalog,
} from '../../lib/specialties';
import { useMe } from '../../lib/defaultPersona';
import { useIsMobile } from '../../lib/breakpoints';
import { bumpPersonas, usePersonas } from '../../lib/personas';
import type { UserProfile } from '../../types';
import type {
  Persona, SpecialtyPromptSectionsCatalog, SpecialtySettingsLayer,
  SpecialtySettingsResponse,
} from '../../types';
import { SpecialtyListView } from './SpecialtyListView';
import { SpecialtyRoleView } from './SpecialtyRoleView';
import { SpecialtyEditView } from './SpecialtyEditView';
import type { Scope } from './personaSpecialtyShared';

export interface PersonasSpecialtiesProps {
  roleKey?: string | null;
  viewMode?: 'list' | 'role' | 'edit';
  onNavigateList?: () => void;
  onNavigateRole?: (key: string) => void;
  onNavigateEdit?: (key: string) => void;
}

export function PersonasSpecialties(props: PersonasSpecialtiesProps): React.ReactElement {
  const isMobile = useIsMobile();
  const me = useMe();
  const isAdmin = me.role === 'admin';

  const catalog = useSpecialtyCatalog();
  const settingsAll = useSpecialtySettings();

  // Каталог секций промптов (для RolePresetsBlock и RolePeopleSlice). Грузится
  // лениво по требованию, общий кэш — несколько экранов и волны делят один запрос.
  const [promptSectionsCatalog, setPromptSectionsCatalog] =
    useState<SpecialtyPromptSectionsCatalog | null>(getPromptSectionsCatalog());
  useEffect(() => {
    let cancelled = false;
    void loadPromptSectionsCatalog().then(c => {
      if (!cancelled && c) setPromptSectionsCatalog(c);
    });
    return () => { cancelled = true; };
  }, []);

  // Список пользователей (для админа при выборе user-слоя) — подтягиваем по
  // требованию через api.users.list и кладём в локальный state. Поле роли в
  // UserProfile и так есть, нам нужно только отфильтровать своё и дать подписи.
  const [users, setUsers] = useState<UserProfile[] | null>(null);
  useEffect(() => {
    if (!isAdmin || users) return;
    let cancelled = false;
    api.users.list()
      .then(list => { if (!cancelled) setUsers(list); })
      .catch(() => { /* список не дошёл — дропдаун покажет пусто */ });
    return () => { cancelled = true; };
  }, [isAdmin, users]);

  // Слой выбирается один раз: для не-админа — всегда owner; для админа —
  // стартуем с owner (он живой, на «Для всех» чаще пусто).
  const [scope, setScope] = useState<Scope | null>(null);
  const activeScope: Scope = scope ?? 'owner';

  // Чужой слой: для админа на слое «user» нужен выбор пользователя +
  // отдельный запрос за user-слоем (бэк отдаёт user-слой ВЫЗЫВАЮЩЕГО).
  const [contextUserId, setContextUserId] = useState<string | null>(null);
  // Открыт ли дропдаун выбора пользователя (на слое «user» и пока не выбран).
  const [userPickerOpen, setUserPickerOpen] = useState(false);
  useEffect(() => {
    if (activeScope === 'user' && contextUserId) void loadUserLayer(contextUserId);
  }, [activeScope, contextUserId]);

  // Полный список персон — единый источник стопок аватаров на owner и
  // среза в визитке. На global/user персон НЕ показываем (T8: за другого
  // пользователя список был бы про чужих).
  const allPersonas = usePersonas();
  const personasForLayer: Persona[] = activeScope === 'owner' ? allPersonas : [];

  // userLayer: для админа на user-слое подгружаем по требованию через
  // прямой api.specialties.getUserLayer и кладём в локальный state. Это
  // обходит хук useUserLayer (он требует синхронного key=userId при маунте,
  // что неудобно для админских чатов с динамической сменой пользователя).
  const [userLayersById, setUserLayersById] = useState<Record<string, SpecialtySettingsLayer>>({});
  useEffect(() => {
    if (activeScope !== 'user' || !contextUserId) return;
    let cancelled = false;
    api.specialties.getUserLayer(contextUserId)
      .then(r => { if (!cancelled) setUserLayersById(prev => ({ ...prev, [contextUserId]: r.user })); })
      .catch(() => { /* слой не дошёл — баннер покажется из settingsError */ });
    return () => { cancelled = true; };
  }, [activeScope, contextUserId]);

  // После записи saveLayer — перечитываем каталог подписей: эффективные
  // имя/описание зависят от Display, который сейчас пришёл.
  const onSaveLayer = useCallback(
    async (s: Scope, reducer: LayerReducer): Promise<void> => {
      const userId = s === 'user' ? (contextUserId ?? null) : null;
      await saveLayer(s, reducer, userId);
      reloadSpecialties();
    },
    [contextUserId],
  );

  // Слой для текущего scope: для owner — settings.owner, для global —
  // settings.global, для user — локальный userLayer.
  const layerSettings: SpecialtySettingsLayer | null = activeScope === 'user'
    ? (contextUserId ? userLayersById[contextUserId] ?? null : null)
    : settingsAll
      ? (activeScope === 'global' ? settingsAll.global : settingsAll.owner)
      : null;

  // Глобальный слой нужен RolePresetsBlock для резолва поверх дефолтов кода.
  const globalLayer: SpecialtySettingsLayer | null = settingsAll?.global ?? null;
  // User-слой текущего выбора — SpecialtyRoleView передаёт его дальше в RolePresetsBlock.
  const userLayerForView: SpecialtySettingsLayer | null =
    activeScope === 'user' && contextUserId ? layerSettings : null;

  const data = useProviderData(isAdmin, contextUserId);
  const { settingsError } = useSaveState();
  // tierModels + data используются в следующих волнах (для подписей моделей).
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  void data;
  // Сейчас вычисления моделей по уровням не нужны — они появятся в этапе
  // «Матрицы моделей на экране роли». Заглушка, чтобы useMemo остался
  // доступным для последующих волн.
  const _tierModels = useMemo<Record<TierKey, string>>(() => ({
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  }), [data]);
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  void _tierModels;

  // === Роутинг viewMode ===
  const viewMode = props.viewMode ?? 'list';
  const roleKey = props.roleKey ?? null;

  const goList = useCallback(() => props.onNavigateList?.(), [props]);
  const goRole = useCallback((key: string) => props.onNavigateRole?.(key), [props]);
  const goEdit = useCallback((key: string) => props.onNavigateEdit?.(key), [props]);

  // Смена слоя: при уходе с user — забываем выбор пользователя (бэкенд требует
  // явного userId при PUT; случайная запись на чужой userId после прыжка
  // по экранам была бы граблей).
  const onLayerChange = useCallback((s: Scope) => {
    setScope(s);
    if (s !== 'user') {
      setContextUserId(null);
      setUserPickerOpen(false);
    } else {
      setUserPickerOpen(true);
    }
  }, []);

  const pickUser = useCallback((userId: string) => {
    setContextUserId(userId);
    setUserPickerOpen(false);
  }, []);

  // После успешного apply-defaults — перечитываем стор персон: realtime
  // подтвердит изменение отдельным сигналом, но локально нужно обновить
  // список сразу, чтобы счётчик «не хватает» пересчитался.
  const onPersonaUpdated = useCallback((_p: Persona) => {
    void bumpPersonas();
  }, []);

  const pickedUser = (contextUserId && users?.find(u => u.id === contextUserId)) ?? null;

  return (
    <div style={{
      maxWidth: MODAL_W.wide, marginLeft: 'auto', marginRight: 'auto', width: '100%',
      background: C.bgWhite, border: `1px solid ${C.borderLight}`,
      borderRadius: R.xl, padding: isMobile ? SP.md : SP.lg,
      boxShadow: SHADOW.card,
      boxSizing: 'border-box',
    }}>
      {settingsError && (
        <div style={{
          margin: `0 0 ${SP.sm}px`, padding: '7px 10px', borderRadius: 8, fontSize: 12,
          color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
        }}>{settingsError}</div>
      )}

      {/* Бейдж выбранного пользователя на user-слое (с возможностью сменить). */}
      {isAdmin && activeScope === 'user' && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.sm,
          marginBottom: SP.sm, flexWrap: 'wrap',
        }}>
          <button
            type="button"
            onClick={() => setUserPickerOpen(v => !v)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 6,
              font: 'inherit', fontFamily: FONT.sans, fontSize: FS.xs,
              fontWeight: 600, color: C.textHeading,
              background: C.bgSelected, border: `1px solid ${C.borderLight}`,
              borderRadius: R.pill, padding: '4px 10px', cursor: 'pointer',
            }}
            title={pickedUser ? 'Сменить пользователя' : 'Выбрать пользователя'}
          >
            <span>{pickedUser ? `Пользователь: ${pickedUser.username}` : 'Выберите пользователя'}</span>
            <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </button>
          {userPickerOpen && users && (
            <UserPicker
              users={users.filter(u => u.id !== me.userId)}
              pickedId={contextUserId}
              onPick={pickUser}
              onClose={() => setUserPickerOpen(false)}
            />
          )}
        </div>
      )}

      {viewMode === 'list' && (
        <SpecialtyListView
          isAdmin={isAdmin}
          layer={activeScope}
          onLayerChange={onLayerChange}
          catalog={catalog}
          layerSettings={layerSettings}
          personas={personasForLayer}
          onOpenRole={(k) => goRole(k)}
        />
      )}

      {viewMode === 'role' && roleKey && (
        <SpecialtyRoleView
          roleKey={roleKey}
          catalog={catalog ?? []}
          layer={activeScope}
          layerSettings={layerSettings}
          globalLayer={globalLayer}
          userLayer={userLayerForView}
          promptSectionsCatalog={promptSectionsCatalog}
          personas={personasForLayer.filter(p => p.specialty === roleKey)}
          onPersonaUpdated={onPersonaUpdated}
          onLayerChange={onLayerChange}
          onBack={goList}
          onEdit={() => goEdit(roleKey)}
        />
      )}

      {viewMode === 'edit' && roleKey && (
        <SpecialtyEditView
          roleKey={roleKey}
          catalog={catalog ?? []}
          layer={activeScope}
          layerSettings={layerSettings}
          userLayer={activeScope === 'user' ? layerSettings : null}
          personas={personasForLayer.filter(p => p.specialty === roleKey)}
          contextUserId={contextUserId}
          onBack={() => goRole(roleKey)}
          onSave={(reducer) => onSaveLayer(activeScope, reducer)}
        />
      )}
    </div>
  );
}

// Дропдаун выбора пользователя для user-слоя (B6). Открывается по кнопке
// «Пользователю …» или по бейджу над списком; на мобиле растягивается во
// всю ширину карточки. Записей обычно 2–5 (столько админов в инстансе), так
// что длинный список не ожидается — фиксированная высота с прокруткой.
function UserPicker({ users, pickedId, onPick, onClose }: {
  users: UserProfile[];
  pickedId: string | null;
  onPick: (id: string) => void;
  onClose: () => void;
}): React.ReactElement {
  return (
    <div style={{
      width: '100%',
      background: C.bgWhite, border: `1px solid ${C.border}`,
      borderRadius: R.md, padding: 4,
      boxShadow: SHADOW.card,
      maxHeight: 220, overflowY: 'auto',
    }}>
      {users.length === 0 ? (
        <div style={{
          padding: '10px 12px', fontSize: FS.xs, color: C.textMuted,
        }}>
          В инстансе больше нет пользователей.
        </div>
      ) : (
        users.map(u => {
          const picked = u.id === pickedId;
          return (
            <button
              key={u.id}
              type="button"
              onClick={() => { onPick(u.id); onClose(); }}
              style={{
                display: 'block', width: '100%', textAlign: 'left',
                font: 'inherit', fontFamily: FONT.sans, fontSize: FS.xs,
                fontWeight: picked ? 700 : 500,
                color: picked ? C.textHeading : C.textPrimary,
                background: picked ? C.bgSelected : 'transparent',
                border: 'none', borderRadius: R.sm,
                padding: '8px 10px', cursor: 'pointer',
                boxSizing: 'border-box',
              }}
            >
              {u.username}
              <span style={{
                marginLeft: 6, color: C.textMuted, fontWeight: 400,
              }}>· {u.role === 'admin' ? 'админ' : 'пользователь'}</span>
            </button>
          );
        })
      )}
      <div style={{ padding: '4px 6px 2px' }}>
        <Button variant="ghost" size="sm" onClick={onClose}>Закрыть</Button>
      </div>
    </div>
  );
}

// SpecialtySettingsResponse — локальный тип для компилятора. Полный набор
// полей (maxSubstitutions, presets, user, userId) не используется здесь —
// только owner/global.
export type { SpecialtySettingsResponse };
