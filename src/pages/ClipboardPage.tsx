import { useEffect, useRef, useState } from 'react'
import { Card, Table, Tag, Button, Space, Image as AntImage, Typography, Empty, message, Popconfirm, Drawer } from 'antd'
import { Copy, Trash2, Share2 } from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { getHistory, deleteEntry, imageUrl, type ClipboardEntry } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

export default function ClipboardPage({ refreshTick }: { refreshTick: number }) {
  const [data, setData] = useState<ClipboardEntry[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(false)
  const [detail, setDetail] = useState<ClipboardEntry | null>(null)
  const pageSize = 20
  const wrapRef = useRef<HTMLDivElement>(null)
  const [scrollY, setScrollY] = useState(360)

  // 卡片填满视口:表格 body 高度 = 容器高度 - 表头/分页等固定开销
  useEffect(() => {
    const measure = () => {
      const el = wrapRef.current
      if (el) setScrollY(Math.max(200, el.clientHeight - 140))
    }
    measure()
    const t = setTimeout(measure, 60)
    window.addEventListener('resize', measure)
    return () => { clearTimeout(t); window.removeEventListener('resize', measure) }
  }, [loading])

  const load = (p: number) => {
    setLoading(true)
    getHistory((p - 1) * pageSize, pageSize)
      .then(res => { setData(res.items); setTotal(res.total) })
      .finally(() => setLoading(false))
  }
  useEffect(() => { load(page) }, [page, refreshTick]) // eslint-disable-line

  const columns: ColumnsType<ClipboardEntry> = [
    {
      title: '类型', dataIndex: 'type', width: 90,
      render: (t: string) => <Tag color={t === 'Text' ? 'blue' : 'purple'}>{t === 'Text' ? '文本' : '图片'}</Tag>,
    },
    {
      title: '内容', dataIndex: 'text', ellipsis: true,
      render: (_, r) => (
        <Space>
          {r.type === 'Image' ? <AntImage src={imageUrl(r.imageRef)} width={44} height={34} style={{ borderRadius: 6, objectFit: 'cover' }} /> : null}
          <Text ellipsis style={{ maxWidth: 420 }}>{r.type === 'Image' ? (r.text ?? '图片') : r.text}</Text>
        </Space>
      ),
    },
    {
      title: '来源设备', dataIndex: 'deviceName', width: 160,
      render: (v: string) => <Tag color="blue" style={{ borderRadius: 999 }}>{v}</Tag>,
    },
    {
      title: '时间', dataIndex: 'createdAt', width: 160,
      render: (v: string) => <Text type="secondary">{dayjs(v).format('YYYY-MM-DD HH:mm:ss')}</Text>,
    },
    {
      title: '操作', key: 'action', width: 150,
      render: (_, r) => (
        <Space size={4}>
          <Button type="link" size="small" icon={<Share2 size={16} />} onClick={() => message.info('已推送')} />
          <Button
            type="link" size="small" icon={<Copy size={16} />}
            onClick={() => { if (r.text) navigator.clipboard.writeText(r.text); message.success('已复制') }}
          />
          <Popconfirm
            title="确定删除这条记录?"
            onConfirm={async () => {
              await deleteEntry(r.id)
              message.success('已删除')
              load(page)
            }}
          >
            <Button type="link" size="small" danger icon={<Trash2 size={16} />} />
          </Popconfirm>
          <Button type="link" size="small" onClick={() => setDetail(r)}>详情</Button>
        </Space>
      ),
    },
  ]

  return (
    <Card
      id="clipsync-clipboard-page"
      className="clipsync-page-card"
      title="剪贴板历史"
      style={{ borderRadius: 14, height: 'calc(100vh - 112px)', display: 'flex', flexDirection: 'column' }}
      styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' } }}
    >
      <div ref={wrapRef} style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <Table<ClipboardEntry>
        id="clipsync-clipboard-history-table"
        className="clipsync-table"
        rowKey="id"
        columns={columns}
        dataSource={data}
        loading={loading}
        scroll={{ y: scrollY }}
        pagination={{
          current: page,
          total,
          pageSize,
          showSizeChanger: false,
          onChange: setPage,
          showTotal: t => `共 ${t} 条`,
        }}
        locale={{ emptyText: <Empty description="暂无剪贴板记录" /> }}
      />
      </div>
      <Drawer
        id="clipsync-clipboard-detail-drawer"
        className="clipsync-drawer"
        title="剪贴板详情"
        open={!!detail}
        onClose={() => setDetail(null)}
        width={480}
      >
        {detail ? (
          <div>
            <p><Tag color="blue">{detail.type === 'Text' ? '文本' : '图片'}</Tag>
              <Tag color="blue" style={{ borderRadius: 999 }}>{detail.deviceName}</Tag>
              <Text type="secondary">{dayjs(detail.createdAt).format('YYYY-MM-DD HH:mm:ss')}</Text></p>
            {detail.type === 'Image' ? (
              <AntImage src={imageUrl(detail.imageRef)} style={{ width: '100%', borderRadius: 10 }} />
            ) : (
              <pre style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all', background: 'rgba(0, 0, 0, 0.045)', padding: 12, borderRadius: 10 }}>{detail.text}</pre>
            )}
          </div>
        ) : null}
      </Drawer>
    </Card>
  )
}
