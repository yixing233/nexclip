import React, { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { Button, Tooltip, message, Dropdown, type MenuProps } from 'antd'
import {
  Monitor,
  Smartphone,
  Globe,
  Download,
  ArrowRight,
  Sun,
  Moon,
  Check,
  ShieldCheck,
  Zap,
  Image as ImageIcon,
  KeyRound,
  ExternalLink,
  LogIn,
  Sliders,
  Sparkles,
  Command,
  Copy,
  Server,
  ChevronDown,
} from 'lucide-react'

interface ReleaseItem {
  filename: string
  size: string
  serverUrl: string
  githubUrl: string
  sha256: string
}

interface ReleaseData {
  version: string
  windows: ReleaseItem
  android: ReleaseItem
}

const defaultReleaseInfo: ReleaseData = {
  version: 'v20260901.01',
  windows: {
    filename: 'NexClip_Setup_v20260901.01_x64.exe',
    size: '18.3 MB',
    serverUrl: '/releases/NexClip_Setup_v20260901.01_x64.exe',
    githubUrl: 'https://github.com/yixing233/nexclip/releases/download/v20260901.01/NexClip_Setup_v20260901.01_x64.exe',
    sha256: '8fb9e32c586a7538a7bad5d93b386afa9f25f0795f77d236b55cbfb1930bb4ad',
  },
  android: {
    filename: 'NexClip_v20260901.01_Android.apk',
    size: '15.3 MB',
    serverUrl: '/releases/NexClip_v20260901.01_Android.apk',
    githubUrl: 'https://github.com/yixing233/nexclip/releases/download/v20260901.01/NexClip_v20260901.01_Android.apk',
    sha256: 'a8b42c9776c4129dca043c641fcbd5631d3af0c5257f5d6d7811a075c2289083',
  },
}

interface LandingPageProps {
  isDark: boolean
  onToggleTheme: () => void
  c: {
    layout: string
    surface: string
    surfaceGlass: string
    border: string
    primary: string
    success: string
    text: string
    textSecondary: string
    textTertiary: string
  }
}

export default function LandingPage({ isDark, onToggleTheme, c }: LandingPageProps) {
  const navigate = useNavigate()
  const [releaseInfo, setReleaseInfo] = useState<ReleaseData>(defaultReleaseInfo)
  const [isScrolled, setIsScrolled] = useState(false)
  const [windowWidth, setWindowWidth] = useState(
    typeof window !== 'undefined' ? window.innerWidth : 1200
  )

  useEffect(() => {
    // 动态拉取服务端 /releases/version.json 实现版本自动同步
    fetch('/releases/version.json')
      .then((res) => {
        if (res.ok) return res.json()
        throw new Error('Failed to load version.json')
      })
      .then((data) => {
        if (data && data.version && data.windows && data.android) {
          const winBytes = data.windows.file_size_bytes || 0
          const winSizeStr = winBytes > 0 ? `${(winBytes / (1024 * 1024)).toFixed(1)} MB` : '18.3 MB'
          const apkBytes = data.android.file_size_bytes || 0
          const apkSizeStr = apkBytes > 0 ? `${(apkBytes / (1024 * 1024)).toFixed(1)} MB` : '15.3 MB'
          const tag = data.version.startsWith('v') ? data.version : `v${data.version}`

          setReleaseInfo({
            version: tag,
            windows: {
              filename: data.windows.filename || `NexClip_Setup_${tag}_x64.exe`,
              size: winSizeStr,
              serverUrl: data.windows.download_url || `/releases/${data.windows.filename}`,
              githubUrl: `https://github.com/yixing233/nexclip/releases/download/${tag}/${data.windows.filename || `NexClip_Setup_${tag}_x64.exe`}`,
              sha256: data.windows.sha256 || '',
            },
            android: {
              filename: data.android.filename || `NexClip_${tag}_Android.apk`,
              size: apkSizeStr,
              serverUrl: data.android.download_url || `/releases/${data.android.filename}`,
              githubUrl: `https://github.com/yixing233/nexclip/releases/download/${tag}/${data.android.filename || `NexClip_${tag}_Android.apk`}`,
              sha256: data.android.sha256 || '',
            },
          })
        }
      })
      .catch((err) => {
        console.warn('Auto version sync fallback:', err)
      })
  }, [])

  useEffect(() => {
    const handleScroll = () => {
      setIsScrolled(window.scrollY > 20)
    }
    const handleResize = () => {
      setWindowWidth(window.innerWidth)
    }
    window.addEventListener('scroll', handleScroll, { passive: true })
    window.addEventListener('resize', handleResize, { passive: true })
    return () => {
      window.removeEventListener('scroll', handleScroll)
      window.removeEventListener('resize', handleResize)
    }
  }, [])

  // 平滑丝滑滚动定位 (带顶栏高度避让与动画)
  const scrollToSection = (e: React.MouseEvent<HTMLElement>, targetId: string) => {
    e.preventDefault()
    const element = document.getElementById(targetId)
    if (element) {
      const navHeaderHeight = 72
      const targetPosition = element.getBoundingClientRect().top + window.pageYOffset - navHeaderHeight
      window.scrollTo({
        top: Math.max(0, targetPosition),
        behavior: 'smooth',
      })
      if (window.history.pushState) {
        window.history.pushState(null, '', `#${targetId}`)
      }
    }
  }

  const isMobile = windowWidth < 768
  const isSmallMobile = windowWidth < 480

  // 悬停高亮边框样式 (无位移，纯净科技感)
  const normalBorder = isDark ? 'rgba(255, 255, 255, 0.10)' : 'rgba(0, 0, 0, 0.08)'
  const activeHighlightBorder = isDark ? '#4493F8' : '#2563EB'
  const normalShadow = isDark ? '0 8px 24px rgba(0, 0, 0, 0.3)' : '0 8px 24px rgba(0, 0, 0, 0.04)'
  const highlightShadow = isDark
    ? '0 0 0 1px rgba(68, 147, 248, 0.5), 0 8px 28px rgba(68, 147, 248, 0.15)'
    : '0 0 0 1px rgba(37, 99, 235, 0.35), 0 8px 28px rgba(37, 99, 235, 0.10)'

  const windowsMenuItems: MenuProps['items'] = [
    {
      key: 'server-direct',
      icon: <Zap size={15} color="#10B981" />,
      label: (
        <div style={{ display: 'flex', flexDirection: 'column', padding: '3px 0' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <span style={{ fontWeight: 600, fontSize: 13 }}>使用服务端直连下载</span>
            <span style={{ fontSize: 10, padding: '1px 6px', borderRadius: 4, background: 'rgba(16, 185, 129, 0.15)', color: '#10B981', fontWeight: 600 }}>大陆加速</span>
          </div>
          <span style={{ fontSize: 11, color: c.textTertiary, marginTop: 2 }}>{releaseInfo.windows.filename} · {releaseInfo.windows.size}</span>
        </div>
      ),
      onClick: () => {
        const link = document.createElement('a')
        link.href = releaseInfo.windows.serverUrl
        link.download = releaseInfo.windows.filename
        document.body.appendChild(link)
        link.click()
        document.body.removeChild(link)
      },
    },
    {
      type: 'divider',
    },
    {
      key: 'github-release',
      icon: <ExternalLink size={14} />,
      label: '查看 GitHub Releases 发布页面',
      onClick: () => {
        window.open(`https://github.com/yixing233/nexclip/releases/tag/${releaseInfo.version}`, '_blank')
      },
    },
    {
      key: 'copy-hash',
      icon: <Copy size={14} />,
      label: '复制 SHA256 校验码',
      onClick: () => {
        navigator.clipboard.writeText(releaseInfo.windows.sha256)
        message.success('Windows 安装包 SHA256 校验码已复制')
      },
    },
  ]

  const androidMenuItems: MenuProps['items'] = [
    {
      key: 'server-direct',
      icon: <Zap size={15} color="#10B981" />,
      label: (
        <div style={{ display: 'flex', flexDirection: 'column', padding: '3px 0' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <span style={{ fontWeight: 600, fontSize: 13 }}>使用服务端直连下载</span>
            <span style={{ fontSize: 10, padding: '1px 6px', borderRadius: 4, background: 'rgba(16, 185, 129, 0.15)', color: '#10B981', fontWeight: 600 }}>大陆加速</span>
          </div>
          <span style={{ fontSize: 11, color: c.textTertiary, marginTop: 2 }}>{releaseInfo.android.filename} · {releaseInfo.android.size}</span>
        </div>
      ),
      onClick: () => {
        const link = document.createElement('a')
        link.href = releaseInfo.android.serverUrl
        link.download = releaseInfo.android.filename
        document.body.appendChild(link)
        link.click()
        document.body.removeChild(link)
      },
    },
    {
      type: 'divider',
    },
    {
      key: 'github-release',
      icon: <ExternalLink size={14} />,
      label: '查看 GitHub Releases 发布页面',
      onClick: () => {
        window.open(`https://github.com/yixing233/nexclip/releases/tag/${releaseInfo.version}`, '_blank')
      },
    },
    {
      key: 'copy-hash',
      icon: <Copy size={14} />,
      label: '复制 SHA256 校验码',
      onClick: () => {
        navigator.clipboard.writeText(releaseInfo.android.sha256)
        message.success('Android 安装包 SHA256 校验码已复制')
      },
    },
  ]

  return (
    <div
      style={{
        minHeight: '100vh',
        background: isDark ? '#0D1117' : '#F8FAFC',
        color: c.text,
        fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif',
        overflowX: 'hidden',
        width: '100%',
        maxWidth: '100vw',
        transition: 'background 0.3s ease, color 0.3s ease',
      }}
    >
      {/* ==================== 1. 悬浮高斯模糊导航顶栏 ==================== */}
      <header
        style={{
          position: 'fixed',
          zIndex: 1000,
          left: '50%',
          transform: 'translateX(-50%)',
          top: isScrolled ? (isMobile ? 8 : 16) : 0,
          width: isScrolled
            ? isMobile
              ? 'calc(100% - 16px)'
              : 'min(1200px, calc(100% - 32px))'
            : '100%',
          height: isMobile ? 56 : (isScrolled ? 60 : 70),
          borderRadius: isScrolled ? (isMobile ? 16 : 9999) : 0,
          background: isDark ? 'rgba(22, 27, 34, 0.5)' : 'rgba(255, 255, 255, 0.5)',
          backdropFilter: 'blur(20px)',
          WebkitBackdropFilter: 'blur(20px)',
          border: isScrolled
            ? `1px solid ${isDark ? 'rgba(255, 255, 255, 0.12)' : 'rgba(0, 0, 0, 0.08)'}`
            : `1px solid ${isDark ? 'rgba(255, 255, 255, 0.06)' : 'rgba(0, 0, 0, 0.04)'}`,
          borderTop: isScrolled ? undefined : 'none',
          boxShadow: isScrolled
            ? isDark
              ? '0 16px 36px -10px rgba(0, 0, 0, 0.7)'
              : '0 16px 36px -10px rgba(0, 0, 0, 0.08)'
            : 'none',
          padding: isMobile ? '0 14px' : (isScrolled ? '0 24px' : '0 40px'),
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          transition: 'all 0.3s cubic-bezier(0.16, 1, 0.3, 1)',
        }}
      >
        {/* Brand Logo */}
        <div
          onClick={() => window.scrollTo({ top: 0, behavior: 'smooth' })}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: isMobile ? 8 : 10,
            cursor: 'pointer',
            userSelect: 'none',
          }}
        >
          <img
            src="/logo.png"
            alt="NexClip Logo"
            style={{
              width: isMobile ? 28 : 32,
              height: isMobile ? 28 : 32,
              borderRadius: 8,
              objectFit: 'contain',
            }}
          />
          <span
            style={{
              fontSize: isMobile ? 18 : 20,
              fontWeight: 700,
              letterSpacing: -0.5,
              color: c.text,
            }}
          >
            NexClip
          </span>
        </div>

        {/* Center Nav Links (Desktop Only) */}
        {!isMobile && (
          <nav
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 32,
              fontSize: 14,
              fontWeight: 500,
            }}
          >
            <a
              href="#features"
              onClick={(e) => scrollToSection(e, 'features')}
              style={{
                color: c.textSecondary,
                textDecoration: 'none',
                transition: 'color 0.2s, transform 0.2s',
                cursor: 'pointer',
              }}
              onMouseEnter={(e) => {
                e.currentTarget.style.color = c.text
                e.currentTarget.style.transform = 'translateY(-1px)'
              }}
              onMouseLeave={(e) => {
                e.currentTarget.style.color = c.textSecondary
                e.currentTarget.style.transform = 'translateY(0)'
              }}
            >
              核心功能
            </a>
            <a
              href="#clients"
              onClick={(e) => scrollToSection(e, 'clients')}
              style={{
                color: c.textSecondary,
                textDecoration: 'none',
                transition: 'color 0.2s, transform 0.2s',
                cursor: 'pointer',
              }}
              onMouseEnter={(e) => {
                e.currentTarget.style.color = c.text
                e.currentTarget.style.transform = 'translateY(-1px)'
              }}
              onMouseLeave={(e) => {
                e.currentTarget.style.color = c.textSecondary
                e.currentTarget.style.transform = 'translateY(0)'
              }}
            >
              支持平台
            </a>
          </nav>
        )}

        {/* Right Actions */}
        <div style={{ display: 'flex', alignItems: 'center', gap: isMobile ? 6 : 10 }}>
          {/* GitHub 入口 */}
          <Tooltip title="前往 GitHub 开源仓库">
            <Button
              type="text"
              href="https://github.com/yixing233/nexclip"
              target="_blank"
              shape={isMobile ? 'circle' : undefined}
              style={{
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: 6,
                borderRadius: 999,
                color: c.text,
                padding: isMobile ? 0 : '4px 12px',
                height: 34,
                width: isMobile ? 34 : undefined,
              }}
            >
              <svg height="16" width="16" viewBox="0 0 16 16" fill="currentColor">
                <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"></path>
              </svg>
              {!isMobile && <span style={{ fontSize: 13, fontWeight: 500 }}>GitHub</span>}
            </Button>
          </Tooltip>

          {/* 明暗模式切换 */}
          <Tooltip title={isDark ? '切换至明亮模式' : '切换至暗黑模式'}>
            <Button
              type="text"
              shape="circle"
              onClick={onToggleTheme}
              style={{
                width: 34,
                height: 34,
                color: c.text,
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
              }}
            >
              {isDark ? <Sun size={16} /> : <Moon size={16} />}
            </Button>
          </Tooltip>

          {/* 管理后台入口 */}
          {!isMobile && (
            <Button
              type="text"
              onClick={() => navigate('/pro/overview')}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 6,
                borderRadius: 999,
                color: c.textSecondary,
                height: 36,
                padding: '4px 12px',
                fontSize: 13,
                fontWeight: 500,
              }}
            >
              <Sliders size={14} />
              <span>管理后台</span>
            </Button>
          )}

          {/* 进入控制台 */}
          <Button
            type="primary"
            shape="round"
            onClick={() => navigate('/index')}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 6,
              height: isMobile ? 32 : 36,
              padding: isMobile ? '0 12px' : '0 16px',
              fontSize: isMobile ? 12 : 13,
              fontWeight: 600,
            }}
          >
            <LogIn size={isMobile ? 13 : 15} />
            <span>进入控制台</span>
          </Button>
        </div>
      </header>

      {/* ==================== 2. Hero 头部介绍区 ==================== */}
      <section
        style={{
          paddingTop: isMobile ? 100 : 150,
          paddingBottom: isMobile ? 50 : 80,
          maxWidth: 1080,
          margin: '0 auto',
          paddingLeft: isMobile ? 16 : 24,
          paddingRight: isMobile ? 16 : 24,
          textAlign: 'center',
        }}
      >
        <div style={{ display: 'flex', justifyContent: 'center', marginBottom: 20 }}>
          <div
            style={{
              width: 64,
              height: 64,
              borderRadius: 16,
              padding: 4,
              background: isDark ? 'rgba(255, 255, 255, 0.06)' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
            }}
          >
            <img src="/logo.png" alt="NexClip Logo" style={{ width: '100%', height: '100%', borderRadius: 12, objectFit: 'contain' }} />
          </div>
        </div>

        <h1
          style={{
            fontSize: 'clamp(28px, 5.5vw, 52px)',
            fontWeight: 800,
            letterSpacing: -1,
            lineHeight: 1.2,
            margin: '0 auto 16px',
            color: c.text,
          }}
        >
          跨平台剪贴板秒级同步系统
        </h1>

        <p
          style={{
            fontSize: isMobile ? 15 : 18,
            color: c.textSecondary,
            maxWidth: 680,
            margin: '0 auto 36px',
            lineHeight: 1.6,
          }}
        >
          在 Windows、Android 与 Web 之间无缝流转文本、代码与高清图片。
          <br />
          告别微信文件传输助手，按下快捷键随叫随到，让剪贴板回归纯粹效率。
        </p>

        {/* 核心操作按钮 */}
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            gap: 14,
            flexWrap: 'wrap',
            marginBottom: 40,
          }}
        >
          <Button
            type="primary"
            size="large"
            shape="round"
            onClick={() => navigate('/index')}
            style={{
              height: isMobile ? 44 : 48,
              padding: isMobile ? '0 24px' : '0 32px',
              fontSize: 15,
              fontWeight: 600,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: 8,
              width: isSmallMobile ? '100%' : 'auto',
            }}
          >
            <span>进入 Web 控制台</span>
            <ArrowRight size={16} />
          </Button>

          <Button
            size="large"
            shape="round"
            href="#clients"
            onClick={(e) => scrollToSection(e, 'clients')}
            style={{
              height: isMobile ? 44 : 48,
              padding: isMobile ? '0 22px' : '0 28px',
              fontSize: 15,
              fontWeight: 500,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: 8,
              width: isSmallMobile ? '100%' : 'auto',
            }}
          >
            <Download size={16} />
            <span>下载多端客户端</span>
          </Button>
        </div>

        {/* 核心特性标签 */}
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            gap: isMobile ? 12 : 24,
            flexWrap: 'wrap',
            color: c.textSecondary,
            fontSize: 13,
          }}
        >
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <Check size={16} color="#10B981" />
            <span>纯文本 / 代码 / 图片</span>
          </span>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <Check size={16} color="#10B981" />
            <span>6 位配对码授权</span>
          </span>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <Check size={16} color="#10B981" />
            <span>全局热键与回车回填</span>
          </span>
          <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <Check size={16} color="#10B981" />
            <span>开源免费无广告</span>
          </span>
        </div>
      </section>

      {/* ==================== 3. 核心功能与使用场景介绍 ==================== */}
      <section
        id="features"
        style={{
          padding: isMobile ? '40px 16px 60px' : '60px 24px 80px',
          maxWidth: 1120,
          margin: '0 auto',
        }}
      >
        <div style={{ textAlign: 'center', marginBottom: isMobile ? 32 : 48 }}>
          <h2 style={{ fontSize: 'clamp(24px, 4vw, 36px)', fontWeight: 800, letterSpacing: -0.8, margin: '0 0 10px' }}>
            核心功能一览
          </h2>
          <p style={{ fontSize: isMobile ? 14 : 16, color: c.textSecondary, margin: 0 }}>
            解决日常工作与多设备协同中的复制粘贴痛点
          </p>
        </div>

        <div
          style={{
            display: 'grid',
            gridTemplateColumns: isMobile ? '1fr' : 'repeat(2, 1fr)',
            gap: 20,
          }}
        >
          {/* 功能卡片 1: 跨端秒级同步 */}
          <div
            style={{
              padding: isMobile ? 24 : 32,
              borderRadius: 18,
              background: isDark ? '#161B22' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              transition: 'border-color 0.25s ease, box-shadow 0.25s ease',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.borderColor = activeHighlightBorder
              e.currentTarget.style.boxShadow = highlightShadow
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.borderColor = normalBorder
              e.currentTarget.style.boxShadow = normalShadow
            }}
          >
            <div style={{ display: 'inline-flex', padding: 10, borderRadius: 12, background: 'rgba(56, 189, 248, 0.12)', color: '#38BDF8', marginBottom: 16 }}>
              <Zap size={22} />
            </div>
            <h3 style={{ fontSize: 18, fontWeight: 700, margin: '0 0 10px' }}>跨端双向即时同步</h3>
            <p style={{ fontSize: 14, color: c.textSecondary, lineHeight: 1.7, margin: 0 }}>
              只要在一个设备上复制内容，其他已配对的设备便可在毫秒级自动接收。无需借助第三方聊天软件建立“文件传输助手”，保持剪贴板自然流动。
            </p>
          </div>

          {/* 功能卡片 2: 支持高清图片与多模态 */}
          <div
            style={{
              padding: isMobile ? 24 : 32,
              borderRadius: 18,
              background: isDark ? '#161B22' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              transition: 'border-color 0.25s ease, box-shadow 0.25s ease',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.borderColor = activeHighlightBorder
              e.currentTarget.style.boxShadow = highlightShadow
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.borderColor = normalBorder
              e.currentTarget.style.boxShadow = normalShadow
            }}
          >
            <div style={{ display: 'inline-flex', padding: 10, borderRadius: 12, background: 'rgba(16, 185, 129, 0.12)', color: '#10B981', marginBottom: 16 }}>
              <ImageIcon size={22} />
            </div>
            <h3 style={{ fontSize: 18, fontWeight: 700, margin: '0 0 10px' }}>支持长文本、代码与高清图片</h3>
            <p style={{ fontSize: 14, color: c.textSecondary, lineHeight: 1.7, margin: 0 }}>
              无论是日常文本、格式复杂的代码段，还是通过系统快捷键截取的高清图片，均支持原画质无损分发，移动端和桌面端即拷即贴。
            </p>
          </div>

          {/* 功能卡片 3: Windows 桌面端悬浮窗与回车直贴 */}
          <div
            style={{
              padding: isMobile ? 24 : 32,
              borderRadius: 18,
              background: isDark ? '#161B22' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              transition: 'border-color 0.25s ease, box-shadow 0.25s ease',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.borderColor = activeHighlightBorder
              e.currentTarget.style.boxShadow = highlightShadow
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.borderColor = normalBorder
              e.currentTarget.style.boxShadow = normalShadow
            }}
          >
            <div style={{ display: 'inline-flex', padding: 10, borderRadius: 12, background: 'rgba(99, 102, 241, 0.12)', color: '#6366F1', marginBottom: 16 }}>
              <Command size={22} />
            </div>
            <h3 style={{ fontSize: 18, fontWeight: 700, margin: '0 0 10px' }}>快捷键随时唤醒与回车回填</h3>
            <p style={{ fontSize: 14, color: c.textSecondary, lineHeight: 1.7, margin: 0 }}>
              桌面端支持全局快捷键唤醒极简磨砂浮窗，快速搜索历史记录，按回车直接自动回填至当前活动输入框，失焦自动隐匿，专注沉浸工作。
            </p>
          </div>

          {/* 功能卡片 4: 6 位配对码极速绑定 */}
          <div
            style={{
              padding: isMobile ? 24 : 32,
              borderRadius: 18,
              background: isDark ? '#161B22' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              transition: 'border-color 0.25s ease, box-shadow 0.25s ease',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.borderColor = activeHighlightBorder
              e.currentTarget.style.boxShadow = highlightShadow
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.borderColor = normalBorder
              e.currentTarget.style.boxShadow = normalShadow
            }}
          >
            <div style={{ display: 'inline-flex', padding: 10, borderRadius: 12, background: 'rgba(236, 72, 153, 0.12)', color: '#EC4899', marginBottom: 16 }}>
              <KeyRound size={22} />
            </div>
            <h3 style={{ fontSize: 18, fontWeight: 700, margin: '0 0 10px' }}>6 位配对码，私密安全</h3>
            <p style={{ fontSize: 14, color: c.textSecondary, lineHeight: 1.7, margin: 0 }}>
              无需绑定手机号或复杂的账户注册。设备间通过 6 位配对码即可建立端到端授权信道，数据掌握在自己手中，告别隐私泄露担忧。
            </p>
          </div>
        </div>
      </section>

      {/* ==================== 4. 客户端与平台支持专区 ==================== */}
      <section
        id="clients"
        style={{
          padding: isMobile ? '40px 16px 80px' : '60px 24px 100px',
          maxWidth: 1120,
          margin: '0 auto',
        }}
      >
        <div style={{ textAlign: 'center', marginBottom: isMobile ? 32 : 48 }}>
          <h2 style={{ fontSize: 'clamp(24px, 4vw, 36px)', fontWeight: 800, letterSpacing: -0.8, margin: '0 0 10px' }}>
            支持的操作系统与平台
          </h2>
          <p style={{ fontSize: isMobile ? 14 : 16, color: c.textSecondary, margin: 0 }}>
            选择适合你设备的版本，快速开始使用
          </p>
        </div>

        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))',
            gap: 20,
          }}
        >
          {/* Windows 客户端 */}
          <div
            style={{
              padding: isMobile ? 24 : 32,
              borderRadius: 18,
              background: isDark ? '#161B22' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              display: 'flex',
              flexDirection: 'column',
              justifyContent: 'space-between',
              transition: 'border-color 0.25s ease, box-shadow 0.25s ease',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.borderColor = activeHighlightBorder
              e.currentTarget.style.boxShadow = highlightShadow
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.borderColor = normalBorder
              e.currentTarget.style.boxShadow = normalShadow
            }}
          >
            <div>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 16 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <div style={{ width: 42, height: 42, borderRadius: 10, background: 'rgba(0, 164, 239, 0.12)', display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#00A4EF' }}>
                    <Monitor size={22} />
                  </div>
                  <div>
                    <h3 style={{ fontSize: 18, fontWeight: 700, margin: 0 }}>Windows 客户端</h3>
                    <span style={{ fontSize: 12, color: c.textTertiary }}>Windows 10 / 11 · x64</span>
                  </div>
                </div>
                <span
                  style={{
                    fontSize: 11,
                    fontWeight: 600,
                    padding: '2px 8px',
                    borderRadius: 9999,
                    background: isDark ? 'rgba(68, 147, 248, 0.15)' : 'rgba(37, 99, 235, 0.10)',
                    color: isDark ? '#58A6FF' : '#2563EB',
                  }}
                >
                  {releaseInfo.version}
                </span>
              </div>
              <p style={{ fontSize: 13, color: c.textSecondary, lineHeight: 1.6, marginBottom: 24 }}>
                WinUI 3 原生 Fluent 视觉，全新 Native AOT 现代安装向导，支持跨端互传、动作识别、色值预览转换、文件路径直达、与智能检索等功能。
              </p>
            </div>

            <div>
              {/* 组合下载按钮: 默认 GitHub 官方下载 + 下拉箭头选择服务端直连 */}
              <div
                style={{
                  display: 'flex',
                  borderRadius: 10,
                  overflow: 'hidden',
                  boxShadow: '0 2px 8px rgba(0, 0, 0, 0.08)',
                }}
              >
                <Button
                  type="primary"
                  size="large"
                  icon={<Download size={16} />}
                  href={releaseInfo.windows.githubUrl}
                  target="_blank"
                  rel="noreferrer"
                  style={{
                    flex: 1,
                    height: 44,
                    fontWeight: 600,
                    borderTopRightRadius: 0,
                    borderBottomRightRadius: 0,
                    borderRight: '1px solid rgba(255, 255, 255, 0.25)',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    gap: 8,
                  }}
                >
                  <span>下载 Windows 版</span>
                  <span style={{ fontSize: 11, opacity: 0.85, fontWeight: 400 }}>({releaseInfo.windows.size})</span>
                </Button>
                <Dropdown menu={{ items: windowsMenuItems }} placement="bottomRight" trigger={['click']}>
                  <Button
                    type="primary"
                    size="large"
                    style={{
                      width: 44,
                      height: 44,
                      padding: 0,
                      borderTopLeftRadius: 0,
                      borderBottomLeftRadius: 0,
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                    }}
                    title="更多下载通道 (服务端直连)"
                  >
                    <ChevronDown size={16} />
                  </Button>
                </Dropdown>
              </div>
            </div>
          </div>

          {/* Android 客户端 */}
          <div
            style={{
              padding: isMobile ? 24 : 32,
              borderRadius: 18,
              background: isDark ? '#161B22' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              display: 'flex',
              flexDirection: 'column',
              justifyContent: 'space-between',
              transition: 'border-color 0.25s ease, box-shadow 0.25s ease',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.borderColor = activeHighlightBorder
              e.currentTarget.style.boxShadow = highlightShadow
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.borderColor = normalBorder
              e.currentTarget.style.boxShadow = normalShadow
            }}
          >
            <div>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 16 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <div style={{ width: 42, height: 42, borderRadius: 10, background: 'rgba(61, 220, 132, 0.12)', display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#3DDC84' }}>
                    <Smartphone size={22} />
                  </div>
                  <div>
                    <h3 style={{ fontSize: 18, fontWeight: 700, margin: 0 }}>Android 客户端</h3>
                    <span style={{ fontSize: 12, color: c.textTertiary }}>Android 8.0 及以上</span>
                  </div>
                </div>
                <span
                  style={{
                    fontSize: 11,
                    fontWeight: 600,
                    padding: '2px 8px',
                    borderRadius: 9999,
                    background: isDark ? 'rgba(61, 220, 132, 0.15)' : 'rgba(16, 185, 129, 0.10)',
                    color: isDark ? '#3DDC84' : '#10B981',
                  }}
                >
                  {releaseInfo.version}
                </span>
              </div>
              <p style={{ fontSize: 13, color: c.textSecondary, lineHeight: 1.6, marginBottom: 24 }}>
                Miuix UI 组件规范，支持 HyperOS 灵动超级岛、提供Xposed/Shizuku双方案实现后台监听, 支持验证码/链接等智能直达功能。
              </p>
            </div>

            <div>
              {/* 组合下载按钮: 默认 GitHub 官方下载 + 下拉箭头选择服务端直连 */}
              <div
                style={{
                  display: 'flex',
                  borderRadius: 10,
                  overflow: 'hidden',
                  boxShadow: '0 2px 8px rgba(0, 0, 0, 0.08)',
                }}
              >
                <Button
                  type="primary"
                  size="large"
                  icon={<Download size={16} />}
                  href={releaseInfo.android.githubUrl}
                  target="_blank"
                  rel="noreferrer"
                  style={{
                    flex: 1,
                    height: 44,
                    fontWeight: 600,
                    background: '#10B981',
                    borderColor: '#10B981',
                    borderTopRightRadius: 0,
                    borderBottomRightRadius: 0,
                    borderRight: '1px solid rgba(255, 255, 255, 0.25)',
                    display: 'flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    gap: 8,
                  }}
                >
                  <span>下载 Android 版</span>
                  <span style={{ fontSize: 11, opacity: 0.85, fontWeight: 400 }}>({releaseInfo.android.size})</span>
                </Button>
                <Dropdown menu={{ items: androidMenuItems }} placement="bottomRight" trigger={['click']}>
                  <Button
                    type="primary"
                    size="large"
                    style={{
                      width: 44,
                      height: 44,
                      padding: 0,
                      background: '#10B981',
                      borderColor: '#10B981',
                      borderTopLeftRadius: 0,
                      borderBottomLeftRadius: 0,
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                    }}
                    title="更多下载通道 (服务端直连)"
                  >
                    <ChevronDown size={16} />
                  </Button>
                </Dropdown>
              </div>
            </div>
          </div>

          {/* Web 控制台 */}
          <div
            style={{
              padding: isMobile ? 24 : 32,
              borderRadius: 18,
              background: isDark ? '#161B22' : '#FFFFFF',
              border: `1px solid ${normalBorder}`,
              boxShadow: normalShadow,
              display: 'flex',
              flexDirection: 'column',
              justifyContent: 'space-between',
              transition: 'border-color 0.25s ease, box-shadow 0.25s ease',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.borderColor = activeHighlightBorder
              e.currentTarget.style.boxShadow = highlightShadow
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.borderColor = normalBorder
              e.currentTarget.style.boxShadow = normalShadow
            }}
          >
            <div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16 }}>
                <div style={{ width: 42, height: 42, borderRadius: 10, background: 'rgba(37, 99, 235, 0.12)', display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#2563EB' }}>
                  <Globe size={22} />
                </div>
                <div>
                  <h3 style={{ fontSize: 18, fontWeight: 700, margin: 0 }}>Web 在线控制台</h3>
                  <span style={{ fontSize: 12, color: c.textTertiary }}>现代浏览器免安装</span>
                </div>
              </div>
              <p style={{ fontSize: 13, color: c.textSecondary, lineHeight: 1.6, marginBottom: 24 }}>
                无需安装客户端软件，在任意电脑或平板浏览器中即可实时查看剪贴板历史流、图片预览与在线设备管理。
              </p>
            </div>
            <Button
              type="primary"
              size="large"
              block
              icon={<LogIn size={16} />}
              onClick={() => navigate('/index')}
              style={{
                borderRadius: 10,
                height: 44,
                fontWeight: 600,
              }}
            >
              进入 Web 控制台
            </Button>
          </div>
        </div>
      </section>

      {/* ==================== 5. 页脚 ==================== */}
      <footer
        style={{
          borderTop: `1px solid ${isDark ? 'rgba(255, 255, 255, 0.08)' : 'rgba(0, 0, 0, 0.06)'}`,
          padding: isMobile ? '30px 16px' : '36px 24px',
          textAlign: 'center',
          color: c.textTertiary,
          fontSize: 13,
          background: isDark ? 'rgba(13, 17, 23, 0.6)' : 'rgba(255, 255, 255, 0.6)',
        }}
      >
        <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', gap: 8, marginBottom: 12 }}>
          <img src="/logo.png" alt="NexClip Logo" style={{ width: 22, height: 22, borderRadius: 6, objectFit: 'contain' }} />
          <span style={{ fontSize: 15, fontWeight: 700, color: c.text }}>NexClip</span>
        </div>
        <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', gap: 14, marginBottom: 12, flexWrap: 'wrap' }}>
          <a
            href="https://github.com/yixing233/nexclip"
            target="_blank"
            rel="noreferrer"
            style={{ color: c.textSecondary, textDecoration: 'none', display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 12 }}
          >
            <ExternalLink size={13} />
            GitHub 源码仓库
          </a>
          <span>·</span>
          <span
            style={{ cursor: 'pointer', color: c.textSecondary, fontSize: 12 }}
            onClick={() => navigate('/index')}
          >
            用户控制台
          </span>
          <span>·</span>
          <span
            style={{ cursor: 'pointer', color: c.textSecondary, fontSize: 12 }}
            onClick={() => navigate('/pro/overview')}
          >
            管理后台
          </span>
        </div>
        <div style={{ fontSize: 12, lineHeight: 1.6 }}>
          NexClip · Next-Generation Cross-Platform Clipboard Sync System · MIT Licensed
        </div>
      </footer>
    </div>
  )
}
