import { useState, useEffect, useCallback, useRef, Fragment } from 'react'
import { createPortal } from 'react-dom'
import { Plus, Terminal, Monitor, Square, Play, RefreshCw, ChevronRight, Globe, GlobeLock, X } from 'lucide-react'
import { C, R, FONT, FS, SP, SHADOW, Z } from '../../lib/design'
import { Dot, EmptyState, IconButton, Button, PanelHeaderSlot, useHasPanelHeader } from '../ui'
import { ListDateDivider } from '../ListDateDivider'
import { ICON_STROKE } from '../ui/icons'
import { statusColor } from '../preview/PreviewView'
import { AddServiceDialog } from '../preview/AddServiceDialog'
import { useExternalPreviewLinks } from '../../hooks/useExternalPreviewLinks'
import { api } from '../../lib/api'
import { saveExternalUrl, clearExternalUrl, clearAllExternalUrls } from '../../lib/externalPreviewUrls'
import type * as ts from '../../lib/terminalSignalr'
import type { ProjectService } from '../../types'

type ToolsTab = 'terminal' | 'preview'

interface Props {
  projectId: string
  activeTab: ToolsTab
  onTabChange: (t: ToolsTab) => void
  // Список терминалов и операции подняты в WorkspacePage (нужны и хедеру ToolsPane)
  terminals: ts.TerminalInfo[]
  onCreateTerminal: () => void
  onStopTerminal: (id: string) => void
  onRenameTerminal: (id: string, name: string) => void
  onSelectTerminal: (id: string | null) => void
  activeTerminalId: string | null
  activePreviewId: string | null
  previewServices: ProjectService[]
  onRefreshServices: () => void
  onStartService: (svc: ProjectService) => void
  onStopService: (serviceId: string) => void
  onSelectPreview: (serviceId: string) => void
  terminalBusy?: boolean
}

// Русская метка и порядок групп источников
const SOURCE_META: Record<string, { label: string; order: number }> = {
  'launch.json': { label: 'Сохранённые', order: 0 },
  // Конфигурации Rider — сразу за сохранёнными: это тоже настроенный человеком запуск,
  // в отличие от догадок разбора манифестов
  'rider': { label: 'Rider', order: 1 },
  'npm': { label: 'Node', order: 2 },
  'dotnet': { label: '.NET', order: 3 },
  'docker-compose': { label: 'Docker', order: 4 },
  'procfile': { label: 'Procfile', order: 5 },
  'makefile': { label: 'Makefile', order: 6 },
  'custom': { label: 'Прочее', order: 7 },
}
const sourceMeta = (s: string) => SOURCE_META[s] ?? { label: s, order: 9 }

// Группировка сервисов по источнику — используется и здесь, и панелькой «Preview»
// нового интерфейса (workspace-cc-panels), чтобы списки выглядели одинаково
export function groupServices(services: ProjectService[]): [string, ProjectService[]][] {
  const map = new Map<string, ProjectService[]>()
  for (const s of services) {
    const arr = map.get(s.source) ?? []
    arr.push(s)
    map.set(s.source, arr)
  }
  return [...map.entries()].sort((a, b) => sourceMeta(a[0]).order - sourceMeta(b[0]).order)
}

export function ToolsSidebar({
  projectId, activeTab, onTabChange,
  terminals, onCreateTerminal, onStopTerminal, onRenameTerminal,
  onSelectTerminal, activeTerminalId,
  activePreviewId, previewServices,
  onRefreshServices, onStartService, onStopService, onSelectPreview,
  terminalBusy,
}: Props) {
  // Инлайн-переименование: id редактируемого терминала + текущее значение поля
  const [renaming, setRenaming] = useState<{ id: string; value: string } | null>(null)

  const commitRename = useCallback(() => {
    setRenaming(prev => {
      if (prev && prev.value.trim()) onRenameTerminal(prev.id, prev.value.trim())
      return null
    })
  }, [onRenameTerminal])

  // Preview: подгрузка списка сервисов при открытии вкладки
  useEffect(() => {
    if (activeTab === 'preview') onRefreshServices()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, projectId])

  // Группировка сервисов по источнику
  const groups = groupServices(previewServices)

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgPanel }}>
      {/* Вкладки */}
      <div style={{ flexShrink: 0, padding: '10px 12px', borderBottom: `1px solid ${C.border}` }}>
        <div style={{ display: 'flex', gap: 4, background: C.bgInset, borderRadius: R.md, padding: 2 }}>
          <TabButton active={activeTab === 'terminal'} onClick={() => onTabChange('terminal')}>
            <Terminal size={14} strokeWidth={2} />
            Терминал
          </TabButton>
          <TabButton active={activeTab === 'preview'} onClick={() => onTabChange('preview')}>
            <Monitor size={14} strokeWidth={2} />
            Сервисы
          </TabButton>
        </div>
      </div>

      {/* Список терминалов */}
      {activeTab === 'terminal' && (
        <div style={{ flex: 1, overflowY: 'auto', padding: '8px 10px' }}>
          {terminals.map(t => (
            <div
              key={t.id}
              onClick={() => onSelectTerminal(t.id)}
              style={{
                display: 'flex', alignItems: 'center', gap: 8,
                padding: '8px 10px', borderRadius: R.md, cursor: 'pointer',
                background: activeTerminalId === t.id ? C.bgSelected : 'transparent',
                marginBottom: 4,
              }}
              onMouseEnter={e => { if (activeTerminalId !== t.id) e.currentTarget.style.background = C.bgInset }}
              onMouseLeave={e => { if (activeTerminalId !== t.id) e.currentTarget.style.background = 'transparent' }}
            >
              {/* Индикатор: зелёный пульс при занятости, зелёный статика когда готов, серый когда остановлен */}
              <Dot color={
                t.status === 'running'
                  ? (activeTerminalId === t.id && terminalBusy ? C.warning : C.success)
                  : C.textMuted
              } />
              {renaming?.id === t.id ? (
                <input
                  autoFocus
                  value={renaming.value}
                  onChange={e => setRenaming({ id: t.id, value: e.target.value })}
                  onClick={e => e.stopPropagation()}
                  onBlur={commitRename}
                  onKeyDown={e => {
                    if (e.key === 'Enter') { e.preventDefault(); commitRename() }
                    else if (e.key === 'Escape') { e.preventDefault(); setRenaming(null) }
                  }}
                  style={{
                    flex: 1, minWidth: 0, fontSize: 13, fontFamily: FONT.sans,
                    color: C.textPrimary, background: C.bgWhite,
                    border: `1px solid ${C.accent}`, borderRadius: 6, padding: '2px 6px', outline: 'none',
                  }}
                />
              ) : (
                <span
                  onDoubleClick={e => { e.stopPropagation(); setRenaming({ id: t.id, value: t.name }) }}
                  title="Двойной клик — переименовать"
                  style={{ flex: 1, fontSize: 13, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                >
                  {t.name}
                </span>
              )}
              <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onStopTerminal(t.id) }} title="Остановить">
                <Square size={10} />
              </IconButton>
            </div>
          ))}
          <Button variant="dashed" size="sm" fullWidth onClick={onCreateTerminal} leftIcon={<Plus size={14} strokeWidth={ICON_STROKE} />}>
            Новый терминал
          </Button>
        </div>
      )}

      {/* Список сервисов Preview */}
      {activeTab === 'preview' && (
        <PreviewServiceList
          projectId={projectId}
          groups={groups}
          hasAny={previewServices.length > 0}
          activePreviewId={activePreviewId}
          onRefreshServices={onRefreshServices}
          onStartService={onStartService}
          onStopService={onStopService}
          onSelectPreview={onSelectPreview}
        />
      )}
    </div>
  )
}

// Список сервисов проекта с группировкой по источникам — экспортирован: его же
// рендерит панелька «Сервисы» нового интерфейса (workspace-cc-panels)
export function PreviewServiceList({
  projectId, groups, hasAny, activePreviewId,
  onRefreshServices, onStartService, onStopService, onSelectPreview,
}: {
  projectId: string
  groups: [string, ProjectService[]][]
  hasAny: boolean
  activePreviewId: string | null
  onRefreshServices: () => void
  onStartService: (svc: ProjectService) => void
  onStopService: (serviceId: string) => void
  onSelectPreview: (serviceId: string) => void
}) {
  const [adding, setAdding] = useState(false)
  // Ссылки внешнего доступа: список сквозной по проектам владельца, поэтому живёт здесь,
  // а не в состоянии проекта
  const { enabled: extEnabled, links: extLinks, refresh: refreshLinks, revoke, revokeAll } = useExternalPreviewLinks()
  const [shareError, setShareError] = useState<string | null>(null)
  const [shareNote, setShareNote] = useState<string | null>(null)
  // Открытые ссылки ЭТОГО проекта — по ним у строк появляется значок и «закрыть»
  const sharedHere = new Map(extLinks.filter(l => l.projectId === projectId).map(l => [l.serviceId, l.jti]))

  const share = useCallback(async (svc: ProjectService) => {
    setShareError(null)
    setShareNote(null)
    // Вкладку открываем СРАЗУ по клику, пустой: после await браузер считает открытие
    // непрошеным и режет его блокировщиком попапов
    const tab = window.open('about:blank', '_blank')
    try {
      const r = await api.projects.previewExternalLink(projectId, svc.id)
      // Адрес нужен центральной панели — сервер его не помнит, повторно не выдать
      saveExternalUrl(projectId, svc.id, r.url)
      if (tab) {
        tab.opener = null
        tab.location.href = r.url
      }
      // Ссылку кладём в буфер молча: показать её второй раз негде — токен живёт в самой
      // ссылке и на сервере не хранится, а на телефон её как-то передать надо
      void navigator.clipboard?.writeText(r.url).catch(() => { /* буфер запрещён — не беда */ })
      if (r.evicted.length > 0) {
        setShareNote('Открытых ссылок стало слишком много — самая старая закрыта.')
      }
      void refreshLinks()
    } catch (e) {
      tab?.close()
      setShareError(e instanceof Error ? e.message : 'Не удалось открыть доступ')
    }
  }, [projectId, refreshLinks])
  // Свёрнутые группы источников: привязаны к проекту — в разных репозиториях свои
  // наборы источников, общий список сворачивал бы то, чего в другом проекте нет
  const [collapsed, setCollapsed] = useState<Set<string>>(() => loadCollapsed(projectId))
  const toggleGroup = useCallback((source: string) => {
    setCollapsed(prev => {
      const next = new Set(prev)
      if (next.has(source)) next.delete(source); else next.add(source)
      saveCollapsed(projectId, next)
      return next
    })
  }, [projectId])
  // Панель в новой раскладке живёт в карточке с шапкой — действия уезжают туда.
  // Старый режим (вкладки этого сайдбара) шапки не имеет: там они остаются в теле.
  const inHeader = useHasPanelHeader()
  // Составные конфигурации показывают состав по именам, а приходят по id
  const allServices = groups.flatMap(([, items]) => items)
  const nameById = new Map(allServices.map(s => [s.id, s.name]))
  const serviceById = new Map(allServices.map(s => [s.id, s]))
  // Кто входит в состав хоть одной составной конфигурации: такие строки рисуются под
  // своей группой, а не отдельно — иначе «All» и три его участника лежат в списке
  // вперемешку, и связь между ними видна только в подсказке
  const memberIds = new Set(allServices.flatMap(s => s.members ?? []))

  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: `${SP.sm}px ${SP.sm}px ${SP.md}px` }}>
      {/* Открытые наружу ссылки — ВСЕ, а не только этого проекта: панель проектная, но
          забытая витрина в соседнем проекте иначе осталась бы невидимой, а именно её
          этот блок и должен ловить */}
      {extEnabled && extLinks.length > 0 && (
        <div style={{
          marginBottom: SP.sm, padding: SP.sm, borderRadius: R.sm, background: C.warningBg,
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginBottom: SP.xs }}>
            <Globe size={12} style={{ flexShrink: 0, color: C.warning }} />
            <span style={{ flex: 1, fontSize: FS.xs, fontWeight: 600, color: C.warningText }}>
              Открыто наружу
            </span>
            <Button size="xs" variant="ghost" onClick={() => { clearAllExternalUrls(); void revokeAll() }}>Закрыть все</Button>
          </div>
          {extLinks.map(l => (
            <div key={l.jti} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: '2px 0' }}>
              <span style={{
                flex: 1, minWidth: 0, fontSize: FS.xs, color: C.warningText,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>
                {nameById.get(l.serviceId) ?? l.serviceId}
                {l.projectId !== projectId && (
                  <span style={{ color: C.textMuted }}> — {l.projectName ?? 'другой проект'}</span>
                )}
              </span>
              <IconButton size="xs" variant="soft" onClick={() => { clearExternalUrl(l.projectId, l.serviceId); void revoke(l.jti) }} title="Закрыть доступ">
                <X size={10} />
              </IconButton>
            </div>
          ))}
        </div>
      )}
      {inHeader ? (
        <>
          <PanelHeaderSlot>
            <IconButton size="xs" variant="soft" onClick={onRefreshServices} title="Обновить список">
              <RefreshCw size={12} />
            </IconButton>
          </PanelHeaderSlot>
          {/* Главное действие панели — в закреплённом слоте: без него на пустой
              панели непонятно, чем её наполнить */}
          <PanelHeaderSlot pinned>
            <Button
              variant="primary" size="xs" title="Добавить свой запуск"
              leftIcon={<Plus size={13} strokeWidth={2} />}
              onClick={() => setAdding(true)}
            >
              Сервис
            </Button>
          </PanelHeaderSlot>
        </>
      ) : (
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: `${SP.xxs}px ${SP.xs}px ${SP.sm}px` }}>
          <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
            Сервисы
          </span>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <IconButton size="xs" variant="soft" onClick={onRefreshServices} title="Обновить список">
              <RefreshCw size={12} />
            </IconButton>
            <Button
              variant="primary" size="xs" title="Добавить свой запуск"
              leftIcon={<Plus size={13} strokeWidth={2} />}
              onClick={() => setAdding(true)}
            >
              Сервис
            </Button>
          </div>
        </div>
      )}

      {!hasAny && (
        <EmptyState compact
          icon={<Monitor size={20} strokeWidth={ICON_STROKE} />}
          title="Сервисы не найдены"
          subtitle={<>Добавьте свой запуск — он сохранится в <code style={{ fontFamily: FONT.mono }}>launch.json</code>.</>}
        />
      )}

      {groups.map(([source, items]) => {
        const isCollapsed = collapsed.has(source)
        return (
          <div key={source}>
            {/* Группа-разделитель — тот же ListDateDivider dense, что рисует разделы
                «Документации»: шеврон, подпись по центру между чертами, клик
                сворачивает. Подложка прилипающая — группа не теряется при скролле
                длинного списка. Фон подложки — В ТОЧНОСТИ фон полотна списка (bgWhite в PanelShell,
                bgPanel в старом сайдбаре): иначе плашка темнее фона и читается
                как постоянная подсветка выбранной группы */}
            <div style={{
              position: 'sticky', top: -(SP.xs + 5), zIndex: 1,
              background: inHeader ? C.bgWhite : C.bgPanel, margin: `0 -${SP.xs}px`, padding: `${SP.xs}px ${SP.xs}px 0 ${SP.sm}px`,
            }}>
              <ListDateDivider
                title={sourceMeta(source).label}
                dense
                onClick={() => toggleGroup(source)}
                titleAttr={`${sourceMeta(source).label} — ${isCollapsed ? 'показать' : 'скрыть'} сервисы`}
                leading={
                  <span style={{ width: 16, flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                    <ChevronRight
                      size={12} strokeWidth={2.4}
                      style={{ color: C.textMuted, transform: isCollapsed ? 'none' : 'rotate(90deg)', transition: 'transform .15s ease' }}
                    />
                  </span>
                }
                trailing={isCollapsed
                  ? <span style={{ flexShrink: 0, fontSize: 10, color: C.textMuted }}>{items.length}</span>
                  : undefined}
              />
            </div>
            {!isCollapsed && items.filter(svc => !memberIds.has(svc.id)).map(svc => {
              const members = svc.members?.map(id => serviceById.get(id)).filter(m => m !== undefined) ?? []
              return (
                <Fragment key={svc.id}>
                  <ServiceRow
                    svc={svc}
                    memberNames={svc.members?.map(id => nameById.get(id) ?? id)}
                    active={activePreviewId === svc.id}
                    onStart={() => onStartService(svc)}
                    onStop={() => onStopService(svc.id)}
                    onSelect={() => onSelectPreview(svc.id)}
                    shared={sharedHere.has(svc.id)}
                    onShare={extEnabled ? () => void share(svc) : undefined}
                    onUnshare={() => {
                      const jti = sharedHere.get(svc.id)
                      if (!jti) return
                      clearExternalUrl(projectId, svc.id)
                      void revoke(jti)
                    }}
                  />
                  {members.map(member => (
                    <ServiceRow
                      key={member.id}
                      svc={member}
                      nested
                      active={activePreviewId === member.id}
                      onStart={() => onStartService(member)}
                      onStop={() => onStopService(member.id)}
                      onSelect={() => onSelectPreview(member.id)}
                      shared={sharedHere.has(member.id)}
                      onShare={extEnabled ? () => void share(member) : undefined}
                      onUnshare={() => {
                        const jti = sharedHere.get(member.id)
                        if (!jti) return
                        clearExternalUrl(projectId, member.id)
                        void revoke(jti)
                      }}
                    />
                  ))}
                </Fragment>
              )
            })}
          </div>
        )
      })}

      {shareError && (
        <div style={{
          marginBottom: SP.sm, padding: SP.sm, borderRadius: R.sm,
          background: C.dangerBg, color: C.dangerText, fontSize: FS.xs, lineHeight: 1.4,
        }}>
          {shareError}
        </div>
      )}

      {shareNote && (
        <div style={{
          marginBottom: SP.sm, padding: SP.sm, borderRadius: R.sm,
          background: C.warningBg, color: C.warningText, fontSize: FS.xs, lineHeight: 1.4,
        }}>
          {shareNote}
        </div>
      )}

      {adding && (
        <AddServiceDialog
          projectId={projectId}
          onClose={() => setAdding(false)}
          onSaved={onRefreshServices}
        />
      )}
    </div>
  )
}

// Свёрнутые группы источников в localStorage: per-project (в другой репе другие источники)
const collapsedKey = (projectId: string) => `cc_services_collapsed_${projectId}`
const loadCollapsed = (projectId: string): Set<string> => {
  try {
    const raw = localStorage.getItem(collapsedKey(projectId))
    return new Set(raw ? JSON.parse(raw) as string[] : [])
  } catch { return new Set() }
}
const saveCollapsed = (projectId: string, value: Set<string>) => {
  try { localStorage.setItem(collapsedKey(projectId), JSON.stringify([...value])) } catch { /* ignore */ }
}

function ServiceRow({ svc, memberNames, active, onStart, onStop, onSelect, shared, onShare, onUnshare, nested }: {
  svc: ProjectService
  memberNames?: string[]
  // Строка — участник составной конфигурации: рисуется под ней со сдвигом и направляющей
  nested?: boolean
  active: boolean
  onStart: () => void
  onStop: () => void
  onSelect: () => void
  // Открыт ли сервис наружу по ссылке и можно ли это менять (фича включена на сервере)
  shared?: boolean
  onShare?: () => void
  onUnshare?: () => void
}) {
  const [hover, setHover] = useState(false)
  const rowRef = useRef<HTMLDivElement>(null)
  // «Стоп» — только у полностью живого сервиса. У частично поднятой группы кнопка
  // остаётся «Запустить»: она доподнимет недостающих участников
  const running = svc.status === 'started' || svc.status === 'starting'
  const partial = svc.status === 'partial'
  // Порт слушает процесс, поднятый вне продукта: показать в превью можем, запускать
  // нечего (упадёт с «порт занят»), останавливать не наше дело
  const external = svc.status === 'external'
  const port = svc.runningPort ?? svc.suggestedPort
  const cmd = memberNames?.length
    ? memberNames.join(' + ')
    : svc.command ? `${svc.command} ${svc.args.join(' ')}`.trim() : svc.name
  const note = external ? 'запущен снаружи' : partial ? 'запущена часть' : ''
  const dotColor = statusColor(svc.status)
  // Словесный статус — из той же таблице смыслов, что цвета: точка мгновенна, но
  // бессловесна (жёлтое — это запуск или часть?), подпись закрывает неоднозначность
  const statusLabel = running ? 'запущен' : svc.status === 'starting' ? 'запускается' : svc.status === 'error' ? 'ошибка' : ''

  // Словесный статус для карточки: у точки есть цвет, но нет текста — в карточке
  // нужен полный смысл, в том числе у зелёного и серого
  const statusText = running && svc.status === 'started' ? 'запущен'
    : svc.status === 'starting' ? 'запускается'
    : svc.status === 'error' ? 'ошибка запуска'
    : external ? 'запущен снаружи'
    : partial ? 'запущена часть участников'
    : 'остановлен'

  return (
    <div
      ref={rowRef}
      onClick={() => { if (running || external || partial) onSelect() }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs,
        padding: `${SP.xxs}px ${SP.sm}px`,
        borderRadius: R.md,
        border: 'none',
        // Участник смещён и подпёрт направляющей: одного отступа мало, чтобы вложенность
        // читалась с первого взгляда, а линия делает её однозначной
        marginLeft: nested ? SP.md : 0,
        width: nested ? `calc(100% - ${SP.md}px)` : '100%',
        borderLeft: nested ? `1px solid ${C.borderLight}` : undefined,
        cursor: running || external || partial ? 'pointer' : 'default',
        background: active ? C.bgSelected : hover ? C.bgInset : 'transparent',
      }}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 0, minWidth: 0, flex: 1 }}>
        {/* Первая строка: статус + имя + порт. Порт всегда справа и не обрезается —
            это адрес, по которому сервис открывается, он важнее хвоста команды */}
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
          <Dot color={dotColor} />
          <span style={{
            fontFamily: FONT.sans, fontSize: FS.base, minWidth: 0, flex: 1,
            color: active || hover ? C.textHeading : C.textPrimary,
            fontWeight: active ? 600 : 400,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {svc.name}
          </span>
          {statusLabel && (
            <span style={{
              fontFamily: FONT.sans, fontSize: FS.xs, flexShrink: 0,
              // Цветом того же статуса, что точка: «запускается» — жёлтым, «ошибка» — красным
              color: svc.status === 'error' ? C.danger : C.warning,
            }}>
              {statusLabel}
            </span>
          )}
          {port !== null && (
            <span style={{
              fontFamily: FONT.mono, fontSize: FS.xs, flexShrink: 0,
              color: C.textMuted,
            }}>
              :{port}
            </span>
          )}
        </div>
        {/* Вторая строка: команда (или состав группы) + нештатные пометки, с ellipsis */}
        <div style={{
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
          paddingLeft: 8 + SP.xs, // под выравнивание по началу имени (ширина Dot)
          minWidth: 0,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {cmd}{note && (
            <span style={{ color: external ? C.info : C.warning }}> — {note}</span>
          )}
        </div>
      </div>
      {/* Открыт наружу — видно всегда, а не только под курсором: забытая витрина
          и есть то, что этот значок должен ловить */}
      {shared && (
        <Globe size={12} style={{ flexShrink: 0, color: C.warning }} aria-label="открыт наружу" />
      )}
      {hover && onShare && (running || external) && (
        shared
          ? (
            <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onUnshare?.() }} title="Закрыть доступ снаружи">
              <GlobeLock size={12} />
            </IconButton>
          )
          : (
            <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onShare() }} title="Открыть снаружи">
              <Globe size={12} />
            </IconButton>
          )
      )}
      {hover && (
        external ? (
          <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onSelect() }} title="Показать страницу">
            <Monitor size={12} />
          </IconButton>
        ) : running ? (
          <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onStop() }} title="Остановить">
            <Square size={10} />
          </IconButton>
        ) : (
          <IconButton size="xs" variant="soft" onClick={e => { e.stopPropagation(); onStart() }} title="Запустить">
            <Play size={12} />
          </IconButton>
        )
      )}
      {hover && <ServiceHoverCard anchorRef={rowRef} svc={svc} cmd={cmd} statusText={statusText} port={port} note={note} external={external} />}
    </div>
  )
}

// Богатая подсказка строки сервиса: обе строки целиком (то, что режет ellipsis),
// статус словами, адрес приметно. Порталом — панель с overflow не должна обрезать
// плашку. Язык тот же, что у HoverCard задачи: белая карточка, dropdown-тень
function ServiceHoverCard({ anchorRef, svc, cmd, statusText, port, note, external }: {
  anchorRef: React.RefObject<HTMLDivElement | null>
  svc: ProjectService
  cmd: string
  statusText: string
  port: number | null
  note: string
  external: boolean
}) {
  const rect = anchorRef.current?.getBoundingClientRect()
  if (!rect) return null
  const WIDTH = 320
  // Справа от строки, если не влезает — слева (как HoverCard задачи)
  const fitsRight = rect.right + WIDTH + 20 <= window.innerWidth
  const left = fitsRight
    ? rect.right + 10
    : Math.max(12, rect.left - WIDTH - 10)
  const top = Math.max(12, Math.min(rect.top, window.innerHeight - 180 - 12))
  return createPortal(
    <div style={{
      position: 'fixed', left, top, width: WIDTH, boxSizing: 'border-box',
      background: C.bgWhite, border: `1px solid ${C.border}`,
      borderRadius: R.xl, boxShadow: SHADOW.dropdown,
      padding: '10px 14px', zIndex: Z.dropdown,
      pointerEvents: 'none',
    }}>
      {/* Заголовок: точка-статус + имя + статус словами */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginBottom: SP.xs }}>
        <Dot color={statusColor(svc.status)} />
        <span style={{
          fontFamily: FONT.sans, fontSize: FS.base, fontWeight: 600, flex: 1, minWidth: 0,
          color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {svc.name}
        </span>
        <span style={{
          fontFamily: FONT.sans, fontSize: FS.xs,
          color: svc.status === 'error' ? C.danger : svc.status === 'started' ? C.success : external ? C.info : C.warning,
        }}>
          {statusText}
        </span>
      </div>
      {/* Адрес: моно, prominently — по нему сервис открывается */}
      {port !== null && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginBottom: SP.xs }}>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textPrimary }}>
            localhost:{port}
          </span>
          {svc.runningPort !== null && svc.suggestedPort !== null && svc.runningPort !== svc.suggestedPort && (
            <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}>
              · вместо :{svc.suggestedPort}
            </span>
          )}
        </div>
      )}
      {/* Команда или состав группы целиком */}
      <div style={{
        fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
        background: C.bgInset, borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
        overflowWrap: 'anywhere',
      }}>
        {cmd}
      </div>
      {note && (
        <div style={{
          fontFamily: FONT.sans, fontSize: FS.xs, marginTop: SP.xs,
          color: external ? C.info : C.warning,
        }}>
          {note}
        </div>
      )}
      {/* Текст ошибки — целиком: причина падения важнее всего остального */}
      {svc.error && (
        <div style={{
          fontFamily: FONT.mono, fontSize: FS.xs, marginTop: SP.xs, color: C.danger,
          background: C.bgInset, borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
          maxHeight: 80, overflowY: 'auto', overflowWrap: 'anywhere',
        }}>
          {svc.error}
        </div>
      )}
    </div>,
    document.body,
  )
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick} style={{
      flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6,
      padding: '6px 10px', borderRadius: R.sm, border: 'none', cursor: 'pointer',
      fontSize: 12, fontWeight: 600,
      background: active ? C.bgWhite : 'transparent',
      color: active ? C.textHeading : C.textSecondary,
      fontFamily: FONT.sans,
    }}>
      {children}
    </button>
  )
}
