import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { activateRelease } from "./publish-direct.mjs";

function fixture(t) {
  const prefix = join(realpathSync(tmpdir()), "nexclip-direct-test-");
  const directory = mkdtempSync(prefix);
  t.after(() => {
    assert.ok(realpathSync(directory).startsWith(prefix));
    rmSync(directory, { recursive: true, force: true });
  });
  const original = { version: "20261112.01", windows: { version: "20261112.01", custom: 42 },
    android: { version: "20261005.01" }, futureChannel: { enabled: true } };
  const bytes = Buffer.from("installer fixture");
  const release = { version: "2.0.0", tag_name: "v2.0.0", filename: "NexClip_Tauri_2.0.0_x64-setup.exe",
    url: "https://nexclip.157342.xyz/releases/NexClip_Tauri_2.0.0_x64-setup.exe",
    size: bytes.length, sha256: createHash("sha256").update(bytes).digest("hex") };
  const stagedName = ".tauri-upload-1234.part";
  writeFileSync(join(directory, "version.json"), JSON.stringify(original));
  writeFileSync(join(directory, stagedName), bytes);
  return { directory, stagedName, release, original, bytes };
}

test("publishes Tauri without changing old or unknown channels and keeps a backup", (t) => {
  const data = fixture(t);
  const result = activateRelease(data);
  assert.deepEqual(JSON.parse(readFileSync(join(data.directory, "version.json"))),
    { ...data.original, windowsTauri: data.release });
  assert.deepEqual(JSON.parse(readFileSync(result.backup)), data.original);
  assert.deepEqual(readFileSync(join(data.directory, data.release.filename)), data.bytes);
  assert.equal(existsSync(join(data.directory, data.stagedName)), false);
});

test("corrupt uploads leave the manifest and published installer untouched", (t) => {
  const data = fixture(t);
  writeFileSync(join(data.directory, data.stagedName), "corrupt");
  assert.throws(() => activateRelease(data), /integrity mismatch/);
  assert.deepEqual(JSON.parse(readFileSync(join(data.directory, "version.json"))), data.original);
  assert.equal(existsSync(join(data.directory, data.release.filename)), false);
});

test("an existing release asset is immutable but identical publication can be retried", (t) => {
  const data = fixture(t);
  const path = join(data.directory, data.release.filename);
  writeFileSync(path, "different release");
  assert.throws(() => activateRelease(data), /different content/);
  assert.equal(readFileSync(path, "utf8"), "different release");
  assert.deepEqual(JSON.parse(readFileSync(join(data.directory, "version.json"))), data.original);
  writeFileSync(path, data.bytes);
  activateRelease(data);
  assert.deepEqual(readFileSync(path), data.bytes);
});

test("rejects wrong channel filenames and staging paths before writing", (t) => {
  const data = fixture(t);
  assert.throws(() => activateRelease({ ...data, stagedName: "../outside.part" }), /staging filename/);
  assert.throws(() => activateRelease({ ...data, release: { ...data.release,
    filename: "NexClip_Setup_v20261112.01_x64.exe" } }), /release metadata/);
  assert.deepEqual(JSON.parse(readFileSync(join(data.directory, "version.json"))), data.original);
});
