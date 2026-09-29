pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "TianshuAndroidHeadless"

include(":core")

val requestedCompileSdk = gradle.startParameter.projectProperties["tianshu.compileSdk"]?.toIntOrNull() ?: 37

fun androidSdkWithPlatform(api: Int): Boolean {
    fun hasPlatform(sdkDir: String): Boolean =
        file(sdkDir).resolve("platforms").let { platforms ->
            platforms.resolve("android-$api").isDirectory || platforms.resolve("android-$api.0").isDirectory
        }

    val local = file("local.properties")
    if (local.isFile) {
        val sdkDir = local.readLines()
            .firstOrNull { it.trimStart().startsWith("sdk.dir=") }
            ?.substringAfter("=")
            ?.replace("\\:", ":")
            ?.replace("\\\\", "\\")
        if (!sdkDir.isNullOrBlank() && hasPlatform(sdkDir)) return true
    }

    val env = listOf("ANDROID_HOME", "ANDROID_SDK_ROOT")
        .mapNotNull { System.getenv(it) }
        .firstOrNull { it.isNotBlank() && hasPlatform(it) }
    return env != null
}

if (androidSdkWithPlatform(requestedCompileSdk) || System.getProperty("tianshu.includeAndroidApp") == "true") {
    include(":app")
} else {
    println("Android SDK platform android-$requestedCompileSdk not found; building :core only. Add local.properties sdk.dir or ANDROID_HOME, or pass -Ptianshu.compileSdk=<installedApi> to include :app.")
}
