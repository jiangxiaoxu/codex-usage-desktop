import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageMetadata = JSON.parse(await readFile(path.join(workspace, "package.json"), "utf8"));
const { name, version, build } = packageMetadata;
const outputDirectory = path.join(workspace, build.directories.output);
const installerFileName = `${name}-setup-${version}-x64.exe`;
const installer = path.join(outputDirectory, installerFileName);
const installerBlockmap = `${installer}.blockmap`;
const packageArchive = path.join(outputDirectory, `${name}-${version}-x64.nsis.7z`);
const updateMetadata = path.join(outputDirectory, "latest.yml");
const packagedUpdateConfiguration = path.join(outputDirectory, "win-unpacked", "resources", "app-update.yml");
const installerStats = await stat(installer);
const installerBlockmapStats = await stat(installerBlockmap);
let archiveStats = null;
try {
  archiveStats = await stat(packageArchive);
} catch (error) {
  if (!(error instanceof Error && "code" in error && error.code === "ENOENT")) throw error;
}

if (archiveStats !== null && installerStats.size <= archiveStats.size) {
  throw new Error(`NSIS installer is incomplete: ${installer} (${installerStats.size} bytes) does not embed ${packageArchive} (${archiveStats.size} bytes).`);
}
if (installerStats.size < 10 * 1024 * 1024) {
  throw new Error(`NSIS installer is unexpectedly small: ${installer} (${installerStats.size} bytes).`);
}
if (installerBlockmapStats.size === 0) {
  throw new Error(`NSIS installer blockmap is empty: ${installerBlockmap}`);
}

const updateMetadataText = await readFile(updateMetadata, "utf8");
if (!updateMetadataText.includes(`version: ${version}`)) {
  throw new Error(`Updater metadata does not describe version ${version}: ${updateMetadata}`);
}
if (!updateMetadataText.includes(`- url: ${installerFileName}`)) {
  throw new Error(`Updater metadata does not reference the NSIS installer: ${updateMetadata}`);
}
if (!/^    sha512: \S+$/m.test(updateMetadataText) || !/^    size: \d+$/m.test(updateMetadataText)) {
  throw new Error(`Updater metadata is missing the installer integrity fields: ${updateMetadata}`);
}

const packagedUpdateConfigurationText = await readFile(packagedUpdateConfiguration, "utf8");
if (!packagedUpdateConfigurationText.includes("provider: github") || !packagedUpdateConfigurationText.includes("owner: jiangxiaoxu") || !packagedUpdateConfigurationText.includes("repo: codex-usage-desktop")) {
  throw new Error(`Packaged updater configuration is incomplete: ${packagedUpdateConfiguration}`);
}
