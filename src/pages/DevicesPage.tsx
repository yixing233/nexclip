import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Card, Table, Tag, Badge, Typography, Space, Button, Input, Modal, Form, Popconfirm,
  message, Select, Empty, Skeleton,
} from 'antd'
import { Monitor, Pencil, Trash2, RefreshCw, KeyRound, Copy, RotateCcw } from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { getDevices, renameDevice, removeDevice, createPairingCode, revokePairingCode, type DeviceInfo } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

export default function DevicesPage({ refreshTick, userFilter, onUserFilterChange }: {
  refreshTick: number
  userFilter: string | null
  onUserFilterChange: (v: string | null) => void
}) {
  const [devices, setDevices] = useState<DeviceInfo[]>([])
  const [loading, setLoading] = useState(false)
  const [filter, setFilter] = useState<'all' | 'online' | 'offline'>('all')
  const [keyword, setKeyword] = useState('')

  // 重命名弹窗
  const [renameTarget, setRenameTarget] = useState<DeviceInfo | null>(null)
  const [renameForm] = Form.useForm()
  const [renaming, setRenaming] = useState(false)

  // 生成配对码弹窗
  const [pairOpen, setPairOpen] = useState(false)
  const [pairCode, setPairCode] = useState<{ code: string; expiresAt: number } | null>(null)
  const [pairUserId, setPairUserId] = useState<string | null>(null)
  const [pairLoading, setPairLoading] = useState(false)
  const [nowTs, setNowTs] = useState(Date.now())

  const openPairing = async () => {
    setPairLoading(true)
    try {
      // 重新生成前先作废旧码(未使用的话)
      if (pairCode) revokePairingCode(pairCode.code).catch(() => {})
      const r = await createPairingCode()
      setPairCode({ code: r.code, expiresAt: Date.parse(r.expiresAt) })
      setPairUserId(r.userId ?? null)
      setNowTs(Date.now())
    } catch {
      message.error('生成配对码失败')
    } finally {
      setPairLoading(false)
    }
  }

  const closePairing = () => {
    if (pairCode) revokePairingCode(pairCode.code).catch(() => {})
    setPairCode(null)
    setPairOpen(false)
  }

  // 倒计时(每秒刷新)
  useEffect(() => {
    if (!pairOpen || !pairCode) return
    const t = setInterval(() => setNowTs(Date.now()), 1000)
    return () => clearInterval(t)
  }, [pairOpen, pairCode])

  const remaining = pairCode ? Math.max(0, Math.floor((pairCode.expiresAt - nowTs) / 1000)) : 0
  const expired = pairCode ? remaining <= 0 : false
  const countdown = String(Math.floor(remaining / 60)).padStart(2, '0') + ':' + String(remaining % 60).padStart(2, '0')

  const load = () => {
    setLoading(true)
    getDevices()
      .then(setDevices)
      .catch(e => message.error('设备列表加载失败:' + (e as Error).message))
      .finally(() => setLoading(false))
  }
  useEffect(() => { load() }, [refreshTick]) // eslint-disable-line

  const wrapRef = useRef<HTMLDivElement>(null)
  const [scrollY, setScrollY] = useState(360)

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

  const filtered = useMemo(() => {
    let list = devices
    if (filter === 'online') list = list.filter(d => d.online)
    if (filter === 'offline') list = list.filter(d => !d.online)
    if (userFilter) list = list.filter(d => d.userId === userFilter)
    if (keyword.trim()) {
      const k = keyword.trim().toLowerCase()
      list = list.filter(d =>
        d.name.toLowerCase().includes(k) ||
        (d.ip ?? '').toLowerCase().includes(k) ||
        d.platform.toLowerCase().includes(k),
      )
    }
    return list
  }, [devices, filter, keyword, userFilter])

  const onlineCount = devices.filter(d => d.online).length

  const columns: ColumnsType<DeviceInfo> = [
    {
      title: '设备', dataIndex: 'name',
      render: (v: string, r) => (
        <Space>
          <Monitor size={16} color="#2563EB" />
          <span className="clipsync-device-name">{v}</span>
          {r.online ? <Badge status="success" /> : null}
        </Space>
      ),
    },
    {
      title: '平台', dataIndex: 'platform', width: 110,
      render: (v: string) => <Tag>{v}</Tag>,
    },
    { title: 'IP 地址', dataIndex: 'ip', width: 150, render: (v?: string | null) => <Text type="secondary">{v ?? '-'}</Text> },
    { title: '版本', dataIndex: 'version', width: 130, render: (v?: string | null) => v ?? '-' },
    {
      title: '状态', dataIndex: 'online', width: 100,
      render: (o: boolean) => <Badge status={o ? 'success' : 'default'} text={o ? '在线' : '离线'} />,
    },
    {
      title: '绑定', dataIndex: 'bound', width: 110,
      render: (v?: boolean, r?: DeviceInfo) => (v ? <Tag color="green">已绑定 {r?.userId}</Tag> : <Tag>未绑定</Tag>),
    },
    {
      title: '最后活跃', dataIndex: 'lastSeenAt', width: 120,
      render: (v: string) => <Text type="secondary">{dayjs(v).fromNow()}</Text>,
    },
    {
      title: '操作', key: 'action', width: 140, fixed: 'right',
      render: (_, r) => (
        <Space size={4}>
          <Button
            type="link" size="small" icon={<Pencil size={16} />}
            onClick={() => { setRenameTarget(r); renameForm.setFieldsValue({ name: r.name }) }}
          >
            重命名
          </Button>
          <Popconfirm
            title={`确定移除设备「${r.name}」?`}
            description="移除后设备 Token 立即失效,需重新配对"
            okText="移除"
            okButtonProps={{ danger: true }}
            onConfirm={async () => {
              try {
                await removeDevice(r.id)
                message.success('已移除设备')
                load()
              } catch (e) {
                message.error('移除失败:' + (e as Error).message)
              }
            }}
          >
            <Button type="link" size="small" danger icon={<Trash2 size={16} />} />
          </Popconfirm>
        </Space>
      ),
    },
  ]

  const doRename = async () => {
    if (!renameTarget) return
    const values = await renameForm.validateFields()
    setRenaming(true)
    try {
      await renameDevice(renameTarget.id, values.name.trim())
      message.success('已重命名')
      setRenameTarget(null)
      load()
    } catch (e) {
      message.error('重命名失败:' + (e as Error).message)
    } finally {
      setRenaming(false)
    }
  }

  return (
    <Card
      id="clipsync-devices-page"
      className="clipsync-page-card"
      title={`设备管理(${onlineCount} / ${devices.length} 在线)`}
      style={{ borderRadius: 14, height: 'calc(100vh - 112px)', display: 'flex', flexDirection: 'column' }}
      styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' } }}
      extra={
        <Space>
          <Select
            id="clipsync-devices-filter"
            value={filter}
            onChange={setFilter}
            style={{ width: 110 }}
            options={[
              { value: 'all', label: '全部设备' },
              { value: 'online', label: '在线' },
              { value: 'offline', label: '离线' },
            ]}
          />
          <Select
            id="clipsync-devices-user-filter"
            allowClear
            placeholder="全部用户"
            style={{ width: 140 }}
            value={userFilter ?? undefined}
            onChange={(v) => onUserFilterChange(v ?? null)}
            options={[...new Map(devices.filter(x => x.userId).map(x => [x.userId, { value: x.userId!, label: x.userId! }])).values()]}
          />
          <Input
            id="clipsync-devices-search"
            placeholder="搜索设备名 / IP / 平台"
            allowClear
            prefix={<Monitor size={16} color="#9CA3AF" />}
            style={{ width: 220 }}
            value={keyword}
            onChange={e => setKeyword(e.target.value)}
          />
          <Button id="clipsync-devices-refresh" icon={<RefreshCw size={16} />} onClick={load}>刷新</Button>
          <Button
            id="clipsync-devices-pair"
            type="primary"
            icon={<KeyRound size={16} />}
            loading={pairLoading}
            onClick={() => { setPairOpen(true); if (!pairCode) openPairing() }}
          >
            生成配对码
          </Button>
        </Space>
      }
    >
      <div ref={wrapRef} style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      {loading && devices.length === 0 ? (
        <Skeleton active paragraph={{ rows: 7 }} style={{ paddingTop: 8 }} />
      ) : (
      <Table<DeviceInfo>
        id="clipsync-devices-table"
        className="clipsync-table"
        rowKey="id"
        columns={columns}
        dataSource={filtered}
        loading={loading && devices.length > 0}
        scroll={{ y: scrollY }}
        pagination={{ pageSize: 10, showTotal: t => `共 ${t} 台设备` }}
        locale={{ emptyText: <Empty description="没有匹配的设备" /> }}
      />
      )}
      </div>

      <Modal
        rootClassName="clipsync-device-rename-modal"
        title="重命名设备"
        open={!!renameTarget}
        onOk={doRename}
        confirmLoading={renaming}
        onCancel={() => setRenameTarget(null)}
        okText="保存"
        cancelText="取消"
        styles={{ mask: { backdropFilter: 'blur(4px)', WebkitBackdropFilter: 'blur(4px)' } }}
      >
        <Form form={renameForm} layout="vertical">
          <Form.Item
            name="name"
            label="设备名称"
            rules={[{ required: true, message: '请输入设备名称' }, { max: 32, message: '最多 32 个字符' }]}
          >
            <Input id="clipsync-device-rename-input" placeholder="请输入新的设备名称" maxLength={32} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        rootClassName="clipsync-pairing-modal"
        title={<Space><KeyRound size={16} color="#2563EB" />生成配对码</Space>}
        open={pairOpen}
        onCancel={closePairing}
        footer={null}
        styles={{ mask: { backdropFilter: 'blur(4px)', WebkitBackdropFilter: 'blur(4px)' } }}
      >
        {pairCode ? (
          <div style={{ textAlign: 'center', padding: '8px 0 4px' }}>
            <div style={{ color: '#9CA3AF', fontSize: 13, marginBottom: 12 }}>
              一次性配对码 · 10 分钟内有效 · 新设备需同时输入 用户ID + 配对码
            </div>
            <div
              style={{
                fontFamily: 'Consolas, monospace', fontSize: 34, letterSpacing: 6, fontWeight: 700,
                color: expired ? '#EF4444' : '#2563EB', userSelect: 'all', marginBottom: 8,
              }}
            >
              {pairCode.code}
            </div>
            <div style={{ fontSize: 13, color: '#6B7280', marginBottom: 4 }}>
              用户ID: <Text strong style={{ fontFamily: 'Consolas, monospace' }}>{pairUserId ?? '-'}</Text>
            </div>
            <div style={{ fontSize: 13, color: expired ? '#EF4444' : '#6B7280', marginBottom: 16 }}>
              {expired ? '已过期' : '剩余有效时间: ' + countdown}
            </div>
            <Space>
              <Button
                icon={<Copy size={15} />}
                onClick={() => { navigator.clipboard.writeText(pairCode.code); message.success('已复制配对码') }}
              >
                复制
              </Button>
              <Button icon={<RotateCcw size={15} />} loading={pairLoading} onClick={openPairing}>
                重新生成
              </Button>
            </Space>
            <div style={{ color: '#9CA3AF', fontSize: 12, marginTop: 16 }}>
              在目标设备设置页输入此配对码完成配对;关闭窗口将作废未使用的码
            </div>
          </div>
        ) : (
          <div style={{ textAlign: 'center', padding: '24px 0' }}>
            <Button type="primary" loading={pairLoading} onClick={openPairing}>生成配对码</Button>
          </div>
        )}
      </Modal>
    </Card>
  )
}
