import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { convertFileSrc } from "@tauri-apps/api/core";
import {
  Layout, Menu, Switch, Input, InputNumber, Select, Button, Tooltip,
  Popconfirm, Checkbox, App as AntApp, Typography, Tag, Card, Modal, Progress,
} from "antd";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Icon } from "../icons";
import {
  settingsGet, settingsSet, historyStats, historyClear, openInExplorer,
  appInfo, formatBytes, devicesCached, lastSeenText,
  syncGetState, syncPairCreate, syncPairConfirm, syncTestConnection,
  syncRemoveDevice, syncRenameDevice, syncDevicesRefresh, syncPairRevoke,
  listUserProcesses, pickFolder,
  checkUpdate as checkUpdateApi, downloadUpdate, updateDownloadState,
  cancelUpdateDownload, runUpdateInstaller, updateCheckState,
  type SettingsDto, type SettingsPatch, type HistoryStats,
  type DeviceInfo, type UpdateResult, type SyncState, type PairingCode,
  type DownloadProgress,
} from "../ipc/settings";
import { QRCodeSVG } from "qrcode.react";
import { subscribeEvent } from "../ipc/events";

const { Sider, Content } = Layout;
const { Text } = Typography;

/// GitHub 官方商标路径(lucide 无品牌图标,内联 SVG)
function GithubIcon({ size = 14 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" fill="currentColor" aria-hidden="true" style={{ verticalAlign: "-2px" }}>
      <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8Z" />
    </svg>
  );
}

type SectionKey = "sync" | "general" | "clipboard" | "screenshot" | "about";

const SECTIONS: { key: SectionKey; label: string; icon: ReactNode }[] = [
  { key: "general", label: "通用", icon: <Icon name="sliders" size={15} /> },
  { key: "clipboard", label: "剪贴板", icon: <Icon name="clipboardList" size={15} /> },
  { key: "sync", label: "同步", icon: <Icon name="cloudUpload" size={15} /> },
  { key: "screenshot", label: "截图", icon: <Icon name="camera" size={15} /> },
  { key: "about", label: "关于", icon: <Icon name="info" size={15} /> },
];

export default function Settings() {
  const [s, setS] = useState<SettingsDto | null>(null);
  const [stats, setStats] = useState<HistoryStats | null>(null);
  const [info, setInfo] = useState<{ version: string; os: string } | null>(null);
  const [devices, setDevices] = useState<DeviceInfo[] | null>(null);
  const [section, setSection] = useState<SectionKey>("general");
  const [availableUpdate, setAvailableUpdate] = useState(false);
  const { message } = AntApp.useApp();

  const refresh = useCallback(async () => {
    setS(await settingsGet());
  }, []);
  const refreshCachedDevices = useCallback(async () => {
    setDevices(await devicesCached());
  }, []);

  useEffect(() => {
    void refresh();
    void historyStats().then(setStats);
    void appInfo().then(setInfo);
    void devicesCached().then(setDevices);
  }, [refresh]);

  useEffect(() => {
    let disposed = false;
    void updateCheckState().then((r) => { if (!disposed) setAvailableUpdate(!!r?.hasUpdate); });
    const stops = [
      subscribeEvent<string>("settings-navigate", (e) => {
        if (SECTIONS.some((section) => section.key === e.payload)) setSection(e.payload as SectionKey);
      }),
      subscribeEvent<UpdateResult>("update-available", (e) => setAvailableUpdate(e.payload.hasUpdate)),
    ];
    return () => { disposed = true; stops.forEach((stop) => stop()); };
  }, []);

  /// 更新一项设置并保存;本地状态乐观更新,失败回读
  const patch = useCallback(
    async (p: SettingsPatch) => {
      setS((prev) => (prev ? { ...prev, ...p } : prev));
      try {
        await settingsSet(p);
        if (p.maxHistory !== undefined || p.retentionDays !== undefined || p.storageDir !== undefined) {
          setStats(await historyStats());
        }
      } catch (e) {
        void message.error(String(e));
        await refresh();
      }
    },
    [message, refresh],
  );

  if (!s) {
    return (
      <div style={{ display: "grid", placeItems: "center", height: "100vh" }}>
        <span style={{ color: "var(--ant-color-text-tertiary, #999)", fontSize: 13 }}>加载设置中…</span>
      </div>
    );
  }

  return (
    <Layout style={{ height: "100vh", background: "transparent" }}>
      <Sider className="settings-sider" width={168} style={{ background: "var(--ant-color-bg-container)", borderRight: "1px solid var(--ant-color-border-secondary, rgba(128,128,128,0.15))" }}>
        <div style={{ padding: "16px 16px 8px", fontSize: 15, fontWeight: 600 }}>设置</div>
        <Menu
          mode="inline"
          inlineIndent={16}
          selectedKeys={[section]}
          onClick={({ key }) => setSection(key as SectionKey)}
          style={{ background: "transparent", borderInlineEnd: "none" }}
          items={SECTIONS.map((sec) => ({
            key: sec.key,
            icon: sec.icon,
            label: sec.key === "about" && availableUpdate ? "关于 · 有更新" : sec.label,
          }))}
        />
      </Sider>
      <Content className="scroll-area" style={{ overflow: "auto", padding: "20px 28px 40px" }}>
        <div style={{ maxWidth: 620, margin: "0 auto" }}>
          {section === "sync" && (
            <SyncSection
              s={s}
              patch={patch}
              devices={devices}
              onDevicesChanged={refreshCachedDevices}
              onRefreshSettings={refresh}
            />
          )}
          {section === "general" && <GeneralSection s={s} patch={patch} />}
          {section === "clipboard" && (
            <ClipboardSection
              s={s}
              patch={patch}
              stats={stats}
              onClear={async (keepStarred) => {
                await historyClear(keepStarred);
                setStats(await historyStats());
                void message.success("历史已清空");
              }}
              onRefreshStats={async () => setStats(await historyStats())}
            />
          )}
          {section === "screenshot" && <ScreenshotSection s={s} patch={patch} />}
          {section === "about" && <AboutSection s={s} info={info} />}
        </div>
      </Content>
    </Layout>
  );
}

// ---- 通用小控件 ----

function Row({
  title, desc, children, emphasized,
}: {
  title: ReactNode;
  desc?: ReactNode;
  children: ReactNode;
  /** 总开关行:标题加粗、字号稍大,与子开关拉开层次 */
  emphasized?: boolean;
}) {
  return (
    <div
      className="settings-row"
      style={{
        display: "flex",
        alignItems: "center",
        gap: 16,
        padding: emphasized ? "12px 0" : "10px 0",
      }}
    >
      <div style={{ flex: 1, minWidth: 0 }}>
        <div
          style={{
            fontSize: emphasized ? 14 : 13.5,
            fontWeight: emphasized ? 600 : 400,
            color: emphasized ? "var(--ant-color-text, #333)" : undefined,
          }}
        >
          {title}
        </div>
        {desc && (
          <div style={{ fontSize: 11.5, color: "var(--ant-color-text-tertiary, #999)", marginTop: 2 }}>
            {desc}
          </div>
        )}
      </div>
      <div style={{ flexShrink: 0 }}>{children}</div>
    </div>
  );
}

function Toggle({
  checked, onChange, disabled, large,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
  /** 总开关:默认尺寸(比 small 大一号) */
  large?: boolean;
}) {
  return (
    <Switch
      size={large ? undefined : "small"}
      checked={checked}
      onChange={onChange}
      disabled={disabled}
    />
  );
}

function SectionTitle({ children }: { children: ReactNode }) {
  return (
    <div style={{ fontSize: 13, fontWeight: 600, color: "var(--ant-color-text, #333)", margin: "18px 0 4px" }}>
      {children}
    </div>
  );
}

// ---- 同步 ----

const SYNC_STATUS_LABEL: Record<string, { text: string; color: string }> = {
  connecting: { text: "连接中", color: "gold" },
  connected: { text: "已连接", color: "green" },
  reconnecting: { text: "重连中", color: "gold" },
  disconnected: { text: "已断开", color: "default" },
  not_configured: { text: "未配对", color: "default" },
  offline: { text: "离线(凭证失效)", color: "red" },
};

function SyncSection({
  s, patch, devices, onDevicesChanged, onRefreshSettings,
}: {
  s: SettingsDto;
  patch: (p: SettingsPatch) => Promise<void>;
  devices: DeviceInfo[] | null;
  onDevicesChanged: () => Promise<void>;
  onRefreshSettings: () => Promise<void>;
}) {
  const { message } = AntApp.useApp();
  const [syncState, setSyncState] = useState<SyncState | null>(null);
  const [pairing, setPairing] = useState<PairingCode | null>(null);
  // 设备列表手动刷新(从服务端拉真值)
  const [refreshingDevices, setRefreshingDevices] = useState(false);
  const refreshDevices = async () => {
    setRefreshingDevices(true);
    try {
      await syncDevicesRefresh();
      await onDevicesChanged();
    } catch (e) {
      void message.error(`刷新设备列表失败:${String(e)}`);
    } finally {
      setRefreshingDevices(false);
    }
  };
  // 配对码剩余有效秒数(每秒刷新;过期自动收起配对卡)
  const [pairRemaining, setPairRemaining] = useState(0);
  useEffect(() => {
    if (!pairing?.expiresAt) {
      setPairRemaining(0);
      return;
    }
    const exp = new Date(pairing.expiresAt).getTime();
    if (Number.isNaN(exp)) {
      setPairRemaining(0);
      return;
    }
    const tick = () => {
      const remain = Math.max(0, Math.floor((exp - Date.now()) / 1000));
      setPairRemaining(remain);
      if (remain === 0) {
        setPairing(null);
        void message.info("配对码已过期,请重新生成");
      }
    };
    tick();
    const t = window.setInterval(tick, 1000);
    return () => window.clearInterval(t);
  }, [pairing?.expiresAt]);
  const [renaming, setRenaming] = useState<{ id: string; name: string } | null>(null);
  const [codeInput, setCodeInput] = useState("");
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<string | null>(null);

  // 实时连接状态(后端 sync-state 事件推送)
  useEffect(() => {
    let disposed = false;
    let statusVersion = 0;
    const stop = subscribeEvent<string>("sync-state", (e) => {
      statusVersion++;
      setSyncState((prev) => ({
        ...(prev ?? { isPaired: s.isPaired, serverUrl: s.serverUrl, deviceName: s.deviceName, deviceId: s.deviceId }),
        status: e.payload,
      }));
    });
    void (async () => {
      try {
        const version = statusVersion;
        const st = await syncGetState();
        if (disposed) return;
        setSyncState((prev) => statusVersion === version ? st : { ...st, status: prev?.status ?? st.status });
        // 打开设置页即从服务端拉设备真值(清掉本地缓存中的僵尸设备),再读缓存渲染
        await syncDevicesRefresh();
        if (!disposed) await onDevicesChanged();
      } catch {
        /* 浏览器环境 */
      }
    })();
    return () => { disposed = true; stop(); };
  }, [onDevicesChanged]);

  const doTest = async () => {
    setTesting(true);
    setTestResult(null);
    try {
      const [ok, msg] = await syncTestConnection();
      if (ok) void message.success(msg);
      else void message.error(msg);
      setTestResult(msg);
    } catch (e) {
      void message.error(String(e));
    } finally {
      setTesting(false);
    }
  };

  const startPair = async () => {
    try {
      const res = await syncPairCreate();
      if (res.code) {
        setPairing(res);
      } else {
        void message.error("服务器未返回配对码");
      }
    } catch (e) {
      void message.error(String(e));
    }
  };

  /// 关闭配对卡 = 作废配对码(与旧版语义一致)
  const cancelPair = () => {
    if (pairing) void syncPairRevoke(pairing.code).catch(() => {});
    setPairing(null);
  };

  const confirmPair = async () => {
    if (!/^\d{6}$/.test(codeInput.trim())) {
      void message.error("请输入 6 位数字配对码");
      return;
    }
    try {
      await syncPairConfirm(codeInput.trim());
      void message.success("配对成功!推送通道已连接");
      setPairing(null);
      setCodeInput("");
      await onRefreshSettings();
      setSyncState(await syncGetState());
      await onDevicesChanged();
    } catch (e) {
      void message.error(String(e));
    }
  };

  return (
    <>
      <SectionTitle>服务器</SectionTitle>
      <div className="settings-group">
      <Row title="服务器地址" desc="同步服务器的 HTTP 地址">
        <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
          <Input
            style={{ width: 220 }}
            value={s.serverUrl}
            onChange={(e) => void patch({ serverUrl: e.target.value })}
            placeholder="http://127.0.0.1:5033"
          />
          <Button size="small" loading={testing} onClick={() => void doTest()}>
            测试
          </Button>
        </span>
      </Row>
      <Row
        title="推送状态"
        desc={
          testResult
            ? `${syncState?.status ?? "…"} · 最近测试:${testResult}`
            : (syncState?.status ?? "…")
        }
      >
        {(() => {
          const st = SYNC_STATUS_LABEL[syncState?.status ?? "not_configured"] ?? SYNC_STATUS_LABEL.not_configured;
          return <Tag color={st.color}>{st.text}</Tag>;
        })()}
      </Row>

      </div>
      <SectionTitle>设备</SectionTitle>
      <div className="settings-group">
      <Row title="设备名称" desc="同步与互传时展示给其他设备的名称">
        <Input
          style={{ width: 240 }}
          value={s.deviceName}
          onChange={(e) => void patch({ deviceName: e.target.value })}
        />
      </Row>
      <Row title="设备 ID">
        <span style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
          <Text code style={{ fontSize: 12 }}>{s.deviceId.slice(0, 8)}</Text>
          <Tooltip title="复制完整设备 ID">
            <Button
              size="small"
              type="text"
              icon={<Icon name="copy" size={13} />}
              onClick={() => {
                void navigator.clipboard.writeText(s.deviceId);
                void message.success("已复制");
              }}
            />
          </Tooltip>
        </span>
      </Row>

      </div>
      <SectionTitle>配对</SectionTitle>
      <div className="settings-group">
      <Row
        title="生成配对码"
        desc="给其他设备用:在其上输入此码或扫 QR,即可加入同步(已配对设备也可邀请新设备)"
      >
        <Button size="small" icon={<Icon name="qrCode" size={13} />} onClick={() => void startPair()}>
          生成配对码
        </Button>
      </Row>
      <Row title="输入配对码" desc="在下方输入另一台设备生成的 6 位配对码,完成本设备配对">
        <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
          <Input
            size="small"
            placeholder="6 位数字"
            maxLength={6}
            value={codeInput}
            onChange={(e) => setCodeInput(e.target.value.replace(/[^0-9]/g, ""))}
            onPressEnter={() => void confirmPair()}
            style={{ width: 120, fontFamily: "monospace" }}
          />
          <Button size="small" type="primary" disabled={!/^\d{6}$/.test(codeInput)} onClick={() => void confirmPair()}>
            配对
          </Button>
        </span>
      </Row>
      {pairing && (
        <Card size="small" style={{ marginTop: 8, borderColor: "var(--ant-color-primary, #2563EB)" }}>
          <div style={{ display: "flex", gap: 20, alignItems: "center" }}>
            {/* 左:二维码(固定宽,垂直居中) */}
            <div style={{ textAlign: "center", flexShrink: 0 }}>
              <QRCodeSVG
                value={pairing.qrPayload ?? `${s.serverUrl.replace(/\/$/, "")}/index?pairCode=${pairing.code}`}
                size={104}
                level="M"
              />
              <div style={{ fontSize: 10.5, color: "var(--ant-color-text-tertiary, #999)", marginTop: 6 }}>
                扫码直连
              </div>
            </div>
            {/* 右:说明 + 大码 + 倒计时(垂直居中) */}
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 12, color: "var(--ant-color-text-tertiary, #999)" }}>
                在另一台设备上输入此配对码
                {pairRemaining > 0 && (
                  <span
                    style={{
                      marginLeft: 6,
                      color: pairRemaining <= 60 ? "var(--ant-color-warning, #faad14)" : "var(--ant-color-text-quaternary, #bbb)",
                      fontVariantNumeric: "tabular-nums",
                    }}
                  >
                    · {Math.floor(pairRemaining / 60)}:{String(pairRemaining % 60).padStart(2, "0")} 后过期
                  </span>
                )}
              </div>
              <div style={{ display: "flex", alignItems: "center", justifyContent: "center", gap: 10, margin: "10px 0 6px" }}>
                <div
                  style={{
                    fontSize: 34,
                    fontWeight: 700,
                    letterSpacing: 8,
                    fontFamily: "monospace",
                    color: "var(--ant-color-text, #333)",
                    userSelect: "all",
                    cursor: "pointer",
                  }}
                  title="点击复制配对码"
                  onClick={() => {
                    void navigator.clipboard.writeText(pairing.code);
                    void message.success("配对码已复制");
                  }}
                >
                  {pairing.code}
                </div>
                <Tooltip title="复制配对码">
                  <Button
                    size="small"
                    type="text"
                    icon={<Icon name="copy" size={14} />}
                    onClick={() => {
                      void navigator.clipboard.writeText(pairing.code);
                      void message.success("配对码已复制");
                    }}
                  />
                </Tooltip>
              </div>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <span style={{ fontSize: 11, color: "var(--ant-color-text-quaternary, #bbb)" }}>
                  或在对方设备上扫描左侧二维码
                </span>
                <Button size="small" type="text" onClick={cancelPair} style={{ color: "var(--ant-color-text-tertiary, #999)" }}>
                  作废并关闭
                </Button>
              </div>
            </div>
          </div>
        </Card>
      )}

      </div>
      <SectionTitle>
        设备列表
        <Tooltip title="从服务端重新拉取设备列表">
          <Button
            size="small"
            type="text"
            loading={refreshingDevices}
            icon={!refreshingDevices ? <Icon name="refresh" size={12} style={{ opacity: 0.6 }} /> : undefined}
            onClick={() => void refreshDevices()}
            style={{ marginLeft: 4, width: 22, height: 22, display: "inline-flex", alignItems: "center", justifyContent: "center" }}
          />
        </Tooltip>
      </SectionTitle>
      <div className="settings-group">
      {devices == null ? (
        <Text type="secondary" style={{ fontSize: 12 }}>加载中…</Text>
      ) : devices.length === 0 ? (
        <Text type="secondary" style={{ fontSize: 12 }}>
          暂无设备记录;完成配对并同步一次后,这里会显示所有已接入设备。
        </Text>
      ) : (
        devices.map((d) => (
          <Row
            key={d.id ?? d.name}
            title={
              <span style={{ display: "inline-flex", alignItems: "center", gap: 8 }}>
                <span
                  style={{
                    width: 8,
                    height: 8,
                    borderRadius: "50%",
                    background: d.online ? "#22c55e" : "rgba(128,128,128,0.4)",
                    flexShrink: 0,
                  }}
                />
                {d.name ?? (d.id ?? "").slice(0, 8)}
                {d.isCurrent && <Tag style={{ marginInlineEnd: 0 }} color="blue">本机</Tag>}
              </span>
            }
            desc={[
              d.platform, d.version,
              d.ip ? d.ip.replace(/^::ffff:/, "") : null,
            ].filter(Boolean).join(" · ")}
          >
            <span style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
              <Text
                type={d.online ? undefined : "secondary"}
                style={{ fontSize: 12, color: d.online ? "#22c55e" : undefined }}
              >
                {lastSeenText(d.online, d.lastSeenAt)}
              </Text>
              <Tooltip title="重命名设备">
                <Button
                  size="small"
                  type="text"
                  icon={<Icon name="pencil" size={13} />}
                  onClick={() => setRenaming({ id: d.id ?? "", name: d.name ?? "" })}
                />
              </Tooltip>
              {!d.isCurrent && (
                <Popconfirm
                  title={`移除设备「${d.name ?? d.id}」?`}
                  description="该设备的配对凭证将立即失效,需要重新配对才能恢复同步。"
                  okText="移除"
                  okButtonProps={{ danger: true }}
                  cancelText="取消"
                  onConfirm={async () => {
                    try {
                      await syncRemoveDevice(d.id ?? "");
                      void message.success("设备已移除");
                      await onDevicesChanged();
                    } catch (e) {
                      void message.error(String(e));
                    }
                  }}
                >
                  <Tooltip title="移除设备">
                    <Button size="small" type="text" danger icon={<Icon name="trash" size={13} />} />
                  </Tooltip>
                </Popconfirm>
              )}
            </span>
          </Row>
        ))
      )}

      {/* 重命名设备弹窗 */}
      </div>
      <Modal
        title="重命名设备"
        open={renaming != null}
        okText="保存"
        cancelText="取消"
        onOk={async () => {
          if (!renaming) return;
          try {
            await syncRenameDevice(renaming.id, renaming.name);
            void message.success("已重命名");
            setRenaming(null);
            await onDevicesChanged();
          } catch (e) {
            void message.error(String(e));
          }
        }}
        onCancel={() => setRenaming(null)}
        destroyOnClose
      >
        <Input
          value={renaming?.name ?? ""}
          onChange={(e) =>
            setRenaming((prev) => (prev ? { ...prev, name: e.target.value } : prev))
          }
          maxLength={40}
          placeholder="设备名称"
        />
      </Modal>
    </>
  );
}

// ---- 通用 ----

function GeneralSection({
  s, patch,
}: {
  s: SettingsDto;
  patch: (p: SettingsPatch) => Promise<void>;
}) {
  return (
    <>
      <SectionTitle>外观</SectionTitle>
      <div className="settings-group">
      <Row title="主题">
        <Select
          size="small"
          style={{ width: 120 }}
          value={s.themeMode}
          onChange={(v) => void patch({ themeMode: v })}
          options={[
            { value: "system", label: "跟随系统" },
            { value: "light", label: "浅色" },
            { value: "dark", label: "深色" },
          ]}
        />
      </Row>
      <Row title="图标库">
        <Select
          size="small"
          style={{ width: 120 }}
          value={s.iconLibrary}
          onChange={(v) => void patch({ iconLibrary: v })}
          options={[
            { value: "lucide", label: "Lucide" },
            { value: "tabler", label: "Tabler" },
            { value: "phosphor", label: "Phosphor" },
            { value: "remix", label: "Remix" },
          ]}
        />
      </Row>

      </div>
      <SectionTitle>启动与窗口</SectionTitle>
      <div className="settings-group">
      <Row title="开机自启" desc="登录 Windows 后自动启动 NexClip">
        <Toggle checked={s.bootStartEnabled} onChange={(v) => void patch({ bootStartEnabled: v })} />
      </Row>
      <Row title="启动时最小化" desc="启动后仅在托盘运行,不弹出窗口">
        <Toggle checked={s.startMinimized} onChange={(v) => void patch({ startMinimized: v })} />
      </Row>
      <Row title="关闭到托盘" desc="点击关闭按钮时隐藏到托盘而不是退出">
        <Toggle checked={s.closeToTray} onChange={(v) => void patch({ closeToTray: v })} />
      </Row>
      <Row title="窗口出现位置" desc="呼出剪贴板窗口时的默认位置">
        <Select
          size="small"
          style={{ width: 120 }}
          value={s.windowPositionMode}
          onChange={(v) => void patch({ windowPositionMode: v })}
          options={[
            { value: "cursor", label: "跟随鼠标" },
            { value: "center", label: "屏幕中心" },
          ]}
        />
      </Row>
      <Row title="记住滚动位置" desc="重新打开剪贴板时保持上次的位置">
        <Toggle
          checked={s.rememberScrollPosition}
          onChange={(v) => void patch({ rememberScrollPosition: v })}
        />
      </Row>

      </div>
      <SectionTitle>更新</SectionTitle>
      <div className="settings-group">
      <Row title="自动检查更新" desc="启动时检查新版本">
        <Toggle checked={s.autoCheckUpdate} onChange={(v) => void patch({ autoCheckUpdate: v })} />
      </Row>
      <Row title="更新下载来源">
        <Select
          size="small"
          style={{ width: 120 }}
          value={s.updateSource}
          onChange={(v) => void patch({ updateSource: v })}
          options={[
            { value: "github", label: "GitHub" },
            { value: "direct", label: "服务器直连" },
          ]}
        />
      </Row>
      </div>
    </>
  );
}

// ---- 剪贴板 ----

function ClipboardSection({
  s, patch, stats, onClear, onRefreshStats,
}: {
  s: SettingsDto;
  patch: (p: SettingsPatch) => Promise<void>;
  stats: HistoryStats | null;
  onClear: (keepStarred: boolean) => Promise<void>;
  onRefreshStats: () => Promise<void>;
}) {
  const [keepStarred, setKeepStarred] = useState(true);
  const [dirDraft, setDirDraft] = useState<string | null>(null);
  // 进程候选:下拉展开时才加载(枚举有可见窗口的用户进程,排除系统进程)
  const [procOptions, setProcOptions] = useState<{ value: string; label: string; icon?: string }[]>([]);
  const [procLoading, setProcLoading] = useState(false);
  const loadProcesses = async () => {
    setProcLoading(true);
    try {
      const list = await listUserProcesses();
      setProcOptions(list.map((p) => ({ value: p.name, label: p.display, icon: p.icon ?? undefined })));
    } catch {
      setProcOptions([]);
    } finally {
      setProcLoading(false);
    }
  };
  // 数据目录:系统文件夹选择对话框
  const browseDataDir = async () => {
    const dir = await pickFolder();
    if (dir) {
      setDirDraft(dir);
      await patch({ storageDir: dir });
      await onRefreshStats();
    }
  };
  return (
    <>
      <SectionTitle>监听</SectionTitle>
      <div className="settings-group">
      <Row title="启用剪贴板监听" desc="关闭后不再捕获新的复制内容">
        <Toggle checked={s.monitorEnabled} onChange={(v) => void patch({ monitorEnabled: v })} />
      </Row>
      <Row title="过滤远程控制应用" desc="忽略 AnyDesk / ToDesk / mstsc 等写入的内容">
        <Toggle checked={s.appFilterEnabled} onChange={(v) => void patch({ appFilterEnabled: v })} />
      </Row>
      <Row
        title="自定义过滤进程"
        desc="附加要忽略的进程名或可执行文件名;下拉列出当前活跃应用,也可直接输入回车添加"
      >
        <Select
          mode="tags"
          size="small"
          style={{ width: 240 }}
          value={s.customFilteredProcesses}
          placeholder="如 vmware-hostd"
          onChange={(v) => void patch({ customFilteredProcesses: v })}
          onDropdownVisibleChange={(open) => {
            if (open) void loadProcesses();
          }}
          loading={procLoading}
          options={procOptions}
          optionFilterProp="label"
          optionRender={(opt) => (
            <div style={{ display: "flex", alignItems: "center", gap: 8, padding: "2px 0" }}>
              {opt.data.icon ? (
                <img
                  src={convertFileSrc(opt.data.icon)}
                  style={{ width: 16, height: 16, borderRadius: 3, flexShrink: 0 }}
                  onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }}
                />
              ) : (
                <span style={{ width: 16, flexShrink: 0 }} />
              )}
              <span style={{ flex: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                {opt.label}
              </span>
              <span style={{ fontSize: 10.5, color: "var(--ant-color-text-quaternary, #bbb)", flexShrink: 0 }}>
                {opt.value}
              </span>
            </div>
          )}
        />
      </Row>

      </div>
      <SectionTitle>行为</SectionTitle>
      <div className="settings-group">
      <Row title="自动写回剪贴板" desc="其他设备同步的内容到达时,自动写入本机剪贴板(5 分钟内的推送)">
        <Toggle checked={s.autoPaste} onChange={(v) => void patch({ autoPaste: v })} />
      </Row>
      <Row title="通知" desc="同步事件与错误弹出系统通知">
        <Toggle checked={s.notifyEnabled} onChange={(v) => void patch({ notifyEnabled: v })} />
      </Row>
      <Row title="富文本支持" desc="捕获并写回带格式(HTML)内容;关闭后等同纯文本">
        <Toggle checked={s.richTextEnabled} onChange={(v) => void patch({ richTextEnabled: v })} />
      </Row>
      <Row title="粘贴键" desc="普通应用使用的粘贴快捷键;Chromium 类应用固定 Ctrl+V">
        <Select
          size="small"
          style={{ width: 120 }}
          value={s.pasteKey}
          onChange={(v) => void patch({ pasteKey: v })}
          options={[
            { value: "CtrlV", label: "Ctrl + V" },
            { value: "ShiftInsert", label: "Shift + Insert" },
          ]}
        />
      </Row>

      </div>
      <SectionTitle>容量</SectionTitle>
      <div className="settings-group">
      <Row title="历史条数上限" desc="超出的最旧未收藏条目会被自动淘汰">
        <InputNumber
          size="small"
          min={10}
          max={10000}
          step={50}
          value={s.maxHistory}
          onChange={(v) => v && void patch({ maxHistory: v })}
        />
      </Row>
      <Row title="历史保留天数" desc="超过天数的条目自动删除;0 = 不限制">
        <InputNumber
          size="small"
          min={0}
          max={3650}
          value={s.retentionDays}
          onChange={(v) => v !== null && void patch({ retentionDays: v })}
        />
      </Row>

      </div>
      <SectionTitle>数据</SectionTitle>
      <div className="settings-group">
      <Row title="数据目录" desc="history.db 与图片缓存所在目录">
        <Tooltip title={s.resolvedStorageDir}>
          <Button
            size="small"
            icon={<Icon name="folderOpen" size={13} />}
            onClick={() => void openInExplorer(s.resolvedStorageDir)}
          >
            打开
          </Button>
        </Tooltip>
      </Row>
      <Row title="修改数据目录" desc="留空恢复默认(%LOCALAPPDATA%/NexClip);切换后立即生效">
        <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
          <Button size="small" icon={<Icon name="folderOpen" size={13} />} onClick={() => void browseDataDir()}>
            浏览
          </Button>
          <Input
            size="small"
            style={{ width: 220, fontFamily: "monospace", fontSize: 11 }}
            placeholder="默认目录"
            value={dirDraft ?? s.storageDir}
            onChange={(e) => setDirDraft(e.target.value)}
            onBlur={() => {
              const v = dirDraft;
              setDirDraft(null);
              if (v != null && v.trim() !== s.storageDir) {
                void patch({ storageDir: v }).then(onRefreshStats);
              }
            }}
            onPressEnter={(e) => (e.target as HTMLInputElement).blur()}
          />
        </span>
      </Row>
      <Row title="当前占用">
        <span style={{ fontSize: 13 }}>
          {stats
            ? `${stats.count} 条(收藏 ${stats.starredCount}) · ${formatBytes(stats.dbSizeBytes)}`
            : "…"}
        </span>
      </Row>
      <Row title="清空历史" desc="删除所有剪贴板历史与图片缓存,不可恢复">
        <Popconfirm
          title="确认清空历史?"
          description={
            <Checkbox checked={keepStarred} onChange={(e) => setKeepStarred(e.target.checked)}>
              保留收藏条目
            </Checkbox>
          }
          okText="清空"
          okButtonProps={{ danger: true }}
          cancelText="取消"
          onConfirm={() => onClear(keepStarred)}
        >
          <Button size="small" danger>
            清空
          </Button>
        </Popconfirm>
      </Row>

      </div>
      <SectionTitle>智能识别</SectionTitle>
      <div className="settings-group">
      {/* 总开关:控制智能直达卡片整体(关闭时下方分类开关禁用) */}
      <Row
        emphasized
        title="智能直达卡片"
        desc="总开关:复制命中的内容时右下角显示快捷操作卡片"
      >
        <Toggle large checked={s.copyDirectEnabled} onChange={(v) => void patch({ copyDirectEnabled: v })} />
      </Row>
      <div
        style={{
          height: 1,
          background: "var(--ant-color-border-secondary, rgba(128,128,128,0.15))",
          margin: "4px 0 2px",
        }}
      />
      <Row title="颜色代码" desc="色值微预览与格式转换">
        <Toggle checked={s.smartColorEnabled} disabled={!s.copyDirectEnabled} onChange={(v) => void patch({ smartColorEnabled: v })} />
      </Row>
      <Row title="本地路径" desc="文件与目录路径直达资源管理器">
        <Toggle checked={s.smartPathEnabled} disabled={!s.copyDirectEnabled} onChange={(v) => void patch({ smartPathEnabled: v })} />
      </Row>
      <Row title="GitHub 深链" desc="仓库 / Issue / PR 深度识别">
        <Toggle
          checked={s.smartDeepLinkEnabled}
          disabled={!s.copyDirectEnabled}
          onChange={(v) => void patch({ smartDeepLinkEnabled: v })}
        />
      </Row>
      <Row title="网盘链接" desc="网盘链接与提取码识别">
        <Toggle checked={s.smartNetdiskEnabled} disabled={!s.copyDirectEnabled} onChange={(v) => void patch({ smartNetdiskEnabled: v })} />
      </Row>
      <Row title="网页链接" desc="通用 URL 直达浏览器">
        <Toggle checked={s.smartUrlEnabled} disabled={!s.copyDirectEnabled} onChange={(v) => void patch({ smartUrlEnabled: v })} />
      </Row>
      </div>
      <SectionTitle>快捷键</SectionTitle>
      <div className="settings-group">
        <Row title="呼出剪贴板" desc="全局快捷键,默认 Alt+V">
          <HotkeyInput value={s.hotkey} onCommit={(v) => void patch({ hotkey: v })} />
        </Row>
        <Row title="置顶开关" desc="全局快捷键,默认 Ctrl+Alt+T">
          <HotkeyInput
            value={s.hotkeyTopmost}
            onCommit={(v) => void patch({ hotkeyTopmost: v })}
          />
        </Row>
        <Row title="打开复制的链接" desc="直达卡片显示时执行卡片主操作;否则打开剪贴板最近的链接">
          <HotkeyInput
            value={s.hotkeyOpenUrl}
            onCommit={(v) => void patch({ hotkeyOpenUrl: v })}
          />
        </Row>
        <Row title="打开设置" desc="全局快捷键,默认 Alt+X">
          <HotkeyInput
            value={s.hotkeySettings}
            onCommit={(v) => void patch({ hotkeySettings: v })}
          />
        </Row>
      </div>
    </>
  );
}

// ---- 截图(独立分区) ----

function ScreenshotSection({
  s, patch,
}: {
  s: SettingsDto;
  patch: (p: SettingsPatch) => Promise<void>;
}) {
  return (
    <>
      <SectionTitle>截图</SectionTitle>
      <div className="settings-group">
        <Row title="启用截图" desc="关闭后截图快捷键、托盘菜单与设置入口均不生效" emphasized>
          <Toggle checked={s.screenshotEnabled} onChange={(v) => void patch({ screenshotEnabled: v })} />
        </Row>
      </div>
      <SectionTitle>全局快捷键</SectionTitle>
      <div className="settings-group">
        <Row title="截图(选区)" desc="默认 F1;拖拽选区后可标注/复制/保存/贴图/长图">
          <HotkeyInput
            value={s.hotkeyScreenshot}
            onCommit={(v) => void patch({ hotkeyScreenshot: v })}
          />
        </Row>
        <Row title="截取光标所在屏幕" desc="默认 Ctrl+Shift+F;整屏直接复制,无选区">
          <HotkeyInput
            value={s.hotkeyScreenshotScreen}
            onCommit={(v) => void patch({ hotkeyScreenshotScreen: v })}
          />
        </Row>
        <Row title="截取活动窗口" desc="默认 Ctrl+Shift+W;前台窗口直接复制,无选区">
          <HotkeyInput
            value={s.hotkeyScreenshotWindow}
            onCommit={(v) => void patch({ hotkeyScreenshotWindow: v })}
          />
        </Row>
        <Row title="剪贴板贴图" desc="默认 F3;把剪贴板里的图片贴到屏幕(不截图,直接贴)">
          <HotkeyInput
            value={s.hotkeyPin}
            onCommit={(v) => void patch({ hotkeyPin: v })}
          />
        </Row>
      </div>
      <SectionTitle>操作快捷键(选区确认后)</SectionTitle>
      <div className="settings-group">
        <Row title="复制选区" desc="Enter 或 Ctrl+C,结果自动入剪贴板历史">
          <span style={hintStyle}>Enter / Ctrl+C</span>
        </Row>
        <Row title="另存为" desc="Ctrl+S 打开保存对话框(记忆上次目录)">
          <span style={hintStyle}>Ctrl+S</span>
        </Row>
        <Row title="取消截图" desc="Esc 或右键;会话中再按截图快捷键也可强制取消">
          <span style={hintStyle}>Esc</span>
        </Row>
        <Row title="移动/调整选区" desc="拖动选区边缘移动;拖四角/四边手柄调整大小">
          <span style={hintStyle}>边缘拖动 / 手柄</span>
        </Row>
      </div>
    </>
  );
}

const hintStyle: React.CSSProperties = {
  fontSize: 12,
  color: "var(--ant-color-text-tertiary, #999)",
  fontFamily: "ui-monospace, monospace",
};

/// 快捷键捕获输入框:聚焦后按组合键即捕获,Esc 放弃
function HotkeyInput({
  value, onCommit,
}: {
  value: string;
  onCommit: (v: string) => void;
}) {
  const [capturing, setCapturing] = useState(false);
  const [draft, setDraft] = useState(value);

  const format = (e: React.KeyboardEvent<HTMLInputElement>): string | null => {
    const { key, ctrlKey, altKey, shiftKey, metaKey } = e;
    if (["Control", "Shift", "Alt", "Meta"].includes(key)) return null;
    const parts: string[] = [];
    if (ctrlKey) parts.push("Ctrl");
    if (altKey) parts.push("Alt");
    if (shiftKey) parts.push("Shift");
    if (metaKey) parts.push("Win");
    const k = key.length === 1 ? key.toUpperCase() : key;
    parts.push(k);
    return parts.join("+");
  };

  return (
    <Input
      size="small"
      readOnly
      style={{ width: 150, cursor: "pointer", textAlign: "center", fontFamily: "monospace" }}
      value={capturing ? "按下组合键…" : draft}
      onFocus={() => {
        setCapturing(true);
        setDraft(value);
      }}
      onBlur={() => {
        setCapturing(false);
        setDraft(value);
      }}
      onKeyDown={(e) => {
        e.preventDefault();
        if (e.key === "Escape") {
          (e.target as HTMLInputElement).blur();
          return;
        }
        const combo = format(e);
        if (combo) {
          setDraft(combo);
          onCommit(combo);
          (e.target as HTMLInputElement).blur();
        }
      }}
    />
  );
}

// ---- 关于 ----

function AboutSection({
  s, info,
}: {
  s: SettingsDto;
  info: { version: string; os: string } | null;
}) {
  const { message } = AntApp.useApp();
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<UpdateResult | null>(null);
  // 内置下载状态:null 空闲 / 进度对象(进行中或已完成)
  const [dl, setDl] = useState<DownloadProgress | null>(null);
  const [dlPath, setDlPath] = useState<string | null>(null);

  // 挂载时恢复下载状态(下载中切换设置页回来不丢进度)
  useEffect(() => {
    let disposed = false;
    void updateCheckState().then((r) => { if (!disposed) setResult(r); });
    void updateDownloadState().then((st) => {
      if (!disposed && st && !st.done) setDl(st);
    });
    const stop = subscribeEvent<UpdateResult>("update-available", (e) => setResult(e.payload));
    return () => { disposed = true; stop(); };
  }, []);

  // 进度事件订阅
  useEffect(() => {
    return subscribeEvent<DownloadProgress>("update-download-progress", (e) => {
          setDl(e.payload);
          if (e.payload.done && e.payload.error == null) {
            void message.success("安装包下载完成,可立即安装");
          } else if (e.payload.done && e.payload.error != null) {
            void message.error(`下载失败:${e.payload.error}`);
          }
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const doCheck = async () => {
    setChecking(true);
    setResult(null);
    try {
      const r = await checkUpdateApi();
      setResult(r);
      if (!r.success) {
        void message.error(`检查更新失败:${r.errorMessage ?? "未知错误"}`);
      } else if (!r.hasUpdate) {
        void message.success(`已是最新版本(v${r.latestVersion || r.currentVersion})`);
      }
    } catch (e) {
      void message.error(String(e));
    } finally {
      setChecking(false);
    }
  };

  const startDownload = async () => {
    if (!result?.downloadUrl) return;
    setDlPath(null);
    setDl({ downloaded: 0, total: result.fileSize ?? null, percent: 0, done: false, error: null });
    try {
      const path = await downloadUpdate(result.downloadUrl, result.sha256);
      setDlPath(path);
    } catch (e) {
      if (String(e).includes("__cancelled__")) {
        void message.info("下载已取消");
      } else {
        void message.error(String(e));
      }
      setDl(null);
    }
  };

  const doInstall = async () => {
    if (!dlPath) return;
    try {
      await runUpdateInstaller(dlPath);
      void message.success("安装程序已启动;建议退出 NexClip 后完成安装");
    } catch (e) {
      void message.error(String(e));
    }
  };

  return (
    <>
      <SectionTitle>关于</SectionTitle>
      <div className="settings-group">
      <div style={{ textAlign: "center", padding: "28px 0 8px" }}>
        <img
          src="app-icon.png"
          alt="NexClip"
          width={72}
          height={72}
          style={{ borderRadius: 16, marginBottom: 10 }}
        />
        <div style={{ fontSize: 22, fontWeight: 700, letterSpacing: 1 }}>NexClip</div>
        <div style={{ fontSize: 12, color: "var(--ant-color-text-tertiary, #999)", marginTop: 4 }}>
          跨设备剪贴板同步 · Tauri 版
        </div>
        <Tag style={{ marginTop: 10 }} color="blue">
          v{info?.version ?? "…"}
        </Tag>
      </div>
      <Row title="运行环境">
        <span style={{ fontSize: 13 }}>{info?.os ?? "…"}</span>
      </Row>
      <Row
        title="检查更新"
        desc={`来源:${s.updateSource === "direct" ? "服务器直连" : "GitHub Releases"}(在「通用」中切换)`}
      >
        <Button size="small" loading={checking} onClick={() => void doCheck()}>
          检查
        </Button>
      </Row>
      {result?.success && result.hasUpdate && (
        <Card size="small" style={{ marginTop: 12, borderColor: "var(--ant-color-primary, #2563EB)" }}>
          <div style={{ fontSize: 13.5, fontWeight: 600 }}>
            {result.releaseTitle || `NexClip v${result.latestVersion}`}
          </div>
          <div style={{ fontSize: 12, color: "var(--ant-color-text-tertiary, #999)", margin: "4px 0 8px" }}>
            {result.currentVersion} → {result.latestVersion}
            {result.fileSize ? ` · ${formatBytes(result.fileSize)}` : ""}
          </div>
          {result.releaseNotes && (
            <div
              className="release-notes"
              style={{
                fontSize: 12.5,
                lineHeight: 1.65,
                maxHeight: 180,
                overflow: "auto",
                padding: "10px 14px",
                borderRadius: 6,
                background: "rgba(128,128,128,0.08)",
              }}
            >
              <ReactMarkdown remarkPlugins={[remarkGfm]}>{result.releaseNotes}</ReactMarkdown>
            </div>
          )}
          {/* 操作区三态:空闲下载 / 进度中 / 已完成待安装 */}
          {!dl && (
            <div style={{ display: "flex", gap: 8, marginTop: 10 }}>
              <Button size="small" type="primary" onClick={() => void startDownload()}>
                下载更新
              </Button>
              <Button size="small" onClick={() => void openInExplorer(result.releaseUrl)}>
                发布页
              </Button>
            </div>
          )}
          {dl && !dl.done && (
            <div style={{ marginTop: 10 }}>
              <Progress
                percent={dl.percent < 0 ? 0 : dl.percent}
                size="small"
                status="active"
                format={(p) => `${p ?? 0}%`}
              />
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginTop: 4 }}>
                <span style={{ fontSize: 11, color: "var(--ant-color-text-tertiary, #999)" }}>
                  {dl.total
                    ? `${formatBytes(dl.downloaded)} / ${formatBytes(dl.total)}`
                    : formatBytes(dl.downloaded)}
                </span>
                <Button
                  size="small"
                  type="text"
                  danger
                  onClick={() => void cancelUpdateDownload()}
                >
                  取消
                </Button>
              </div>
            </div>
          )}
          {dl?.done && dl.error == null && (
            <div style={{ display: "flex", gap: 8, marginTop: 10, alignItems: "center" }}>
              <Button size="small" type="primary" onClick={() => void doInstall()}>
                立即安装
              </Button>
              <Button size="small" onClick={() => void openInExplorer(dlPath ?? "")}>
                打开所在文件夹
              </Button>
              <span style={{ fontSize: 11, color: "var(--ant-color-text-tertiary, #999)" }}>
                安装前建议退出 NexClip
              </span>
            </div>
          )}
          {dl?.done && dl.error != null && (
            <div style={{ display: "flex", gap: 8, marginTop: 10 }}>
              <Button size="small" onClick={() => void startDownload()}>
                重试下载
              </Button>
              <Button size="small" onClick={() => void openInExplorer(result.releaseUrl)}>
                发布页
              </Button>
            </div>
          )}
        </Card>
      )}
      <Row title="项目主页">
        <Button
          size="small"
          icon={<GithubIcon size={13} />}
          onClick={() => {
            void openInExplorer("https://github.com/yixing233/nexclip/releases");
            void message.success("已在浏览器打开");
          }}
        >
          GitHub
        </Button>
      </Row>
      </div>
    </>
  );
}
