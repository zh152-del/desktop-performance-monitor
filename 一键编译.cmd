@echo off
chcp 936 >nul
cd /d %~dp0Project
title 桌面性能助手 新面板 一键编译

rem ============================================================
rem  2026-10-24 修正：
rem  DST / ARC / STAMP 必须在 if 块【之外】定义。
rem  批处理解析整个 if(...) 块时会一次性展开变量，
rem  若在块内 set 又在块内用 %VAR%，取到的是空值 ——
rem  上一版的 copy 目标会变成 "\"，且被 >nul 吞掉错误，
rem  结果就是「每次都显示编译成功、软件本体却从未被替换」。
rem ============================================================

set "MSBUILD=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo [错误] 找不到 MSBuild：%MSBUILD%
    pause
    exit /b 1
)

set "DST=%~dp0软件本体"

rem 版本时间戳：供旧版本归档目录命名
set "STAMP="
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%i"
if "%STAMP%"=="" set "STAMP=backup"
set "ARC=%~dp0旧版本封存\%STAMP%"

echo [1/4] 结束正在运行的旧程序（不结束的话输出文件被占用，编译必失败）...
taskkill /F /IM 桌面性能助手.exe >nul 2>&1

echo [2/4] 正在编译（Rebuild / Release），完整日志写入 build_log.txt ...
"%MSBUILD%" PerfMonitorWPF.csproj /t:Rebuild /p:Configuration=Release /nologo /v:m > build_log.txt 2>&1
type build_log.txt
echo.

if exist "bin\Release\桌面性能助手.exe" (
    echo ============ 编译成功 ============
    echo 产物：%~dp0Project\bin\Release\桌面性能助手.exe

    echo [3/4] 封存旧版本 → 旧版本封存\%STAMP%\
    if exist "%DST%\桌面性能助手.exe" (
        if not exist "%ARC%" mkdir "%ARC%" >nul 2>&1
        xcopy /y /q "%DST%\*.*" "%ARC%" >nul 2>&1
        echo     已封存：%ARC%
    ) else (
        echo     软件本体为空，首次安装，无需封存
    )

    echo     替换新版到软件本体...
    copy /y "bin\Release\桌面性能助手.exe" "%DST%\" >nul
    for %%F in (LibreHardwareMonitorLib.dll HidSharp.dll BlackSharp.Core.dll DiskInfoToolkit.dll RAMSPDToolkit-NDD.dll System.Memory.dll System.Buffers.dll System.Numerics.Vectors.dll System.Runtime.CompilerServices.Unsafe.dll) do copy /y "bin\Release\%%F" "%DST%\" >nul 2>&1

    echo [4/4] 已替换，正在重新启动面板...
    start "" "%DST%\桌面性能助手.exe"
    echo.
    echo 全部完成！面板已启动：%DST%\桌面性能助手.exe
    echo 旧版本已归档：%ARC%
) else (
    echo ============ 编译失败 ============
    echo 完整日志：%~dp0Project\build_log.txt
    echo 关键错误：
    findstr /C:"error " build_log.txt
)
echo.
pause
