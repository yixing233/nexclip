import { useEffect, useState } from "react";
import { ExternalLink, Copy, X } from "lucide-react";
import { smartExec, hideToast, type SmartAction } from "../ipc/chat";

/// 智能直达 toast:右下角非激活卡片,6s 倒计时后自动隐藏(后端兜底)
export default function LinkToast() {
  const [action, setAction] = useState<SmartAction | null>(null);
  const [countdown, setCountdown] = useState(6);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    void (async () => {
      try {
        const { listen } = await import("@tauri-apps/api/event");
        unlisten = await listen<SmartAction>("smart-action", (e) => {
          setAction(e.payload);
          setCountdown(6);
        });
      } catch {
        /* 浏览器环境 */
      }
    })();
    return () => unlisten?.();
  }, []);

  // 倒计时 → 隐藏
  useEffect(() => {
    if (!action) return;
    const t = window.setInterval(() => {
      setCountdown((c) => {
        if (c <= 1) {
          void hideToast();
          setAction(null);
          return 0;
        }
        return c - 1;
      });
    }, 1000);
    return () => window.clearInterval(t);
  }, [action]);

  const exec = async (spec: SmartAction["primary"]) => {
    try {
      await smartExec(spec);
    } finally {
      await hideToast();
      setAction(null);
    }
  };

  if (!action) {
    return null;
  }

  const isColor = action.previewColor != null;

  return (
    <div
      style={{
        padding: 12,
        borderRadius: 10,
        background: "var(--ant-color-bg-container, #fff)",
        boxShadow: "0 6px 24px rgba(0,0,0,0.22)",
        border: "1px solid rgba(128,128,128,0.2)",
        display: "flex",
        flexDirection: "column",
        gap: 8,
        height: "100vh",
        boxSizing: "border-box",
      }}
    >
      {/* 头部:标题 + 倒计时 + 关闭 */}
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        {isColor && (
          <span
            style={{
              width: 22,
              height: 22,
              borderRadius: 6,
              background: action.previewColor!,
              border: "1px solid rgba(128,128,128,0.3)",
              flexShrink: 0,
            }}
          />
        )}
        <span
          style={{
            fontSize: 13,
            fontWeight: 600,
            flex: 1,
            overflow: "hidden",
            display: "-webkit-box",
            WebkitLineClamp: 2,
            WebkitBoxOrient: "vertical",
            wordBreak: "break-all",
            lineHeight: 1.4,
          }}
          title={action.title}
        >
          {action.title}
        </span>
        <span style={{ fontSize: 11, color: "var(--ant-color-text-tertiary, #999)" }}>{countdown}s</span>
        <button
          onClick={() => {
            void hideToast();
            setAction(null);
          }}
          style={{ border: "none", background: "transparent", cursor: "pointer", color: "var(--ant-color-text-tertiary, #999)", padding: 2, display: "flex" }}
        >
          <X size={14} />
        </button>
      </div>

      <div
        style={{
          fontSize: 11.5,
          color: "var(--ant-color-text-tertiary, #888)",
          // 长链接允许换行(最多两行),不再单行截断
          overflow: "hidden",
          display: "-webkit-box",
          WebkitLineClamp: 2,
          WebkitBoxOrient: "vertical",
          wordBreak: "break-all",
          lineHeight: 1.5,
          userSelect: "text",
          cursor: "text",
        }}
        title={action.subtitle}
      >
        {action.subtitle}
      </div>

      {/* 颜色三格式 */}
      {isColor && (
        <div style={{ display: "flex", gap: 6, fontSize: 11, fontFamily: "monospace" }}>
          <span style={{ background: "rgba(128,128,128,0.1)", padding: "2px 6px", borderRadius: 4 }}>
            {action.hexColor}
          </span>
          <span style={{ background: "rgba(128,128,128,0.1)", padding: "2px 6px", borderRadius: 4 }}>
            {action.rgbColor}
          </span>
          <span style={{ background: "rgba(128,128,128,0.1)", padding: "2px 6px", borderRadius: 4 }}>
            {action.hslColor}
          </span>
        </div>
      )}

      {/* 提取码 */}
      {action.extractionCode && (
        <div style={{ display: "flex", alignItems: "center", gap: 6, fontSize: 12 }}>
          提取码:
          <span
            style={{
              fontFamily: "monospace",
              fontWeight: 700,
              fontSize: 14,
              background: "rgba(37,99,235,0.1)",
              color: "var(--ant-color-primary, #2563eb)",
              padding: "2px 8px",
              borderRadius: 4,
            }}
          >
            {action.extractionCode}
          </span>
        </div>
      )}

      {/* 按钮区 */}
      <div style={{ display: "flex", gap: 8, marginTop: "auto" }}>
        <button
          onClick={() => void exec(action.primary)}
          style={{
            flex: 1,
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            gap: 6,
            padding: "7px 0",
            border: "none",
            borderRadius: 7,
            background: "var(--ant-color-primary, #2563EB)",
            color: "#fff",
            fontSize: 12.5,
            fontWeight: 600,
            cursor: "pointer",
          }}
        >
          <ExternalLink size={13} />
          {action.primary.kind === "copy_text" ? (
            <>
              <Copy size={12} /> {action.primary.arg.slice(0, 18)}
            </>
          ) : (
            action.primary.kind === "open_url"
              ? "打开链接"
              : action.primary.kind === "open_path"
                ? "打开文件夹"
                : action.primary.kind === "locate_path"
                  ? "定位文件"
                  : action.primary.kind === "open_file"
                    ? "打开文件"
                    : "执行"
          )}
        </button>
        {action.secondary && (
          <button
            onClick={() => void exec(action.secondary!)}
            style={{
              display: "flex",
              alignItems: "center",
              gap: 5,
              padding: "7px 10px",
              border: "1px solid rgba(128,128,128,0.3)",
              borderRadius: 7,
              background: "transparent",
              color: "var(--ant-color-text, #333)",
              fontSize: 12,
              cursor: "pointer",
            }}
          >
            {action.secondary.kind === "copy_text" ? <Copy size={12} /> : <ExternalLink size={12} />}
            {action.secondary.kind === "copy_text" ? "复制" : "终端"}
          </button>
        )}
      </div>
    </div>
  );
}
