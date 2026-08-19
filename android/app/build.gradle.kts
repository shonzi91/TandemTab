import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.compose.compiler)
}

// ---------------------------------------------------------------------------------------------
// Release signing.
//
// The keystore never lives in the repo. A local build reads "android/keystore.properties"
// (gitignored); CI writes that same file from repository secrets, so there is exactly one shape to
// get right. When it is absent the release build is signed with the DEBUG key rather than failing:
// a contributor without the key still gets an installable APK for internal testing, and the warning
// below is what stops that build being mistaken for a shippable one — the debug key is public and
// universally known, so a store upload signed with it is rejected anyway.
// ---------------------------------------------------------------------------------------------
val keystoreProperties = Properties().apply {
    val file = rootProject.file("keystore.properties")
    if (file.exists()) file.inputStream().use { load(it) }
}

fun signingSetting(property: String, environmentVariable: String): String? =
    keystoreProperties.getProperty(property) ?: System.getenv(environmentVariable)

val releaseStorePath = signingSetting("storeFile", "TANDEMTAB_KEYSTORE_FILE")
val releaseStorePassword = signingSetting("storePassword", "TANDEMTAB_KEYSTORE_PASSWORD")
val releaseKeyAlias = signingSetting("keyAlias", "TANDEMTAB_KEY_ALIAS")
val releaseKeyPassword = signingSetting("keyPassword", "TANDEMTAB_KEY_PASSWORD")
val releaseStore = releaseStorePath?.let { rootProject.file(it) }
val hasReleaseKey = releaseStore?.exists() == true &&
    releaseStorePassword != null && releaseKeyAlias != null && releaseKeyPassword != null

// A build anyone can install needs a versionCode that only ever goes up; CI passes its run number.
// Locally both keep the defaults, so nothing extra has to be remembered to build.
val buildVersionCode = (providers.gradleProperty("versionCode").orNull ?: "1").toInt()
val buildVersionName = providers.gradleProperty("versionName").orNull ?: "0.1.0"

android {
    namespace = "com.tandemtab.app"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.tandemtab.app"
        minSdk = 26
        targetSdk = 35
        versionCode = buildVersionCode
        versionName = buildVersionName
        vectorDrawables { useSupportLibrary = true }
    }

    signingConfigs {
        if (hasReleaseKey) {
            create("release") {
                storeFile = releaseStore
                storePassword = releaseStorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        debug {
            // Point at the live API by default; overridable per build type later.
            buildConfigField("String", "API_BASE_URL", "\"https://tandemtab.com\"")
            isDebuggable = true
        }
        release {
            buildConfigField("String", "API_BASE_URL", "\"https://tandemtab.com\"")
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (hasReleaseKey) {
                signingConfigs.getByName("release")
            } else {
                logger.warn(
                    "TandemTab: no release keystore configured (android/keystore.properties or the " +
                        "TANDEMTAB_KEYSTORE_* environment) — signing the release APK with the DEBUG " +
                        "key. Installable for testing; NOT publishable."
                )
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
}

// "app-release.apk" says nothing to somebody about to sideload it.
base { archivesName = "tandemtab" }

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.datastore.preferences)

    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.extended)
    debugImplementation(libs.androidx.compose.ui.tooling)

    implementation(libs.ktor.client.core)
    implementation(libs.ktor.client.okhttp)
    implementation(libs.ktor.client.content.negotiation)
    implementation(libs.ktor.client.logging)
    implementation(libs.ktor.serialization.kotlinx.json)
    implementation(libs.kotlinx.serialization.json)
}
