import { useEffect, useRef, useState, useCallback } from 'react'
import {
  Card, Typography, Space, Button, Table, Tag, Empty, Upload, Popconfirm,
  message, Tooltip,
} from 'antd'
import type { UploadProps } from 'antd'
import {
  FileText, Download, Trash2, RefreshCw, UploadCloud, Monitor, HardDrive,
} from 'lucide-react'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import relativeTime from 'dayjs/plugin/relativeTime'
import {
  listFiles, uploadFile, deleteFile, downloadFile,
  type FileItem,
} from '../api'

dayjs.extend(relativeTime)
const { Text } = Typography

function formatSize(bytes: number): string {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' MB'
  return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB'
}

export default function FilesPage({ refreshTick, onHubFileChange }: {
  refreshTick: number
  /** 注册 hub 回调:其它端上传/删除时刷新列表 */
  onHubFileChange?: (handlers: { onTransferred: () => void; onDeleted: (fileId: string) => void }) => void
}) {
  const [items, setItems] = useState<FileItem[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [uploading, setUploading] = useState(0) // 进行中的上传数
  const listRef = useRef(0)

  const load = useCallback(() => {
    const seq = ++listRef.current
    setLoading(true)
    listFiles(0, 100)
      .then((d) => {
        if (seq !== listRef.current) return // 过期响应丢弃
        setItems(d.items)
        setTotal(d.total)
      })
      .catch((e) => message.error('文件列表加载失败: ' + (e?.message ?? e)))
      .finally(() => {
        if (seq === listRef.current) setLoading(false)
      })
  }, [])

  useEffect(() => { load() }, [load, refreshTick])

  // hub 推送:其它端上传/删除 → 刷新(由 App 层把 hub 事件接进来)
  useEffect(() => {
    onHubFileChange?.({
      onTransferred: () => load(),
      onDeleted: () => load(),
    })
  }, [onHubFileChange, load])

  const doUpload = async (file: File) => {
    setUploading((n) => n + 1)
    try {
      await uploadFile(file)
      message.success('已上传: ' + file.name)
      load()
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e)
      message.error('上传失败: ' + msg)
    } finally {
      setUploading((n) => n - 1)
    }
    // 阻止 antd Upload 的默认继续行为(我们手动处理)
    return Upload.LIST_IGNORE
  }

  const uploadProps: UploadProps = {
    multiple: true,
    showUploadList: false,
    // 服务端上限与图片一致(SC_MAX_IMAGE_BYTES,默认 10MB);超限时服务端会 400/413
    beforeUpload: (_file, fileList) => {
      if (fileList.indexOf(_file) === 0) setUploading((n) => n + fileList.length)
      return false // 不走 antd 默认 xhr,由 customRequest 语义在 doUpload 处理
    },
    customRequest: (options) => {
      doUpload(options.file as File).finally(() => setUploading((n) => Math.max(0, n - 1)))
    },
  }

  const doDownload = async (item: FileItem) => {
    try {
      await downloadFile(item)
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e)
      message.error(msg)
    }
  }

  const doDelete = async (item: FileItem) => {
    try {
      await deleteFile(item.fileId)
      message.success('已删除: ' + item.fileName)
      load()
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e)
      message.error('删除失败: ' + msg)
    }
  }

  const columns: ColumnsType<FileItem> = [
    {
      title: '文件',
      dataIndex: 'fileName',
      key: 'fileName',
      ellipsis: true,
      render: (name: string) => (
        <Space>
          <FileText size={16} style={{ flexShrink: 0 }} />
          <Text strong ellipsis style={{ maxWidth: 420 }}>{name}</Text>
        </Space>
      ),
    },
    {
      title: '大小',
      dataIndex: 'size',
      key: 'size',
      width: 100,
      render: (s: number) => <Text type="secondary">{formatSize(s)}</Text>,
    },
    {
      title: '来源',
      dataIndex: 'deviceName',
      key: 'deviceName',
      width: 160,
      render: (name: string | null, row) => (
        <Space>
          <Monitor size={14} />
          <Text type="secondary">{name || row.deviceId}</Text>
        </Space>
      ),
    },
    {
      title: '时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 140,
      render: (t: string) => (
        <Tooltip title={dayjs(t).format('YYYY-MM-DD HH:mm:ss')}>
          <Text type="secondary">{dayjs(t).fromNow()}</Text>
        </Tooltip>
      ),
    },
    {
      title: '操作',
      key: 'actions',
      width: 120,
      render: (_, item) => (
        <Space>
          <Tooltip title="下载">
            <Button size="small" type="text" icon={<Download size={15} />} onClick={() => doDownload(item)} />
          </Tooltip>
          <Popconfirm title="确定删除该文件？" onConfirm={() => doDelete(item)}>
            <Tooltip title="删除">
              <Button size="small" type="text" danger icon={<Trash2 size={15} />} />
            </Tooltip>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  return (
    <Card
      title={
        <Space>
          <HardDrive size={18} />
          文件互传
          <Tag>{total}</Tag>
        </Space>
      }
      extra={
        <Space>
          <Upload {...uploadProps}>
            <Button
              type="primary"
              icon={<UploadCloud size={16} />}
              loading={uploading > 0}
            >
              上传文件
            </Button>
          </Upload>
          <Button icon={<RefreshCw size={15} />} onClick={load} loading={loading} />
        </Space>
      }
    >
      <Table
        rowKey="fileId"
        columns={columns}
        dataSource={items}
        loading={loading}
        size="middle"
        pagination={{ pageSize: 20, total, showSizeChanger: false }}
        locale={{
          emptyText: (
            <Empty
              description="还没有互传过的文件"
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            >
              <Text type="secondary">点击「上传文件」或把文件拖到这里,同账号的设备与网页会话都会收到通知</Text>
            </Empty>
          ),
        }}
      />
    </Card>
  )
}
