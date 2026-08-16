import { useEffect, useRef, useState } from 'react'
import { Card, Table, Tag, Button, Space, Image as AntImage, Typography, Empty, message, Popconfirm, Drawer, Skeleton, Select } from 'antd'
import { Copy, Trash2, Share2 } from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { getHistory, deleteEntry, imageUrl, listUsers, type ClipboardEntry } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

const PAGE_SIZE = 20

/** 剪贴板历史:限高内滚 + 滚动到底自动加载更多(懒加载)+ 骨架屏;search 为顶栏全局搜索(服务端文本过滤) */
export default function ClipboardPage({ refreshTick, userFilter, onUserFilterChange, search = '' }: {
  refreshTick: number
  userFilter: string | null
  onUserFilterChange: (v: string | null) => void
  search?: string
}) {
  const [data, setData] = useState<ClipboardEntry[]>([])
  const [total, setTotal] = useState(0)
  const [firstLoading, setFirstLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [detail, setDetail] = useState<ClipboardEntry | null>(null)
  const [users, setUsers] = useState<Array<{ id: string; name: string }>>([])
  const wrapRef = useRef<HTMLDivElement>(null)
  const offsetRef = useRef(0)
  const hasMoreRef = useRef(true)

  // 搜索关键字防抖(顶栏连续输入时避免每键一次请求)
  const [debouncedSearch, setDebouncedSearch] = useState(search)
  useEffect(() => {
    const t = setTimeout(() => setDebouncedSearch(search), 300)
    return () => clearTimeout(t)
  }, [search])

  // 用户筛选下拉(管理端)
  useEffect(() => { listUsers().then(setUsers).catch(() => message.error('用户列表加载失败')) }, [])

  const fetchPage = async (offset: number, first: boolean) => {
    if (first) setFirstLoading(true); else setLoadingMore(true)
    try {
      const res = await getHistory(offset, PAGE_SIZE, userFilter, debouncedSearch)
      setTotal(res.total)
      setData(prev => first
        ? res.items
        : [...prev, ...res.items.filter(n => !prev.some(o => o.id === n.id))])
      offsetRef.current = offset + res.items.length
      if (offset + res.items.length >= res.total) hasMoreRef.current = false
      else hasMoreRef.current = true
    } catch (e) {
      if (first) message.error('剪贴板历史加载失败:' + (e as Error).message)
    } finally {
      setFirstLoading(false)
      setLoadingMore(false)
    }
  }

  // 首次/刷新/用户筛选或搜索变化:重置并加载第一页
  useEffect(() => {
    offsetRef.current = 0
    hasMoreRef.current = true
    fetchPage(0, true)
  }, [refreshTick, userFilter, debouncedSearch]) // eslint-disable-line

  const onScroll = (e: React.UIEvent<HTMLElement>) => {
    const el = e.currentTarget
    if (el.scrollTop + el.clientHeight >= el.scrollHeight - 100 && !loadingMore && hasMoreRef.current) {
      fetchPage(offsetRef.current, false)
    }
  }

  const doDelete = async (id: number) => {
    try {
      await deleteEntry(id)
      message.success('已删除')
      fetchPage(0, false) // 刷新当前列表(不闪骨架)
    } catch (e) {
      message.error('删除失败:' + (e as Error).message)
    }
  }

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
            onConfirm={() => doDelete(r.id)}
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
      title={'剪贴板历史' + (total > 0 ? ' (' + data.length + ' / ' + total + ')' : '')}
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
        {firstLoading && data.length === 0 ? (
          <Skeleton active paragraph={{ rows: 8 }} style={{ paddingTop: 8 }} />
        ) : (
          <Table<ClipboardEntry>
            id="clipsync-clipboard-history-table"
            className="clipsync-table"
            rowKey="id"
            columns={columns}
            dataSource={data}
            loading={loadingMore}
            pagination={false}
            locale={{ emptyText: <Empty description="暂无剪贴板记录" /> }}
          />
        )}
        {!hasMoreRef.current && data.length > 0 ? (
          <div style={{ textAlign: 'center', color: '#9CA3AF', fontSize: 12, padding: '8px 0' }}>已加载全部 {data.length} 条</div>
        ) : null}
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
