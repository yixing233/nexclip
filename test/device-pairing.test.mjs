import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { DatabaseSync } from 'node:sqlite';

const port = 15_000 + Math.floor(Math.random() * 2_000);
const temp = await mkdtemp(join(tmpdir(), 'nexclip-pairing-'));
const dbPath = join(temp, 'syncclipboard.db');
const imagePath = join(temp, 'images');
const base = `http://127.0.0.1:${port}`;
const env = {
  ...process.env,
  SC_PORT: String(port),
  SC_DB_PATH: dbPath,
  SC_IMAGE_PATH: imagePath,
  ADMIN_PASSWORD: 'test-password',
  SC_PAIRING_TTL_SECONDS: '120',
};

const child = spawn(process.execPath, ['dist/server.js'], {
  cwd: new URL('..', import.meta.url),
  env,
  stdio: ['ignore', 'pipe', 'pipe'],
});
let output = '';
child.stdout.on('data', (chunk) => { output += chunk.toString(); });
child.stderr.on('data', (chunk) => { output += chunk.toString(); });

async function waitForHealth() {
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(`${base}/api/health`);
      if (res.ok) return;
    } catch { /* server still starting */ }
    await delay(100);
  }
  throw new Error(`server did not start\n${output}`);
}

async function api(path, init = {}) {
  const res = await fetch(base + path, {
    ...init,
    headers: { ...(init.headers ?? {}), ...(init.body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
  });
  const text = await res.text();
  let body = null;
  if (text.trim()) {
    try { body = JSON.parse(text); } catch { body = text; }
  }
  return { res, body };
}

function json(body) {
  return { method: 'POST', body: JSON.stringify(body) };
}

function deviceHeaders(id, token) {
  return { 'X-Device-Id': id, 'X-Device-Token': token };
}

try {
  await waitForHealth();

  const unauth = await api('/api/clipboard');
  assert.equal(unauth.res.status, 401);

  const legacyUnauth = await api('/SyncClipboard.json');
  assert.equal(legacyUnauth.res.status, 401);

  const first = await api('/api/pairing-codes', json({ deviceId: 'device-a', deviceName: 'A' }));
  assert.equal(first.res.status, 200, JSON.stringify(first.body));
  assert.match(first.body.code, /^[0-9]{6}$/);
  assert.ok(first.body.userId);
  assert.ok(first.body.deviceToken);

  // Simulate an existing pre-token client upgraded in place. It must be able
  // to claim exactly one new credential without creating a replacement row.
  const migrationDb = new DatabaseSync(dbPath);
  migrationDb.prepare('INSERT INTO "Devices" ("Id","Name","Platform","Ip","Version","LastSeenAt","Token","PairedAt","UserId","RevokedAt") VALUES (?,?,?,?,?,?,?,?,?,NULL)')
    .run('legacy-device', 'Legacy', 'Windows', null, null, new Date().toISOString().replace('T', ' ').replace('Z', ''), null, null, first.body.userId);
  migrationDb.close();
  const legacyClaim = await api('/api/pairing-codes', json({ deviceId: 'legacy-device', deviceName: 'Legacy' }));
  assert.equal(legacyClaim.res.status, 200, JSON.stringify(legacyClaim.body));
  assert.ok(legacyClaim.body.deviceToken);
  const revokeLegacyCode = await api(`/api/pairing-codes/${legacyClaim.body.code}`, {
    method: 'DELETE',
    headers: deviceHeaders('legacy-device', legacyClaim.body.deviceToken),
  });
  assert.equal(revokeLegacyCode.res.status, 204, JSON.stringify(revokeLegacyCode.body));

  // 单向 6 位验证码即入配对
  const secondPair = await api('/api/pair', json({
    code: first.body.code,
    deviceId: 'device-b',
    deviceName: 'B',
  }));
  assert.equal(secondPair.res.status, 200, JSON.stringify(secondPair.body));
  assert.equal(secondPair.body.status, 'approved');
  assert.equal(secondPair.body.userId, first.body.userId);
  const oldToken = secondPair.body.deviceToken;
  assert.ok(oldToken);
  const webSessionToken = secondPair.body.token;
  assert.ok(webSessionToken);

  const push = await api('/api/clipboard', {
    method: 'PUT',
    headers: { ...deviceHeaders('device-b', oldToken), 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: 'pairing-regression', deviceId: 'device-b', deviceName: 'B' }),
  });
  assert.equal(push.res.status, 200, JSON.stringify(push.body));

  const imageForm = new FormData();
  imageForm.append('file', new Blob([Buffer.from([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }), 'test.png');
  imageForm.append('deviceId', 'device-b');
  imageForm.append('deviceName', 'B');
  const imagePush = await fetch(`${base}/api/clipboard/image`, {
    method: 'POST',
    headers: deviceHeaders('device-b', oldToken),
    body: imageForm,
  });
  const imageBody = await imagePush.json();
  assert.equal(imagePush.status, 200, JSON.stringify(imageBody));
  assert.equal(imageBody.type, 'Image');

  const remove = await api('/api/devices/device-b', {
    method: 'DELETE',
    headers: deviceHeaders('device-a', first.body.deviceToken),
  });
  assert.equal(remove.res.status, 204, JSON.stringify(remove.body));

  const staleWebSession = await api('/api/me', {
    headers: { Authorization: 'Bearer ' + webSessionToken },
  });
  assert.equal(staleWebSession.res.status, 410, JSON.stringify(staleWebSession.body));

  const stalePush = await api('/api/clipboard', {
    method: 'PUT',
    headers: { ...deviceHeaders('device-b', oldToken), 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: 'must-be-rejected', deviceId: 'device-b', deviceName: 'B' }),
  });
  assert.ok([401, 410].includes(stalePush.res.status), JSON.stringify(stalePush.body));

  const staleLegacy = await api('/SyncClipboard.json', {
    headers: deviceHeaders('device-b', oldToken),
  });
  assert.equal(staleLegacy.res.status, 410, JSON.stringify(staleLegacy.body));

  const devicesAfterRemove = await api('/api/devices', {
    headers: deviceHeaders('device-a', first.body.deviceToken),
  });
  assert.equal(devicesAfterRemove.res.status, 200);
  assert.equal(devicesAfterRemove.body.some((d) => d.id === 'device-b'), false);

  const secondCode = await api('/api/pairing-codes', {
    method: 'POST',
    headers: { ...deviceHeaders('device-a', first.body.deviceToken), 'Content-Type': 'application/json' },
    body: JSON.stringify({ deviceId: 'device-a', deviceName: 'A' }),
  });
  assert.equal(secondCode.res.status, 200, JSON.stringify(secondCode.body));

  const rePair = await api('/api/pair', json({
    code: secondCode.body.code,
    deviceId: 'device-b',
    deviceName: 'B re-paired',
  }));
  assert.equal(rePair.res.status, 200, JSON.stringify(rePair.body));
  assert.equal(rePair.body.status, 'approved');
  const newToken = rePair.body.deviceToken;
  assert.ok(newToken && newToken !== oldToken);

  const oldTokenAgain = await api('/api/clipboard', {
    method: 'PUT',
    headers: { ...deviceHeaders('device-b', oldToken), 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: 'old-token-must-stay-dead', deviceId: 'device-b', deviceName: 'B' }),
  });
  assert.ok([401, 410].includes(oldTokenAgain.res.status), JSON.stringify(oldTokenAgain.body));

  const newTokenPush = await api('/api/clipboard', {
    method: 'PUT',
    headers: { ...deviceHeaders('device-b', newToken), 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: 'new-token-works', deviceId: 'device-b', deviceName: 'B re-paired' }),
  });
  assert.equal(newTokenPush.res.status, 200, JSON.stringify(newTokenPush.body));

  const noHubToken = await api('/hubs/clipboard/negotiate?deviceId=device-b');
  assert.equal(noHubToken.res.status, 401);
  const badHubToken = await api('/hubs/clipboard/negotiate?deviceId=device-b&deviceToken=invalid');
  assert.equal(badHubToken.res.status, 401);

  // 验证未认证设备或未绑定设备请求 /api/devices 时不会泄露他人设备
  const unauthDevices = await api('/api/devices');
  assert.equal(unauthDevices.res.status, 200);
  assert.deepEqual(unauthDevices.body, []);

  // 模拟未配对新设备请求自身的 /api/devices?deviceId=new-unknown-device
  const newDeviceQuery = await api('/api/devices?deviceId=new-unknown-device');
  assert.equal(newDeviceQuery.res.status, 200);
  assert.deepEqual(newDeviceQuery.body, []);

  // --- 测试完整流程：新用户组创建 -> 两个设备配对 -> 再次生成配对码保持同用户组 -> 第三个设备加入 ---
  // 1. 用户组1已由 device-a 与 device-b 组成（userId: first.body.userId）
  // 验证从 device-b 再次生成配对码，其 userId 必须仍为该用户组
  const group1CodeFromB = await api('/api/pairing-codes', {
    method: 'POST',
    headers: { ...deviceHeaders('device-b', newToken), 'Content-Type': 'application/json' },
    body: JSON.stringify({ deviceId: 'device-b', deviceName: 'B re-paired' }),
  });
  assert.equal(group1CodeFromB.res.status, 200);
  assert.equal(group1CodeFromB.body.userId, first.body.userId);

  // 2. 第三台设备 device-c 加入用户组1
  const pairDeviceC = await api('/api/pair', json({
    code: group1CodeFromB.body.code,
    deviceId: 'device-c',
    deviceName: 'C',
  }));
  assert.equal(pairDeviceC.res.status, 200);
  assert.equal(pairDeviceC.body.userId, first.body.userId);
  const tokenC = pairDeviceC.body.deviceToken;
  assert.ok(tokenC);

  // 3. 验证此时用户组1中的设备列表包含 A, B, C 以及测试前置的 legacy-device（共4台）
  const group1Devices = await api('/api/devices', {
    headers: deviceHeaders('device-c', tokenC),
  });
  assert.equal(group1Devices.res.status, 200);
  assert.equal(group1Devices.body.length, 4);
  const group1Ids = group1Devices.body.map((d) => d.id).sort();
  assert.deepEqual(group1Ids, ['device-a', 'device-b', 'device-c', 'legacy-device']);

  // 4. 全新用户设备 X 首次生成配对码，必须创建全新的独立用户组 userX
  const group2First = await api('/api/pairing-codes', json({
    deviceId: 'device-x',
    deviceName: 'Device X',
  }));
  assert.equal(group2First.res.status, 200);
  assert.ok(group2First.body.userId);
  assert.notEqual(group2First.body.userId, first.body.userId); // 必须是不同用户组
  const tokenX = group2First.body.deviceToken;
  assert.ok(tokenX);

  // 设备 X 查看设备列表，只能看到自己，绝看不到用户组1的设备
  const group2Devices = await api('/api/devices', {
    headers: deviceHeaders('device-x', tokenX),
  });
  assert.equal(group2Devices.res.status, 200);
  assert.equal(group2Devices.body.length, 1);
  assert.equal(group2Devices.body[0].id, 'device-x');

  // 5. 设备 Y 通过设备 X 的配对码加入用户组2
  const pairDeviceY = await api('/api/pair', json({
    code: group2First.body.code,
    deviceId: 'device-y',
    deviceName: 'Device Y',
  }));
  assert.equal(pairDeviceY.res.status, 200);
  assert.equal(pairDeviceY.body.userId, group2First.body.userId);
  const tokenY = pairDeviceY.body.deviceToken;
  assert.ok(tokenY);

  // 用户组2内查设备列表：包含 X 和 Y（2台），不含 A, B, C
  const group2DevicesAfterY = await api('/api/devices', {
    headers: deviceHeaders('device-y', tokenY),
  });
  assert.equal(group2DevicesAfterY.res.status, 200);
  assert.equal(group2DevicesAfterY.body.length, 2);
  const group2Ids = group2DevicesAfterY.body.map((d) => d.id).sort();
  assert.deepEqual(group2Ids, ['device-x', 'device-y']);
} finally {
  child.kill('SIGTERM');
  await Promise.race([
    new Promise((resolve) => child.once('exit', resolve)),
    delay(2_000),
  ]);
  await rm(temp, { recursive: true, force: true });
}

console.log('device pairing regression passed');
