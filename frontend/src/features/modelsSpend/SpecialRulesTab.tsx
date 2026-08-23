import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Button } from '../../components/ui';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { api } from '../../lib/api';
import { showToast } from '../../lib/toast';
import { useIsMobile } from '../../lib/breakpoints';
import { TIER_ORDER, type TierKey } from '../../lib/modelProvidersShared';
import { presetRoute } from '../../lib/presets';
import { ANY_SPECIALTY, useSpecialtyCatalog, withTierCell } from '../../lib/specialties';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import type { ProviderData } from '../../lib/modelProvidersShared';
import type { ModelOption } from '../../lib/models';
import type {
  Persona, ResetResult, SpecialtySettingsLayer, SpecialtySettingsResponse,
} from '../../types';
import { ResetConfirmDialog } from './ResetConfirmDialog';
import { AddRuleWizard } from './specialRules/AddRuleWizard';
import { LevelsPicture } from './specialRules/LevelsPicture';
import { AnySpecialtyCard, RuleGroupCard, RuleSpecCard } from './specialRules/cards';
import { SectionTitle, type PickerCtx, type Scope } from './specialRules/parts';
import {
  allRoleRows, buildGroups, buildLevelBars, catalogRoles, configuredRoleRows,
  countFilledFields, fieldsWord, groupsWord, pickStartScope, rolesWord, sameTriple,
  totalFields, tripleOf, type RoleRow,
} from './specialRules/model';
import { SpecialtyPromptSectionsPanel } from './SpecialtyPromptSectionsPanel';

// === Вкладка «Особые правила для специальностей» (макет v4) ===
//
// Экран отвечает на три вопроса подряд, сверху вниз:
//   1. «Любая специальность» — закреплённая карточка: что идёт персонам БЕЗ специальности.
//   2. «Картина по уровням» — три пропорциональные полосы: как устроено в целом.
//   3. Карточки — роли с совпадающими наборами собраны в группы, остальные по одной.
//
// Почему не список из 42 строк и не 14 изолированных карточек — см. проверку гипотезы
// в макете: различных наборов 9 на 14 ролей, а повторяемость поуровневая, и увидеть её
// можно только полосами. Группа — ВИД, а не сущность: записи у ролей остаются свои,
// поэтому «выделить» роль ничего не пишет на сервер.
//
// Три слоя: «Для всех» (правит админ) · «Только для меня» · «Пользователю…» (админ,
// чужой слой грузится отдельным запросом — GET /settings отдаёт user-слой ВЫЗЫВАЮЩЕГО).
// Стартовый слой: пустой «Для всех» уводит на «Только для меня» — иначе экран встречает
// пустотой, пока все правила лежат в личном слое (решение владельца 14.08.2026).
//
// Горизонтального скролла нет ни на какой ширине: всё течёт сверху вниз, длинные подписи
// режутся многоточием с полным текстом в title. Вложенность ровно два уровня:
// карточка → панель выбора.

interface SpecialRulesTabProps {
  isAdmin: boolean;
  meUserId: string | null;
  data: ProviderData;
  contextUserId: string | null;
  onContextUserId: (id: string | null) => void;
  settings: SpecialtySettingsResponse | null;
  models: ModelOption[];
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  savingScope: Scope | null;
  onSaveLayer: (scope: Scope, next: SpecialtySettingsLayer) => Promise<void>;
  // Перечитывание настроек после сброса делает сама модалка внутри onReset — вкладке
  // остаётся лишь дочитать чужой слой (он живёт здесь, а не в settings)
  onReloadSettings: () => Promise<void>;
  resettingScope: Scope | null;
  onReset: (scope: Scope, key?: string) => Promise<ResetResult>;
}

export function SpecialRulesTab({
  isAdmin, meUserId, data, contextUserId, onContextUserId, settings, models, tierModels,
  ollamaModel, savingScope, onSaveLayer, resettingScope, onReset,
}: SpecialRulesTabProps) {
  const isMobile = useIsMobile();
  const catalog = useSpecialtyCatalog();

  // Слой выбирается один раз — как только доехали настройки и каталог. Дальше его
  // двигает только пользователь: пересчёт на каждом обновлении settings перекидывал бы
  // человека на другой слой прямо во время правки.
  // Слой ВЫВОДИТСЯ, пока человек его не трогал: состояние держит только явный выбор.
  // Так стартовый слой не требует эффекта с setState (каскад ре-рендеров) и сам
  // доуточняется, когда доедут настройки: до загрузки админ видит «Для всех», после —
  // «Только для меня», если общий слой пуст.
  const [scope, setScope] = useState<Scope | null>(null);
  const activeScope: Scope = scope ?? pickStartScope(settings, catalog, isAdmin);

  // Чужой слой («Пользователю…»): GET /specialties/settings отдаёт user-слой ВЫЗЫВАЮЩЕГО,
  // поэтому слой выбранного пользователя читаем отдельным запросом и держим здесь.
  // Ключом служит сам userId — «ещё не загрузился» отличается от «пуст» без доп. флага
  // и без setState в теле эффекта.
  const [userLayerState, setUserLayerState] =
    useState<{ userId: string; layer: SpecialtySettingsLayer } | null>(null);
  const [userLayerError, setUserLayerError] = useState<string | null>(null);
  const loadUserLayer = useCallback((id: string) => {
    api.specialties.getUserLayer(id)
      .then(r => { setUserLayerState({ userId: id, layer: r.user }); setUserLayerError(null); })
      .catch(e => setUserLayerError(e instanceof Error ? e.message : 'Не удалось загрузить слой пользователя'));
  }, []);
  useEffect(() => {
    if (activeScope !== 'user' || !contextUserId) return;
    loadUserLayer(contextUserId);
  }, [activeScope, contextUserId, loadUserLayer]);
  const userLayer = userLayerState?.userId === contextUserId ? userLayerState.layer : null;

  // Персоны владельца — только ради подписи «Любой специальности»: сколько персон идут
  // без своей специальности. В чужом и общем слое счёт не показываем: он был бы про
  // персон СМОТРЯЩЕГО, а карточка — про чужие настройки.
  const [personaStats, setPersonaStats] = useState<{ noSpec: number; total: number } | null>(null);
  useEffect(() => {
    let cancelled = false;
    api.personas.list()
      .then((list: Persona[]) => {
        if (cancelled) return;
        const noSpec = list.filter(p => !p.specialty || p.specialty === 'none').length;
        setPersonaStats({ noSpec, total: list.length });
      })
      .catch(() => { /* персон нет или фича выключена — карточка обойдётся без счёта */ });
    return () => { cancelled = true; };
  }, []);

  // Раскрытая карточка (не больше одной: внутри панели выбора живёт несохранённый
  // черновик цепочки, и схлопывать её мимоходом нельзя), выделенные из групп роли,
  // выбранный сегмент полосы и открытый мастер.
  const [expanded, setExpanded] = useState<string | null>(null);
  const [splitKeys, setSplitKeys] = useState<Set<string>>(new Set());
  const [selected, setSelected] = useState<{ tier: TierKey; route: string } | null>(null);
  const [wizardOpen, setWizardOpen] = useState(false);
  const [opError, setOpError] = useState<string | null>(null);

  // Массовый сброс слоя: preview грузится ДО открытия диалога — числа в тексте настоящие
  const [bulkPreview, setBulkPreview] = useState<ResetResult | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [confirmBusy, setConfirmBusy] = useState(false);

  const layer: SpecialtySettingsLayer | null = settings
    ? (activeScope === 'global' ? settings.global
      : activeScope === 'owner' ? settings.owner
        : userLayer)
    : null;

  const canEdit = activeScope === 'owner' || (isAdmin && (activeScope === 'global' || !!contextUserId));
  const busy = savingScope === activeScope || resettingScope === activeScope;

  const roles = useMemo(() => catalogRoles(catalog), [catalog]);
  const allRows = useMemo(() => allRoleRows(catalog, layer), [catalog, layer]);
  const rows = useMemo(() => configuredRoleRows(allRows), [allRows]);
  const { groups, singles } = useMemo(() => buildGroups(rows, splitKeys), [rows, splitKeys]);
  const bars = useMemo(() => buildLevelBars(allRows), [allRows]);
  const anyTriple = useMemo(() => tripleOf(layer?.defaultSpecialty), [layer]);

  const filled = countFilledFields(layer, catalog);
  const ownerFilled = countFilledFields(settings?.owner ?? null, catalog);
  const total = totalFields(catalog);

  // Панель «Инструкции для роли» (фича specialty-prompt-sections): рендерим под блоком
  // матриц/карточек, перед подвалом. Флаг по умолчанию выключен — реестр покажет тумблер.
  const promptSectionsEnabled = useFeature(FLAGS.specialtyPromptSections);

  // Смена слоя/пользователя сбрасывает состояния вида: раскрытая карточка показала бы
  // значения другого слоя, а выделенные роли относятся к прежнему набору записей.
  const switchScope = (next: Scope) => {
    if (next === activeScope) return;
    setScope(next);
    setExpanded(null);
    setSelected(null);
    setSplitKeys(new Set());
    setWizardOpen(false);
    setOpError(null);
    if (next === 'user' && !contextUserId && isAdmin) {
      const first = data.users.find(u => u.id !== meUserId) ?? data.users[0];
      if (first) onContextUserId(first.id);
    }
  };

  // === Запись слоя ===
  // Чужой слой правится оптимистично в локальном состоянии: он живёт здесь, а не
  // в settings модалки (там лежит user-слой самого админа).
  const saveLayer = (next: SpecialtySettingsLayer) => {
    if (activeScope === 'user') {
      if (!contextUserId) return;
      setUserLayerState({ userId: contextUserId, layer: next });
      void onSaveLayer('user', next).catch(() => loadUserLayer(contextUserId));
      return;
    }
    // catch пустой намеренно: отказ уже показан баннером модалки, здесь он нужен только
    // чтобы отклонённый промис не всплыл как «Uncaught (in promise)»
    void onSaveLayer(activeScope, next).catch(() => {});
  };

  const templateOf = (key: string) => roles.find(e => e.key === key)?.template ?? null;

  // Правка полей ОДНИМ PUT: у группы значение уходит сразу всем её ролям — раздельные
  // запросы по одному слою гонятся, и последний ответ стёр бы предыдущие (класс 65d8df66).
  // base — слой, поверх которого писать: обычно текущий, но при inline-сборке цепочки
  // сюда приходит свежий клон с уже добавленным пресетом (см. applyCreatedPreset).
  const setCells = (keys: string[], tier: TierKey, value: string, base?: SpecialtySettingsLayer) => {
    const from = base ?? layer;
    if (!from) return;
    let next = from;
    for (const key of keys) next = withTierCell(next, key, tier, value, templateOf(key));
    saveLayer(next);
  };

  const setCell = (key: string, tier: TierKey, value: string) => setCells([key], tier, value);

  // Возврат наследования по ОДНОЙ роли — серверный (POST .../reset/{scope}?key=):
  // запись слоя без полей продолжала бы перекрывать нижний слой, поэтому «вернуть»
  // означает удалить запись, а не обнулить её поля.
  const resetRoles = async (keys: string[]) => {
    setOpError(null);
    try {
      let shadowed: string[] = [];
      for (const key of keys) {
        const res = await onReset(activeScope, key);
        shadowed = res.shadowed;
      }
      showToast('Особые правила', shadowed.some(k => keys.includes(k))
        ? 'Поля сняли, но у специальности остались свои права — она по-прежнему перекрывает нижний слой.'
        : 'Вернули к наследованию');
      if (activeScope === 'user' && contextUserId) loadUserLayer(contextUserId);
      setExpanded(null);
    } catch (e) {
      setOpError(e instanceof Error ? e.message : 'Не удалось вернуть наследование');
    }
  };

  // ✕ у поля: обычно это просто очистка ячейки, но если поле было последним заданным
  // у роли — очистка оставила бы пустую запись-затенение, поэтому уходим в серверный
  // сброс всей роли.
  const clearCell = (key: string, tier: TierKey) => {
    if (!layer) return;
    const triple = key === ANY_SPECIALTY ? tripleOf(layer.defaultSpecialty) : tripleOf(layer.specialties[key]);
    const idx = TIER_ORDER.indexOf(tier);
    const rest = triple.filter((v, i) => i !== idx && v);
    if (rest.length === 0) { void resetRoles([key]); return; }
    setCell(key, tier, '');
  };

  const clearGroupCell = (group: { roles: RoleRow[]; triple: [string, string, string] }, tier: TierKey) => {
    const idx = TIER_ORDER.indexOf(tier);
    const rest = group.triple.filter((v, i) => i !== idx && v);
    if (rest.length === 0) { void resetRoles(group.roles.map(r => r.key)); return; }
    setCells(group.roles.map(r => r.key), tier, '');
  };

  // Inline-сборка цепочки прямо в поле: PresetOptions отдаёт СВЕЖИЙ слой (клон + новая
  // цепочка, ещё не сохранён). Если цепочка легла в ТОТ ЖЕ слой, что правим, — дописываем
  // значение на том же клоне и шлём один PUT (раздельные PUT по одному слою гонятся,
  // и второй ответ стёр бы только что созданную цепочку). Если в другой (общая цепочка
  // для чужого слоя) — сначала пишем слой с цепочкой и только после подтверждения
  // назначаем поле: бэкенд проверяет preset:{id} по снимку, и обгон дал бы 400.
  const applyCreatedPreset = (keys: string[], tier: TierKey,
    presetId: string, presetScope: Scope, freshLayer: SpecialtySettingsLayer) => {
    const route = presetRoute(presetId);
    if (presetScope === activeScope) { setCells(keys, tier, route, freshLayer); return; }
    void onSaveLayer(presetScope, freshLayer).then(() => setCells(keys, tier, route)).catch(() => {});
  };

  // === Подсветка по сегменту полосы ===
  const cardRefs = useRef<Record<string, HTMLDivElement | null>>({});
  const matches = (triple: [string, string, string]): boolean =>
    !!selected && triple[TIER_ORDER.indexOf(selected.tier)] === selected.route;
  const firstMatchId = useMemo(() => {
    if (!selected) return null;
    const i = TIER_ORDER.indexOf(selected.tier);
    if (anyTriple[i] === selected.route) return 'any';
    const g = groups.find(x => x.triple[i] === selected.route);
    if (g) return `g:${g.id}`;
    const s = singles.find(x => x.triple[i] === selected.route);
    return s ? `s:${s.key}` : null;
  }, [selected, groups, singles, anyTriple]);
  useEffect(() => {
    if (!firstMatchId) return;
    cardRefs.current[firstMatchId]?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }, [firstMatchId]);

  // === Массовый сброс слоя ===
  const openBulkReset = async () => {
    if (filled === 0 || previewLoading || busy) return;
    setOpError(null);
    setPreviewLoading(true);
    try {
      setBulkPreview(await api.specialties.resetPreview(activeScope, undefined, contextUserId ?? undefined));
    } catch (e) {
      setOpError(e instanceof Error ? e.message : 'Не удалось получить предпросмотр сброса');
    } finally {
      setPreviewLoading(false);
    }
  };

  const confirmBulkReset = async () => {
    setConfirmBusy(true);
    try {
      const res = await onReset(activeScope);
      showToast('Особые правила',
        `Вернули к наследованию: ${rolesWord(res.specialties)}`);
      setBulkPreview(null);
      setExpanded(null);
      setSplitKeys(new Set());
      if (activeScope === 'user' && contextUserId) loadUserLayer(contextUserId);
    } catch (e) {
      setOpError(e instanceof Error ? e.message : 'Не удалось сбросить особые правила');
      setBulkPreview(null);
    } finally {
      setConfirmBusy(false);
    }
  };

  const ctx: PickerCtx = {
    models, tierModels, ollamaModel, settings, savingScope, onSaveLayer,
    // Личный слой берёт цепочки обоих слоёв и создаёт свои; общий и ЧУЖОЙ слой —
    // только общие: личная цепочка была бы битой ссылкой у всех, кроме автора
    presetScope: activeScope === 'owner' ? undefined : 'global',
    busy, readOnly: !canEdit,
  };

  // Подпись «Любой специальности» под слой
  const anyHint = activeScope === 'user'
    ? 'у пользователя не задана — наследует его собственный слой'
    : activeScope === 'global'
      ? 'общая для всех пользователей'
      : personaStats
        ? `персоны без своей специальности — таких ${personaStats.noSpec} из ${personaStats.total}`
        : 'персоны без своей специальности';

  const selectedUser = data.users.find(u => u.id === contextUserId) ?? null;

  return (
    <div style={{ paddingBottom: SP.xl }}>
      {/* Подзаголовок вкладки */}
      <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5, margin: '0 2px 10px' }}>
        Правила для специальностей; их наследуют поля персоны этой специальности.
      </div>

      {/* Переключатель слоёв */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', margin: '2px 2px 12px',
      }}>
        {/* На мобиле три подписи в строку не влезают — сегмент переносится на вторую
            строку вместо горизонтального скролла (его на вкладке нет нигде) */}
        <div style={{
          display: 'flex', gap: 2, background: C.bgSelected, borderRadius: R.pill, padding: 2,
          width: isMobile ? '100%' : undefined, flexWrap: isMobile ? 'wrap' : undefined,
        }}>
          {isAdmin && (
            <SegBtn active={activeScope === 'global'} grow={isMobile} onClick={() => switchScope('global')}>
              Для всех
            </SegBtn>
          )}
          <SegBtn active={activeScope === 'owner'} grow={isMobile} onClick={() => switchScope('owner')}>
            Только для меня
          </SegBtn>
          {isAdmin && (
            <SegBtn active={activeScope === 'user'} grow={isMobile} onClick={() => switchScope('user')}>
              Пользователю…
            </SegBtn>
          )}
        </div>
        {isAdmin && (
          <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
            Слой определяет, кого коснётся правило. Третий вариант видит только администратор.
          </span>
        )}
      </div>

      {/* Выбор пользователя для чужого слоя */}
      {activeScope === 'user' && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', margin: '0 2px 12px',
        }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>Пользователь:</span>
          <select
            value={contextUserId ?? ''}
            onChange={e => { onContextUserId(e.target.value || null); setExpanded(null); setSplitKeys(new Set()); }}
            style={{
              font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, padding: '5px 9px',
              borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite,
              color: C.textHeading, maxWidth: '100%',
            }}
          >
            <option value="">— выберите —</option>
            {data.users.map(u => (
              <option key={u.id} value={u.id}>{u.displayName || u.username}</option>
            ))}
          </select>
          {selectedUser && layer && (
            <span style={{
              fontSize: FS.xs, fontWeight: 700, padding: '2px 8px', borderRadius: R.max,
              background: C.bgSelected, color: C.textSecondary,
            }}>
              задано {fieldsWord(filled)} из {total}
            </span>
          )}
          <div style={{ flexBasis: '100%', fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
            Строку «Сейчас пойдёт» здесь не показать: резолв считается за того, кто смотрит,
            и за другого пользователя соврал бы.
          </div>
        </div>
      )}

      {opError && (
        <div style={{ fontSize: FS.xs, color: C.dangerText, padding: `0 2px ${SP.sm}px` }}>{opError}</div>
      )}
      {userLayerError && activeScope === 'user' && (
        <div style={{ fontSize: FS.xs, color: C.dangerText, padding: `0 2px ${SP.sm}px` }}>{userLayerError}</div>
      )}

      {/* Загрузка: каталог и настройки ещё едут */}
      {(!catalog || !settings || (activeScope === 'user' && contextUserId && !layer && !userLayerError)) ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {[0, 1, 2].map(i => (
            <div key={i} style={{ height: 56, borderRadius: R.xl, background: C.bgSelected }} />
          ))}
        </div>
      ) : activeScope === 'user' && !contextUserId ? (
        <EmptyBox title="Выберите пользователя">
          Правило этого слоя коснётся только выбранного пользователя — и только там, где он
          не задал своё.
        </EmptyBox>
      ) : (
        <>
          <AnySpecialtyCard
            triple={anyTriple}
            hint={anyHint}
            scope={activeScope}
            ctx={ctx}
            highlight={matches(anyTriple)}
            innerRef={el => { cardRefs.current.any = el; }}
            onCell={(t, v) => setCell(ANY_SPECIALTY, t, v)}
            onClear={t => clearCell(ANY_SPECIALTY, t)}
            onPresetCreated={(t, id, s, l) => applyCreatedPreset([ANY_SPECIALTY], t, id, s, l)}
          />

          {rows.length > 0 && (
            <LevelsPicture
              bars={bars}
              selected={selected}
              onSelect={setSelected}
              tierModels={tierModels}
              ollamaModel={ollamaModel}
              subtitle={`${rolesWord(roles.length)}. Клик по сегменту подсвечивает его карточки ниже.`}
            />
          )}

          {rows.length === 0 && (
            <EmptyBox title={activeScope === 'global' ? 'Общих правил пока нет' : 'Особых правил пока нет'}>
              {activeScope === 'global' ? (
                <>
                  Правило на этом слое сработает для всех пользователей, кроме тех, кто задал своё.
                  {ownerFilled > 0 && (
                    <>
                      {' '}У вас есть личные правила ({fieldsWord(ownerFilled)}) — они на слое{' '}
                      <LinkBtn onClick={() => switchScope('owner')}>«Только для меня»</LinkBtn>.
                    </>
                  )}
                </>
              ) : activeScope === 'user' ? (
                <>Пользователь идёт по вашим настройкам и по общему слою. Правило здесь перекроет
                  общий слой только для него.</>
              ) : (
                <>Все специальности идут за «Любой специальностью», а она — за «Моделями по
                  умолчанию». Правило нужно там, где одной роли требуется своя модель.</>
              )}
            </EmptyBox>
          )}

          {groups.length > 0 && (
            <>
              <SectionTitle>
                Одинаковые наборы · {rolesWord(groups.reduce((n, g) => n + g.roles.length, 0))} в
                {' '}{groupsWord(groups.length)}
              </SectionTitle>
              {groups.map(g => (
                <RuleGroupCard
                  key={g.id}
                  group={g}
                  open={expanded === `g:${g.id}`}
                  onToggle={() => setExpanded(k => (k === `g:${g.id}` ? null : `g:${g.id}`))}
                  scope={activeScope}
                  ctx={ctx}
                  highlight={matches(g.triple)}
                  innerRef={el => { cardRefs.current[`g:${g.id}`] = el; }}
                  matchesAny={sameTriple(g.triple, anyTriple)}
                  onCell={(t, v) => setCells(g.roles.map(r => r.key), t, v)}
                  onClear={t => clearGroupCell(g, t)}
                  onPresetCreated={(t, id, s, l) =>
                    applyCreatedPreset(g.roles.map(r => r.key), t, id, s, l)}
                  onSplit={key => {
                    setSplitKeys(prev => new Set(prev).add(key));
                    setExpanded(`s:${key}`);
                  }}
                />
              ))}
            </>
          )}

          {singles.length > 0 && (
            <>
              <SectionTitle>Отдельные наборы · {rolesWord(singles.length)}</SectionTitle>
              {singles.map(r => (
                <RuleSpecCard
                  key={r.key}
                  role={r}
                  open={expanded === `s:${r.key}`}
                  onToggle={() => setExpanded(k => (k === `s:${r.key}` ? null : `s:${r.key}`))}
                  scope={activeScope}
                  ctx={ctx}
                  highlight={matches(r.triple)}
                  innerRef={el => { cardRefs.current[`s:${r.key}`] = el; }}
                  onCell={(t, v) => setCell(r.key, t, v)}
                  onClear={t => clearCell(r.key, t)}
                  onResetRole={() => void resetRoles([r.key])}
                  onPresetCreated={(t, id, s, l) => applyCreatedPreset([r.key], t, id, s, l)}
                />
              ))}
            </>
          )}

          {/* Подвал: добавление правила, сброс слоя и порядок наследования */}

          {/* Панель «Инструкции для роли» (план «Секции промптов», этап 4) — за флагом */}
          {promptSectionsEnabled && (
            <SpecialtyPromptSectionsPanel
              isMobile={isMobile}
              activeScope={activeScope}
              globalLayer={settings?.global ?? null}
              editLayer={layer}
              userLayer={userLayer}
              contextUserId={contextUserId}
              catalog={catalog}
              saving={busy}
              canEdit={canEdit}
              onSaveLayer={async (next) => { saveLayer(next); }}
            />
          )}

          {canEdit && (
            <div style={{
              display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.md,
            }}>
              <Button size="sm" variant="ghost" disabled={busy || wizardOpen}
                onClick={() => setWizardOpen(true)}>
                ＋ {activeScope === 'global' && rows.length === 0 ? 'Добавить общее правило' : 'Добавить правило'}
              </Button>
              <span style={{ flex: 1 }} />
              {filled > 0 && (
                <Button size="sm" variant="ghost" loading={previewLoading}
                  disabled={previewLoading || busy} onClick={openBulkReset}>
                  {isMobile ? 'Сбросить все' : 'Сбросить все правила слоя'}
                </Button>
              )}
            </div>
          )}

          {wizardOpen && canEdit && (
            <AddRuleWizard
              roles={allRows}
              scope={activeScope}
              ctx={ctx}
              onCancel={() => setWizardOpen(false)}
              onSave={(key, tier, route) => {
                setCell(key, tier, route);
                setWizardOpen(false);
                setSelected(null);
                setExpanded(`s:${key}`);
              }}
            />
          )}

          <div style={{
            fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5, marginTop: SP.md, padding: '0 2px',
          }}>
            Поле персоны сильнее правила специальности; специальность без правила наследует:
            «Любая специальность» → «Модели по умолчанию».
          </div>
        </>
      )}

      <ResetConfirmDialog
        open={bulkPreview !== null}
        title={activeScope === 'global' ? 'Сбросить общие правила?'
          : activeScope === 'user' ? 'Сбросить правила пользователя?'
            : 'Сбросить свои особые правила?'}
        body={bulkPreview
          ? `Свои модели потеряют ${rolesWord(bulkPreview.specialties)}${bulkPreview.personas > 0
            ? ` и ${bulkPreview.personas} персон` : ''}. Все они снова пойдут за «Любой специальностью».`
          : ''}
        confirmLabel="Сбросить"
        busy={confirmBusy}
        onCancel={() => setBulkPreview(null)}
        onConfirm={confirmBulkReset}
      />
    </div>
  );
}

function SegBtn({ active, grow, onClick, children }: {
  active: boolean; grow?: boolean; onClick: () => void; children: React.ReactNode;
}) {
  return (
    <button type="button" onClick={onClick} style={{
      font: 'inherit', fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, cursor: 'pointer',
      border: 'none', borderRadius: R.md, padding: '5px 11px',
      flex: grow ? '1 1 auto' : undefined, minWidth: 0,
      background: active ? C.bgWhite : 'transparent',
      color: active ? C.textHeading : C.textSecondary,
      boxShadow: active ? 'var(--shadow-card)' : 'none', whiteSpace: 'nowrap',
    }}>{children}</button>
  );
}

function EmptyBox({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={{
      border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: '22px 18px',
      textAlign: 'center', color: C.textSecondary, fontSize: FS.sm, lineHeight: 1.55,
    }}>
      <div style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: 4 }}>
        {title}
      </div>
      {children}
    </div>
  );
}

function LinkBtn({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" onClick={onClick} style={{
      font: 'inherit', fontFamily: FONT.sans, fontSize: 'inherit', fontWeight: 600,
      color: C.accent, background: 'none', border: 'none', padding: 0, cursor: 'pointer',
      textDecoration: 'underline', textUnderlineOffset: 2,
    }}>{children}</button>
  );
}
