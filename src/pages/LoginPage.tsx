import { useState } from 'react'
import { Card, Form, Input, Button, message, Typography, theme } from 'antd'
import { Cloud, User, Lock } from 'lucide-react'
import { login as apiLogin } from '../api'

const { Title, Text } = Typography

export default function LoginPage({ onLogin }: { onLogin: () => void }) {
  const { token } = theme.useToken()
  const [loading, setLoading] = useState(false)

  const submit = async (values: { username: string; password: string }) => {
    setLoading(true)
    try {
      const ok = await apiLogin(values.username.trim(), values.password)
      if (ok) {
        message.success('登录成功')
        onLogin()
      } else {
        message.error('用户名或密码错误')
      }
    } finally {
      setLoading(false)
    }
  }

  return (
    <div id="clipsync-login-page" className="clipsync-login-page" style={{
      minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center',
      background: 'linear-gradient(135deg, ' + token.colorPrimaryBg + ' 0%, ' + token.colorBgLayout + ' 100%)',
    }}>
      <Card id="clipsync-login-card" className="clipsync-login-card" style={{ width: 400, boxShadow: '0 8px 30px rgba(0,0,0,0.08)', borderRadius: 16 }}>
        <div style={{ textAlign: 'center', marginBottom: 24 }}>
          <Cloud size={44} color="#2563EB" />
          <Title level={3} style={{ marginTop: 12, marginBottom: 4 }}>ClipSync</Title>
          <Text type="secondary">剪贴板共享服务端 · 管理台</Text>
        </div>
        <Form id="clipsync-login-form" className="clipsync-login-form" layout="vertical" onFinish={submit}>
          <Form.Item
            name="username"
            label="用户名"
            rules={[{ required: true, message: '请输入用户名' }]}
          >
            <Input id="clipsync-login-username" prefix={<User size={15} color="#9CA3AF" />} placeholder="管理员用户名" size="large" autoComplete="username" />
          </Form.Item>
          <Form.Item
            name="password"
            label="密码"
            rules={[{ required: true, message: '请输入密码' }]}
          >
            <Input.Password id="clipsync-login-password" prefix={<Lock size={15} color="#9CA3AF" />} placeholder="密码" size="large" autoComplete="current-password" />
          </Form.Item>
          <Button id="clipsync-login-submit" className="clipsync-login-submit" type="primary" htmlType="submit" block size="large" loading={loading}>
            登录
          </Button>
          <div style={{ textAlign: 'center', marginTop: 12 }}>
            <Text type="secondary" style={{ fontSize: 12 }}>凭据配置于服务端 .env 文件</Text>
          </div>
        </Form>
      </Card>
    </div>
  )
}
