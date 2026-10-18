import { useState } from 'react';
import { MoreHorizontal, Plug, RefreshCw } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Island, IslandHeader } from '../../components/ui/Island';
import { IconButton, Menu, MenuItem, MenuSep, Notice } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { useProjectFeature } from '../../lib/projectCapabilities';
import { ProjectFeature, type Project } from '../../types';
import { useInstalledPlugins } from './useInstalledPlugins';

// === Слот панели плагина в sidebar (экран 3 макетов, ADR-031 §3, флаг plugins-mvp) ===
// Каркас слота (2.3): остров «Панели плагинов» в боковой колонке. Тело — sandbox-iframe
// по мосту v1 (ADR §3.4); сам мост и контент панели — пачка C (3.1/3.2), здесь только
// оправа: шапка (плитка + имя + перезагрузка + меню), тело, футер с правами и версией.
//
// Гейт V5: слота нет, пока нет ни одного установленного плагина; плагины стоят, но
// панелей у них нет — пустой остров (мост/состав панелей уточнит пачка C).

// Мини-плитка 20×20 с инициалами (шапка острова, макет экран 3).
function MiniTile({ name }: { name: string }) {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const initials = parts.length === 0 ? '?'
    : parts.length === 1 ? parts[0].slice(0, 2).toUpperCase()
      : (parts[0][0] + parts[1][0]).toUpperCase();
  return (
    <div style={{
      width: 20, height: 20, borderRadius: R.sm, flexShrink: 0,
      background: C.accentLight, color: C.accent,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      fontWeight: 700, fontSize: 9,
    }}>
      {initials}
    </div>
  );
}

// Короткая дата/версия для футера: «Права: …» + версия моно.
function footerText(granted: string[], version: string): string {
  const perms = granted.length > 0 ? granted.join(', ') : 'не выданы';
  return `Права: ${perms} · v${version}`;
}

export function PluginFrame({ project }: { project: Project }) {
  const isMobile = useIsMobile();
  const pluginsAvail = useProjectFeature(project, ProjectFeature.Plugins);
  const { plugins, loading } = useInstalledPlugins(project.id, pluginsAvail);
  const [menuOpen, setMenuOpen] = useState(false);

  // V5: без плагинов слота нет вовсе.
  if (!pluginsAvail) return null;
  if (!loading && plugins.length === 0) return null;

  // Пока мост (пачка C) не подключён — тело показывает загрузку/нейтральную подложку.
  // Первый плагин с панелью станет содержимым; здесь каркас.
  const first = plugins[0];
  const name = first ? (first.id.split('/').pop() ?? first.id) : '';

  return (
    <Island>
      <IslandHeader
        icon={<MiniTile name={name || 'P'} />}
        title="Панели плагинов"
        actions={
          <div style={{ display: 'flex', alignItems: 'center', gap: 2 }}>
            <IconButton size="xs" tone="muted" ariaLabel="Перезагрузить панель" onClick={() => {}}>
              <RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            <div style={{ position: 'relative' }}>
              <IconButton size="xs" tone="muted" ariaLabel="Меню панели" onClick={() => setMenuOpen(o => !o)}>
                <MoreHorizontal size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
              {menuOpen && (
                <Menu onClose={() => setMenuOpen(false)} align="right" top={-2}>
                  <MenuItem label="Права…" onClick={() => setMenuOpen(false)} />
                  <MenuItem label="Открыть в «Плагинах»" onClick={() => setMenuOpen(false)} />
                  <MenuSep />
                  <MenuItem label="Скрыть панель" onClick={() => setMenuOpen(false)} />
                </Menu>
              )}
            </div>
          </div>
        }
      />

      {/* Тело: sandbox-iframe по мосту v1 (без allow-same-origin) — пачка C.
          Здесь каркас: подложка C.bgCard + состояние (загрузка/плейсхолдер). */}
      <div style={{
        flex: 1, minHeight: 0, overflow: 'auto',
        background: C.bgCard, padding: SP.md,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
      }}>
        {loading ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            {[0, 1, 2, 3].map(i => (
              <div key={i} style={{ height: 14, borderRadius: R.sm, background: C.bgInset, opacity: 0.6 + i * 0.1 }} />
            ))}
          </div>
        ) : (
          <Notice tone="info" icon={Plug}>
            Панель плагина подключается по мосту v1 (пачка C). Содержимое появится после
            подключения; права и версия — в футере.
          </Notice>
        )}
      </div>

      {/* Футер: права + версия (макет, экран 3; В4 — на мобиле скрыть) */}
      {!isMobile && first && (
        <div style={{
          flexShrink: 0, padding: '6px 12px',
          background: C.bgInset, borderTop: `1px solid ${C.borderLight}`,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {footerText(first.granted, first.version)}
        </div>
      )}
    </Island>
  );
}
