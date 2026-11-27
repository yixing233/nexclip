import React, { useEffect, useState } from "react";
import ReactDOM from "react-dom/client";
import { App as AntApp, ConfigProvider, theme } from "antd";
import zhCN from "antd/locale/zh_CN";
import App from "./App";
import { IconLibraryContext } from "./icons";
import "./index.css";
import { subscribeEvent } from "./ipc/events";

// 品牌色与旧版 WinUI 一致(#2563EB)
const brandColor = "#2563EB";

type ThemeMode = "light" | "dark" | "system";
type IconLibrary = "lucide" | "tabler" | "phosphor" | "remix";

async function detectLabel(): Promise<string> {
  try {
    const { getCurrentWindow } = await import("@tauri-apps/api/window");
    return getCurrentWindow().label;
  } catch {
    return "main"; // 浏览器直接打开(无 Tauri 运行时)时按主窗口渲染
  }
}

async function loadThemeMode(): Promise<ThemeMode> {
  try {
    const { invoke } = await import("@tauri-apps/api/core");
    const s = await invoke<{ themeMode: ThemeMode }>("settings_get");
    return s.themeMode;
  } catch {
    return "system";
  }
}

const ICON_LIBRARIES = ["lucide", "tabler", "phosphor", "remix"] as const;

async function loadIconLibrary(): Promise<IconLibrary> {
  try {
    const { invoke } = await import("@tauri-apps/api/core");
    const s = await invoke<{ iconLibrary?: IconLibrary }>("settings_get");
    // 旧版后端无此字段(undefined)、或持久化了被移除的库 → 回退 lucide
    return s.iconLibrary && ICON_LIBRARIES.includes(s.iconLibrary)
      ? s.iconLibrary
      : "lucide";
  } catch {
    return "lucide";
  }
}

function resolveDark(mode: ThemeMode): boolean {
  if (mode === "dark") return true;
  if (mode === "light") return false;
  return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function Root({ windowLabel }: { windowLabel: string }) {
  const [mode, setMode] = useState<ThemeMode>("system");
  const [iconLib, setIconLib] = useState<IconLibrary>("lucide");
  const [systemDark, setSystemDark] = useState(() => resolveDark("system"));

  // 初始读取设置;设置页保存后广播 settings-changed,各窗口即时换肤/换图标库
  useEffect(() => {
    void loadThemeMode().then(setMode);
    void loadIconLibrary().then(setIconLib);
    return subscribeEvent("settings-changed", () => {
          void loadThemeMode().then(setMode);
          void loadIconLibrary().then(setIconLib);
    });
  }, []);

  // 跟随系统
  useEffect(() => {
    if (mode !== "system") return;
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => setSystemDark(media.matches);
    onChange();
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, [mode]);

  const dark = mode === "system" ? systemDark : resolveDark(mode);

  // colorScheme 跟随主题;html/body 底色直接按算法赋值
  // (antd cssVar 变量不作用于 html 元素,故不能用 var(--ant-color-bg-layout))
  // 值与 antd 算法一致:深色 layout #000 / 浅色 #f5f5f5,卡片 container 色 #1f1f1f/#fff 自然分层
  // 注意:截图覆盖窗/贴图窗是透明窗,必须保持 body 透明(否则贴图的圆角透明像素露出黑底)
  useEffect(() => {
    const transparent = windowLabel.startsWith("pin-") || windowLabel.startsWith("overlay-");
    if (transparent) {
      document.body.style.backgroundColor = "transparent";
      document.documentElement.style.backgroundColor = "transparent";
      return;
    }
    const bg = dark ? "#000000" : "#f5f5f5";
    document.body.style.backgroundColor = bg;
    document.documentElement.style.backgroundColor = bg;
    // 属性而非内联 style:CSS 里 html[color-scheme="dark"] 选择器依赖它
    document.documentElement.setAttribute("color-scheme", dark ? "dark" : "light");
    document.documentElement.style.colorScheme = dark ? "dark" : "light";
  }, [dark, windowLabel]);

  return (
    <ConfigProvider
      locale={zhCN}
      theme={{
        // 开启 cssVar:主题 token 以 CSS 变量(--ant-*)输出,
        // 自定义 CSS(.settings-group 等)才能随深色模式联动
        cssVar: true,
        algorithm: dark ? theme.darkAlgorithm : theme.defaultAlgorithm,
        token: {
          colorPrimary: brandColor,
          colorLink: brandColor,
          borderRadius: 8,
        },
      }}
    >
      {/* antd App 提供全局 message 上下文 */}
      <AntApp style={{ height: "100%" }}>
        <IconLibraryContext.Provider value={iconLib}>
          <App windowLabel={windowLabel} />
        </IconLibraryContext.Provider>
      </AntApp>
    </ConfigProvider>
  );
}

detectLabel().then((windowLabel) => {
  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
      <Root windowLabel={windowLabel} />
    </React.StrictMode>,
  );
});
