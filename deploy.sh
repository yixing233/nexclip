#!/usr/bin/env bash
# NexClip 服务端部署脚本
#
# 用法：在服务器上的部署目录里执行
#   cd /var/www/nexclip-server && ./deploy.sh
#
# 设计原则：
#   1. 部署目录就是 git 检出，线上跑哪个 commit 随时可查（git rev-parse HEAD）
#   2. 构建失败绝不重启服务：新产物先构建到 dist.new，成功后才原子替换 dist
#   3. 重启后健康检查不过，自动回滚到上一版 dist 并再次重启
#
# 环境变量（可选）：
#   PM2_APP   默认 nexclip-server
#   HEALTH_URL 默认 http://127.0.0.1:${SC_PORT:-5033}/api/health
#   BRANCH    默认 server

set -Eeuo pipefail

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$APP_DIR"

PM2_APP="${PM2_APP:-nexclip-server}"
BRANCH="${BRANCH:-server}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:${SC_PORT:-5033}/api/health}"
TS="$(date +%Y%m%d-%H%M%S)"

log()  { printf '\033[36m[deploy]\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[deploy]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31m[deploy]\033[0m %s\n' "$*" >&2; exit 1; }

trap 'warn "第 $LINENO 行失败，已中止（服务未重启，仍运行上一版）"' ERR

# ---------- 0. 前置检查 ----------
[ -d .git ] || die "$APP_DIR 不是 git 仓库，无法部署。请先按 README 的『部署（生产）』一节把目录切成检出。"

command -v node >/dev/null || die "找不到 node"
command -v npm  >/dev/null || die "找不到 npm"

# 构建只要求 tsc 能跑；运行时版本由 pm2 的解释器决定，这里只做提示
NODE_MAJOR="$(node -v | sed 's/^v\([0-9]*\).*/\1/')"
[ "$NODE_MAJOR" -ge 18 ] || die "构建需要 Node.js >= 18，当前 $(node -v)"
RUNTIME_NODE="$(pm2 jlist 2>/dev/null | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const a=JSON.parse(s).filter(p=>p.name===process.argv[1]);console.log(a[0]?.pm2_env?.exec_interpreter||"")}catch{}})' "$PM2_APP")"
if [ -n "$RUNTIME_NODE" ] && [ -x "$RUNTIME_NODE" ]; then
  RT_MAJOR="$("$RUNTIME_NODE" -v | sed 's/^v\([0-9]*\).*/\1/')"
  [ "$RT_MAJOR" -ge 22 ] || warn "pm2 用的 node 是 $("$RUNTIME_NODE" -v)，低于 22.5，node:sqlite 可能不可用"
  if [ "$(command -v node)" != "$RUNTIME_NODE" ]; then
    warn "PATH 上的 node（$(node -v)）与 pm2 运行时（$("$RUNTIME_NODE" -v)）不同；构建用前者，运行用后者"
  fi
else
  warn "无法确定 pm2 运行时版本，跳过运行时检查"
fi

if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  warn "工作区有未提交改动，可能被覆盖："
  git status --short --untracked-files=no >&2
  die "请先处理这些改动（git stash / git checkout -- .），再部署"
fi

PREV_SHA="$(git rev-parse --short HEAD)"
log "部署前版本：$PREV_SHA（$(git log -1 --format=%s HEAD)）"

# ---------- 1. 拉取代码 ----------
log "拉取 origin/$BRANCH ..."
git fetch --prune origin
git checkout -q "$BRANCH" 2>/dev/null || die "切换到 $BRANCH 分支失败"
git pull --ff-only origin "$BRANCH" || die "git pull --ff-only 失败：线上有本地提交或历史分叉，请人工处理"

NEW_SHA="$(git rev-parse --short HEAD)"
if [ "$PREV_SHA" = "$NEW_SHA" ]; then
  log "代码无变化（$NEW_SHA），仍会重新构建以确保 dist 与源码一致"
else
  log "更新：$PREV_SHA -> $NEW_SHA"
  git log --oneline "$PREV_SHA..$NEW_SHA" | sed 's/^/  /'
fi

# ---------- 2. 安装依赖 ----------
# 必须装 devDependencies：构建需要 tsc，服务器上没装就完全没法重新 build
log "安装依赖（含 devDependencies）..."
npm ci --include=dev --no-audit --no-fund || die "npm ci 失败"

# ---------- 3. 构建到 dist.new ----------
log "构建 src/ -> dist.new ..."
rm -rf dist.new
if ! npx tsc -p tsconfig.json --outDir dist.new; then
  rm -rf dist.new
  die "TypeScript 构建失败，dist/ 保持原样，服务未重启"
fi
[ -f dist.new/server.js ] || { rm -rf dist.new; die "构建产物缺少 server.js，已放弃"; }

# ---------- 4. 原子替换 dist ----------
rm -rf "dist.old"
[ -d dist ] && mv dist "dist.old"
mv dist.new dist
log "dist 已更新（上一版保留在 dist.old）"

# ---------- 5. 重启 ----------
log "pm2 restart $PM2_APP ..."
if ! pm2 restart "$PM2_APP" --update-env >/dev/null 2>&1; then
  pm2 restart "$PM2_APP" || true
fi

# ---------- 6. 健康检查 + 自动回滚 ----------
health_ok() {
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    if curl -fsS -m 3 "$HEALTH_URL" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

if health_ok; then
  log "健康检查通过：$(curl -fsS -m 3 "$HEALTH_URL")"
else
  warn "健康检查失败（$HEALTH_URL），开始回滚到 $PREV_SHA 的产物"
  pm2 logs "$PM2_APP" --lines 30 --nostream 2>/dev/null | tail -40 >&2 || true
  if [ -d dist.old ]; then
    rm -rf dist
    mv dist.old dist
    pm2 restart "$PM2_APP" >/dev/null 2>&1 || true
    health_ok && log "已回滚，服务恢复（代码仍是 $NEW_SHA，产物是 $PREV_SHA）"
  fi
  die "部署失败已回滚，请检查上面的 pm2 日志"
fi

rm -rf dist.old
log "部署完成：$NEW_SHA → $(pm2 describe "$PM2_APP" | sed -n 's/.*status *│ *//p' | head -1)"
log "线上版本确认：git -C $APP_DIR rev-parse --short HEAD"
