import { useState, useEffect } from 'react';
import { Lock, Plus, Settings, Trash2 } from 'lucide-react';
import type { UserProfile } from '../types';
import { api } from '../lib/api';
import { C, R, FONT, SHADOW, MODAL_W, SP, FS } from '../lib/design';
import { Modal, ModalActions, TextField, Button, Checkbox, SegmentedControl } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useIsMobile } from '../lib/breakpoints';
import { FLAGS, useFeature } from '../lib/featureFlags';
// Ф2f: вход в окно настроек ребёнка. Каркас (Ф2b) и три раздела (Ф2c–Ф2e) собираются здесь:
// «Управление пользователями» — единственный вызывающий окна и единственное место, где список
// имён пользователей уже есть (контракт каркаса: имена даёт вызывающий, не вторая копия запроса).
import { ChildSettingsWindow } from '../features/childMode/ChildSettingsWindow';
import { ChildSubjectsSection } from '../features/childMode/ChildSubjectsSection';
import { ChildTimeSection, type ChildTimeValues } from '../features/childMode/ChildTimeSection';
import { ChildSafetySection } from '../features/childMode/ChildSafetySection';
import { childApi } from '../features/childMode/api';
import type { ChildRoute, ChildSettingsProfile } from '../features/childMode/types';

interface Props {
  currentUserId?: string;
  onClose: () => void;
}

// Инициалы из username: первые две буквы или первые буквы двух слов
function initials(username: string): string {
  const parts = username.trim().split(/[\s_-]+/).filter(Boolean);
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
  return username.slice(0, 2).toUpperCase();
}

// Бейдж роли
function RoleBadge({ role }: { role: 'admin' | 'user' }) {
  const isAdmin = role === 'admin';
  return (
    <span style={{
      display: 'inline-block',
      padding: '2px 8px',
      borderRadius: R.sm,
      fontSize: 11,
      fontWeight: 600,
      letterSpacing: '0.04em',
      background: isAdmin ? C.accentLight : C.bgPanel,
      color: isAdmin ? C.accent : C.textSecondary,
    }}>
      {isAdmin ? 'Администратор' : 'Пользователь'}
    </span>
  );
}

// Бейдж среды исполнения: у каждого пользователя видно, где работают его процессы
function EnvBadge({ env }: { env?: 'local' | 'container' }) {
  const sandboxed = env === 'container';
  return (
    <span
      title={sandboxed
        ? 'Процессы пользователя (ИИ, терминал, dev-серверы) работают изолированно в Docker-песочнице'
        : 'Процессы пользователя (ИИ, терминал, dev-серверы) работают на машине сервера с полным доступом'}
      style={{
        display: 'inline-block',
        padding: '2px 8px',
        borderRadius: R.sm,
        fontSize: 11,
        fontWeight: 600,
        letterSpacing: '0.04em',
        background: C.bgPanel,
        color: C.textSecondary,
      }}
    >
      {sandboxed ? '📦 Песочница' : '🖥 Сервер'}
    </span>
  );
}

// Аватар с инициалами
function Avatar({ username }: { username: string }) {
  return (
    <div style={{
      width: 28, height: 28, borderRadius: '50%',
      background: C.accentLight, color: C.accent,
      fontSize: 11, fontWeight: 700,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      flexShrink: 0, fontFamily: FONT.sans,
    }}>
      {initials(username)}
    </div>
  );
}

type Dialog =
  | { kind: 'add' }
  | { kind: 'delete'; user: UserProfile }
  | { kind: 'resetPassword'; user: UserProfile };

// Ф2f: состояние галки «Детский режим» одного пользователя. Список пользователей детский режим
// не отдаёт (UserDto в UsersController), поэтому при открытии модалки состояние читается
// GET child-profile по каждому пользователю песочницы: галка обязана показывать состояние
// сервера, а не угадывать его. loaded — снимок пришёл (profile null = выключен);
// busy — PUT child-mode в пути (галка гасится на время запроса); error — текст отказа GET или PUT,
// показывается под галкой.
interface ChildRowState {
  loaded: boolean;
  enabled: boolean;
  busy: boolean;
  error: string;
}

// Текст отказа — серверный message как есть (тот же порядок, что у rejectText каркаса окна:
// формулировки по кодам держит сервер). Своего текста нет — нейтральный минимум: пустая
// строка сделала бы отказ невидимым.
const rejectText = (e: unknown): string => {
  const m = (e as { body?: { message?: string } } | null)?.body?.message;
  return (typeof m === 'string' ? m.trim() : '') || 'Не получилось. Попробуйте ещё раз.';
};

export function UserManagementModal({ currentUserId, onClose }: Props) {
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const isMobile = useIsMobile();
  // Флаг child-mode прячет только этот интерфейс включения (ADR-041): ограничения ребёнка
  // зависят только от его профиля, выключенный флаг не снимает ни одного запрета.
  const childModeUi = useFeature(FLAGS.childMode);
  // Состояние галки по id пользователя: ключ — user.id, ответ для одного пользователя не попадает
  // в чужое состояние (тот же приём, что прежде).
  const [childStates, setChildStates] = useState<Record<string, ChildRowState>>({});
  // Ребёнок, чьё окно настроек открыто (Ф2f): окно — отдельный диалог поверх «Пользователей»,
  // в строку списка оно не встраивается (макет kids-settings-window: окно MODAL_W.wide).
  const [settingsUser, setSettingsUser] = useState<UserProfile | null>(null);
  // Список маршрутов для пикера раздела «Безопасность»: грузится, когда окно открыто, и отменяется
  // при его закрытии (тот же приём, что у старого редактора). null — ещё не загружен (пикер
  // не говорит «нет записей», пока список в пути); пустой — действительно нет.
  const [childRoutes, setChildRoutes] = useState<ChildRoute[] | null>(null);

  // Снимок состояния при открытии модалки: список пользователей режима не несёт, а галка без
  // снимка его угадывала бы. Отказ одного пользователя не глушит других: их строки остаются при
  // своих запросах (ключ — id).
  const loadChildStates = (list: UserProfile[]) => {
    const ids = list.filter((u) => u.executionEnvironment === 'container').map((u) => u.id);
    setChildStates(Object.fromEntries(ids.map((id) => [id, { loaded: false, enabled: false, busy: false, error: '' } as ChildRowState])));
    for (const id of ids) {
      childApi.getChildProfile(id)
        .then((r) => setChildStates((prev) => ({ ...prev, [id]: { ...prev[id], loaded: true, enabled: r.profile?.enabled === true, error: '' } })))
        .catch((e) => setChildStates((prev) => ({ ...prev, [id]: { ...prev[id], error: rejectText(e) } })));
    }
  };

  // Галка — узкая ручка PUT child-mode (ADR-041 ред. 4 решение 30): тела профиля нет; включение
  // заводит профиль «не настроен», выключение сохраняет настройки. Ответ — сохранённый профиль
  // сервера, им и обновляем галку. Отказ (not_container — среда сменилась с момента чтения
  // списка) — текст под галкой; галка остаётся при своём подтверждённом состоянии.
  const toggleMode = (userId: string, enabled: boolean) => {
    setChildStates((prev) => ({ ...prev, [userId]: { ...prev[userId], busy: true, error: '' } }));
    childApi.setChildMode(userId, enabled)
      .then((res) => setChildStates((prev) => ({ ...prev, [userId]: { loaded: true, enabled: res.profile?.enabled === true, busy: false, error: '' } })))
      .catch((e) => setChildStates((prev) => ({ ...prev, [userId]: { ...prev[userId], busy: false, error: rejectText(e) } })));
  };

  // Галка и кнопка — только пользователям песочницы: ограничения ребёнка работают только в
  // container, `local`-пользователю сервер отказывает not_container (та же граница, что KM-F12).
  // Галка гасится до снимка: состояния ещё нет, и клик послал бы неверное значение. Кнопка —
  // только при включённом режиме: окно правит настройки, выключенному режиму править нечего.
  const childRow = (user: UserProfile) => {
    if (user.executionEnvironment !== 'container') return null;
    const st = childStates[user.id];
    const known = st?.loaded === true;
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', ...(isMobile ? {} : { padding: '0 16px 12px' }) }}>
        <Checkbox
          checked={known && st?.enabled === true}
          disabled={!known || st?.busy === true}
          ariaLabel="Детский режим"
          onChange={(v) => toggleMode(user.id, v)}
        />
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>Детский режим</span>
        {known && st?.enabled === true && (
          <Button
            variant="ghostAccent" size="sm" dataTestId="child-settings-open"
            leftIcon={<Settings size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => setSettingsUser(user)}
          >
            Настроить занятия
          </Button>
        )}
        {st?.error && (
          <span data-testid="child-mode-error" style={{ fontSize: FS.sm, color: C.danger }}>
            {st.error}
          </span>
        )}
      </div>
    );
  };

  // ── Слоты окна (Ф2f, по контракту Ф2b: функция живого черновика) ──────────────────
  // Черновик окна — профиль контракта (ChildSettingsProfile); разделы просят свою форму
  // (ChildSettingsSubject[], ChildTimeValues, опекуны/согласие/маршрут). Маппинг — здесь:
  // это знание вызывающего, каркас о нём не догадывается.

  // Время профиля живёт в space «lessons» — единственном пространстве режима (тот же читатель,
  // что у Обзора: ChildSettingsOverview.tsx:90). Бюджеты и порог — поля профиля, не space.
  const timeValues = (p: ChildSettingsProfile) => {
    const sp = p.spaces?.lessons;
    return {
      windows: sp?.windows ?? [],
      dailyMinutes: sp?.dailyMinutes ?? null,
      warnMinutes: p.warnMinutes ?? 5,
      dailyBudgetUsd: p.dailyBudgetUsd ?? null,
      monthlyBudgetUsd: p.monthlyBudgetUsd ?? null,
    };
  };
  const patchTime = (p: ChildSettingsProfile, v: ChildTimeValues) => ({
    // Прочие space профиля (если появятся) остаются как есть: меняется только lessons.
    spaces: { ...p.spaces, lessons: { windows: v.windows, dailyMinutes: v.dailyMinutes } },
    warnMinutes: v.warnMinutes,
    dailyBudgetUsd: v.dailyBudgetUsd,
    monthlyBudgetUsd: v.monthlyBudgetUsd,
  });

  const userNames = Object.fromEntries(users.map((u) => [u.id, u.displayName || u.username]));

  // Г2: кандидаты в опекуны для окна — пользователи без детского режима: ребёнок не бывает
  // опекуном (его лимиты и стоп-сигналы — его собственные), и его профиль правится в его окне.
  // Исключается подтверждённый снимком включённый (тот же признак, что у галки: состояние без
  // снимка — не знание); local-пользователь — кандидат: опекуном он быть может, child-mode
  // ему просто не включается. Сам ребёнок (id окна) в список не входит.
  const guardianCandidates = (childId: string) =>
    users.filter((u) => u.id !== childId && childStates[u.id]?.enabled !== true).map((u) => u.id);

  useEffect(() => {
    api.users.list()
      .then(list => {
        setUsers(list);
        setLoading(false);
        // Снимок детского режима — вместе со списком: галка часть строки, её состояние должно
        // приходить вместе со строкой. Без флага интерфейс не рисуется — запросов тоже нет.
        if (childModeUi) loadChildStates(list);
      })
      .catch(e => { setLoadError(e.message ?? 'Ошибка загрузки'); setLoading(false); });
  }, [childModeUi]);

  // Список маршрутов для пикера окна: один запрос на открытое окно, отмена при его закрытии
  // (тот же приём, что у старого редактора). Отказ — пустой список: пикер сам говорит, что
  // выбирать не из чего, правка предметов и времени при этом не блокируется.
  useEffect(() => {
    if (!settingsUser) return;
    setChildRoutes(null);
    const ac = new AbortController();
    childApi.getRoutes({ signal: ac.signal })
      .then((list) => setChildRoutes(list))
      .catch(() => setChildRoutes([]));
    return () => ac.abort();
  }, [settingsUser]);

  const removeUser = (id: string) => setUsers(prev => prev.filter(u => u.id !== id));
  const upsertUser = (u: UserProfile) => {
    setUsers(prev =>
      prev.some(x => x.id === u.id) ? prev.map(x => x.id === u.id ? u : x) : [...prev, u]
    );
    // Ф2f, доработка по ревью 37e1a4f4: у пользователя, созданного в этой модалке, детского
    // профиля быть не может — POST создания не принимает тела профиля и при заведении его не
    // заводит (UsersController.Create → users.Add). Значит его состояние подтверждено без GET:
    // выключен. Без этой строки состояние заводит только loadChildStates (эффект открытия) —
    // галка новой строки мёртва до повторного открытия модалки. Инвариант «галка показывает
    // только подтверждённое состояние» сохраняется: loaded=true здесь — знание о факте создания,
    // не догадка. Уже прочитанное состояние не перезаписывается: upsert не теряет снимок;
    // граница по флагу — та же, что у loadChildStates: нет интерфейса — нет состояния.
    if (childModeUi && u.executionEnvironment === 'container') {
      setChildStates(prev => prev[u.id] ? prev : { ...prev, [u.id]: { loaded: true, enabled: false, busy: false, error: '' } });
    }
  };

  // Заголовок: на мобиле — column, кнопка под названием; на десктопе — row
  const titleNode = isMobile ? (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <span>Пользователи</span>
      <Button
        size="sm"
        variant="primary"
        onClick={() => setDialog({ kind: 'add' })}
        leftIcon={
          <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        }
      >
        Добавить пользователя
      </Button>
    </div>
  ) : (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
      <span>Пользователи</span>
      <Button
        size="sm"
        variant="primary"
        onClick={() => setDialog({ kind: 'add' })}
        leftIcon={
          <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        }
      >
        Добавить
      </Button>
    </div>
  );

  return (
    <>
      <Modal
        title={titleNode}
        width={620}
        onClose={onClose}
        // В titleNode справа primary-кнопка «Добавить» — встроенный крестик встал
        // бы правее неё и сломал порядок «главное действие у края». Вместо него
        // явный выход футером «Закрыть».
        hideCloseButton
        footer={
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <Button variant="secondary" size="md" onClick={onClose}>
              Закрыть
            </Button>
          </div>
        }
      >
        {loading && (
          <div style={{ color: C.textMuted, fontSize: 14, padding: '12px 0' }}>Загрузка…</div>
        )}
        {loadError && (
          <div style={{ color: C.danger, fontSize: 13 }}>{loadError}</div>
        )}
        {!loading && !loadError && (
          <div style={{
            border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden',
            boxShadow: SHADOW.card,
          }}>
            {users.map((user, i) => {
              const isSelf = user.id === currentUserId;
              const isLast = i === users.length - 1;

              if (isMobile) {
                // Мобильный вид: карточка с вертикальным layout
                return (
                  <div
                    key={user.id}
                    data-testid={`user-row-${user.id}`}
                    style={{
                      display: 'flex', flexDirection: 'column', gap: 10,
                      padding: '12px 14px',
                      borderBottom: !isLast ? `1px solid ${C.borderLight}` : 'none',
                      background: C.bgWhite,
                    }}
                  >
                    {/* Верхняя строка: аватар + имя + бейдж роли */}
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                      <Avatar username={user.username} />
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                          <span style={{
                            fontSize: 14, fontWeight: 600, color: C.textHeading,
                            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                          }}>
                            {user.username}
                          </span>
                          {isSelf && (
                            <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0 }}>(вы)</span>
                          )}
                        </div>
                        <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 2 }}>
                          {new Date(user.createdAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' })}
                        </div>
                      </div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, alignItems: 'flex-end' }}>
                        <RoleBadge role={user.role} />
                        <EnvBadge env={user.executionEnvironment} />
                      </div>
                    </div>

                    {/* Нижняя строка: кнопки действий — растянутые */}
                    {!isSelf && (
                      <div style={{ display: 'flex', gap: 8 }}>
                        <button
                          onClick={() => setDialog({ kind: 'resetPassword', user })}
                          title="Сбросить пароль"
                          style={{ ...actionBtn(false), flex: 1, justifyContent: 'center' }}
                        >
                          <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                          Сброс пароля
                        </button>
                        <button
                          onClick={() => setDialog({ kind: 'delete', user })}
                          title="Удалить пользователя"
                          style={{ ...actionBtn(false, true), flex: 1, justifyContent: 'center' }}
                        >
                          <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                          Удалить
                        </button>
                      </div>
                    )}

                    {/* Раздел профиля ребёнка — после основных полей, под флагом (ADR-041) */}
                    {childModeUi && childRow(user)}
                  </div>
                );
              }

              // Десктопный вид: горизонтальная строка; раздел профиля — под строкой
              return (
                <div
                  key={user.id}
                  data-testid={`user-row-${user.id}`}
                  style={{
                    borderBottom: !isLast ? `1px solid ${C.borderLight}` : 'none',
                    background: C.bgWhite,
                  }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '10px 16px' }}>
                    <Avatar username={user.username} />

                    <div style={{ flex: 1, minWidth: 0 }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                        <span style={{ fontSize: 14, fontWeight: 600, color: C.textHeading }}>
                          {user.username}
                        </span>
                        {isSelf && (
                          <span style={{ fontSize: 11, color: C.textMuted }}>(вы)</span>
                        )}
                      </div>
                      <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 2 }}>
                        {new Date(user.createdAt).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' })}
                      </div>
                    </div>

                    <EnvBadge env={user.executionEnvironment} />
                    <RoleBadge role={user.role} />

                    <div style={{ display: 'flex', gap: 6, flexShrink: 0 }}>
                      <button
                        disabled={isSelf}
                        onClick={() => setDialog({ kind: 'resetPassword', user })}
                        title="Сбросить пароль"
                        style={actionBtn(isSelf)}
                      >
                        <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                        Сброс пароля
                      </button>
                      <button
                        disabled={isSelf}
                        onClick={() => setDialog({ kind: 'delete', user })}
                        title="Удалить пользователя"
                        style={actionBtn(isSelf, true)}
                      >
                        <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                      </button>
                    </div>
                  </div>
                  {childModeUi && childRow(user)}
                </div>
              );
            })}
            {users.length === 0 && (
              <div style={{ padding: '24px', textAlign: 'center', color: C.textMuted, fontSize: 14 }}>
                Нет пользователей
              </div>
            )}
          </div>
        )}
      </Modal>

      {/* Ф2f: окно настроек ребёнка — три раздела в слотах каркаса (контракт Ф2b: слот — функция
          живого черновика). Маппинг полей профиля в форму разделов — здесь, у вызывающего. */}
      {settingsUser && (
        <ChildSettingsWindow
          userId={settingsUser.id}
          userLabel={settingsUser.displayName || settingsUser.username}
          userNames={userNames}
          onClose={() => setSettingsUser(null)}
          subjectsSection={(d) => (
            <ChildSubjectsSection
              value={d.profile.subjects ?? []}
              onChange={(next) => d.patch({ subjects: next })}
              disabled={d.saving}
              errorId={d.saveError?.subjectId ?? null}
            />
          )}
          timeSection={(d) => (
            <ChildTimeSection
              enabled={d.profile.enabled}
              values={timeValues(d.profile)}
              onChange={(v) => d.patch(patchTime(d.profile, v))}
            />
          )}
          // Г2: строки — по списку черновика (порядок guardianUserIds), флаг — известный из
          // ответа; у добавленного до сохранения опекуна флага нет — его сообщит ответ.
          safetySection={(d) => (
            <ChildSafetySection
              guardians={(d.profile.guardianUserIds ?? []).map(
                (id) => (d.profile.guardians ?? []).find((g) => g.userId === id) ?? { userId: id })}
              guardianCandidates={guardianCandidates(settingsUser.id)}
              // Г2-д1б: правка опекунов патчит только список. Согласие — не её часть: это
              // осознанный акт админа отдельной галкой (onAcknowledgeChange), явный отзыв
              // не должен стираться правкой списка; живую пару согласия слияние стора и так
              // не двигает (UserStore.cs:704-711).
              onGuardiansChange={(ids) => d.patch({ guardianUserIds: ids })}
              sharedProjectsInstanceEnabled={d.profile.sharedProjectsInstanceEnabled}
              userNames={userNames}
              acknowledge={d.profile.acknowledgeRouteRisk ?? null}
              acknowledgedAt={d.profile.routeRiskAcknowledgedAt}
              acknowledgedBy={d.profile.routeRiskAcknowledgedBy}
              onAcknowledgeChange={(v) => d.patch({ acknowledgeRouteRisk: v })}
              routes={childRoutes}
              route={{ providerKey: d.profile.providerKey, model: d.profile.model }}
              onRouteChange={(r) => d.patch({ providerKey: r.providerKey, model: r.model })}
              disabled={d.saving}
            />
          )}
        />
      )}

      {dialog?.kind === 'add' && (
        <AddUserDialog
          onClose={() => setDialog(null)}
          onCreated={u => { upsertUser(u); setDialog(null); }}
        />
      )}
      {dialog?.kind === 'delete' && (
        <DeleteUserDialog
          user={dialog.user}
          onClose={() => setDialog(null)}
          onDeleted={() => { removeUser(dialog.user.id); setDialog(null); }}
        />
      )}
      {dialog?.kind === 'resetPassword' && (
        <ResetPasswordDialog
          user={dialog.user}
          onClose={() => setDialog(null)}
        />
      )}
    </>
  );
}

// --- Диалог добавления пользователя ---

function AddUserDialog({ onClose, onCreated }: {
  onClose: () => void;
  onCreated: (u: UserProfile) => void;
}) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<'user' | 'admin'>('user');
  const [env, setEnv] = useState<'local' | 'container'>('local');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleCreate = async () => {
    if (!username.trim()) { setError('Введите имя пользователя'); return; }
    if (password.length < 8) { setError('Пароль — не менее 8 символов'); return; }
    setError('');
    setLoading(true);
    try {
      const user = await api.users.create({
        username: username.trim(), password, role, executionEnvironment: env,
      });
      onCreated(user);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка создания');
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Добавить пользователя"
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Создать"
          onConfirm={handleCreate}
          onCancel={onClose}
          loading={loading}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField value={username} onChange={setUsername} autoComplete="username" placeholder="Имя пользователя" autoFocus />
      <TextField type="password" value={password} onChange={setPassword} autoComplete="new-password" placeholder="Пароль (не менее 8 символов)" />
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          Роль
        </span>
        <SegmentedControl
          value={role}
          options={[
            { value: 'user', label: 'Пользователь' },
            { value: 'admin', label: 'Администратор' },
          ]}
          onChange={v => setRole(v as 'user' | 'admin')}
        />
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span style={{ fontSize: 12, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>
          Среда исполнения
        </span>
        <SegmentedControl
          value={env}
          options={[
            { value: 'local', label: 'Сервер' },
            { value: 'container', label: '📦 Песочница' },
          ]}
          onChange={v => setEnv(v as 'local' | 'container')}
        />
        <span style={{ fontSize: 12, color: C.textMuted, lineHeight: 1.5 }}>
          {env === 'local'
            ? 'AI, терминал и dev-серверы работают на машине сервера с полным доступом.'
            : 'Всё исполняется в изолированном Docker-контейнере: пользователь не видит файлы сервера. Сменить среду после появления чатов нельзя.'}
        </span>
      </div>
    </Modal>
  );
}

// --- Диалог удаления пользователя ---

function DeleteUserDialog({ user, onClose, onDeleted }: {
  user: UserProfile;
  onClose: () => void;
  onDeleted: () => void;
}) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleDelete = async () => {
    setLoading(true);
    try {
      await api.users.delete(user.id);
      onDeleted();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка удаления');
      setLoading(false);
    }
  };

  return (
    <Modal
      title="Удалить пользователя?"
      width={MODAL_W.confirm}
      onClose={onClose}
      subtitle={
        <>
          Пользователь «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{user.username}</strong>» будет удалён без возможности восстановления.
          {error && <div style={{ color: C.danger, marginTop: 8 }}>{error}</div>}
        </>
      }
      footer={
        <ModalActions
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={handleDelete}
          onCancel={onClose}
          loading={loading}
        />
      }
    />
  );
}

// --- Диалог сброса пароля ---

function ResetPasswordDialog({ user, onClose }: {
  user: UserProfile;
  onClose: () => void;
}) {
  const [newPassword, setNewPassword] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleReset = async () => {
    if (newPassword.length < 8) { setError('Пароль — не менее 8 символов'); return; }
    setError('');
    setLoading(true);
    try {
      await api.users.resetPassword(user.id, newPassword);
      onClose();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Ошибка сброса пароля');
      setLoading(false);
    }
  };

  return (
    <Modal
      title={`Сброс пароля: ${user.username}`}
      width={MODAL_W.form}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Установить пароль"
          onConfirm={handleReset}
          onCancel={onClose}
          loading={loading}
        />
      }
    >
      {error && <div style={{ color: C.danger, fontSize: 13 }}>{error}</div>}
      <TextField
        type="password"
        value={newPassword}
        autoComplete="new-password"
        onChange={setNewPassword}
        placeholder="Новый пароль (не менее 8 символов)"
        autoFocus
        onEnter={handleReset}
      />
    </Modal>
  );
}

// Стиль кнопки действия в строке таблицы
function actionBtn(disabled: boolean, danger = false): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 5,
    padding: '4px 9px', borderRadius: R.md, border: `1px solid ${C.border}`,
    background: 'none', cursor: disabled ? 'not-allowed' : 'pointer',
    fontSize: 12, fontWeight: 500, fontFamily: 'inherit',
    color: disabled ? C.textMuted : danger ? C.danger : C.textSecondary,
    opacity: disabled ? 0.45 : 1,
    transition: 'color 0.12s, border-color 0.12s',
  };
}
