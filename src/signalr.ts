import type { Server, IncomingMessage } from 'node:http';
import type { Duplex } from 'node:stream';
import { WebSocket, WebSocketServer } from 'ws';
import { randomHex, extractClientIpv4 } from './util.js';

/**
 * ASP.NET Core SignalR JSON 线协议服务端(兼容官方 JS/.NET/Java 客户端)。
 * 仅支持 WebSocket 传输:negotiate → 握手帧 {"protocol":"json","version":1} → JSON 消息帧。
 */

export interface HubConn {
  token: string;
  deviceId: string | null;
  ws: WebSocket;
  lastActivity: number;
  /** 用户网页会话:不接收剪贴板推送(全站广播对其是噪音),仅保留设备变更通知 */
  mutedClipboard: boolean;
}

export interface HubAuthorization {
  deviceId: string | null;
  mutedClipboard: boolean;
}

const PING_INTERVAL_MS = 15_000;

/** SignalR JSON 协议:每条消息以 ASCII 记录分隔符 0x1E 结尾 */
function trimRecordSeparator(s: string): string {
  return s.endsWith('\u001e') ? s.slice(0, -1) : s;
}

export class SignalRHub {
  private readonly conns = new Map<string, HubConn>(); // token -> conn
  /** negotiate 时标记的"剪贴板静默"连接令牌(用户网页会话),WS 建立时消费 */
  private readonly mutedTokens = new Set<string>();
  private readonly negotiated = new Map<string, HubAuthorization>();
  private wss: WebSocketServer | null = null;
  private pingTimer: NodeJS.Timeout | null = null;

  /** 连接登记回调(设备 upsert + connect 活动日志),由 server 注入 */
  onConnected: ((deviceId: string | null, deviceName: string | null, platform: string | null, version: string | null, ip: string | null) => void) | null = null;

  /** negotiate 处理:返回响应体;mutedClipboard=true 标记该连接不收剪贴板推送(用户会话) */
  negotiate(url: URL, opts: HubAuthorization = { deviceId: null, mutedClipboard: false }): { status: number; body: unknown } {
    const token = randomHex(16); // 64 hex
    this.negotiated.set(token, opts);
    return {
      status: 200,
      body: {
        connectionId: token,
        connectionToken: token,
        negotiateVersion: 1,
        availableTransports: [
          { transport: 'WebSockets', transferFormats: ['Text', 'Binary'] },
        ],
      },
    };
  }

  attach(server: Server, authorize: (url: URL, req: IncomingMessage) => HubAuthorization | null): void {
    this.wss = new WebSocketServer({ noServer: true });
    server.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
      const u = new URL(req.url ?? '/', 'http://localhost');
      if (u.pathname !== '/hubs/clipboard') {
        socket.write('HTTP/1.1 404 Not Found\r\n\r\n');
        socket.destroy();
        return;
      }
      const ip = extractClientIpv4(req);
      const auth = authorize(u, req);
      if (!auth) {
        socket.write('HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{"error":"unauthorized"}');
        socket.destroy();
        return;
      }
      this.wss!.handleUpgrade(req, socket, head, (ws) => this.accept(u, ws, ip, auth));
    });

    this.pingTimer = setInterval(() => this.pingAll(), PING_INTERVAL_MS);
    this.pingTimer.unref();
  }

  private accept(url: URL, ws: WebSocket, ip: string | null, auth: HubAuthorization): void {
    // 第一步:等待客户端握手帧
    const onFirst = (data: Buffer) => {
      ws.off('message', onFirst);
      let handshake: { protocol?: string; version?: number };
      try {
        handshake = JSON.parse(trimRecordSeparator(data.toString('utf8')));
      } catch {
        ws.close(1002, 'Invalid handshake');
        return;
      }
      if (handshake.protocol !== 'json' || handshake.version !== 1) {
        ws.close(1002, 'Invalid protocol');
        return;
      }
      ws.send('{}\u001e'); // 服务端握手响应(JSON 协议消息以 0x1E 结尾)
      const token = url.searchParams.get('id') ?? '';
      const deviceId = url.searchParams.get('deviceId');
      const deviceName = url.searchParams.get('deviceName');
      const platform = url.searchParams.get('platform');
      const version = url.searchParams.get('version');
      if (!token) {
        ws.close(1008, 'Missing connection id');
        return;
      }
      const negotiated = this.negotiated.get(token);
      this.negotiated.delete(token);
      this.mutedTokens.delete(token);
      const conn: HubConn = {
        token,
        deviceId: auth.deviceId ?? negotiated?.deviceId ?? deviceId,
        ws,
        lastActivity: Date.now(),
        mutedClipboard: auth.mutedClipboard || negotiated?.mutedClipboard === true,
      };
      // 同一 token 重复连接:先清理旧连接
      const old = this.conns.get(token);
      if (old && old.ws !== ws) {
        try { old.ws.close(1000, 'Replaced'); } catch { /* ignore */ }
      }
      this.conns.set(token, conn);
      ws.on('message', (d: Buffer) => this.onMessage(conn, d));
      ws.on('close', () => { this.conns.delete(token); });
      ws.on('error', () => { this.conns.delete(token); });
      this.onConnected?.(conn.deviceId, deviceName, platform, version, ip);
    };
    ws.once('message', onFirst);
    ws.on('error', () => { /* 忽略客户端错误 */ });
  }

  private onMessage(conn: HubConn, data: Buffer): void {
    conn.lastActivity = Date.now();
    let msg: { type?: number } = {};
    try {
      msg = JSON.parse(trimRecordSeparator(data.toString('utf8')));
    } catch {
      return;
    }
    if (msg.type === 6) return; // Ping:无需应答
    if (msg.type === 7) {
      try { conn.ws.close(1000, 'Client close'); } catch { /* ignore */ }
      return;
    }
    // type 1 Invocation(本 hub 无客户端可调方法)与其余类型:忽略
  }

  private sendJson(ws: WebSocket, obj: unknown): void {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(obj) + '\u001e');
    }
  }

  private pingAll(): void {
    for (const conn of this.conns.values()) {
      this.sendJson(conn.ws, { type: 6 });
    }
  }

  /** 全员广播 ClipboardUpdated(Windows / Android / Web 实时同步) */
  broadcastUpdated(entry: unknown): void {
    const msg = { type: 1, target: 'ClipboardUpdated', arguments: [entry] };
    for (const conn of this.conns.values()) {
      this.sendJson(conn.ws, msg);
    }
  }

  /** 定向推送:只通知指定 deviceId 的在线连接 */
  broadcastUpdatedTo(entry: unknown, deviceIds: ReadonlySet<string>): void {
    const msg = { type: 1, target: 'ClipboardUpdated', arguments: [entry] };
    for (const conn of this.conns.values()) {
      if (conn.deviceId && deviceIds.has(conn.deviceId)) this.sendJson(conn.ws, msg);
    }
  }

  /** 全员广播 ClipboardCleared */
  broadcastCleared(): void {
    const msg = { type: 1, target: 'ClipboardCleared', arguments: [] };
    for (const conn of this.conns.values()) {
      this.sendJson(conn.ws, msg);
    }
  }

  /** 全员广播设备列表变更(配对/重命名/移除),各端收到后自行刷新列表 */
  broadcastDevicesChanged(): void {
    const msg = { type: 1, target: 'DevicesChanged', arguments: [] };
    for (const conn of this.conns.values()) this.sendJson(conn.ws, msg);
  }

  /** 主动断开指定 deviceId 的所有活跃连接(用于设备注销/移除) */
  disconnectDevice(deviceId: string): void {
    for (const [token, conn] of this.conns.entries()) {
      if (conn.deviceId === deviceId) {
        try { conn.ws.close(4001, 'Device removed'); } catch { /* ignore */ }
        this.conns.delete(token);
      }
    }
  }

  /** 当前在线 deviceId 集合(心跳用) */
  onlineDeviceIds(): Set<string> {
    const ids = new Set<string>();
    for (const conn of this.conns.values()) if (conn.deviceId) ids.add(conn.deviceId);
    return ids;
  }

  dispose(): void {
    if (this.pingTimer) clearInterval(this.pingTimer);
    for (const conn of this.conns.values()) {
      try { conn.ws.close(1000, 'Server shutdown'); } catch { /* ignore */ }
    }
    this.conns.clear();
  }
}
