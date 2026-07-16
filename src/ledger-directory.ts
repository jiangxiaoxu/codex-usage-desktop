import path from "node:path";

export interface LedgerDirectoryInput {
  readonly overrideDirectory: string | undefined;
  readonly localAppDataDirectory: string | undefined;
  readonly userDataDirectory: string;
  readonly productName: string;
}

export function resolveLedgerDirectory(input: LedgerDirectoryInput): string {
  const overrideDirectory = input.overrideDirectory?.trim();
  if (overrideDirectory) return path.resolve(overrideDirectory);
  const localAppDataDirectory = input.localAppDataDirectory?.trim();
  if (localAppDataDirectory) return path.join(localAppDataDirectory, input.productName);
  return input.userDataDirectory;
}
