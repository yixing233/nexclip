import { useEffect, useState, useRef } from 'react'
import {
  Card, Typography, Space, Button, Input, Table, Tag, Badge, message, Popconfirm,
  Empty, Row, Col, Skeleton, Tabs, Image as AntImage, Upload, Tooltip, Dropdown, Modal, QRCode,
} from 'antd'
import {
  Home, Clock, Settings, KeyRound, RefreshCw, Check, X, LogOut, Pencil, Copy,
  Monitor, Trash2, Search, Send, Image as ImageIcon, Code2, Globe, ExternalLink,
  Smartphone, Laptop, UploadCloud, ChevronRight, Filter, ShieldCheck, QrCode as QrIcon, AlertTriangle,
} from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import {
  getUserId, createPairingCode, revokePairingCode,
  getDevices, removeDevice, getUser, renameUser, getActivities, deviceId,
  getHistory, deleteEntry, clearHistory, pushText, pushImage, getCurrentClipboard,
  imageUrl, type DeviceInfo, type ActivityLog, type ClipboardEntry,
} from '../api'

dayjs.extend(relativeTime)
const { Text, Title, Paragraph } = Typography
const { TextArea } = Input

export default function UserPage({ refreshTick, onLogout }: { refreshTick: number; onLogout: () => void }) {
  const uid = getUserId() ?? ''
  const [activeTab, setActiveTab] = useState<'home' | 'records' | 'settings'>('home')

  // 用户与设备数据
  const [userName, setUserName] = useState('')
  const [editingName, setEditingName] = useState(false)
  const [nameInput, setNameInput] = useState('')
  const [devices, setDevices] = useState<DeviceInfo[]>([])
  const [activities, setActivities] = useState<ActivityLog[]>([])

  // 配对码 (方案 1 扫码直连 + 方案 2 纯 6 位数字单向即入)
  const [pairing, setPairing] = useState<{ code: string; expiresAt: number; qrPayload?: string } | null>(null)
  const [pairLoading, setPairLoading] = useState(false)
  const [nowTs, setNowTs] = useState(Date.now())
  const prevDevCountRef = useRef(0)

  // 当前最新剪贴板
  const [currentClip, setCurrentClip] = useState<ClipboardEntry | null>(null)
  const [inputText, setInputText] = useState('')
  const [sendingText, setSendingText] = useState(false)

  // 历史记录列表
  const [historyItems, setHistoryItems] = useState<ClipboardEntry[]>([])
  const [historyTotal, setHistoryTotal] = useState(0)
  const [historyLoading, setHistoryLoading] = useState(false)
  const [historyFilter, setHistoryFilter] = useState<'all' | 'text' | 'code' | 'image' | 'link'>('all')
  const [searchQuery, setSearchQuery] = useState('')

  const [firstLoading, setFirstLoading] = useState(true)

  // 加载全量数据
  const loadAll = () => {
    if (uid) {
      getUser(uid)
        .then((u) => {
          if (u?.name) {
            setUserName(u.name)
            setNameInput(u.name)
          }
        })
        .catch((e) => console.warn('用户信息加载失败:', e))

      getActivities(15).then((a) => setActivities(a || [])).catch(() => {})
    }

    getDevices()
      .then((list) => setDevices((list || []).filter((d) => d.userId === uid)))
      .catch((e) => console.warn('设备列表加载失败:', e))

    getCurrentClipboard()
      .then((clip) => setCurrentClip(clip))
      .catch(() => {})

    loadHistory()
  }

  // 加载历史记录
  const loadHistory = (query = searchQuery) => {
    setHistoryLoading(true)
    getHistory(0, 50, uid, query)
      .then((page) => {
        setHistoryItems(page.items || [])
        setHistoryTotal(page.total || 0)
      })
      .catch((e) => console.warn('历史记录加载失败:', e))
      .finally(() => {
        setHistoryLoading(false)
        setTimeout(() => setFirstLoading(false), 300)
      })
  }

  useEffect(() => {
    loadAll()
  }, [refreshTick, uid]) // eslint-disable-line

  // 配对倒计时
  useEffect(() => {
    if (!pairing) return
    const t = setInterval(() => setNowTs(Date.now()), 1000)
    return () => clearInterval(t)
  }, [pairing])

  const remaining = pairing ? Math.max(0, Math.floor((pairing.expiresAt - nowTs) / 1000)) : 0
  const countdown =
    String(Math.floor(remaining / 60)).padStart(2, '0') +
    ':' +
    String(remaining % 60).padStart(2, '0')

  // 关闭配对弹窗: 立即作废配对码(与安卓端一致,关闭即失效)
  const closePairingModal = () => {
    if (pairing) {
      revokePairingCode(pairing.code).catch(() => {})
      setPairing(null)
    }
  }

  // 组件卸载时作废未使用的配对码
  useEffect(() => {
    return () => {
      if (pairing) revokePairingCode(pairing.code).catch(() => {})
    }
  }, [pairing])

  // 监听新设备加入: 弹窗开启期间一旦检测到新设备连入, 自动提示成功并平滑关闭弹窗
  useEffect(() => {
    if (pairing && devices.length > prevDevCountRef.current && prevDevCountRef.current > 0) {
      message.success('新设备已成功扫码/验证接入！')
      setPairing(null)
    }
    prevDevCountRef.current = devices.length
  }, [devices, pairing])

  // 生成 6 位纯数字配对码与扫码直连 URL
  const genCode = async () => {
    setPairLoading(true)
    try {
      if (pairing) revokePairingCode(pairing.code).catch(() => {})
      const r = await createPairingCode()
      prevDevCountRef.current = devices.length
      setPairing({
        code: r.code,
        expiresAt: Date.parse(r.expiresAt),
        qrPayload: r.qrPayload || `${window.location.origin}/index?pairCode=${r.code}`,
      })
      setNowTs(Date.now())
    } catch (e) {
      message.error('生成失败:' + (e as Error).message)
    } finally {
      setPairLoading(false)
    }
  }

  // 修改用户ID
  const doRename = async () => {
    const name = nameInput.trim()
    if (!name || name === userName) {
      setEditingName(false)
      return
    }
    try {
      await renameUser(uid, name)
      message.success('用户ID已修改')
      setUserName(name)
      setEditingName(false)
    } catch (e) {
      message.error((e as Error).message)
    }
  }



  // 移除设备
  const removeDev = async (id: string, name: string) => {
    try {
      await removeDevice(id)
      message.success('已移除「' + name + '」')
      loadAll()
    } catch (e) {
      message.error('移除失败:' + (e as Error).message)
    }
  }

  // 发送新文本剪贴板
  const handlePushText = async () => {
    const text = inputText.trim()
    if (!text) {
      message.warning('请输入要发送的文本')
      return
    }
    setSendingText(true)
    try {
      await pushText(text, deviceId(), 'Web 控制台')
      message.success('已推送至所有在线设备！')
      setInputText('')
      loadAll()
    } catch (e) {
      message.error('推送失败:' + (e as Error).message)
    } finally {
      setSendingText(false)
    }
  }

  // 发送图片
  const handlePushImage = async (file: File) => {
    try {
      message.loading({ content: '正在上传并广播图片...', key: 'upload' })
      await pushImage(file, deviceId(), 'Web 控制台')
      message.success({ content: '图片已同步至所有在线设备！', key: 'upload' })
      loadAll()
    } catch (e) {
      message.error({ content: '图片上传失败:' + (e as Error).message, key: 'upload' })
    }
  }

  // 删除单条历史
  const handleDeleteEntry = async (id: number) => {
    try {
      await deleteEntry(id)
      message.success('已删除记录')
      loadHistory()
    } catch (e) {
      message.error('删除失败:' + (e as Error).message)
    }
  }

  // 清空历史
  const handleClearHistory = async () => {
    try {
      await clearHistory()
      message.success('已清空剪贴板历史')
      loadHistory()
    } catch (e) {
      message.error('清空失败:' + (e as Error).message)
    }
  }

  // 复制内容到剪贴板(深度适配:图片真实位图写入 + 文本写入)
  const handleCopyEntry = async (item?: ClipboardEntry | null) => {
    if (!item) return
    if (item.type === 'Image' && item.imageRef) {
      const hide = message.loading('正在复制图片到剪贴板...', 0)
      try {
        const fullUrl = imageUrl(item.imageRef)
        const res = await fetch(fullUrl)
        const blob = await res.blob()
        let pngBlob = blob
        if (!blob.type.includes('png')) {
          const img = document.createElement('img')
          img.crossOrigin = 'anonymous'
          img.src = fullUrl
          await new Promise((r) => {
            img.onload = r
          })
          const canvas = document.createElement('canvas')
          canvas.width = img.naturalWidth || img.width
          canvas.height = img.naturalHeight || img.height
          const ctx = canvas.getContext('2d')
          ctx?.drawImage(img, 0, 0)
          pngBlob = await new Promise<Blob>((r) => canvas.toBlob((b) => r(b!), 'image/png'))
        }
        await navigator.clipboard.write([
          new ClipboardItem({ 'image/png': pngBlob }),
        ])
        hide()
        message.success('图片已成功复制到系统剪贴板！')
      } catch (err) {
        hide()
        try {
          await navigator.clipboard.writeText(window.location.origin + imageUrl(item.imageRef))
          message.info('已复制图片直链（可直接粘贴访问）')
        } catch {
          message.error('复制失败，请右键图片复制或另存为')
        }
      }
      return
    }

    if (item.text) {
      navigator.clipboard.writeText(item.text)
      message.success('已复制到系统剪贴板！')
    }
  }

  const handleCopy = (text?: string | null) => {
    if (!text) return
    navigator.clipboard.writeText(text)
    message.success('已复制到系统剪贴板！')
  }

  // 识别文本类型
  const detectType = (item: ClipboardEntry) => {
    if (item.type === 'Image' || item.imageRef) return 'image'
    const str = item.text || ''
    if (str.startsWith('http://') || str.startsWith('https://')) return 'link'
    if (
      str.includes('const ') ||
      str.includes('function ') ||
      str.includes('import ') ||
      str.includes('def ') ||
      str.includes('class ') ||
      str.includes('npm ') ||
      str.includes('{') && str.includes('}')
    ) {
      return 'code'
    }
    return 'text'
  }

  // 过滤后的记录
  const filteredHistory = historyItems.filter((item) => {
    const t = detectType(item)
    if (historyFilter === 'all') return true
    return t === historyFilter
  })

  // 设备表格列定义
  const devCols: ColumnsType<DeviceInfo> = [
    {
      title: '设备',
      dataIndex: 'name',
      render: (v: string, r) => (
        <Space>
          {r.platform?.toLowerCase().includes('android') ? (
            <Smartphone size={16} color="#3DDC84" />
          ) : r.platform?.toLowerCase().includes('win') ? (
            <Laptop size={16} color="#00A4EF" />
          ) : (
            <Monitor size={16} color="#2563EB" />
          )}
          <Text strong>{v}</Text>
          {r.online ? <Badge status="success" text={<Text style={{ fontSize: 11, color: '#10B981' }}>在线</Text>} /> : null}
        </Space>
      ),
    },
    { title: '平台', dataIndex: 'platform', width: 110, render: (v: string) => <Tag>{v || 'Unknown'}</Tag> },
    { title: 'IP 地址', dataIndex: 'ip', width: 140, render: (v?: string | null) => <Text type="secondary" style={{ fontSize: 12 }}>{v ?? '-'}</Text> },
    { title: '最后活跃', dataIndex: 'lastSeenAt', width: 120, render: (v: string) => <Text type="secondary" style={{ fontSize: 12 }}>{dayjs(v).fromNow()}</Text> },
    {
      title: '操作',
      key: 'action',
      width: 80,
      render: (_, r) => (
        <Popconfirm
          title={'确定移除设备「' + r.name + '」?'}
          okText="移除"
          okButtonProps={{ danger: true }}
          onConfirm={() => removeDev(r.id, r.name)}
        >
          <Button type="text" size="small" danger icon={<Trash2 size={14} />} />
        </Popconfirm>
      ),
    },
  ]

  if (firstLoading && !userName && devices.length === 0) {
    return (
      <div style={{ maxWidth: 1040, margin: '0 auto', padding: '16px 8px' }}>
        <Card style={{ borderRadius: 16 }}>
          <Skeleton active paragraph={{ rows: 8 }} />
        </Card>
      </div>
    )
  }

  return (
    <div style={{ maxWidth: 1040, margin: '0 auto', paddingBottom: 40 }}>
      {/* ==================== 顶部全局导航栏 ==================== */}
      <Card
        style={{
          borderRadius: 16,
          marginBottom: 16,
          boxShadow: '0 4px 16px rgba(0, 0, 0, 0.04)',
        }}
        styles={{ body: { padding: '14px 20px' } }}
      >
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 12 }}>
          {/* 左侧用户身份 */}
          <Space size={12}>
            <div
              style={{
                width: 40,
                height: 40,
                borderRadius: 10,
                background: 'rgba(37, 99, 235, 0.10)',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                color: '#2563EB',
              }}
            >
              <Monitor size={20} />
            </div>
            <div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ fontSize: 16, fontWeight: 700 }}>NexClip 控制台</span>
                <Tag color="blue" style={{ borderRadius: 999, margin: 0, fontSize: 11 }}>
                  {devices.filter((d) => d.online).length} 台设备在线
                </Tag>
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2 }}>
                <Text type="secondary" style={{ fontSize: 12 }}>用户ID:</Text>
                {editingName ? (
                  <Space size={4}>
                    <Input
                      size="small"
                      value={nameInput}
                      onChange={(e) => setNameInput(e.target.value)}
                      maxLength={32}
                      style={{ width: 140, fontFamily: 'Consolas, monospace', fontSize: 12 }}
                      onPressEnter={doRename}
                    />
                    <Button size="small" type="primary" onClick={doRename}>保存</Button>
                    <Button size="small" onClick={() => setEditingName(false)}>取消</Button>
                  </Space>
                ) : (
                  <Space size={4}>
                    <Text strong style={{ fontFamily: 'Consolas, monospace', fontSize: 13 }}>
                      {userName || uid}
                    </Text>
                    <Tooltip title="修改用户ID">
                      <Button type="text" size="small" icon={<Pencil size={12} />} onClick={() => setEditingName(true)} />
                    </Tooltip>
                  </Space>
                )}
              </div>
            </div>
          </Space>

          {/* 中间 Tab 切换胶囊组 (对齐安卓三大板块) */}
          <div style={{ display: 'flex', gap: 6, background: 'rgba(0, 0, 0, 0.04)', padding: 4, borderRadius: 999 }}>
            <Button
              type={activeTab === 'home' ? 'primary' : 'text'}
              shape="round"
              size="middle"
              icon={<Home size={15} />}
              onClick={() => setActiveTab('home')}
              style={{ fontWeight: 600, fontSize: 13, height: 34 }}
            >
              总览
            </Button>
            <Button
              type={activeTab === 'records' ? 'primary' : 'text'}
              shape="round"
              size="middle"
              icon={<Clock size={15} />}
              onClick={() => setActiveTab('records')}
              style={{ fontWeight: 600, fontSize: 13, height: 34 }}
            >
              记录 ({historyTotal})
            </Button>
            <Button
              type={activeTab === 'settings' ? 'primary' : 'text'}
              shape="round"
              size="middle"
              icon={<Settings size={15} />}
              onClick={() => setActiveTab('settings')}
              style={{ fontWeight: 600, fontSize: 13, height: 34 }}
            >
              设置 & 设备
            </Button>
          </div>

          {/* 右侧全局操作 */}
          <Space size={8}>
            <Tooltip title="刷新数据">
              <Button icon={<RefreshCw size={15} />} onClick={loadAll} shape="circle" />
            </Tooltip>
            <Popconfirm title="确定退出当前控制台?" okText="退出" onConfirm={onLogout}>
              <Button icon={<LogOut size={15} />} danger shape="circle" />
            </Popconfirm>
          </Space>
        </div>
      </Card>

      {/* ==================== 1. 【总览】板块 (Home) ==================== */}
      {activeTab === 'home' && (
        <div>
          <Row gutter={[16, 16]}>
            {/* 当前剪贴板卡片 */}
            <Col xs={24} lg={15}>
              <Card
                title={
                  <Space>
                    <Clock size={16} color="#2563EB" />
                    <span>当前剪贴板</span>
                    {currentClip ? (
                      <Tag color="green" style={{ borderRadius: 999, fontSize: 11, margin: 0 }}>
                        {currentClip.type === 'Image' ? '高清图片' : '文本内容'}
                      </Tag>
                    ) : null}
                  </Space>
                }
                extra={
                  currentClip ? (
                    <Button
                      type="primary"
                      size="small"
                      icon={<Copy size={13} />}
                      onClick={() => handleCopyEntry(currentClip)}
                    >
                      复制
                    </Button>
                  ) : null
                }
                style={{ borderRadius: 16, height: '100%' }}
              >
                {currentClip ? (
                  <div>
                    {currentClip.type === 'Image' && currentClip.imageRef ? (
                      <div style={{ textAlign: 'center', padding: '12px 0' }}>
                        <AntImage
                          src={imageUrl(currentClip.imageRef)}
                          style={{ maxHeight: 220, borderRadius: 10, objectFit: 'contain' }}
                        />
                        <div style={{ fontSize: 12, color: '#9CA3AF', marginTop: 8 }}>
                          来源: {currentClip.deviceName || '远端设备'} · {dayjs(currentClip.createdAt).fromNow()}
                        </div>
                      </div>
                    ) : (
                      <div>
                        <div
                          style={{
                            padding: '14px 16px',
                            background: 'rgba(0, 0, 0, 0.03)',
                            borderRadius: 10,
                            fontFamily: detectType(currentClip) === 'code' ? 'Consolas, monospace' : 'inherit',
                            fontSize: 14,
                            lineHeight: 1.6,
                            maxHeight: 200,
                            overflowY: 'auto',
                            wordBreak: 'break-word',
                            whiteSpace: 'pre-wrap',
                          }}
                        >
                          {currentClip.text}
                        </div>
                        <div style={{ fontSize: 12, color: '#9CA3AF', marginTop: 10, display: 'flex', justifyContent: 'space-between' }}>
                          <span>来源: {currentClip.deviceName || '远端设备'}</span>
                          <span>{dayjs(currentClip.createdAt).fromNow()}</span>
                        </div>
                      </div>
                    )}
                  </div>
                ) : (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无活动剪贴板内容" />
                )}
              </Card>
            </Col>

            {/* 快速推送发送栏 */}
            <Col xs={24} lg={9}>
              <Card
                title={
                  <Space>
                    <Send size={16} color="#2563EB" />
                    <span>快速推送至所有设备</span>
                  </Space>
                }
                style={{ borderRadius: 16, height: '100%' }}
              >
                <div
                  onDragOver={(e) => e.preventDefault()}
                  onDrop={(e) => {
                    e.preventDefault()
                    const files = e.dataTransfer.files
                    if (files && files.length > 0) {
                      for (let i = 0; i < files.length; i++) {
                        if (files[i].type.startsWith('image/')) {
                          handlePushImage(files[i])
                          return
                        }
                      }
                    }
                  }}
                >
                  <TextArea
                    rows={4}
                    placeholder="在此输入文本发送，或直接按 Ctrl+V 粘贴截图/拖拽图片直接同步..."
                    value={inputText}
                    onChange={(e) => setInputText(e.target.value)}
                    onPaste={(e) => {
                      const items = e.clipboardData?.items
                      if (items) {
                        for (let i = 0; i < items.length; i++) {
                          if (items[i].type.startsWith('image/')) {
                            const file = items[i].getAsFile()
                            if (file) {
                              e.preventDefault()
                              handlePushImage(file)
                              return
                            }
                          }
                        }
                      }
                    }}
                    style={{ borderRadius: 10, marginBottom: 12, resize: 'none' }}
                  />
                </div>
                <div style={{ display: 'flex', gap: 10 }}>
                  <Button
                    type="primary"
                    block
                    icon={<Send size={15} />}
                    loading={sendingText}
                    onClick={handlePushText}
                    style={{ borderRadius: 8 }}
                  >
                    发送文本
                  </Button>
                  <Upload
                    showUploadList={false}
                    accept="image/*"
                    beforeUpload={(file) => {
                      handlePushImage(file)
                      return false
                    }}
                  >
                    <Button icon={<ImageIcon size={15} />} style={{ borderRadius: 8 }}>
                      发送图片
                    </Button>
                  </Upload>
                </div>
              </Card>
            </Col>
          </Row>

          {/* 下方最近记录与设备状态概览 */}
          <Row gutter={[16, 16]} style={{ marginTop: 16 }}>
            {/* 最近同步历史 */}
            <Col xs={24} lg={15}>
              <Card
                title={
                  <Space>
                    <Clock size={16} color="#2563EB" />
                    <span>最近剪贴板流</span>
                  </Space>
                }
                extra={
                  <Button type="link" size="small" onClick={() => setActiveTab('records')}>
                    查看全部记录 <ChevronRight size={14} />
                  </Button>
                }
                style={{ borderRadius: 16 }}
              >
                {historyItems.slice(0, 5).length === 0 ? (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无历史记录" />
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                    {historyItems.slice(0, 5).map((item) => {
                      const type = detectType(item)
                      return (
                        <div
                          key={item.id}
                          style={{
                            padding: '10px 14px',
                            background: 'rgba(0, 0, 0, 0.02)',
                            borderRadius: 10,
                            display: 'flex',
                            alignItems: 'center',
                            justifyContent: 'space-between',
                            gap: 12,
                          }}
                        >
                          <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0, flex: 1 }}>
                            {type === 'image' ? (
                              <AntImage
                                src={imageUrl(item.imageRef)}
                                width={36}
                                height={36}
                                style={{ borderRadius: 6, objectFit: 'cover' }}
                              />
                            ) : type === 'code' ? (
                              <Code2 size={18} color="#38BDF8" />
                            ) : type === 'link' ? (
                              <Globe size={18} color="#6366F1" />
                            ) : (
                              <Monitor size={18} color="#2563EB" />
                            )}
                            <div style={{ minWidth: 0, flex: 1 }}>
                              <div
                                style={{
                                  fontSize: 13,
                                  fontWeight: 500,
                                  overflow: 'hidden',
                                  textOverflow: 'ellipsis',
                                  whiteSpace: 'nowrap',
                                  fontFamily: type === 'code' ? 'Consolas, monospace' : 'inherit',
                                }}
                              >
                                {item.type === 'Image' ? '高清图片快照' : item.text}
                              </div>
                              <div style={{ fontSize: 11, color: '#9CA3AF' }}>
                                {item.deviceName || '远端设备'} · {dayjs(item.createdAt).fromNow()}
                              </div>
                            </div>
                          </div>
                          <Button
                            type="text"
                            size="small"
                            icon={<Copy size={14} />}
                            onClick={() => handleCopyEntry(item)}
                          />
                        </div>
                      )
                    })}
                  </div>
                )}
              </Card>
            </Col>

            {/* 设备状态概况 */}
            <Col xs={24} lg={9}>
              <Card
                title={
                  <Space>
                    <ShieldCheck size={16} color="#2563EB" />
                    <span>本组设备 ({devices.length})</span>
                  </Space>
                }
                extra={
                  <Button type="link" size="small" onClick={() => setActiveTab('settings')}>
                    管理设备 <ChevronRight size={14} />
                  </Button>
                }
                style={{ borderRadius: 16 }}
              >
                {devices.length === 0 ? (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无已配对设备" />
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                    {devices.map((dev) => (
                      <div
                        key={dev.id}
                        style={{
                          padding: '8px 12px',
                          borderRadius: 8,
                          background: 'rgba(0, 0, 0, 0.02)',
                          display: 'flex',
                          alignItems: 'center',
                          justifyContent: 'space-between',
                        }}
                      >
                        <Space size={8}>
                          {dev.platform?.toLowerCase().includes('android') ? (
                            <Smartphone size={15} color="#3DDC84" />
                          ) : (
                            <Laptop size={15} color="#00A4EF" />
                          )}
                          <Text strong style={{ fontSize: 13 }}>{dev.name}</Text>
                        </Space>
                        <Tag color={dev.online ? 'green' : 'default'} style={{ borderRadius: 999, fontSize: 11, margin: 0 }}>
                          {dev.online ? '在线' : '离线'}
                        </Tag>
                      </div>
                    ))}
                  </div>
                )}
              </Card>
            </Col>
          </Row>
        </div>
      )}

      {/* ==================== 2. 【记录】板块 (Records / History) ==================== */}
      {activeTab === 'records' && (
        <div>
          {/* 搜索与类型过滤工具栏 */}
          <Card style={{ borderRadius: 16, marginBottom: 16 }} styles={{ body: { padding: '16px 20px' } }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 14 }}>
              {/* 搜索框 */}
              <Input
                placeholder="搜索剪贴板文本、代码或来源设备..."
                prefix={<Search size={15} color="#9CA3AF" />}
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                onPressEnter={() => loadHistory(searchQuery)}
                allowClear
                style={{ width: 280, borderRadius: 999 }}
              />

              {/* 分类过滤胶囊 (对齐安卓端: 全部、文本、代码、图片、链接) */}
              <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                {(
                  [
                    { key: 'all', label: '全部' },
                    { key: 'text', label: '文本' },
                    { key: 'code', label: '代码' },
                    { key: 'image', label: '图片' },
                    { key: 'link', label: '链接' },
                  ] as const
                ).map((tab) => (
                  <Button
                    key={tab.key}
                    type={historyFilter === tab.key ? 'primary' : 'default'}
                    shape="round"
                    size="small"
                    onClick={() => setHistoryFilter(tab.key)}
                    style={{ fontWeight: 500, fontSize: 12, height: 28 }}
                  >
                    {tab.label}
                  </Button>
                ))}
              </div>

              {/* 操作按钮 */}
              <Space>
                <Button icon={<RefreshCw size={14} />} onClick={() => loadHistory(searchQuery)}>
                  刷新
                </Button>
                <Popconfirm
                  title="确定清空所有剪贴板历史记录?"
                  description="此操作不可恢复，请谨慎操作。"
                  okText="清空"
                  okButtonProps={{ danger: true }}
                  onConfirm={handleClearHistory}
                >
                  <Button danger icon={<Trash2 size={14} />}>
                    清空历史
                  </Button>
                </Popconfirm>
              </Space>
            </div>
          </Card>

          {/* 历史记录卡片流 */}
          {historyLoading ? (
            <Card style={{ borderRadius: 16 }}>
              <Skeleton active paragraph={{ rows: 6 }} />
            </Card>
          ) : filteredHistory.length === 0 ? (
            <Card style={{ borderRadius: 16 }}>
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={searchQuery ? '未找到符合条件的剪贴板记录' : '暂无剪贴板历史记录'}
              />
            </Card>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {filteredHistory.map((item) => {
                const type = detectType(item)
                return (
                  <Card
                    key={item.id}
                    hoverable
                    style={{
                      borderRadius: 14,
                      transition: 'all 0.2s ease',
                    }}
                    styles={{ body: { padding: '16px 20px' } }}
                  >
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 14 }}>
                      {/* 左侧内容区 */}
                      <div style={{ flex: 1, minWidth: 0 }}>
                        {/* 顶栏元数据 */}
                        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                          <Tag
                            color={
                              type === 'image'
                                ? 'green'
                                : type === 'code'
                                ? 'blue'
                                : type === 'link'
                                ? 'purple'
                                : 'default'
                            }
                            style={{ borderRadius: 6, margin: 0, fontSize: 11 }}
                          >
                            {type === 'image'
                              ? '高清图片'
                              : type === 'code'
                              ? '代码片段'
                              : type === 'link'
                              ? '网页链接'
                              : '纯文本'}
                          </Tag>
                          <Text type="secondary" style={{ fontSize: 12 }}>
                            {item.deviceName || '远端设备'}
                          </Text>
                          <Text type="secondary" style={{ fontSize: 11, opacity: 0.7 }}>
                            · {dayjs(item.createdAt).format('YYYY-MM-DD HH:mm:ss')} ({dayjs(item.createdAt).fromNow()})
                          </Text>
                        </div>

                        {/* 内容渲染 */}
                        {type === 'image' && item.imageRef ? (
                          <div>
                            <AntImage
                              src={imageUrl(item.imageRef)}
                              style={{ maxHeight: 200, maxWidth: 360, borderRadius: 8, objectFit: 'contain' }}
                            />
                          </div>
                        ) : type === 'code' ? (
                          <div
                            style={{
                              padding: '12px 14px',
                              background: 'rgba(0, 0, 0, 0.04)',
                              borderRadius: 8,
                              fontFamily: 'Consolas, Monaco, monospace',
                              fontSize: 13,
                              lineHeight: 1.5,
                              wordBreak: 'break-word',
                              whiteSpace: 'pre-wrap',
                              maxHeight: 240,
                              overflowY: 'auto',
                            }}
                          >
                            {item.text}
                          </div>
                        ) : type === 'link' ? (
                          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                            <a
                              href={item.text ?? '#'}
                              target="_blank"
                              rel="noreferrer"
                              style={{ fontSize: 14, wordBreak: 'break-all', color: '#2563EB' }}
                            >
                              {item.text}
                            </a>
                            <ExternalLink size={14} color="#2563EB" />
                          </div>
                        ) : (
                          <Paragraph
                            ellipsis={{ rows: 4, expandable: true, symbol: '展开全文' }}
                            style={{ margin: 0, fontSize: 14, lineHeight: 1.6, wordBreak: 'break-word', whiteSpace: 'pre-wrap' }}
                          >
                            {item.text}
                          </Paragraph>
                        )}
                      </div>

                      {/* 右侧快捷操作按钮 */}
                      <Space size={4} style={{ flexShrink: 0 }}>
                        <Tooltip title={item.type === 'Image' ? '复制图片到剪贴板' : '复制文本到剪贴板'}>
                          <Button
                            type="primary"
                            size="middle"
                            icon={<Copy size={15} />}
                            onClick={() => handleCopyEntry(item)}
                            style={{ borderRadius: 8 }}
                          >
                            复制
                          </Button>
                        </Tooltip>
                        <Popconfirm
                          title="确定删除此条记录?"
                          okText="删除"
                          okButtonProps={{ danger: true }}
                          onConfirm={() => handleDeleteEntry(item.id)}
                        >
                          <Tooltip title="删除该条记录">
                            <Button type="text" danger icon={<Trash2 size={15} />} style={{ borderRadius: 8 }} />
                          </Tooltip>
                        </Popconfirm>
                      </Space>
                    </div>
                  </Card>
                )
              })}
            </div>
          )}
        </div>
      )}

      {/* ==================== 3. 【设置与设备】板块 (Settings & Devices) ==================== */}
      {activeTab === 'settings' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>

          {/* 用户身份与快捷配对操作卡片 */}
          <Card style={{ borderRadius: 16 }} styles={{ body: { padding: '20px' } }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 16 }}>
              <div>
                <div style={{ fontSize: 16, fontWeight: 700, display: 'flex', alignItems: 'center', gap: 8 }}>
                  <ShieldCheck size={18} color="#2563EB" />
                  <span>设备组与配对中心</span>
                </div>
                <div style={{ fontSize: 13, color: '#6B7280', marginTop: 4 }}>
                  当前用户组拥有 <Text strong style={{ color: '#2563EB' }}>{devices.length}</Text> 台已绑定设备，
                  生成配对码可快速将新手机或桌面端加入本组。
                </div>
              </div>

              <Space size={10}>
                <Button
                  type="primary"
                  size="middle"
                  icon={<KeyRound size={15} />}
                  loading={pairLoading}
                  onClick={genCode}
                  style={{ borderRadius: 8, fontWeight: 600 }}
                >
                  生成配对码 (添加新设备)
                </Button>
              </Space>
            </div>
          </Card>

          {/* 设备列表与同步日志 */}
          <Row gutter={[16, 16]}>
            {/* 我的设备列表 */}
            <Col xs={24} lg={15}>
              <Card
                title={
                  <Space>
                    <Monitor size={16} color="#2563EB" />
                    <span>本组已授权设备 ({devices.length})</span>
                  </Space>
                }
                extra={
                  <Button type="link" size="small" icon={<RefreshCw size={13} />} onClick={loadAll}>
                    刷新
                  </Button>
                }
                style={{ borderRadius: 16, height: '100%' }}
                styles={{ body: { overflow: 'auto' } }}
              >
                {devices.length === 0 ? (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="本组暂无已配对设备" />
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                    {devices.map((dev) => (
                      <div
                        key={dev.id}
                        style={{
                          display: 'flex',
                          alignItems: 'center',
                          justifyContent: 'space-between',
                          padding: '12px 16px',
                          background: 'rgba(0, 0, 0, 0.02)',
                          borderRadius: 12,
                          border: '1px solid rgba(0, 0, 0, 0.04)',
                          gap: 12,
                          flexWrap: 'wrap',
                        }}
                      >
                        {/* 左侧: 设备图标 + 设备名称 + 在线指示灯 */}
                        <div style={{ display: 'flex', alignItems: 'center', gap: 12, minWidth: 200, flex: 1 }}>
                          <div
                            style={{
                              width: 40,
                              height: 40,
                              borderRadius: 10,
                              background: dev.online ? 'rgba(16, 185, 129, 0.12)' : 'rgba(156, 163, 175, 0.12)',
                              display: 'flex',
                              alignItems: 'center',
                              justifyContent: 'center',
                              color: dev.online ? '#10B981' : '#6B7280',
                              flexShrink: 0,
                            }}
                          >
                            {dev.platform?.toLowerCase().includes('android') || dev.platform?.toLowerCase().includes('ios') || dev.platform?.toLowerCase().includes('iphone') ? (
                              <Smartphone size={20} />
                            ) : dev.platform?.toLowerCase().includes('win') ? (
                              <Laptop size={20} />
                            ) : (
                              <Monitor size={20} />
                            )}
                          </div>
                          <div style={{ minWidth: 0, flex: 1 }}>
                            <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'nowrap' }}>
                              <span
                                style={{
                                  fontWeight: 600,
                                  fontSize: 14,
                                  whiteSpace: 'nowrap',
                                  overflow: 'hidden',
                                  textOverflow: 'ellipsis',
                                }}
                              >
                                {dev.name}
                              </span>
                              {dev.online ? (
                                <span
                                  style={{
                                    display: 'inline-flex',
                                    alignItems: 'center',
                                    gap: 5,
                                    fontSize: 11,
                                    color: '#10B981',
                                    background: 'rgba(16, 185, 129, 0.12)',
                                    padding: '2px 8px',
                                    borderRadius: 999,
                                    whiteSpace: 'nowrap',
                                    flexShrink: 0,
                                    fontWeight: 500,
                                  }}
                                >
                                  <span style={{ width: 6, height: 6, borderRadius: '50%', background: '#10B981' }} />
                                  在线
                                </span>
                              ) : (
                                <span
                                  style={{
                                    display: 'inline-flex',
                                    alignItems: 'center',
                                    gap: 5,
                                    fontSize: 11,
                                    color: '#9CA3AF',
                                    background: 'rgba(156, 163, 175, 0.12)',
                                    padding: '2px 8px',
                                    borderRadius: 999,
                                    whiteSpace: 'nowrap',
                                    flexShrink: 0,
                                  }}
                                >
                                  <span style={{ width: 6, height: 6, borderRadius: '50%', background: '#9CA3AF' }} />
                                  离线
                                </span>
                              )}
                            </div>
                            <div style={{ fontSize: 12, color: '#9CA3AF', marginTop: 2, whiteSpace: 'nowrap' }}>
                              最后活跃: {dayjs(dev.lastSeenAt).fromNow()}
                            </div>
                          </div>
                        </div>

                        {/* 右侧: 平台 Tag + IP + 移除按钮 */}
                        <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexShrink: 0 }}>
                          <Tag
                            color={
                              dev.platform?.includes('Android')
                                ? 'green'
                                : dev.platform?.includes('iOS') || dev.platform?.includes('iPhone')
                                ? 'orange'
                                : dev.platform?.includes('Windows')
                                ? 'blue'
                                : dev.platform?.includes('macOS') || dev.platform?.includes('Mac')
                                ? 'purple'
                                : 'cyan'
                            }
                            style={{ borderRadius: 6, margin: 0, fontSize: 12, whiteSpace: 'nowrap' }}
                          >
                            {dev.platform || 'Unknown'}
                          </Tag>
                          <span
                            style={{
                              fontSize: 12,
                              color: '#6B7280',
                              fontFamily: 'Consolas, monospace',
                              whiteSpace: 'nowrap',
                            }}
                          >
                            {dev.ip || '-'}
                          </span>
                          <Popconfirm
                            title={`确定移除设备「${dev.name}」?`}
                            okText="移除"
                            okButtonProps={{ danger: true }}
                            onConfirm={() => removeDev(dev.id, dev.name)}
                          >
                            <Button type="text" size="small" danger icon={<Trash2 size={15} />} />
                          </Popconfirm>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </Card>
            </Col>

            {/* 同步日志 */}
            <Col xs={24} lg={9}>
              <Card
                title={
                  <Space>
                    <Clock size={16} color="#2563EB" />
                    <span>近期同步活动</span>
                  </Space>
                }
                style={{ borderRadius: 16, height: '100%' }}
                styles={{ body: { overflow: 'auto', maxHeight: 380 } }}
              >
                {activities.length === 0 ? (
                  <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无同步日志" />
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                    {activities.map((a) => (
                      <div key={a.id} style={{ fontSize: 13 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                          <Tag
                            color={a.action === 'push' ? 'green' : a.action === 'connect' ? 'blue' : 'red'}
                            style={{ borderRadius: 4, margin: 0, fontSize: 11 }}
                          >
                            {a.action === 'push' ? '推送' : a.action === 'connect' ? '连接' : '删除'}
                          </Tag>
                          <Text strong>{a.deviceName}</Text>
                        </div>
                        {a.content ? (
                          <div style={{ fontSize: 12, color: '#6B7280', marginTop: 2, wordBreak: 'break-all' }}>
                            {a.content}
                          </div>
                        ) : null}
                        <div style={{ fontSize: 11, color: '#9CA3AF', marginTop: 2 }}>{dayjs(a.createdAt).fromNow()}</div>
                      </div>
                    ))}
                  </div>
                )}
              </Card>
            </Col>
          </Row>
        </div>
      )}

      {/* ==================== 配对码弹窗 (方案 1 扫码直连 + 方案 2 纯 6 位数字码) ==================== */}
      <Modal
        open={pairing !== null}
        onCancel={closePairingModal}
        footer={null}
        centered
        width={400}
        title={
          <Space>
            <QrIcon size={18} color="#2563EB" />
            <span style={{ fontWeight: 600 }}>添加新设备</span>
          </Space>
        }
      >
        {pairing && (
          <div style={{ textAlign: 'center', padding: '8px 0 4px 0' }}>
            {/* 方案 1: 动态二维码展示区 */}
            <div style={{ display: 'flex', justifyContent: 'center', marginBottom: 12 }}>
              <div
                style={{
                  padding: 12,
                  background: '#FFFFFF',
                  borderRadius: 16,
                  border: '1px solid rgba(0, 0, 0, 0.08)',
                  boxShadow: '0 4px 16px rgba(0,0,0,0.04)',
                }}
              >
                <QRCode
                  value={pairing.qrPayload || `${window.location.origin}/index?pairCode=${pairing.code}`}
                  size={160}
                  bordered={false}
                />
              </div>
            </div>
            <div style={{ fontSize: 13, color: '#4B5563', fontWeight: 500, marginBottom: 16, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
              <Smartphone size={16} color="#2563EB" />
              <span>手机使用系统相机或扫一扫，即可一秒直连</span>
            </div>

            {/* 分隔线与方案 2 提示 */}
            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 10,
                color: '#9CA3AF',
                fontSize: 12,
                margin: '12px 0',
              }}
            >
              <div style={{ flex: 1, height: 1, background: 'rgba(0,0,0,0.06)' }} />
              <span>或在其他设备输入 6 位验证码</span>
              <div style={{ flex: 1, height: 1, background: 'rgba(0,0,0,0.06)' }} />
            </div>

            {/* 方案 2: 6 位纯数字卡片 */}
            <div
              style={{
                background: 'rgba(37, 99, 235, 0.06)',
                borderRadius: 14,
                padding: '12px 16px',
                border: '1px dashed rgba(37, 99, 235, 0.3)',
                marginBottom: 14,
              }}
            >
              <div
                style={{
                  fontFamily: 'Consolas, Monaco, monospace',
                  fontSize: 32,
                  fontWeight: 700,
                  letterSpacing: 6,
                  color: '#2563EB',
                  userSelect: 'all',
                }}
              >
                {pairing.code.slice(0, 3)} {pairing.code.slice(3)}
              </div>
              <div style={{ fontSize: 11, color: '#6B7280', marginTop: 2 }}>
                单向接入 · 无需二次确认
              </div>
            </div>

            {/* 倒计时 & 提示 */}
            <div style={{ fontSize: 12, color: '#9CA3AF', marginBottom: 12 }}>
              验证码有效时间剩余: <Text strong style={{ color: '#F59E0B' }}>{countdown}</Text>
            </div>

            <div style={{ fontSize: 12, color: '#6B7280', marginBottom: 16, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4 }}>
              <div>在另一台设备输入上述 6 位数字验证码或扫码即可直接连接。</div>
              <div style={{ color: '#EF4444', display: 'flex', alignItems: 'center', gap: 4 }}>
                <AlertTriangle size={13} color="#EF4444" />
                <span>关闭对话框后配对码将立即失效。</span>
              </div>
            </div>

            {/* 操作按钮 */}
            <Space size={10}>
              <Button
                type="primary"
                icon={<Copy size={14} />}
                onClick={() => {
                  navigator.clipboard.writeText(pairing.code)
                  message.success('已复制 6 位验证码！')
                }}
                style={{ borderRadius: 8 }}
              >
                复制 6 位验证码
              </Button>
              <Button onClick={closePairingModal} style={{ borderRadius: 8 }}>
                完成并关闭
              </Button>
            </Space>
          </div>
        )}
      </Modal>
    </div>
  )
}
