import { useEffect, useState } from 'react'
import { Card, Typography, Space, Button, Input, Table, Tag, Badge, message, Popconfirm, Empty, Row, Col, Skeleton } from 'antd'
import { KeyRound, RefreshCw, Check, X, LogOut, Pencil, Copy, Monitor, Clock, Trash2 } from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import {
  getUserId, createPairingCode, revokePairingCode, listPairingRequests, confirmPairingRequest,
  getDevices, removeDevice, getUser, renameUser, getActivities, deviceId,
  type DeviceInfo, type ActivityLog,
} from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

interface PendingReq {
  code: string; userId: string; deviceId: string | null; deviceName: string | null; status: string; createdAt: string;
}

export default function UserPage({ refreshTick, onLogout }: { refreshTick: number; onLogout: () => void }) {
  const uid = getUserId() ?? ''
  const [userName, setUserName] = useState('')
  const [editingName, setEditingName] = useState(false)
  const [nameInput, setNameInput] = useState('')

  // 配对码
  const [pairing, setPairing] = useState<{ code: string; expiresAt: number } | null>(null)
  const [pairLoading, setPairLoading] = useState(false)
  const [nowTs, setNowTs] = useState(Date.now())

  // 请求 / 设备 / 记录
  const [requests, setRequests] = useState<PendingReq[]>([])
  const [devices, setDevices] = useState<DeviceInfo[]>([])
  const [activities, setActivities] = useState<ActivityLog[]>([])
  const [firstLoading, setFirstLoading] = useState(true)

  const load = () => {
    setFirstLoading(true)
    if (uid) {
      getUser(uid).then(u => { setUserName(u.name); setNameInput(u.name) }).catch(() => {})
      listPairingRequests().then(setRequests).catch(() => {})
      getActivities(15).then(setActivities).catch(() => {})
    }
    getDevices().then(list => setDevices(list.filter(d => d.userId === uid))).catch(() => {})
    setTimeout(() => setFirstLoading(false), 400) // 首屏骨架至少展示 400ms,避免闪烁
  }
  useEffect(() => { load() }, [refreshTick, uid]) // eslint-disable-line

  useEffect(() => {
    if (!pairing) return
    const t = setInterval(() => setNowTs(Date.now()), 1000)
    return () => clearInterval(t)
  }, [pairing])

  const remaining = pairing ? Math.max(0, Math.floor((pairing.expiresAt - nowTs) / 1000)) : 0
  const countdown = String(Math.floor(remaining / 60)).padStart(2, '0') + ':' + String(remaining % 60).padStart(2, '0')

  const genCode = async () => {
    setPairLoading(true)
    try {
      if (pairing) revokePairingCode(pairing.code).catch(() => {})
      const r = await createPairingCode()
      setPairing({ code: r.code, expiresAt: Date.parse(r.expiresAt) })
      setNowTs(Date.now())
    } catch (e) {
      message.error('生成失败:' + (e as Error).message)
    } finally { setPairLoading(false) }
  }

  const doRename = async () => {
    const name = nameInput.trim()
    if (!name || name === userName) { setEditingName(false); return }
    try {
      await renameUser(uid, name)
      message.success('用户ID已修改')
      setUserName(name)
      setEditingName(false)
    } catch (e) { message.error((e as Error).message) }
  }

  const confirm = async (code: string, action: 'approve' | 'reject') => {
    try {
      await confirmPairingRequest(code, action)
      message.success(action === 'approve' ? '已确认,设备加入本组' : '已拒绝')
      load()
    } catch (e) { message.error((e as Error).message) }
  }

  const devCols: ColumnsType<DeviceInfo> = [
    { title: '设备', dataIndex: 'name', render: (v: string, r) => <Space><Monitor size={15} color="#2563EB" />{v} {r.online ? <Badge status="success" /> : null}</Space> },
    { title: '平台', dataIndex: 'platform', width: 100, render: (v: string) => <Tag>{v}</Tag> },
    { title: 'IP', dataIndex: 'ip', width: 150, render: (v?: string | null) => <Text type="secondary">{v ?? '-'}</Text> },
    { title: '最后活跃', dataIndex: 'lastSeenAt', width: 110, render: (v: string) => <Text type="secondary">{dayjs(v).fromNow()}</Text> },
    {
      title: '操作', key: 'action', width: 90,
      render: (_, r) => (
        <Popconfirm title={'移除设备「' + r.name + '」?'} okText="移除" okButtonProps={{ danger: true }} onConfirm={async () => { await removeDevice(r.id); message.success('已移除'); load() }}>
          <Button type="link" size="small" danger icon={<Trash2 size={15} />} />
        </Popconfirm>
      ),
    },
  ]

  if (firstLoading && !userName && requests.length === 0 && devices.length === 0) {
    return (
      <div id="clipsync-user-page" style={{ maxWidth: 980, margin: '0 auto' }}>
        <Card style={{ borderRadius: 14 }}>
          <Skeleton active paragraph={{ rows: 9 }} />
        </Card>
      </div>
    )
  }

  return (
    <div id="clipsync-user-page" style={{ maxWidth: 980, margin: '0 auto' }}>
      {/* 头部 */}
      <Card style={{ borderRadius: 14, marginBottom: 16 }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 12 }}>
          <Space size={12}>
            <Monitor size={22} color="#2563EB" />
            <div>
              <div style={{ fontSize: 17, fontWeight: 700 }}>我的设备组</div>
              <Space size={6} style={{ marginTop: 2 }}>
                <Text type="secondary">用户ID:</Text>
                {editingName ? (
                  <Space>
                    <Input size="small" value={nameInput} onChange={e => setNameInput(e.target.value)} maxLength={32} style={{ width: 160, fontFamily: 'Consolas, monospace' }} onPressEnter={doRename} />
                    <Button size="small" type="primary" onClick={doRename}>保存</Button>
                    <Button size="small" onClick={() => setEditingName(false)}>取消</Button>
                  </Space>
                ) : (
                  <Text strong style={{ fontFamily: 'Consolas, monospace', fontSize: 15 }}>{userName}</Text>
                )}
                {!editingName ? <Button type="text" size="small" icon={<Pencil size={13} />} onClick={() => setEditingName(true)}>修改</Button> : null}
              </Space>
            </div>
          </Space>
          <Space>
            <Button icon={<RefreshCw size={15} />} onClick={load}>刷新</Button>
            <Button icon={<LogOut size={15} />} onClick={onLogout}>退出</Button>
          </Space>
        </div>
      </Card>

      <Row gutter={[16, 16]}>
        {/* 生成配对码 */}
        <Col xs={24} lg={10}>
          <Card title={<Space><KeyRound size={16} color="#2563EB" />生成配对码</Space>} style={{ borderRadius: 14, height: '100%' }} extra={<Button size="small" icon={<RefreshCw size={13} />} loading={pairLoading} onClick={genCode}>生成</Button>}>
            {pairing ? (
              <div style={{ textAlign: 'center', padding: '6px 0' }}>
                <div style={{ color: '#9CA3AF', fontSize: 12, marginBottom: 10 }}>新设备输入以下 配对码 + 用户ID 发起配对,确认后加入本组</div>
                <div style={{ fontFamily: 'Consolas, monospace', fontSize: 30, fontWeight: 700, letterSpacing: 5, color: '#2563EB', userSelect: 'all' }}>{pairing.code}</div>
                <div style={{ fontSize: 13, color: '#6B7280', marginTop: 8 }}>用户ID: <Text strong style={{ fontFamily: 'Consolas, monospace' }}>{userName || uid}</Text></div>
                <div style={{ fontSize: 12, color: '#9CA3AF', marginTop: 6 }}>剩余有效时间: {countdown}
                  <Button type="link" size="small" icon={<Copy size={12} />} onClick={() => { navigator.clipboard.writeText(pairing.code + ' ' + (userName || uid)); message.success('已复制 配对码 + 用户ID') }}>复制</Button>
                </div>
              </div>
            ) : (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="点击右上角生成,新设备凭此加入你的设备组" />
            )}
          </Card>
        </Col>

        {/* 待确认请求 */}
        <Col xs={24} lg={14}>
          <Card title={<Space><Clock size={16} color="#2563EB" />待确认的配对请求</Space>} style={{ borderRadius: 14, height: '100%' }}>
            {requests.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无待确认请求" />
            ) : (
              <Space direction="vertical" style={{ width: '100%' }} size={8}>
                {requests.map(r => (
                  <div key={r.code} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10, padding: '10px 12px', background: '#F8FAFC', borderRadius: 10, border: '1px solid #E5E7EB' }}>
                    <Space>
                      <Monitor size={15} color="#6B7280" />
                      <div>
                        <div style={{ fontWeight: 600 }}>{r.deviceName ?? '未知设备'}</div>
                        <div style={{ fontSize: 12, color: '#9CA3AF' }}>{r.deviceId ?? ''} · {dayjs(r.createdAt).fromNow()}</div>
                      </div>
                    </Space>
                    <Space>
                      <Button size="small" type="primary" icon={<Check size={14} />} onClick={() => confirm(r.code, 'approve')}>确认</Button>
                      <Button size="small" danger icon={<X size={14} />} onClick={() => confirm(r.code, 'reject')}>拒绝</Button>
                    </Space>
                  </div>
                ))}
              </Space>
            )}
          </Card>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }}>
        {/* 我的设备 */}
        <Col xs={24} lg={14}>
          <Card title={<Space><Monitor size={16} color="#2563EB" />本组设备({devices.length})</Space>} style={{ borderRadius: 14, height: '100%' }} styles={{ body: { overflow: 'auto' } }}>
            <Table<DeviceInfo> rowKey="id" size="small" columns={devCols} dataSource={devices} pagination={false} locale={{ emptyText: <Empty description="本组暂无设备" /> }} />
          </Card>
        </Col>
        {/* 同步记录 */}
        <Col xs={24} lg={10}>
          <Card title={<Space><Clock size={16} color="#2563EB" />同步记录</Space>} style={{ borderRadius: 14, height: '100%' }} styles={{ body: { overflow: 'auto', maxHeight: 420 } }}>
            {activities.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无同步记录" /> : (
              <Space direction="vertical" size={10} style={{ width: '100%' }}>
                {activities.map(a => (
                  <div key={a.id}>
                    <div style={{ fontSize: 13 }}>
                      <Tag color={a.action === 'push' ? 'green' : a.action === 'connect' ? 'purple' : 'red'} style={{ marginRight: 6 }}>
                        {a.action === 'push' ? '推送' : a.action === 'connect' ? '连接' : '删除'}
                      </Tag>
                      <Text strong>{a.deviceName}</Text>
                    </div>
                    {a.content ? <div style={{ fontSize: 12, color: '#6B7280' }}>{a.content}</div> : null}
                    <div style={{ fontSize: 11, color: '#9CA3AF' }}>{dayjs(a.createdAt).fromNow()}</div>
                  </div>
                ))}
              </Space>
            )}
          </Card>
        </Col>
      </Row>
    </div>
  )
}
