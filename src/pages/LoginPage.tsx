import { useState } from 'react'
import { Card, Form, Input, Button, message, Typography, theme } from 'antd'
import { Cloud } from 'lucide-react'
import { login as apiLogin } from '../api'

const { Title, Text } = Typography

export default function LoginPage({ onLogin }: { onLogin: () => void }) {
  const { token } = theme.useToken()
  const [loading, setLoading] = useState(false)

  const submit = async (values: { token: string }) => {
    setLoading(true)
    try {
      const ok = await apiLogin(values.token.trim())
      if (ok) {
        message.success('登录成功')
        onLogin()
      } else {
        message.error('令牌无效')
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
          <Text type="secondary">剪贴板共享服务端</Text>
        </div>
        <Form id="clipsync-login-form" className="clipsync-login-form" layout="vertical" onFinish={submit}>
          <Form.Item
            name="token"
            label="访问令牌"
            rules={[{ required: true, message: '请输入访问令牌' }]}
          >
            <Input.Password id="clipsync-login-token" className="clipsync-login-token" placeholder="请输入服务端配置的访问令牌" size="large" />
          </Form.Item>
          <Button id="clipsync-login-submit" className="clipsync-login-submit" type="primary" htmlType="submit" block size="large" loading={loading}>
            登录
          </Button>
        </Form>
      </Card>
    </div>
  )
}
