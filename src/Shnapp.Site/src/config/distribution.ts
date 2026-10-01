/** Update these flags only after the linked public assets exist. The site renders them at build time. */
export const distribution = {
  publicRepository: true,
  /** Requires both ZIPs and matching .sha256 files in the latest stable GitHub Release. */
  downloadsLive: true,
  /** Requires both files under public/demos/. */
  workflowVideoLive: true,
  repositoryUrl: 'https://github.com/Zettersten/shhnap',
  contactEmail: 'erik@zettersten.com',
  builds: {
    x64: {
      fileName: 'Shnapp-win-x64.zip',
      label: 'Windows x64',
      processor: 'Intel and AMD PCs',
    },
    arm64: {
      fileName: 'Shnapp-win-arm64.zip',
      label: 'Windows ARM64',
      processor: 'ARM-based PCs',
    },
  },
} as const;

export const releaseUrl = `${distribution.repositoryUrl}/releases`;
export const latestReleaseUrl = `${distribution.repositoryUrl}/releases/latest`;
export const downloadsAvailable = distribution.publicRepository && distribution.downloadsLive;

export function assetUrl(fileName: string) {
  return `${latestReleaseUrl}/download/${fileName}`;
}

export const issueUrls = {
  chooser: `${distribution.repositoryUrl}/issues/new/choose`,
  bug: `${distribution.repositoryUrl}/issues/new?template=01-bug.yml`,
  feature: `${distribution.repositoryUrl}/issues/new?template=02-feature-request.yml`,
} as const;

export type CatalogState = 'planned' | 'awaiting-listing' | 'live';

export interface CatalogChannel {
  id: string;
  name: string;
  state: CatalogState;
  listingUrl: string | null;
  installCommand: string | null;
  updateCommand: string;
  description: string;
}

/** Set a channel to live only after its public catalog listing opens. */
export const catalogChannels: CatalogChannel[] = [
  {
    id: 'winget',
    name: 'WinGet',
    state: 'awaiting-listing',
    listingUrl: null,
    installCommand: 'winget install --id Zettersten.Shnapp -e',
    updateCommand: 'winget upgrade --id Zettersten.Shnapp -e',
    description: 'The Windows Package Manager catalog.',
  },
  {
    id: 'scoop',
    name: 'Scoop',
    state: 'live',
    listingUrl: 'https://github.com/Zettersten/scoop-bucket/blob/master/bucket/shnapp.json',
    installCommand: 'scoop bucket add shnapp https://github.com/Zettersten/scoop-bucket\nscoop install shnapp/shnapp',
    updateCommand: 'scoop update shnapp',
    description: 'A command-line route for portable apps.',
  },
  {
    id: 'chocolatey',
    name: 'Chocolatey',
    state: 'planned',
    listingUrl: null,
    installCommand: 'choco install shnapp',
    updateCommand: 'choco upgrade shnapp',
    description: 'A managed package for Windows.',
  },
  {
    id: 'microsoft-store',
    name: 'Microsoft Store',
    state: 'planned',
    listingUrl: null,
    installCommand: null,
    updateCommand: 'Update through Microsoft Store',
    description: 'A Store-managed install and update path.',
  },
];
