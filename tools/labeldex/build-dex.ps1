<#
  AppLabelProbe dex 构建脚本
  把 src\AppLabelProbe.java 编译为 bin\classes.dex

  依赖：JDK 17+（javac/java）、Android SDK 的 android.jar 与 build-tools 的 d8.jar

  用法：
    pwsh -File build-dex.ps1
    pwsh -File build-dex.ps1 -Sdk E:\Sdk -Platform android-36 -BuildTools 36.0.0

  注意：项目已内置编译好的 bin\classes.dex，普通开发无需重新编译。
        仅在修改 src\AppLabelProbe.java 后需要跑一次本脚本。
#>
param(
    [string]$Sdk = "E:\Sdk",
    [string]$Platform = "android-36",
    [string]$BuildTools = "36.0.0"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$work = Join-Path $here "build"
$out = Join-Path $here "bin"

# JDK：优先 Android Studio 自带 JBR，其次 PATH 中的 javac
$jdkCandidates = @(
    "D:\Android studio\jbr\bin",
    "C:\Program Files\Android\Android Studio\jbr\bin"
)
$jdk = $jdkCandidates | Where-Object { Test-Path (Join-Path $_ "javac.exe") } | Select-Object -First 1
$javac = if ($jdk) { Join-Path $jdk "javac.exe" } else { "javac" }
$java = if ($jdk) { Join-Path $jdk "java.exe" } else { "java" }

$androidJar = Join-Path $Sdk "platforms\$Platform\android.jar"
$d8Jar = Join-Path $Sdk "build-tools\$BuildTools\lib\d8.jar"

if (-not (Test-Path $androidJar)) { throw "找不到 android.jar: $androidJar" }
if (-not (Test-Path $d8Jar)) { throw "找不到 d8.jar: $d8Jar" }

# javac / d8 对带空格的路径处理不一致，统一复制到无空格的工作目录后再引用
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work, (Join-Path $work "classes"), (Join-Path $work "dex") -Force | Out-Null
New-Item -ItemType Directory -Path $out -Force | Out-Null

Copy-Item $androidJar (Join-Path $work "android.jar")
Copy-Item $d8Jar (Join-Path $work "d8.jar")

Write-Host "[1/3] javac 编译..."
# 必须显式 -encoding UTF-8：源码含中文注释，Windows 默认 GBK 会报"不可映射字符"
# -source/-target 11：d8 不支持过高版本的 class 文件（JBR 21 默认产出会被拒）
& $javac -nowarn -Xlint:-options -encoding UTF-8 -source 11 -target 11 `
    -classpath (Join-Path $work "android.jar") `
    -d (Join-Path $work "classes") `
    (Join-Path $here "src\AppLabelProbe.java")
if ($LASTEXITCODE -ne 0) { throw "javac 编译失败" }

Write-Host "[2/3] d8 生成 dex..."
# --output 目录必须已存在，否则 d8 报 "Invalid output"
& $java -cp (Join-Path $work "d8.jar") com.android.tools.r8.D8 --min-api 24 `
    --output (Join-Path $work "dex") `
    (Join-Path $work "classes\AppLabelProbe.class")
if ($LASTEXITCODE -ne 0) { throw "d8 转换失败" }

Write-Host "[3/3] 输出..."
Copy-Item (Join-Path $work "dex\classes.dex") (Join-Path $out "classes.dex") -Force
Remove-Item $work -Recurse -Force

$dex = Get-Item (Join-Path $out "classes.dex")
Write-Host ""
Write-Host "完成: $($dex.FullName)"
Write-Host "大小: $($dex.Length) 字节"
