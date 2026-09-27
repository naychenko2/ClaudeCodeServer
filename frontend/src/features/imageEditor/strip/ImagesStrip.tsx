// Полоса «Картинки» над композером (реестр composer-strip, записка v3 «Полоса
// «Картинки» — состав»). Первая строка — переключатель полос от хоста и чип «Работаем
// с: …» (или «Нарисовать новую» без выбора), вторая — чем рисовать, число вариантов,
// цена, персонаж и «Размер оригинала». Свёрнутая — одна строка 30 px.

import { useEffect, useState, type MouseEvent, type ReactNode } from 'react';
import { AlertTriangle, Minus, Plus, Sparkles, User } from 'lucide-react';
import {
  Button, Checkbox, Chip, IconButton, Menu, MenuItem, C, FS, R, SP, ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import type { ComposerStripCtx } from '../../../lib/subsystems/registryCore';
import { AUTO_MODEL, imageEditorApi, type ImageEditCharacter } from '../api';
import { ProviderModelPicker } from '../ProviderModelPicker';
import { createDraft, releaseFocus } from '../thread/actions';
import { focusLabel, isEmptyThread } from '../thread/model';
import { setPrefs } from '../thread/prefs';
import { getFocusedThread, useThreads } from '../thread/threadStore';
import { useThreadLaunch } from '../thread/useThreadLaunch';

const ic = (I: typeof User, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// Персонажи проекта: один запрос на проект за вкладку
const charLists = new Map<string, Promise<ImageEditCharacter[]>>();
function useCharacterList(projectId: string, open: boolean) {
  const [list, setList] = useState<ImageEditCharacter[] | null>(null);
  useEffect(() => {
    let alive = true;
    let p = charLists.get(projectId);
    if (!p) {
      p = imageEditorApi().listCharacters(projectId).catch(() => { charLists.delete(projectId); return []; });
      charLists.set(projectId, p);
    }
    void p.then(l => { if (alive) setList(l); });
    return () => { alive = false; };
  }, [projectId, open]);
  return list;
}

// Персонаж — на проект: остаётся подключённым при смене картинки, чип на миг подсвечивается
function CharacterChip({ projectId, slug, focusKey }: { projectId: string; slug: string | null; focusKey: string | null }) {
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const list = useCharacterList(projectId, !!menu);
  const [flash, setFlash] = useState(false);
  const [seenFocus, setSeenFocus] = useState(focusKey);
  if (seenFocus !== focusKey) {
    setSeenFocus(focusKey);
    if (slug && focusKey) setFlash(true);
  }
  useEffect(() => {
    if (!flash) return;
    const t = setTimeout(() => setFlash(false), 1200);
    return () => clearTimeout(t);
  }, [flash]);
  const current = list?.find(c => c.slug === slug) ?? null;
  const avatar = current?.photos[0]
    ? <img src={imageEditorApi().characterPhotoUrl(projectId, current.slug, current.photos[0].file)} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
    : undefined;
  const open = (e: MouseEvent) => setMenu((e.currentTarget as HTMLElement).getBoundingClientRect());
  return (
    <span style={{ display: 'inline-flex' }} onClick={slug ? undefined : open}>
      {slug
        ? <Chip selected={flash} leading={avatar} maxW={160} title="Уходит в каждую генерацию" onRemove={() => setPrefs(projectId, { characterSlug: null })}>
            {current?.name ?? slug}
          </Chip>
        : <Chip dashed leading={ic(User)} title="Подключить персонажа проекта">Персонаж</Chip>}
      {menu && (
        <Menu anchor={menu} onClose={() => setMenu(null)} minWidth={220}>
          {list === null && <MenuItem label="Загружаем…" disabled onClick={() => {}} />}
          {list?.length === 0 && <MenuItem label="В проекте пока нет персонажей" disabled onClick={() => {}} />}
          {list?.map(c => (
            <MenuItem key={c.slug} label={`${c.name} · ${c.photos.length} фото`}
              onClick={() => { setMenu(null); setPrefs(projectId, { characterSlug: c.slug }); }} />
          ))}
        </Menu>
      )}
    </span>
  );
}

export function ImagesStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { projectId, sessionId, isMobile, collapsed, switcher } = ctx;
  const state = useThreads(projectId, sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  const L = useThreadLaunch(projectId, sessionId, thread);
  const [drafting, setDrafting] = useState(false);

  const draw = async () => {
    if (!sessionId) return;
    setDrafting(true);
    await createDraft(projectId, sessionId, '');
    setDrafting(false);
  };
  const release = () => { if (sessionId) void releaseFocus(projectId, sessionId, thread); };

  const [mobileOpen, setMobileOpen] = useState(false);
  const summary = [L.provider?.label, L.model?.label, L.priceLabel].filter(Boolean).join(' · ');
  const focusChip = thread && (
    <Chip selected maxW={isMobile ? 190 : 320} title="К этой картинке относится режим «Картинка» и просьбы агенту" onRemove={release}>
      Работаем с: {focusLabel(thread)}
    </Chip>
  );

  const row = (children: ReactNode, h: number) => (
    <div data-composer-strip="images" style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: h, flexWrap: 'wrap',
      padding: `${SP.xxs}px ${SP.sm}px`, fontSize: FS.sm, color: C.textSecondary,
    }}>
      {children}
    </div>
  );

  // Телефон: полоса скрыта, пока картинка не выбрана; выбранная — строкой, тап разворачивает
  if (isMobile && !thread) return null;
  if (collapsed || (isMobile && !mobileOpen)) {
    return row(
      <>
        {switcher}
        {thread ? focusChip : <span style={{ color: C.textMuted }}>Картинка не выбрана</span>}
        <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: C.textMuted }}>{summary}</span>
        {isMobile && !collapsed && (
          <Button size="xs" variant="ghost" onClick={() => setMobileOpen(true)}>Настроить</Button>
        )}
      </>, 30,
    );
  }

  const count = L.settings.count;
  const maxCount = L.model?.caps?.maxCount ?? L.catalog?.limits.maxCount ?? 4;
  const withSteps = !!thread && !isEmptyThread(thread);

  return (
    <div style={{ borderTop: `1px solid ${C.borderLight}`, background: C.bgPanel }}>
      {row(
        <>
          {switcher}
          {thread ? focusChip : (
            <>
              <span style={{ color: C.textMuted, minWidth: 0 }}>Выберите картинку в ленте или нарисуйте новую</span>
              <Button size="xs" variant="ghostAccent" leftIcon={ic(Sparkles)} loading={drafting} disabled={!sessionId}
                title={sessionId ? undefined : 'Сначала начните чат'} onClick={() => { void draw(); }}>
                Нарисовать новую
              </Button>
            </>
          )}
        </>, 36,
      )}
      {L.catalog && (L.catalog.providers.length ? row(
        <>
          <ProviderModelPicker catalog={L.catalog} provider={L.settings.provider ?? 'settings'}
            model={L.settings.model ?? L.model?.id ?? AUTO_MODEL}
            onProvider={p => L.setSettings({ provider: p === 'settings' ? null : p, model: null })}
            onModel={m => L.setSettings({ model: m })}
            hasImage={L.hasImage} hasMask={L.hasMask} priceLabel={L.priceLabel} mobile={isMobile} />
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, border: `1px solid ${C.border}`, borderRadius: R.full, padding: `0 ${SP.xxs}px` }}>
            <IconButton size="xs" title="Меньше вариантов" ariaLabel="Меньше вариантов" disabled={count <= 1}
              onClick={() => L.setSettings({ count: count - 1 })}>{ic(Minus)}</IconButton>
            <span style={{ minWidth: 14, textAlign: 'center', color: C.textPrimary }}>{count}</span>
            <IconButton size="xs" title="Больше вариантов" ariaLabel="Больше вариантов" disabled={count >= maxCount}
              onClick={() => L.setSettings({ count: count + 1 })}>{ic(Plus)}</IconButton>
          </span>
          <span data-image-price="" style={{ color: C.textPrimary, fontWeight: 600, whiteSpace: 'nowrap' }}>{L.priceLabel}</span>
          <CharacterChip projectId={projectId} slug={L.prefs.characterSlug} focusKey={state.focus} />
          {withSteps && (
            <span style={{ display: 'inline-flex', alignItems: 'center' }} title="Результат вернётся в размере исходника">
              <Checkbox checked={L.settings.matchSourceSize} onChange={v => L.setSettings({ matchSourceSize: v })}
                ariaLabel="Размер оригинала" />
              Размер оригинала
            </span>
          )}
          {L.blocked && L.model && (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, color: C.warningText, fontSize: FS.xs }}>
              {ic(AlertTriangle)}{L.model.label}: {L.blocked.charAt(0).toLowerCase() + L.blocked.slice(1)}
            </span>
          )}
        </>, 36,
      ) : row(<span style={{ color: C.textMuted }}>Рисовать нечем: администратор не настроил поставщиков картинок</span>, 36))}
    </div>
  );
}

// Строка состояния в меню переключателя «Git ▾»
export function imagesStripStatus(sessionId: string | null): string {
  const t = getFocusedThread(sessionId);
  return t ? `Работаем с ${focusLabel(t)}` : 'картинка не выбрана';
}
