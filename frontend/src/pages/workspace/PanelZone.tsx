// Зона панелей — рельса иконок у края окна плюс открытые панели-карточки.
//
// ОДИН компонент на обе стороны. Раньше это были LeftPanelStack и
// RightPanelStack — два почти дословных файла, которые расходились при каждой
// правке геометрии (левая долго жила без колонок, правая — с иной формулой
// ширины и своей подсказкой режима). Различия сторон теперь параметры:
//
//   side       — геометрия: рельса слева и рост панелей вправо, либо зеркально;
//   compact    — планшет/телефон: одна-две панели стеком, drawer поверх на узком;
//   sessionOnly— экран без проекта: доступны только чаты и панели сессии;
//   session*   — контент, видимость и бейджи панелей текущей сессии (useSessionPanels).
//
// Раскладка — ЯВНЫЕ колонки: дефолт «по две на колонку» в порядке открытия,
// drag-and-drop за шапку даёт любое распределение, В ТОМ ЧИСЛЕ перенос панели
// в соседнюю зону (состояние перетаскивания общее — panelDrag).
//
// Панели — «воздушные» скруглённые карточки с зазорами; границы высот тянутся
// невидимыми хендлами в зазорах, ширина колонок — сплиттером со стороны центра.
//
// ВЫСОТА (panelStretched): пустой низ под панелью терпим только у ОДИНОЧНОЙ панели
// в колонке у центра — короткий список чатов не растягивается на весь экран. Как
// только в колонке 2+ панели (в любом ряду) — они тянутся до нижней кромки и делят
// высоту по весам, там же живёт хендл ресайза. Колонки разной длины посреди зоны
// читались бы как рваная раскладка, поэтому одиночную «по контенту» держим лишь у
// самого центра. Ширину колонок делит сплиттер между ними (colFlex).
import { Fragment, useEffect, useRef, useState, type DragEvent, type ReactNode } from 'react';
import { Pin } from 'lucide-react';
import { C, ISLAND, SHADOW, PANEL_ANIM, Z } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { PanelShell } from '../../components/ui/PanelShell';
import { PanelRail, RAIL_W, RAIL_GAP, type RailItem } from '../../components/ui/PanelRail';
import { PanelDropGuide, PanelDropLine, SEP_HIT, sepShift } from '../../components/ui/PanelDropGuide';
import { IslandSplitter } from '../../components/ui/IslandSplitter';
import { useWindowWidth } from '../../lib/breakpoints';
import {
  PANEL_META, PANEL_KEYS, CENTER_KEYS, PROJECT_KEYS, SESSION_KEYS, TOOLS_KEYS, WORKSPACE_KEYS,
  isPanelKey, type PanelKey, type Zone,
} from './panelCatalog';
import { PanelFillContext, usePanelFillRequests } from './panelFill';
import { wsPanels, homeOf, isTucked, isZoneCollapsed, placeByRail, sortRail, zoneOf, COL_CAP, PANEL_MIN_H, type PanelZonesStore } from './panelStackState';
import { usePanelColResize, usePanelDnd, usePanelRowResize, usePanelWidthDrag } from './zoneGestures';
import { usePanelPeek } from './panelPeek';
import { useRailHover } from './railHover';
import { usePanelHeights } from './panelHeights';
import { PanelSlot } from './PanelSlot';
import type { SessionPanels } from './useSessionPanels';

const GAP = ISLAND.gap; // зазор между карточками — та самая «воздушность»

// Порог планшета: шире — панель в потоке рядом с чатом, уже — drawer поверх
const TABLET_INLINE_MIN = 1000;

// Ширина полосы-приёмника, которую пустая зона показывает на время перетаскивания.
// Здесь раскладка намеренно «дышит»: у пустой зоны нет ни одной панели, рядом с
// которой можно было бы встать невидимым оверлеем, а целиться в невидимую кромку
// окна — мучение. Полоса живёт только пока панель тащат.
const EMPTY_DROP_W = 28;

// Группы кнопок рельсы сверху вниз: содержимое ПРОЕКТА, инструменты запуска
// (Терминал, Сервисы) и всё, что относится к текущему контексту — панели сессии
// (План, Агенты, Персона) и центральной области (Оглавление). Последние две
// категории разделены в реестре (у них разные источники видимости), но в рельсе
// идут ОДНОЙ группой: это соседи по смыслу — «что сейчас перед глазами», и черта
// между ними делила бы рельсу там, где деления нет.
//
// Группа — ещё и предел перестановки кнопок: порядок внутри неё пользовательский,
// а между группами — нет (разделители отделяют разные по смыслу наборы). Отсюда
// общий список: и рельса, и запись порядка обязаны видеть одни и те же границы.
const RAIL_GROUPS: readonly (readonly PanelKey[])[] = [
  PROJECT_KEYS,
  TOOLS_KEYS,
  [...SESSION_KEYS, ...CENTER_KEYS],
];

// Группа, внутри которой кнопку разрешено переставлять (null — ключ не из рельсы)
const railGroupOf = (k: PanelKey): readonly PanelKey[] | null => RAIL_GROUPS.find(g => g.includes(k)) ?? null;

// Попап-превью панели по наведению на иконку рельсы временно выключен: механика
// готова (panelPeek + peek в PanelRail), но пока живём без неё. Флаг — чтобы
// вернуть одним значением, а не восстанавливать вырезанный код.
const PEEK_ENABLED = false;

interface Props {
  // Сторона окна, к которой прижата зона
  side: Zone;
  // Готовый контент ВСЕХ панелей экрана — обе зоны получают один и тот же набор
  // и рисуют из него то, что лежит именно в них
  panels: Partial<Record<PanelKey, ReactNode>>;
  // Числа-кружки на иконках рельсы (changes/tasks/terminal/preview/chats).
  // Сессионные свои числа берут из sessionPanels.
  railCounts?: Partial<Record<PanelKey, number>>;
  // Инстанс стора зон: каждый экран держит НЕЗАВИСИМУЮ раскладку
  panelStack?: PanelZonesStore;
  // Какие панели вообще доступны на этом экране. У воркспейса это инструменты
  // проекта и сессии, у раздела хаба — его собственные. Раньше вместо набора был
  // флаг sessionOnly, и добавить экран с другим составом было нечем.
  allowedKeys?: readonly PanelKey[];
  // Рельсу целиком прячем, когда показывать нечего (у чата без артефактов иначе
  // торчала бы пустая полоса)
  hideWhenEmpty?: boolean;
  // Терминал и Сервисы доступны только при включённых инструментах проекта
  toolsEnabled?: boolean;
  // Планшет/телефон: одна-две панели, drawer поверх на узком экране, без DnD и колонок
  compact?: boolean;
  // Панели текущей сессии (План/Агенты/Персона) — контент, видимость, бейджи
  sessionPanels?: SessionPanels;
  // Хук на ЯВНУЮ активацию панели кликом по иконке рельсы (панель открылась).
  // Только клик: восстановление раскладки из localStorage его не дёргает.
  onPanelOpen?: (k: PanelKey) => void;
  // Второй остров ПОД рельсой зоны — сейчас это док проектов воркспейса. К раскладке
  // панелей он отношения не имеет, но живёт в той же вертикали у края окна, поэтому
  // держит зону на экране даже когда открывать в ней нечего.
  railFooter?: ReactNode;
  // Плавающий режим («Стена»): открытые панели не занимают место в раскладке, а
  // всплывают поверх контента у своей рельсы и закрываются кликом мимо них. Кнопки,
  // раскладка и перенос между зонами — те же, что в обычном режиме (общий стор).
  floating?: boolean;
}

export function PanelZone({
  side, panels, railCounts, panelStack,
  allowedKeys = WORKSPACE_KEYS, hideWhenEmpty, toolsEnabled, compact, sessionPanels, onPanelOpen,
  railFooter, floating,
}: Props) {
  const usePanels = (panelStack ?? wsPanels).use;
  const { zones, toggle, closeTo, tuck, untuck, reorder, evict, setMode, setWidth, setWeights, setColFlex, toggleCollapsed, swapWith, replaceWith, moveAt, moveToNewColumn } = usePanels();
  const zoneState = zones[side];
  const { layout, mode, width, colFlex } = zoneState;
  const windowWidth = useWindowWidth();
  const isLeft = side === 'left';

  const { panelRefs, rowDragging, handleRowDrag } = usePanelRowResize<PanelKey>(zones.weights, setWeights);
  // Ресайз ширины между колонками: доли colFlex перетягиваются внутри пары
  const { colRefs, colDragging, handleColDrag } = usePanelColResize(colFlex, next => setColFlex(side, next));
  // Высоты панелей, стоящих по контенту: по их сумме укорачивается сплиттер ширины,
  // иначе его grip висит в пустоте под короткой колонкой.
  const [panelHeightRef, panelH, panelHeightNow] = usePanelHeights<PanelKey>();
  // Панели, которые САМИ просят всю высоту колонки (нижняя зона превью у «Документации»).
  // Требование приходит из панели через контекст — см. panelFill.ts
  const [fillWanted, fillSinkFor] = usePanelFillRequests<PanelKey>();

  // Компактный режим: до ДВУХ панелей стеком; выбор локальный эфемерный —
  // раскладка зоны не трогается. Третья открытая вытесняет самую старую (FIFO).
  const [tabletPanels, setTabletPanels] = useState<PanelKey[]>([]);
  const tabletInline = windowWidth >= TABLET_INLINE_MIN;

  // Панель доступна на этом экране: есть контент (у сессионных он всегда есть),
  // экран не sessionOnly либо ключ из разрешённых там, инструменты включены.
  const keyAvailable = (k: PanelKey): boolean => {
    if (!allowedKeys.includes(k)) return false;
    if (TOOLS_KEYS.includes(k) && !toolsEnabled) return false;
    return content(k) != null;
  };

  // Контент панели: сессионные собирает useSessionPanels, остальные приходят пропом
  function content(k: PanelKey): ReactNode {
    if (SESSION_KEYS.includes(k)) return sessionPanels?.content[k] ?? null;
    return panels[k] ?? null;
  }

  const soloMode = mode === 'solo';

  // Видимые колонки. Вместе с ключами колонка несёт свой ИСХОДНЫЙ индекс в
  // layout: недоступные панели отсеиваются, поэтому видимые координаты
  // (колонка, строка) не совпадают с настоящими, а moveAt/moveToNewColumn
  // работают именно с настоящими.
  const columns = compact ? [] : layout
    .map((col, ci) => ({ ci, keys: col.filter(keyAvailable) }))
    .filter(c => c.keys.length > 0);
  const tabletKeys = compact ? tabletPanels.filter(keyAvailable) : [];
  const openKeys = compact ? tabletKeys : columns.flatMap(c => c.keys);

  // Колонка, ближайшая к ЦЕНТРУ экрана: у левой зоны это последняя (панели растут
  // от рельсы вправо), у правой — первая (порядок зеркальный).
  const centerVi = isLeft ? columns.length - 1 : 0;
  // Растягивается ли панель на всю высоту слота. Пустой низ (высота по контенту)
  // терпим ТОЛЬКО у ОДИНОЧНОЙ панели в колонке у центра: короткий список чатов не
  // должен растягиваться на весь экран. Как только в колонке у центра 2+ панели —
  // они тянутся до низа и делят высоту по весам (обычный ресайз), иначе колонка
  // рваная. Во втором и дальних рядах панели тянутся всегда; панель, попросившая
  // высоту сама (fillWanted — напр. «Документация» с включённым превью снизу),
  // тянется даже одиночкой у центра. colLen — число панелей в колонке.
  // floating: панели всплывают поверх контента, и «высота по содержимому» там
  // означала бы попап, который прыгает в размере от панели к панели. Слой всегда
  // одной высоты — открытие следующей панели не дёргает картинку.
  const panelStretched = (k: PanelKey, vi: number, colLen: number): boolean =>
    !!floating || !!fillWanted[k] || vi !== centerVi || colLen > 1;
  // Колонка стоит по контенту целиком — под ней свободный низ (место для новой
  // панели, растяжимая направляющая, укороченный сплиттер ширины).
  const colByContent = (keys: PanelKey[], vi: number): boolean =>
    !keys.some(k => panelStretched(k, vi, keys.length));

  // Панель встаёт в колонку так, чтобы УЖЕ ОТКРЫТЫЕ не меняли размер:
  //  • есть свободный низ (колонка стоит по контенту) — новая занимает ровно его;
  //  • низа нет — половину отдаёт ОДИН сосед по месту вставки, прочие не шевелятся.
  // Без этого все панели колонки становились равновесными (вес 1 у каждой) и делили
  // высоту поровну: открытие одной панели дёргало всю колонку, хотя человек нажал
  // одну кнопку.
  //
  // Веса считаются ПО ПИКСЕЛЯМ (живой DOM), поэтому дальше всё работает как обычно:
  // ресайз границы, перестановки, закрытие. null — по месту не вышло (замеров нет
  // или соседу нечем делиться): такую панель зона уводит в новую колонку, а если
  // выбора нет (дроп в конкретное место) — колонка делится по весам, как раньше.
  // Живая высота панели колонки: у растянутой её знает слот (ресайз высот держит
  // на него ссылку), у стоящей по контенту — замер panelHeights. Именно ЖИВАЯ, а не
  // из состояния panelH: то обновляется наблюдателем и на момент клика отстаёт от
  // экрана — раскладка поехала бы по протухшим числам.
  const panelHeightIn = (k: PanelKey): number | null =>
    panelRefs.current[k]?.offsetHeight ?? panelHeightNow(k);

  const insertPlan = (ci: number, skip: PanelKey | null, at?: number): { keys: PanelKey[]; own: number[]; mine: number } | null => {
    const vi = columns.findIndex(c => c.ci === ci);
    if (vi < 0) return null;
    // Панель может ехать из этой же колонки — её саму в расчёт не берём
    const keys = columns[vi].keys.filter(x => x !== skip);
    if (keys.length === 0) return null;
    const colH = colRefs.current[ci]?.getBoundingClientRect().height ?? 0;
    if (colH <= 0) return null;
    const heights: number[] = [];
    for (const key of keys) {
      const h = panelHeightIn(key);
      if (h == null || h <= 0) return null; // панели ещё нет в DOM
      heights.push(h);
    }
    // Свободный низ колонки: зазор перед новой панелью тоже отсюда
    const free = colH - heights.reduce((a, b) => a + b, 0) - GAP * keys.length;
    if (free >= PANEL_MIN_H) return { keys, own: heights, mine: free };
    // Пустоты нет — половину отдаёт сосед по месту вставки (новый зазор тоже с него)
    const di = Math.min(keys.length - 1, Math.max(0, (at ?? keys.length) - 1));
    const shared = heights[di] - GAP;
    if (shared < PANEL_MIN_H * 2) return null; // делить нечего
    const own = [...heights];
    own[di] = shared / 2;
    return { keys, own, mine: shared / 2 };
  };

  // Записать план весами. Пиксели переводим в ОБЫЧНЫЙ масштаб (среднее 1 на панель):
  // словарь весов общий на обе зоны, и сырые пиксели придавили бы панели соседней
  // зоны до нуля — там веса единичные.
  const keepHeightsOnInsert = (k: PanelKey, ci: number, at?: number) => {
    const plan = insertPlan(ci, k, at);
    if (!plan) return;
    const total = plan.mine + plan.own.reduce((a, b) => a + b, 0);
    const scale = (plan.keys.length + 1) / total;
    const next: Partial<Record<PanelKey, number>> = {};
    next[k] = plan.mine * scale;
    plan.keys.forEach((key, i) => { next[key] = plan.own[i] * scale; });
    setWeights(next);
  };

  // Вместимость колонки у рельсы для правила размещения (см. addPanel): панель идёт
  // ВНИЗ, пока туда есть куда встать не двигая соседей — то есть пока insertPlan
  // что-то возвращает. Не влезла — заводит колонку сбоку. Раньше вместимость была
  // зашита числом («по две на колонку»), и третья панель уезжала вбок при пустом
  // экране, а на тесной колонке новая, наоборот, ужимала всех соседей.
  const colCapNow = (): number => {
    const railCi = isLeft ? 0 : layout.length - 1;
    const len = layout[railCi]?.length ?? 0;
    if (len === 0) return COL_CAP;
    return insertPlan(railCi, null) ? len + 1 : len;
  };

  // Иконка панели живёт в ТОЙ зоне, где панель лежит; закрытая — в домашней.
  // Отсюда «иконка едет вместе с панелью», а закрытие возвращает её домой.
  //
  // withTucked=false — не учитывать ящик рельсы: по такому счёту решается, есть ли
  // в зоне чем управлять (иначе, спрятав всё кроме одной кнопки, человек потерял бы
  // и «Свернуть все», и сам ящик).
  const railKeyVisible = (k: PanelKey, withTucked = true): boolean => {
    if (!keyAvailable(k)) return false;
    // Кнопка убрана в ящик — в столбце её нет. ОТКРЫТАЯ панель исключение: её
    // кнопка возвращается в рельсу, пока панель на экране, иначе закрыть панель
    // привычным кликом было бы нечем. В компактном режиме ящика нет вовсе, и
    // спрятанная кнопка иначе стала бы недоступной.
    if (withTucked && !compact && isTucked(zones, k) && zoneOf(zones, k) === null) return false;
    // Где панель сейчас лежит (null — закрыта). В компактном режиме раскладка
    // зоны не участвует: там свой эфемерный стек.
    const at = compact ? (tabletKeys.includes(k) ? side : null) : zoneOf(zones, k);
    // Открыта в соседней зоне — её иконка сейчас там
    if (at !== null && at !== side) return false;
    // Закрыта — иконка ждёт там, где панель лежала в последний раз (а до первого
    // открытия — в домашней зоне из реестра)
    if (at === null && homeOf(zones, k) !== side) return false;
    // Сессионные показываются только когда есть что открывать
    if (SESSION_KEYS.includes(k) && sessionPanels) return sessionPanels.visible(k, openKeys.includes(k));
    return true;
  };

  // Показывать нечего: рельсу не рисуем вовсе, чтобы у контента не торчала пустая
  // полоса. Ширина зоны при этом 0 — иначе FAB AI-хаба уедет под невидимую рельсу.
  // Панели, доступные в этой зоне, СЧИТАЯ спрятанные в ящик: по этому счёту зона
  // решает, показывать ли рельсу и есть ли чем управлять. Вызов стрелкой, а не
  // ссылкой на функцию: filter вторым аргументом отдаёт индекс, и он молча сошёл бы
  // за флаг «учитывать ящик».
  const availableAll = PANEL_KEYS.filter(k => railKeyVisible(k, false));
  // Кнопки, лежащие в ящике ЭТОЙ рельсы: спрятанные, закрытые и приписанные к этой
  // стороне (открытая панель показывает кнопку в столбце, а не строкой в меню).
  const tuckedKeys = compact ? [] : availableAll.filter(k => isTucked(zones, k) && zoneOf(zones, k) === null);
  // Ящик разворачивается обратно в столбец, когда прятать больше нечего: спрятаны
  // ВСЕ кнопки зоны, и рельса состояла бы из одной «…». Такая рельса ничего не
  // экономит и не говорит, что за ней, — вместо привычной кнопки (закрыл чаты и
  // открываешь их обратно тем же местом) человек видит безымянное многоточие.
  // Само состояние ящика при этом не трогаем: вернулась хоть одна кнопка в столбец —
  // спрятанные снова уезжают в меню.
  const stashRevealed = !compact && tuckedKeys.length > 0 && tuckedKeys.length === availableAll.length;
  // Кнопки, стоящие в столбце прямо сейчас: по ним решается, есть ли что убирать
  // в ящик (последнюю кнопку рельсы туда не отдаём).
  const columnKeys = stashRevealed ? availableAll : availableAll.filter(k => railKeyVisible(k));
  // Порядок кнопок столбца СВЕРХУ ВНИЗ — по нему панель встаёт на своё место в
  // раскладке, и по нему же рисуется призрак места (placeByRail). Собирается ровно
  // тем же способом, что и сами кнопки (см. railGroup): группы подряд, внутри
  // группы — пользовательский порядок railOrder. Разойдись эти два фильтра,
  // обещание рельсы разъехалось бы с её собственным видом.
  const railSeq = RAIL_GROUPS.flatMap(g => sortRail(
    zones.railOrder,
    g.filter(k => (stashRevealed ? railKeyVisible(k, false) : railKeyVisible(k))),
  ));
  // Счёт «есть ли зоне что показать» идёт по availableAll: спрятанные кнопки со
  // столбца ушли, но рельса нужна — без неё ящик вместе с ними исчез бы с экрана.
  const railHidden = !!hideWhenEmpty && availableAll.length === 0 && openKeys.length === 0;

  // === ВИДИМОСТЬ РЕЛЬСЫ ===
  // Рельса стоит, пока зоне есть что показать: даже единственная открытая панель
  // не прячет её. Раньше в этом случае рельса убиралась (панель, мол, сама себя
  // называет), но тогда закрыть панель было нечем, кроме крестика в шапке, и
  // край окна дёргался при каждом открытии.
  //
  // Пустая зона — исключение: она появляется на экране только чтобы принять
  // перетаскиваемую панель, и рельса в ней состояла бы из одних служебных кнопок
  // («режим» и «свернуть все») — управлять ими там нечем.
  const zoneEmpty = availableAll.length === 0 && openKeys.length === 0;
  const showRail = !railHidden && !zoneEmpty;
  // Управлять раскладкой при единственной доступной панели нечем: тумблер режима
  // и «свернуть все» в этом случае не рисуем. Считаем со спрятанными: убранная в
  // ящик кнопка никуда не делась, просто лежит в меню.
  const singlePanelMode = availableAll.length === 1 && !compact;

  // Ремонт сохранённой раскладки: панель, которой на этом экране в этой зоне быть
  // не может, выселяется домой. Иначе она пропадала совсем — в родной зоне её нет
  // («лежит в соседней»), а соседняя нарисовать её не умеет. Проверка идёт по
  // allowedKeys, а не по наличию контента: набор экрана статичен, а контент
  // приезжает асинхронно и на полкадра бывает пустым у кого угодно.
  useEffect(() => {
    evict(side, allowedKeys);
  }, [evict, side, allowedKeys, layout, zoneState.stash]);

  // Плавающие панели закрываются кликом мимо: слой висит поверх контента, и
  // «убрать его с глаз» должно быть так же дёшево, как открыть. Клик по самой
  // панели, по рельсе и по докам под ней — не «мимо» (иначе кнопка закрывала бы
  // панель ровно в тот момент, когда её открывает).
  const floatRef = useRef<HTMLDivElement | null>(null);
  const railBoxRef = useRef<HTMLDivElement | null>(null);
  // Место в столбце, выбранное курсором прямо сейчас: кнопка встанет ПЕРЕД before
  // (null — в конец группы), сам объект null — места нет и порядок не трогаем.
  // Считает его рельса (только она знает геометрию столбца), а зоне оно нужно
  // единственный раз — в момент дропа: отсюда ref, а не состояние. Иначе зона
  // перерисовывалась бы на каждое дрожание курсора над рельсой.
  const railInsert = useRef<{ before: string | null } | null>(null);
  useEffect(() => {
    if (!floating || openKeys.length === 0 || isZoneCollapsed(zoneState)) return;
    const onDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (floatRef.current?.contains(t) || railBoxRef.current?.contains(t)) return;
      // Клик в модалку/меню, живущие порталом поверх всего, панели не касается
      if ((t as HTMLElement).closest?.('[data-portal-layer]')) return;
      // СВОРАЧИВАЕМ зону, а не закрываем панели: закрытые пришлось бы открывать
      // заново по одной, а свёрнутые возвращает та же кнопка рельсы, которой их
      // прячут вручную — состав раскладки при этом цел.
      toggleCollapsed(side);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [floating, openKeys, toggleCollapsed, side, zoneState]);

  const dnd = usePanelDnd({
    zone: side,
    // Тащить есть смысл, когда переносить есть куда: соседняя зона всегда рядом,
    // поэтому хватает одной открытой панели. Режим одной панели перетаскиванию не
    // помеха — внутри зоны менять нечего, но перенести панель НА ДРУГУЮ СТОРОНУ
    // из него нужно ровно так же (принимающая solo-зона меняет свою панель на
    // гостя, см. moveAcrossAt). Без DnD только компактный режим.
    enabled: openKeys.length > 0 && !compact,
    // Зона принимает только те панели, которые сама умеет показать
    accepts: keyAvailable,
    // Дроп НА панель: две открытые меняются местами, а кнопка из рельсы (панель
    // закрыта — своего слота у неё нет) ЗАМЕЩАЕТ панель под курсором: гость встаёт
    // в её слот, хозяин уходит закрытым. Так кнопкой можно открыть панель именно
    // вместо конкретной соседки, а не туда, куда решит раскладка.
    onSwap: (from, to) => {
      if (zoneOf(zones, from) === null) {
        // Кнопку вытащили из ящика прямо на панель — она встаёт в её слот, а сама
        // кнопка возвращается на рельсу: дроп в раскладку и есть жест возврата
        if (dnd.fromTucked) untuck(side, from);
        replaceWith(from, to);
      } else swapWith(from, to);
    },
  });

  // Иконка ЗАКРЫТОЙ панели под курсором: в раскладке показываем место, куда эта
  // панель встанет по клику. Дешевле любого превью и отвечает на главный вопрос
  // рельсы — «куда оно денется», особенно когда панелей уже несколько.
  // Гашение с паузой (railHover) — иначе призрак мигал бы на зазорах между иконками.
  const hovered = useRailHover();
  const hoverKey = hovered.key;

  // Место, куда встанет панель под курсором: та же логика, что у открытия
  // (общее правило placeByRail — панель встаёт на своё место в порядке кнопок).
  // В solo-режиме показывать нечего — там новая панель просто заменяет единственную.
  const ghostKey = !compact && !soloMode && !dnd.active
    && hoverKey && !openKeys.includes(hoverKey) && keyAvailable(hoverKey)
    ? hoverKey : null;

  // Курсор на кнопке УЖЕ ОТКРЫТОЙ панели в рельсе — подсвечиваем её карточку
  // акцентным кольцом (статично, не вспышкой flash), чтобы глаз сразу нашёл, к
  // чему относится кнопка. Для закрытой панели этим занимаются ghost/peek.
  const railHighlightKey = !compact && !dnd.active && hoverKey && openKeys.includes(hoverKey)
    ? hoverKey : null;
  const ghostAt = ghostKey ? placeByRail(layout, ghostKey, side, colCapNow(), railSeq) : null;
  // Колонка призрака в ВИДИМЫХ координатах: раскладка может держать колонки из
  // недоступных на этом экране панелей, и их индексы со списком columns не совпадают
  const ghostCol = ghostAt && !ghostAt.newColumn ? columns.findIndex(c => c.ci === ghostAt.ci) : -1;
  // Своя колонка нужна и когда место — новая колонка, и когда целевая колонка
  // раскладки на этом экране не показана
  const ghostNewCol = !!ghostAt && ghostCol < 0;
  // Строка призрака в ВИДИМЫХ координатах — обратный пересчёт к layoutRowFor:
  // недоступные на этом экране панели раскладка держит, но не рисует, и по их
  // числу выше места вставки линия уехала бы мимо своего стыка.
  const ghostRow = ghostAt && ghostCol >= 0
    ? (layout[ghostAt.ci] ?? []).slice(0, ghostAt.ri).filter(k => columns[ghostCol].keys.includes(k)).length
    : 0;
  // У какой кромки зоны родится новая колонка. Обычно это центр: панель встаёт в
  // колонку у рельсы, а вытесненный ею «хвост» уезжает от рельсы (см. placeByRail).
  // Пустая зона — исключение: там первая колонка и есть колонка у рельсы.
  const ghostAtStart = ghostAt?.newColumn && layout.length > 0 ? ghostAt.ci === 0 : isLeft;

  // Ширина зоны: колонки по width плюс зазоры МЕЖДУ ними (крайние направляющие
  // в покое нулевые: зазор к рельсе даёт отдельная прокладка, к центру — сплиттер).
  // Место будущей колонки ширины не занимает: её обещает вертикальная линия у
  // края зоны — раздвигать раскладку под курсором значило бы дёргать центр на
  // каждое наведение, да и при перетаскивании новая колонка показана так же.
  const zoneW = columns.length > 0 ? columns.length * width + (columns.length - 1) * GAP : 0;
  // Ширина зоны: тянем от кромки окна — колонки растут; width хранится на ОДНУ
  // колонку, поэтому сдвиг курсора делится на их число
  const { dragging: widthDragging, onPointerDown: handleWidthDrag } =
    usePanelWidthDrag(width, n => setWidth(side, n), side, columns.length);

  // Сдвиг FAB AI-хаба: кромку занимают рельса и панели — пробрасываем их суммарную
  // ширину в глобальную переменную (её читает AiLauncher). Слагаемые считаются ПО
  // РАЗМЕТКЕ: рельса + зазор до панелей + сама зона + её ресайз-сплиттер.
  // Drawer компактного режима не считаем — он overlay и живёт поверх контента сам.
  // Кромку занимает и док под рельсой: даже при схлопнутой рельсе панелей он стоит
  // на своём месте, и FAB, посчитанный по нулю, уехал бы под него.
  // В плавающем режиме панели лежат ПОВЕРХ контента и кромки не занимают — иначе
  // FAB отпрыгивал бы от каждого открытия панели вместе с центром
  const zoneEdgeW = !showRail ? (railFooter ? RAIL_W + RAIL_GAP : 0) : RAIL_W + RAIL_GAP + (compact
    ? (tabletKeys.length > 0 && tabletInline ? width + GAP * 2 : 0)
    : (columns.length > 0 && !floating ? zoneW + RAIL_GAP : 0));
  useEffect(() => {
    const prop = isLeft ? '--cc-fab-left' : '--cc-fab-right';
    document.documentElement.style.setProperty(prop, `${zoneEdgeW + 20}px`);
    return () => { document.documentElement.style.removeProperty(prop); };
  }, [zoneEdgeW, isLeft]);

  // Флеш «панель уже открыта»: внешние кнопки (git-бар над композером) шлют
  // cc-panel-flash, карточка на мгновение обводится акцентом. Счётчик n нужен,
  // чтобы повторный клик по той же панели перезапускал таймер. Слушают ОБЕ зоны —
  // панель могла переехать в любую из них.
  const [flash, setFlash] = useState<{ key: PanelKey; n: number } | null>(null);
  useEffect(() => {
    const onFlash = (e: Event) => {
      const key = (e as CustomEvent<{ key?: PanelKey }>).detail?.key;
      if (key) setFlash(cur => ({ key, n: (cur?.n ?? 0) + 1 }));
    };
    window.addEventListener('cc-panel-flash', onFlash);
    return () => window.removeEventListener('cc-panel-flash', onFlash);
  }, []);
  useEffect(() => {
    if (!flash) return;
    // Снимаем класс сразу по окончании анимации (0.55s в index.css) — иначе
    // повторный клик по кнопке не перезапустил бы вспышку
    const id = setTimeout(() => setFlash(null), 600);
    return () => clearTimeout(id);
  }, [flash]);

  // Превью панели под курсором (механика с паузой на уход — panelPeek).
  // Панель закрепили кликом или потащили — попап уступает место: считаем это
  // прямо на рендере, а не эффектом со сбросом состояния (лишний кадр с попапом
  // поверх уже открытой панели виден глазом).
  const peeked = usePanelPeek();
  // Панель, которую только что закрепили из попапа: она уже стоит перед глазами на
  // этом самом месте, поэтому появляется без анимации — иначе картинка дёргается,
  // будто панель прилетела из рельсы. Сбрасывается при обычном открытии кликом.
  const [pinned, setPinned] = useState<PanelKey | null>(null);
  const peek = PEEK_ENABLED && peeked.key && !openKeys.includes(peeked.key) && !dnd.active
    ? peeked.key : null;

  // Тащат ЗАКРЫТУЮ панель (её вытянули за иконку из рельсы) — дроп её откроет
  const dragClosed = dnd.from !== null && zoneOf(zones, dnd.from) === null;

  // Панель, которую примет РЕЛЬСА этой зоны (null — мишени нет), и что дроп сделает:
  //  • открытая на СВОЕЙ рельсе (fromZone === side) — закрыть, оставив кнопку здесь;
  //  • открытая на ЧУЖОЙ рельсе — перенести панель в эту зону (открыть тут);
  //  • кнопка закрытой панели на ЧУЖОЙ рельсе — переезд самой кнопки;
  //  • кнопка из ящика — возврат в столбец.
  // И к любому из исходов добавляется МЕСТО: куда именно в столбце встанет кнопка
  // (см. railInsert). Поэтому мишень даёт и своя рельса закрытой кнопке — раньше
  // такой дроп ничего бы не изменил, а теперь это и есть перестановка.
  const railDrop = dnd.accepting ? dnd.from : null;
  // Дроп на СВОЮ рельсу открытой панели = закрыть; иначе (чужая рельса) = перенос.
  const railWillClose = railDrop != null && !dragClosed && dnd.fromZone === side;

  // Панель тащат из соседней зоны или из рельсы — эта обязана показаться, даже
  // когда пуста: иначе, утащив отсюда последнюю панель, вернуть её было бы
  // некуда (осталась бы только дорога через клик по иконке).
  const acceptsForeign = dnd.accepting && !compact && (dnd.fromZone !== side || dragClosed);

  // Ранний return — ПОСЛЕ всех хуков (useSyncExternalStore, useEffect выше).
  // Ни одной доступной панели и ничего не открыто — зоны на экране нет вовсе,
  // иначе у контента торчала бы пустая полоса рельсы. Док под рельсой от раскладки
  // не зависит: пока он есть, зона остаётся на экране (капсула рельсы при этом
  // схлопнута — showRail её погасит).
  if (availableAll.length === 0 && openKeys.length === 0 && !acceptsForeign && !railFooter) return null;

  // Где лежит перетаскиваемая панель в ВИДИМОЙ раскладке этой зоны (null — тащат
  // из соседней). Нужно, чтобы не предлагать места, дающие ту же раскладку:
  // «над собой», «под собой» и вынос в свою же колонку, если панель в ней одна.
  // Такие направляющие сбивают — человек целится, отпускает, и ничего не меняется.
  const dragPos = (() => {
    if (!dnd.from) return null;
    for (let vi = 0; vi < columns.length; vi++) {
      const ri = columns[vi].keys.indexOf(dnd.from);
      if (ri >= 0) return { vi, ri, alone: columns[vi].keys.length === 1 };
    }
    return null;
  })();
  const rowGuideUseless = (vi: number, ri: number) =>
    dragPos != null && dragPos.vi === vi && (ri === dragPos.ri || ri === dragPos.ri + 1);
  const colGuideUseless = (vi: number) =>
    dragPos != null && dragPos.alone && (vi === dragPos.vi || vi === dragPos.vi + 1);

  // Настоящий индекс СТРОКИ в layout по видимой позиции ri колонки: пропущенные
  // (недоступные) панели сдвигают нумерацию, а moveAt ждёт индекс в исходной
  // колонке. Позиция за последней видимой панелью = конец настоящей колонки.
  const layoutRowFor = (col: { ci: number; keys: PanelKey[] }, ri: number): number => {
    const real = layout[col.ci] ?? [];
    if (ri >= col.keys.length) return real.length;
    const at = real.indexOf(col.keys[ri]);
    return at >= 0 ? at : real.length;
  };

  // Настоящий индекс РАЗДЕЛИТЕЛЯ колонок по видимому: разделитель перед видимой
  // колонкой — это её же исходный индекс, крайний — конец layout.
  const layoutSepFor = (vi: number): number => (vi < columns.length ? columns[vi].ci : layout.length);

  // Направляющая места вставки в колонку. base — место в потоке: по краям колонки
  // 0 (в покое их нет), между панелями GAP — там направляющая подменяет хендл
  // ресайза той же высоты.
  const rowGuide = (col: { ci: number; keys: PanelKey[] }, vi: number, ri: number, base = 0, edge?: 'start' | 'end', fill?: boolean) => (
    <PanelDropGuide
      axis="y"
      key={`row-${vi}-${ri}`}
      dndActive={dnd.accepting && !rowGuideUseless(vi, ri)}
      base={base}
      edge={edge}
      fill={fill}
      // В большом плейсхолдере показываем иконку той панели, которую тащат: место
      // размером в полколонки без опознавательных знаков читается как «что-то
      // сломалось», а не как «панель встанет сюда»
      icon={dnd.from ? PANEL_META[dnd.from].Icon : undefined}
      {...dnd.guideProps(`row:${vi}:${ri}`, from => {
        // Кнопку вытащили ИЗ ЯЩИКА прямо в раскладку — это и возврат кнопки на
        // рельсу, и открытие панели одним жестом
        if (dnd.fromTucked) untuck(side, from);
        // Колонка стоит по контенту — гость встаёт в её свободный низ, а не делит
        // высоту с соседями пополам (см. keepHeightsOnInsert)
        keepHeightsOnInsert(from, col.ci, ri);
        moveAt(from, side, col.ci, layoutRowFor(col, ri));
      })}
    />
  );

  // Направляющая между колонками: дроп сюда выносит панель в НОВУЮ колонку
  const colGuide = (vi: number, base = 0, edge?: 'start' | 'end') => (
    <PanelDropGuide
      axis="x"
      key={`col-${vi}`}
      dndActive={dnd.accepting && !colGuideUseless(vi)}
      base={base}
      edge={edge}
      {...dnd.guideProps(`col:${vi}`, from => {
        if (dnd.fromTucked) untuck(side, from);
        moveToNewColumn(from, side, layoutSepFor(vi));
      })}
    />
  );

  // Иконки одной группы рельсы: скрытые отсеиваются здесь же — пустая группа не
  // рисуется вовсе, вместе со своим разделителем (это делает PanelRail).
  // tucked — набор строится для ЯЩИКА («…»): фильтр там свой (кнопки как раз убраны
  // со столбца), а перетаскивание строки помечается как жест возврата.
  //
  // Порядок внутри группы — пользовательский (railOrder), заданный перетаскиванием
  // кнопок; нетронутые группы идут каталожным порядком. Сортируем ПОСЛЕ фильтра:
  // спрятанные и уехавшие в соседнюю зону кнопки в столбце не участвуют, но своё
  // место в сохранённом порядке держат — оно вернётся вместе с ними.
  const railGroup = (keys: readonly PanelKey[], tucked = false): RailItem[] => sortRail(
    zones.railOrder,
    keys.filter(k => tucked || (stashRevealed ? railKeyVisible(k, false) : railKeyVisible(k))),
  ).map(k => ({
    key: k,
    title: PANEL_META[k].title,
    Icon: PANEL_META[k].Icon,
    active: openKeys.includes(k),
    badge: sessionPanels?.railBadge(k) ?? railCounts?.[k] ?? null,
    // Иконку можно не только нажать, но и утащить в раскладку: клик открывает
    // панель туда, куда решит зона, а перетаскивание — ровно на выбранное место.
    // В компактном режиме раскладки нет, там только клик.
    //
    // Начало перетаскивания снимает превью: браузер не шлёт mouseleave при
    // dragstart, поэтому назначенный по наведению попап доживал до конца
    // перетаскивания и выскакивал уже ПОСЛЕ дропа, в покинутой рельсе. Снимаем
    // СЛЕДУЮЩИМ кадром — по той же причине, по какой отложено и само состояние
    // перетаскивания (см. dragSourceProps): перерисовка в обработчике dragstart
    // отменяет жест.
    dragProps: compact ? undefined : (() => {
      // rail — кнопка стоит в СТОЛБЦЕ (а не строкой в меню ящика): её превью браузер
      // уводит вбок, чтобы не накрывать рельсу с местами вставки
      const src = dnd.dragSourceProps(k, { tucked, rail: !tucked });
      return {
        ...src,
        onDragStart: (e: DragEvent<HTMLElement>) => {
          src.onDragStart?.(e);
          requestAnimationFrame(() => peeked.clear());
        },
      };
    })(),
    // Наведение — на десктопе для ЛЮБОЙ иконки (на тач-экране наведения не
    // бывает). Место показывается только для закрытой панели, но решает это
    // ghostKey, а не подписка: иначе после закрытия панели кликом курсор уже
    // стоял бы на иконке без единого mouseenter впереди — и место не появилось
    // бы, пока мышь не отвести и не вернуть.
    ...(compact ? null : {
      pinnable: PEEK_ENABLED && !openKeys.includes(k),
      onHoverStart: () => {
        hovered.enter(k);
        if (PEEK_ENABLED && !openKeys.includes(k)) peeked.show(k);
      },
      onHoverEnd: () => { hovered.leave(); peeked.hide(); },
    }),
    onClick: () => {
      const isOpen = openKeys.includes(k);
      // Клик прерывает попап: назначенный уже не нужен, а показанный сменится
      // настоящей панелью. Если попап этой панели сейчас на экране — это
      // закрепление (кнопка под курсором и выглядит булавкой), и панель обязана
      // встать без анимации: она уже стоит на этом месте. Клик по любой другой
      // иконке — обычное открытие, с анимацией.
      // Наведение при этом НЕ сбрасываем: курсор с иконки никуда не делся, и
      // после закрытия панели место её возврата должно показаться сразу. Для
      // открывшейся панели место и так не рисуется — ghostKey отсеет её по
      // openKeys.
      peeked.clear();
      setPinned(peek === k ? k : null);
      if (compact) {
        // До двух панелей: третья вытесняет самую старую (FIFO)
        setTabletPanels(cur => cur.includes(k) ? cur.filter(x => x !== k) : [...cur, k].slice(-2));
      } else {
        // Вместимость колонки считаем ОДИН раз на клик: и место панели, и веса
        // должны исходить из одной и той же высоты
        const cap = colCapNow();
        // Панель открывается в колонку, стоящую по контенту — соседи сохраняют
        // свою высоту, новая забирает свободный низ (см. keepHeightsOnInsert)
        if (!isOpen && !soloMode) {
          const at = placeByRail(layout, k, side, cap, railSeq);
          // Место вставки передаём целиком (колонка И строка): панель встаёт по
          // порядку кнопок, то есть может попасть в середину — высоту ей уступает
          // сосед сверху от ЭТОГО места, а не последняя панель колонки.
          if (!at.newColumn) keepHeightsOnInsert(k, at.ci, at.ri);
        }
        toggle(side, k, cap, railSeq);
      }
      // Панель в результате клика ОТКРЫЛАСЬ — сообщаем подписчику (граф и т.п.)
      if (!isOpen) onPanelOpen?.(k);
    },
  }));

  // Карточка панели. Растягивается ли она на всю высоту, решает panelStretched:
  // одиночная панель в колонке у центра — по контенту, всё прочее (2+ в колонке,
  // ряды не у центра) — на всю высоту с делением по весам. В компактном режиме
  // колонок нет (vi не передан) — там стек из двух панелей делит высоту, как и был.
  const renderPanel = (k: PanelKey, multiInCol: boolean, vi?: number): ReactNode => {
    const { title, Icon } = PANEL_META[k];
    const stretched = vi === undefined
      ? multiInCol || !!fillWanted[k]
      : panelStretched(k, vi, multiInCol ? 2 : 1);
    const onCloseThis = compact ? () => setTabletPanels(cur => cur.filter(x => x !== k)) : () => closeTo(side, k);
    const shell = (
      <PanelShell
        icon={<Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />}
        title={title}
        badge={sessionPanels?.headerBadge(k) ?? null}
        // Закрытие из шапки: на десктопе иконка панели под курсором сама
        // становится крестиком, в компактном режиме (тач, hover'а нет) остаётся
        // отдельная кнопка справа. Закрываем В СВОЮ ЗОНУ — кнопка панели остаётся
        // там, где её только что закрыли.
        onClose={onCloseThis}
        closeMode={compact ? 'button' : 'icon'}
        fill={stretched}
        flash={flash?.key === k}
        highlighted={railHighlightKey === k}
        slideDirection={isLeft ? 'left' : 'up'}
        // Анимация появления — только когда карточка действительно возникла на
        // новом месте: закреплённый попап уже стоит перед глазами, а при переносе
        // «прилетает» одна панель — соседние перестраиваются, но с места не
        // сходили, и мигать им незачем.
        animate={pinned !== k && (dnd.moved === null || dnd.moved === k)}
        // Панель стоит по контенту — её высоту меряем: по сумме таких высот
        // укорачивается сплиттер ширины (см. panelHeights). Растянутая мерки не
        // требует — она и так до низа.
        rootRef={stretched ? undefined : panelHeightRef(k)}
        {...dnd.panelProps(k)}
      >
        {/* Панель может попросить всю высоту сама (см. panelFill) — приёмник её
            запроса привязан к ключу и живёт ровно вокруг её содержимого */}
        <PanelFillContext.Provider value={fillSinkFor(k)}>
          {content(k)}
        </PanelFillContext.Provider>
      </PanelShell>
    );
    // Слот ставится ВСЕГДА — иначе при появлении соседа в колонке менялся бы тип
    // узла на позиции и React перемонтировал бы карточку (анимация появления на
    // ровном месте и потерянное состояние панели). Вес распределяют только те,
    // кто высоту делит; остальным слот отдаёт высоту по контенту.
    //
    // Делят высоту только растянутые соседи. Растянутая в одиночку идёт без веса
    // (weight по умолчанию 1 → flex:1) и без ресайза — делить не с кем.
    const shares = multiInCol && stretched;
    return (
      <PanelSlot
        fill={stretched}
        weight={shares ? zones.weights[k] : undefined}
        // Ссылка на слот нужна ресайзу высот (он бывает только у делящих) и расчёту
        // места для новой панели (insertPlan) — а тому нужна высота ЛЮБОЙ панели
        // колонки, включая одиночную растянутую: у неё нет ни веса, ни замера
        // panelHeights, и без слота её высота была бы неизвестна.
        slotRef={el => { panelRefs.current[k] = el; }}
      >
        {shell}
      </PanelSlot>
    );
  };

  // Карточка попапа-превью: та же панель, что открылась бы кликом, но временная.
  // Отличается тенью модалки и акцентной рамкой, а вместо крестика в шапке —
  // булавка: закрепить в раскладке. Ширина — как у колонки зоны.
  //
  // Во всю высоту тянемся, только когда в зоне УЖЕ есть открытые панели: попап
  // должен накрыть их целиком, иначе из-под него торчали бы куски чужой раскладки.
  // Над пустым холстом растягивать нечего — там высота по содержимому.
  const peekFull = openKeys.length > 0;
  const peekCard = peek && (
    <PanelShell
      icon={(() => { const { Icon } = PANEL_META[peek]; return <Icon size={15} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />; })()}
      title={PANEL_META[peek].title}
      badge={sessionPanels?.headerBadge(peek) ?? null}
      // Закрепить — той же механикой, что закрытие у обычных панелей: иконка в
      // шапке под курсором становится булавкой. Отдельная кнопка справа была бы
      // единственной в продукте, кто так делает.
      iconAction={{
        Icon: Pin,
        title: 'Закрепить панель',
        onClick: () => { peeked.clear(); setPinned(peek); toggle(side, peek, colCapNow(), railSeq); onPanelOpen?.(peek); },
      }}
      fill={peekFull}
      // Временный слой обозначаем ТЕНЬЮ, а не цветной рамкой: акцентная обводка
      // спорила с содержимым панели и читалась как «выделено», хотя ничего не
      // выбрано. Рамка обычная, островная; высоту над раскладкой даёт двойная
      // тень SHADOW.peek — контактная у кромки плюс широкий разлёт.
      style={{ width, boxShadow: SHADOW.peek }}
    >
      {content(peek)}
    </PanelShell>
  );

  // Строки ящика этой рельсы. Собираются тем же сборщиком, что и группы иконок:
  // клик, бейдж и ручка перетаскивания у них те же, отличается только место, где
  // их рисуют.
  // Развёрнутый ящик пуст: его кнопки уже стоят в столбце (см. stashRevealed)
  const tuckedItems = stashRevealed ? [] : railGroup(tuckedKeys, true);
  // Кружки спрятанных панелей не видны — их сумма переезжает на кнопку «…»
  const tuckedBadge = tuckedItems.reduce((n, it) => n + (it.badge ?? 0), 0);

  const rail = (
    <PanelRail
      side={side}
      visible={showRail}
      peek={peekCard ? {
        node: peekCard,
        full: peekFull,
        onMouseEnter: () => peeked.hold(),
        onMouseLeave: () => peeked.hide(),
      } : undefined}
      footer={railFooter}
      // Состав групп — RAIL_GROUPS: тот же список задаёт и пределы перестановки
      // кнопок, поэтому он один на оба применения. Разделители PanelRail рисует сам и
      // убирает вместе с пустой группой — выключенные инструменты уносят и свою черту.
      groups={RAIL_GROUPS.map(g => railGroup(g))}
      // Свой зазор до центра нужен только при закрытых панелях: иначе его даёт
      // прокладка перед зоной
      // Зазор до центра обычно даёт сама зона (её сплиттер/крайняя направляющая), но
      // при закрытых панелях — и ВСЕГДА в плавающем режиме — его держит рельса: там
      // зона места не занимает вовсе, и без этого зазора центр прыгал бы на 8px при
      // каждом открытии панели
      gapToCenter={openKeys.length === 0 || floating ? RAIL_GAP : 0}
      // Ящик рельсы: редкие кнопки, которые сюда перетащили, и тумблер режима зоны
      // (своей кнопки в столбце у режима больше нет). В компактном режиме ящика нет
      // вовсе — там ни колонок, ни перетаскивания; при единственной панели он нужен,
      // только если в нём что-то лежит.
      overflow={compact || (singlePanelMode && tuckedItems.length === 0) ? undefined : {
        items: tuckedItems,
        modeToggle: singlePanelMode ? undefined : {
          soloMode,
          onToggle: () => setMode(side, soloMode ? 'multi' : 'solo'),
        },
        badge: tuckedBadge || null,
        dragActive: dnd.active,
        // Дроп на «…» убирает кнопку панели в ящик (открытая при этом закрывается).
        // Мишень не предлагается тому, кто лежит в ЭТОМ же ящике (дроп ничего не
        // изменил бы), и последней кнопке столбца: рельса осталась бы из одного
        // многоточия, а его тут же развернуло бы обратно (см. stashRevealed) —
        // жест, который отменяет сам себя, лучше не предлагать вовсе. Из ящика
        // соседней стороны кнопку принимаем — так она переезжает между ящиками,
        // не появляясь по дороге в столбце.
        drop: dnd.accepting && dnd.from !== null
          && !(isTucked(zones, dnd.from) && homeOf(zones, dnd.from) === side)
          && columnKeys.some(k => k !== dnd.from)
          ? { active: true, ...dnd.guideProps('overflow', from => tuck(side, from)) }
          : undefined,
      }}
      collapse={compact || singlePanelMode ? undefined : {
        collapsed: isZoneCollapsed(zoneState),
        disabled: openKeys.length === 0 && !isZoneCollapsed(zoneState),
        onToggle: () => toggleCollapsed(side),
      }}
      // Дроп на рельсу — четыре исхода на одном пути (см. railDrop / railWillClose):
      //  • СВОЯ рельса открытой панели → закрыть, оставив кнопку здесь;
      //  • ЧУЖАЯ рельса открытой панели → перенести панель в эту зону (открыть тут);
      //  • ЧУЖАЯ рельса закрытой кнопки → переезд самой кнопки, не открывая панель;
      //  • СВОЯ рельса закрытой кнопки → одна перестановка, без прочих последствий.
      // К каждому из них добавляется МЕСТО в столбце: куда встанет кнопка. Раньше для
      // переноса на другую сторону приходилось целиться в направляющие колонок;
      // теперь хватает броска на рельсу, и он же выбирает позицию.
      drop={railDrop
        ? {
            active: true,
            key: railDrop,
            // Знак на месте вставки: крестик — только когда дроп ЗАКРОЕТ панель
            // (своя рельса); иначе иконка панели — «встанет сюда»
            icon: railWillClose ? undefined : PANEL_META[railDrop].Icon,
            onInsert: pos => { railInsert.current = pos; },
            ...dnd.guideProps('rail', from => {
              // Порядок кнопки в столбце — ПЕРВЫМ: он про место, всё остальное ниже
              // про принадлежность (открыта ли панель и чья кнопка). Место рельса
              // отдаёт соседом; своей группы у ключа может и не быть на экране —
              // тогда переставлять нечего.
              const pos = railInsert.current;
              const group = railGroupOf(from);
              railInsert.current = null;
              if (pos && group) reorder(group, from, isPanelKey(pos.before) ? pos.before : null);
              // Кнопку вернули из ящика на рельсу: панель не открываем — возвращают
              // именно кнопку (открытие — это дроп в раскладку)
              if (dnd.fromTucked) untuck(side, from);
              // Открытую панель на чужой рельсе ОТКРЫВАЕМ в этой зоне (перенос);
              // остальное (закрытие своей / переезд кнопки) делает closeTo
              else if (!railWillClose && zoneOf(zones, from) !== null) {
                const cap = colCapNow();
                const at = placeByRail(layout, from, side, cap, railSeq);
                if (!at.newColumn) keepHeightsOnInsert(from, at.ci, at.ri);
                toggle(side, from, cap, railSeq);
              } else closeTo(side, from);
            }),
          }
        : undefined}
    />
  );

  // Прокладка между рельсой и панелями зоны
  const railGapBox = <div style={{ width: RAIL_GAP, flexShrink: 0 }} />;

  // Длина сплиттера ширины: он стоит вплотную к колонке у центра, и когда та по
  // контенту (иначе тянулся бы через весь пустой холст, а grip оказывался напротив
  // пустоты), длина — сумма высот её панелей плюс зазоры между ними. Хоть у одной
  // панели замера ещё нет — тянемся на всю высоту, иначе сплиттер мигнёт огрызком.
  const contentLen = (keys: PanelKey[], stretched: (k: PanelKey) => boolean): number | null => {
    if (keys.length === 0 || keys.some(stretched)) return null;
    let sum = GAP * (keys.length - 1);
    for (const k of keys) {
      const h = panelH[k];
      if (h == null) return null;
      sum += h;
    }
    return sum;
  };
  const splitterLen = compact
    // Компактный стек: две панели делят высоту между собой, одна стоит по контенту
    ? contentLen(tabletKeys, k => tabletKeys.length > 1 || !!fillWanted[k])
    : (columns[centerVi]
        ? contentLen(columns[centerVi].keys, k => panelStretched(k, centerVi, columns[centerVi].keys.length))
        : null);
  const splitter = (
    <IslandSplitter
      orientation="v" active={widthDragging} onMouseDown={handleWidthDrag}
      gap={RAIL_GAP} length={splitterLen ?? undefined}
    />
  );

  // Призрак места: ВСЕГДА тонкая линия у кромки — «панель встанет сюда».
  // Большой прямоугольник (PanelDropSpot) оставлен только за ПЕРЕТАСКИВАНИЕМ: там
  // он ещё и мишень, в которую надо попасть курсором, поэтому обязан быть крупным.
  // У наведения ловить нечего — карточка в полколонки, вспыхивающая под курсором
  // при каждом проходе вдоль рельсы, шумит сильнее, чем подсказывает.
  //
  // Нулевая высота в потоке + absolute-линия у кромки: панели не сдвигаются,
  // знак совпадает с местом вставки при перетаскивании (base 0, edge 'end').
  // pointerEvents: none — призрак висит в раскладке, но курсору не мешает.
  // accent: наведение на кнопку — точное «кликнешь — встанет сюда», поэтому линия
  // контрастная акцентным цветом, но остаётся штриховой (не сплошной, как дроп).
  //
  // Панель встаёт по порядку кнопок, поэтому место бывает и В СЕРЕДИНЕ колонки.
  // Линия рисуется у кромки СОСЕДНЕЙ панели и отодвигается от неё в зазор: сверху
  // (up — место над самой первой панелью) или снизу (место под панелью выше). Тот
  // же приём, что у крайних направляющих дропа: edge 'start' → −sepShift,
  // 'end' → +sepShift.
  const ghostBox = (up: boolean) => (
    <div style={{ height: 0, position: 'relative', pointerEvents: 'none' }}>
      <div style={{
        position: 'absolute', left: 0, right: 0, top: -SEP_HIT / 2, height: SEP_HIT,
        display: 'flex', alignItems: 'center',
      }}>
        <PanelDropLine axis="y" accent shift={up ? -sepShift(0) : sepShift(0)} />
      </div>
    </div>
  );

  // Вертикальная линия «здесь заведётся новая колонка». Геометрия дословно как у
  // крайней направляющей при перетаскивании (PanelDropGuide с base 0): нулевая
  // ширина в потоке, хит-зона центром на кромке, линия отодвинута наружу на
  // sepShift. Знак сдвига — по КРОМКЕ, у которой колонка родится: у левой
  // (ghostAtStart, edge 'start') линия отодвигается влево (−sepShift), у правой
  // (edge 'end') — вправо (+sepShift). Одним знаком на обе стороны линия у левой
  // рельсы уезжала вправо от настоящего места вставки. Считать «на глаз» уже
  // пробовали. Кромка не равна стороне зоны: переполненная колонка у рельсы
  // выталкивает «хвост» к ЦЕНТРУ, и новая колонка рождается там (см. placeByRail).
  const newColShift = ghostAtStart ? -sepShift(0) : sepShift(0);
  const newColGhost = (
    <div style={{ width: 0, flexShrink: 0, position: 'relative', alignSelf: 'stretch' }}>
      <div style={{
        position: 'absolute', top: 0, bottom: 0, left: -SEP_HIT / 2, width: SEP_HIT,
        display: 'flex', alignItems: 'stretch', justifyContent: 'center', pointerEvents: 'none',
      }}>
        <PanelDropLine axis="x" accent shift={newColShift} />
      </div>
    </div>
  );

  // Колонки зоны. Крайние направляющие в потоке нулевые: зазоры уже дают
  // прокладка у рельсы и сплиттер у центра. Дроп-зоны направляющих —
  // absolute-оверлеи, поэтому при DnD раскладка не «дышит».
  const zoneBody = (
    <div style={{
      width: zoneW,
      flexShrink: 0,
      display: 'flex',
      boxSizing: 'border-box',
      // Тени панелей-островов не должны срезаться обёрткой
      overflow: 'visible',
      transition: widthDragging ? 'none' : `width ${PANEL_ANIM}`,
    }}>
      {/* Новая колонка родится у ЛЕВОЙ кромки зоны — линию рисуем перед колонками
          (какая это кромка, решает ghostAtStart: у пустой зоны — та, что у рельсы,
          иначе та, куда уедет вытесненный «хвост») */}
      {ghostKey && ghostNewCol && ghostAtStart && newColGhost}
      {columns.map((col, vi) => (
        <Fragment key={col.ci}>
          {/* Между колонками: в покое — сплиттер ширины (перетягивает доли пары),
              на время перетаскивания — направляющая новой колонки той же ширины
              (base GAP), раскладка от подмены не «дышит». Перед ПЕРВОЙ колонкой
              сплиттера нет — там край, и это направляющая новой колонки у рельсы. */}
          {vi === 0
            ? colGuide(vi, 0, 'start')
            : dnd.active
              ? colGuide(vi, GAP)
              : <IslandSplitter
                  orientation="v"
                  active={colDragging === columns[vi - 1].ci}
                  onMouseDown={handleColDrag(columns[vi - 1].ci, col.ci, 1)}
                  gap={GAP}
                />}
          <div
            // Ссылка на колонку — ресайзу ширины: по её пикселям берётся доля на старте
            ref={el => { colRefs.current[col.ci] = el; }}
            // Доля ширины колонки (grow-ratio): равные → 1, перетянутые делят зону
            style={{ flex: `${colFlex[col.ci] ?? 1} 1 0`, minWidth: 0, display: 'flex', flexDirection: 'column' }}
          >
            {/* Место будущей панели — под уже открытыми в этой колонке */}
            {rowGuide(col, vi, 0, 0, 'start')}
            {col.keys.map((k, ri) => {
              const prev = col.keys[ri - 1];
              const tag = `${vi}:${ri}`;
              // Растянутые соседи делят высоту долями (веса) — им весовой ресайз.
              // Колонка с 2+ панелями всегда растянута (см. panelStretched), так что
              // пара стретчится; нерастянутой паре делить нечего — там просто зазор.
              const pairShares = ri > 0
                && panelStretched(k, vi, col.keys.length)
                && panelStretched(prev, vi, col.keys.length);
              return (
                <Fragment key={k}>
                  {/* Между соседними панелями — хендл ресайза высот (тот же grip,
                      что у сплиттера ширины). Он же и есть зазор: отдельный gap
                      колонке не нужен, иначе между панелями было бы вдвое.
                      На время перетаскивания хендл подменяется направляющей той же
                      высоты — раскладка от этого не «дышит». */}
                  {ri > 0 && (
                    dnd.active
                      ? rowGuide(col, vi, ri, GAP)
                      : pairShares
                        ? <IslandSplitter
                            orientation="h"
                            active={rowDragging === tag}
                            onMouseDown={handleRowDrag(prev, k, tag)}
                            gap={GAP}
                          />
                        : <div style={{ height: GAP, flexShrink: 0 }} />
                  )}
                  {/* Место будущей панели НАД самой первой в колонке: своей
                      «панели сверху» у него нет, поэтому линия рисуется у верхней
                      кромки этой и отодвигается вверх */}
                  {ghostKey && ghostCol === vi && ghostRow === 0 && ri === 0 && ghostBox(true)}
                  {renderPanel(k, col.keys.length > 1, vi)}
                  {/* Место под ЭТОЙ панелью (в том числе последнее в колонке):
                      линия у её нижней кромки, отодвинутая в зазор */}
                  {ghostKey && ghostCol === vi && ghostRow === ri + 1 && ghostBox(false)}
                </Fragment>
              );
            })}
            {/* Последняя направляющая забирает свободный низ колонки — но ТОЛЬКО
                когда он там есть. Свободное место бывает у колонки по контенту (ряд
                у центра): целиться в полоску у кромки, когда ниже пустует полколонки,
                — мучение. Растянутый ряд доходит до низа сам, и растяжимая
                направляющая отбирала бы у его панелей долю: колонка переставала бы
                доходить до кромки. */}
            {/* Растяжимая направляющая выключена, пока в колонке стоит призрак
                (ghostCol !== vi): она забрала бы свободный низ колонки и утащила
                линию к самому низу, хотя панель встанет вплотную к соседке */}
            {rowGuide(col, vi, col.keys.length, 0, 'end',
              colByContent(col.keys, vi) && ghostCol !== vi)}
          </div>
        </Fragment>
      ))}
      {/* Панель заведёт свою колонку у ПРАВОЙ кромки зоны — обещаем это вертикальной
          линией, ровно как направляющая между колонками при перетаскивании. Линия
          висит оверлеем в нулевой ширине: раскладка не «дышит». */}
      {ghostKey && ghostNewCol && !ghostAtStart && newColGhost}
      {colGuide(columns.length, 0, 'end')}
    </div>
  );

  // Компактный режим: стек до двух панелей — в потоке на широком экране,
  // drawer поверх на узком; между двумя панелями — хендл ресайза высот
  const compactBody = (() => {
    if (!compact || tabletKeys.length === 0) return null;
    const stack = (
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {tabletKeys.map((k, ri) => (
          <Fragment key={k}>
            {ri > 0 && (
              <IslandSplitter orientation="h" active={rowDragging === 'tablet'} onMouseDown={handleRowDrag(tabletKeys[ri - 1], k, 'tablet')} gap={GAP} />
            )}
            {renderPanel(k, tabletKeys.length > 1)}
          </Fragment>
        ))}
      </div>
    );
    if (tabletInline) {
      return (
        <>
          {splitter}
          <div style={{ width: width + GAP * 2, flexShrink: 0, display: 'flex', padding: `0 ${GAP}px`, boxSizing: 'border-box' }}>
            {stack}
          </div>
        </>
      );
    }
    return (
      <>
        <div onClick={() => setTabletPanels([])} style={{ position: 'absolute', inset: 0, zIndex: 14, background: C.overlay }} />
        <div style={{
          position: 'absolute', top: GAP, bottom: GAP, zIndex: 15,
          ...(isLeft ? { left: RAIL_W + GAP } : { right: RAIL_W + GAP }),
          width: 'min(85vw, 380px)', display: 'flex', flexDirection: 'column', boxShadow: SHADOW.modal,
        }}>
          {stack}
        </div>
      </>
    );
  })();

  // Пустая зона на время перетаскивания из соседней: показываем единственную
  // направляющую — дроп сюда создаёт первую колонку.
  const emptyDropZone = (
    <div style={{ display: 'flex', flexShrink: 0 }}>
      {colGuide(0, EMPTY_DROP_W, isLeft ? 'start' : 'end')}
    </div>
  );

  // Порядок элементов по стороне: у левой зоны рельса первая и панели растут
  // вправо, у правой — зеркально.
  const body = compact ? compactBody : (
    columns.length > 0
      ? (isLeft ? <>{railGapBox}{zoneBody}{splitter}</> : <>{splitter}{zoneBody}{railGapBox}</>)
      : (acceptsForeign ? emptyDropZone : null)
  );

  // Плавающий режим («Стена»): панели не раздвигают контент, а всплывают поверх него
  // у своей рельсы. Кнопки и раскладка при этом ОБЩИЕ с обычным режимом — панель
  // просто рисуется другим слоем. Клик мимо панелей закрывает их (обработчик ниже).
  if (floating && !compact) {
    return (
      <>
        {isLeft ? <div ref={railBoxRef} style={{ display: 'flex', alignItems: 'stretch', height: '100%' }}>{rail}</div> : null}
        {columns.length > 0 && (
          <div
            ref={floatRef}
            style={{
              position: 'absolute', top: 0, bottom: 0, zIndex: Z.dropdown,
              ...(isLeft ? { left: RAIL_W + RAIL_GAP } : { right: RAIL_W + RAIL_GAP }),
              display: 'flex', alignItems: 'stretch', padding: `0 ${RAIL_GAP}px`,
              boxSizing: 'border-box', pointerEvents: 'auto',
            }}
          >
            {zoneBody}
          </div>
        )}
        {!isLeft ? <div ref={railBoxRef} style={{ display: 'flex', alignItems: 'stretch', height: '100%' }}>{rail}</div> : null}
      </>
    );
  }

  return isLeft ? <>{rail}{body}</> : <>{body}{rail}</>;
}
