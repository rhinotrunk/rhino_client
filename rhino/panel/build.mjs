import * as esbuild from 'esbuild';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import postcss from 'postcss';
import tailwindcss from 'tailwindcss';
import autoprefixer from 'autoprefixer';
import tailwindConfig from './tailwind.config.mjs';

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

function options(preview) {
  const stem = preview ? 'panel' : 'panel.host';
  return {
    entryPoints: [join(root, 'src/main.tsx')],
    bundle: true,
    format: 'iife',
    target: ['es2022'],
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
      name: 'tailwind',
      setup(build) {
        build.onLoad({ filter: /panel\.css$/ }, async ({ path }) => {
          const result = await postcss([
            tailwindcss({ ...tailwindConfig, content: [join(root, 'src/**/*.{ts,tsx}')] }),
            autoprefixer(),
          ]).process(await readFile(path, 'utf8'), { from: path });
          const sources = (await readdir(join(root, 'src'), { recursive: true }))
            .filter((name) => /\.(ts|tsx|css)$/.test(name))
            .map((name) => join(root, 'src', name));
          return {
            contents: result.css,
            loader: 'css',
            resolveDir: dirname(path),
            // Class changes must invalidate CSS as well as the JS bundle.
            watchFiles: sources,
            watchDirs: [...new Set(sources.map(dirname))],
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
