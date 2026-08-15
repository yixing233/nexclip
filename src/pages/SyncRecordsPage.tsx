import { useEffect, useState } from 'react'
import { Card, Timeline, Typography, Empty, Button, message , theme } from 'antd'
import { Upload, Download, Monitor, Trash2 } from 'lucide-react'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { getActivities, type ActivityLog } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

export default function SyncRecordsPage() {
  const { token } = theme.useToken()
  const [items, setItems] = useState<ActivityLog[]>([])
  const [limit, setLimit] = useState(30)
  useEffect(() => { getActivities(limit).then(setItems).catch(() => {}) }, [limit])

  return (
    <Card
      id="clipsync-records-page"
      className="clipsync-page-card"
      title="同步记录"
      style={{ borderRadius: 14 }}
      extra={<Button size="small" onClick={() => { setLimit(limit + 30); message.success('已加载更多') }}>加载更多</Button>}
    >
      {items.length === 0 ? <Empty /> : (
        <Timeline
          className="clipsync-timeline"
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
    </Card>
  )
}
