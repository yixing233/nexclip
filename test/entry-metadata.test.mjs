import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

// 端点级回归:PUT /api/clipboard/{id} 与 POST /api/clipboard/batch 的字段语义,
// 桌面端收藏开关依赖"只提交 starred、不碰 remark"来避免清掉别的设备写的备注。
const port = 17_000 + Math.floor(Math.random() * 2_000);
const temp = await mkdtemp(join(tmpdir(), 'nexclip-metadata-'));
const base = `http://127.0.0.1:${port}`;

const child = spawn(process.execPath, ['dist/server.js'], {
  cwd: new URL('..', import.meta.url),
  env: {
    ...process.env,
    SC_PORT: String(port),
    SC_DB_PATH: join(temp, 'syncclipboard.db'),
    SC_IMAGE_PATH: join(temp, 'images'),
    SC_MAX_HISTORY: '1000',
    ADMIN_PASSWORD: 'test-password',
  },
  stdio: ['ignore', 'pipe', 'pipe'],
});
let output = '';
child.stdout.on('data', (c) => { output += c.toString(); });
child.stderr.on('data', (c) => { output += c.toString(); });

async function waitForHealth() {
  for (let i = 0; i < 80; i++) {
    try {
      if ((await fetch(`${base}/api/health`)).ok) return;
    } catch { /* starting */ }
    await delay(100);
  }
  throw new Error(`server did not start\n${output}`);
}

function deviceHeaders(id, token) {
  return { 'X-Device-Id': id, 'X-Device-Token': token, 'Content-Type': 'application/json' };
}

async function call(path, init = {}) {
  const res = await fetch(base + path, init);
  const text = await res.text();
  let body = null;
  if (text.trim()) { try { body = JSON.parse(text); } catch { body = text; } }
  return { res, body };
}

try {
  await waitForHealth();

  // 配对一台设备(生成方即入组)
  const paired = await call('/api/pairing-codes', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deviceId: 'dev-1', deviceName: 'Device 1' }),
  });
  assert.equal(paired.res.status, 200, JSON.stringify(paired.body));
  const token = paired.body.deviceToken;
  assert.ok(token);

  // 造一条记录
  const push = await call('/api/clipboard', {
    method: 'PUT',
    headers: deviceHeaders('dev-1', token),
    body: JSON.stringify({ text: 'metadata-target', deviceId: 'dev-1', deviceName: 'Device 1' }),
  });
  assert.equal(push.res.status, 200, JSON.stringify(push.body));
  const id = push.body.id;
  assert.ok(id > 0);

  // 1) 只提交 remark
  const remarkOnly = await call(`/api/clipboard/${id}`, {
    method: 'PUT',
    headers: deviceHeaders('dev-1', token),
    body: JSON.stringify({ remark: '发票抬头' }),
  });
  assert.equal(remarkOnly.res.status, 200, JSON.stringify(remarkOnly.body));
  assert.equal(remarkOnly.body.remark, '发票抬头');
  assert.equal(remarkOnly.body.starred, false, '未提交 starred 时不应被置为 true');

  // 2) 只提交 starred(桌面端收藏开关的行为):必须保留已有备注
  const starOnly = await call(`/api/clipboard/${id}`, {
    method: 'PUT',
    headers: deviceHeaders('dev-1', token),
    body: JSON.stringify({ starred: true }),
  });
  assert.equal(starOnly.res.status, 200, JSON.stringify(starOnly.body));
  assert.equal(starOnly.body.starred, true);
  assert.equal(starOnly.body.remark, '发票抬头', '只收藏不应清空已有备注');

  // 3) 显式 remark: null 才清除备注,且不动 starred
  const clearRemark = await call(`/api/clipboard/${id}`, {
    method: 'PUT',
    headers: deviceHeaders('dev-1', token),
    body: JSON.stringify({ remark: null }),
  });
  assert.equal(clearRemark.res.status, 200, JSON.stringify(clearRemark.body));
  assert.equal(clearRemark.body.remark, null, '显式 null 应清除备注');
  assert.equal(clearRemark.body.starred, true, '清除备注不应重置收藏');

  // 4) 批量收藏同样必须保留备注
  const push2 = await call('/api/clipboard', {
    method: 'PUT',
    headers: deviceHeaders('dev-1', token),
    body: JSON.stringify({ text: 'batch-target', deviceId: 'dev-1', deviceName: 'Device 1' }),
  });
  const id2 = push2.body.id;
  await call(`/api/clipboard/${id2}`, {
    method: 'PUT',
    headers: deviceHeaders('dev-1', token),
    body: JSON.stringify({ remark: '批量备注' }),
  });
  const batch = await call('/api/clipboard/batch', {
    method: 'POST',
    headers: deviceHeaders('dev-1', token),
    body: JSON.stringify({ action: 'star', ids: [id2] }),
  });
  assert.equal(batch.res.status, 200, JSON.stringify(batch.body));
  assert.equal(batch.body.changed, 1);

  const afterBatch = await call(`/api/clipboard/${id2}`, {
    method: 'GET',
    headers: deviceHeaders('dev-1', token),
  });
  assert.equal(afterBatch.res.status, 200, JSON.stringify(afterBatch.body));
  assert.equal(afterBatch.body.starred, true);
  assert.equal(afterBatch.body.remark, '批量备注', '批量收藏不应清空备注');

  // 5) 历史列表返回 starred/remark,供跨端读取
  const history = await call('/api/clipboard/history?limit=50', {
    headers: deviceHeaders('dev-1', token),
  });
  assert.equal(history.res.status, 200);
  const found = history.body.items.find((e) => e.id === id2);
  assert.ok(found, '历史列表应包含该条目');
  assert.equal(found.starred, true);
  assert.equal(found.remark, '批量备注');
} finally {
  child.kill('SIGTERM');
  await Promise.race([new Promise((r) => child.once('exit', r)), delay(2_000)]);
  await rm(temp, { recursive: true, force: true });
}

console.log('entry metadata regression passed');
