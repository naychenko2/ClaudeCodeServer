import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Sparkles } from 'lucide-react';
import type { AuthState, Persona, Project, Session } from '../../types';
import type { HubTabValue } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { C, FONT, FS, R, SP, PANEL_ANIM, CONTENT_MAX_W } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas, personaLabel } from '../../lib/personas';
import { useMe, refreshMe } from '../../lib/defaultPersona';
import { OPEN_INTRO_EVENT } from '../onboarding/OnboardingPage';
import { navPush, navReplace, getNav, parseHash, type NavSnapshot } from '../../lib/nav';
import { showToast } from '../../lib/toast';
import { Button, ConfirmDialog, IntroDot, IslandScaffold, BackButton } from '../../components/ui';
import { PillSwitch } from '../../components/Toolbar';
import { PageCanvas } from '../../components/ui/PageCanvas';
import { PersonaAvatar } from './PersonaAvatar';
import { useIsMobile } from '../../lib/breakpoints';
import { PanelZone } from '../../pages/workspace/PanelZone';
import { personasPanels } from '../../pages/workspace/panelStackState';
import { PERSONAS_KEYS } from '../../pages/workspace/panelCatalog';
import { PersonaList, type PersonaListMode } from './PersonaList';
import { PersonaForm, type PersonaFormHandle, type PersonaFormStatus } from './PersonaForm';
import { PersonaToolbar, type PersonaView } from './PersonaToolbar';
import { PersonaEditFab } from './PersonaEditFab';
import { PersonaPreview } from './PersonaPreview';
import { PersonaMemoryPanel } from './PersonaMemoryPanel';
import { PersonaBindingsPanel } from './PersonaBindingsPanel';
import { PersonaTasksPanel } from './PersonaTasksPanel';
import { PersonaAutomationPanel } from './PersonaAutomationPanel';
import { PersonaWizard } from './PersonaWizard';
import { PersonasHub } from './PersonasHub';
import { PersonasSpecialties } from './PersonasSpecialties';
import { useSpecialtyCatalog } from '../../lib/specialties';

// Утилита пуша URL для specialties: hash вида
//   #/personas/specialties
//   #/personas/specialties/{roleKey}
//   #/personas/specialties/{roleKey}/edit
// history.state остаётся валидным NavSnapshot с personaView='specialties' и
// дополнительными полями specialtyKey/specialtyEdit (расширение NavSnapshot
// типа для под-адресов раздела). Парсится в PersonasPage.onPop и в consume().
// Прямая запись через history.pushState: toHash в nav.ts не знает про под-
// адреса specialties/{roleKey} (его контракт — общий), а здесь нам нужен
// кастомный URL с двумя сегментами и опциональным /edit.
function pushSpecialtiesUrl(
  roleKey: string | null, viewMode: 'list' | 'role' | 'edit' | null,
): void {
  let hash = '#/personas/specialties';
  if (roleKey) hash = `#/personas/specialties/${encodeURIComponent(roleKey)}`;
  if (viewMode === 'edit' && roleKey) hash = `#/personas/specialties/${encodeURIComponent(roleKey)}/edit`;
  const state: Record<string, unknown> = { screen: 'personas', personaView: 'specialties' };
  if (roleKey) state.specialtyKey = roleKey;
  if (viewMode === 'edit') state.specialtyEdit = true;
  window.history.pushState(state, '', hash);
}

// Парсит под-адрес specialties из текущего hash: возвращает роль и viewMode.
// null — это не под-адрес specialties (другой раздел / старая форма).
function parseSpecialtiesHash(): { roleKey: string | null; viewMode: 'list' | 'role' | 'edit' } | null {
  const h = window.location.hash;
  const m = h.match(/^#\/personas\/specialties(?:\/([^/?]+))?(\/edit)?$/);
  if (!m) return null;
  const roleKey = m[1] ? decodeURIComponent(m[1]) : null;
  const viewMode: 'list' | 'role' | 'edit' = m[2] ? 'edit' : (roleKey ? 'role' : 'list');
  return { roleKey, viewMode };
}
import { PersonaActivityFeed } from './PersonaActivityFeed';
import { usePersonasActivity } from './personasActivity';
import { DeletePersonaDialog } from './DeletePersonaDialog';
import { useSpecialtiesCoverage } from './useSpecialtiesCoverage';

export function PersonasPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
}) {
  const isMobile = useIsMobile();
  // Раздел живёт на рельсе панелей: ширина, сворачивание и раскладка — в состоянии
  // зон (прежние sidebarMode и общая на все разделы ширина больше не нужны)
  // Раздел показывает глобальных персон, а по переключателю — вообще всех, вместе с
  // проектными. Дефолт «Глобальные»: у кого много проектных персон, список иначе
  // распухает и глобальные в нём тонут. Выбор запоминается на устройстве.
  const allPersonas = usePersonas();
  const [listMode, setListMode] = useState<PersonaListMode>(() =>
    localStorage.getItem('cc_personas_list_mode') === 'all' ? 'all' : 'global');
  useEffect(() => { localStorage.setItem('cc_personas_list_mode', listMode); }, [listMode]);
  const personas = useMemo(
    () => listMode === 'all' ? allPersonas : allPersonas.filter(p => p.scope === 'global'),
    [allPersonas, listMode]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Вкладка студии, на которую нужно сразу открыться (бэйдж автоматизации в чате) —
  // одноразовая, сбрасывается любым обычным выбором персоны из списка
  const [pendingView, setPendingView] = useState<PersonaView | null>(null);
  // На мобиле раздел «Специальности» рисуется только в режиме карточки (см. body ниже),
  // поэтому прямой заход/F5 по хешу specialties обязан стартовать с 'card' — иначе
  // диплинк открывал список персон вместо запрошенного экрана.
  const [mobileView, setMobileView] = useState<'list' | 'card'>(
    () => parseSpecialtiesHash() !== null ? 'card' : 'list');
  // Режим создания новой персоны: мастер прямо в контентной зоне
  const [creating, setCreating] = useState(false);
  // Режим центральной зоны: 'hub' (витрина), 'studio' (карточка персоны), 'create' (мастер),
  // 'specialties' (настройка специальностей). Не четвёртая ось навигации — вариант
  // содержимого того же центра; рельса слева и список персон остаются на месте.
  const [specialtiesMode, setSpecialtiesMode] = useState(() => parseSpecialtiesHash() !== null);
  // Под-адрес specialties: roleKey + viewMode. Под-адрес — отдельная запись
  // history.state, чтобы кнопка «Назад» возвращала на уровень выше, а не
  // выкидывала из раздела. Инициализация — из текущего hash.
  const initialSpec = parseSpecialtiesHash();
  const [specialtyRoleKey, setSpecialtyRoleKey] = useState<string | null>(initialSpec?.roleKey ?? null);
  const [specialtyViewMode, setSpecialtyViewMode] = useState<'list' | 'role' | 'edit'>(
    initialSpec?.viewMode ?? 'list',
  );
  // Проекты — чтобы показать имя/зону проектной персоны и открыть её проект в «Поговорить»
  const [projects, setProjects] = useState<Project[]>([]);
  // Идёт создание чата по кнопке «Поговорить»
  const [talking, setTalking] = useState(false);

  // Карточка-приглашение «знакомство» на мобиле (п.5.1.2) — PersonasHub в мобильную
  // ветку не попадает вовсе, без этой точки рендера знакомство на мобиле необнаружимо.
  // Живёт над списком, а не внутри PersonaList — список переиспользуется панелью
  // «Команда» проекта, где личное приглашение не к месту.
  const me = useMe();
  // Прямой хеш .../edit для не-админа режется в PersonasSpecialties (effectiveViewMode)
  // — он сам даунгрейдит до 'role' при !isAdmin. Здесь эффект НЕ нужен: он бы дал
  // лишний ре-рендер и переписал URL, а URL-сторона уже валидна.
  const defaultPersona = me.defaultPersonaId ? allPersonas.find(p => p.id === me.defaultPersonaId) : undefined;
  const showMobileInvite = isMobile && me.loaded && me.needsOnboarding && !!defaultPersona;
  // Бейдж охвата «N из M» на переключателе режима — считаем по стартовому слою
  // (см. useSpecialtiesCoverage). На переключателе виден и до открытия экрана.
  const specialtiesCoverage = useSpecialtiesCoverage(me.role === 'admin');
  // Каталог ролей — для подписи в мобильной шапке экрана при открытой роли.
  const specialtyCatalog = useSpecialtyCatalog();
  const mobileSpecialtyLabel = specialtyRoleKey
    ? specialtyCatalog?.find(r => r.key === specialtyRoleKey)?.label ?? null
    : null;
  // Отклик на тап «Выбрать другого ассистента» (Д-3): scrollIntoView контейнера здесь
  // бесполезен — прокрутка живёт внутри самого PersonaList, а контейнер и так во вьюпорте,
  // поэтому браузер ничего не делал, и тап выглядел мёртвой кнопкой. Короткая рамка C.accent
  // (~400мс) честнее по смыслу «вот он, список»: видна всегда, даже когда список прокручен.
  const [listHighlight, setListHighlight] = useState(false);
  const highlightTimer = useRef<number | undefined>(undefined);
  const focusMobileList = () => {
    setListHighlight(true);
    window.clearTimeout(highlightTimer.current);
    highlightTimer.current = window.setTimeout(() => setListHighlight(false), 400);
  };
  useEffect(() => () => window.clearTimeout(highlightTimer.current), []);

  useEffect(() => { void ensurePersonasLoaded(); }, []);
  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);

  // Зеркало списка персон для обработчика диплинка: подписка одна на монтирование,
  // а персоны могли ещё не догрузиться к моменту события
  const allPersonasRef = useRef(allPersonas);
  useEffect(() => { allPersonasRef.current = allPersonas; }, [allPersonas]);

  // Диплинк #/personas/{id} (старый #/agents/{id} парсится как алиас) — прямой заход/обновление
  // страницы. Плюс pending-канал cc_pending_persona_id + событие cc-open-persona — навигация
  // изнутри приложения (бэйдж автоматизации в чате глобальной персоны, см. lib/chatOrigin.ts),
  // когда раздел «Персоны» уже смонтирован и hash просто переключился на «#/personas» без id.
  useEffect(() => {
    const consume = () => {
      // Хинт с дашборда «Домой» (кнопка «Новая персона») — сразу открыть мастер создания
      if (sessionStorage.getItem('cc_pending_persona_create')) {
        sessionStorage.removeItem('cc_pending_persona_create');
        setSelectedId(null); setCreating(true); setMobileView('card');
        return;
      }
      const pending = sessionStorage.getItem('cc_pending_persona_id');
      if (pending) {
        sessionStorage.removeItem('cc_pending_persona_id');
        const view = sessionStorage.getItem('cc_pending_persona_view');
        sessionStorage.removeItem('cc_pending_persona_view');
        setSelectedId(pending); setMobileView('card');
        setPendingView(view === 'automation' ? 'automation' : null);
        // Диплинк на проектную персону — в дефолтном «Глобальные» её нет в списке, переключаем
        const target = allPersonasRef.current.find(p => p.id === pending);
        if (target && target.scope !== 'global') setListMode('all');
        navPush({ screen: 'personas', persona: pending });
        return;
      }
      const t = parseHash();
      if (t?.screen === 'personas' && t.personaId) {
        setSelectedId(t.personaId); setMobileView('card');
        setPendingView(t.personaView === 'automation' ? 'automation' : null);
        const target = allPersonasRef.current.find(p => p.id === t.personaId);
        if (target && target.scope !== 'global') setListMode('all');
      }
    };
    consume();
    window.addEventListener('cc-open-persona', consume);
    return () => window.removeEventListener('cc-open-persona', consume);
  }, []);

  // Back/forward браузера внутри раздела «Персоны». Восстанавливаем и выбор персоны,
  // и режим «Специальности» — иначе popstate по «#/personas/specialties» гасил бы
  // состояние specialtiesMode, и кнопка «Назад» возвращала бы не туда. Под-адрес
  // specialties/{roleKey} восстанавливается из history.state (pushSpecialtiesUrl
  // сохраняет specialtyKey и specialtyEdit в state), с фолбэком на парсинг hash.
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as (NavSnapshot & { specialtyKey?: string; specialtyEdit?: boolean }) | null;
      // Сначала пробуем state: он хранит то, что записал pushSpecialtiesUrl.
      // Затем — парсим hash напрямую: popstate иногда срабатывает без
      // восстановленного state, и тогда единственный надёжный источник —
      // window.location.hash.
      let roleKey: string | null = s?.specialtyKey ?? null;
      let edit = !!s?.specialtyEdit;
      const parsed = parseSpecialtiesHash();
      if (parsed) {
        roleKey = parsed.roleKey;
        edit = parsed.viewMode === 'edit';
      }
      if (s?.screen === 'personas' && (s.personaView === 'specialties' || parsed)) {
        setSpecialtiesMode(true);
        setCreating(false); setSelectedId(null);
        // 'card' — как в openSpecialties: на мобиле раздел живёт только в этом режиме.
        setMobileView('card');
        setSpecialtyRoleKey(roleKey);
        setSpecialtyViewMode(edit ? 'edit' : (roleKey ? 'role' : 'list'));
      } else if (s?.screen === 'personas') {
        setSpecialtiesMode(false);
        setSpecialtyRoleKey(null);
        setSpecialtyViewMode('list');
        setSelectedId(s.persona ?? null);
        setMobileView(s.persona ? 'card' : 'list');
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const selected = personas.find(p => p.id === selectedId) ?? null;

  // Диплинк/выбор указывает на проектную персону — в глобальном разделе её нет, сбрасываем выбор
  useEffect(() => {
    if (selectedId && allPersonas.some(p => p.id === selectedId) && !personas.some(p => p.id === selectedId)) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс выбора, если персоны диплинка нет в проекте
      setSelectedId(null); setMobileView('list');
      if (getNav()?.persona) navReplace({ screen: 'personas' });
    }
  }, [selectedId, allPersonas, personas]);

  // Переключатель режима центра: hub ↔ specialties. Режим студии/создания не имеет
// отношения к переключателю — там выбрана персона или идёт мастер. PillSwitch
// не принимает ref напрямую (внутри несколько кнопок), поэтому возвращаем фокус
// через обёртку: первый button внутри div с ref — сегмент «Персоны» при выходе
// из specialties.
  const modeSwitcherRef = useRef<HTMLDivElement | null>(null);
  const focusModeSwitcher = () => {
    const btn = modeSwitcherRef.current?.querySelector('button');
    if (btn instanceof HTMLButtonElement) btn.focus();
  };
  const openSpecialties = () => {
    if (specialtiesMode) return;
    setSpecialtiesMode(true);
    // mobileView='card': на мобиле раздел рисуется только в режиме карточки
    // (см. body ниже), и со списком вкладка «Специальности» переключалась,
    // а на экране оставался хаб персон.
    setCreating(false); setSelectedId(null); setMobileView('card');
    setSpecialtyRoleKey(null);
    setSpecialtyViewMode('list');
    pushSpecialtiesUrl(null, 'list');
  };
  // Переходы внутри specialties — навигация по под-адресам.
  const navigateSpecialtiesRole = (key: string) => {
    setSpecialtyRoleKey(key);
    setSpecialtyViewMode('role');
    pushSpecialtiesUrl(key, 'role');
  };
  const navigateSpecialtiesEdit = (key: string) => {
    setSpecialtyRoleKey(key);
    setSpecialtyViewMode('edit');
    pushSpecialtiesUrl(key, 'edit');
  };
  const navigateSpecialtiesList = () => {
    setSpecialtyRoleKey(null);
    setSpecialtyViewMode('list');
    pushSpecialtiesUrl(null, 'list');
  };
  const closeSpecialties = () => {
    if (!specialtiesMode) return;
    setSpecialtiesMode(false);
    // Намеренно НЕ делаем navReplace: иначе системная кнопка «Назад» в браузере
    // вернёт в specialties (текущая запись заменилась бы на hub, а prev — specialties).
    // Без replace текущая запись остаётся #/personas/specialties, а стейт уже hub —
    // браузерный popstate вытащит предыдущий снапшот (часто тоже #/personas) и
    // state не дёрнется. Фокус возвращаем на сегмент «Персоны» переключателя.
    window.setTimeout(focusModeSwitcher, 0);
  };

  // view — опционально сразу открыть конкретную вкладку студии (бэйдж автоматизации
  // в чате, клик по событию памяти в ленте активности хаба и т.п.)
  // Навигация: если пришли из specialties (через срез «кто работает» или сайдбар),
  // делаем navReplace — иначе кнопка «Назад» вернёт в specialties, а не в hub.
  // В обычном сценарии (hub → studio) — navPush, как было.
  const selectPersona = (id: string, view?: PersonaView) => {
    setCreating(false);
    setSpecialtiesMode(false);
    setSelectedId(id); setMobileView('card'); setPendingView(view ?? null);
    // Переход к проектной/не-глобальной персоне из среза в режиме specialties — в дефолтном
    // «Глобальные» её нет в списке, страж ниже молча сбросил бы выбор. Переключаем listMode
    // на 'all', чтобы персону стало видно. В диплинк-ветке выше это уже сделано в consume().
    const target = allPersonas.find(p => p.id === id);
    if (target && target.scope !== 'global') setListMode('all');
    if (specialtiesMode) navReplace({ screen: 'personas', persona: id });
    else navPush({ screen: 'personas', persona: id });
  };
  const clearSelection = () => {
    setCreating(false);
    // Очистка выбора из студии/создания — в hub. Если были в specialties, тоже сбрасываем.
    if (specialtiesMode) closeSpecialties();
    setSelectedId(null); setMobileView('list'); setPendingView(null);
    if (getNav()?.persona) navReplace({ screen: 'personas' });
  };
  // Кнопка «Новая персона» — мастер создания в контентной зоне. Та же логика
  // навигации: из specialties — replace, иначе push.
  const startCreate = () => {
    setSpecialtiesMode(false);
    setSelectedId(null); setCreating(true); setMobileView('card');
    if (specialtiesMode) navReplace({ screen: 'personas' });
    else if (getNav()?.persona) navReplace({ screen: 'personas' });
  };

  // Удаление в два шага: запрос подтверждения (диалог) → само удаление.
  // DeletePersonaDialog сам обрабатывает 400 «нужен преемник» у дефолт-персоны.
  const [deleteTarget, setDeleteTarget] = useState<Persona | null>(null);
  const onDelete = (p: Persona) => setDeleteTarget(p);

  // «Поговорить»: создаём чат от лица персоны и уводим пользователя в раздел разговоров.
  // Глобальная персона → чат вне проекта (таб «Чаты»). Проектная → её проект и стартовая сессия.
  const talk = async (p: Persona) => {
    if (talking) return;
    setTalking(true);
    try {
      const session = await api.personas.createChat(p.id, { mode: 'auto' });
      if (session.projectId) {
        const proj = projects.find(x => x.id === session.projectId);
        if (!proj) { showToast('Персоны', 'Проект персоны недоступен.'); return; }
        // Стартовую сессию отдаём проекту через sessionStorage — её подхватит WorkspacePage
        sessionStorage.setItem('cc_pending_session', JSON.stringify(session));
        window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project: proj } }));
      } else {
        // Глобальная персона: её чат живёт среди обычных чатов. Активный чат ChatsPage
        // читает из localStorage (ключ cc_open_chat) при монтировании.
        localStorage.setItem('cc_open_chat', session.id);
        onHubTab('chats');
      }
    } catch (e) {
      showToast('Персоны', e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setTalking(false);
    }
  };

  // Открыть СУЩЕСТВУЮЩИЙ чат персоны (из «Недавних разговоров» обзора) —
  // та же навигация, что у talk(), но без создания новой сессии.
  const openSession = (session: Session) => {
    if (session.projectId) {
      const proj = projects.find(x => x.id === session.projectId);
      if (!proj) { showToast('Персоны', 'Проект чата недоступен.'); return; }
      sessionStorage.setItem('cc_pending_session', JSON.stringify(session));
      window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project: proj } }));
    } else {
      localStorage.setItem('cc_open_chat', session.id);
      onHubTab('chats');
    }
  };

  const sidebar = (
    <>
      <PersonaList personas={personas} selectedId={selectedId} onSelect={selectPersona} onNew={startCreate}
        mode={listMode} onModeChange={setListMode} projects={projects} />
    </>
  );

  // Панель рельсы «Персоны» — тот же список; заголовок рисует PanelShell
  const zonePanels = {
    personasList: (
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
        {sidebar}
      </div>
    ),
  };

  // Шапка центральной зоны: переключатель режима (hub ↔ specialties).
  // В студии/создании переключатель скрыт — выбрана персона или идёт мастер,
  // переключаться некуда. Бейдж охвата «N из M» живёт на неактивном сегменте,
  // чтобы приглашать заглянуть (макет v4: «это и есть точка входа»).
  const showModeSwitch = !creating && !selected;

  const modeSwitcher = showModeSwitch ? (
    <div style={{ marginBottom: SP.lg }}>
      <div ref={modeSwitcherRef} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <PillSwitch<'hub' | 'specialties'>
          value={specialtiesMode ? 'specialties' : 'hub'}
          onChange={v => v === 'specialties' ? openSpecialties() : closeSpecialties()}
          options={[
            { value: 'hub', label: 'Персоны' },
            { value: 'specialties', label: 'Специальности',
              ...(specialtiesCoverage ? { title: `Охват специальностей: ${specialtiesCoverage}` } : {}) },
          ]}
          // PillSwitch рендерит бейдж через стандартный слот — нам нужен кастомный,
          // поэтому вешаем его рядом с подписью через обёртку ниже.
          persistKey="cc_personas_mode"
        />
        {specialtiesCoverage && (
          <span style={{
            fontFamily: 'JetBrains Mono, monospace', fontSize: FS.xs, fontWeight: 700,
            color: C.textSecondary, background: C.bgSelected,
            padding: '2px 8px', borderRadius: 12,
          }}>{specialtiesCoverage}</span>
        )}
      </div>
    </div>
  ) : null;

  const centerPane = creating
    ? <PersonaCreatePane
        projects={projects}
        onOpenStudio={p => selectPersona(p.id)}
        onStartChat={p => void talk(p)}
        onCancel={clearSelection}
        onBack={isMobile ? clearSelection : undefined} />
    : selected
    ? <PersonaStudio
        key={selected.id}
        persona={selected}
        projects={projects}
        talking={talking}
        initialView={pendingView}
        onDelete={() => onDelete(selected)}
        onTalk={() => talk(selected)}
        onOpenSession={openSession}
        onOpenSpecialties={openSpecialties}
        onBack={clearSelection}
        hero={!isMobile}
        isMobile={isMobile} />
    : specialtiesMode
    ? <PersonasSpecialties
        roleKey={specialtyRoleKey}
        viewMode={specialtyViewMode}
        onNavigateList={navigateSpecialtiesList}
        onNavigateRole={navigateSpecialtiesRole}
        onNavigateEdit={navigateSpecialtiesEdit}
      />
    : <PersonasHub
        personas={personas}
        talking={talking}
        onTalk={talk}
        onOpenSession={openSession}
        onNew={startCreate}
        onOpenPersonaView={selectPersona} />;

  const hasContent = creating || !!selected;

  // «Активность» на мобиле (QA Fold 8 round 2, F2): PersonasHub в мобильную ветку не
  // попадает, и лента активности там пропадала совсем. Рисуем её отдельной карточкой
  // под витриной (на мобиле витрина — сам список персон), во всю ширину и со своим
  // потолком прокрутки: без потолка лента вытесняет список за нижнюю кромку экрана.
  // Хук зовём всегда (правило хуков), фетч дешёвый и общий с хабом.
  const { items: mobileActivityItems, loading: mobileActivityLoading } = usePersonasActivity(personas);
  const [mobileActivityOpen, setMobileActivityOpen] = useState(false);

  const body = isMobile ? (
    (mobileView === 'card' && (hasContent || specialtiesMode))
      ? (
        <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>
          {/* На мобиле в режиме specialties — явная кнопка «Назад» слева в шапке
              экрана (макет v4: 360px). Возврат в hub вызывает closeSpecialties(false),
              чтобы кнопка «Назад» браузера тоже отрабатывала корректно. */}
          {specialtiesMode && (
            <div style={{
              display: 'flex', alignItems: 'center', gap: SP.sm,
              padding: `${SP.sm}px ${SP.md}px`,
              borderBottom: `1px solid ${C.borderLight}`,
              background: C.bgWhite, flex: 'none',
            }}>
              <BackButton
                onClick={() => closeSpecialties()}
                title="Назад в раздел Персоны"
                style={{
                  fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                }}
              >
                <span>Назад</span>
              </BackButton>
              <div style={{
                fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 600,
                color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis',
                whiteSpace: 'nowrap', flex: 1, minWidth: 0,
              }}>
                {mobileSpecialtyLabel ?? 'Специальности'}
              </div>
            </div>
          )}
          <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflowY: 'auto' }}>
            {centerPane}
          </div>
        </div>
      )
      : (
        <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>
          {/* На мобиле в режиме hub переключатель живёт над списком персон —
              тот же ряд, что и приглашение «Познакомиться». Бейдж охвата — справа от
              подписи, чтобы приглашать заглянуть. */}
          {showModeSwitch && (
            <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
                <PillSwitch<'hub' | 'specialties'>
                  value={specialtiesMode ? 'specialties' : 'hub'}
                  onChange={v => v === 'specialties' ? openSpecialties() : closeSpecialties()}
                  options={[
                    { value: 'hub', label: 'Персоны' },
                    { value: 'specialties', label: 'Специальности' },
                  ]}
                  persistKey="cc_personas_mode"
                />
                {specialtiesCoverage && (
                  <span style={{
                    fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
                    color: C.textSecondary, background: C.bgSelected,
                    padding: '2px 8px', borderRadius: 12,
                  }}>{specialtiesCoverage}</span>
                )}
              </div>
            </div>
          )}
          {showMobileInvite && defaultPersona && (
            <div style={mobileInviteCard}>
              <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
                <div style={{ position: 'relative', flex: 'none' }}>
                  <PersonaAvatar persona={defaultPersona} size={32} />
                  <IntroDot size={6} />
                </div>
                <div style={mobileInviteTitle}>Ваш ассистент пока стандартный</div>
              </div>
              <div style={mobileInviteText}>Познакомьтесь — он получит имя, характер и будет помнить, чем вы занимаетесь. Пара минут разговора.</div>
              <Button variant="primary" size="md" fullWidth leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                onClick={() => window.dispatchEvent(new CustomEvent(OPEN_INTRO_EVENT))}>
                Познакомиться
              </Button>
              <Button variant="ghost" size="md" fullWidth onClick={focusMobileList}>Выбрать другого ассистента</Button>
            </div>
          )}
          {!mobileActivityOpen && (
            <div style={{
              flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column',
              // Рамка всегда 2px (outline не двигает layout), анимируется только её цвет —
              // переход берём из PANEL_ANIM, новых значений в шкалы не заводим
              outline: `2px solid ${listHighlight ? C.accent : 'transparent'}`,
              outlineOffset: -2,
              transition: `outline-color ${PANEL_ANIM}`,
            }}>{sidebar}</div>
          )}
          <div style={mobileActivityOpen ? mobileActivityCardOpen : mobileActivityCard}>
            <PersonaActivityFeed
              personas={personas}
              items={mobileActivityItems}
              loading={mobileActivityLoading}
              expanded={mobileActivityOpen}
              onToggleExpanded={() => setMobileActivityOpen(v => !v)}
              onOpenSession={openSession}
              onOpenPersonaView={selectPersona}
              scrollMaxHeight={mobileActivityOpen ? undefined : MOBILE_ACTIVITY_FEED_H}
            />
          </div>
        </div>
      )
  ) : (
    // Десктоп: рельса панелей по краям | центр на холсте (hero-стиль: студия сама
    // рисует тулбар на холсте + контент-остров; хаб — на холсте)
    <IslandScaffold
      left={<PanelZone side="left" panelStack={personasPanels} allowedKeys={PERSONAS_KEYS} panels={zonePanels} />}
      right={<PanelZone side="right" panelStack={personasPanels} allowedKeys={PERSONAS_KEYS} panels={zonePanels} />}
      centerBare
      // Компенсация перекоса зон — только для хаба: его сетка ограничена
      // CONTENT_MAX_W и без компенсации съезжает вслед за центром, стоит открыть
      // панель с одной стороны. Студия, создание персоны и раздел «Специальности»
      // резиновые — им нечего компенсировать, ширина не передаётся.
      centerContentWidth={hasContent || specialtiesMode ? undefined : CONTENT_MAX_W}
      center={
        <div style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          {modeSwitcher}
          {centerPane}
        </div>
      }
    />
  );

  return (
    <PageCanvas>
      <HubHeader value="personas" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>{body}</div>
      {deleteTarget && (
        <DeletePersonaDialog
          persona={deleteTarget}
          onDeleted={() => { bumpPersonas(); clearSelection(); setDeleteTarget(null); }}
          onCancel={() => setDeleteTarget(null)}
        />
      )}
    </PageCanvas>
  );
}

// Панель создания новой персоны — пошаговый мастер (единая точка входа:
// по описанию / из шаблона / с нуля).
function PersonaCreatePane({ projects, onOpenStudio, onStartChat, onCancel, onBack }: {
  projects: Project[];
  onOpenStudio: (p: Persona) => void;
  onStartChat: (p: Persona) => void;
  onCancel: () => void;
  onBack?: () => void;
}) {
  const isMobile = useIsMobile();
  return (
    <PersonaWizard
      scope="global"
      projects={projects}
      onOpenStudio={onOpenStudio}
      onStartChat={onStartChat}
      onCancel={onCancel}
      onBack={onBack}
      isMobile={isMobile}
    />
  );
}

// Студия персоны: центральная область = обзор-визитка (дефолт), инлайн-форма
// профиля ИЛИ долгая память. Чата здесь нет — разговор живёт среди обычных
// чатов (кнопка «Поговорить»).
function PersonaStudio({ persona, projects, talking, initialView, onDelete, onTalk, onOpenSession, onOpenSpecialties, onBack, isMobile, hero }: {
  persona: Persona;
  projects: Project[];
  talking: boolean;
  // Вкладка, на которую нужно сразу открыться (бэйдж автоматизации в чате) — только при монтировании
  initialView?: PersonaView | null;
  onDelete: () => void;
  onTalk: () => void;
  onOpenSession: (s: Session) => void;
  // Мостик T9 — кнопка «Специальность: … →» в PersonaPreview
  onOpenSpecialties: () => void;
  onBack?: () => void;
  isMobile: boolean;
  // Стиль Islands (десктоп): тулбар — заголовок раздела на холсте, контент — остров
  hero?: boolean;
}) {
  // Активный вид: профиль (визитка/форма), умения, память или задачи.
  // Компонент перемонтируется по key={persona.id} — смена персоны сама сбрасывает вид на профиль.
  const [view, setView] = useState<PersonaView>(initialView ?? 'preview');
  // Развёрнута ли форма правки профиля (внутри вида «Профиль»). key={persona.id}
  // перемонтирует компонент, так что смена персоны сбрасывает editing сама.
  const [editing, setEditing] = useState(false);
  // Подтверждение отмены несохранённых изменений — через ConfirmDialog вместо window.confirm
  const [confirmDiscard, setConfirmDiscard] = useState<null | (() => void)>(null);

  // Императивный доступ к форме профиля + её состояние (для кнопок тулбара)
  const formRef = useRef<PersonaFormHandle>(null);
  const [status, setStatus] = useState<PersonaFormStatus>({ canSave: false, saving: false, dirty: false });
  const onStatus = useCallback((s: PersonaFormStatus) => {
    setStatus(prev => (prev.canSave === s.canSave && prev.saving === s.saving && prev.dirty === s.dirty ? prev : s));
  }, []);

  // Навигация между вкладками: если правим и есть несохранённое — сначала спросить
  const goView = (v: PersonaView) => {
    if (editing && status.dirty) { setConfirmDiscard(() => () => { setEditing(false); setView(v); }); return; }
    setEditing(false);
    setView(v);
  };

  const isProjectScope = persona.scope === 'project';
  const zoneName = isProjectScope
    ? (projects.find(p => p.id === persona.projectId)?.name ?? persona.projectId ?? 'Проект')
    : null;
  const zoneLabel = isProjectScope ? `Проект · ${zoneName}` : 'Глобальный';

  // Живой цвет из формы (перекрашивает акцент мгновенно) с фолбэком на сохранённый
  const [liveColor, setLiveColor] = useState<string | undefined>(undefined);
  const accent = AGENT_COLORS[liveColor ?? persona.avatar?.color ?? ''] ?? C.accent;

  // Смена дефолт-персоны: глобальную можно назначить личным дефолтом из меню тулбара;
  // проектную здесь не трогаем — её дефолт живёт в проекте
  const me = useMe();
  const isDefault = !isProjectScope && me.defaultPersonaId === persona.id;
  // Кандидат ждёт подтверждения: смена СУЩЕСТВУЮЩЕГО дефолта — только через диалог
  // (защита от случайного клика мимо пункта меню); первое назначение — сразу.
  const [confirmDefault, setConfirmDefault] = useState(false);
  const allPersonas = usePersonas();
  const currentDefault = me.defaultPersonaId ? allPersonas.find(p => p.id === me.defaultPersonaId) : undefined;
  const makeDefault = async () => {
    try {
      await api.personas.makeDefault(persona.id);
      await refreshMe();
      showToast('Персоны', `«${personaLabel(persona)}» — теперь ваша персона по умолчанию.`);
    } catch (e) {
      showToast('Персоны', e instanceof Error ? e.message : 'Не удалось назначить персону по умолчанию.');
    }
  };
  const requestMakeDefault = () => {
    if (me.defaultPersonaId && me.defaultPersonaId !== persona.id) setConfirmDefault(true);
    else void makeDefault();
  };

  const content = view === 'memory'
    // Память — под тулбаром, свой заголовок не нужен (идентичность уже в тулбаре)
    ? <div style={{ flex: 1, minHeight: 0 }}><PersonaMemoryPanel persona={persona} isMobile={isMobile} embedded /></div>
    : view === 'tasks'
    // Задачи — отфильтрованный вид реальных задач, где персона исполнитель
    ? <div style={{ flex: 1, minHeight: 0 }}><PersonaTasksPanel persona={persona} isMobile={isMobile} /></div>
    : view === 'knowledge'
    // Знания — привязки источников и правил (фича persona-bindings)
    ? <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaBindingsPanel persona={persona} accent={accent} isMobile={isMobile} />
      </div>
    : view === 'automation'
    // Проактивность — правила «событие → действие» (событийно-управляемая автоматизация)
    ? <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaAutomationPanel persona={persona} projects={projects} accent={accent} isMobile={isMobile} />
      </div>
    : editing
    // Профиль в режиме правки — инлайн-форма (действия — в тулбаре); успешное
    // сохранение возвращает к визитке
    ? <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaForm ref={formRef} persona={persona} projects={projects} onStatus={onStatus}
          onColorChange={setLiveColor} onOpenMemory={() => goView('memory')}
          onOpenKnowledge={() => goView('knowledge')}
          onSaved={() => setEditing(false)} onDelete={() => onDelete()} />
      </div>
    // Профиль — read-only визитка со сводкой и недавними разговорами
    : <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaPreview persona={persona} accent={accent} talking={talking} zoneLabel={zoneLabel}
          onTalk={onTalk} onOpenSession={onOpenSession}
          onEditProfile={() => setEditing(true)}
          onOpenKnowledge={() => goView('knowledge')}
          onOpenTasks={() => goView('tasks')}
          onOpenAutomation={() => goView('automation')}
          onOpenMemory={() => goView('memory')}
          onOpenSpecialties={onOpenSpecialties} isMobile={isMobile} />
      </div>;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <PersonaToolbar
        mode="edit"
        persona={persona}
        accent={accent}
        zoneLabel={zoneLabel}
        view={view}
        onView={goView}
        editing={editing}
        onEdit={() => setEditing(true)}
        onCancelEdit={() => { if (status.dirty) setConfirmDiscard(() => () => setEditing(false)); else setEditing(false); }}
        status={status}
        talking={talking}
        onTalk={onTalk}
        onDelete={onDelete}
        isDefault={isDefault}
        onMakeDefault={!isProjectScope ? requestMakeDefault : undefined}
        onSave={() => void formRef.current?.save()}
        onBack={onBack}
        isMobile={isMobile}
        hero={hero}
      />
      {/* Тонкая акцентная полоса персоны — разделитель шапки и контента
          (в hero заменяет нижнюю границу тулбара, контент живёт прямо на холсте) */}
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />
      {content}

      {/* Плавающая «Редактировать» на мобиле — вместо карандаша в тулбаре */}
      {isMobile && view === 'preview' && !editing && (
        <PersonaEditFab accent={accent} onClick={() => setEditing(true)} />
      )}

      {confirmDiscard && (
        <ConfirmDialog
          title="Отменить изменения?"
          subtitle="Несохранённые изменения профиля будут потеряны."
          confirmLabel="Отменить изменения"
          confirmVariant="danger"
          onConfirm={() => { const act = confirmDiscard; setConfirmDiscard(null); act?.(); }}
          onCancel={() => setConfirmDiscard(null)}
        />
      )}
      {confirmDefault && (
        <ConfirmDialog
          title="Сменить основного собеседника?"
          subtitle={<>Сейчас это <b>{currentDefault ? personaLabel(currentDefault) : '—'}</b>. Новые чаты начнёт <b>{personaLabel(persona)}</b> — прежняя останется на месте, её можно позвать в любой момент.</>}
          confirmLabel="Сменить"
          onConfirm={() => { setConfirmDefault(false); void makeDefault(); }}
          onCancel={() => setConfirmDefault(false)}
        />
      )}
    </div>
  );
}

// Карточка-приглашение «знакомство» (мобиль, п.5.1.2 волны 5) — над списком,
// не внутри PersonaList (та переиспользуется панелью «Команда» проекта)
const mobileInviteCard: React.CSSProperties = {
  flex: 'none', display: 'flex', flexDirection: 'column', alignItems: 'stretch', gap: SP.sm,
  background: C.accentLight, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: SP.md, margin: `${SP.md}px ${SP.md}px 0`,
};
const mobileInviteTitle: React.CSSProperties = {
  fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 600, color: C.textHeading, lineHeight: 1.3,
};
const mobileInviteText: React.CSSProperties = { fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 };

// «Активность» на мобиле (F2): своя карточка под списком персон, во всю ширину.
// Потолок ленты 220 — по спеке; выше ленты живёт её шапка с фильтрами, поэтому карточка
// сама по себе получается ощутимо выше и в поток встаёт фиксированным блоком (flex: none).
const MOBILE_ACTIVITY_FEED_H = 220;
const mobileActivityCard: React.CSSProperties = {
  flex: 'none', background: C.bgCard, border: `1px solid ${C.borderLight}`,
  borderRadius: R.xl, padding: SP.md, margin: SP.md,
};
// Раскрытая «Активность» («Показать всё») забирает пространство списка — так же, как в
// хабе разворот ленты вытесняет витрину. Прокрутка тут своя, потолок ленты снимается.
const mobileActivityCardOpen: React.CSSProperties = {
  flex: 1, minHeight: 0, overflowY: 'auto',
  background: C.bgCard, border: `1px solid ${C.borderLight}`,
  borderRadius: R.xl, padding: SP.md, margin: SP.md,
};
