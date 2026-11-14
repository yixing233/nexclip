import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Tauri 约定:dev 固定 1420 端口,构建产物输出到 dist(tauri.conf.json 的 frontendDist)
export default defineConfig({
  plugins: [react()],
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    watch: {
      // Rust 产物目录必须排除:Windows 上 cargo 正在写 dll 时 fs.watch 会 EBUSY
      ignored: ["**/src-tauri/**"],
    },
  },
  build: {
    target: "chrome110",
    outDir: "dist",
  },
});
