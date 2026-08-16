import { useEffect, useRef, useState } from 'react'
import { Card, Timeline, Typography, Empty, Skeleton, theme, Select, message } from 'antd'
import { Upload, Download, Monitor, Trash2 } from 'lucide-react'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { getActivities, listUsers, type ActivityLog } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

const PAGE_SIZE = 30

/** 同步记录:限高 + 内部滚动 + 滚到底自动加载更多 + 骨架屏 */
export default function SyncRecordsPage({ refreshTick, userFilter, onUserFilterChange }: {
  refreshTick: number
  userFilter: string | null
  onUserFilterChange: (v: string | null) => void
}) {
  const { token } = theme.useToken()
  const [items, setItems] = useState<ActivityLog[]>([])
  const [firstLoading, setFirstLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [hasMore, setHasMore] = useState(true)
  const [users, setUsers] = useState<Array<{ id: string; name: string }>>([])
  const wrapRef = useRef<HTMLDivElement>(null)
  const offsetRef = useRef(0)

  useEffect(() => { listUsers().then(setUsers).catch(() => message.error('用户列表加载失败')) }, [])

  const load = async (append: boolean) => {
    if (append) setLoadingMore(true); else setFirstLoading(true)
    try {
      const more = await getActivities(offsetRef.current + PAGE_SIZE, userFilter)
      setItems(prev => append ? [...prev, ...more] : more)
      offsetRef.current += more.length
      if (more.length < PAGE_SIZE) setHasMore(false)
    } catch (e) {
      // 首次加载提示失败;追加失败静默,下次滚动重试
      if (!append) message.error('同步记录加载失败:' + (e as Error).message)
    } finally {
      setFirstLoading(false)
      setLoadingMore(false)
    }
  }

  useEffect(() => { load(false) }, [refreshTick, userFilter]) // eslint-disable-line

  const onScroll = () => {
    const el = wrapRef.current
    if (!el || loadingMore || !hasMore) return
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - 100) load(true)
  }

  return (
    <Card
      id="clipsync-records-page"
      className="clipsync-page-card"
      title="同步记录"
      extra={
        <Select
          allowClear
          placeholder="全部用户"
          style={{ width: 180 }}
          value={userFilter ?? undefined}
          onChange={(v) => onUserFilterChange(v ?? null)}
          options={users.map(u => ({ value: u.id, label: u.name }))}
        />
      }
      style={{ borderRadius: 14, height: 'calc(100vh - 112px)', display: 'flex', flexDirection: 'column' }}
      styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' } }}
    >
      <div
        ref={wrapRef}
        onScroll={onScroll}
        style={{ flex: 1, minHeight: 0, overflow: 'auto', paddingRight: 4 }}
      >
        {firstLoading ? (
          <Skeleton active paragraph={{ rows: 9 }} style={{ paddingTop: 8 }} />
        ) : items.length === 0 ? (
          <Empty style={{ marginTop: 80 }} />
        ) : (
          <Timeline
            className="clipsync-timeline"
            style={{ marginTop: 8 }}
            items={items.map(a => ({
              color: a.action === 'push' ? '#10B981' : a.action === 'receive' ? '#2563EB' : a.action === 'connect' ? '#9333EA' : '#EF4444',
              children: (
                <div>
                  <Text strong>
                    {a.action === 'push' ? <><Upload size={14} color="#10B981" /> {a.deviceName} 推送了剪贴板内容</> :
                     a.action === 'receive' ? <><Download size={14} color="#2563EB" /> {a.deviceName} 接收了剪贴板内容</> :
                     a.action === 'connect' ? <><Monitor size={14} color="#9333EA" /> {a.deviceName} 已连接到服务端</> :
                     <><Trash2 size={14} color="#EF4444" /> {a.deviceName} 删除了剪贴板记录</>}
                  </Text>
                  {a.content ? <div style={{ color: token.colorTextSecondary, marginTop: 2 }}>{a.content}</div> : null}
                  <div style={{ color: token.colorTextTertiary, fontSize: 12, marginTop: 2 }}>{dayjs(a.createdAt).format('YYYY-MM-DD HH:mm:ss')} · {dayjs(a.createdAt).fromNow()}</div>
                </div>
              ),
            }))}
          />
        )}
        {loadingMore ? <Skeleton active paragraph={{ rows: 3 }} style={{ marginTop: 8 }} /> : null}
        {!hasMore && items.length > 0 ? (
          <div style={{ textAlign: 'center', color: '#9CA3AF', fontSize: 12, padding: '10px 0' }}>已加载全部 {items.length} 条记录</div>
        ) : null}
      </div>
    </Card>
  )
}
