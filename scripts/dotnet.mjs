import { spawnSync } from 'node:child_process';
import { homedir } from 'node:os';
import { join } from 'node:path';

export function findDotnet() {
  const binary = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const candidates = [process.env.RHINOCLIENT_DOTNET, binary, join(homedir(), '.dotnet', binary)];
  for (const candidate of candidates.filter(Boolean)) {
    const result = spawnSync(candidate, ['--version'], { encoding: 'utf8' });
    if (result.status === 0 && result.stdout.trim().match(/^(8|9|10)./)) return candidate;
  }
  throw new Error('Install the .NET 8 SDK, or set RHINOCLIENT_DOTNET to its dotnet executable.');
}
