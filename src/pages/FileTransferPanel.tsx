import { useEffect, useRef, useState } from 'react'
import {
  Card, Typography, Space, Button, Select, Progress, Tag, Tooltip, Empty, Upload, message,
} from 'antd'
import type { UploadProps } from 'antd'
import {
  IconFile, IconDownload, IconCloudUpload, IconCircleCheck, IconAlertCircle, IconRefresh, IconDeviceMobile,
} from '@tabler/icons-react'
import {
  uploadFile, downloadFile, getDefaultDeviceName, getBrowserPlatform,
  type FileItem, type DeviceInfo,
} from '../api'

const { Text } = Typography

function formatSize(bytes: number): string {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' MB'
  return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB'
}

/** 单个发送任务(内存态,刷新即清 —— 即时传输不需要持久记录) */
interface TransferTask {
  key: string
  file: File
  pct: number
  status: 'uploading' | 'done' | 'failed'
  error?: string
}

/**
 * 即时文件互传面板(用户端)。
 *
 * 两方/多方即时传输:选目标设备 → 添加文件立即发送 → 展示进度与结果;
 * 不做持久文件列表(服务端存储的管理在管理端 FilesPage)。
 */
export default function FileTransferPanel({ devices, receivedFiles }: {
  devices: DeviceInfo[]
  /** 本次会话收到的文件(来自 hub FileTransferred,排除本机回声) */
  receivedFiles: FileItem[]
}) {
  const [targets, setTargets] = useState<string[]>([])
  const [tasks, setTasks] = useState<TransferTask[]>([])
  const seqRef = useRef(0)

  // 默认选中第一台在线设备(可改选/加选/清空=全员广播)
  const online = devices.filter(d => d.online)
  useEffect(() => {
    if (online.length > 0) {
      setTargets(prev => (prev.length === 0 ? [online[0].id] : prev))
    }
  }, [online.length]) // eslint-disable-line

  const sendOne = (key: string, file: File) => {
    setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'uploading', pct: 0, error: undefined } : t)))
    uploadFile(file, {
      targets,
      onProgress: (pct) => setTasks(arr => arr.map(t => (t.key === key ? { ...t, pct } : t))),
    })
      .then(() => setTasks(arr => arr.map(t => (t.key === key ? { ...t, status: 'done', pct: 100 } : t))))
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
      sendOne(key, f)
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
        styles={{ header: { flexWrap: 'wrap', gap: 6 } }}
      >
        <div style={{ marginBottom: 12 }}>
          <div style={{ fontSize: 12, color: '#9CA3AF', marginBottom: 4 }}>目标设备(不选 = 全员广播)</div>
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
              添加后立即发送到目标设备 · 单个文件 ≤ 10MB · 推送通知仅目标设备可见
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
                  {t.status === 'uploading' && (
                    <Progress percent={t.pct} size="small" strokeColor="#2563EB" />
                  )}
                </div>
                {t.status === 'uploading' && <Tag color="processing" style={{ margin: 0 }}>传输中</Tag>}
                {t.status === 'done' && (
                  <Tooltip title="目标设备已收到推送,可从其设备端下载">
                    <Tag color="success" icon={<IconCircleCheck size={12} />} style={{ margin: 0 }}>已送达</Tag>
                  </Tooltip>
                )}
                {t.status === 'failed' && (
                  <Space size={4}>
                    <Tooltip title={t.error}>
                      <Tag color="error" icon={<IconAlertCircle size={12} />} style={{ margin: 0 }}>失败</Tag>
                    </Tooltip>
                    <Button size="small" icon={<IconRefresh size={13} />} onClick={() => sendOne(t.key, t.file)}>
                      重试
                    </Button>
                  </Space>
                )}
              </div>
            ))}
          </div>
        )}
      </Card>

      <Card
        title={
          <Space>
            <IconDeviceMobile size={18} />
            本次收到的文件
            <Tag>{receivedFiles.length}</Tag>
          </Space>
        }
        style={{ borderRadius: 16 }}
      >
        {receivedFiles.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="暂无接收记录 · 其它设备发来文件时会实时出现在这里"
          />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {receivedFiles.map((f) => (
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
