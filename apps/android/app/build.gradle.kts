plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}
val smartPrintServiceUrl = providers.gradleProperty("smartPrintServiceUrl")
    .orElse(providers.environmentVariable("SMARTPRINT_PUBLIC_BASE_URL"))
    .orNull
    ?.trim()
    ?.trimEnd('/')
    ?.takeIf { it.isNotBlank() }
    ?: throw GradleException(
        "Set smartPrintServiceUrl or SMARTPRINT_PUBLIC_BASE_URL to the configured public HTTPS origin."
    )
val smartPrintVersionCode = providers.gradleProperty("smartPrintVersionCode").orNull?.toIntOrNull() ?: 1
val smartPrintVersionName = providers.gradleProperty("smartPrintVersionName").orElse("0.1.0").get()
val smartPrintLocalQa = providers.gradleProperty("smartPrintLocalQa").orNull?.toBooleanStrictOrNull() ?: false
val smartPrintPhotoStudioQaEnabled = providers.gradleProperty("smartPrintPhotoStudioQaEnabled").orNull?.toBooleanStrictOrNull() ?: false
val smartPrintQaApplicationId = providers.gradleProperty("smartPrintQaApplicationId").orNull
    ?.trim()
    ?.takeIf { it.isNotBlank() }
val smartPrintApplicationId = if (smartPrintLocalQa) {
    val candidate = smartPrintQaApplicationId ?: "com.quantaedge.smartprint.localqa"
    if (!candidate.matches(Regex("^com\\.quantaedge\\.smartprint\\.localqa(?:\\.[a-z0-9]+)*$"))) {
        throw GradleException(
            "smartPrintQaApplicationId must remain under com.quantaedge.smartprint.localqa."
        )
    }
    candidate
} else {
    "com.quantaedge.smartprint"
}
val releaseKeystorePath = providers.environmentVariable("ANDROID_SIGNING_KEYSTORE_FILE").orNull
val releaseKeystorePassword = providers.environmentVariable("ANDROID_SIGNING_KEYSTORE_PASSWORD").orNull
val releaseKeyAlias = providers.environmentVariable("ANDROID_SIGNING_KEY_ALIAS").orNull
val releaseKeyPassword = providers.environmentVariable("ANDROID_SIGNING_KEY_PASSWORD").orNull
val hasReleaseSigning = listOf(releaseKeystorePath, releaseKeystorePassword, releaseKeyAlias, releaseKeyPassword).all { !it.isNullOrBlank() }

android {
    buildFeatures { buildConfig = true }
    namespace = "com.quantaedge.smartprint"
    compileSdk = 36
    defaultConfig {
        // Local physical-device QA installs side-by-side with the customer app.
        // It must never replace the production-signed package or clear its data.
        applicationId = smartPrintApplicationId
        minSdk = 26
        targetSdk = 36
        versionCode = smartPrintVersionCode
        versionName = smartPrintVersionName
        buildConfigField("String", "SERVICE_BASE_URL", "\"$smartPrintServiceUrl\"")
        buildConfigField("Boolean", "PHOTO_STUDIO_QA_ENABLED", smartPrintPhotoStudioQaEnabled.toString())
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    signingConfigs {
        if (hasReleaseSigning) {
            create("release") {
                storeFile = file(releaseKeystorePath!!)
                storePassword = releaseKeystorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = false
            if (hasReleaseSigning) {
                signingConfig = signingConfigs.getByName("release")
            }
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

// Stream each Android JVM test as it starts and completes so local QA and CI
// show the exact test currently executing instead of a silent Gradle wait.
tasks.withType<org.gradle.api.tasks.testing.Test>().configureEach {
    testLogging {
        events("started", "passed", "skipped", "failed")
        showStandardStreams = true
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.documentfile:documentfile:1.0.1")
    implementation("androidx.exifinterface:exifinterface:1.3.7")
    implementation("com.github.Dimezis:BlurView:version-3.2.0")
    implementation("androidx.webkit:webkit:1.18.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.camera:camera-camera2:1.6.2")
    implementation("androidx.camera:camera-core:1.6.2")
    implementation("androidx.camera:camera-lifecycle:1.6.2")
    implementation("androidx.camera:camera-view:1.6.2")
    implementation("com.google.mlkit:barcode-scanning:17.3.0")
    implementation("com.google.android.gms:play-services-mlkit-document-scanner:16.0.0")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test:core:1.6.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.6.1")
}
