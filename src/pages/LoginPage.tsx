import React, { useEffect, useRef, useState } from 'react'
import { Card, Form, Input, Button, message, Typography, theme, Spin, Tag, Grid } from 'antd'
import {
  IconUser as User,
  IconLock as Lock,
  IconArrowRight as ArrowRight,
  IconDeviceMobile as Smartphone,
  IconCopy,
  IconCloudUpload,
  IconHistory,
  IconUsers,
  IconSettings,
  IconWorld,
  IconShieldCheck,
} from '@tabler/icons-react'
import { login as apiLogin, pairDirect, setSession, getDefaultDeviceName } from '../api'

const { Title, Text } = Typography

/** mode: 'user' = /index/login(6位码/扫码直连); 'admin' = /pro/login(账密) */
export default function LoginPage({ mode, onLogin }: { mode: 'user' | 'admin'; onLogin: () => void }) {
  const { token } = theme.useToken()
  const screens = Grid.useBreakpoint()
  const isMobile = screens.lg === false
  const [loading, setLoading] = useState(false)
  const [autoPairing, setAutoPairing] = useState(false)

  const [digits, setDigits] = useState<string[]>(Array(6).fill(''))
  const boxRefs = useRef<Array<React.ComponentRef<typeof Input> | null>>([])
  const [pairName, setPairName] = useState(getDefaultDeviceName())
  const pairCode = digits.join('')

  // 1. 扫码直连捕获: 检测 URL 中是否存在 pairCode 参数(路由守卫已保留查询串)
  useEffect(() => {
    if (mode !== 'user') return
    const urlParams = new URLSearchParams(window.location.search)
    const codeFromUrl = urlParams.get('pairCode')
    if (codeFromUrl && codeFromUrl.trim().length === 6) {
      setAutoPairing(true)
      setDigits(codeFromUrl.trim().split(''))
      doDirectPair(codeFromUrl.trim())
    }
  }, [mode])

  // 2. 6 位数字单向即入配对 (方案 1 扫码直连 + 方案 2 纯 6 位验证码)
  const doDirectPair = async (codeToUse?: string) => {
    const code = (codeToUse ?? pairCode).trim()
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

  /** 分格输入:从 start 起 连续填入(支持整段粘贴),6 位满自动提交 */
  const fillFrom = (start: number, raw: string) => {
    const chars = raw.replace(/\D/g, '').split('')
    if (chars.length === 0) return
    const next = [...digits]
    let i = start
    for (const c of chars) {
      if (i > 5) break
      next[i] = c
      i++
    }
    setDigits(next)
    boxRefs.current[Math.min(i, 5)]?.focus()
    const code = next.join('')
    if (!next.includes('')) doDirectPair(code)
  }

  const features = mode === 'user'
    ? [
        { icon: <IconCopy size={15} />, title: '多端剪贴板实时同步', desc: '手机 / 电脑复制,全端即时可达' },
        { icon: <IconCloudUpload size={15} />, title: '文件互传即传即达', desc: '选目标设备,进度可见秒级送达' },
        { icon: <IconHistory size={15} />, title: '历史记录随时回溯', desc: '文本 / 代码 / 图片,分类检索' },
      ]
    : [
        { icon: <IconUsers size={15} />, title: '用户与设备管理', desc: '配对解绑、用户组一目了然' },
        { icon: <IconHistory size={15} />, title: '审计与同步记录', desc: '每一次推送都有迹可循' },
        { icon: <IconSettings size={15} />, title: '服务配置在线可调', desc: '参数热更新,无需重启' },
      ]

  const steps = mode === 'user'
    ? ['在已连接设备打开「设置 & 设备」,生成配对码', '在此输入 6 位验证码,或手机扫码直达本页', '连接成功,剪贴板即刻开始同步']
    : []

  const iconChip = (icon: React.ReactNode) => (
    <span
      style={{
        width: 28, height: 28, borderRadius: 8, flexShrink: 0,
        background: 'rgba(255, 255, 255, 0.18)', color: '#fff',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      {icon}
    </span>
  )

  // ===== 表单内容(user 配对 / admin 账密),桌面右栏与移动主卡共用 =====
  const formBlock = mode === 'user' ? (
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
      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <div>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 10, flexWrap: 'wrap', gap: 6 }}>
            <Text strong style={{ fontSize: 14 }}>
              6 位配对验证码
            </Text>
            <Tag color="blue" style={{ margin: 0, borderRadius: 6, fontSize: 11 }}>
              单向即入 · 10 分钟有效
            </Tag>
          </div>

          {/* 6 位分格输入:自动跳格 / 粘贴整段 / 回删回退 */}
          <div style={{ display: 'flex', gap: 8, justifyContent: 'center' }}>
            {digits.map((d, i) => (
              <Input
                key={i}
                ref={el => { boxRefs.current[i] = el }}
                value={d}
                inputMode="numeric"
                maxLength={6}
                onChange={e => {
                  const v = e.target.value
                  if (!v) {
                    setDigits(arr => arr.map((x, j) => (j === i ? '' : x)))
                    return
                  }
                  fillFrom(i, v)
                }}
                onKeyDown={e => {
                  if (e.key === 'Backspace' && !digits[i] && i > 0) {
                    boxRefs.current[i - 1]?.focus()
                    setDigits(arr => arr.map((x, j) => (j === i - 1 ? '' : x)))
                  }
                }}
                style={{
                  width: isMobile ? 42 : 46,
                  height: 52,
                  textAlign: 'center',
                  fontSize: 22,
                  fontWeight: 700,
                  fontFamily: 'Consolas, monospace',
                  borderRadius: 10,
                  padding: 0,
                }}
              />
            ))}
          </div>
          <div style={{ fontSize: 12, color: '#9CA3AF', textAlign: 'center', marginTop: 10 }}>
            输满 6 位自动连接 · 也可直接粘贴整段验证码
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
            onChange={e => setPairName(e.target.value)}
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
          style={{ borderRadius: 10, height: 44, fontWeight: 600, marginTop: 4 }}
        >
          立即连接并登录
        </Button>

        <div style={{ fontSize: 12, color: '#9CA3AF', textAlign: 'center' }}>
          在已连接设备点击「添加设备」,输入屏幕显示的 6 位纯数字即可连接
        </div>
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
  )

  // ===== 移动端:轻量品牌头 + 主卡 + 特性胶囊,无大色块 =====
  if (isMobile) {
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
        <div style={{ width: '100%', maxWidth: 420, display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div style={{ textAlign: 'center', padding: '4px 0 2px' }}>
            <img
              src="/logo.png"
              alt="NexClip Logo"
              style={{ width: 44, height: 44, borderRadius: 12, objectFit: 'contain' }}
            />
            <Title level={4} style={{ margin: '8px 0 2px', fontWeight: 700 }}>
              NexClip
            </Title>
            <Text type="secondary" style={{ fontSize: 12 }}>
              {mode === 'user' ? '多端剪贴板实时协同' : '系统管理控制台'}
            </Text>
          </div>

          <Card
            id="clipsync-login-card"
            className="clipsync-login-card"
            styles={{ body: { padding: '22px 20px' } }}
            style={{ borderRadius: 20, boxShadow: '0 12px 36px rgba(0,0,0,0.08)', border: '1px solid rgba(0, 0, 0, 0.06)' }}
          >
            {formBlock}
          </Card>

          <div style={{ display: 'flex', gap: 8, justifyContent: 'center', flexWrap: 'wrap' }}>
            {features.map(f => (
              <span
                key={f.title}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 5,
                  background: 'rgba(0,0,0,0.04)', borderRadius: 999, padding: '5px 10px',
                  fontSize: 11, color: '#4B5563',
                }}
              >
                <span style={{ color: token.colorPrimary, display: 'inline-flex' }}>{f.icon}</span>
                {f.title}
              </span>
            ))}
          </div>

          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8, fontSize: 12, color: '#9CA3AF' }}>
            <a href="/" style={{ color: '#6B7280', display: 'inline-flex', alignItems: 'center', gap: 4 }}>
              <IconWorld size={13} /> 返回官网
            </a>
            <span>·</span>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
              <IconShieldCheck size={13} /> 仅用户组内可见
            </span>
          </div>
        </div>
      </div>
    )
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
      <div
        style={{
          display: 'flex',
          flexDirection: 'row',
          alignItems: 'stretch',
          width: '100%',
          maxWidth: 960,
          margin: '0 auto',
          boxShadow: '0 12px 36px rgba(0,0,0,0.08)',
          borderRadius: 20,
          overflow: 'hidden',
          border: '1px solid rgba(0, 0, 0, 0.06)',
          background: token.colorBgContainer,
        }}
      >
        {/* ===== 左:品牌 + 特性 + 步骤 ===== */}
        <div
          style={{
            width: 400,
            padding: '36px 32px',
            background: 'linear-gradient(160deg, ' + token.colorPrimary + ' 0%, #1E40AF 100%)',
            color: '#fff',
            display: 'flex',
            flexDirection: 'column',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <img
              src="/logo.png"
              alt="NexClip Logo"
              style={{ width: 46, height: 46, borderRadius: 12, objectFit: 'contain', background: 'rgba(255,255,255,0.18)', padding: 5 }}
            />
            <div>
              <Title level={4} style={{ color: '#fff', margin: 0, fontWeight: 700 }}>
                NexClip
              </Title>
              <div style={{ fontSize: 12, color: 'rgba(255,255,255,0.8)' }}>
                {mode === 'user' ? '多端剪贴板实时协同' : '系统管理控制台'}
              </div>
            </div>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, marginTop: 30 }}>
            {features.map(f => (
              <div key={f.title} style={{ display: 'flex', alignItems: 'flex-start', gap: 10 }}>
                {iconChip(f.icon)}
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontWeight: 600, fontSize: 13 }}>{f.title}</div>
                  <div style={{ fontSize: 11, color: 'rgba(255,255,255,0.75)', marginTop: 1 }}>{f.desc}</div>
                </div>
              </div>
            ))}
          </div>

          {steps.length > 0 && (
            <div
              style={{
                marginTop: 30,
                paddingTop: 18,
                borderTop: '1px solid rgba(255,255,255,0.18)',
                display: 'flex',
                flexDirection: 'column',
                gap: 10,
              }}
            >
              {steps.map((s, i) => (
                <div key={i} style={{ display: 'flex', alignItems: 'flex-start', gap: 10 }}>
                  <span
                    style={{
                      width: 20, height: 20, borderRadius: '50%', flexShrink: 0,
                      background: 'rgba(255,255,255,0.2)', fontSize: 11, fontWeight: 700,
                      display: 'flex', alignItems: 'center', justifyContent: 'center',
                    }}
                  >
                    {i + 1}
                  </span>
                  <span style={{ fontSize: 12, color: 'rgba(255,255,255,0.88)', lineHeight: '20px' }}>{s}</span>
                </div>
              ))}
            </div>
          )}

          <div style={{ marginTop: 'auto', paddingTop: 18, display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <a href="/" style={{ color: 'rgba(255,255,255,0.8)', fontSize: 12, display: 'inline-flex', alignItems: 'center', gap: 4 }}>
              <IconWorld size={13} /> 返回官网
            </a>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 12, color: 'rgba(255,255,255,0.8)' }}>
              <IconShieldCheck size={13} /> 仅用户组内可见
            </span>
          </div>
        </div>

        {/* ===== 右:操作卡 ===== */}
        <Card
          id="clipsync-login-card"
          className="clipsync-login-card"
          styles={{ body: { padding: '32px 30px', display: 'flex', flexDirection: 'column', justifyContent: 'center', flex: 1 } }}
          style={{ flex: 1, border: 'none', boxShadow: 'none' }}
        >
          {formBlock}
        </Card>
      </div>
    </div>
  )
}
