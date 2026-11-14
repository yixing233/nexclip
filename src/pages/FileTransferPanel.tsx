import { useEffect, useRef, useState } from 'react'
import {
  Card, Typography, Space, Button, Select, Progress, Tag, Tooltip, Empty, Upload, message,
} from 'antd'
import type { UploadProps } from 'antd'
import {
  IconFile, IconDownload, IconCloudUpload, IconCircleCheck, IconAlertCircle, IconRefresh,
  IconDeviceMobile, IconX, IconClock,
} from '@tabler/icons-react'
import {
  uploadFile, downloadFile, deleteFile, acceptFile, rejectFile, requestFileTransfer,
  getDefaultDeviceName, getBrowserPlatform,
  type FileItem, type DeviceInfo,
} from '../api'

const { Text } = Typography

function formatSize(bytes: number): string {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' MB'
  return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB'
}

/**
 * 单个发送任务(内存态,刷新即清 —— 即时传输不需要持久记录)。
 * uploading →(上传完)→ pending(等待目标设备确认)→ done / rejected / expired
 */
interface TransferTask {
  key: string
  file: File
  fileId?: string
  pct: number
  status: 'uploading' | 'pending' | 'done' | 'rejected' | 'expired' | 'failed'
  note?: string
  error?: string
}

/**
 * 即时文件互传面板(用户端,确认制)。
 *
 * 发送:选目标设备 → 添加文件立即上传 → 目标设备接受后才算送达;拒绝/超时即失败(服务端删文件)。
 * 接收:收到 FileTransferRequested 后在本端「待确认」区接受/拒绝。
 */
export default function FileTransferPanel({
  devices, receivedFiles, requestedFiles, respondedEvents, deletedFileIds,
}: {
  devices: DeviceInfo[]
  /** 本次会话收到的文件(来自 hub FileTransferred,排除本机回声) */
  receivedFiles: FileItem[]
  /** 待确认请求(来自 hub FileTransferRequested,排除本机回声) */
  requestedFiles: FileItem[]
  /** 本端作为发送方收到的响应事件 */
  respondedEvents: { fileId: string; action: 'accepted' | 'rejected' }[]
  /** 已删除的 fileId(过期清扫/对端拒绝等) */
  deletedFileIds: string[]
}) {
  const [targets, setTargets] = useState<string[]>([])
  const [tasks, setTasks] = useState<TransferTask[]>([])
  const seqRef = useRef(0)
  // 本端已接受的请求 → 收到区展示(与 receivedFiles 合并)
  const [acceptedLocal, setAcceptedLocal] = useState<FileItem[]>([])
  // 请求处理状态(accepting/accepted/rejected),按 fileId
  const [handledRequests, setHandledRequests] = useState<Record<string, string>>({})

  // 默认选中第一台在线设备(可改选/加选/清空=全员广播)
  const online = devices.filter(d => d.online)
  useEffect(() => {
    if (online.length > 0) {
      setTargets(prev => (prev.length === 0 ? [online[0].id] : prev))
    }
  }, [online.length]) // eslint-disable-line

  // 目标设备响应:accepted → 已送达;rejected → 已拒绝
  const respSeen = useRef(0)
  useEffect(() => {
    const fresh = respondedEvents.slice(respSeen.current)
    respSeen.current = respondedEvents.length
    if (fresh.length === 0) return
    setTasks(arr => arr.map(t => {
      const ev = fresh.find(e => e.fileId === t.fileId && t.status === 'pending')
      if (ev && ev.action === 'accepted') {
        // 对方已接受 → 开始传输文件本体
        doUpload(t.key, t.file, ev.fileId)
        return { ...t, status: 'uploading', pct: 0 }
      }
      return ev ? { ...t, status: 'rejected' } : t
    }))
  }, [respondedEvents])

  // 文件被删除(超时清扫/取消):pending → expired
  const delSeen = useRef(0)
  useEffect(() => {
    const fresh = deletedFileIds.slice(delSeen.current)
    delSeen.current = deletedFileIds.length
    if (fresh.length === 0) return
    setTasks(arr => arr.map(t => (
      t.status === 'pending' && t.fileId && fresh.includes(t.fileId)
        ? { ...t, status: 'expired', note: '对方未响应,已过期' }
        : t
    )))
  }, [deletedFileIds])

  /** 确认制:对方已接受 → 上传文件本体(带 requestId 挂到既有请求) */
  const doUpload = (key: string, file: File, fileId: string) => {
    setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'uploading', pct: 0, error: undefined } : t)))
    uploadFile(file, { requestId: fileId, onProgress: (pct) => setTasks(arr => arr.map(t => (t.key === key ? { ...t, pct } : t))) })
      .then(() => {
        setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'done', pct: 100 } : t)))
      })
      .catch((e: unknown) => {
        const msg = e instanceof Error ? e.message : String(e)
        setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'failed', error: msg } : t)))
      })
  }

  /** 不选目标 = 全员广播,无需确认,直接上传 */
  const sendBroadcast = (key: string, file: File) => {
    setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'uploading', pct: 0, error: undefined } : t)))
    uploadFile(file, {
      onProgress: (pct) => setTasks(arr => arr.map(t => (t.key === key ? { ...t, pct } : t))),
    })
      .then(() => {
        setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'done', pct: 100 } : t)))
      })
      .catch((e: unknown) => {
        const msg = e instanceof Error ? e.message : String(e)
        setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'failed', error: msg } : t)))
      })
  }

  const addFiles = (files: File[]) => {
    if (files.length === 0) return
    for (const f of files) {
      const key = 't' + Date.now() + '-' + ++seqRef.current
      setTasks(arr => [...arr, { key, file: f, pct: 0, status: 'uploading' }])
      if (targets.length === 0) {
        sendBroadcast(key, f)
        continue
      }
      // 确认制第一步:只发请求(元数据),对方接受后才开始上传文件本体
      setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'pending' } : t)))
      requestFileTransfer(f.name, f.size, targets)
        .then((dto) => {
          setTasks(arr => arr.map(t => (t.key === key ? { ...t, fileId: dto.fileId, status: 'pending' } : t)))
        })
        .catch((e: unknown) => {
          const msg = e instanceof Error ? e.message : String(e)
          setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'failed', error: msg } : t)))
        })
    }
  }

  const draggerProps: UploadProps = {
    multiple: true,
    showUploadList: false,
    beforeUpload: (file) => {
      addFiles([file as File])
      return Upload.LIST_IGNORE
    },
    customRequest: () => undefined, // 实际发送在 addFiles,禁用 antd 默认 xhr
  }

  const doDownload = async (item: FileItem) => {
    try {
      await downloadFile(item)
    } catch (e: unknown) {
      message.error(e instanceof Error ? e.message : String(e))
    }
  }

  const respondRequest = (f: FileItem, accept: boolean) => {
    setHandledRequests(h => ({ ...h, [f.fileId]: accept ? 'accepting' : 'rejecting' }))
    const call = accept ? acceptFile(f.fileId) : rejectFile(f.fileId)
    call
      .then(() => {
        setHandledRequests(h => ({ ...h, [f.fileId]: accept ? 'accepted' : 'rejected' }))
        if (accept) setAcceptedLocal(arr => [...arr, f])
        else message.info('已拒绝该文件')
      })
      .catch((e: unknown) => {
        setHandledRequests(h => {
          const { [f.fileId]: _drop, ...rest } = h
          return rest
        })
        message.error(e instanceof Error ? e.message : String(e))
      })
  }

  const pendingRequests = requestedFiles.filter(f => !handledRequests[f.fileId])
  const receivedAll = [...acceptedLocal, ...receivedFiles.filter(r => !acceptedLocal.some(a => a.fileId === r.fileId))]

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <Card
        title={
          <Space>
            <IconCloudUpload size={18} />
            发送文件
          </Space>
        }
        style={{ borderRadius: 16 }}
        styles={{ header: { flexWrap: 'wrap', gap: 8 } }}
      >
        <div style={{ marginBottom: 12 }}>
          <div style={{ fontSize: 12, color: '#9CA3AF', marginBottom: 4 }}>目标设备(不选 = 全员广播,无需对方确认)</div>
          <Select
            mode="multiple"
            allowClear
            placeholder="选择接收文件的目标设备"
            value={targets}
            onChange={setTargets}
            options={devices.map(d => ({ value: d.id, label: d.online ? d.name : d.name + '（离线）' }))}
            maxTagCount="responsive"
            style={{ width: '100%', maxWidth: 480 }}
          />
        </div>
        <Upload.Dragger {...draggerProps} style={{ borderRadius: 12 }}>
          <div style={{ padding: '18px 0' }}>
            <IconCloudUpload size={34} color="#2563EB" style={{ marginBottom: 8 }} />
            <div style={{ fontSize: 14, fontWeight: 600 }}>点击选择文件,或拖拽到此处</div>
            <div style={{ fontSize: 12, color: '#9CA3AF', marginTop: 4 }}>
              添加后立即上传,目标设备确认后才送达 · 单个文件 ≤ 10MB
            </div>
          </div>
        </Upload.Dragger>

        {tasks.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10, marginTop: 12 }}>
            {tasks.map(t => (
              <div
                key={t.key}
                style={{
                  display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
                  padding: '10px 12px', background: 'rgba(0, 0, 0, 0.02)', borderRadius: 10,
                }}
              >
                <IconFile size={16} style={{ flexShrink: 0 }} />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, marginBottom: 4 }}>
                    <Text strong ellipsis style={{ minWidth: 0 }}>{t.file.name}</Text>
                    <Text type="secondary" style={{ fontSize: 12, flexShrink: 0 }}>{formatSize(t.file.size)}</Text>
                  </div>
                  {(t.status === 'uploading' || t.status === 'pending') && (
                    <Progress percent={t.pct} size="small" strokeColor="#2563EB" />
                  )}
                </div>
                {t.status === 'uploading' && <Tag color="processing" style={{ margin: 0 }}>上传中</Tag>}
                {t.status === 'pending' && (
                  <Space size={4}>
                    <Tooltip title="等待目标设备接受">
                      <Tag color="processing" icon={<IconClock size={12} />} style={{ margin: 0 }}>等待确认</Tag>
                    </Tooltip>
                    <Tooltip title="取消传输并删除文件">
                      <Button
                        size="small"
                        icon={<IconX size={13} />}
                        onClick={() => {
                          if (t.fileId) deleteFile(t.fileId).catch(() => {})
                          setTasks(arr => arr.map(x => (x.key === t.key ? { ...x, status: 'expired', note: '已取消' } : x)))
                        }}
                      >
                        取消
                      </Button>
                    </Tooltip>
                  </Space>
                )}
                {t.status === 'done' && (
                  <Tooltip title="目标设备已接受,可从其设备端下载">
                    <Tag color="success" icon={<IconCircleCheck size={12} />} style={{ margin: 0 }}>已送达</Tag>
                  </Tooltip>
                )}
                {t.status === 'rejected' && (
                  <Tag color="error" icon={<IconX size={12} />} style={{ margin: 0 }}>对方已拒绝</Tag>
                )}
                {t.status === 'expired' && (
                  <Tag icon={<IconClock size={12} />} style={{ margin: 0 }}>{t.note ?? '已过期'}</Tag>
                )}
                {t.status === 'failed' && (
                  <Space size={4}>
                    <Tooltip title={t.error}>
                      <Tag color="error" icon={<IconAlertCircle size={12} />} style={{ margin: 0 }}>失败</Tag>
                    </Tooltip>
                    <Button size="small" icon={<IconRefresh size={13} />} onClick={() => { setTasks(arr => arr.filter(x => x.key !== t.key)); addFiles([t.file]) }}>
                      重试
                    </Button>
                  </Space>
                )}
              </div>
            ))}
          </div>
        )}
      </Card>

      {pendingRequests.length > 0 && (
        <Card
          title={
            <Space>
              <IconDeviceMobile size={18} />
              待确认的传输请求
              <Tag color="orange">{pendingRequests.length}</Tag>
            </Space>
          }
          style={{ borderRadius: 16, borderColor: '#F59E0B' }}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {pendingRequests.map(f => (
              <div
                key={f.fileId}
                style={{
                  display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
                  padding: '10px 12px', background: 'rgba(245, 158, 11, 0.08)', borderRadius: 10,
                }}
              >
                <IconFile size={16} style={{ flexShrink: 0 }} />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Text strong ellipsis style={{ display: 'block', minWidth: 0 }}>{f.fileName}</Text>
                  <Text type="secondary" style={{ fontSize: 12 }}>
                    {f.deviceName || f.deviceId} 请求发送 · {formatSize(f.size)}
                  </Text>
                </div>
                <Space size={8}>
                  <Button
                    size="small"
                    type="primary"
                    icon={<IconCircleCheck size={13} />}
                    loading={handledRequests[f.fileId] === 'accepting'}
                    onClick={() => respondRequest(f, true)}
                  >
                    接受
                  </Button>
                  <Button
                    size="small"
                    danger
                    icon={<IconX size={13} />}
                    loading={handledRequests[f.fileId] === 'rejecting'}
                    onClick={() => respondRequest(f, false)}
                  >
                    拒绝
                  </Button>
                </Space>
              </div>
            ))}
          </div>
        </Card>
      )}

      <Card
        title={
          <Space>
            <IconDeviceMobile size={18} />
            本次收到的文件
            <Tag>{receivedAll.length}</Tag>
          </Space>
        }
        style={{ borderRadius: 16 }}
      >
        {receivedAll.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="暂无接收记录 · 其它设备发来文件并确认后,会实时出现在这里"
          />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {receivedAll.map((f) => (
              <div
                key={f.fileId + f.createdAt}
                style={{
                  display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
                  padding: '10px 12px', background: 'rgba(0, 0, 0, 0.02)', borderRadius: 10,
                }}
              >
                <IconFile size={16} style={{ flexShrink: 0 }} />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Text strong ellipsis style={{ display: 'block', minWidth: 0 }}>{f.fileName}</Text>
                  <Text type="secondary" style={{ fontSize: 12 }}>
                    来自 {f.deviceName || f.deviceId} · {formatSize(f.size)}
                  </Text>
                </div>
                <Button size="small" icon={<IconDownload size={13} />} onClick={() => doDownload(f)}>
                  下载
                </Button>
              </div>
            ))}
          </div>
        )}
      </Card>
    </div>
  )
}
