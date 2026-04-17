@echo off
call "D:\Microsoft Visual Studio\2026\VC\Auxiliary\Build\vcvars64.bat"
cd /d "%~dp0"
echo === cl version ===
cl 2>&1 | findstr "Version"
echo === /openmp:llvm ===
cl /nologo /c /openmp:llvm ompprobe.c 2>&1
echo === /openmp:experimental ===
cl /nologo /c /openmp:experimental ompprobe.c 2>&1
