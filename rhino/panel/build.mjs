import * as esbuild from 'esbuild';
import { existsSync, statSync } from 'node:fs';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import postcss from 'postcss';
import tailwindcss from '@tailwindcss/postcss';

const root = fileURLToPath(new URL('.', import.meta.url));
const dist = join(root, 'dist');
const serve = process.argv.includes('--serve');
const port = Number(process.argv.find((arg) => arg.startsWith('--port='))?.slice(7) ?? 5173);
await mkdir(dist, { recursive: true });

// Also typecheck when MSBuild invokes this script directly.
const check = spawnSync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), '--noEmit'], {
  cwd: root, stdio: 'inherit',
});
if (check.status !== 0) process.exit(check.status ?? 1);

function resolveAt(specifier) {
  const target = join(root, 'src', specifier.slice(2));
  if (existsSync(target) && statSync(target).isFile()) return target;
  for (const ext of ['.ts', '.tsx', '.js', '.mjs', '.css', '.json']) {
    if (existsSync(target + ext)) return target + ext;
  }
  for (const ext of ['.ts', '.tsx', '.js']) {
    const index = join(target, 'index' + ext);
    if (existsSync(index)) return index;
  }
  return null;
}

function options(preview) {
  const stem = preview ? 'panel' : 'panel.host';
  return {
    entryPoints: [join(root, 'src/main.tsx')],
    bundle: true,
    format: 'iife',
    target: ['es2022'],
    jsx: 'automatic',
    outdir: dist,
    entryNames: stem,
    define: {
      __PREVIEW__: serve ? '__RHINOCLIENT_PREVIEW__' : JSON.stringify(preview),
      'process.env.NODE_ENV': JSON.stringify(serve ? 'development' : 'production'),
    },
    banner: serve ? { js: 'const __RHINOCLIENT_PREVIEW__ = location.pathname === "/panel.html";' } : undefined,
    minify: !serve,
    sourcemap: serve ? 'inline' : false,
    plugins: [{
      name: 'at-alias',
      setup(build) {
        build.onResolve({ filter: /^@\// }, (args) => {
          const path = resolveAt(args.path);
          if (!path) return { errors: [{ text: `Cannot resolve ${args.path}` }] };
          return { path };
        });
      },
    }, {
      // Tailwind is compiled into the page. The plugin does not load it from a CDN.
      name: 'tailwind',
      setup(build) {
        build.onLoad({ filter: /panel\.css$/ }, async (args) => {
          const result = await postcss([tailwindcss()]).process(await readFile(args.path, 'utf8'), { from: args.path });
          const sources = (await readdir(join(root, 'src'), { recursive: true }))
            .filter((name) => /\.(ts|tsx|css)$/.test(String(name)))
            .map((name) => join(root, 'src', String(name)));
          return {
            contents: result.css,
            loader: 'css',
            resolveDir: dirname(args.path),
            watchFiles: sources,
            watchDirs: [...new Set(sources.map((file) => dirname(file)))],
          };
        });
      },
    }, {
      name: 'self-contained-html',
      setup(build) {
        build.onEnd(async (result) => {
          if (result.errors.length) return;
          const [js, css] = await Promise.all([
            readFile(join(dist, `${stem}.js`), 'utf8'),
            readFile(join(dist, `${stem}.css`), 'utf8'),
          ]);
          const html = `<!doctype html>
<html lang="en"><head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>RhinoClient</title>
<style>${css}</style>
</head><body><main id="root"></main><script>${js}</script>${serve
  ? '<script>new EventSource("/esbuild").addEventListener("change", () => location.reload());</script>'
  : ''}</body></html>`;
          await writeFile(join(dist, `${stem}.html`), html);
          // Both development pages use the same build so the reload event is
          // sent only after both pages have been updated.
          if (serve) await writeFile(join(dist, 'panel.html'), html);
        });
      },
    }],
  };
}

if (serve) {
  const context = await esbuild.context(options(false));
  await context.watch();
  await context.serve({ servedir: dist, host: '127.0.0.1', port });
  console.log(`Panel preview: http://127.0.0.1:${port}/panel.html`);
  console.log(`Rhino panel: http://127.0.0.1:${port}/panel.host.html`);
  console.log('TypeScript and CSS edits automatically reload both pages.');
} else {
  await esbuild.build(options(true));
  await esbuild.build(options(false));
  console.log('Built browser preview and embedded Rhino panel.');
}
