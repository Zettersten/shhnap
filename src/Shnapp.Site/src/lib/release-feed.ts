export const releaseFeedUrl = 'https://raw.githubusercontent.com/Zettersten/shhnap/release-metadata/latest.json';

type Architecture = 'x64' | 'arm64';
type ReleaseAsset = { url: string; sha256: string };

interface ReleaseFeedBase {
  version: string;
  tag: string;
  publishedAt: string;
  releaseUrl: string;
  notes: string;
  downloads: Record<Architecture, ReleaseAsset>;
}

export type ReleaseFeed =
  | (ReleaseFeedBase & { schemaVersion: 1 })
  | (ReleaseFeedBase & { schemaVersion: 2; installers: Record<Architecture, ReleaseAsset> });

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Keep an unavailable or malformed feed from replacing working static download links. */
export function parseReleaseFeed(value: unknown): ReleaseFeed | null {
  if (!isObject(value) || (value.schemaVersion !== 1 && value.schemaVersion !== 2) ||
      typeof value.version !== 'string' ||
      !/^[1-9]\d*\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value.version) ||
      value.tag !== `v${value.version}` ||
      typeof value.publishedAt !== 'string' || !Number.isFinite(Date.parse(value.publishedAt)) ||
      value.releaseUrl !== `https://github.com/Zettersten/shhnap/releases/tag/${value.tag}` ||
      typeof value.notes !== 'string' || value.notes.length > 30000 || !isObject(value.downloads)) {
    return null;
  }

  for (const arch of ['x64', 'arm64'] as const) {
    const download = value.downloads[arch];
    if (!isObject(download) ||
        download.url !== `https://github.com/Zettersten/shhnap/releases/download/${value.tag}/Shnapp-win-${arch}.zip` ||
        typeof download.sha256 !== 'string' || !/^[a-f\d]{64}$/i.test(download.sha256)) {
      return null;
    }

    if (value.schemaVersion === 2) {
      if (!isObject(value.installers)) return null;
      const installer = value.installers[arch];
      if (!isObject(installer) ||
          installer.url !== `https://github.com/Zettersten/shhnap/releases/download/${value.tag}/ErikZettersten.Shnapp.${arch}-win-${arch}-Setup.exe` ||
          typeof installer.sha256 !== 'string' || !/^[a-f\d]{64}$/i.test(installer.sha256)) {
        return null;
      }
    }
  }

  return value as unknown as ReleaseFeed;
}

export function preferredReleaseAsset(feed: ReleaseFeed, architecture: Architecture): ReleaseAsset {
  return feed.schemaVersion === 2 ? feed.installers[architecture] : feed.downloads[architecture];
}

/** Only release-specific bullets are shown inline; the full notes remain on GitHub. */
export function releaseHighlights(notes: string): string[] {
  const lines = notes.split(/\r?\n/);
  const highlights: string[] = [];
  let inChanges = false;

  for (const line of lines) {
    if (/^##\s+(?:New in\b|What's changed\b)/i.test(line)) {
      inChanges = true;
      continue;
    }
    if (inChanges && /^##\s+/.test(line)) break;
    if (!inChanges) continue;

    const match = line.match(/^\s*[-*]\s+(.+)/);
    if (!match) continue;
    const plainText = match[1]
      .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1')
      .replace(/[*_`]/g, '')
      .trim();
    if (plainText) highlights.push(plainText);
    if (highlights.length === 3) break;
  }

  return highlights;
}
