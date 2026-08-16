import { useEffect, useState } from 'react'
import { Card, Table, Tag, Typography, Space, Button, Input, Modal, Form, Popconfirm, message, Empty, Divider } from 'antd'
import { Users, Pencil, Trash2, RefreshCw, ShieldCheck, Monitor } from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import { listUsers, deleteUser, renameUser, getAudit } from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

interface UserRow { id: string; name: string; createdAt: string; deviceCount: number }
interface AuditRow { id: number; action: string; detail: string | null; ip: string | null; createdAt: string }

export default function UsersPage({ onViewUser }: { onViewUser: (uid: string) => void }) {
  const [users, setUsers] = useState<UserRow[]>([])
  const [audit, setAudit] = useState<AuditRow[]>([])
  const [loading, setLoading] = useState(false)
  const [renameTarget, setRenameTarget] = useState<UserRow | null>(null)
  const [renameForm] = Form.useForm()
  const [renaming, setRenaming] = useState(false)

  const load = () => {
    setLoading(true)
    Promise.all([listUsers(), getAudit(50)])
      .then(([u, a]) => { setUsers(u); setAudit(a) })
      .catch(() => {})
      .finally(() => setLoading(false))
  }
  useEffect(() => { load() }, [])

  const doRename = async () => {
    if (!renameTarget) return
    const values = await renameForm.validateFields()
    setRenaming(true)
    try {
      await renameUser(renameTarget.id, values.name.trim())
      message.success('用户ID已修改')
      setRenameTarget(null)
      load()
    } catch (e) { message.error((e as Error).message) }
    finally { setRenaming(false) }
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
          <Popconfirm title={'删除用户「' + r.name + '」?'} description="其设备将解绑,用户网页将无法登录" okText="删除" okButtonProps={{ danger: true }} onConfirm={async () => { await deleteUser(r.id); message.success('已删除'); load() }}>
            <Button type="link" size="small" danger icon={<Trash2 size={15} />} />
          </Popconfirm>
        </Space>
      ),
    },
  ]

  const auditCols: ColumnsType<AuditRow> = [
    { title: '时间', dataIndex: 'createdAt', width: 130, render: (v: string) => <Text type="secondary">{dayjs(v).format('MM-DD HH:mm:ss')}</Text> },
    { title: '事件', dataIndex: 'action', width: 120, render: (v: string) => <Tag>{v}</Tag> },
    { title: '详情', dataIndex: 'detail', render: (v: string | null) => v ?? '-' },
    { title: 'IP', dataIndex: 'ip', width: 150, render: (v: string | null) => <Text type="secondary">{v ?? '-'}</Text> },
  ]

  return (
    <div id="clipsync-users-page" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <Card
        id="clipsync-users-card"
        title={<Space><Users size={16} color="#2563EB" />用户管理</Space>}
        extra={<Button icon={<RefreshCw size={15} />} onClick={load}>刷新</Button>}
        style={{ borderRadius: 14 }}
      >
        <Table<UserRow> rowKey="id" columns={columns} dataSource={users} loading={loading} pagination={false} size="middle"
          locale={{ emptyText: <Empty description="暂无用户(设备完成首次配对后自动创建)" /> }} />
        <Divider style={{ margin: '14px 0' }} />
        <div style={{ color: '#9CA3AF', fontSize: 12 }}>
          用户ID = 设备组公共身份:首次配对自动创建短随机ID,组内可自行修改(全局唯一);删除用户后其设备解绑、用户网页无法再进入。
        </div>
      </Card>

      <Card id="clipsync-audit-card" title={<Space><ShieldCheck size={16} color="#10B981" />审计日志</Space>} style={{ borderRadius: 14 }}>
        <Table<AuditRow> rowKey="id" columns={auditCols} dataSource={audit} pagination={{ pageSize: 15, showTotal: t => '共 ' + t + ' 条' }} size="small" />
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
