/**
 * Pre-build environment check script.
 * Run with: node scripts/check-env.mjs
 *
 * Verifies that the development environment has all required tools
 * at the correct versions before attempting to build.
 */

import { execSync } from 'child_process';

const requirements = [
  {
    name: 'Node.js',
    command: 'node --version',
    minVersion: '20.0.0',
    installHint: 'Install Node.js 20+ from https://nodejs.org or use nvm: nvm install 22'
  },
  {
    name: 'npm',
    command: 'npm --version',
    minVersion: '10.0.0',
    installHint: 'npm is included with Node.js. Update with: npm install -g npm@latest'
  }
];

function parseVersion(versionStr) {
  return versionStr.replace(/^v/, '').trim().split('.').map(Number);
}

function isVersionSatisfied(current, minimum) {
  const curr = parseVersion(current);
  const min = parseVersion(minimum);
  for (let i = 0; i < 3; i++) {
    if ((curr[i] || 0) > (min[i] || 0)) return true;
    if ((curr[i] || 0) < (min[i] || 0)) return false;
  }
  return true;
}

let allGood = true;

for (const req of requirements) {
  try {
    const version = execSync(req.command, { encoding: 'utf8' }).trim();
    if (!isVersionSatisfied(version, req.minVersion)) {
      console.error(`[FAIL] ${req.name}: found ${version}, need >= ${req.minVersion}`);
      console.error(`       ${req.installHint}`);
      allGood = false;
    } else {
      console.log(`[OK]   ${req.name}: ${version}`);
    }
  } catch {
    console.error(`[FAIL] ${req.name}: not found`);
    console.error(`       ${req.installHint}`);
    allGood = false;
  }
}

if (!allGood) {
  console.error('\nEnvironment check failed. Please install the required tools above.');
  process.exit(1);
} else {
  console.log('\nEnvironment check passed.');
}
