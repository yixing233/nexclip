package clip.yixing.sync.ui.scan

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.OptIn
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.ExperimentalGetImage
import androidx.camera.core.FocusMeteringAction
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.RoundRect
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.ClipOp
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import clip.yixing.sync.SnackType
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.util.SyncSettings
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.theme.MiuixTheme
import java.util.concurrent.Executors

@Composable
fun QrScanPage(
    snackbarHostState: SnackbarHostState?,
    onBack: () -> Unit,
    onPairSuccess: () -> Unit,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var hasCameraPermission by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        )
    }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        hasCameraPermission = granted
        if (!granted) {
            scope.launch {
                snackbarHostState?.showAppSnack("需要相机权限以扫描二维码", SnackType.Error)
            }
        }
    }

    LaunchedEffect(Unit) {
        if (!hasCameraPermission) {
            permissionLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    // 扫码结果与确认对话框状态
    var pendingResult by remember { mutableStateOf<QrPairingResult?>(null) }
    var isPairing by remember { mutableStateOf(false) }
    var isTorchOn by remember { mutableStateOf(false) }
    var cameraInstance by remember { mutableStateOf<Camera?>(null) }

    // 相册选图 Launcher
    val photoPickerLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.PickVisualMedia()
    ) { uri ->
        if (uri != null) {
            scope.launch {
                val raw = BitmapQrDecoder.decodeFromUri(context, uri)
                if (raw.isNullOrBlank()) {
                    snackbarHostState?.showAppSnack("未在选中图片中识别到二维码", SnackType.Error)
                } else {
                    val res = QrPairingParser.parse(raw, SyncSettings.serverUrl(context))
                    if (res != null) {
                        vibrate(context)
                        pendingResult = res
                    } else {
                        snackbarHostState?.showAppSnack("非配对二维码", SnackType.Info)
                    }
                }
            }
        }
    }

    BackHandler(enabled = true) {
        if (pendingResult != null) {
            pendingResult = null
        } else {
            onBack()
        }
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black)
    ) {
        if (hasCameraPermission) {
            // CameraX 相机取景流
            CameraPreviewView(
                isTorchOn = isTorchOn,
                onCameraReady = { cameraInstance = it },
                onBarcodeDetected = { raw ->
                    scope.launch(Dispatchers.Main) {
                        if (pendingResult == null && !isPairing) {
                            val res = QrPairingParser.parse(raw, SyncSettings.serverUrl(context))
                            if (res != null) {
                                vibrate(context)
                                pendingResult = res
                            }
                        }
                    }
                }
            )

            // 扫码取景框与扫描线遮罩
            ScannerOverlay(
                modifier = Modifier.fillMaxSize()
            )
        } else {
            // 无权限提示
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Text(
                    text = "需要相机权限",
                    style = MiuixTheme.textStyles.title2,
                    color = Color.White
                )
                Spacer(Modifier.height(12.dp))
                Text(
                    text = "请授权相机权限以使用扫一扫配对功能",
                    color = Color.White.copy(alpha = 0.7f),
                    textAlign = TextAlign.Center
                )
                Spacer(Modifier.height(24.dp))
                Button(
                    onClick = { permissionLauncher.launch(Manifest.permission.CAMERA) }
                ) {
                    Text("立即授权")
                }
            }
        }

        // 顶部操作栏 (返回 + 闪光灯 + 相册选图)
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(WindowInsets.statusBars.asPaddingValues())
                .padding(horizontal = 16.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            IconButton(
                onClick = onBack,
                modifier = Modifier
                    .size(40.dp)
                    .clip(CircleShape)
                    .background(Color.Black.copy(alpha = 0.5f))
            ) {
                Icon(
                    imageVector = MiuixIcons.Normal.Back,
                    contentDescription = "返回",
                    tint = Color.White
                )
            }

            Text(
                text = "扫码配对",
                color = Color.White,
                fontSize = 17.sp,
                fontWeight = FontWeight.SemiBold
            )

            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                // 闪光灯开关
                if (hasCameraPermission && cameraInstance?.cameraInfo?.hasFlashUnit() == true) {
                    IconButton(
                        onClick = {
                            isTorchOn = !isTorchOn
                            cameraInstance?.cameraControl?.enableTorch(isTorchOn)
                        },
                        modifier = Modifier
                            .size(40.dp)
                            .clip(CircleShape)
                            .background(
                                if (isTorchOn) Color(0xFFFFD60A).copy(alpha = 0.35f)
                                else Color.Black.copy(alpha = 0.5f)
                            )
                    ) {
                        Icon(
                            imageVector = LucideIcons.Zap,
                            contentDescription = "闪光灯",
                            tint = if (isTorchOn) Color(0xFFFFD60A) else Color.White,
                            modifier = Modifier.size(20.dp)
                        )
                    }
                }

                // 相册选图按钮
                IconButton(
                    onClick = {
                        photoPickerLauncher.launch(
                            PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)
                        )
                    },
                    modifier = Modifier
                        .size(40.dp)
                        .clip(CircleShape)
                        .background(Color.Black.copy(alpha = 0.5f))
                ) {
                    Icon(
                        imageVector = LucideIcons.Image,
                        contentDescription = "相册选图",
                        tint = Color.White,
                        modifier = Modifier.size(20.dp)
                    )
                }
            }
        }

        // 底部提示文字 (无弹窗时展示)
        if (pendingResult == null) {
            Box(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(bottom = 70.dp)
                    .clip(RoundedCornerShape(20.dp))
                    .background(Color.Black.copy(alpha = 0.6f))
                    .padding(horizontal = 20.dp, vertical = 10.dp)
            ) {
                Text(
                    text = "将 Web 控制台或设备配对二维码放入框内",
                    color = Color.White.copy(alpha = 0.9f),
                    fontSize = 13.sp
                )
            }
        }

        // 扫码成功确认接入弹层 (置于顶部覆盖层，避免 SurfaceView 遮挡)
        AnimatedVisibility(
            visible = pendingResult != null,
            enter = fadeIn(tween(200)) + slideInVertically(initialOffsetY = { it }, animationSpec = tween(200)),
            exit = fadeOut(tween(200)) + slideOutVertically(targetOffsetY = { it }, animationSpec = tween(200)),
            modifier = Modifier.fillMaxSize()
        ) {
            val result = pendingResult
            if (result != null) {
                val urlState = remember(result) {
                    TextFieldState(result.serverUrl ?: SyncSettings.serverUrl(context))
                }
                val codeState = remember(result) {
                    TextFieldState(result.pairCode)
                }

                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(Color.Black.copy(alpha = 0.65f))
                        .clickable(enabled = !isPairing) { pendingResult = null },
                    contentAlignment = Alignment.BottomCenter
                ) {
                    Card(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp)
                            .padding(WindowInsets.navigationBars.asPaddingValues())
                            .clickable(enabled = false) {},
                        insideMargin = PaddingValues(20.dp)
                    ) {
                        Column(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(20.dp)
                        ) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(
                                    imageVector = LucideIcons.ScanLine,
                                    contentDescription = "配对",
                                    tint = MiuixTheme.colorScheme.primary,
                                    modifier = Modifier.size(22.dp)
                                )
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    text = "确认接入设备组",
                                    style = MiuixTheme.textStyles.title2,
                                    color = MiuixTheme.colorScheme.onSurface
                                )
                            }
                            Spacer(Modifier.height(4.dp))
                            Text(
                                text = "已成功识别配对二维码，请确认服务地址与配对码",
                                fontSize = 13.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                            Spacer(Modifier.height(16.dp))

                            TextField(
                                state = urlState,
                                label = "服务地址",
                                useLabelAsPlaceholder = true,
                                modifier = Modifier.fillMaxWidth()
                            )
                            Spacer(Modifier.height(10.dp))

                            TextField(
                                state = codeState,
                                label = "配对码",
                                useLabelAsPlaceholder = true,
                                modifier = Modifier.fillMaxWidth()
                            )
                            Spacer(Modifier.height(20.dp))

                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(12.dp)
                            ) {
                                Button(
                                    onClick = { pendingResult = null },
                                    colors = ButtonDefaults.buttonColors(
                                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                        contentColor = MiuixTheme.colorScheme.onSurface
                                    ),
                                    modifier = Modifier.weight(1f),
                                    enabled = !isPairing
                                ) {
                                    Text("重新扫描")
                                }

                                Button(
                                    onClick = {
                                        val sUrl = urlState.text.toString().trim().trimEnd('/')
                                        val pCode = codeState.text.toString().trim().uppercase()
                                        if (sUrl.isBlank() || pCode.isBlank()) {
                                            scope.launch {
                                                snackbarHostState?.showAppSnack("服务地址和配对码不能为空", SnackType.Error)
                                            }
                                            return@Button
                                        }

                                        isPairing = true
                                        scope.launch {
                                            try {
                                                val devId = SyncSettings.ensureDeviceId(context)
                                                val devName = SyncSettings.deviceName(context)
                                                val api = SyncApi(sUrl, devId, "")
                                                val directRes = withContext(Dispatchers.IO) {
                                                    api.pair(pCode, devId, devName)
                                                }

                                                if (directRes.deviceToken.isNullOrBlank()) {
                                                    throw Exception("未能获取设备凭证")
                                                }

                                                // 保存配置并自动开启服务
                                                SyncSettings.prefs(context).edit().putString(SyncSettings.KEY_SERVER_URL, sUrl).apply()
                                                SyncSettings.setDeviceToken(context, directRes.deviceToken)
                                                SyncSettings.setPaired(context, true)
                                                ClipboardMonitorService.start(context)

                                                snackbarHostState?.showAppSnack("配对成功！已连接到服务", SnackType.Success)
                                                pendingResult = null
                                                onPairSuccess()
                                            } catch (e: Exception) {
                                                snackbarHostState?.showAppSnack("配对失败: ${e.message}", SnackType.Error)
                                            } finally {
                                                isPairing = false
                                            }
                                        }
                                    },
                                    modifier = Modifier.weight(1.3f),
                                    enabled = !isPairing
                                ) {
                                    Text(if (isPairing) "接入中…" else "确认接入")
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

/**
 * CameraX 实时相机预览与 ZXing + ML Kit 双核二维码帧分析器
 */
@OptIn(ExperimentalGetImage::class)
@Composable
private fun CameraPreviewView(
    isTorchOn: Boolean,
    onCameraReady: (Camera) -> Unit,
    onBarcodeDetected: (String) -> Unit
) {
    val lifecycleOwner = LocalLifecycleOwner.current
    val cameraExecutor = remember { Executors.newSingleThreadExecutor() }
    val barcodeScanner = remember { BarcodeScanning.getClient() }

    DisposableEffect(Unit) {
        onDispose {
            cameraExecutor.shutdown()
            barcodeScanner.close()
        }
    }

    AndroidView(
        factory = { ctx ->
            val previewView = PreviewView(ctx).apply {
                scaleType = PreviewView.ScaleType.FILL_CENTER
                implementationMode = PreviewView.ImplementationMode.COMPATIBLE
            }
            val cameraProviderFuture = ProcessCameraProvider.getInstance(ctx)

            cameraProviderFuture.addListener({
                val cameraProvider = cameraProviderFuture.get()
                val preview = Preview.Builder().build().also {
                    it.surfaceProvider = previewView.surfaceProvider
                }

                var frameCount = 0
                var lastLogTime = 0L

                val imageAnalysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
                    .build()

                imageAnalysis.setAnalyzer(cameraExecutor) { imageProxy ->
                    try {
                        frameCount++
                        val now = System.currentTimeMillis()
                        if (now - lastLogTime > 2000L) {
                            android.util.Log.d("QrScanPage", "Analyzing frame #$frameCount (${imageProxy.width}x${imageProxy.height}, rot=${imageProxy.imageInfo.rotationDegrees})")
                            lastLogTime = now
                        }

                        // 1. 优先使用 ZXing 离线解析
                        val zxingResult = ZxingQrDecoder.decodeImageProxy(imageProxy)
                        if (!zxingResult.isNullOrBlank()) {
                            android.util.Log.d("QrScanPage", "ZXing detected QR: $zxingResult")
                            onBarcodeDetected(zxingResult)
                            imageProxy.close()
                            return@setAnalyzer
                        }

                        // 2. 备用 ML Kit 解析
                        val mediaImage = imageProxy.image
                        if (mediaImage != null) {
                            val image = InputImage.fromMediaImage(mediaImage, imageProxy.imageInfo.rotationDegrees)
                            barcodeScanner.process(image)
                                .addOnSuccessListener { barcodes ->
                                    val qr = barcodes.firstOrNull { it.format == Barcode.FORMAT_QR_CODE }
                                        ?: barcodes.firstOrNull()
                                    qr?.rawValue?.let { raw ->
                                        android.util.Log.d("QrScanPage", "MLKit detected QR: $raw")
                                        onBarcodeDetected(raw)
                                    }
                                }
                                .addOnCompleteListener {
                                    imageProxy.close()
                                }
                        } else {
                            imageProxy.close()
                        }
                    } catch (e: Exception) {
                        imageProxy.close()
                    }
                }

                val cameraSelector = CameraSelector.DEFAULT_BACK_CAMERA
                try {
                    cameraProvider.unbindAll()
                    val camera = cameraProvider.bindToLifecycle(
                        lifecycleOwner,
                        cameraSelector,
                        preview,
                        imageAnalysis
                    )
                    onCameraReady(camera)
                    camera.cameraControl.enableTorch(isTorchOn)

                    // 支持点击对焦
                    previewView.setOnTouchListener { v, event ->
                        if (event.action == android.view.MotionEvent.ACTION_UP) {
                            val factory = previewView.meteringPointFactory
                            val point = factory.createPoint(event.x, event.y)
                            val action = FocusMeteringAction.Builder(
                                point,
                                FocusMeteringAction.FLAG_AF or FocusMeteringAction.FLAG_AE
                            ).build()
                            camera.cameraControl.startFocusAndMetering(action)
                            v.performClick()
                        }
                        true
                    }
                } catch (e: Exception) {
                    android.util.Log.e("QrScanPage", "Camera binding failed", e)
                }
            }, ContextCompat.getMainExecutor(ctx))

            previewView
        },
        modifier = Modifier.fillMaxSize()
    )
}

/**
 * 扫码遮罩与动态扫描线
 */
@Composable
private fun ScannerOverlay(
    modifier: Modifier = Modifier
) {
    val infiniteTransition = rememberInfiniteTransition(label = "scannerLine")
    val scanProgress by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(2400, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "scanLineProgress"
    )

    BoxWithConstraints(modifier = modifier) {
        val boxWidth = maxWidth
        val scanFrameSize = (boxWidth * 0.72f).coerceAtMost(280.dp)

        Canvas(modifier = Modifier.fillMaxSize()) {
            val framePx = scanFrameSize.toPx()
            val left = (size.width - framePx) / 2
            val top = (size.height - framePx) / 2 - 40.dp.toPx()
            val right = left + framePx
            val bottom = top + framePx

            val frameRect = Rect(left, top, right, bottom)
            val cornerRadius = CornerRadius(16.dp.toPx(), 16.dp.toPx())

            // 1. 绘制镂空暗色遮罩
            val framePath = Path().apply {
                addRoundRect(RoundRect(frameRect, cornerRadius))
            }

            clipPath(framePath, clipOp = ClipOp.Difference) {
                drawRect(color = Color.Black.copy(alpha = 0.65f))
            }

            // 2. 绘制取景框四个发光角标 (Corner Accents)
            val strokeWidth = 3.5.dp.toPx()
            val cornerLength = 22.dp.toPx()
            val accentColor = Color(0xFF34C759)

            // 左上角
            drawLine(accentColor, Offset(left, top + cornerLength), Offset(left, top + 16.dp.toPx()), strokeWidth)
            drawLine(accentColor, Offset(left + cornerLength, top), Offset(left + 16.dp.toPx(), top), strokeWidth)

            // 右上角
            drawLine(accentColor, Offset(right, top + cornerLength), Offset(right, top + 16.dp.toPx()), strokeWidth)
            drawLine(accentColor, Offset(right - cornerLength, top), Offset(right - 16.dp.toPx(), top), strokeWidth)

            // 左下角
            drawLine(accentColor, Offset(left, bottom - cornerLength), Offset(left, bottom - 16.dp.toPx()), strokeWidth)
            drawLine(accentColor, Offset(left + cornerLength, bottom), Offset(left + 16.dp.toPx(), bottom), strokeWidth)

            // 右下角
            drawLine(accentColor, Offset(right, bottom - cornerLength), Offset(right, bottom - 16.dp.toPx()), strokeWidth)
            drawLine(accentColor, Offset(right - cornerLength, bottom), Offset(right - 16.dp.toPx(), bottom), strokeWidth)

            // 3. 绘制动态激光扫描线
            val lineY = top + framePx * scanProgress
            val lineBrush = Brush.horizontalGradient(
                colors = listOf(
                    Color.Transparent,
                    accentColor.copy(alpha = 0.85f),
                    Color(0xFF68FF95),
                    accentColor.copy(alpha = 0.85f),
                    Color.Transparent
                ),
                startX = left,
                endX = right
            )
            drawLine(
                brush = lineBrush,
                start = Offset(left + 8.dp.toPx(), lineY),
                end = Offset(right - 8.dp.toPx(), lineY),
                strokeWidth = 2.5.dp.toPx()
            )
        }
    }
}

/**
 * 触感震动反馈
 */
private fun vibrate(context: Context) {
    runCatching {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val vm = context.getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as? VibratorManager
            vm?.defaultVibrator?.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_CLICK))
        } else {
            @Suppress("DEPRECATION")
            val v = context.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator
            @Suppress("DEPRECATION")
            v?.vibrate(60)
        }
    }
}
