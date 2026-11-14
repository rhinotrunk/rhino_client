import * as esbuild from 'esbuild';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const root = fileURLToPath(new URL('.', import.meta.url));
const dist = join(root, 'dist');
const serve = process.argv.includes('--serve');
await mkdir(dist, { recursive: true });

// Also typecheck when MSBuild invokes this script directly.
const check = spawnSync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), '--noEmit'], {
  cwd: root, stdio: 'inherit',
});
if (check.status !== 0) process.exit(check.status ?? 1);

function options(preview) {
  const stem = preview ? 'panel' : 'panel.host';
  return {
    entryPoints: [join(root, 'src/main.ts')],
    bundle: true,
    format: 'iife',
    target: ['es2022'],
    outdir: dist,
    entryNames: stem,
    define: { __PREVIEW__: serve ? '__RHINOCLIENT_PREVIEW__' : JSON.stringify(preview) },
    banner: serve ? { js: 'const __RHINOCLIENT_PREVIEW__ = location.pathname === "/panel.html";' } : undefined,
    minify: !serve,
    sourcemap: serve ? 'inline' : false,
    plugins: [{
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
  await context.serve({ servedir: dist, host: '127.0.0.1', port: 5173 });
  console.log('Panel preview: http://127.0.0.1:5173/panel.html');
  console.log('Rhino panel: http://127.0.0.1:5173/panel.host.html');
  console.log('TypeScript and CSS edits automatically reload both pages.');
} else {
  await esbuild.build(options(true));
  await esbuild.build(options(false));
  console.log('Built browser preview and embedded Rhino panel.');
}
