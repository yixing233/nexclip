import { useEffect, useMemo, useRef, useState } from 'react'
import { BrowserRouter, Routes, Route, Navigate, useParams, useNavigate } from 'react-router-dom'
import {
  Layout, Menu, Button, Input, Avatar, Dropdown, Badge, Tooltip, message, theme as antdTheme, ConfigProvider,
} from 'antd'
import {
  Home, FileText, Monitor, Clock, Settings, RefreshCw, Search, LogOut, Cloud, Users,
} from 'lucide-react'
import type { MenuProps } from 'antd'
import LoginPage from './pages/LoginPage'
import OverviewPage from './pages/OverviewPage'
import ClipboardPage from './pages/ClipboardPage'
import DevicesPage from './pages/DevicesPage'
import SyncRecordsPage from './pages/SyncRecordsPage'
import SettingsPage from './pages/SettingsPage'
import UsersPage from './pages/UsersPage'
import UserPage from './pages/UserPage'
import { connectHub, disconnectHub } from './hub'
import { getThemeMode, logout as apiLogout, deviceId, getRole, getMe, getHealth } from './api'
import { addIncoming } from './chatStore'

const { Header, Sider, Content } = Layout

type PageKey = 'overview' | 'clipboard' | 'devices' | 'records' | 'users' | 'settings'
const PAGE_KEYS: PageKey[] = ['overview', 'clipboard', 'devices', 'records', 'users', 'settings']

/** 两套主题的语义色板:所有自定义颜色统一从这里取,保证明暗模式协调一致 */
const LIGHT = {
  layout: '#F3F4F6',
  surface: '#FFFFFF',
  surfaceGlass: 'rgba(255, 255, 255, 0.72)',
  elevated: '#FFFFFF',
  border: '#E5E7EB',
  borderMuted: '#F3F4F6',
  primary: '#2563EB',
  success: '#10B981',
  danger: '#EF4444',
  text: '#1F2937',
  textSecondary: '#4B5563',
  textTertiary: '#9CA3AF',
  menuHover: '#F3F4F6',
  menuSelected: '#EFF6FF',
  menuSelectedText: '#2563EB',
  serviceBg: '#F0FDF4',
  serviceBorder: '#BBF7D0',
  serviceText: '#15803D',
  avatarBg: '#E5E7EB',
  avatarText: '#4B5563',
}
// GitHub Primer Dark 官方配色(https://primer.style/foundations/color · github-vscode-theme 同款)
const DARK = {
  layout: '#0D1117',        // bgColor-canvas-default:页面底色
  surface: '#161B22',       // bgColor-canvas-subtle:侧栏/顶栏/卡片同面
  surfaceGlass: 'rgba(22, 27, 34, 0.72)',
  elevated: '#1C2128',      // bgColor-canvas-overlay:下拉/弹层
  border: '#30363D',        // borderColor-default
  borderMuted: '#21262D',   // borderColor-muted
  text: '#E6EDF3',          // fgColor-default
  textSecondary: '#8B949E', // fgColor-muted
  textTertiary: '#6E7681',  // fgColor-subtle
  primary: '#4493F8',       // accent
  success: '#3FB950',       // success
  danger: '#F85149',        // danger
  menuHover: '#21262D',
  menuSelected: 'rgba(68, 147, 248, 0.18)',
  menuSelectedText: '#F0F6FC',
  serviceBg: 'rgba(63, 185, 80, 0.12)',
  serviceBorder: 'rgba(63, 185, 80, 0.40)',
  serviceText: '#3FB950',
  avatarBg: '#21262D',
  avatarText: '#C9D1D9',
}

interface AdminConsoleProps {
  refreshTick: number
  onRefresh: () => void
  userFilter: string | null
  onUserFilterChange: (v: string | null) => void
  statusBadge: React.ReactNode
  isDark: boolean
  c: typeof LIGHT
  themeMode: 'light' | 'dark' | 'system'
  onThemeChange: (m: 'light' | 'dark' | 'system') => void
  onLogout: () => void
}

/** 管理台主框架(/pro/:page),页面进 URL:可刷新/深链/前进后退 */
function AdminConsole({
  refreshTick, onRefresh, userFilter, onUserFilterChange, statusBadge, isDark, c, themeMode, onThemeChange, onLogout,
}: AdminConsoleProps) {
  const navigate = useNavigate()
  const { pageKey } = useParams()
  const page: PageKey = PAGE_KEYS.includes(pageKey as PageKey) ? (pageKey as PageKey) : 'overview'
  const contentRef = useRef<HTMLDivElement>(null)
  const scrollPositions = useRef<Record<string, number>>({})
  const [search, setSearch] = useState('')
  const [username, setUsername] = useState<string | null>(null)
  const [version, setVersion] = useState('')
  const [serviceOk, setServiceOk] = useState<boolean | null>(null)

  // 会话信息:侧栏展示真实登录用户名
  useEffect(() => {
    getMe().then(m => setUsername(m.username)).catch(() => {})
  }, [])

  // 服务健康 + 版本:进入与每次手动刷新时探测(不再写死"运行正常")
  useEffect(() => {
    getHealth().then(h => {
      setServiceOk(true)
      if (h.version) setVersion(h.version)
    }).catch(() => setServiceOk(false))
  }, [refreshTick])

  /** 切页:先保存当前页滚动位置,再改路由;useEffect 恢复目标页自己的位置 */
  const go = (key: PageKey) => {
    if (key === page) return
    if (contentRef.current) scrollPositions.current[page] = contentRef.current.scrollTop
    navigate('/pro/' + key)
  }

  useEffect(() => {
    if (contentRef.current) contentRef.current.scrollTop = scrollPositions.current[page] ?? 0
  }, [page])

  /** 顶栏搜索:输入即跳剪贴板页,并作为内容关键字过滤(服务端 LIKE) */
  const onSearchInput = (v: string) => {
    setSearch(v)
    if (v && page !== 'clipboard') go('clipboard')
  }

  const menuItems: MenuProps['items'] = [
    { key: 'overview', icon: <Home size={16} />, label: '总览' },
    { key: 'clipboard', icon: <FileText size={16} />, label: '剪贴板' },
    { key: 'devices', icon: <Monitor size={16} />, label: '设备管理' },
    { key: 'records', icon: <Clock size={16} />, label: '同步记录' },
    { key: 'users', icon: <Users size={16} />, label: '用户管理' },
    { key: 'settings', icon: <Settings size={16} />, label: '设置' },
  ]

  const pageEl = useMemo(() => {
    switch (page) {
      case 'overview': return <OverviewPage refreshTick={refreshTick} />
      case 'clipboard': return <ClipboardPage refreshTick={refreshTick} userFilter={userFilter} onUserFilterChange={onUserFilterChange} search={search} />
      case 'devices': return <DevicesPage refreshTick={refreshTick} userFilter={userFilter} onUserFilterChange={onUserFilterChange} />
      case 'records': return <SyncRecordsPage refreshTick={refreshTick} userFilter={userFilter} onUserFilterChange={onUserFilterChange} />
      case 'users': return <UsersPage onViewUser={(uid) => { onUserFilterChange(uid); go('clipboard') }} />
      default: return <SettingsPage onThemeChange={onThemeChange} themeMode={themeMode} onRefresh={onRefresh} />
    }
  }, [page, refreshTick, themeMode, userFilter, search]) // eslint-disable-line

  return (
    <Layout id="clipsync-app" className="clipsync-app" style={{ height: '100vh' }}>
      <Sider
        id="clipsync-sidebar"
        className="clipsync-sidebar"
        width={216}
        theme={isDark ? 'dark' : 'light'}
        style={{ borderRight: '1px solid ' + c.border, position: 'relative' }}
      >
        <div id="clipsync-sidebar-logo" className="clipsync-sidebar-logo" style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '18px 20px' }}>
          <Cloud size={26} color="#2563EB" />
          <span style={{ fontSize: 18, fontWeight: 700, color: c.text }}>ClipSync</span>
        </div>
        <Menu
          id="clipsync-sidebar-menu"
          className="clipsync-sidebar-menu"
          mode="inline"
          selectedKeys={[page]}
          items={menuItems}
          onClick={({ key }) => go(key as PageKey)}
          style={{ borderInlineEnd: 'none' }}
        />
        <div id="clipsync-sidebar-footer" className="clipsync-sidebar-footer" style={{ position: 'absolute', bottom: 16, left: 16, right: 16 }}>
          <div
            id="clipsync-service-status"
            className="clipsync-service-status"
            style={{
              background: serviceOk === false ? 'rgba(239, 68, 68, 0.10)' : c.serviceBg,
              border: '1px solid ' + (serviceOk === false ? 'rgba(239, 68, 68, 0.40)' : c.serviceBorder),
              borderRadius: 12, padding: '10px 12px', marginBottom: 12,
            }}
          >
            <div style={{ color: serviceOk === false ? c.danger : c.serviceText, fontWeight: 600, fontSize: 13 }}>
              <Badge status={serviceOk === false ? 'error' : 'success'} /> {serviceOk === false ? '服务不可用' : '服务运行中'}
            </div>
            <div style={{ color: c.textTertiary, fontSize: 12, marginTop: 2 }}>版本 {version || '…'}</div>
          </div>
          <Dropdown
            menu={{
              items: [{ key: 'logout', icon: <LogOut size={16} />, label: '退出登录', onClick: onLogout }],
            }}
          >
            <div id="clipsync-user-info" className="clipsync-user-info" style={{ display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer', padding: '6px 4px' }}>
              <Avatar size={34} style={{ background: c.avatarBg, color: c.avatarText }}>{(username ?? 'A').slice(0, 1).toUpperCase()}</Avatar>
              <div>
                <div style={{ fontWeight: 600, color: c.text, fontSize: 13 }}>{username ?? '管理员'}</div>
                <div style={{ color: c.textTertiary, fontSize: 12 }}>管理员</div>
              </div>
            </div>
          </Dropdown>
        </div>
      </Sider>
      <Layout style={{ position: 'relative' }}>
        <Header
          id="clipsync-header"
          className="clipsync-header"
          style={{
            background: c.surfaceGlass, backdropFilter: 'blur(12px)', WebkitBackdropFilter: 'blur(12px)',
            position: 'absolute', top: 0, left: 0, right: 0, zIndex: 20,
            padding: '0 24px', borderBottom: '1px solid ' + c.border,
            height: 64, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 16,
          }}
        >
          <div id="clipsync-header-title" className="clipsync-header-title" style={{ flex: 1 }}>
            <div style={{ fontSize: 18, fontWeight: 700, color: c.text }}>剪贴板共享服务端</div>
          </div>
          {serviceOk === false ? (
            <span style={{ color: c.danger, fontWeight: 600, fontSize: 13 }}><Badge status="error" /> 服务异常</span>
          ) : serviceOk === null ? (
            <span style={{ color: c.textTertiary, fontWeight: 600, fontSize: 13 }}><Badge status="default" /> 检测中</span>
          ) : (
            <span id="clipsync-header-server-status" className="clipsync-header-server-status" style={{ color: c.success, fontWeight: 600, fontSize: 13 }}>
              <Badge status="success" /> 服务运行正常
            </span>
          )}
          {statusBadge}
          <Input
            id="clipsync-header-search"
            className="clipsync-header-search"
            prefix={<Search size={16} color="#9CA3AF" />}
            placeholder="搜索剪贴板内容"
            allowClear
            style={{ width: 220 }}
            value={search}
            onChange={e => onSearchInput(e.target.value)}
          />
          <Tooltip title="刷新全部数据">
            <Button id="clipsync-header-refresh" className="clipsync-header-refresh" icon={<RefreshCw size={16} />} onClick={onRefresh}>刷新</Button>
          </Tooltip>
          <Tooltip title="服务设置">
            <Button id="clipsync-header-settings" className="clipsync-header-settings" icon={<Settings size={16} />} onClick={() => go('settings')} />
          </Tooltip>
        </Header>
        <Content id="clipsync-content" className="clipsync-content" ref={contentRef} style={{ overflow: 'auto', height: '100%', padding: '88px 24px 24px' }}>
          {pageEl}
        </Content>
      </Layout>
    </Layout>
  )
}

export default function App() {
  const [authed, setAuthed] = useState<boolean>(() => !!localStorage.getItem('clipsync_token'))
  const [hubStatus, setHubStatus] = useState<'connected' | 'reconnecting' | 'disconnected'>('disconnected')
  const [refreshTick, setRefreshTick] = useState(0)
  const [themeMode, setThemeModeState] = useState<'light' | 'dark' | 'system'>(getThemeMode())
  const [userFilter, setUserFilter] = useState<string | null>(null)

  /** 手动刷新:驱动全部页面的 refreshTick 重新拉取 */
  const refreshAll = () => setRefreshTick(t => t + 1)

  // 令牌失效(被吊销/过期):强制回到登录页
  useEffect(() => {
    const onUnauthorized = () => {
      setAuthed(false)
      message.warning('登录已失效,请重新登录')
    }
    window.addEventListener('clipsync:unauthorized', onUnauthorized)
    return () => window.removeEventListener('clipsync:unauthorized', onUnauthorized)
  }, [])

  // hub:任一角色登录后连接(设备变更/剪贴板实时刷新;用户会话连接已被服务端标记剪贴板静默)
  useEffect(() => {
    if (!authed) return
    connectHub({
      onClipboardUpdated: (entry) => {
        const e = entry as { text?: string; deviceName?: string; deviceId?: string } | null
        if (e?.deviceId && e.deviceId === deviceId()) {
          setRefreshTick(t => t + 1)
          return
        }
        addIncoming(e?.deviceName || '其他设备', e?.text || '[收到新剪贴板]')
        message.info('收到新剪贴板推送')
        setRefreshTick(t => t + 1)
      },
      onClipboardCleared: () => {
        message.info('剪贴板历史已清空')
        setRefreshTick(t => t + 1)
      },
      onDevicesChanged: () => {
        setRefreshTick(t => t + 1)
      },
      onStatusChange: (s) => {
        if (s === 'connected' || s === 'reconnecting' || s === 'disconnected') {
          setHubStatus(s)
        }
      },
    })
    return () => disconnectHub()
  }, [authed])

  const logout = () => {
    apiLogout()
    setAuthed(false)
  }

  const role = getRole()

  const isDark = themeMode === 'dark' || (themeMode === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches)
  const c = isDark ? DARK : LIGHT

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light')
  }, [isDark])

  const statusBadge =
    hubStatus === 'connected' ? <Badge status="success" text="实时连接" /> :
    hubStatus === 'reconnecting' ? <Badge status="warning" text="重连中" /> :
    <Badge status="default" text="未连接" />

  return (
    <ConfigProvider
      theme={{
        algorithm: isDark ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
        token: {
          colorPrimary: isDark ? c.primary : '#2563EB',
          colorSuccess: isDark ? c.success : '#10B981',
          colorError: isDark ? c.danger : '#EF4444',
          colorBgLayout: c.layout,
          colorText: c.text,
          colorTextSecondary: c.textSecondary,
          colorTextTertiary: c.textTertiary,
          ...(isDark ? {
            colorBgContainer: c.surface,
            colorBgElevated: c.elevated,
            colorBgSpotlight: c.elevated,
            colorBorder: c.border,
            colorBorderSecondary: c.borderMuted,
          } : {}),
          borderRadius: 10,
          fontSize: 14,
        },
        components: {
          Layout: {
            siderBg: c.surface,
            headerBg: c.surface,
            bodyBg: c.layout,
          },
          Menu: isDark ? {
            darkItemBg: c.surface,
            darkItemColor: c.textSecondary,
            darkItemHoverBg: c.menuHover,
            darkItemHoverColor: c.text,
            darkItemSelectedBg: c.menuSelected,
            darkItemSelectedColor: c.menuSelectedText,
            darkGroupTitleColor: c.textTertiary,
            darkSubMenuItemBg: c.surface,
          } : {
            itemHoverBg: c.menuHover,
            itemSelectedBg: c.menuSelected,
            itemSelectedColor: c.menuSelectedText,
          },
        },
      }}
    >
      <BrowserRouter>
        <Routes>
          <Route path="/" element={<Navigate to="/index" replace />} />
          {/* 用户端:/index(已登录=用户页;未登录跳 /index/login) */}
          <Route path="/index" element={
            !authed ? (
              <Navigate to="/index/login" replace />
            ) : role === 'user' ? (
              <div style={{ minHeight: '100vh', background: '#F3F4F6', padding: 24 }}>
                <UserPage refreshTick={refreshTick} onLogout={logout} />
              </div>
            ) : (
              <Navigate to="/pro/overview" replace />
            )
          } />
          {/* 用户端登录页 */}
          <Route path="/index/login" element={
            !authed ? (
              <LoginPage mode="user" onLogin={() => setAuthed(true)} />
            ) : role === 'user' ? (
              <Navigate to="/index" replace />
            ) : (
              <Navigate to="/pro/overview" replace />
            )
          } />
          {/* 管理端:/pro → /pro/overview(页面进 URL,支持刷新/深链/后退) */}
          <Route path="/pro" element={<Navigate to="/pro/overview" replace />} />
          <Route path="/pro/:pageKey" element={
            !authed ? (
              <Navigate to="/pro/login" replace />
            ) : role === 'user' ? (
              <Navigate to="/index" replace />
            ) : (
              <AdminConsole
                refreshTick={refreshTick}
                onRefresh={refreshAll}
                userFilter={userFilter}
                onUserFilterChange={setUserFilter}
                statusBadge={statusBadge}
                isDark={isDark}
                c={c}
                themeMode={themeMode}
                onThemeChange={setThemeModeState}
                onLogout={logout}
              />
            )
          } />
          {/* 管理端登录页 */}
          <Route path="/pro/login" element={
            !authed ? (
              <LoginPage mode="admin" onLogin={() => setAuthed(true)} />
            ) : role === 'user' ? (
              <Navigate to="/index" replace />
            ) : (
              <Navigate to="/pro/overview" replace />
            )
          } />
          <Route path="*" element={<Navigate to="/index" replace />} />
        </Routes>
      </BrowserRouter>
    </ConfigProvider>
  )
}
