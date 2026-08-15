// 聊天式同步消息流:发送(出)与收到推送(入),localStorage 持久化 + 轻量订阅。
export interface ChatMsg {
  id: number
  direction: 'out' | 'in'
  content: string
  targets?: string[]    // 出:目标设备 id 列表
  deviceName?: string   // 入:来源设备名
  createdAt: number
}

const KEY = 'clipsync_chat_msgs'
const MAX = 50

type Listener = () => void
const listeners = new Set<Listener>()

export function getChatMsgs(): ChatMsg[] {
  try {
    const raw = localStorage.getItem(KEY)
    return raw ? (JSON.parse(raw) as ChatMsg[]) : []
  } catch {
    return []
  }
}

function persist(list: ChatMsg[]) {
  // 保留最新的 MAX 条;数组顺序 = 从旧到新,渲染时顶部旧、底部新
  localStorage.setItem(KEY, JSON.stringify(list.slice(-MAX)))
}

function notify() {
  listeners.forEach(fn => fn())
}

/** 记录一次本端发起的发送 */
export function addOutgoing(content: string, targets: string[]): void {
  const list = getChatMsgs()
  list.push({ id: Date.now(), direction: 'out', content: content.slice(0, 500), targets, createdAt: Date.now() })
  persist(list)
  notify()
}

/** 记录一条收到的剪贴板推送 */
export function addIncoming(deviceName: string, content: string): void {
  const list = getChatMsgs()
  list.push({ id: Date.now(), direction: 'in', content: content.slice(0, 500), deviceName, createdAt: Date.now() })
  persist(list)
  notify()
}

export function clearChatMsgs(): void {
  localStorage.removeItem(KEY)
  notify()
}

/** 订阅消息流变化,返回取消订阅函数 */
export function subscribeChatMsgs(fn: Listener): () => void {
  listeners.add(fn)
  return () => { listeners.delete(fn) }
}
