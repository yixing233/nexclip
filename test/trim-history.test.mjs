import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { openDb } from '../dist/db.js';
import { SyncService } from '../dist/service.js';

/** 只用到广播方法,测试里全部空实现(在线设备集合为空)。 */
function fakeHub() {
  const noop = () => {};
  return {
    broadcastUpdated: noop,
    broadcastUpdatedTo: noop,
    broadcastCleared: noop,
    broadcastDevicesChanged: noop,
    disconnectDevice: noop,
    onlineDeviceIds: () => new Set(),
  };
}

function testConfig(dir, maxHistoryCount) {
  return {
    port: 0,
    maxHistoryCount,
    databasePath: join(dir, 'syncclipboard.db'),
    imageStoragePath: join(dir, 'images'),
    maxImageSizeBytes: 10 * 1024 * 1024,
    onlineThresholdSeconds: 120,
    webDist: null,
    pairingCodeTtlSeconds: 600,
    adminUsername: 'admin',
    adminPassword: 'x',
    sessionTtlHours: 24,
    startedAt: new Date(),
  };
}

/** WAL 模式下必须先关连接再删目录,否则 Windows 上 unlink 报 EBUSY。 */
async function makeService(t, maxHistoryCount, hub = fakeHub()) {
  const dir = await mkdtemp(join(tmpdir(), 'nexclip-trim-'));
  const cfg = testConfig(dir, maxHistoryCount);
  const db = openDb(cfg);
  t.after(async () => {
    db.close();
    await rm(dir, { recursive: true, force: true });
  });
  return { svc: new SyncService(db, cfg, hub), cfg };
}

function pushText(svc, text) {
  return svc.uploadText(text, null, 'dev-a', 'DeviceA', 'Windows', null, null, false);
}

test('trimHistory 不淘汰已收藏文本条目', async (t) => {
  const { svc } = await makeService(t, 5);

  // 最旧的 3 条全部收藏
  const starredIds = [];
  for (let i = 0; i < 3; i++) starredIds.push(pushText(svc, `starred-${i}`).entry.id);
  for (const id of starredIds) {
    assert.ok(svc.updateEntryMetadata(id, { starred: true, remark: '重要' }), `条目 ${id} 应能收藏`);
  }

  // 再推 10 条未收藏:总数 13,远超上限 5
  for (let i = 0; i < 10; i++) pushText(svc, `filler-${i}`);

  for (const id of starredIds) {
    assert.ok(svc.getById(id), `收藏条目 ${id} 不应被 trimHistory 删除`);
  }
  const unstarred = svc.getHistory(0, 200).items.filter((e) => !e.starred);
  assert.ok(
    unstarred.length <= 5,
    `未收藏条目应被压到上限 5 以内,实际 ${unstarred.length}`,
  );
});

test('trimHistory 不淘汰已收藏图片条目,且图片文件保留', async (t) => {
  const { svc, cfg } = await makeService(t, 3);
  const png = Buffer.from('89504e470d0a1a0a', 'hex'); // PNG magic,内容不校验

  const oldest = svc.uploadImage('a.png', png, 'dev-a', 'DeviceA', null);
  svc.updateEntryMetadata(oldest.id, { starred: true });
  const ref = oldest.imageRef;
  assert.ok(ref && existsSync(join(cfg.imageStoragePath, ref)), '收藏图片应已落盘');

  // 多张不同内容的图片把未收藏部分推到上限之外
  for (let i = 0; i < 6; i++) {
    svc.uploadImage(`b${i}.png`, Buffer.concat([png, Buffer.from([i])]), 'dev-a', 'DeviceA', null);
  }

  assert.ok(svc.getById(oldest.id), '收藏的图片条目不应被删除');
  assert.ok(existsSync(join(cfg.imageStoragePath, ref)), '收藏条目的图片文件不应被删除');
});

test('trimHistory 按未收藏条目数计算上限,收藏不占额度', async (t) => {
  const { svc } = await makeService(t, 4);

  // 20 条收藏 + 4 条未收藏:收藏不占上限额度,未收藏刚好等于上限。
  // 每条必须在推下一条之前就收藏,否则它会在下一次上传的 trim 里作为未收藏条目被淘汰。
  const starredIds = [];
  for (let i = 0; i < 20; i++) {
    const id = pushText(svc, `s-${i}`).entry.id;
    svc.updateEntryMetadata(id, { starred: true });
    starredIds.push(id);
  }
  for (let i = 0; i < 4; i++) pushText(svc, `u-${i}`);

  for (const id of starredIds) assert.ok(svc.getById(id), `收藏条目 ${id} 应保留`);
  assert.equal(svc.getHistory(0, 200).items.length, 24);

  // 再多一条未收藏,只应淘汰最旧的未收藏条目
  const extra = pushText(svc, 'u-extra').entry;
  for (const id of starredIds) assert.ok(svc.getById(id), `淘汰后收藏条目 ${id} 仍应保留`);
  assert.ok(svc.getById(extra.id), '最新条目应保留');
  assert.equal(svc.getHistory(0, 200).items.filter((e) => !e.starred).length, 4);
});

test('取消收藏后条目重新参与淘汰', async (t) => {
  const { svc } = await makeService(t, 3);

  const oldest = pushText(svc, 'will-unstar').entry.id;
  svc.updateEntryMetadata(oldest, { starred: true });
  for (let i = 0; i < 5; i++) pushText(svc, `f-${i}`);
  assert.ok(svc.getById(oldest), '收藏期间应保留');

  svc.updateEntryMetadata(oldest, { starred: false });
  pushText(svc, 'after-unstar');

  assert.ok(!svc.getById(oldest), '取消收藏后应作为最旧条目被淘汰');
  assert.equal(svc.getHistory(0, 200).items.filter((e) => !e.starred).length, 3);
});

test('updateEntryMetadata 归一化备注,且不广播 ClipboardUpdated', async (t) => {
  const broadcasts = [];
  const hub = { ...fakeHub(), broadcastUpdated: (dto, userId) => broadcasts.push({ dto, userId }) };
  const { svc } = await makeService(t, 100, hub);

  const { entry } = pushText(svc, 'mark me');
  const before = broadcasts.length;

  const updated = svc.updateEntryMetadata(entry.id, { starred: true, remark: '  备注内容  ' });
  assert.equal(updated.starred, true);
  assert.equal(updated.remark, '备注内容', '备注应去除首尾空白');
  // 广播会被客户端当成"来了新剪贴板"而触发粘贴,收藏/备注必须静默
  assert.equal(broadcasts.length, before, '更新元数据不应广播 ClipboardUpdated');

  const cleared = svc.updateEntryMetadata(entry.id, { remark: '   ' });
  assert.equal(cleared.remark, null, '纯空白备注应归一化为 null');

  assert.equal(svc.updateEntryMetadata(99999, { starred: true }), null, '不存在的条目返回 null');
});

test('updateEntryMetadata 按字段局部更新,未提及的字段保持原值', async (t) => {
  const { svc } = await makeService(t, 100);
  const { entry } = pushText(svc, 'partial update');

  // 另一台设备写了备注
  svc.updateEntryMetadata(entry.id, { remark: '发票抬头' });

  // 本设备只切收藏(本地并不知道服务端已有备注),不能把备注清掉
  const starred = svc.updateEntryMetadata(entry.id, { starred: true });
  assert.equal(starred.starred, true);
  assert.equal(starred.remark, '发票抬头', '只切收藏不应清空已有备注');

  // 只改备注,收藏位保持
  const remarked = svc.updateEntryMetadata(entry.id, { remark: '新备注' });
  assert.equal(remarked.remark, '新备注');
  assert.equal(remarked.starred, true, '只改备注不应重置收藏位');

  // 空 patch 不动任何字段
  const untouched = svc.updateEntryMetadata(entry.id, {});
  assert.equal(untouched.starred, true);
  assert.equal(untouched.remark, '新备注');

  // 显式传 null 才清除备注
  const cleared = svc.updateEntryMetadata(entry.id, { remark: null });
  assert.equal(cleared.remark, null);
  assert.equal(cleared.starred, true);
});
