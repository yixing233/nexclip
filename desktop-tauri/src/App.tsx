import { Component, useMemo } from "react";
import type { ErrorInfo, ReactNode } from "react";
import ClipboardPopup from "./pages/ClipboardPopup";
import Settings from "./pages/Settings";
import TransferChat from "./pages/TransferChat";
import LinkToast from "./pages/LinkToast";

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
        return <ClipboardPopup />;
    }
  }, [windowLabel]);

  return <ErrorBoundary>{page}</ErrorBoundary>;
}
