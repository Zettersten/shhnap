import assert from 'node:assert/strict';
import test from 'node:test';
import { parseReleaseFeed, preferredReleaseAsset, releaseHighlights } from '../src/lib/release-feed.ts';

const validFeed = {
  schemaVersion: 1,
  version: '1.0.5',
  tag: 'v1.0.5',
  publishedAt: '2026-10-02T19:53:40Z',
  releaseUrl: 'https://github.com/Zettersten/shhnap/releases/tag/v1.0.5',
  notes: '# Shnapp v1.0.5\n\n## New in v1.0.5\n\n- Redesigned Settings.\n- Improved the Library.\n',
  downloads: {
    x64: {
      url: 'https://github.com/Zettersten/shhnap/releases/download/v1.0.5/Shnapp-win-x64.zip',
      sha256: 'a'.repeat(64),
    },
    arm64: {
      url: 'https://github.com/Zettersten/shhnap/releases/download/v1.0.5/Shnapp-win-arm64.zip',
      sha256: 'b'.repeat(64),
    },
  },
};

test('accepts a verified release feed for both Windows architectures', () => {
  assert.deepEqual(parseReleaseFeed(validFeed), validFeed);
  assert.equal(preferredReleaseAsset(validFeed, 'x64').url, validFeed.downloads.x64.url);
});

test('prefers verified installers in v2 while retaining portable ZIP assets', () => {
  const installerFeed = {
    ...structuredClone(validFeed),
    schemaVersion: 2,
    installers: {
      x64: {
        url: 'https://github.com/Zettersten/shhnap/releases/download/v1.0.5/ErikZettersten.Shnapp.x64-win-x64-Setup.exe',
        sha256: 'c'.repeat(64),
      },
      arm64: {
        url: 'https://github.com/Zettersten/shhnap/releases/download/v1.0.5/ErikZettersten.Shnapp.arm64-win-arm64-Setup.exe',
        sha256: 'd'.repeat(64),
      },
    },
  };

  assert.deepEqual(parseReleaseFeed(installerFeed), installerFeed);
  assert.equal(preferredReleaseAsset(installerFeed, 'x64').url, installerFeed.installers.x64.url);
  assert.equal(installerFeed.downloads.arm64.url, validFeed.downloads.arm64.url);

  const missingInstaller = structuredClone(installerFeed);
  delete missingInstaller.installers.arm64;
  assert.equal(parseReleaseFeed(missingInstaller), null);

  const redirectedInstaller = structuredClone(installerFeed);
  redirectedInstaller.installers.x64.url = 'https://example.com/ErikZettersten.Shnapp.x64-win-x64-Setup.exe';
  assert.equal(parseReleaseFeed(redirectedInstaller), null);

  const badChecksum = structuredClone(installerFeed);
  badChecksum.installers.arm64.sha256 = '';
  assert.equal(parseReleaseFeed(badChecksum), null);

  const renamedInstallerFeed = structuredClone(installerFeed);
  renamedInstallerFeed.version = '1.0.12';
  renamedInstallerFeed.tag = 'v1.0.12';
  renamedInstallerFeed.releaseUrl = 'https://github.com/Zettersten/shhnap/releases/tag/v1.0.12';
  for (const arch of ['x64', 'arm64']) {
    renamedInstallerFeed.downloads[arch].url = `https://github.com/Zettersten/shhnap/releases/download/v1.0.12/Shnapp-win-${arch}.zip`;
    renamedInstallerFeed.installers[arch].url = `https://github.com/Zettersten/shhnap/releases/download/v1.0.12/Shnapp-v1.0.12-${arch}.exe`;
  }
  assert.deepEqual(parseReleaseFeed(renamedInstallerFeed), renamedInstallerFeed);

  const legacyNameOnNewVersion = structuredClone(renamedInstallerFeed);
  legacyNameOnNewVersion.installers.x64.url = installerFeed.installers.x64.url.replaceAll('v1.0.5', 'v1.0.12');
  assert.equal(parseReleaseFeed(legacyNameOnNewVersion), null);
});

test('rejects a feed that could redirect downloads or show a version without valid checksums', () => {
  const redirected = structuredClone(validFeed);
  redirected.downloads.x64.url = 'https://example.com/Shnapp-win-x64.zip';
  assert.equal(parseReleaseFeed(redirected), null);

  const incomplete = structuredClone(validFeed);
  incomplete.downloads.arm64.sha256 = '';
  assert.equal(parseReleaseFeed(incomplete), null);
});

test('extracts only version-specific changes as plain text', () => {
  const notes = '# Shnapp\n\n## New in v1.0.5\n- Add **Settings** with [update details](https://example.com).\n- Keep `Library` captures local.\n\n## What you can do\n- Generic feature list.\n';
  assert.deepEqual(releaseHighlights(notes), [
    'Add Settings with update details.',
    'Keep Library captures local.',
  ]);
});
