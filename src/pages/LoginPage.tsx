import { useEffect, useRef, useState } from 'react'
import { Card, Form, Input, Button, message, Typography, theme, Spin, Tag } from 'antd'
import { Cloud, User, Lock, Link2 } from 'lucide-react'
import { login as apiLogin, pairDevice, pairStatus, createPairSession, setSession } from '../api'

const { Title } = Typography

/** mode: 'user' = /index/login(配对码);'admin' = /pro/login(账密) */
export default function LoginPage({ mode, onLogin }: { mode: 'user' | 'admin'; onLogin: () => void }) {
  const { token } = theme.useToken()
  const [loading, setLoading] = useState(false)

  const [pairCode, setPairCode] = useState('')
  const [pairUid, setPairUid] = useState('')
  const [pairName, setPairName] = useState('Web 浏览器')
  const [pairState, setPairState] = useState<'idle' | 'waiting' | 'approved' | 'rejected' | 'expired'>('idle')
  const pollRef = useRef<number | null>(null)

  const stopPoll = () => {
    if (pollRef.current !== null) { clearInterval(pollRef.current); pollRef.current = null }
  }
  useEffect(() => stopPoll, [])

  const doPair = async () => {
    const code = pairCode.trim().toUpperCase()
    const uid = pairUid.trim()
    if (!code || !uid) { message.warning('请输入配对码与用户ID'); return }
    setLoading(true)
    try {
      await pairDevice(code, uid, pairName.trim() || 'Web 浏览器')
      setPairState('waiting')
      pollRef.current = window.setInterval(async () => {
        try {
          const st = await pairStatus(code)
          if (st.status === 'approved') {
            stopPoll()
            setPairState('approved')
            const sess = await createPairSession(code)
            setSession(sess.token, 'user', sess.userId)
            message.success('配对成功')
            onLogin()
          } else if (st.status === 'rejected') {
            stopPoll(); setPairState('rejected')
          } else if (st.status === 'expired') {
            stopPoll(); setPairState('expired')
          }
        } catch { /* 轮询失败重试 */ }
      }, 3000)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  const doAdminLogin = async (values: { username: string; password: string }) => {
    setLoading(true)
    try {
      const ok = await apiLogin(values.username.trim(), values.password)
      if (ok) { message.success('登录成功'); onLogin() }
      else message.error('用户名或密码错误')
    } finally { setLoading(false) }
  }

  return (
    <div id="clipsync-login-page" className="clipsync-login-page" style={{
      minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center',
      background: 'linear-gradient(135deg, ' + token.colorPrimaryBg + ' 0%, ' + token.colorBgLayout + ' 100%)',
    }}>
      <Card id="clipsync-login-card" className="clipsync-login-card" style={{ width: 400, boxShadow: '0 8px 30px rgba(0,0,0,0.08)', borderRadius: 16 }}>
        <div style={{ textAlign: 'center', marginBottom: 22 }}>
          <Cloud size={44} color="#2563EB" />
          <Title level={3} style={{ marginTop: 10, marginBottom: 0 }}>ClipSync</Title>
        </div>

        {mode === 'user' ? (
          pairState === 'waiting' || pairState === 'approved' ? (
            <div style={{ textAlign: 'center', padding: '26px 0' }}>
              <Spin size="large" />
              {pairState === 'approved' ? null : (
                <Button type="link" size="small" style={{ marginTop: 12, display: 'block', marginLeft: 'auto', marginRight: 'auto' }} onClick={() => { stopPoll(); setPairState('idle') }}>
                  取消
                </Button>
              )}
            </div>
          ) : pairState === 'rejected' || pairState === 'expired' ? (
            <div style={{ textAlign: 'center', padding: '18px 0' }}>
              <Tag color={pairState === 'rejected' ? 'red' : 'orange'}>{pairState === 'rejected' ? '配对被拒绝' : '配对码已过期'}</Tag>
              <div style={{ marginTop: 10 }}>
                <Button onClick={() => setPairState('idle')}>重新配对</Button>
              </div>
            </div>
          ) : (
            <Form layout="vertical" onFinish={doPair}>
              <Form.Item label="配对码" required style={{ marginBottom: 14 }}>
                <Input
                  placeholder="配对码" size="large" maxLength={8} value={pairCode}
                  onChange={e => setPairCode(e.target.value.toUpperCase())}
                  style={{ fontFamily: 'Consolas, monospace', letterSpacing: 3 }}
                />
              </Form.Item>
              <Form.Item label="用户ID" required style={{ marginBottom: 14 }}>
                <Input
                  placeholder="用户ID" size="large" value={pairUid}
                  onChange={e => setPairUid(e.target.value.trim())}
                  style={{ fontFamily: 'Consolas, monospace', letterSpacing: 2 }}
                />
              </Form.Item>
              <Form.Item label="设备名称" style={{ marginBottom: 18 }}>
                <Input placeholder="设备名称" size="large" value={pairName} onChange={e => setPairName(e.target.value)} maxLength={32} />
              </Form.Item>
              <Button type="primary" htmlType="submit" block size="large" loading={loading} icon={<Link2 size={16} />}>
                提交
              </Button>
            </Form>
          )
        ) : (
          <Form layout="vertical" onFinish={doAdminLogin}>
            <Form.Item name="username" label="用户名" rules={[{ required: true, message: '请输入用户名' }]}>
              <Input id="clipsync-login-username" prefix={<User size={15} color="#9CA3AF" />} size="large" autoComplete="username" />
            </Form.Item>
            <Form.Item name="password" label="密码" rules={[{ required: true, message: '请输入密码' }]}>
              <Input.Password id="clipsync-login-password" prefix={<Lock size={15} color="#9CA3AF" />} size="large" autoComplete="current-password" />
            </Form.Item>
            <Button id="clipsync-login-submit" type="primary" htmlType="submit" block size="large" loading={loading}>
              登录
            </Button>
          </Form>
        )}
      </Card>
    </div>
  )
}
