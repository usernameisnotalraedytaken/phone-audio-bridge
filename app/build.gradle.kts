plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.dicaeopolis.snad"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.dicaeopolis.snad"
        minSdk = 29          // AudioPlaybackCapture 自 Android 10 起可用
        targetSdk = 34       // 前台服务类型在 Android 14 起强制要求
        versionCode = 1
        versionName = "1.0"

        // 默认资源是中文，需显式声明，否则 lint 的 MissingTranslation 会中断 release 构建
        resourceConfigurations += listOf("zh", "en")
    }

    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
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
        viewBinding = false
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
}
