import { useEffect, useMemo, useState } from 'react'
import { Card, Table, Tag, Typography, Space, Button, Input, Modal, Form, Popconfirm, message, Empty, Divider, Select } from 'antd'
import { Users, Pencil, Trash2, RefreshCw, ShieldCheck, Monitor } from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { listUsers, deleteUser, renameUser, getAudit } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

interface UserRow { id: string; name: string; createdAt: string; deviceCount: number }
interface AuditRow { id: number; action: string; detail: string | null; ip: string | null; createdAt: string }

/** 审计事件 → 标签颜色(登录/锁定=红系,配对=蓝系,删除=橙系,其余默认) */
const AUDIT_COLORS: Record<string, string> = {
  login_ok: 'green',
  login_fail: 'red',
  login_locked: 'red',
  pair_code: 'blue',
  pair_request: 'geekblue',
  pair_approve: 'green',
  pair_reject: 'orange',
  user_rename: 'purple',
  user_delete: 'orange',
  history_clear: 'orange',
  settings_update: 'cyan',
}

export default function UsersPage({ onViewUser }: { onViewUser: (uid: string) => void }) {
  const [users, setUsers] = useState<UserRow[]>([])
  const [audit, setAudit] = useState<AuditRow[]>([])
  const [loading, setLoading] = useState(false)
  const [auditLoading, setAuditLoading] = useState(false)
  const [auditLimit, setAuditLimit] = useState(50)
  const [actionFilter, setActionFilter] = useState<string | null>(null)
  const [renameTarget, setRenameTarget] = useState<UserRow | null>(null)
  const [renameForm] = Form.useForm()
  const [renaming, setRenaming] = useState(false)

  const loadUsers = () => {
    setLoading(true)
    listUsers()
      .then(setUsers)
      .catch(e => message.error('用户列表加载失败:' + (e as Error).message))
      .finally(() => setLoading(false))
  }
  const loadAudit = (limit: number) => {
    setAuditLoading(true)
    getAudit(limit)
      .then(setAudit)
      .catch(e => message.error('审计日志加载失败:' + (e as Error).message))
      .finally(() => setAuditLoading(false))
  }
  useEffect(() => { loadUsers(); loadAudit(auditLimit) }, []) // eslint-disable-line

  const doRename = async () => {
    if (!renameTarget) return
    const values = await renameForm.validateFields()
    setRenaming(true)
    try {
      await renameUser(renameTarget.id, values.name.trim())
      message.success('用户ID已修改')
      setRenameTarget(null)
      loadUsers()
    } catch (e) { message.error((e as Error).message) }
    finally { setRenaming(false) }
  }

  const doDelete = async (r: UserRow) => {
    try {
      await deleteUser(r.id)
      message.success('已删除')
      loadUsers()
    } catch (e) {
      message.error('删除失败:' + (e as Error).message)
    }
  }

  const columns: ColumnsType<UserRow> = [
    { title: '用户ID', dataIndex: 'name', render: (v: string) => <Text strong style={{ fontFamily: 'Consolas, monospace' }}>{v}</Text> },
    { title: '内部ID', dataIndex: 'id', width: 140, render: (v: string) => <Text type="secondary" style={{ fontFamily: 'Consolas, monospace', fontSize: 12 }}>{v}</Text> },
    { title: '设备数', dataIndex: 'deviceCount', width: 90, render: (v: number) => <Tag color={v > 0 ? 'blue' : 'default'}>{v}</Tag> },
    { title: '创建时间', dataIndex: 'createdAt', width: 120, render: (v: string) => <Text type="secondary">{dayjs(v).fromNow()}</Text> },
    {
      title: '操作', key: 'action', width: 130,
      render: (_, r) => (
        <Space size={4}>
          <Button type="link" size="small" icon={<Monitor size={15} />} onClick={() => onViewUser(r.id)}>查看</Button>
          <Button type="link" size="small" icon={<Pencil size={15} />} onClick={() => { setRenameTarget(r); renameForm.setFieldsValue({ name: r.name }) }}>改名</Button>
          <Popconfirm title={'删除用户「' + r.name + '」?'} description="其设备将解绑,用户网页将无法登录" okText="删除" okButtonProps={{ danger: true }} onConfirm={() => doDelete(r)}>
            <Button type="link" size="small" danger icon={<Trash2 size={15} />} />
          </Popconfirm>
        </Space>
      ),
    },
  ]

  const auditActions = useMemo(() => [...new Set(audit.map(a => a.action))], [audit])
  const filteredAudit = useMemo(
    () => (actionFilter ? audit.filter(a => a.action === actionFilter) : audit),
    [audit, actionFilter],
  )
  const canLoadMore = audit.length >= auditLimit && auditLimit < 500

  const loadMoreAudit = () => {
    const next = Math.min(auditLimit * 2, 500)
    setAuditLimit(next)
    loadAudit(next)
  }

  const auditCols: ColumnsType<AuditRow> = [
    { title: '时间', dataIndex: 'createdAt', width: 130, render: (v: string) => <Text type="secondary">{dayjs(v).format('MM-DD HH:mm:ss')}</Text> },
    { title: '事件', dataIndex: 'action', width: 130, render: (v: string) => <Tag color={AUDIT_COLORS[v] ?? 'default'}>{v}</Tag> },
    { title: '详情', dataIndex: 'detail', render: (v: string | null) => v ?? '-' },
    { title: 'IP', dataIndex: 'ip', width: 150, render: (v: string | null) => <Text type="secondary">{v ?? '-'}</Text> },
  ]

  return (
    <div id="clipsync-users-page" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <Card
        id="clipsync-users-card"
        title={<Space><Users size={16} color="#2563EB" />用户管理</Space>}
        extra={<Button icon={<RefreshCw size={15} />} onClick={() => { loadUsers(); loadAudit(auditLimit) }}>刷新</Button>}
        style={{ borderRadius: 14 }}
      >
        <Table<UserRow> rowKey="id" columns={columns} dataSource={users} loading={loading} pagination={false} size="middle"
          locale={{ emptyText: <Empty description="暂无用户(设备完成首次配对后自动创建)" /> }} />
        <Divider style={{ margin: '14px 0' }} />
        <div style={{ color: '#9CA3AF', fontSize: 12 }}>
          用户ID = 设备组公共身份:首次配对自动创建短随机ID,组内可自行修改(全局唯一);删除用户后其设备解绑、用户网页无法再进入。
        </div>
      </Card>

      <Card
        id="clipsync-audit-card"
        title={<Space><ShieldCheck size={16} color="#10B981" />审计日志</Space>}
        extra={
          <Space>
            <Select
              allowClear
              placeholder="全部事件"
              style={{ width: 160 }}
              value={actionFilter ?? undefined}
              onChange={v => setActionFilter(v ?? null)}
              options={auditActions.map(a => ({ value: a, label: a }))}
            />
            {canLoadMore ? <Button size="small" loading={auditLoading} onClick={loadMoreAudit}>加载更多</Button> : null}
          </Space>
        }
        style={{ borderRadius: 14 }}
      >
        <Table<AuditRow> rowKey="id" columns={auditCols} dataSource={filteredAudit} loading={auditLoading} pagination={{ pageSize: 15, showTotal: t => '共 ' + t + ' 条' }} size="small" />
      </Card>

      <Modal
        rootClassName="clipsync-user-rename-modal"
        title="修改用户ID"
        open={!!renameTarget}
        onOk={doRename}
        confirmLoading={renaming}
        onCancel={() => setRenameTarget(null)}
        okText="保存"
        cancelText="取消"
        styles={{ mask: { backdropFilter: 'blur(4px)', WebkitBackdropFilter: 'blur(4px)' } }}
      >
        <Form form={renameForm} layout="vertical">
          <Form.Item name="name" label="新的用户ID" rules={[{ required: true, message: '请输入用户ID' }, { pattern: /^[A-Za-z0-9_-]+$/, message: '仅支持字母数字与 _-' }, { max: 32, message: '最多 32 个字符' }]}>
            <Input placeholder="全局唯一,修改后组内设备需用新ID配对" maxLength={32} style={{ fontFamily: 'Consolas, monospace' }} />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
