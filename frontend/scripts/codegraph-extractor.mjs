#!/usr/bin/env node
/**
 * Экстрактор кодеграфа для .ts/.tsx исходников.
 *
 * Принимает корень исходников (аргументом или через $CODEGRAPH_SRC),
 * обходит файлы и печатает на stdout JSON-снапшот в PascalCase-формате:
 *
 *   {
 *     "Nodes":   [ { Id, Name, Category, FilePath } ],
 *     "Edges":   [ { From, To, Kind } ],
 *     "Metadata": { SourceRoot, GeneratedAt, TotalFiles, TotalNodes, TotalEdges }
 *   }
 *
 * Категории узлов: component | hook | ui-примитив | util.
 *   component     — .tsx-файл с именованной функцией, в теле которой есть JSX
 *   ui-примитив   — путь содержит components/ui/
 *   hook          — имя начинается с «use» + заглавная (useFoo)
 *   util          — всё остальное (в т.ч. типы, сторы, константы)
 *
 * Рёбра References — от каждого named import к фактическому файлу-источнику,
 * с резолвом алиасов tsconfig и полным разворачиванием index-реэкспортов
 * (export { X } from '...'), с защитой от циклов.
 *
 * C#-сторона десериализует вывод напрямую в Core.CodeGraph.
 */

import { createRequire } from 'node:module';
import {
  statSync,
  readdirSync,
  existsSync,
} from 'node:fs';
import {
  join,
  relative,
  resolve,
  dirname,
  extname,
  basename,
  sep,
} from 'node:path';

// typescript — CommonJS-пакет, в ESM-контексте грузим через createRequire.
const require = createRequire(import.meta.url);
const ts = require('typescript');

// ===== CLI =====

function parseArgs(argv) {
  const args = { src: null, tsconfig: null };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--tsconfig' || a === '-p') {
      args.tsconfig = argv[++i];
    } else if (a === '--help' || a === '-h') {
      printHelp();
      process.exit(0);
    } else if (a.startsWith('-')) {
      console.error(`Unknown flag: ${a}`);
      process.exit(2);
    } else if (!args.src) {
      args.src = a;
    }
  }
  args.src = args.src || process.env.CODEGRAPH_SRC || 'src';
  return args;
}

function printHelp() {
  process.stdout.write(
    'Usage: node codegraph-extractor.mjs [src] [--tsconfig path]\n' +
      '  src         Корень исходников (по умолчанию ./src или $CODEGRAPH_SRC)\n' +
      '  --tsconfig  Путь к tsconfig.json (по умолчанию ./tsconfig.app.json)\n'
  );
}

// ===== Обход файлов =====

const DEFAULT_EXCLUDE = [/[/\\]dev[/\\]/, /\.test\.[cm]?[jt]sx?$/, /\.spec\.[cm]?[jt]sx?$/, /\.d\.ts$/];

function walkFiles(srcRoot) {
  const out = [];
  function recurse(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.name.startsWith('.')) continue;
      if (entry.name === 'node_modules') continue;
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        recurse(full);
      } else if (/\.tsx?$/.test(entry.name)) {
        const rel = relative(srcRoot, full).split(sep).join('/');
        if (DEFAULT_EXCLUDE.some(re => re.test(rel))) continue;
        out.push({ full, rel });
      }
    }
  }
  recurse(srcRoot);
  return out;
}

// ===== Резолв путей =====

// tsconfig ищем не только в cwd: rootPath бывает frontend/src (конфиг лежит в frontend/)
// или корнем репо (конфиг в frontend/) — поднимаемся от srcRoot, затем от cwd.
// Не нашли нигде — прежний дефолт, дальше main() уходит в fallback без алиасов.
function findTsconfig(srcRoot, cwd) {
  for (const start of [srcRoot, cwd]) {
    let dir = resolve(start);
    while (true) {
      for (const name of ['tsconfig.app.json', 'tsconfig.json']) {
        const candidate = join(dir, name);
        if (existsSync(candidate)) return candidate;
      }
      const parent = dirname(dir);
      if (parent === dir) break;
      dir = parent;
    }
  }
  return join(cwd, 'tsconfig.app.json');
}

function loadTsconfig(tsconfigPath) {
  // getParsedCommandLineOfConfigFile даёт нормализованные options
  // (target/module как числа и т.п.) — createProgram их принимает напрямую,
  // в отличие от сырого JSON.
  const parsed = ts.getParsedCommandLineOfConfigFile(
    tsconfigPath,
    undefined,
    ts.sys
  );
  if (!parsed) {
    throw new Error(`Failed to parse ${tsconfigPath}`);
  }
  const co = parsed.options;
  const baseUrl = co.baseUrl
    ? resolve(dirname(tsconfigPath), co.baseUrl)
    : dirname(tsconfigPath);
  return {
    baseUrl,
    paths: co.paths || {},
    options: co,
  };
}

function tryResolveAlias(spec, baseUrl, paths) {
  if (!paths) return null;
  if (spec.startsWith('.') || spec.startsWith('/')) return null;
  // exact match
  if (Object.prototype.hasOwnProperty.call(paths, spec)) {
    for (const target of paths[spec]) {
      const hit = resolveFile(resolve(baseUrl, target));
      if (hit) return hit;
    }
  }
  // wildcard match (*)
  for (const [pattern, targets] of Object.entries(paths)) {
    if (!pattern.endsWith('/*')) continue;
    const prefix = pattern.slice(0, -2);
    if (spec === prefix || spec.startsWith(prefix + '/')) {
      const tail = spec === prefix ? '' : spec.slice(prefix.length + 1);
      for (const target of targets) {
        const replaced = target.replace(/\*/g, tail);
        const hit = resolveFile(resolve(baseUrl, replaced));
        if (hit) return hit;
      }
    }
  }
  return null;
}

function resolveFile(absPath) {
  const p = absPath.replace(/[\\/]+$/, '');
  for (const ext of [
    '',
    '.ts',
    '.tsx',
    '.mts',
    '.cts',
    '/index.ts',
    '/index.tsx',
    '/index.mts',
    '/index.cts',
  ]) {
    const candidate = p + ext;
    if (existsSync(candidate) && statSync(candidate).isFile()) return candidate;
  }
  return null;
}

function makeResolveImport(baseUrl, paths) {
  return function resolveImport(fromFull, spec) {
    if (!spec.startsWith('.')) {
      const aliased = tryResolveAlias(spec, baseUrl, paths);
      if (aliased) return aliased;
    }
    if (spec.startsWith('.')) {
      const base = dirname(fromFull);
      return resolveFile(resolve(base, spec));
    }
    return null;
  };
}

// ===== Парсинг деклараций =====

function hasModifier(modifiers, kind) {
  return !!(modifiers && modifiers.some(m => m.kind === kind));
}

function fileNameFromPath(rel) {
  const base = basename(rel, extname(rel));
  return base.charAt(0).toUpperCase() + base.slice(1);
}

// Возвращает true, если stmt — экспорт declaration с указанным именем
// (без учёта re-export).
function isLocalExportOfName(stmt, targetName) {
  if (ts.isFunctionDeclaration(stmt) && stmt.name && stmt.name.text === targetName) {
    return hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword);
  }
  if (ts.isClassDeclaration(stmt) && stmt.name && stmt.name.text === targetName) {
    return hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword);
  }
  if (ts.isVariableStatement(stmt) && hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword)) {
    for (const decl of stmt.declarationList.declarations) {
      if (ts.isIdentifier(decl.name) && decl.name.text === targetName) return true;
    }
  }
  if (
    (ts.isInterfaceDeclaration(stmt) ||
      ts.isTypeAliasDeclaration(stmt) ||
      ts.isEnumDeclaration(stmt)) &&
    stmt.name.text === targetName
  ) {
    return hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword);
  }
  if (ts.isModuleDeclaration(stmt) && stmt.name.text === targetName) {
    return hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword);
  }
  return false;
}

function defaultExportName(stmt, fileRel) {
  if (!ts.isExportAssignment(stmt)) return null;
  const expr = stmt.expression;
  if (ts.isIdentifier(expr)) return expr.text;
  return fileNameFromPath(fileRel);
}

// Резолвим, куда фактически указывает каждый named export файла.
// Возвращает Map<exportName, {file, name}>.
// Циклы разрываем: если при обходе попадаем в уже посещённый файл —
// записываем текущий targetRel/targetName как есть.
function resolveExports(sf, fileRel, ctx) {
  const result = new Map();
  const visited = new Set();

  function visit(s, rel, chain) {
    if (visited.has(rel)) return;
    visited.add(rel);

    for (const stmt of s.statements) {
      // --- Re-export через module specifier ---
      if (ts.isExportDeclaration(stmt) && stmt.moduleSpecifier) {
        const moduleSpec = stmt.moduleSpecifier.text;
        const targetFull = ctx.resolveImport(s.fileName, moduleSpec);
        if (!targetFull) continue;
        const tRel = relative(ctx.srcRoot, targetFull).split(sep).join('/');
        if (chain.has(tRel)) continue; // защита от циклов

        const tSf = ctx.program.getSourceFile(targetFull);
        if (!tSf) continue;

        if (!stmt.exportClause) {
          // export * from '...'
          visit(tSf, tRel, new Set(chain).add(tRel));
          continue;
        }
        if (ts.isNamespaceExport(stmt.exportClause)) {
          // export * as X from '...' — пропускаем в v1
          continue;
        }
        if (ts.isNamedExports(stmt.exportClause)) {
          for (const spec of stmt.exportClause.elements) {
            const importedName = (spec.propertyName || spec.name).text;
            const localName = spec.name.text;
            const found = findName(tSf, tRel, importedName, new Set(chain).add(tRel));
            if (found) result.set(localName, found);
          }
        }
        continue;
      }

      // --- Локальный re-export: export { X } без from — прямо ищем X в этом же sf ---
      if (ts.isExportDeclaration(stmt) && !stmt.moduleSpecifier && ts.isNamedExports(stmt.exportClause)) {
        for (const spec of stmt.exportClause.elements) {
          const importedName = (spec.propertyName || spec.name).text;
          const localName = spec.name.text;
          for (const inner of s.statements) {
            if (isLocalExportOfName(inner, importedName)) {
              result.set(localName, { file: rel, name: importedName });
              break;
            }
          }
        }
        continue;
      }

      // --- Прямые декларации в этом файле ---
      if (ts.isFunctionDeclaration(stmt) && stmt.name && hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword)) {
        result.set(stmt.name.text, { file: rel, name: stmt.name.text });
        continue;
      }
      if (ts.isClassDeclaration(stmt) && stmt.name && hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword)) {
        result.set(stmt.name.text, { file: rel, name: stmt.name.text });
        continue;
      }
      if (ts.isVariableStatement(stmt) && hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword)) {
        for (const decl of stmt.declarationList.declarations) {
          if (ts.isIdentifier(decl.name)) {
            result.set(decl.name.text, { file: rel, name: decl.name.text });
          }
        }
        continue;
      }
      if (
        (ts.isInterfaceDeclaration(stmt) ||
          ts.isTypeAliasDeclaration(stmt) ||
          ts.isEnumDeclaration(stmt)) &&
        hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword)
      ) {
        result.set(stmt.name.text, { file: rel, name: stmt.name.text });
        continue;
      }
      if (ts.isModuleDeclaration(stmt) && hasModifier(stmt.modifiers, ts.SyntaxKind.ExportKeyword)) {
        result.set(stmt.name.text, { file: rel, name: stmt.name.text });
        continue;
      }
      if (ts.isExportAssignment(stmt)) {
        const name = defaultExportName(stmt, rel);
        if (name) result.set(name, { file: rel, name });
      }
    }
  }

  // Ищем targetName в файле s. Возвращает {file, name} или null.
  // Цикл защищён через chain (Set посещённых путей).
  function findName(s, rel, targetName, inChain) {
    for (const stmt of s.statements) {
      // Прямой экспорт — нашли
      if (isLocalExportOfName(stmt, targetName)) {
        return { file: rel, name: targetName };
      }

      // Re-export через from
      if (ts.isExportDeclaration(stmt) && stmt.moduleSpecifier) {
        const targetFull = ctx.resolveImport(s.fileName, stmt.moduleSpecifier.text);
        if (!targetFull) continue;
        const tRel = relative(ctx.srcRoot, targetFull).split(sep).join('/');
        if (inChain.has(tRel)) continue;

        const tSf = ctx.program.getSourceFile(targetFull);
        if (!tSf) continue;

        if (!stmt.exportClause) {
          // export * from — проваливаемся в tSf
          const found = findName(tSf, tRel, targetName, new Set(inChain).add(tRel));
          if (found) return found;
          continue;
        }
        if (ts.isNamedExports(stmt.exportClause)) {
          for (const spec of stmt.exportClause.elements) {
            const importedName = (spec.propertyName || spec.name).text;
            if (importedName === targetName) {
              const found = findName(tSf, tRel, targetName, new Set(inChain).add(tRel));
              if (found) return found;
            }
          }
        }
      }

      // Локальный re-export без from — ищем прямое объявление в этом же sf.
      // НЕ рекурсируем в findName: иначе цикл на export { X } без from в этом же файле.
      if (ts.isExportDeclaration(stmt) && !stmt.moduleSpecifier && ts.isNamedExports(stmt.exportClause)) {
        for (const spec of stmt.exportClause.elements) {
          const importedName = (spec.propertyName || spec.name).text;
          if (importedName === targetName) {
            for (const inner of s.statements) {
              if (isLocalExportOfName(inner, targetName)) {
                return { file: rel, name: targetName };
              }
            }
          }
        }
      }

      // export default <expression> — это анонимный default
      if (ts.isExportAssignment(stmt) && targetName === defaultExportName(stmt, rel)) {
        return { file: rel, name: targetName };
      }
    }
    return null;
  }

  visit(sf, fileRel, new Set([fileRel]));
  return result;
}

// ===== Категоризация =====

function isJsxInNode(node) {
  let found = false;
  function walk(n) {
    if (found) return;
    if (
      ts.isJsxElement(n) ||
      ts.isJsxSelfClosingElement(n) ||
      ts.isJsxFragment(n)
    ) {
      found = true;
      return;
    }
    // не проваливаемся во вложенные функции
    if (
      ts.isFunctionDeclaration(n) ||
      ts.isFunctionExpression(n) ||
      ts.isArrowFunction(n)
    ) {
      return;
    }
    ts.forEachChild(n, walk);
  }
  if (node.body) walk(node.body);
  return found;
}

function functionReturnsJsx(fnNode) {
  if (!fnNode.body) return false;
  if (ts.isIdentifier(fnNode.body)) return false; // типа () => Foo
  return isJsxInNode(fnNode);
}

function valueReturnsJsx(initializer) {
  if (!initializer) return false;
  if (ts.isArrowFunction(initializer) || ts.isFunctionExpression(initializer)) {
    return functionReturnsJsx(initializer);
  }
  return false;
}

// Категория конкретного named-экспорта в файле.
function categorizeExport(fileRel, exportName, declarationNode) {
  // ui-примитив — определяется путём (любой named export в components/ui/)
  if (fileRel.includes('/components/ui/') || fileRel.startsWith('components/ui/')) {
    return 'ui-примитив';
  }
  // hook — useFooBar
  if (/^use[A-Z0-9_]/.test(exportName)) return 'hook';
  // component — .tsx с JSX в теле функции
  if (fileRel.endsWith('.tsx') && declarationNode) {
    if (
      (ts.isFunctionDeclaration(declarationNode) || ts.isFunctionExpression(declarationNode) ||
        ts.isArrowFunction(declarationNode)) &&
      functionReturnsJsx(declarationNode)
    ) {
      return 'component';
    }
    if (ts.isVariableDeclaration(declarationNode) && valueReturnsJsx(declarationNode.initializer)) {
      return 'component';
    }
  }
  return 'util';
}

// ===== Main =====

function buildProgram(fileList, parsedOptions) {
  const compilerOptions = {
    ...parsedOptions,
    noEmit: true,
    // Экстрактор ходит только по AST исходников: lib.d.ts и @types ему не нужны,
    // а их загрузка — большая доля createProgram. Импорты продолжают резолвиться
    // обычным module resolution (noResolve нельзя — program.getSourceFile по
    // резолвнутому пути требует, чтобы файл был в программе).
    lib: [],
    types: [],
    skipLibCheck: true,
  };
  return ts.createProgram({
    rootNames: fileList.map(f => f.full),
    options: compilerOptions,
  });
}

function main() {
  const args = parseArgs(process.argv);
  const cwd = process.cwd();
  const srcRoot = resolve(cwd, args.src);
  const tsconfigPath = args.tsconfig
    ? resolve(cwd, args.tsconfig)
    : findTsconfig(srcRoot, cwd);

  if (!existsSync(srcRoot) || !statSync(srcRoot).isDirectory()) {
    console.error(`Source root not found: ${srcRoot}`);
    process.exit(2);
  }

  const tsconfig = existsSync(tsconfigPath)
    ? loadTsconfig(tsconfigPath)
    : { baseUrl: srcRoot, paths: {}, options: {} };

  const files = walkFiles(srcRoot);
  if (files.length === 0) {
    console.error(`No .ts/.tsx files found under ${srcRoot}`);
    process.exit(2);
  }

  const program = buildProgram(files, tsconfig.options);
  const resolveImport = makeResolveImport(tsconfig.baseUrl, tsconfig.paths);
  const ctx = { resolveImport, srcRoot, program };

  // 1. Резолвим экспорты каждого файла
  const resolvedExportsByFile = new Map();
  for (const file of files) {
    const sf = program.getSourceFile(file.full);
    if (!sf) continue;
    const map = resolveExports(sf, file.rel, ctx);
    resolvedExportsByFile.set(file.rel, map);
  }

  // 2. Соберём узлы: по (file, name) уникальные
  const nodeKey = (file, name) => `${file}::${name}`;
  const nodes = new Map();
  function ensureNode(fileRel, name, declNode, viaRel) {
    const key = nodeKey(fileRel, name);
    if (nodes.has(key)) return;
    const category = categorizeExport(viaRel || fileRel, name, declNode);
    nodes.set(key, {
      Id: key,
      Name: name,
      Category: category,
      FilePath: fileRel,
    });
  }

  // Категория для узла — по filePath самого узла (куда он реально указывает),
  // а не по файлу-источнику реэкспорта.
  for (const file of files) {
    const sf = program.getSourceFile(file.full);
    if (!sf) continue;
    const map = resolvedExportsByFile.get(file.rel);
    // Прямые экспорты + re-export'ы (через резолв)
    for (const [, target] of map.entries()) {
      // Категоризуем по target.file (где фактически живёт узел)
      const targetSf = program.getSourceFile(join(srcRoot, target.file.split('/').join(sep)));
      const declNode = findDeclarationNode(targetSf, target.name);
      ensureNode(target.file, target.name, declNode, target.file);
    }
  }

  // 3. Соберём рёбра: импорты
  const edgeSet = new Set();
  const edges = [];
  function addEdge(fromKey, toKey) {
    const k = `${fromKey}->${toKey}`;
    if (edgeSet.has(k)) return;
    edgeSet.add(k);
    edges.push({ From: fromKey, To: toKey, Kind: 'References' });
  }

  for (const file of files) {
    const sf = program.getSourceFile(file.full);
    if (!sf) continue;
    const fromKey = `${file.rel}::*`;

    for (const stmt of sf.statements) {
      if (!ts.isImportDeclaration(stmt)) continue;
      const clause = stmt.importClause;
      if (!clause) continue;
      if (clause.isTypeOnly) continue;

      const specText = stmt.moduleSpecifier.text;
      const targetFull = ctx.resolveImport(sf.fileName, specText);
      if (!targetFull) continue; // bare module (react, etc.)
      const tgtRel = relative(srcRoot, targetFull).split(sep).join('/');

      // default import
      if (clause.name) {
        // Резолвим default. Простой случай: import default указывает на export default.
        // Имя default-узла = имя файла (PascalCase), либо имя export default expr.
        const targetMap = resolvedExportsByFile.get(tgtRel);
        let defaultName = fileNameFromPath(tgtRel);
        if (targetMap) {
          // Ищем export с isDefault === true в исходнике
          const targetSf = program.getSourceFile(targetFull);
          const dn = findDefaultExportName(targetSf, tgtRel);
          if (dn) defaultName = dn;
        }
        // Гарантируем узел default-цели
        const targetSf = program.getSourceFile(targetFull);
        const declNode = findDeclarationNode(targetSf, defaultName);
        ensureNode(tgtRel, defaultName, declNode, tgtRel);
        addEdge(fromKey, nodeKey(tgtRel, defaultName));
      }

      if (clause.namedBindings) {
        if (ts.isNamespaceImport(clause.namedBindings)) {
          // import * as X from '...' — ребро к самому файлу
          addEdge(fromKey, `${tgtRel}::*`);
        } else if (ts.isNamedImports(clause.namedBindings)) {
          for (const spec of clause.namedBindings.elements) {
            if (spec.isTypeOnly) continue;
            const importedName = (spec.propertyName || spec.name).text;
            const targetMap = resolvedExportsByFile.get(tgtRel);
            const resolved = targetMap && targetMap.get(importedName);
            if (resolved) {
              const targetSf = program.getSourceFile(
                join(srcRoot, resolved.file.split('/').join(sep))
              );
              const declNode = findDeclarationNode(targetSf, resolved.name);
              ensureNode(resolved.file, resolved.name, declNode, resolved.file);
              addEdge(fromKey, nodeKey(resolved.file, resolved.name));
            } else {
              // fallback: пишем узел как есть (может быть type-only или не нашли)
              const targetSf = program.getSourceFile(targetFull);
              const declNode = findDeclarationNode(targetSf, importedName);
              ensureNode(tgtRel, importedName, declNode, tgtRel);
              addEdge(fromKey, nodeKey(tgtRel, importedName));
            }
          }
        }
      }
    }
  }

  const out = {
    Nodes: [...nodes.values()].sort((a, b) => a.Id.localeCompare(b.Id)),
    Edges: edges.sort((a, b) =>
      a.From === b.From ? a.To.localeCompare(b.To) : a.From.localeCompare(b.From)
    ),
    Metadata: {
      SourceRoot: args.src,
      GeneratedAt: new Date().toISOString(),
      TotalFiles: files.length,
      TotalNodes: nodes.size,
      TotalEdges: edges.length,
    },
  };

  // Без отступов: pretty-print раздувает stdout в ~4 раза и тормозит пайп с C#-парсером.
  process.stdout.write(JSON.stringify(out) + '\n');
}

function findDeclarationNode(sf, name) {
  if (!sf) return null;
  for (const stmt of sf.statements) {
    if (ts.isFunctionDeclaration(stmt) && stmt.name && stmt.name.text === name) return stmt;
    if (ts.isClassDeclaration(stmt) && stmt.name && stmt.name.text === name) return stmt;
    if (ts.isVariableStatement(stmt)) {
      for (const decl of stmt.declarationList.declarations) {
        if (ts.isIdentifier(decl.name) && decl.name.text === name) return decl.initializer || decl;
      }
    }
    if (
      (ts.isInterfaceDeclaration(stmt) ||
        ts.isTypeAliasDeclaration(stmt) ||
        ts.isEnumDeclaration(stmt)) &&
      stmt.name.text === name
    ) {
      return stmt;
    }
  }
  return null;
}

function findDefaultExportName(sf, fileRel) {
  if (!sf) return null;
  for (const stmt of sf.statements) {
    if (ts.isExportAssignment(stmt)) {
      return defaultExportName(stmt, fileRel);
    }
  }
  return null;
}

main();
