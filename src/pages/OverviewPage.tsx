import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import {
  Row, Col, Card, Table, Tag, Typography, Button,
  Timeline, Empty, Space, Image as AntImage, message, Badge, Divider, Tooltip, theme, Select, Skeleton,
} from 'antd'
import { Bubble, Sender, Conversations } from '@ant-design/x'
import {
  Monitor, Users, ArrowUpDown, FileText, ShieldCheck, Signal, Send, Copy, Trash2, Share2,
  Download, Upload, Server, Cloud, Maximize2, Minimize2,
} from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import StatCard from '../components/StatCard'
import {
  getStats, getCurrentClipboard, getHistory, getDevices, getActivities,
  sendToDevices, imageUrl, type ClipboardEntry, type DeviceInfo,
  type ActivityLog, type Stats,
} from '../api'
import {
  getChatMsgs, addOutgoing, subscribeChatMsgs, type ChatMsg,
} from '../chatStore'

dayjs.extend(relativeTime)

const { Text } = Typography

function useStats() {
  const [stats, setStats] = useState<Stats | null>(null)
  const load = () => getStats().then(setStats).catch(e => message.error('统计数据加载失败:' + (e as Error).message))
  useEffect(() => { load() }, [])
  return { stats, reload: load }
}

function useClipboard() {
  const [current, setCurrent] = useState<ClipboardEntry | null>(null)
  const [history, setHistory] = useState<ClipboardEntry[]>([])
  const load = () => {
    getCurrentClipboard().then(setCurrent).catch(e => message.error('剪贴板数据加载失败:' + (e as Error).message))
    getHistory(0, 5).then(p => setHistory(p.items)).catch(() => {})
  }
  useEffect(() => { load() }, [])
  return { current, history, reload: load }
}

function useDevices() {
  const [devices, setDevices] = useState<DeviceInfo[]>([])
  useEffect(() => { getDevices().then(setDevices).catch(e => message.error('设备列表加载失败:' + (e as Error).message)) }, [])
  return devices
}

function useActivities() {
  const [activities, setActivities] = useState<ActivityLog[]>([])
  useEffect(() => { getActivities(8).then(setActivities).catch(() => {}) }, [])
  return activities
}

export default function OverviewPage({ refreshTick }: { refreshTick: number }) {
  const { token } = theme.useToken()
  const { stats, reload: reloadStats } = useStats()
  const { current, history, reload: reloadClipboard } = useClipboard()
  const devices = useDevices()
  const activities = useActivities()
  const [sendText, setSendText] = useState('')
  const [targets, setTargets] = useState<string[]>([])
  const [sending, setSending] = useState(false)
  const [chatFull, setChatFull] = useState(false)
  const [leaving, setLeaving] = useState(false)
  const [msgs, setMsgs] = useState<ChatMsg[]>(() => getChatMsgs())

  /** 退出全屏:先播离场动画(220ms),结束后再真正卸载 */
  const exitFull = () => {
    if (!chatFull) return
    setLeaving(true)
    setTimeout(() => { setChatFull(false); setLeaving(false) }, 220)
  }

  const toggleFull = () => {
    if (chatFull) exitFull()
    else { setLeaving(false); setChatFull(true) }
  }

  // 全屏态按 Esc 退出(带离场动画)
  useEffect(() => {
    if (!chatFull) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') exitFull() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [chatFull])

  useEffect(() => {
    const un = subscribeChatMsgs(() => setMsgs(getChatMsgs()))
    return un
  }, [])

  // 「最近剪贴板」表格填满卡片剩余高度,行不足时内部滚动,页脚固定卡片底部
  const wrapRef = useRef<HTMLDivElement>(null)
  const [recentScrollY, setRecentScrollY] = useState(240)

  useEffect(() => {
    const measure = () => {
      const el = wrapRef.current
      if (el) setRecentScrollY(Math.max(120, el.clientHeight - 64))
    }
    measure()
    const t = setTimeout(measure, 60)
    window.addEventListener('resize', measure)
    return () => { clearTimeout(t); window.removeEventListener('resize', measure) }
  }, [history.length])

  useEffect(() => {
    reloadStats(); reloadClipboard()
  }, [refreshTick]) // eslint-disable-line

  const onlineDevices = devices.filter(d => d.online)
  const offlineDevices = devices.filter(d => !d.online)

  const columns: ColumnsType<ClipboardEntry> = [
    {
      title: '内容',
      dataIndex: 'text',
      key: 'text',
      ellipsis: true,
      render: (_, r) => (
        <Space>
          {r.type === 'Image' ? <AntImage src={imageUrl(r.imageRef)} width={36} height={28} style={{ borderRadius: 6, objectFit: 'cover' }} /> : null}
          <Text style={{ maxWidth: 380 }} ellipsis={{ tooltip: r.text ?? '' }}>
            {r.type === 'Image' ? (r.text ?? '图片') : r.text}
          </Text>
        </Space>
      ),
    },
    {
      title: '来源设备',
      dataIndex: 'deviceName',
      key: 'deviceName',
      width: 150,
      render: (v: string) => <Tag color="blue" style={{ borderRadius: 999 }}>{v}</Tag>,
    },
    {
      title: '时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 110,
      render: (v: string) => <Text type="secondary">{dayjs(v).fromNow()}</Text>,
    },
    {
      title: '操作',
      key: 'action',
      width: 120,
      render: (_, r) => (
        <Space size={6}>
          <Tooltip title="推送">
            <Button type="text" size="small" icon={<Share2 size={16} color="#2563EB" />} />
          </Tooltip>
          <Tooltip title="复制">
            <Button
              type="text" size="small" icon={<Copy size={16} />}
              onClick={() => { if (r.text) navigator.clipboard.writeText(r.text); message.success('已复制') }}
            />
          </Tooltip>
          <Tooltip title="删除">
            <Button type="text" size="small" danger icon={<Trash2 size={16} />} />
          </Tooltip>
        </Space>
      ),
    },
  ]

  const handleSend = async (msg: string) => {
    const text = msg.trim()
    if (!text) { message.warning('请输入要发送的内容'); return }
    if (targets.length === 0) { message.warning('请选择目标设备'); return }
    setSending(true)
    try {
      await sendToDevices(text, targets)
      addOutgoing(text, targets)
      message.success('已发送到所选设备')
      setSendText('')
      reloadClipboard()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSending(false)
    }
  }

            const renderQuickSyncCard = (expanded: boolean) => (
            <Card
              id="clipsync-quick-sync"
            className="clipsync-panel-card"
            title={<Space><Send size={16} color="#2563EB" /> 快速同步 / 发送剪贴板</Space>}
            extra={
              <Tooltip title={expanded ? '退出全屏' : '全屏显示'}>
                <Button type="text" icon={expanded ? <Minimize2 size={16} /> : <Maximize2 size={16} />} onClick={toggleFull} />
              </Tooltip>
            }
            style={{ borderRadius: expanded ? 0 : 14, height: '100%', flex: 1, display: 'flex', flexDirection: 'column' }}
            styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' } }}
          >
            <div style={{ flex: 1, minHeight: 160, maxHeight: expanded ? undefined : 320, display: 'flex', flexDirection: 'column' }}>
              {msgs.length === 0 ? (
                <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="还没有同步消息,输入内容并选择目标设备" />
                </div>
              ) : (
                <Bubble.List
                  id="clipsync-quick-sync-chat"
                  className="clipsync-quick-sync-chat"
                  style={{ flex: 1, minHeight: 0 }}
                  autoScroll
                  items={msgs.map(m => ({
                    key: m.id,
                    role: m.direction === 'out' ? 'user' : 'ai',
                    placement: m.direction === 'out' ? 'end' : 'start',
                    avatar: m.direction === 'out'
                      ? <Cloud size={15} />
                      : <Monitor size={15} />,
                    content: m.content,
                    footer: m.direction === 'out'
                      ? '→ ' + (m.targets?.map(id => devices.find(d => d.id === id)?.name ?? id).join(' · ') || '全部设备') + ' · ' + dayjs(m.createdAt).fromNow()
                      : '来自 ' + (m.deviceName ?? '其他设备') + ' · ' + dayjs(m.createdAt).fromNow(),
                  }))}
                />
              )}
            </div>
            <Sender
              className="clipsync-quick-sync-sender"
              style={{ marginTop: 'auto' }}
              value={sendText}
              onChange={setSendText}
              onSubmit={handleSend}
              loading={sending}
              placeholder="输入要发送的内容,支持文本、链接等..."
              autoSize={{ minRows: 1, maxRows: 4 }}
              footer={
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                  <Select
                    id="clipsync-quick-sync-targets"
                    className="clipsync-quick-sync-targets"
                    mode="multiple"
                    value={targets}
                    onChange={setTargets}
                    placeholder="选择目标设备"
                    style={{ minWidth: 240, flex: 1 }}
                    maxTagCount="responsive"
                    options={devices.map(d => ({ value: d.id, label: d.name }))}
                  />
                  <a onClick={() => setTargets(onlineDevices.map(d => d.id))} style={{ color: token.colorPrimary, whiteSpace: 'nowrap', fontSize: 13 }}>
                    全选在线设备
                  </a>
                </div>
              }
            />
          </Card>
          )

  return (
    <div>
      {/* 统计卡片:Row/Col 栅格,与下方各行共用同一 gutter 体系,边缘对齐 */}
      <Row id="clipsync-overview-stats" className="clipsync-overview-stats" gutter={[16, 16]}>
        {stats === null ? (
          Array.from({ length: 5 }).map((_, i) => (
            <Col key={'sk' + i} flex="1 1 180px" style={{ minWidth: 180 }}>
              <Card style={{ borderRadius: 14, height: 132 }}>
                <Skeleton active paragraph={{ rows: 1 }} style={{ marginTop: 6 }} />
              </Card>
            </Col>
          ))
        ) : [
          <StatCard
            key="users"
            id="clipsync-stat-users"
            title="在线用户"
            value={stats ? `${stats.onlineUsers} / ${stats.totalUsers}` : '-'}
            valueColor="#2563EB"
            helper={stats ? `${stats.onlineUsers} 个用户在线` : ''}
            icon={<Users size={16} />}
            iconBg="rgba(37, 99, 235, 0.12)"
            iconColor="#2563EB"
            sparkline={stats?.sparklines.users}
            sparklineColor="#2563EB"
          />,
          <StatCard
            key="sync"
            id="clipsync-stat-sync"
            title="今日同步次数"
            value={stats?.todaySyncCount ?? '-'}
            helper={stats ? `较昨日 ↑ ${stats.syncTrend}%` : ''}
            helperColor="#10B981"
            icon={<ArrowUpDown size={16} />}
            iconBg="rgba(37, 99, 235, 0.12)"
            iconColor="#2563EB"
            sparkline={stats?.sparklines.sync}
            sparklineColor="#2563EB"
          />,
          <StatCard
            key="history"
            id="clipsync-stat-history"
            title="历史剪贴板"
            value={stats ? stats.totalClipboardCount.toLocaleString() : '-'}
            helper="总条数"
            icon={<FileText size={16} />}
            iconBg="rgba(147, 51, 234, 0.12)"
            iconColor="#9333EA"
            sparkline={stats?.sparklines.history}
            sparklineColor="#9333EA"
          />,
          <StatCard
            key="status"
            id="clipsync-stat-status"
            title="服务状态"
            value="运行中"
            valueColor="#10B981"
            helper={stats ? `已运行 ${stats.uptime}` : ''}
            icon={<ShieldCheck size={16} />}
            iconBg="rgba(16, 185, 129, 0.12)"
            iconColor="#10B981"
            // 平稳的运行时间趋势线:与其他卡片一致,底部图表区不再留白
            sparkline={[99, 99, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100]}
            sparklineColor="#10B981"
          />,
          <StatCard
            key="latency"
            id="clipsync-stat-latency"
            title="平均延迟"
            value={stats ? `${stats.avgLatencyMs} ms` : '-'}
            helper="网络延迟"
            icon={<Signal size={16} />}
            iconBg="rgba(249, 115, 22, 0.12)"
            iconColor="#F97316"
            sparkline={stats?.sparklines.latency}
            sparklineColor="#F97316"
          />,
        ].map(node => (
          <Col key={node.key} flex="1 1 180px" style={{ minWidth: 180 }}>
            {node}
          </Col>
        ))}
      </Row>

      {/* 最近剪贴板 + 已连接设备:卡片等高对齐 */}
      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} lg={15} style={{ display: 'flex' }}>
          <Card
            id="clipsync-recent-clipboard"
            className="clipsync-panel-card"
            title={<Space><FileText size={16} color="#2563EB" /> 最近剪贴板</Space>}
            extra={<a onClick={reloadClipboard} style={{ color: '#2563EB' }}>刷新</a>}
            style={{ borderRadius: 14, height: 360, flex: 1, display: 'flex', flexDirection: 'column' }}
            styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' } }}
          >
            <div ref={wrapRef} style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
            <Table<ClipboardEntry>
              id="clipsync-recent-clipboard-table"
              className="clipsync-table"
              rowKey="id"
              columns={columns}
              dataSource={history}
              pagination={false}
              size="middle"
              scroll={{ y: recentScrollY }}
              locale={{ emptyText: <Empty description="暂无剪贴板记录" /> }}
            />
            </div>
            <Divider style={{ margin: '8px 0' }} />
            <div style={{ color: token.colorTextTertiary, fontSize: 12 }}>最近 {history.length} 条记录</div>
          </Card>
        </Col>
        <Col xs={24} lg={9} style={{ display: 'flex' }}>
          <Card
            id="clipsync-devices-panel"
            className="clipsync-panel-card"
            title={<Space><Monitor size={16} color="#2563EB" /> 已连接设备</Space>}
            extra={<a style={{ color: '#2563EB' }}>管理设备</a>}
            style={{ borderRadius: 14, height: 360, flex: 1, display: 'flex', flexDirection: 'column' }}
            styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' } }}
          >
            <Conversations
              id="clipsync-devices-conversations"
              className="clipsync-devices-conversations"
              items={devices.map(d => ({
                key: d.id,
                icon: <Monitor size={15} color={d.online ? token.colorPrimary : token.colorTextTertiary} />,
                label: (
                  <div style={{ minWidth: 0 }}>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
                      <span style={{ fontWeight: 600 }}>{d.name}</span>
                      <Badge status={d.online ? 'success' : 'default'} text={d.online ? '在线' : '离线'} />
                    </div>
                    <div style={{ color: token.colorTextTertiary, fontSize: 12, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {d.ip} · {d.version} · {dayjs(d.lastSeenAt).fromNow()}
                    </div>
                  </div>
                ),
              }))}
              style={{ flex: 1, minHeight: 0, overflow: 'auto' }}
            />
            <Divider style={{ margin: '8px 0' }} />
            <div style={{ color: token.colorTextTertiary, fontSize: 12, textAlign: 'right' }}>
              在线 {onlineDevices.length} 台 · 离线 {offlineDevices.length} 台
            </div>
          </Card>
        </Col>
      </Row>

      {/* 快速同步 + 最近活动:卡片等高对齐 */}
      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} lg={15} style={{ display: 'flex' }}>
          {chatFull || leaving ? (
            <>
              <div style={{ flex: 1, visibility: 'hidden' }}>{renderQuickSyncCard(true)}</div>
              {createPortal(
                <div
                  className={'cs-chat-fullscreen' + (leaving ? ' cs-chat-fullscreen-leave' : '')}
                  style={{ position: 'fixed', inset: 0, zIndex: 1000, background: token.colorBgLayout, display: 'flex', flexDirection: 'column' }}
                >
                  {renderQuickSyncCard(true)}
                </div>,
                document.body,
              )}
            </>
          ) : (
            renderQuickSyncCard(false)
          )}
        </Col>
        <Col xs={24} lg={9} style={{ display: 'flex' }}>
          <Card
            id="clipsync-activities"
            className="clipsync-panel-card"
            title={<Space><Server size={16} color="#2563EB" /> 最近活动</Space>}
            extra={<a style={{ color: '#2563EB' }}>查看全部</a>}
            style={{ borderRadius: 14, height: '100%', flex: 1 }}
          >
            <Timeline
              className="clipsync-timeline"
              style={{ marginTop: 8, maxHeight: 320, overflow: 'auto' }}
              items={activities.map(a => ({
                color: a.action === 'push' ? '#10B981' : a.action === 'receive' ? '#2563EB' : a.action === 'connect' ? '#9333EA' : '#EF4444',
                children: (
                  <div>
                    <div>
                      <Text strong style={{ fontSize: 13 }}>
                        {a.action === 'push' ? <><Upload size={14} color="#10B981" /> {a.deviceName} 推送了剪贴板</> :
                         a.action === 'receive' ? <><Download size={14} color="#2563EB" /> {a.deviceName} 接收了剪贴板</> :
                         a.action === 'connect' ? <><Monitor size={14} color="#9333EA" /> {a.deviceName} 已连接</> :
                         <><Trash2 size={14} color="#EF4444" /> {a.deviceName} 删除了记录</>}
                      </Text>
                    </div>
                    {a.content ? (
                      <div style={{ color: token.colorTextSecondary, fontSize: 12, marginTop: 2, maxWidth: 300 }}>
                        <Text type="secondary" ellipsis style={{ fontSize: 12 }}>{a.content}</Text>
                      </div>
                    ) : null}
                    <div style={{ color: token.colorTextTertiary, fontSize: 12, marginTop: 2 }}>{dayjs(a.createdAt).fromNow()}</div>
                  </div>
                ),
              }))}
            />
          </Card>
        </Col>
      </Row>
    </div>
  )
}
