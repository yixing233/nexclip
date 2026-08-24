import { useEffect, useRef, useState } from 'react'
import { Card, Table, Tag, Button, Space, Image as AntImage, Typography, Empty, message, Popconfirm, Drawer, Skeleton, Select, Modal, Input, Radio, Upload, Checkbox } from 'antd'
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome'
import { faPaperPlane, faShareNodes, faCopy, faTrashCan, faFileLines, faImage, faPlus } from '@fortawesome/free-solid-svg-icons'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { getHistory, deleteEntry, imageUrl, listUsers, pushText, pushImage, sendToDevices, getDevices, deviceId, getDefaultDeviceName, type ClipboardEntry, type DeviceInfo } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography
const { TextArea } = Input

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

  // 手动推送弹窗状态
  const [pushModalOpen, setPushModalOpen] = useState(false)
  const [pushType, setPushType] = useState<'text' | 'image'>('text')
  const [pushContent, setPushContent] = useState('')
  const [pushImageFile, setPushImageFile] = useState<File | null>(null)
  const [pushSending, setPushSending] = useState(false)
  const [onlineDevices, setOnlineDevices] = useState<DeviceInfo[]>([])
  const [selectedDeviceIds, setSelectedDeviceIds] = useState<string[]>([])

  // 搜索关键字防抖(顶栏连续输入时避免每键一次请求)
  const [debouncedSearch, setDebouncedSearch] = useState(search)
  useEffect(() => {
    const t = setTimeout(() => setDebouncedSearch(search), 300)
    return () => clearTimeout(t)
  }, [search])

  // 用户筛选下拉(管理端)
  useEffect(() => { listUsers().then(setUsers).catch(() => message.error('用户列表加载失败')) }, [])

  // 打开推送弹窗时获取在线设备
  useEffect(() => {
    if (pushModalOpen) {
      getDevices().then(devs => {
        setOnlineDevices(devs)
        setSelectedDeviceIds(devs.filter(d => d.online).map(d => d.id))
      }).catch(() => {})
    }
  }, [pushModalOpen])

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
      fetchPage(0, false)
    } catch (e) {
      message.error('删除失败:' + (e as Error).message)
    }
  }

  const doPushItem = async (entry: ClipboardEntry) => {
    try {
      if (entry.type === 'Text' && entry.text) {
        await pushText(entry.text, deviceId(), getDefaultDeviceName())
        message.success('已成功推送到所有设备')
      } else if (entry.type === 'Image' && entry.imageRef) {
        const resp = await fetch(imageUrl(entry.imageRef))
        const blob = await resp.blob()
        await pushImage(blob, deviceId(), getDefaultDeviceName())
        message.success('已成功推送到所有设备')
      }
    } catch (e) {
      message.error('推送失败: ' + (e as Error).message)
    }
  }

  const handleManualPush = async () => {
    if (pushType === 'text') {
      if (!pushContent.trim()) {
        message.warning('请输入要推送的文本内容')
        return
      }
      setPushSending(true)
      try {
        if (selectedDeviceIds.length > 0 && selectedDeviceIds.length < onlineDevices.length) {
          await sendToDevices(pushContent.trim(), selectedDeviceIds)
        } else {
          await pushText(pushContent.trim(), deviceId(), getDefaultDeviceName())
        }
        message.success('已成功推送到设备')
        setPushModalOpen(false)
        setPushContent('')
      } catch (e) {
        message.error('推送失败: ' + (e as Error).message)
      } finally {
        setPushSending(false)
      }
    } else {
      if (!pushImageFile) {
        message.warning('请选择要推送的图片')
        return
      }
      setPushSending(true)
      try {
        await pushImage(pushImageFile, deviceId(), getDefaultDeviceName())
        message.success('已成功推送到设备')
        setPushModalOpen(false)
        setPushImageFile(null)
      } catch (e) {
        message.error('推送失败: ' + (e as Error).message)
      } finally {
        setPushSending(false)
      }
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
      title: '操作', key: 'action', width: 160,
      render: (_, r) => (
        <Space size={4}>
          <Button
            type="link"
            size="small"
            icon={<FontAwesomeIcon icon={faShareNodes} />}
            title="推送到所有设备"
            onClick={() => doPushItem(r)}
          />
          <Button
            type="link"
            size="small"
            icon={<FontAwesomeIcon icon={faCopy} />}
            title="复制"
            onClick={() => { if (r.text) navigator.clipboard.writeText(r.text); message.success('已复制') }}
          />
          <Popconfirm
            title="确定删除这条记录?"
            onConfirm={() => doDelete(r.id)}
          >
            <Button
              type="link"
              size="small"
              danger
              icon={<FontAwesomeIcon icon={faTrashCan} />}
              title="删除"
            />
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
        <Space>
          <Button
            type="primary"
            icon={<FontAwesomeIcon icon={faPaperPlane} />}
            onClick={() => setPushModalOpen(true)}
          >
            手动推送
          </Button>
          <Select
            allowClear
            placeholder="全部用户"
            style={{ width: 160 }}
            value={userFilter ?? undefined}
            onChange={(v) => onUserFilterChange(v ?? null)}
            options={users.map(u => ({ value: u.id, label: u.name }))}
          />
        </Space>
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

      {/* 详情抽屉 */}
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
            <p>
              <Tag color="blue">{detail.type === 'Text' ? '文本' : '图片'}</Tag>
              <Tag color="blue" style={{ borderRadius: 999 }}>{detail.deviceName}</Tag>
              <Text type="secondary">{dayjs(detail.createdAt).format('YYYY-MM-DD HH:mm:ss')}</Text>
            </p>
            {detail.type === 'Image' ? (
              <AntImage src={imageUrl(detail.imageRef)} style={{ width: '100%', borderRadius: 10 }} />
            ) : (
              <pre style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all', background: 'rgba(0, 0, 0, 0.045)', padding: 12, borderRadius: 10 }}>{detail.text}</pre>
            )}
            <div style={{ marginTop: 16 }}>
              <Button
                type="primary"
                icon={<FontAwesomeIcon icon={faShareNodes} />}
                onClick={() => doPushItem(detail)}
              >
                推送到所有设备
              </Button>
            </div>
          </div>
        ) : null}
      </Drawer>

      {/* 手动推送模态框 */}
      <Modal
        title={
          <Space>
            <FontAwesomeIcon icon={faPaperPlane} style={{ color: '#2563EB' }} />
            <span>手动推送至设备</span>
          </Space>
        }
        open={pushModalOpen}
        onCancel={() => setPushModalOpen(false)}
        onOk={handleManualPush}
        confirmLoading={pushSending}
        okText="立即推送"
        cancelText="取消"
        width={520}
      >
        <div style={{ padding: '12px 0' }}>
          <Radio.Group
            value={pushType}
            onChange={e => setPushType(e.target.value)}
            style={{ marginBottom: 16 }}
          >
            <Radio.Button value="text">
              <Space>
                <FontAwesomeIcon icon={faFileLines} />
                <span>文本消息</span>
              </Space>
            </Radio.Button>
            <Radio.Button value="image">
              <Space>
                <FontAwesomeIcon icon={faImage} />
                <span>图片推送</span>
              </Space>
            </Radio.Button>
          </Radio.Group>

          {pushType === 'text' ? (
            <div>
              <TextArea
                rows={5}
                placeholder="输入要发送的文本内容或从剪贴板粘贴…"
                value={pushContent}
                onChange={e => setPushContent(e.target.value)}
                maxLength={50000}
                showCount
              />
            </div>
          ) : (
            <div>
              <Upload.Dragger
                maxCount={1}
                beforeUpload={(file) => {
                  setPushImageFile(file)
                  return false
                }}
                onRemove={() => setPushImageFile(null)}
                accept="image/*"
              >
                <p className="ant-upload-drag-icon">
                  <FontAwesomeIcon icon={faPlus} size="2x" style={{ color: '#2563EB' }} />
                </p>
                <p className="ant-upload-text">点击或将图片拖拽到此处选择</p>
                <p className="ant-upload-hint">支持 PNG、JPG、GIF、WEBP 格式图片</p>
              </Upload.Dragger>
            </div>
          )}

          {onlineDevices.length > 0 && (
            <div style={{ marginTop: 16 }}>
              <Text type="secondary" style={{ fontSize: 12, display: 'block', marginBottom: 6 }}>
                目标设备（默认全选在线设备）：
              </Text>
              <Checkbox.Group
                value={selectedDeviceIds}
                onChange={vals => setSelectedDeviceIds(vals as string[])}
              >
                <Space wrap>
                  {onlineDevices.map(d => (
                    <Checkbox key={d.id} value={d.id}>
                      <Tag color={d.online ? 'green' : 'default'} style={{ borderRadius: 999 }}>
                        {d.name} {d.online ? '(在线)' : '(离线)'}
                      </Tag>
                    </Checkbox>
                  ))}
                </Space>
              </Checkbox.Group>
            </div>
          )}
        </div>
      </Modal>
    </Card>
  )
}
