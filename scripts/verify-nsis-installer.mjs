import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageMetadata = JSON.parse(await readFile(path.join(workspace, "package.json"), "utf8"));
const { name, version, build } = packageMetadata;
const outputDirectory = path.join(workspace, build.directories.output);
const installer = path.join(outputDirectory, `${build.productName} Setup ${version}.exe`);
const packageArchive = path.join(outputDirectory, `${name}-${version}-x64.nsis.7z`);
const installerStats = await stat(installer);
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
