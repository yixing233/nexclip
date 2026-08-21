import { useEffect, useState } from 'react'
import { Card, Form, Input, Button, message, Typography, theme, Spin, Tag } from 'antd'
import { User, Lock, ArrowRight, Smartphone } from 'lucide-react'
import { login as apiLogin, pairDirect, setSession, getDefaultDeviceName } from '../api'

const { Title, Text } = Typography

/** mode: 'user' = /index/login(6位码/扫码直连); 'admin' = /pro/login(账密) */
export default function LoginPage({ mode, onLogin }: { mode: 'user' | 'admin'; onLogin: () => void }) {
  const { token } = theme.useToken()
  const [loading, setLoading] = useState(false)
  const [autoPairing, setAutoPairing] = useState(false)

  const [pairCode, setPairCode] = useState('')
  const [pairName, setPairName] = useState(getDefaultDeviceName())

  // 1. 扫码直连捕获: 检测 URL 中是否存在 pairCode 参数
  useEffect(() => {
    if (mode !== 'user') return
    const urlParams = new URLSearchParams(window.location.search)
    const codeFromUrl = urlParams.get('pairCode')
    if (codeFromUrl && codeFromUrl.trim().length === 6) {
      setAutoPairing(true)
      setPairCode(codeFromUrl.trim())
      doDirectPair(codeFromUrl.trim())
    }
  }, [mode])

  // 2. 6 位数字单向即入配对 (方案 1 扫码直连 + 方案 2 纯 6 位验证码)
  const doDirectPair = async (codeToUse?: string) => {
    const code = (codeToUse || pairCode).trim()
    if (!code || code.length < 6) {
      message.warning('请输入 6 位配对验证码')
      return
    }
    setLoading(true)
    try {
      const res = await pairDirect(code, pairName.trim() || getDefaultDeviceName())
      setSession(res.token, 'user', res.userId)
      message.success('配对成功！已加入同步组')
      // 清除 URL 中的 pairCode 参数，保持地址干净
      window.history.replaceState({}, '', window.location.pathname)
      onLogin()
    } catch (e) {
      setAutoPairing(false)
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  const doAdminLogin = async (values: { username: string; password: string }) => {
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
    <div
      id="clipsync-login-page"
      className="clipsync-login-page"
      style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'linear-gradient(135deg, ' + token.colorPrimaryBg + ' 0%, ' + token.colorBgLayout + ' 100%)',
        padding: 16,
      }}
    >
      <Card
        id="clipsync-login-card"
        className="clipsync-login-card"
        style={{
          width: 420,
          maxWidth: '100%',
          boxShadow: '0 12px 36px rgba(0,0,0,0.08)',
          borderRadius: 20,
          border: '1px solid rgba(0, 0, 0, 0.06)',
        }}
      >
        <div style={{ textAlign: 'center', marginBottom: 24 }}>
          <img
            src="/logo.png"
            alt="NexClip Logo"
            style={{ width: 60, height: 60, borderRadius: 14, objectFit: 'contain' }}
          />
          <Title level={3} style={{ marginTop: 12, marginBottom: 4, fontWeight: 700 }}>
            NexClip
          </Title>
          <Text type="secondary" style={{ fontSize: 13 }}>
            {mode === 'user' ? '多端剪贴板实时协同' : '系统管理控制台'}
          </Text>
        </div>

        {mode === 'user' ? (
          autoPairing ? (
            <div style={{ textAlign: 'center', padding: '32px 0' }}>
              <Spin size="large" />
              <div style={{ marginTop: 20, fontSize: 15, fontWeight: 600, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
                <Smartphone size={18} color={token.colorPrimary} />
                <span>正在通过扫码直连加入同步组...</span>
              </div>
              <Text type="secondary" style={{ fontSize: 13, marginTop: 8, display: 'block' }}>
                验证通过后将自动进入控制台
              </Text>
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
              <div>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
                  <Text strong style={{ fontSize: 14 }}>
                    6 位配对验证码
                  </Text>
                  <Tag color="blue" style={{ margin: 0, borderRadius: 6, fontSize: 11 }}>
                    单向即入 · 5 分钟有效
                  </Tag>
                </div>

                <div style={{ display: 'flex', justifyContent: 'center', margin: '8px 0 14px' }}>
                  <Input
                    placeholder="例如: 839201"
                    size="large"
                    maxLength={6}
                    value={pairCode}
                    onChange={(e) => {
                      const val = e.target.value.replace(/\D/g, '')
                      setPairCode(val)
                      if (val.length === 6) doDirectPair(val)
                    }}
                    style={{
                      fontFamily: 'Consolas, monospace',
                      fontSize: 24,
                      textAlign: 'center',
                      letterSpacing: 8,
                      borderRadius: 12,
                      height: 52,
                      fontWeight: 700,
                    }}
                  />
                </div>
                <div style={{ fontSize: 12, color: '#9CA3AF', textAlign: 'center' }}>
                  在已连接设备点击「添加设备」，输入屏幕显示的 6 位纯数字即可连接
                </div>
              </div>

              <div>
                <Text type="secondary" style={{ fontSize: 12, marginBottom: 6, display: 'block' }}>
                  本设备名称 (选填):
                </Text>
                <Input
                  placeholder="设备名称"
                  size="middle"
                  value={pairName}
                  onChange={(e) => setPairName(e.target.value)}
                  maxLength={32}
                  style={{ borderRadius: 8 }}
                />
              </div>

              <Button
                type="primary"
                block
                size="large"
                loading={loading}
                disabled={pairCode.length !== 6}
                onClick={() => doDirectPair()}
                icon={<ArrowRight size={16} />}
                style={{ borderRadius: 10, height: 44, fontWeight: 600, marginTop: 6 }}
              >
                立即连接并登录
              </Button>
            </div>
          )
        ) : (
          <Form layout="vertical" onFinish={doAdminLogin}>
            <Form.Item name="username" label="用户名" rules={[{ required: true, message: '请输入用户名' }]}>
              <Input
                id="clipsync-login-username"
                prefix={<User size={15} color="#9CA3AF" />}
                size="large"
                autoComplete="username"
                style={{ borderRadius: 10 }}
              />
            </Form.Item>
            <Form.Item name="password" label="密码" rules={[{ required: true, message: '请输入密码' }]}>
              <Input.Password
                id="clipsync-login-password"
                prefix={<Lock size={15} color="#9CA3AF" />}
                size="large"
                autoComplete="current-password"
                style={{ borderRadius: 10 }}
              />
            </Form.Item>
            <Button
              type="primary"
              htmlType="submit"
              block
              size="large"
              loading={loading}
              style={{ borderRadius: 10, height: 44, fontWeight: 600, marginTop: 10 }}
            >
              登录管理员台
            </Button>
          </Form>
        )}
      </Card>
    </div>
  )
}
