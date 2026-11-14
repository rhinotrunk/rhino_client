import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const pluginDirectory = join(root, 'rhino/plugin/bin/Debug/net8.0');
if (!existsSync(join(pluginDirectory, 'RhinoClient.rhp'))) {
  throw new Error('Build the plugin first with npm run build.');
}
const executable = process.env.RHINOCLIENT_RHINO ?? (process.platform === 'darwin'
  ? '/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros'
  : 'C:\\Program Files\\Rhino 8\\System\\Rhino.exe');
const child = spawn(executable, process.platform === 'win32' ? ['/netcore', '/nosplash'] : ['-nosplash'], {
  cwd: root,
  env: {
    ...process.env,
    RHINO_PACKAGE_DIRS: [pluginDirectory, process.env.RHINO_PACKAGE_DIRS].filter(Boolean).join(';'),
  },
  detached: true,
  stdio: 'ignore',
});
child.on('error', (error) => { console.error(error.message); process.exitCode = 1; });
child.once('spawn', () => console.log('Rhino started. Run RhinoClient in its command line.'));
child.unref();
