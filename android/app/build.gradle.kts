plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val boboVersionName = providers.gradleProperty("boboReleaseVersion").orElse("7.6.5").get()
val releaseVersionMatch = requireNotNull(
    Regex("(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)").matchEntire(boboVersionName)
) { "boboReleaseVersion must be a numeric major.minor.patch version" }
val (releaseMajor, releaseMinor, releasePatch) = releaseVersionMatch.groupValues.drop(1).map {
    requireNotNull(it.toIntOrNull()) { "An Android version component is out of range" }
}
require(releaseMinor < 100 && releasePatch < 100) { "The Android minor and patch versions must fit in two digits" }
// Keep upgrades ordered while preserving versionCode 623 for the installed 6.2.3 release.
val boboVersionCode = releaseMajor * 10_000L + releaseMinor * 100L + releasePatch - 59_580L
require(boboVersionCode in 1L..2_100_000_000L) { "The Android version code is out of range" }

val boboUploadStorePath = System.getenv("BOBO_UPLOAD_STORE_FILE")
    ?: providers.gradleProperty("boboUploadStoreFile").orNull
val boboUploadStorePassword = System.getenv("BOBO_UPLOAD_STORE_PASSWORD")
    ?: providers.gradleProperty("boboUploadStorePassword").orNull
val boboUploadKeyAlias = System.getenv("BOBO_UPLOAD_KEY_ALIAS")
    ?: providers.gradleProperty("boboUploadKeyAlias").orNull
val boboUploadKeyPassword = System.getenv("BOBO_UPLOAD_KEY_PASSWORD")
    ?: providers.gradleProperty("boboUploadKeyPassword").orNull
val hasBoboReleaseKeystore = !boboUploadStorePath.isNullOrBlank() &&
    file(boboUploadStorePath).exists() &&
    !boboUploadStorePassword.isNullOrBlank() &&
    !boboUploadKeyAlias.isNullOrBlank() &&
    !boboUploadKeyPassword.isNullOrBlank()
if (providers.gradleProperty("boboRequireReleaseSigning").orNull == "true") {
    check(hasBoboReleaseKeystore) {
        "Official release builds require BOBO_UPLOAD_STORE_FILE and Android signing credentials."
    }
}

android {
    namespace = "com.bobo.remote"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.bobo.remote"
        minSdk = 26
        targetSdk = 34
        versionCode = boboVersionCode.toInt()
        versionName = boboVersionName

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        vectorDrawables {
            useSupportLibrary = true
        }
    }

    signingConfigs {
        getByName("debug") {
            enableV1Signing = false
            enableV2Signing = true
            enableV3Signing = true
        }
        if (hasBoboReleaseKeystore) {
            create("release") {
                storeFile = file(boboUploadStorePath!!)
                storePassword = boboUploadStorePassword
                keyAlias = boboUploadKeyAlias
                keyPassword = boboUploadKeyPassword
                enableV1Signing = false
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
            signingConfig = if (hasBoboReleaseKeystore) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    composeOptions {
        kotlinCompilerExtensionVersion = "1.5.14"
    }

    testOptions {
        unitTests {
            isReturnDefaultValues = true
        }
    }

    packaging {
        resources {
            excludes += "/META-INF/{AL2.0,LGPL2.1}"
        }
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.06.00")
    implementation(composeBom)
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.3")
    implementation("androidx.activity:activity-compose:1.9.0")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
