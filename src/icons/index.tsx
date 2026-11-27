// 图标库抽象层:按设置(iconLibrary)在 lucide / tabler / phosphor / remix 之间切换
//
// 用法:页面不再直接 import "lucide-react",改从本文件按语义名取图标组件:
//   import { Icon } from "../icons";
//   <Icon name="copy" size={14} />
// 切换图标库由 settings-changed 事件驱动,全部窗口即时生效,无需重启。

import { createContext, useContext } from "react";
import type { ComponentType, CSSProperties } from "react";

import {
  Copy, Image as LucideImage, File as LucideFile, Link as LucideLink, Star,
  Type, Pin, PinOff, Settings as LucideSettings, Trash2, ClipboardPaste,
  Laptop, ArrowUp, LayoutGrid, MessagesSquare, GripVertical, ExternalLink,
  Pencil, PenLine, FolderOpen, Palette, Send, Loader2, Download, FileUp,
  CloudUpload, SlidersHorizontal, ClipboardList, Keyboard, Info, QrCode,
  RefreshCw, X,
  MousePointer2, Highlighter as LucideHighlighter, Slash, ArrowUpRight,
  Square as LucideSquare, Circle as LucideCircle, Droplet, Lightbulb,
  Ruler, Eraser, Undo2, Redo2, Check, Camera, Pipette, MoveVertical,
  Minus, Plus, Radius, Search, SquareRoundCorner, Save,
  Smartphone, Globe,
} from "lucide-react";

import {
  IconCopy, IconPhoto, IconFile, IconLink, IconStar, IconTypography,
  IconPinned, IconPinnedOff, IconSettings, IconTrash, IconClipboardCopy,
  IconDeviceLaptop, IconArrowUp, IconLayoutGrid, IconMessages, IconGripVertical,
  IconExternalLink, IconPencil, IconEdit, IconFolderOpen, IconPalette,
  IconSend, IconLoader2, IconDownload, IconFileUpload, IconCloudUpload,
  IconAdjustmentsHorizontal, IconClipboardList, IconKeyboard, IconInfoCircle,
  IconQrcode, IconRefresh, IconX,
  IconPointer, IconHighlight, IconSlash, IconArrowUpRight, IconSquare,
  IconCircle, IconDroplet, IconBulb, IconRuler, IconEraser,
  IconArrowBackUp, IconArrowForwardUp, IconCheck, IconCamera, IconColorPicker,
  IconArrowsVertical, IconMinus, IconPlus, IconSearch, IconBorderCornerRounded,
  IconDeviceMobile, IconWorld, IconDeviceFloppy, IconBallpen,
} from "@tabler/icons-react";

import {
  Copy as PhCopy, Image as PhImage, File as PhFile, Link as PhLink, Star as PhStar,
  TextAa as PhType, PushPin as PhPin, PushPinSlash as PhPinOff, Gear as PhSettings,
  Trash as PhTrash, ClipboardText as PhClipboardPaste, Laptop as PhLaptop,
  ArrowUp as PhArrowUp, SquaresFour as PhGrid, Chats as PhChat,
  DotsSixVertical as PhGripVertical, ArrowSquareOut as PhExternalLink,
  PencilSimple as PhPencil, Pen as PhPenLine, FolderOpen as PhFolderOpen,
  Palette as PhPalette, PaperPlaneRight as PhSend, CircleNotch as PhSpinner,
  DownloadSimple as PhDownload, UploadSimple as PhFileUpload,
  CloudArrowUp as PhCloudUpload, SlidersHorizontal as PhSliders,
  Keyboard as PhKeyboard, Info as PhInfo, QrCode as PhQrCode,
  ArrowsClockwise as PhRefresh, X as PhX,
  CornersOut as PhCornersOut,
  FloppyDisk as PhFloppy,
  DeviceMobile as PhSmartphone, Globe as PhGlobe,
  Cursor as PhCursor, Highlighter as PhHighlighter, LineSegment as PhLineSegment,
  ArrowUpRight as PhArrowUpRight, Square as PhSquare, Circle as PhCircle,
  Drop as PhDrop, Lightbulb as PhLightbulb, Ruler as PhRuler, Eraser as PhEraser,
  ArrowUUpLeft as PhUndo, ArrowUUpRight as PhRedo, Check as PhCheck,
  Camera as PhCamera, Eyedropper as PhPipette, ArrowsVertical as PhScrollShot,
  Minus as PhMinus, Plus as PhPlus, MagnifyingGlass as PhMagnifyingGlass,
} from "@phosphor-icons/react";

import {
  RiFileCopyLine, RiImageLine, RiFileLine, RiLink, RiStarLine, RiText,
  RiPushpinLine, RiUnpinLine, RiSettings3Line, RiDeleteBinLine,
  RiClipboardLine, RiMacbookLine, RiArrowUpLine, RiLayoutGridLine,
  RiChat3Line, RiDraggable, RiExternalLinkLine, RiPencilLine, RiEdit2Line,
  RiFolderOpenLine, RiPaletteLine, RiSendPlane2Line, RiLoader4Line,
  RiDownloadLine, RiUpload2Line, RiUploadCloud2Line, RiEqualizerLine,
  RiKeyboardLine, RiInformationLine, RiQrCodeLine,
  RiRefreshLine, RiCloseLine,
  RiSmartphoneLine, RiGlobalLine,
  RiCursorLine, RiMarkPenLine, RiSubtractLine, RiArrowRightUpLine,
  RiSquareLine, RiCircleLine, RiDropLine, RiFocus3Line,
  RiRulerLine, RiEraserLine, RiArrowGoBackLine, RiArrowGoForwardLine,
  RiCheckLine, RiCameraLine, RiDropperLine, RiArrowUpDownLine, RiAddLine,
  RiSearchLine, RiRoundedCorner, RiSaveLine, RiBallPenLine,
} from "@remixicon/react";

/// 图标语义名(与 lucide 名对应,各库侧选语义一致的图标)
export type IconName =
  | "copy" | "image" | "file" | "link" | "star" | "type"
  | "pin" | "pinOff" | "settings" | "trash" | "clipboardPaste"
  | "laptop" | "smartphone" | "globe" | "arrowUp" | "grid" | "chat" | "gripVertical"
  | "externalLink" | "pencil" | "penLine" | "draw" | "folderOpen" | "palette"
  | "send" | "spinner" | "download" | "save" | "fileUpload"
  | "cloudUpload" | "sliders" | "clipboardList" | "keyboard" | "info"
  | "qrCode" | "refresh" | "x"
  // 截图工具
  | "select" | "highlight" | "line" | "arrowUpRight" | "square" | "circle"
  | "blur" | "spotlight" | "measure" | "eraser" | "undo" | "redo"
  | "check" | "camera" | "pipette" | "scrollShot" | "radius"
  | "minus" | "add" | "search";

export type IconLibraryId = "lucide" | "tabler" | "phosphor" | "remix";

type IconComponent = ComponentType<{
  size?: number | string;
  strokeWidth?: number | string;
  weight?: "thin" | "light" | "regular" | "bold" | "fill" | "duotone";
  className?: string;
  style?: CSSProperties;
  fill?: string;
  color?: string;
}>;

/// 四库映射。
/// 视觉粗细差异由 <Icon> 归一化:lucide 默认线宽 2,tabler 1.5(补到 2),
/// phosphor stroke 属性 2(一致),remix 是描边固定粗细的填充 SVG(无需处理)。
const MAP: Record<IconName, Record<IconLibraryId, IconComponent>> = {
  copy: { lucide: Copy, tabler: IconCopy, phosphor: PhCopy, remix: RiFileCopyLine },
  image: { lucide: LucideImage, tabler: IconPhoto, phosphor: PhImage, remix: RiImageLine },
  file: { lucide: LucideFile, tabler: IconFile, phosphor: PhFile, remix: RiFileLine },
  link: { lucide: LucideLink, tabler: IconLink, phosphor: PhLink, remix: RiLink },
  star: { lucide: Star, tabler: IconStar, phosphor: PhStar, remix: RiStarLine },
  type: { lucide: Type, tabler: IconTypography, phosphor: PhType, remix: RiText },
  pin: { lucide: Pin, tabler: IconPinned, phosphor: PhPin, remix: RiPushpinLine },
  pinOff: { lucide: PinOff, tabler: IconPinnedOff, phosphor: PhPinOff, remix: RiUnpinLine },
  settings: { lucide: LucideSettings, tabler: IconSettings, phosphor: PhSettings, remix: RiSettings3Line },
  trash: { lucide: Trash2, tabler: IconTrash, phosphor: PhTrash, remix: RiDeleteBinLine },
  clipboardPaste: { lucide: ClipboardPaste, tabler: IconClipboardCopy, phosphor: PhClipboardPaste, remix: RiClipboardLine },
  laptop: { lucide: Laptop, tabler: IconDeviceLaptop, phosphor: PhLaptop, remix: RiMacbookLine },
  smartphone: { lucide: Smartphone, tabler: IconDeviceMobile, phosphor: PhSmartphone, remix: RiSmartphoneLine },
  globe: { lucide: Globe, tabler: IconWorld, phosphor: PhGlobe, remix: RiGlobalLine },
  arrowUp: { lucide: ArrowUp, tabler: IconArrowUp, phosphor: PhArrowUp, remix: RiArrowUpLine },
  grid: { lucide: LayoutGrid, tabler: IconLayoutGrid, phosphor: PhGrid, remix: RiLayoutGridLine },
  chat: { lucide: MessagesSquare, tabler: IconMessages, phosphor: PhChat, remix: RiChat3Line },
  gripVertical: { lucide: GripVertical, tabler: IconGripVertical, phosphor: PhGripVertical, remix: RiDraggable },
  externalLink: { lucide: ExternalLink, tabler: IconExternalLink, phosphor: PhExternalLink, remix: RiExternalLinkLine },
  pencil: { lucide: Pencil, tabler: IconPencil, phosphor: PhPencil, remix: RiPencilLine },
  penLine: { lucide: PenLine, tabler: IconEdit, phosphor: PhPenLine, remix: RiEdit2Line },
  draw: { lucide: PenLine, tabler: IconBallpen, phosphor: PhPenLine, remix: RiBallPenLine },
  folderOpen: { lucide: FolderOpen, tabler: IconFolderOpen, phosphor: PhFolderOpen, remix: RiFolderOpenLine },
  palette: { lucide: Palette, tabler: IconPalette, phosphor: PhPalette, remix: RiPaletteLine },
  send: { lucide: Send, tabler: IconSend, phosphor: PhSend, remix: RiSendPlane2Line },
  spinner: { lucide: Loader2, tabler: IconLoader2, phosphor: PhSpinner, remix: RiLoader4Line },
  download: { lucide: Download, tabler: IconDownload, phosphor: PhDownload, remix: RiDownloadLine },
  save: { lucide: Save, tabler: IconDeviceFloppy, phosphor: PhFloppy, remix: RiSaveLine },
  fileUpload: { lucide: FileUp, tabler: IconFileUpload, phosphor: PhFileUpload, remix: RiUpload2Line },
  cloudUpload: { lucide: CloudUpload, tabler: IconCloudUpload, phosphor: PhCloudUpload, remix: RiUploadCloud2Line },
  sliders: { lucide: SlidersHorizontal, tabler: IconAdjustmentsHorizontal, phosphor: PhSliders, remix: RiEqualizerLine },
  clipboardList: { lucide: ClipboardList, tabler: IconClipboardList, phosphor: PhClipboardPaste, remix: RiClipboardLine },
  keyboard: { lucide: Keyboard, tabler: IconKeyboard, phosphor: PhKeyboard, remix: RiKeyboardLine },
  info: { lucide: Info, tabler: IconInfoCircle, phosphor: PhInfo, remix: RiInformationLine },
  qrCode: { lucide: QrCode, tabler: IconQrcode, phosphor: PhQrCode, remix: RiQrCodeLine },
  refresh: { lucide: RefreshCw, tabler: IconRefresh, phosphor: PhRefresh, remix: RiRefreshLine },
  x: { lucide: X, tabler: IconX, phosphor: PhX, remix: RiCloseLine },
  // 截图工具
  select: { lucide: MousePointer2, tabler: IconPointer, phosphor: PhCursor, remix: RiCursorLine },
  highlight: { lucide: LucideHighlighter, tabler: IconHighlight, phosphor: PhHighlighter, remix: RiMarkPenLine },
  line: { lucide: Slash, tabler: IconSlash, phosphor: PhLineSegment, remix: RiSubtractLine },
  arrowUpRight: { lucide: ArrowUpRight, tabler: IconArrowUpRight, phosphor: PhArrowUpRight, remix: RiArrowRightUpLine },
  square: { lucide: LucideSquare, tabler: IconSquare, phosphor: PhSquare, remix: RiSquareLine },
  circle: { lucide: LucideCircle, tabler: IconCircle, phosphor: PhCircle, remix: RiCircleLine },
  blur: { lucide: Droplet, tabler: IconDroplet, phosphor: PhDrop, remix: RiDropLine },
  spotlight: { lucide: Lightbulb, tabler: IconBulb, phosphor: PhLightbulb, remix: RiFocus3Line },
  measure: { lucide: Ruler, tabler: IconRuler, phosphor: PhRuler, remix: RiRulerLine },
  eraser: { lucide: Eraser, tabler: IconEraser, phosphor: PhEraser, remix: RiEraserLine },
  undo: { lucide: Undo2, tabler: IconArrowBackUp, phosphor: PhUndo, remix: RiArrowGoBackLine },
  redo: { lucide: Redo2, tabler: IconArrowForwardUp, phosphor: PhRedo, remix: RiArrowGoForwardLine },
  check: { lucide: Check, tabler: IconCheck, phosphor: PhCheck, remix: RiCheckLine },
  camera: { lucide: Camera, tabler: IconCamera, phosphor: PhCamera, remix: RiCameraLine },
  pipette: { lucide: Pipette, tabler: IconColorPicker, phosphor: PhPipette, remix: RiDropperLine },
  scrollShot: { lucide: MoveVertical, tabler: IconArrowsVertical, phosphor: PhScrollShot, remix: RiArrowUpDownLine },
  radius: { lucide: SquareRoundCorner, tabler: IconBorderCornerRounded, phosphor: PhCornersOut, remix: RiRoundedCorner },
  minus: { lucide: Minus, tabler: IconMinus, phosphor: PhMinus, remix: RiSubtractLine },
  add: { lucide: Plus, tabler: IconPlus, phosphor: PhPlus, remix: RiAddLine },
  search: { lucide: Search, tabler: IconSearch, phosphor: PhMagnifyingGlass, remix: RiSearchLine },
};
/// 当前图标库上下文(由 main.tsx 的 Root 按设置注入)
export const IconLibraryContext = createContext<IconLibraryId>("lucide");

export function useIconLibrary(): IconLibraryId {
  return useContext(IconLibraryContext);
}

/// 按语义名取当前图标库的组件;lib 值异常时回退 lucide(防御旧后端/损坏设置)
export function getIcon(name: IconName): IconComponent {
  const lib = useIconLibrary();
  return MAP[name][lib] ?? MAP[name].lucide;
}

/// 便捷组件:<Icon name="copy" size={14} style={...} />
/// 线宽统一:lucide 默认 2,tabler 默认 1.5 → 补到 2;phosphor 默认 2(一致);
/// remix 是固定描边填充 SVG,strokeWidth 无效(忽略)。
/// 注意:undefined 的 fill/color 绝不能传下去 —— phosphor/remix 组件内部
/// `{ fill: currentColor, ...rest }` 的 rest 展开在 fill 之后,显式 undefined
/// 会覆盖掉 currentColor,深色主题下图标变黑。
export function Icon(props: {
  name: IconName;
  size?: number;
  className?: string;
  strokeWidth?: number;
  style?: CSSProperties;
  fill?: string;
  color?: string;
}) {
  const { name, size = 16, className, strokeWidth, style, fill, color } = props;
  const lib = useIconLibrary();
  const Cmp = MAP[name][lib] ?? MAP[name].lucide;
  // 只有描边型图标库(lucide/tabler)接受 strokeWidth;
  // phosphor 用 weight 控制粗细,remix 是固定描边的填充图形。
  const sw = strokeWidth ?? (lib === "tabler" ? 2 : undefined);
  const strokeProps =
    sw && (lib === "lucide" || lib === "tabler") ? { strokeWidth: sw } : {};
  // fill 语义差异:
  //  - lucide/tabler 是描边图标,fill="none" 表示空心(必须传下去);
  //  - phosphor/remix 是填充图形,传 fill="none" 会直接变空白;
  //    需要实心时用 color 着色 + phosphor 的 weight="fill"。
  const isStrokeLib = lib === "lucide" || lib === "tabler";
  const solidFill = fill && fill !== "none" ? fill : undefined;
  const fillProps = isStrokeLib
    ? fill
      ? { fill }
      : {}
    : solidFill
      ? { fill: solidFill }
      : {};
  const fillWeightProps =
    lib === "phosphor" && solidFill ? { weight: "fill" as const } : {};
  return (
    <Cmp
      size={size}
      {...(className ? { className } : {})}
      {...(style ? { style } : {})}
      {...(color ? { color } : {})}
      {...fillProps}
      {...fillWeightProps}
      {...strokeProps}
    />
  );
}
