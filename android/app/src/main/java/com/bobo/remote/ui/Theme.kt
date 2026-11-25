package com.bobo.remote.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val BoBoBlue = Color(0xFF2563EB)
val BoBoBlueDark = Color(0xFF1D4ED8)
val BoBoSurfaceBg = Color(0xFFF4F7FC)
val BoBoCardWhite = Color(0xFFFFFFFF)
val BoBoInkDark = Color(0xFF0F172A)
val BoBoInkMuted = Color(0xFF64748B)
val BoBoSuccess = Color(0xFF16A34A)
val BoBoWarning = Color(0xFFD97706)
val BoBoDanger = Color(0xFFDC2626)

private val BoBoLightColors = lightColorScheme(
    primary = BoBoBlue,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDBEAFE),
    onPrimaryContainer = Color(0xFF1E3A8A),
    secondary = Color(0xFF0EA5E9),
    background = BoBoSurfaceBg,
    onBackground = BoBoInkDark,
    surface = BoBoCardWhite,
    onSurface = BoBoInkDark,
    surfaceVariant = Color(0xFFEFF4FB),
    onSurfaceVariant = BoBoInkMuted,
    error = BoBoDanger
)

@Composable
fun BoBoTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = BoBoLightColors,
        content = content
    )
}
