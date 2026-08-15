import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Card, Table, Tag, Badge, Typography, Space, Button, Input, Modal, Form, Popconfirm,
  message, Select, Empty,
} from 'antd'
import { Monitor, Pencil, Trash2, RefreshCw } from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { getDevices, renameDevice, removeDevice, type DeviceInfo } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

export default function DevicesPage() {
  const [devices, setDevices] = useState<DeviceInfo[]>([])
  const [loading, setLoading] = useState(false)
  const [filter, setFilter] = useState<'all' | 'online' | 'offline'>('all')
  const [keyword, setKeyword] = useState('')

  // 重命名弹窗
  const [renameTarget, setRenameTarget] = useState<DeviceInfo | null>(null)
  const [renameForm] = Form.useForm()
  const [renaming, setRenaming] = useState(false)

  const load = () => {
    setLoading(true)
    getDevices().then(setDevices).finally(() => setLoading(false))
  }
  useEffect(() => { load() }, [])

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
    if (keyword.trim()) {
      const k = keyword.trim().toLowerCase()
      list = list.filter(d =>
        d.name.toLowerCase().includes(k) ||
        (d.ip ?? '').toLowerCase().includes(k) ||
        d.platform.toLowerCase().includes(k),
      )
    }
    return list
  }, [devices, filter, keyword])

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
            description="移除后该设备需重新注册"
            okText="移除"
            okButtonProps={{ danger: true }}
            onConfirm={async () => {
              await removeDevice(r.id)
              message.success('已移除设备')
              load()
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
            style={{ width: 120 }}
            options={[
              { value: 'all', label: '全部设备' },
              { value: 'online', label: '在线' },
              { value: 'offline', label: '离线' },
            ]}
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
        </Space>
      }
    >
      <div ref={wrapRef} style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <Table<DeviceInfo>
        id="clipsync-devices-table"
        className="clipsync-table"
        rowKey="id"
        columns={columns}
        dataSource={filtered}
        loading={loading}
        scroll={{ y: scrollY }}
        pagination={{ pageSize: 10, showTotal: t => `共 ${t} 台设备` }}
        locale={{ emptyText: <Empty description="没有匹配的设备" /> }}
      />
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
    </Card>
  )
}
