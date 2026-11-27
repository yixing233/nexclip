import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { mkdir } from "node:fs/promises";

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.NEXCLIP_PLAYWRIGHT || "playwright");
const baseUrl = process.env.NEXCLIP_TEST_URL || "http://127.0.0.1:1420";
const browser = await chromium.launch({ channel: "msedge", headless: true });
const results = [];
const output = new URL("../.zcode/ui-regression/", import.meta.url);
await mkdir(output, { recursive: true });

async function openPage(label = "main", width = 420) {
  const page = await browser.newPage({ viewport: { width, height: 560 } });
  await page.addInitScript(({ label }) => {
    const state = window.fixture = { calls: [], listeners: new Map(), callbacks: new Map(), next: 1, delay: {}, label };
    const now = Date.now();
    state.history = Array.from({ length: 160 }, (_, index) => ({
      id: 160 - index, entryType: "Text", text: `history-${160 - index} sample`,
      html: null, imagePath: null, files: [], deviceId: "self", deviceName: "Desktop",
      createdAt: now - index * 1000, origin: 0, starred: false, oversized: false, isManual: false,
    }));
    state.messages = Array.from({ length: 30 }, (_, index) => ({
      id: index + 1, entryType: "Text", text: `chat-${index + 1}`, imagePath: null,
      isFromSelf: index % 2 === 1, createdAt: now - (30 - index) * 1000, deviceName: "Phone",
    }));
    state.files = [{ fileId: "file-a", fileName: "before-last.bin", size: 120, deviceId: "self",
      deviceName: "Desktop", isFromSelf: true, createdAt: new Date(now - 1500).toISOString(), receivedAt: now }];
    state.settings = {
      themeMode: "light", iconLibrary: "lucide", rememberScrollPosition: true, isPaired: true,
      serverUrl: "http://fixture", deviceId: "self", deviceName: "Desktop", customFilteredProcesses: [],
      richTextEnabled: true, hotkeys: {}, maxHistory: 1000, storageDir: "", pasteKey: "CtrlV",
    };
    window.__TAURI_INTERNALS__ = {
      metadata: { currentWindow: { label }, currentWebview: { label } },
      transformCallback(fn) { const id = state.next++; state.callbacks.set(id, fn); return id; },
      unregisterCallback(id) { state.callbacks.delete(id); },
      convertFileSrc(path) { return path; },
      async invoke(cmd, args = {}) {
        state.calls.push({ cmd, args });
        if (cmd === "plugin:event|listen") {
          const id = state.next++;
          state.listeners.set(id, args);
          await new Promise((resolve) => setTimeout(resolve, 40));
          return id;
        }
        if (cmd === "plugin:event|unlisten") { state.listeners.delete(args.eventId); return; }
        if (cmd === "settings_get") return state.settings;
        if (cmd === "settings_set") { Object.assign(state.settings, args.patch); return; }
        if (cmd === "history_query") {
          let rows = state.history.filter((row) => (!args.entryType || row.entryType === args.entryType)
            && (!args.search || row.text?.includes(args.search)) && (!args.starredOnly || row.starred)
            && (!args.urlOnly || row.text?.startsWith("https://")));
          rows = rows.slice(args.offset, args.offset + args.limit);
          if (state.delay[args.search]) await new Promise((resolve) => setTimeout(resolve, state.delay[args.search]));
          return rows;
        }
        if (cmd === "history_delete") { state.history = state.history.filter((row) => row.id !== args.id); return; }
        if (cmd === "history_toggle_star") { state.history.find((row) => row.id === args.id).starred = args.starred; return; }
        if (cmd === "chat_query") return [...state.messages];
        if (cmd === "transfer_files_query") return [...state.files];
        if (cmd === "devices_cached" || cmd === "sync_devices_refresh") return [{ id: "phone", name: "Phone", online: true }];
        if (cmd === "sync_get_state") return { status: "connected", ...state.settings };
        if (cmd === "history_stats") return { total: 160, images: 0, files: 0, texts: 160, starred: 0, storageBytes: 0 };
        if (cmd === "app_info") return { version: "fixture", os: "Windows" };
        if (cmd === "update_check_state") return null;
        if (cmd === "smart_detect") return null;
        if (cmd === "pick_images" || cmd === "pick_files") return [];
        if (cmd === "sync_send_text") {
          state.messages.push({ id: 99, text: args.text, entryType: "Text", isFromSelf: true, createdAt: Date.now() });
        }
      },
    };
    window.__TAURI_EVENT_PLUGIN_INTERNALS__ = {
      unregisterListener(_event, id) { state.listeners.delete(id); },
    };
    state.emit = (event, payload) => {
      for (const [id, listener] of state.listeners) {
        if (listener.event === event) state.callbacks.get(listener.handler)?.({ event, id, payload });
      }
    };
  }, { label });
  await page.goto(baseUrl);
  return page;
}

const count = (page, name) => page.evaluate((event) => [...fixture.listeners.values()].filter((item) => item.event === event).length, name);
const chatTab = (page) => page.locator('.ant-segmented-item').nth(5);
const allTab = (page) => page.locator('.ant-segmented-item').first();

try {
  const page = await openPage();
  await page.locator('.entry-card').first().waitFor();
  await page.waitForTimeout(150);
  assert.equal(await count(page, "history-refresh"), 1);
  results.push("StrictMode history subscription cleanup");

  const scroll = page.locator('.scroll-area').first();
  await scroll.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await page.waitForFunction(() => document.querySelectorAll('.entry-card').length >= 100);
  await scroll.evaluate((el) => { el.scrollTop = 4200; });
  await page.waitForTimeout(150);
  const top = await scroll.evaluate((el) => el.scrollTop);
  await chatTab(page).click();
  await page.locator('.chat-scroll').waitFor();
  await page.waitForTimeout(750);
  const ids = await page.locator('[data-chat-id]').evaluateAll((nodes) => nodes.map((node) => node.dataset.chatId));
  assert.deepEqual(ids.slice(-3), ["t-29", "f-file-a", "t-30"]);
  const ownFile = page.locator('[data-chat-id="f-file-a"]');
  assert.equal(await ownFile.locator('div').nth(0).evaluate((el) => el.style.alignItems), "flex-end");
  assert.match(await ownFile.innerText(), /\d{2}:\d{2}:\d{2}/);
  results.push("Mixed timeline, self file alignment and timestamps");

  const chatScroll = page.locator('.chat-scroll');
  assert.ok(await chatScroll.evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop < 3));
  await chatScroll.evaluate((el) => { el.scrollTop = 0; });
  await page.evaluate(() => {
    fixture.messages.push({ id: 31, text: "incoming", entryType: "Text", createdAt: Date.now(), isFromSelf: false });
    fixture.emit("clipboard-changed", null);
  });
  await page.locator('[data-chat-id="t-31"]').waitFor();
  await page.waitForTimeout(750);
  assert.equal(await chatScroll.evaluate((el) => el.scrollTop), 0);
  results.push("Initial bottom position and incoming messages preserve history position");

  await page.locator('.chat-composer input').fill("typed message");
  await page.locator('.chat-composer input').press("Enter");
  await page.locator('[data-chat-id="t-99"]').waitFor();
  assert.equal(await page.evaluate(() => fixture.calls.filter((call) => call.cmd === "paste_item").length), 0);
  results.push("Chat Enter sends without pasting history");

  await allTab(page).click();
  await page.waitForFunction(() => document.querySelectorAll('.entry-card').length >= 100);
  await page.waitForTimeout(150);
  const restoredTop = await scroll.evaluate((el) => el.scrollTop);
  assert.ok(Math.abs(restoredTop - top) < 3, `category scroll restoration: expected ${top}, received ${restoredTop}`);
  await page.evaluate(() => fixture.emit("history-refresh", null));
  await page.waitForTimeout(200);
  assert.ok(Math.abs(await scroll.evaluate((el) => el.scrollTop) - top) < 3);
  results.push("Loaded range and scroll anchor survive category return and reopen");

  for (let index = 0; index < 3; index++) {
    await chatTab(page).click();
    await allTab(page).click();
  }
  await chatTab(page).click();
  await page.waitForTimeout(300);
  assert.equal(await count(page, "tauri://drag-drop"), 1);
  await page.evaluate(() => fixture.emit("tauri://drag-drop", { paths: ["fixture.bin"], position: { x: 0, y: 0 } }));
  await page.waitForTimeout(150);
  assert.equal(await page.evaluate(() => fixture.calls.filter((call) => call.cmd === "sync_send_file").length), 1);
  await ownFile.waitFor();
  results.push("Rapid chat remount keeps one drag listener and one upload, files persist");

  await page.screenshot({ path: new URL("chat-desktop.png", output).pathname.replace(/^\/(\w:)/, "$1") });
  await page.setViewportSize({ width: 320, height: 420 });
  await page.waitForTimeout(150);
  assert.ok(await page.locator('.chat-composer input').evaluate((el) => el.getBoundingClientRect().width > 25));
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  await page.screenshot({ path: new URL("chat-narrow.png", output).pathname.replace(/^\/(\w:)/, "$1") });
  results.push("Desktop and narrow composer layout");

  await allTab(page).click();
  await page.evaluate(() => { fixture.delay["history-16"] = 500; });
  const search = page.locator('input').first();
  await search.fill("history-16");
  await page.waitForTimeout(190);
  await search.fill("history-15");
  await page.waitForTimeout(750);
  const rows = await page.locator('.entry-card').allTextContents();
  assert.ok(rows.length > 0 && rows.every((text) => text.includes("history-15")));
  await page.evaluate(() => {
    fixture.history.unshift({ ...fixture.history[0], id: 999, text: "unrelated" });
    fixture.emit("clipboard-changed", null);
  });
  await page.waitForTimeout(200);
  assert.equal(await page.locator('.entry-card').filter({ hasText: "unrelated" }).count(), 0);
  results.push("Delayed stale search is discarded and live updates keep filtering");

  await search.fill("");
  await page.locator('.entry-card').first().waitFor();
  await page.locator('body').press("ArrowDown");
  await page.evaluate(() => {
    const dialog = document.createElement("div"); dialog.className = "ant-image-preview-wrap"; document.body.append(dialog);
  });
  await page.locator('body').press("Enter");
  assert.equal(await page.evaluate(() => fixture.calls.filter((call) => call.cmd === "paste_item").length), 0);
  results.push("Image preview blocks background Enter paste");
  await page.close();

  const toast = await openPage("toast", 360);
  await toast.waitForTimeout(250);
  assert.equal(await count(toast, "smart-action"), 1);
  await toast.close();
  const settings = await openPage("settings", 860);
  await settings.getByText("同步", { exact: true }).click();
  await settings.waitForTimeout(250);
  assert.equal(await count(settings, "sync-state"), 1);
  for (let index = 0; index < 3; index++) {
    await settings.getByText("通用", { exact: true }).click();
    await settings.getByText("同步", { exact: true }).click();
  }
  await settings.waitForTimeout(250);
  assert.equal(await count(settings, "sync-state"), 1);
  await settings.close();
  results.push("Toast and settings late listener cleanup");
  console.log(JSON.stringify({ passed: results.length, checks: results }, null, 2));
} finally {
  await browser.close();
}
