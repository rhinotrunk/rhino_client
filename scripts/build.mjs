import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { findDotnet } from './dotnet.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const configuration = process.argv.includes('--release') ? 'Release' : 'Debug';
const result = spawnSync(findDotnet(), [
  'build', 'rhino/plugin/RhinoClient.csproj', '-c', configuration, '--nologo',
], { cwd: root, stdio: 'inherit' });
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
