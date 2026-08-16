import { useEffect, useState } from 'react'
import {
  Card, Form, InputNumber, Button, Segmented, Typography, Tag, Popconfirm, Space, message, Skeleton,
} from 'antd'
import {
  Sun, Moon, Monitor, Server, History, Database, Info, Save, Trash2, Palette, ShieldCheck,
} from 'lucide-react'
import {
  setThemeMode, getAdminSettings, putAdminSettings, clearHistory, getHealth, type ThemeMode,
} from '../api'

const { Text } = Typography

interface SettingsPageProps {
  themeMode?: ThemeMode
  onThemeChange?: (m: ThemeMode) => void
  /** 操作完成后触发全局刷新(清空历史后各页重拉) */
  onRefresh?: () => void
}

export default function SettingsPage({ themeMode, onThemeChange, onRefresh }: SettingsPageProps) {
  const [form] = Form.useForm()
  const [theme, setTheme] = useState<ThemeMode>(themeMode ?? 'light')
  const [maxHistory, setMaxHistory] = useState<number | null>(null)
  const [saving, setSaving] = useState(false)
  const [version, setVersion] = useState('')

  // 服务端运行设置 + 版本(真实数据,不再存 localStorage)
  useEffect(() => {
    getAdminSettings()
      .then(s => { setMaxHistory(s.maxHistoryCount); form.setFieldValue('maxHistory', s.maxHistoryCount) })
      .catch(() => message.error('服务端设置加载失败'))
    getHealth().then(h => setVersion(h.version)).catch(() => {})
  }, [form])

  const handleThemeChange = (v: ThemeMode) => {
    setTheme(v)
    setThemeMode(v)
    onThemeChange?.(v)
    message.success(v === 'system' ? '已切换为跟随系统' : v === 'dark' ? '已切换为深色主题' : '已切换为浅色主题')
  }

  const handleSaveServer = async (values: { maxHistory?: number }) => {
    setSaving(true)
    try {
      const r = await putAdminSettings(values.maxHistory ?? 1000)
      setMaxHistory(r.maxHistoryCount)
      message.success('已保存并立即生效(重启后保留)')
    } catch (e) {
      message.error('保存失败:' + (e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <div id="clipsync-settings-page" className="clipsync-settings-page" style={{ maxWidth: 760, margin: '0 auto' }}>
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        {/* ---------- 主题 ---------- */}
        <Card
          id="clipsync-settings-theme-card"
          className="clipsync-settings-card"
          title={<Space size={8}><Palette size={16} color="#2563EB" />主题外观</Space>}
          style={{ borderRadius: 14 }}
        >
          <div id="clipsync-settings-theme-body" style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 16, flexWrap: 'wrap' }}>
            <div style={{ fontWeight: 600 }}>界面主题</div>
            <Segmented<ThemeMode>
              id="clipsync-settings-theme-segmented"
              className="clipsync-settings-theme-segmented"
              value={theme}
              onChange={handleThemeChange}
              options={[
                { value: 'light', label: '浅色', icon: <Sun size={14} /> },
                { value: 'dark', label: '深色', icon: <Moon size={14} /> },
                { value: 'system', label: '跟随系统', icon: <Monitor size={14} /> },
              ]}
            />
          </div>
        </Card>

        {/* ---------- 服务端 ---------- */}
        <Card
          id="clipsync-settings-server-card"
          className="clipsync-settings-card"
          title={<Space size={8}><Server size={16} color="#2563EB" />服务端设置</Space>}
          style={{ borderRadius: 14 }}
        >
          {maxHistory === null ? (
            <Skeleton active paragraph={{ rows: 1 }} />
          ) : (
            <Form
              id="clipsync-settings-server-form"
              className="clipsync-settings-server-form"
              form={form}
              layout="vertical"
              initialValues={{ maxHistory }}
              onFinish={handleSaveServer}
            >
              <Form.Item
                name="maxHistory"
                label={<Space size={6}><History size={14} />历史上限(超出自动清理最旧记录,含图片文件)</Space>}
                extra="保存后立即生效并持久化,服务端重启后保留;初始值来自环境变量 SC_MAX_HISTORY"
              >
                <InputNumber
                  id="clipsync-settings-max-history"
                  className="clipsync-settings-max-history"
                  min={100}
                  max={100000}
                  step={100}
                  style={{ width: 220 }}
                  addonAfter="条"
                />
              </Form.Item>
              <Button id="clipsync-settings-server-save" className="clipsync-settings-save" type="primary" htmlType="submit" loading={saving} icon={<Save size={15} />}>
                保存设置
              </Button>
            </Form>
          )}
        </Card>

        {/* ---------- 数据 ---------- */}
        <Card
          id="clipsync-settings-data-card"
          className="clipsync-settings-card"
          title={<Space size={8}><Database size={16} color="#2563EB" />数据管理</Space>}
          style={{ borderRadius: 14 }}
        >
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            <Space size={12} wrap>
              <Popconfirm
                title="清空全部剪贴板历史?"
                description="该操作不可恢复,确定继续?"
                okText="清空"
                cancelText="取消"
                okButtonProps={{ danger: true }}
                onConfirm={async () => {
                  try {
                    await clearHistory()
                    message.success('历史已清空')
                    onRefresh?.()
                  } catch (e) {
                    message.error('清空失败:' + (e as Error).message)
                  }
                }}
              >
                <Button id="clipsync-settings-clear" className="clipsync-settings-clear" danger icon={<Trash2 size={15} />}>
                  清空历史
                </Button>
              </Popconfirm>
            </Space>
          </Space>
        </Card>

        {/* ---------- 关于 ---------- */}
        <Card
          id="clipsync-settings-about-card"
          className="clipsync-settings-card"
          title={<Space size={8}><Info size={16} color="#2563EB" />关于</Space>}
          style={{ borderRadius: 14 }}
        >
          <Space direction="vertical" size={12}>
            <Space size={8} align="center">
              <ShieldCheck size={18} color="#10B981" />
              <Text strong style={{ fontSize: 15 }}>ClipSync 剪贴板共享服务端</Text>
              {version ? <Tag color="blue">v{version}</Tag> : null}
            </Space>
            <Text type="secondary">自建部署 · 配对码配对 + 会话鉴权 · 登录限流与审计</Text>
            <Space size={8} wrap>
              <Tag>React 19</Tag>
              <Tag>Ant Design 6</Tag>
              <Tag>Node.js</Tag>
              <Tag>SignalR 协议(WebSocket)</Tag>
              <Tag>SQLite</Tag>
            </Space>
            <Text type="secondary" style={{ fontSize: 12 }}>Android / Windows / 网页端 · 文本与图片实时同步</Text>
          </Space>
        </Card>
      </Space>
    </div>
  )
}
