import { createHash, randomUUID } from "node:crypto";
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, readFileSync, realpathSync, renameSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { isDeepStrictEqual } from "node:util";
import { fileURLToPath } from "node:url";

const origin = "https://nexclip.157342.xyz";
const remoteDirectory = "/var/www/nexclip-server/releases";
const repository = "yixing233/nexclip";

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function validateRelease(release) {
  if (!/^\d+\.\d+\.\d+$/.test(release.version)
    || release.tag_name !== `v${release.version}`
    || release.filename !== `NexClip_Tauri_${release.version}_x64-setup.exe`
    || release.url !== `${origin}/releases/${release.filename}`
    || !/^[a-f0-9]{64}$/.test(release.sha256)
    || !Number.isSafeInteger(release.size) || release.size <= 0) {
    throw new Error("Invalid Tauri release metadata");
  }
}

export function activateRelease({ directory, stagedName, release }) {
  validateRelease(release);
  if (!/^\.tauri-upload-[a-f0-9-]+\.part$/.test(stagedName)) {
    throw new Error("Invalid staging filename");
  }
  const root = realpathSync(directory);
  const manifestPath = join(root, "version.json");
  const original = readFileSync(manifestPath, "utf8");
  const manifest = JSON.parse(original);
  if (!manifest || Array.isArray(manifest) || typeof manifest !== "object") {
    throw new Error("Invalid existing release manifest");
  }
  const stagedPath = join(root, stagedName);
  const stagedBytes = readFileSync(stagedPath);
  if (stagedBytes.length !== release.size || sha256(stagedBytes) !== release.sha256) {
    throw new Error("Uploaded installer integrity mismatch");
  }
  const installerPath = join(root, release.filename);
  if (existsSync(installerPath) && sha256(readFileSync(installerPath)) !== release.sha256) {
    throw new Error("An installer with different content already exists for this version");
  }

  // Read the current server manifest so all other release channels stay intact.
  const nextManifest = { ...manifest, windowsTauri: release };
  const operation = randomUUID();
  const backup = `${manifestPath}.bak-tauri-${operation}`;
  const temporary = `${manifestPath}.${operation}.tmp`;
  copyFileSync(manifestPath, backup);
  writeFileSync(temporary, `${JSON.stringify(nextManifest, null, 2)}\n`, { flag: "wx", mode: 0o644 });
  if (readFileSync(manifestPath, "utf8") !== original) {
    unlinkSync(temporary);
    throw new Error("Release manifest changed during publication; retry with the current manifest");
  }
  if (existsSync(installerPath)) unlinkSync(stagedPath);
  else renameSync(stagedPath, installerPath);
  renameSync(temporary, manifestPath);
  return { version: release.version, filename: release.filename, sha256: release.sha256, backup };
}

async function publicManifest() {
  const response = await fetch(`${origin}/releases/version.json`, {
    cache: "no-store", signal: AbortSignal.timeout(20000),
  });
  if (!response.ok) throw new Error(`Manifest HTTP ${response.status}`);
  return response.json();
}

async function publish(version, host = "root@47.116.39.247") {
  if (!/^\d+\.\d+\.\d+$/.test(version) || !/^[a-zA-Z0-9_][a-zA-Z0-9_.@-]*$/.test(host)) {
    throw new Error("Usage: node scripts/publish-direct.mjs --publish VERSION [SSH_HOST]");
  }
  const filename = `NexClip_Tauri_${version}_x64-setup.exe`;
  const installer = fileURLToPath(new URL(`../release-out/${filename}`, import.meta.url));
  const bytes = readFileSync(installer);
  const digest = sha256(bytes);
  const github = JSON.parse(execFileSync("gh", ["release", "view", `v${version}`, "--repo", repository,
    "--json", "tagName,isDraft,isPrerelease,assets"], { encoding: "utf8" }));
  const asset = github.assets.find((item) => item.name === filename);
  if (github.isDraft || github.isPrerelease || github.tagName !== `v${version}`
    || !asset || asset.digest !== `sha256:${digest}` || asset.size !== bytes.length) {
    throw new Error("Local installer does not match the published GitHub release");
  }
  const release = {
    version, tag_name: `v${version}`, name: `NexClip Tauri v${version} - Windows`,
    body: readFileSync(new URL(`../docs/releases/v${version}.md`, import.meta.url), "utf8").trim(),
    html_url: `https://github.com/${repository}/releases/tag/v${version}`,
    filename, url: `${origin}/releases/${filename}`, size: bytes.length,
    file_size_bytes: bytes.length, sha256: digest,
  };
  validateRelease(release);
  const before = await publicManifest();
  const stagedName = `.tauri-upload-${randomUUID()}.part`;
  const sshOptions = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"];
  console.log(`Uploading ${filename} (${statSync(installer).size} bytes)`);
  execFileSync("scp", [...sshOptions, installer,
    `${host}:${remoteDirectory}/${stagedName}`], { stdio: "inherit" });

  // Source and metadata go through stdin, never through remote shell interpolation.
  const payload = { directory: remoteDirectory, stagedName, release };
  const remoteSource = `${readFileSync(new URL(import.meta.url), "utf8")}\nconsole.log(JSON.stringify(activateRelease(${JSON.stringify(payload)})));\n`;
  const result = execFileSync("ssh", [...sshOptions, host, "node --input-type=module"], {
    input: remoteSource, encoding: "utf8", timeout: 60000,
  });
  console.log(result.trim());
  const after = await publicManifest();
  const { windowsTauri: previousTauri, ...previousChannels } = before;
  const { windowsTauri: publishedTauri, ...currentChannels } = after;
  if (!isDeepStrictEqual(previousChannels, currentChannels) || !isDeepStrictEqual(publishedTauri, release)) {
    throw new Error("Public manifest verification failed");
  }
  const download = await fetch(release.url, { signal: AbortSignal.timeout(60000) });
  if (!download.ok) throw new Error(`Installer HTTP ${download.status}`);
  const downloaded = Buffer.from(await download.arrayBuffer());
  if (downloaded.length !== release.size || sha256(downloaded) !== digest) {
    throw new Error("Public installer integrity mismatch");
  }
  console.log("Verified: direct manifest, preserved channels, public installer size and SHA-256");
}

if (process.argv[2] === "--publish") {
  await publish(process.argv[3], process.argv[4]);
}
