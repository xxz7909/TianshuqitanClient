plugins {
    id("com.android.application")
}

android {
    namespace = "com.tianshuqitan.headless"
    compileSdk = providers.gradleProperty("tianshu.compileSdk").map(String::toInt).getOrElse(37)

    defaultConfig {
        applicationId = "com.tianshuqitan.headless"
        minSdk = 26
        targetSdk = providers.gradleProperty("tianshu.targetSdk").map(String::toInt).getOrElse(37)
        versionCode = 3
        versionName = "0.1.2"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation(project(":core"))
    implementation("androidx.datastore:datastore-preferences:1.1.7")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
