// Shared-конфиг React для MF-модулей: модуль НИКОГДА не поставляет свою копию React и
// только потребляет singleton ядра (дефект D1).
//
// import: false — React не бандлится в модуль. Без него MF выбирает старшую из
// предложенных версий, и React модуля (свежая установка без lock даёт версию новее
// ядра) вставал рядом с react-dom ядра: null-диспетчер на хуках и белый экран.
//
// version — из package.json ядра, а не из node_modules модуля: иначе заглушка
// объявляется «старшей» версией, мост remoteEntry ядра выбирает её и падает на get().

import { readFileSync } from 'node:fs';

const core = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')) as {
  dependencies: Record<string, string>;
};

// Контракт — точная X.Y.Z: диапазон (>=19, 19.2.x) молча дал бы неверную version.
const version = (pkg: string) => {
  const raw = core.dependencies[pkg];
  const exact = raw?.replace(/^[\^~]/, '');
  if (!exact || !/^\d+\.\d+\.\d+$/.test(exact)) {
    throw new Error(
      `hostReactShared: версия ${pkg} в frontend/package.json должна быть X.Y.Z (допускается ^ или ~), получено «${raw}»`,
    );
  }
  return exact;
};

const consume = (pkg: string) => ({
  singleton: true,
  import: false as const,
  version: version(pkg),
  requiredVersion: `^${version(pkg)}`,
});

export const hostReactShared = {
  react: consume('react'),
  // Подпуть явно: свежий плагин MF не распространяет shared пакета на подпути
  // и бандлил бы jsx-runtime в модуль.
  'react/jsx-runtime': consume('react'),
  'react-dom': consume('react-dom'),
};
