// Секция «Ссылки» (внешние URL из хода). Перенесена из ArtifactsPanel verbatim.
import { useState } from 'react';
import { C, FONT, R, SHADOW } from '../../lib/design';
import type { ArtifactLink } from '../../hooks/useSessionArtifacts';

function LinkRow({ link }: { link: ArtifactLink }) {
  const [hover, setHover] = useState(false);
  return (
    <a
      href={link.url}
      target="_blank"
      rel="noopener noreferrer"
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={link.url}
      style={{
        display: 'flex', flexDirection: 'column', gap: 2,
        padding: '9px 12px', textDecoration: 'none',
        border: `1px solid ${hover ? C.accent : C.borderLight}`, borderRadius: R.lg,
        boxShadow: hover ? `0 0 0 1px ${C.accent}` : SHADOW.card, background: C.bgWhite,
        transition: 'border-color .12s, box-shadow .12s',
      }}
    >
      <span style={{ fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {link.domain}
      </span>
      <span style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {link.url}
      </span>
    </a>
  );
}

export function LinksSection({ links }: { links: ArtifactLink[] }) {
  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: '8px 12px', display: 'flex', flexDirection: 'column', gap: 5 }}>
      {links.map(l => <LinkRow key={l.url} link={l} />)}
    </div>
  );
}
