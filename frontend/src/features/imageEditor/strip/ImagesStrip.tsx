// Полоса «Картинки» над композером (реестр composer-strip; прототип полос, вариант C —
// docs/mockups/image-editor-v3-strips-prototype.html). Одна строка высотой как у git-полосы:
// переключатель от хоста, чип «Работаем с: …» (или «Нарисовать новую»), кнопка-сводка
// «поставщик · модель · N вар. · цена ▾» и аватар персонажа. Настройки — карточкой над
// полосой (на телефоне шторкой): поставщик, модель, число вариантов, персонаж, образцы,
// «Размер оригинала». Свёрнутая — строка 30 px со сводкой, клик разворачивает.
// В личном чате вне проекта (область personal) нет персонажа и образцов из проекта: их
// источник — папки проекта.

import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  AlertTriangle, ChevronDown, ChevronUp, FolderOpen, Image as ImageIcon, Minus, Plus, SlidersHorizontal, Sparkles, Upload, User, X,
} from 'lucide-react';
import {
  Button, Checkbox, Chip, IconButton, Menu, MenuItem, Modal, C, FS, R, SHADOW, SP, Z, ICON_SIZE, ICON_STROKE, api as appApi,
} from 'aihome_shell/kit';
import type { ComposerStripCtx } from '../../../lib/subsystems/registryCore';
import { AUTO_MODEL, imageEditorApi, type ImageEditCatalog, type ReferenceRole } from '../api';
import { CharactersPanel } from '../characters/CharactersPanel';
import { CHARACTERS_PANEL, revealWorkspacePanel } from '../characters/panel';
import { useCharacters } from '../characters/useCharacters';
import { maxSamples, roleShort, SAMPLE_ROLES, type Sample } from '../editorInputs';
import { effectiveProvider, modelBlockReason, providerHint, unavailableMark, variantsWord } from '../format';
import { ProjectImagePicker } from '../PanelSections';
import { createDraft, releaseFocus } from '../thread/actions';
import { enterScope, isPersonalScope } from '../scope';
import { focusLabel } from '../thread/model';
import { setPrefs } from '../thread/prefs';
import { getFocusedThread, getSamples, setSamples, useThreads } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';
import { activeSrc, launchSummaryParts, threadHasImage, useThreadLaunch } from '../thread/useThreadLaunch';
import { stripSummary } from './summary';

const ic = (I: typeof User, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

type Launch = ReturnType<typeof useThreadLaunch>;

function Thumb({ src, round }: { src: string | null; round?: boolean }) {
  if (!src) return null;
  return (
    <span style={{ display: 'inline-flex', width: 20, height: 20, flexShrink: 0, borderRadius: round ? R.full : R.sm, overflow: 'hidden', background: C.bgInset }}>
      <img src={src} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
    </span>
  );
}

function Label({ children }: { children: ReactNode }) {
  return (
    <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4, margin: `${SP.md}px 0 ${SP.xs}px` }}>
      {children}
    </div>
  );
}

// Выбор в карточке настроек: имя и подсказка в две строки
function Opt({ on, name, hint, disabled, title, onClick }: {
  on: boolean; name: string; hint?: ReactNode; disabled?: boolean; title?: string; onClick: () => void;
}) {
  return (
    <Button size="sm" variant={on ? 'ghostAccent' : 'secondary'} disabled={disabled} title={title} onClick={onClick}
      style={{ height: 'auto', padding: `${SP.xs}px ${SP.md}px`, border: `1px solid ${on ? C.accent : C.border}`, textAlign: 'left' }}>
      <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: 1, fontWeight: 400 }}>
        <span>{name}</span>
        {hint && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</span>}
      </span>
    </Button>
  );
}

// projectId = null — личная область: персонажей нет, запросов к проекту тоже
function useCharacter(projectId: string | null, slug: string | null) {
  const { list } = useCharacters(projectId);
  const current = projectId && slug ? list?.find(c => c.slug === slug) ?? null : null;
  const photo = projectId && current?.photos[0] ? imageEditorApi().characterPhotoUrl(projectId, current.slug, current.photos[0].file) : null;
  return { current, photo, name: current?.name ?? slug };
}

function openCharacters(isMobile: boolean, sheet: () => void) {
  if (isMobile) sheet();
  else revealWorkspacePanel(CHARACTERS_PANEL);
}

// Образцы: чипы с миниатюрой, клик — роль, «+ Образец» — с компьютера или из проекта
// (у личной области — сразу с компьютера)
function SampleChips({ projectId, max }: { projectId: string; max: number }) {
  const personal = isPersonalScope(projectId);
  const samples = getSamples(projectId);
  const [roleFor, setRoleFor] = useState<string | null>(null);
  const [addAt, setAddAt] = useState<DOMRect | null>(null);
  const [picker, setPicker] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const id = () => `s${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  const add = (more: Sample[]) => setSamples(projectId, [...samples, ...more].slice(0, max));
  const roleOf = roleFor ? samples.find(s => s.id === roleFor) ?? null : null;

  return (
    <>
      {samples.map(s => (
        <span key={s.id} data-sample={s.name} style={{ display: 'inline-flex' }}>
          <Chip leading={<img src={s.url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />} maxW={170}
            title={`Образец · ${roleShort(s.role).toLowerCase()} — нажмите, чтобы сменить роль`}
            onClick={() => setRoleFor(s.id)}
            onRemove={() => setSamples(projectId, samples.filter(x => x.id !== s.id))}>
            {roleShort(s.role)} · {s.name}
          </Chip>
        </span>
      ))}
      {samples.length < max && (
        <span style={{ display: 'inline-flex' }}
          onClick={e => { if (personal) input.current?.click(); else setAddAt((e.currentTarget as HTMLElement).getBoundingClientRect()); }}>
          <Chip dashed leading={ic(Plus)} title="Картинка-пример для модели: лицо, стиль или предмет">Образец</Chip>
        </span>
      )}
      {addAt && (
        <Menu anchor={addAt} minWidth={230} onClose={() => setAddAt(null)}>
          <MenuItem icon={ic(Upload, ICON_SIZE.sm)} label="С компьютера" onClick={() => { setAddAt(null); input.current?.click(); }} />
          <MenuItem icon={ic(FolderOpen, ICON_SIZE.sm)} label="Из файлов проекта…" onClick={() => { setAddAt(null); setPicker(true); }} />
        </Menu>
      )}
      {roleOf && (
        <Modal title={`Как модели использовать «${roleOf.name}»`} width={380} onClose={() => setRoleFor(null)}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            {SAMPLE_ROLES.map(([role, full]) => (
              <Button key={role} size="sm" fullWidth variant={roleOf.role === role ? 'ghostAccent' : 'ghost'} style={{ justifyContent: 'flex-start' }}
                onClick={() => { setSamples(projectId, samples.map(x => (x.id === roleOf.id ? { ...x, role: role as ReferenceRole } : x))); setRoleFor(null); }}>
                {full}
              </Button>
            ))}
          </div>
        </Modal>
      )}
      {picker && !personal && (
        <ProjectImagePicker projectId={projectId}
          taken={samples.flatMap(s => (s.source === 'project' ? [s.path] : []))}
          onPick={path => {
            add([{ id: id(), source: 'project', name: path.split('/').pop() ?? path, role: 'style', path, url: appApi.files.fileUrl(projectId, path) }]);
            setPicker(false);
          }}
          onClose={() => setPicker(false)} />
      )}
      <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" multiple hidden
        onChange={e => {
          const files = [...(e.target.files ?? [])];
          e.target.value = '';
          if (files.length) add(files.map(f => ({ id: id(), source: 'upload', name: f.name, role: 'style', file: f, url: URL.createObjectURL(f) })));
        }} />
    </>
  );
}

// Подсказка с пометкой лежащего поставщика отдельным span цвета предупреждения
function joinHint(hint: string, warn: string, warnFirst: boolean): ReactNode {
  if (!warn) return hint;
  const w = <span key="w" style={{ color: C.warningText }}>{warn}</span>;
  if (!hint) return w;
  return warnFirst ? <>{w} · {hint}</> : <>{hint} · {w}</>;
}

// Лежащий поставщик выбирается как обычный — пометка лишь предупреждает заранее
export function ProviderOpts({ catalog, choice, onPick }: {
  catalog: ImageEditCatalog; choice: string; onPick: (provider: string | null) => void;
}) {
  const admin = catalog.providers.find(p => p.key === catalog.default.provider);
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
      {admin && (
        <Opt on={choice === 'settings'} name="Как в настройках"
          hint={joinHint(`сейчас ${admin.label}`, unavailableMark(admin), false)}
          onClick={() => onPick(null)} />
      )}
      {catalog.providers.map(p => (
        <Opt key={p.key} on={choice === p.key} name={p.label}
          hint={joinHint(providerHint(p), unavailableMark(p), true)}
          onClick={() => onPick(p.key)} />
      ))}
    </div>
  );
}

// Карточка настроек генерации — над полосой на десктопе и шторкой на телефоне
function SettingsPanel({ projectId, L, catalog, isMobile, thread }: {
  projectId: string; L: Launch; catalog: ImageEditCatalog; isMobile: boolean; thread: ImageThread | null;
}) {
  const [charSheet, setCharSheet] = useState(false);
  const choice = L.settings.provider ?? 'settings';
  const pv = effectiveProvider(catalog, choice);
  const count = L.settings.count;
  const maxCount = L.model?.caps?.maxCount ?? catalog.limits.maxCount ?? 4;
  const personal = isPersonalScope(projectId);
  const { current, photo, name } = useCharacter(personal ? null : projectId, L.prefs.characterSlug);
  const max = maxSamples(catalog.limits.maxReferences, L.model?.caps?.maxReferences);

  return (
    <div data-image-settings="" style={{ fontSize: FS.sm }}>
      <Label>Поставщик</Label>
      <ProviderOpts catalog={catalog} choice={choice} onPick={provider => L.setSettings({ provider, model: null })} />
      {pv && (
        <>
          <Label>Модель</Label>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
            {pv.models.map(m => {
              const why = modelBlockReason(m, L.hasImage, L.hasMask);
              return (
                <Opt key={m.id} on={m.id === L.model?.id} name={m.label} disabled={!!why} title={why || undefined}
                  hint={why || (m.id === AUTO_MODEL ? 'подберём под задачу' : undefined)}
                  onClick={() => L.setSettings({ model: m.id })} />
              );
            })}
          </div>
        </>
      )}
      <Label>Варианты и цена</Label>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite }}>
          <IconButton size="xs" title="Меньше вариантов" ariaLabel="Меньше вариантов" disabled={count <= 1}
            onClick={() => L.setSettings({ count: count - 1 })}>{ic(Minus)}</IconButton>
          <span data-image-count="" style={{ minWidth: 14, textAlign: 'center', fontWeight: 600, color: C.textPrimary }}>{count}</span>
          <IconButton size="xs" title="Больше вариантов" ariaLabel="Больше вариантов" disabled={count >= maxCount}
            onClick={() => L.setSettings({ count: count + 1 })}>{ic(Plus)}</IconButton>
        </span>
        <span data-image-price="" style={{ color: C.textSecondary }}>{variantsWord(count)} · {L.price ?? L.priceLabel}</span>
      </div>
      {(!personal || max > 0) && <Label>{personal ? 'Образцы' : 'Персонаж и образцы'}</Label>}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
        {personal ? null : L.prefs.characterSlug
          ? <Chip selected leading={photo ? <img src={photo} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : undefined}
              maxW={160} title="Фото персонажа уходят в каждую генерацию" onClick={() => openCharacters(isMobile, () => setCharSheet(true))}
              onRemove={() => setPrefs(projectId, { characterSlug: null })}>
              {current?.name ?? name}
            </Chip>
          : <Chip dashed leading={ic(User)} title="Персонажи проекта" onClick={() => openCharacters(isMobile, () => setCharSheet(true))}>Персонаж</Chip>}
        {max > 0 && <SampleChips projectId={projectId} max={max} />}
      </div>
      {thread && threadHasImage(thread) && (
        <>
          <Label>Размер</Label>
          <label style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, cursor: 'pointer', color: C.textPrimary }}>
            <Checkbox checked={L.settings.matchSourceSize} onChange={v => L.setSettings({ matchSourceSize: v })} ariaLabel="Размер оригинала" />
            Вернуть в размере оригинала
          </label>
        </>
      )}
      {L.blocked && L.model && (
        <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', marginTop: SP.md, padding: `${SP.xs}px ${SP.sm}px`, background: C.warningBg, color: C.warningText, borderRadius: R.md }}>
          {ic(AlertTriangle)}<span>{L.model.label}: {L.blocked.charAt(0).toLowerCase() + L.blocked.slice(1)}</span>
        </div>
      )}
      {charSheet && !personal && (
        <Modal title="Персонажи" onClose={() => setCharSheet(false)}>
          <CharactersPanel projectId={projectId} />
        </Modal>
      )}
    </div>
  );
}

export function ImagesStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { sessionId, isMobile, collapsed, setCollapsed, switcher } = ctx;
  const projectId = enterScope(ctx.projectId, sessionId);
  const personal = isPersonalScope(projectId);
  const state = useThreads(projectId, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const L = useThreadLaunch(projectId, sessionId, thread);
  const [drafting, setDrafting] = useState(false);
  const [open, setOpen] = useState(false);
  const [charSheet, setCharSheet] = useState(false);
  const shell = useRef<HTMLDivElement>(null);
  const { current: character, photo } = useCharacter(personal ? null : projectId, L.prefs.characterSlug);

  // Карточка настроек закрывается кликом мимо полосы и Esc
  useEffect(() => {
    if (!open || isMobile) return;
    const onDown = (e: PointerEvent) => {
      const t = e.target as Node | null;
      // Меню и диалоги карточки рисуются порталом — клик по ним не «мимо»
      if (t && (shell.current?.contains(t) || (t as Element).closest?.('[role="dialog"], [role="menu"]'))) return;
      setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('pointerdown', onDown);
    window.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('pointerdown', onDown); window.removeEventListener('keydown', onKey); };
  }, [open, isMobile]);

  const draw = async () => {
    if (!sessionId) return;
    setDrafting(true);
    await createDraft(projectId, sessionId, '');
    setDrafting(false);
  };
  const release = () => { if (sessionId) void releaseFocus(projectId, sessionId, thread); };
  const src = thread ? activeSrc(projectId, thread) : null;
  const parts = {
    focus: thread ? focusLabel(thread, false, personal) : null, provider: L.provider?.label ?? null, model: L.model?.label ?? null,
    count: L.settings.count, price: L.price ?? null, character: character?.name ?? null,
  };
  const title = switcher ?? (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
      {ic(ImageIcon)}Картинки
    </span>
  );

  if (collapsed) {
    return (
      <div role="button" tabIndex={0} data-composer-strip="images" data-images-strip="mini" title="Развернуть полосу «Картинки»"
        onClick={() => setCollapsed(false)}
        onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setCollapsed(false); } }}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, height: 30, margin: '4px 0 6px', padding: '0 6px 0 4px',
          boxSizing: 'border-box', minWidth: 0, cursor: 'pointer',
          background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.lg,
        }}>
        {title}
        <Thumb src={src} />
        <span data-images-summary="" style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {stripSummary(parts)}
        </span>
        {thread && (
          <span style={{ display: 'inline-flex' }} onClick={e => e.stopPropagation()}>
            <IconButton size="xs" title="Снять выбор картинки" ariaLabel="Снять выбор картинки" onClick={release}>{ic(X)}</IconButton>
          </span>
        )}
        <span style={{ display: 'inline-flex', color: C.textMuted }}>{ic(ChevronUp, ICON_SIZE.sm)}</span>
      </div>
    );
  }

  const noProviders = L.catalog && !L.catalog.providers.length;
  const settings = L.catalog && !noProviders
    ? <SettingsPanel projectId={projectId} L={L} catalog={L.catalog} isMobile={isMobile} thread={thread} /> : null;

  return (
    <div ref={shell} data-composer-strip="images" data-images-strip="full" style={{
      position: 'relative', display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 8, boxSizing: 'border-box', minWidth: 0,
      height: isMobile ? 44 : 51, margin: isMobile ? '6px 0' : '10px 0 8px', padding: isMobile ? '0 6px' : '0 8px',
      background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
    }}>
      {title}
      {thread ? (
        <span style={{ display: 'inline-flex', minWidth: 72, flex: '0 1 auto' }}>
          <Chip selected leading={src ? <img src={src} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : ic(Sparkles)}
            maxW="100%" title={personal ? 'Режим «Картинка» работает с этой версией. ✕ — снять выбор' : 'Режим «Картинка» и Claude работают с этой версией. ✕ — снять выбор'} onRemove={release}>
            {!isMobile && 'Работаем с: '}<b>{focusLabel(thread, true, personal)}</b>
          </Chip>
        </span>
      ) : (
        <span style={{ display: 'inline-flex', flexShrink: 0 }}>
          <Chip dashed leading={ic(Sparkles)} title={sessionId ? 'Карточка «Новая картинка» в ленте, поле — в режим «Картинка»' : 'Сначала начните чат'}
            onClick={sessionId && !drafting ? () => { void draw(); } : undefined}>
            Нарисовать новую
          </Chip>
        </span>
      )}
      <span style={{ flex: 1 }} />
      {noProviders ? (
        <span style={{ fontSize: FS.xs, color: C.textMuted, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          Рисовать нечем: поставщиков не настроил администратор
        </span>
      ) : (
        <span data-images-settings-toggle="" style={{ display: 'inline-flex', minWidth: 0, flex: '0 1 auto' }}>
          <Button size="xs" variant="secondary"
            leftIcon={ic(SlidersHorizontal)} title="Настройки генерации"
            onClick={() => setOpen(v => !v)}
            style={{ minWidth: 0, flex: '0 1 auto', height: 28, border: `1px solid ${open ? C.accent : C.border}`, background: C.bgWhite }}>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
              <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {isMobile ? `${L.settings.count} вар.` : stripSummary(parts, true)}
              </span>
              {L.blocked && <span style={{ display: 'inline-flex', color: C.warningText }}>{ic(AlertTriangle)}</span>}
              {ic(ChevronDown)}
            </span>
          </Button>
        </span>
      )}
      {!isMobile && !personal && (L.prefs.characterSlug
        ? (
          <IconButton size="sm" title={`Персонаж: ${character?.name ?? L.prefs.characterSlug} — фото уходят в запрос`}
            ariaLabel="Персонаж" onClick={() => revealWorkspacePanel(CHARACTERS_PANEL)}
            style={{ padding: 0, borderRadius: R.full, border: `1.5px solid ${C.accent}`, overflow: 'hidden' }}>
            {photo ? <img src={photo} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} /> : ic(User)}
          </IconButton>
        )
        : (
          <IconButton size="sm" title="Подключить персонажа" ariaLabel="Подключить персонажа"
            onClick={() => openCharacters(isMobile, () => setCharSheet(true))}>{ic(User)}</IconButton>
        ))}
      <IconButton size="sm" title="Свернуть полосу в строку" ariaLabel="Свернуть полосу в строку" onClick={() => setCollapsed(true)}>
        {ic(ChevronDown, ICON_SIZE.sm)}
      </IconButton>

      {open && settings && !isMobile && (
        <div data-images-settings-card="" style={{
          position: 'absolute', right: 0, bottom: 'calc(100% + 6px)', width: 380, maxWidth: 'calc(100vw - 40px)', zIndex: Z.dropdown,
          boxSizing: 'border-box', padding: `${SP.xs}px ${SP.lg}px ${SP.md}px`, cursor: 'default',
          background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xxl, boxShadow: SHADOW.dropdown,
        }}>
          {settings}
        </div>
      )}
      {open && settings && isMobile && (
        <Modal title="Настройки генерации" onClose={() => setOpen(false)}>{settings}</Modal>
      )}
      {charSheet && !personal && (
        <Modal title="Персонажи" onClose={() => setCharSheet(false)}>
          <CharactersPanel projectId={projectId} />
        </Modal>
      )}
    </div>
  );
}

// Строка состояния в меню переключателя полос — та же сводка, что у свёрнутой строки
export function imagesStripStatus(projectId: string | null, sessionId: string | null): string {
  const scope = enterScope(projectId, sessionId);
  const t = getFocusedThread(sessionId);
  return stripSummary({ focus: t ? focusLabel(t, false, isPersonalScope(scope)) : null, character: null, ...launchSummaryParts(scope, t) });
}
