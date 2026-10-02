import assert from 'node:assert/strict';
import test from 'node:test';
import { parseReleaseFeed, releaseHighlights } from '../src/lib/release-feed.ts';

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
