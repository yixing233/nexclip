import { useState } from 'react'
import {
  Card, Form, Input, InputNumber, Button, Segmented, Typography, Tag, Popconfirm, Space, message,
} from 'antd'
import {
  Sun, Moon, Monitor, Server, History, Database, Info, Save, Trash2, Palette, ShieldCheck,
} from 'lucide-react'
import {
  getMaxHistory, setMaxHistory, clearHistory,
  getThemeMode, setThemeMode, type ThemeMode,
} from '../api'

const { Text } = Typography

interface SettingsPageProps {
  themeMode?: ThemeMode
  onThemeChange?: (m: ThemeMode) => void
}

export default function SettingsPage({ themeMode, onThemeChange }: SettingsPageProps) {
  const [form] = Form.useForm()
  const [theme, setTheme] = useState<ThemeMode>(themeMode ?? getThemeMode())

  const handleThemeChange = (v: ThemeMode) => {
    setTheme(v)
    setThemeMode(v)
    onThemeChange?.(v)
    message.success(v === 'system' ? '已切换为跟随系统' : v === 'dark' ? '已切换为深色主题' : '已切换为浅色主题')
  }

  /** 生成 32 字节随机令牌,base64url 编码(浏览器端 crypto.getRandomValues,无第三方依赖) */
  const generateRandomToken = (): string => {
    const bytes = new Uint8Array(32)
    crypto.getRandomValues(bytes)
    let bin = ''
    for (const b of bytes) bin += String.fromCharCode(b)
    return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
  }

  const handleGenerateToken = () => {
    form.setFieldValue('token', generateRandomToken())
    message.success('已生成随机令牌,确认后点击保存')
  }

  const handleCopyToken = async () => {
    const v = (form.getFieldValue('token') as string) ?? ''
    if (!v.trim()) { message.warning('令牌为空,请先输入或生成'); return }
    try {
      await navigator.clipboard.writeText(v.trim())
      message.success('令牌已复制到剪贴板')
    } catch {
      message.warning('复制失败,请手动选择复制')
    }
  }

  const handleSaveServer = (values: { maxHistory?: number }) => {
    setMaxHistory(values.maxHistory ?? 1000)
    message.success('服务端设置已保存,刷新页面后生效')
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
          title={<Space size={8}><Server size={16} color="#2563EB" />服务端连接</Space>}
          style={{ borderRadius: 14 }}
        >
          <Form
            id="clipsync-settings-server-form"
            className="clipsync-settings-server-form"
            form={form}
            layout="vertical"
            initialValues={{
              maxHistory: getMaxHistory(),
            }}
            onFinish={handleSaveServer}
          >
            <Form.Item
              name="maxHistory"
              label={<Space size={6}><History size={14} />历史上限</Space>}
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
            <Button id="clipsync-settings-server-save" className="clipsync-settings-save" type="primary" htmlType="submit" icon={<Save size={15} />}>
              保存设置
            </Button>
          </Form>
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
                  await clearHistory()
                  message.success('历史已清空')
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
              <Tag color="blue">v0.1.0</Tag>
            </Space>
            <Text type="secondary">自建部署 · 端到端令牌鉴权</Text>
            <Space size={8} wrap>
              <Tag>React 19</Tag>
              <Tag>Ant Design 6</Tag>
              <Tag>SignalR</Tag>
              <Tag>ASP.NET Core 9</Tag>
              <Tag>SQLite</Tag>
            </Space>
            <Text type="secondary" style={{ fontSize: 12 }}>Android / Windows / 网页端 · 文本与图片实时同步</Text>
          </Space>
        </Card>
      </Space>
    </div>
  )
}
