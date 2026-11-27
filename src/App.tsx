import { Component, useMemo } from "react";
import type { ErrorInfo, ReactNode } from "react";
import ClipboardPopup from "./pages/ClipboardPopup";
import Settings from "./pages/Settings";
import TransferChat from "./pages/TransferChat";
import LinkToast from "./pages/LinkToast";
import ScreenOverlay from "./pages/ScreenOverlay";
import ScreenPin from "./pages/ScreenPin";
import PinTools from "./pages/PinTools";

/// 开发期错误边界:子组件崩溃时直接显示错误,避免整窗白屏无从排查
class ErrorBoundary extends Component<
  { children: ReactNode },
  { error: Error | null }
> {
  state = { error: null as Error | null };
  static getDerivedStateFromError(error: Error) {
    return { error };
  }
  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error("页面崩溃:", error, info);
  }
  render() {
    if (this.state.error) {
      return (
        <div style={{ padding: 20, fontSize: 13, whiteSpace: "pre-wrap" }}>
          <b>页面崩溃:</b>
          {"\n"}
          {this.state.error.message}
          {"\n\n"}
          {this.state.error.stack}
          {/* 截图覆盖窗崩溃时窗口仍全屏置顶吞输入,必须给退出通道 */}
          <div style={{ marginTop: 12 }}>
            <button
              onClick={() => { void import("@tauri-apps/api/core").then(({ invoke }) => invoke("screenshot_cancel")); }}
              style={{ padding: "4px 12px", borderRadius: 6, border: "1px solid #555", background: "#2563eb", color: "#fff", cursor: "pointer" }}
            >
              取消截图 (或再按截图快捷键)
            </button>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}

// 按 Tauri 窗口标签路由:main=剪贴板弹窗 / settings=设置 / toast=智能直达
export default function App({ windowLabel }: { windowLabel: string }) {
  const page = useMemo(() => {
    switch (windowLabel) {
      case "main":
        return <ClipboardPopup />;
      case "settings":
        return <Settings />;
      case "toast":
        return <LinkToast />;
      default:
        // 截图覆盖窗 / 贴图窗 / 贴图工具条(运行时创建,label 前缀匹配)
        if (windowLabel.startsWith("overlay-")) return <ScreenOverlay />;
        if (windowLabel.startsWith("pin-tools-")) return <PinTools />;
        if (windowLabel.startsWith("pin-")) return <ScreenPin />;
        return <ClipboardPopup />;
    }
  }, [windowLabel]);

  return <ErrorBoundary>{page}</ErrorBoundary>;
}
