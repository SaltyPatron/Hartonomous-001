@echo off
setlocal

rem Source oneAPI env manually — setvars.bat has issues invoking component vars.bat scripts
set "ONEAPI_ROOT=C:\Program Files (x86)\Intel\oneAPI"
set "MKLROOT=%ONEAPI_ROOT%\mkl\latest"
set "IPPROOT=%ONEAPI_ROOT%\ipp\latest"
set "DNNLROOT=%ONEAPI_ROOT%\dnnl\latest"
set "CMPLR_ROOT=%ONEAPI_ROOT%\compiler\latest"
set "TBBROOT=%ONEAPI_ROOT%\tbb\latest"
set "PATH=%MKLROOT%\bin;%CMPLR_ROOT%\bin;%TBBROOT%\bin;%PATH%"
set "LIB=%MKLROOT%\lib;%CMPLR_ROOT%\lib;%LIB%"
set "INCLUDE=%MKLROOT%\include;%CMPLR_ROOT%\include;%INCLUDE%"

cd /d "%~dp0"
if exist build rmdir /s /q build
cmake -S . -B build -G "Visual Studio 18 2026" -A x64 -DCMAKE_BUILD_TYPE=Release -DMKL_DIR="%MKLROOT%\lib\cmake\mkl"
endlocal
