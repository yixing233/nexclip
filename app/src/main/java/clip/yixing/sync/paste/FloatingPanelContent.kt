package clip.yixing.sync.paste

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.formatTime
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.ui.resolveDeviceIcon
import clip.yixing.sync.util.ImageLoader
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.HorizontalDivider
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Copy
import top.yukonga.miuix.kmp.icon.extended.Favorites
import top.yukonga.miuix.kmp.icon.extended.FavoritesFill
import top.yukonga.miuix.kmp.theme.MiuixTheme

/** 面板最多列出多少条,再多用户也不会在悬浮窗里翻 */
private const val PANEL_MAX_ITEMS = 20

/**
 * 悬浮球展开后的最近记录面板。
 *
 * 覆盖窗口后面是别的应用,Miuix 的 layerBackdrop 毛玻璃采样不到内容,
 * 所以这里一律用实色 Card —— 改动时不要顺手加回毛玻璃。
 */
@Composable
fun FloatingPanelContent(
    clips: List<CapturedClip>,
    onPaste: (CapturedClip) -> Unit,
    onCopy: (CapturedClip) -> Unit,
    onToggleFavorite: (CapturedClip) -> Unit,
    onClose: () -> Unit
) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        insideMargin = PaddingValues(0.dp)
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(start = 14.dp, end = 6.dp, top = 8.dp, bottom = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(
                    imageVector = LucideIcons.ClipboardPaste,
                    contentDescription = null,
                    tint = MiuixTheme.colorScheme.primary,
                    modifier = Modifier.size(16.dp)
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    text = "点按即粘贴",
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Medium,
                    color = MiuixTheme.colorScheme.onSurface
                )
                Spacer(Modifier.weight(1f))
                IconButton(onClick = onClose) {
                    Icon(
                        imageVector = LucideIcons.X,
                        contentDescription = "关闭",
                        tint = MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier.size(16.dp)
                    )
                }
            }
            HorizontalDivider()

            if (clips.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(96.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = "暂无剪贴板记录",
                        fontSize = 13.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }
            } else {
                LazyColumn(modifier = Modifier.heightIn(max = 300.dp)) {
                    items(clips.take(PANEL_MAX_ITEMS), key = { it.id }) { clip ->
                        PanelClipRow(
                            clip = clip,
                            onPaste = { onPaste(clip) },
                            onCopy = { onCopy(clip) },
                            onToggleFavorite = { onToggleFavorite(clip) }
                        )
                        HorizontalDivider()
                    }
                }
            }
        }
    }
}

/** 单条记录:点按直接粘贴,长按展开复制 / 收藏 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PanelClipRow(
    clip: CapturedClip,
    onPaste: () -> Unit,
    onCopy: () -> Unit,
    onToggleFavorite: () -> Unit
) {
    val context = LocalContext.current
    var actionsVisible by remember(clip.id) { mutableStateOf(false) }
    var thumbnail by remember(clip.id) { mutableStateOf<ImageBitmap?>(null) }

    LaunchedEffect(clip.id) {
        if (clip.isImage) {
            thumbnail = ImageLoader.loadImageBitmap(context, clip.imageRef, clip.text)
        }
    }

    val sourceLabel = clip.sourceDevice?.takeIf { it.isNotBlank() }
        ?: clip.sourceApp?.takeIf { it.isNotBlank() }
        ?: "本机"

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .combinedClickable(
                onClick = onPaste,
                onLongClick = { actionsVisible = !actionsVisible }
            )
            .padding(horizontal = 14.dp, vertical = 10.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (clip.isImage) {
                Box(
                    modifier = Modifier
                        .size(40.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainerHigh),
                    contentAlignment = Alignment.Center
                ) {
                    val bitmap = thumbnail
                    if (bitmap != null) {
                        Image(
                            bitmap = bitmap,
                            contentDescription = null,
                            contentScale = ContentScale.Crop,
                            modifier = Modifier.fillMaxSize()
                        )
                    } else {
                        Icon(
                            imageVector = LucideIcons.Image,
                            contentDescription = null,
                            tint = MiuixTheme.colorScheme.onBackgroundVariant,
                            modifier = Modifier.size(16.dp)
                        )
                    }
                }
                Spacer(Modifier.width(10.dp))
            }

            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = if (clip.isImage) "[图片]" else clip.text,
                    fontSize = 14.sp,
                    color = MiuixTheme.colorScheme.onSurface,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(3.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(
                        imageVector = resolveDeviceIcon(sourceLabel),
                        contentDescription = null,
                        tint = MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier.size(11.dp)
                    )
                    Spacer(Modifier.width(4.dp))
                    Text(
                        text = "$sourceLabel · ${formatTime(clip.time)}",
                        fontSize = 11.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
        }

        AnimatedVisibility(visible = actionsVisible) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 6.dp),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = {
                    actionsVisible = false
                    onCopy()
                }) {
                    Icon(
                        imageVector = MiuixIcons.Normal.Copy,
                        contentDescription = "复制",
                        tint = MiuixTheme.colorScheme.primary,
                        modifier = Modifier.size(16.dp)
                    )
                }
                IconButton(onClick = {
                    actionsVisible = false
                    onToggleFavorite()
                }) {
                    Icon(
                        imageVector = if (clip.isFavorite) {
                            MiuixIcons.Normal.FavoritesFill
                        } else {
                            MiuixIcons.Normal.Favorites
                        },
                        contentDescription = "收藏",
                        tint = MiuixTheme.colorScheme.primary,
                        modifier = Modifier.size(16.dp)
                    )
                }
            }
        }
    }
}
