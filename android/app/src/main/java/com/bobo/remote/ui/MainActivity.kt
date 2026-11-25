package com.bobo.remote.ui

import android.Manifest
import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.LaptopMac
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.PhoneIphone
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.ScreenShare
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.bobo.remote.input.BoBoAccessibilityService
import com.bobo.remote.media.BoBoAppRuntime
import com.bobo.remote.media.ScreenCaptureService
import com.bobo.remote.network.BoBoStreamClient
import com.bobo.remote.protocol.BoBoDiscoveredPeer
import com.bobo.remote.protocol.BoBoIdentityStore
import com.bobo.remote.protocol.BoBoLocalPairSession
import com.bobo.remote.protocol.BoBoProtocol
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {

    private val projectionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK && result.data != null) {
            val svc = Intent(this, ScreenCaptureService::class.java).apply {
                action = ScreenCaptureService.ACTION_START_PROJECTION
                putExtra(ScreenCaptureService.EXTRA_RESULT_CODE, result.resultCode)
                putExtra(ScreenCaptureService.EXTRA_RESULT_DATA, result.data)
            }
            ContextCompat.startForegroundService(this, svc)
            Toast.makeText(this, "已开启 Android 屏幕被控就绪", Toast.LENGTH_SHORT).show()
        } else {
            Toast.makeText(this, "未授予录屏权限，暂无法被远控查看屏幕", Toast.LENGTH_SHORT).show()
        }
    }

    private val notifPermLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        BoBoAppRuntime.init(this)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notifPermLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        setContent {
            BoBoTheme {
                BoBoHomeScreen(
                    onRequestProjection = { requestScreenProjection() },
                    onStopProjection = { stopScreenProjection() },
                    onOpenAccessibility = {
                        startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                    },
                    onCopyText = { label, value ->
                        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        cm.setPrimaryClip(ClipData.newPlainText(label, value))
                        Toast.makeText(this, "已复制 $label", Toast.LENGTH_SHORT).show()
                    },
                    onLaunchStream = { host, port, name, privacy ->
                        val intent = Intent(this, StreamActivity::class.java).apply {
                            putExtra(StreamActivity.EXTRA_HOST, host)
                            putExtra(StreamActivity.EXTRA_PORT, port)
                            putExtra(StreamActivity.EXTRA_NAME, name)
                            putExtra(StreamActivity.EXTRA_PRIVACY, privacy)
                        }
                        startActivity(intent)
                    }
                )
            }
        }
    }

    private fun requestScreenProjection() {
        val mpm = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        projectionLauncher.launch(mpm.createScreenCaptureIntent())
    }

    private fun stopScreenProjection() {
        startService(Intent(this, ScreenCaptureService::class.java).apply {
            action = ScreenCaptureService.ACTION_STOP
        })
        BoBoAppRuntime.hostServer.disconnectAllStreams("手机已主动停止被控")
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BoBoHomeScreen(
    onRequestProjection: () -> Unit,
    onStopProjection: () -> Unit,
    onOpenAccessibility: () -> Unit,
    onCopyText: (String, String) -> Unit,
    onLaunchStream: (host: String, port: Int, name: String, autoPrivacy: Boolean) -> Unit
) {
    var selectedTab by remember { mutableIntStateOf(0) }
    val lanPeers by BoBoAppRuntime.lanPeers.collectAsState()
    val activePeer by BoBoAppRuntime.activeSessionPeer.collectAsState()
    val projectionGranted by BoBoAppRuntime.projectionGranted.collectAsState()
    val scope = rememberCoroutineScope()

    var targetInput by remember { mutableStateOf("") }
    var autoPrivacy by remember { mutableStateOf(true) }
    var connecting by remember { mutableStateOf(false) }
    var statusNotice by remember { mutableStateOf("") }

    // Pairing dialog state
    var pendingPairHost by remember { mutableStateOf<Pair<String, Int>?>(null) }
    var pendingPairName by remember { mutableStateOf("") }
    var pinInput by remember { mutableStateOf("") }
    var pairError by remember { mutableStateOf("") }
    var pairingBusy by remember { mutableStateOf(false) }

    // Local 4-digit pairing code state
    var localPair by remember { mutableStateOf<BoBoLocalPairSession?>(BoBoAppRuntime.hostServer.getActivePairing()) }
    var pairCountdown by remember { mutableIntStateOf(0) }
    var accessibilityEnabled by remember { mutableStateOf(BoBoAccessibilityService.isEnabled) }

    LaunchedEffect(Unit) {
        while (true) {
            localPair = BoBoAppRuntime.hostServer.getActivePairing()
            pairCountdown = localPair?.remainingSeconds ?: 0
            accessibilityEnabled = BoBoAccessibilityService.isEnabled
            delay(1000L)
        }
    }

    fun startConnectFlow(rawTarget: String, port: Int = BoBoProtocol.DEFAULT_TCP_PORT, displayName: String = "") {
        val resolved = BoBoAppRuntime.discovery.resolveHostByDeviceCode(rawTarget)
        if (resolved.isBlank()) {
            statusNotice = "请输入对方 IP（局域网/Tailscale）或 9 位 BoBo 设备码"
            return
        }
        connecting = true
        statusNotice = "正在探测 $resolved ..."
        scope.launch {
            val probe = withContext(Dispatchers.IO) {
                BoBoStreamClient.probeHost(resolved, port, BoBoAppRuntime.identity, 5000)
            }
            connecting = false
            when {
                !probe.reachable || !probe.isBoBoHost -> {
                    statusNotice = probe.error.ifBlank { "未检测到对方开启 BoBo远程" }
                }
                !probe.allowIncoming -> {
                    statusNotice = "对方已关闭「允许本机被控」"
                }
                !probe.authorized -> {
                    pendingPairHost = Pair(resolved, port)
                    pendingPairName = probe.hostName.ifBlank { displayName.ifBlank { resolved } }
                    pinInput = ""
                    pairError = ""
                    statusNotice = "首次连接 ${pendingPairName}，请输入对方 4 位临时配对码"
                }
                else -> {
                    statusNotice = ""
                    onLaunchStream(resolved, port, probe.hostName.ifBlank { displayName.ifBlank { resolved } }, autoPrivacy)
                }
            }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(
                            modifier = Modifier
                                .size(30.dp)
                                .clip(RoundedCornerShape(8.dp))
                                .background(Brush.linearGradient(listOf(BoBoBlue, Color(0xFF0EA5E9)))),
                            contentAlignment = Alignment.Center
                        ) {
                            Text("B", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 16.sp)
                        }
                        Spacer(Modifier.width(10.dp))
                        Column {
                            Text("BoBo远程", fontWeight = FontWeight.Bold, fontSize = 17.sp, color = BoBoInkDark)
                            Text("V6.2 四端互通 · Win / Mac / Android / iOS", fontSize = 11.sp, color = BoBoInkMuted)
                        }
                    }
                },
                actions = {
                    IconButton(onClick = { BoBoAppRuntime.discovery.sendProbeNow() }) {
                        Icon(Icons.Default.Refresh, contentDescription = "刷新局域网设备", tint = BoBoBlue)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = BoBoSurfaceBg)
            )
        },
        bottomBar = {
            NavigationBar(containerColor = BoBoCardWhite) {
                NavigationBarItem(
                    selected = selectedTab == 0,
                    onClick = { selectedTab = 0 },
                    icon = { Icon(Icons.Default.Devices, contentDescription = null) },
                    label = { Text("远程控制") }
                )
                NavigationBarItem(
                    selected = selectedTab == 1,
                    onClick = { selectedTab = 1 },
                    icon = { Icon(Icons.Default.ScreenShare, contentDescription = null) },
                    label = { Text("本机被控") }
                )
                NavigationBarItem(
                    selected = selectedTab == 2,
                    onClick = { selectedTab = 2 },
                    icon = { Icon(Icons.Default.Settings, contentDescription = null) },
                    label = { Text("设置") }
                )
            }
        },
        containerColor = BoBoSurfaceBg
    ) { innerPadding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .padding(horizontal = 16.dp)
        ) {
            when (selectedTab) {
                0 -> RemoteControlTab(
                    targetInput = targetInput,
                    onTargetChange = { targetInput = it },
                    autoPrivacy = autoPrivacy,
                    onAutoPrivacyChange = { autoPrivacy = it },
                    connecting = connecting,
                    statusNotice = statusNotice,
                    lanPeers = lanPeers,
                    authorizedPeers = BoBoAppRuntime.identity.listAuthorizedPeers(),
                    onConnect = { h, p, n -> startConnectFlow(h, p, n) }
                )
                1 -> LocalHostTab(
                    deviceCode = BoBoAppRuntime.identity.formattedDeviceCode,
                    deviceName = BoBoAppRuntime.identity.deviceName,
                    localPair = localPair,
                    countdown = pairCountdown,
                    projectionGranted = projectionGranted,
                    accessibilityEnabled = accessibilityEnabled,
                    activePeer = activePeer,
                    onCopyCode = { onCopyText("本机设备码", BoBoAppRuntime.identity.deviceCode) },
                    onGeneratePin = {
                        localPair = BoBoAppRuntime.hostServer.beginPairing(120)
                        pairCountdown = localPair?.remainingSeconds ?: 120
                    },
                    onRequestProjection = onRequestProjection,
                    onStopProjection = onStopProjection,
                    onOpenAccessibility = onOpenAccessibility
                )
                2 -> SettingsTab(
                    identity = BoBoAppRuntime.identity,
                    allowIncoming = BoBoAppRuntime.hostServer.allowIncomingControl,
                    onAllowIncomingChange = { BoBoAppRuntime.hostServer.allowIncomingControl = it }
                )
            }
        }
    }

    // 4-Digit Temporary PIN Dialog
    val pairTarget = pendingPairHost
    if (pairTarget != null) {
        AlertDialog(
            onDismissRequest = { if (!pairingBusy) pendingPairHost = null },
            title = { Text("BoBo 双端配对授权", fontWeight = FontWeight.Bold) },
            text = {
                Column {
                    Text(
                        "请在「$pendingPairName」的 BoBo远程 界面点击生成 4 位临时配对码并输入：",
                        fontSize = 13.sp,
                        color = BoBoInkMuted
                    )
                    Spacer(Modifier.height(12.dp))
                    OutlinedTextField(
                        value = pinInput,
                        onValueChange = { if (it.length <= 4 && it.all { c -> c.isDigit() }) pinInput = it },
                        label = { Text("4 位数字配对码") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    if (pairError.isNotBlank()) {
                        Spacer(Modifier.height(8.dp))
                        Text(pairError, color = BoBoDanger, fontSize = 12.sp)
                    }
                }
            },
            confirmButton = {
                Button(
                    enabled = pinInput.length == 4 && !pairingBusy,
                    onClick = {
                        pairingBusy = true
                        pairError = ""
                        scope.launch {
                            val (ok, hostName, err) = withContext(Dispatchers.IO) {
                                BoBoStreamClient.pairWithPin(
                                    pairTarget.first,
                                    pairTarget.second,
                                    pinInput,
                                    BoBoAppRuntime.identity
                                )
                            }
                            pairingBusy = false
                            if (ok) {
                                pendingPairHost = null
                                onLaunchStream(pairTarget.first, pairTarget.second, hostName.ifBlank { pendingPairName }, autoPrivacy)
                            } else {
                                pairError = err.ifBlank { "配对码校验失败" }
                            }
                        }
                    }
                ) {
                    Text(if (pairingBusy) "校验中..." else "配对并连接")
                }
            },
            dismissButton = {
                TextButton(enabled = !pairingBusy, onClick = { pendingPairHost = null }) {
                    Text("取消")
                }
            }
        )
    }
}

@Composable
private fun RemoteControlTab(
    targetInput: String,
    onTargetChange: (String) -> Unit,
    autoPrivacy: Boolean,
    onAutoPrivacyChange: (Boolean) -> Unit,
    connecting: Boolean,
    statusNotice: String,
    lanPeers: List<BoBoDiscoveredPeer>,
    authorizedPeers: List<com.bobo.remote.protocol.BoBoAuthorizedPeer>,
    onConnect: (host: String, port: Int, name: String) -> Unit
) {
    LazyColumn(
        verticalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxSize()
    ) {
        item {
            Card(
                shape = RoundedCornerShape(16.dp),
                colors = CardDefaults.cardColors(containerColor = BoBoCardWhite),
                elevation = CardDefaults.cardElevation(defaultElevation = 1.dp)
            ) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text("快速连接远端设备", fontWeight = FontWeight.Bold, fontSize = 15.sp, color = BoBoInkDark)
                    Spacer(Modifier.height(4.dp))
                    Text("支持局域网 IP、Tailscale 虚拟 IP（100.x）或 9 位 BoBo 设备码", fontSize = 12.sp, color = BoBoInkMuted)
                    Spacer(Modifier.height(10.dp))
                    OutlinedTextField(
                        value = targetInput,
                        onValueChange = onTargetChange,
                        placeholder = { Text("例如 192.168.1.10 / 100.x.y.z / 9位设备码") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth()
                    )
                    Spacer(Modifier.height(10.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Default.Shield, contentDescription = null, tint = BoBoBlue, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text("连接瞬间开启电脑防窥屏", fontSize = 13.sp, color = BoBoInkDark)
                        }
                        Switch(checked = autoPrivacy, onCheckedChange = onAutoPrivacyChange)
                    }
                    Spacer(Modifier.height(10.dp))
                    Button(
                        onClick = { onConnect(targetInput, BoBoProtocol.DEFAULT_TCP_PORT, targetInput) },
                        enabled = !connecting && targetInput.isNotBlank(),
                        modifier = Modifier.fillMaxWidth().height(46.dp),
                        shape = RoundedCornerShape(12.dp)
                    ) {
                        if (connecting) {
                            CircularProgressIndicator(modifier = Modifier.size(18.dp), color = Color.White, strokeWidth = 2.dp)
                            Spacer(Modifier.width(8.dp))
                        }
                        Icon(Icons.Default.Link, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(if (connecting) "正在握手探测..." else "进入远程桌面", fontWeight = FontWeight.Bold)
                    }
                    if (statusNotice.isNotBlank()) {
                        Spacer(Modifier.height(8.dp))
                        Text(statusNotice, fontSize = 12.sp, color = BoBoDanger)
                    }
                }
            }
        }

        item {
            Text(
                "局域网自动发现（UDP 49812 · ${lanPeers.size} 台）",
                fontWeight = FontWeight.Bold,
                fontSize = 14.sp,
                color = BoBoInkDark,
                modifier = Modifier.padding(top = 4.dp)
            )
        }

        if (lanPeers.isEmpty()) {
            item {
                Card(
                    shape = RoundedCornerShape(14.dp),
                    colors = CardDefaults.cardColors(containerColor = BoBoCardWhite)
                ) {
                    Text(
                        "暂未发现同局域网在线的 BoBo远程 设备。请确保对方已打开 BoBo远程，或直接输入 Tailscale IP 连接。",
                        fontSize = 13.sp,
                        color = BoBoInkMuted,
                        modifier = Modifier.padding(16.dp)
                    )
                }
            }
        } else {
            items(lanPeers, key = { it.nodeId }) { peer ->
                PeerCard(
                    name = peer.deviceName,
                    subtitle = "${platformLabel(peer.platform)} · ${peer.host} · 码 ${BoBoIdentityStore.formatDeviceCode(peer.deviceCode)}",
                    platform = peer.platform,
                    badge = if (!peer.allowIncoming) "已关被控" else if (peer.hostControlMode == BoBoProtocol.CONTROL_MODE_VIEW_ONLY) "仅观看" else "在线直连",
                    badgeColor = if (!peer.allowIncoming) BoBoInkMuted else BoBoSuccess,
                    onClick = { onConnect(peer.host, peer.port, peer.deviceName) }
                )
            }
        }

        if (authorizedPeers.isNotEmpty()) {
            item {
                Text(
                    "已授权常用设备（${authorizedPeers.size} 台）",
                    fontWeight = FontWeight.Bold,
                    fontSize = 14.sp,
                    color = BoBoInkDark,
                    modifier = Modifier.padding(top = 6.dp)
                )
            }
            items(authorizedPeers, key = { it.certThumbprint }) { peer ->
                PeerCard(
                    name = peer.deviceName.ifBlank { peer.lastHost },
                    subtitle = "${peer.lastHost} · 设备码 ${BoBoIdentityStore.formatDeviceCode(peer.deviceCode)}",
                    platform = BoBoProtocol.PLATFORM_WINDOWS,
                    badge = "免码直连",
                    badgeColor = BoBoBlue,
                    onClick = { onConnect(peer.lastHost, BoBoProtocol.DEFAULT_TCP_PORT, peer.deviceName) }
                )
            }
        }
        item { Spacer(Modifier.height(12.dp)) }
    }
}

@Composable
private fun LocalHostTab(
    deviceCode: String,
    deviceName: String,
    localPair: BoBoLocalPairSession?,
    countdown: Int,
    projectionGranted: Boolean,
    accessibilityEnabled: Boolean,
    activePeer: String?,
    onCopyCode: () -> Unit,
    onGeneratePin: () -> Unit,
    onRequestProjection: () -> Unit,
    onStopProjection: () -> Unit,
    onOpenAccessibility: () -> Unit
) {
    LazyColumn(
        verticalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxSize()
    ) {
        item {
            Card(
                shape = RoundedCornerShape(18.dp),
                colors = CardDefaults.cardColors(containerColor = BoBoBlue)
            ) {
                Column(modifier = Modifier.padding(18.dp)) {
                    Text("本机 Android 设备码（$deviceName）", color = Color(0xFFDBEAFE), fontSize = 12.sp)
                    Spacer(Modifier.height(6.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            deviceCode.ifBlank { "--- --- ---" },
                            color = Color.White,
                            fontSize = 28.sp,
                            fontWeight = FontWeight.ExtraBold,
                            letterSpacing = 1.sp
                        )
                        IconButton(onClick = onCopyCode) {
                            Icon(Icons.Default.ContentCopy, contentDescription = "复制", tint = Color.White)
                        }
                    }
                    Spacer(Modifier.height(10.dp))
                    if (localPair != null && countdown > 0) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(12.dp))
                                .background(Color.White.copy(alpha = 0.16f))
                                .padding(horizontal = 14.dp, vertical = 10.dp),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Column {
                                Text("4 位临时配对码（剩余 ${countdown}s）", color = Color(0xFFDBEAFE), fontSize = 11.sp)
                                Text(localPair.pin, color = Color.White, fontSize = 26.sp, fontWeight = FontWeight.Bold)
                            }
                            TextButton(onClick = onGeneratePin) {
                                Text("换一个", color = Color.White)
                            }
                        }
                    } else {
                        Button(
                            onClick = onGeneratePin,
                            colors = ButtonDefaults.buttonColors(containerColor = Color.White, contentColor = BoBoBlue),
                            shape = RoundedCornerShape(10.dp)
                        ) {
                            Text("生成 4 位临时配对码（120秒有效）", fontWeight = FontWeight.Bold)
                        }
                    }
                }
            }
        }

        if (activePeer != null) {
            item {
                Card(
                    shape = RoundedCornerShape(14.dp),
                    colors = CardDefaults.cardColors(containerColor = Color(0xFFFEF2F2))
                ) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(14.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("正在被「$activePeer」远程控制", color = BoBoDanger, fontWeight = FontWeight.Bold, fontSize = 13.sp)
                        TextButton(onClick = onStopProjection) {
                            Text("立即断开", color = BoBoDanger, fontWeight = FontWeight.Bold)
                        }
                    }
                }
            }
        }

        item {
            Card(
                shape = RoundedCornerShape(16.dp),
                colors = CardDefaults.cardColors(containerColor = BoBoCardWhite)
            ) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("免 Root 被控两步授权", fontWeight = FontWeight.Bold, fontSize = 15.sp)

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text("1. 屏幕画面授权（MediaProjection）", fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                            Text(
                                if (projectionGranted) "已就绪 · 远端连接后自动开始低延迟 H.264 推流"
                                else "授权后允许已配对电脑/手机查看本机屏幕",
                                fontSize = 12.sp,
                                color = if (projectionGranted) BoBoSuccess else BoBoInkMuted
                            )
                        }
                        if (projectionGranted) {
                            OutlinedButton(onClick = onStopProjection) { Text("停止") }
                        } else {
                            Button(onClick = onRequestProjection) { Text("开启") }
                        }
                    }

                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text("2. 免 Root 远程触控（无障碍服务）", fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                            Text(
                                if (accessibilityEnabled) "已启用 · 支持远程点击、滑动、返回与主页"
                                else "未启用 · 仅可观看画面，开启后可完整远程操控手机",
                                fontSize = 12.sp,
                                color = if (accessibilityEnabled) BoBoSuccess else BoBoWarning
                            )
                        }
                        OutlinedButton(onClick = onOpenAccessibility) {
                            Text(if (accessibilityEnabled) "已开启" else "去开启")
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SettingsTab(
    identity: BoBoIdentityStore,
    allowIncoming: Boolean,
    onAllowIncomingChange: (Boolean) -> Unit
) {
    var allowState by remember { mutableStateOf(allowIncoming) }
    var peers by remember { mutableStateOf(identity.listAuthorizedPeers()) }

    LazyColumn(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item {
            Card(shape = RoundedCornerShape(16.dp), colors = CardDefaults.cardColors(containerColor = BoBoCardWhite)) {
                Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("安全与协议设置", fontWeight = FontWeight.Bold, fontSize = 15.sp)
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text("允许本机被远控", fontSize = 14.sp, fontWeight = FontWeight.Medium)
                            Text("关闭后立即断开所有接入，其他设备无法探测连接", fontSize = 12.sp, color = BoBoInkMuted)
                        }
                        Switch(checked = allowState, onCheckedChange = {
                            allowState = it
                            onAllowIncomingChange(it)
                        })
                    }
                    Text("协议版本：BoBo6 v6.2（TCP+TLS 1.2 :49810 / UDP :49812）", fontSize = 12.sp, color = BoBoInkMuted)
                    Text("证书指纹：${identity.certThumbprint.take(24)}...", fontSize = 12.sp, color = BoBoInkMuted)
                }
            }
        }
        item {
            Text("已授权信任设备（${peers.size}）", fontWeight = FontWeight.Bold, fontSize = 14.sp)
        }
        items(peers, key = { it.certThumbprint }) { p ->
            Card(shape = RoundedCornerShape(12.dp), colors = CardDefaults.cardColors(containerColor = BoBoCardWhite)) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(14.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(p.deviceName.ifBlank { p.lastHost }, fontWeight = FontWeight.SemiBold)
                        Text("${p.lastHost} · ${BoBoIdentityStore.formatDeviceCode(p.deviceCode)}", fontSize = 12.sp, color = BoBoInkMuted)
                    }
                    TextButton(onClick = {
                        identity.removeAuthorizedPeer(p.certThumbprint)
                        peers = identity.listAuthorizedPeers()
                    }) {
                        Text("移除", color = BoBoDanger)
                    }
                }
            }
        }
    }
}

@Composable
private fun PeerCard(
    name: String,
    subtitle: String,
    platform: String,
    badge: String,
    badgeColor: Color,
    onClick: () -> Unit
) {
    Card(
        shape = RoundedCornerShape(14.dp),
        colors = CardDefaults.cardColors(containerColor = BoBoCardWhite),
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(14.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(42.dp)
                    .clip(CircleShape)
                    .background(Color(0xFFEFF6FF)),
                contentAlignment = Alignment.Center
            ) {
                Icon(platformIcon(platform), contentDescription = null, tint = BoBoBlue)
            }
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(name, fontWeight = FontWeight.Bold, fontSize = 15.sp, color = BoBoInkDark)
                Text(subtitle, fontSize = 12.sp, color = BoBoInkMuted)
            }
            Box(
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .background(badgeColor.copy(alpha = 0.12f))
                    .padding(horizontal = 8.dp, vertical = 4.dp)
            ) {
                Text(badge, color = badgeColor, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
            }
        }
    }
}

private fun platformIcon(platform: String): ImageVector = when (platform.lowercase()) {
    BoBoProtocol.PLATFORM_MACOS -> Icons.Default.LaptopMac
    BoBoProtocol.PLATFORM_ANDROID -> Icons.Default.PhoneAndroid
    BoBoProtocol.PLATFORM_IOS -> Icons.Default.PhoneIphone
    else -> Icons.Default.Computer
}

private fun platformLabel(platform: String): String = when (platform.lowercase()) {
    BoBoProtocol.PLATFORM_MACOS -> "macOS"
    BoBoProtocol.PLATFORM_ANDROID -> "Android"
    BoBoProtocol.PLATFORM_IOS -> "iOS"
    else -> "Windows"
}
