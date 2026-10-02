// Вкладка «Фильм» — заглушка до коммита с монтажным списком (заменяется целиком)
import type { ReactNode } from 'react';
import { C, FS, SP, type GenerationFoot } from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';

export function FilmTab(_p: { ctx: WorkspacePanelDefCtx }) {
  return <div style={{ padding: SP.md, fontSize: FS.sm, color: C.textMuted }}>Фильм не выбран.</div>;
}

export interface FilmPanelModel {
  foot?: GenerationFoot;
  count: number;
  subtitle?: string;
  context?: ReactNode;
  contextAction?: ReactNode;
  peekSummary?: string;
  nameOf: (path: string) => string;
}

export function useFilmPanel(_projectId: string | null, _sessionId: string | null): FilmPanelModel {
  return { count: 0, nameOf: p => p };
}
