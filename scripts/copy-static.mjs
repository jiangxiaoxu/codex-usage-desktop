import { cp, mkdir, rm } from "node:fs/promises";

await rm("dist/renderer.html", { force: true });
await rm("dist/styles.css", { force: true });
await mkdir("dist", { recursive: true });
await cp("src/renderer.html", "dist/renderer.html");
await cp("src/styles.css", "dist/styles.css");
