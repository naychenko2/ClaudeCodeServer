// Babel-плагин UI-инспектора: каждому host-элементу JSX (div, button, …) проставляет
// атрибут data-cc-src = «путь от корня РЕПОЗИТОРИЯ:строка» (frontend/src/…/X.tsx:214).
// Инъекция работает и в dev, и в prod-бандле — по атрибуту инспектор строит цепочку
// компонентов и привязывает заметку к исходнику (см. features/inspector/).
// Подключается в vite.config.ts строкой-путём (импорт .mjs уронил бы tsc -b).
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Якорь — корень репозитория, детерминированно от расположения самого плагина
// (frontend/scripts → две ступени вверх): root/cwd бабеля зависят от того, откуда
// запустили vite, и ошибка якоря молча дала бы FileMissing у привязки заметки
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

export default function ccSrcPlugin({ types: t }) {
  return {
    name: 'cc-src',
    visitor: {
      JSXOpeningElement(nodePath, state) {
        const name = nodePath.node.name;
        // Только host-элементы (тег с маленькой буквы): у компонентов атрибут стал бы
        // лишним пропом и до DOM не дошёл
        if (name.type !== 'JSXIdentifier' || !/^[a-z]/.test(name.name)) return;
        const line = nodePath.node.loc?.start.line;
        if (!line) return;
        // Атрибут уже проставлен руками — не дублируем
        if (nodePath.node.attributes.some(
          a => a.type === 'JSXAttribute' && a.name.name === 'data-cc-src',
        )) return;
        const filename = state.file.opts.filename;
        if (!filename) return;
        const rel = path.relative(repoRoot, filename).replace(/\\/g, '/');
        nodePath.node.attributes.push(
          t.jsxAttribute(t.jsxIdentifier('data-cc-src'), t.stringLiteral(`${rel}:${line}`)),
        );
      },
    },
  };
}
